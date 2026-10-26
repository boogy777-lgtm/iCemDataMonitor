"""Real --bus-live Host with an independent safe writer; no production instrumentation.
Raw samples are retained. Separate probe counts its own live Arc payloads, not Host Arcs.
"""
import argparse
import concurrent.futures
import csv
import http.client
import json
from pathlib import Path
import platform
import socket
import shutil
import struct
import subprocess
import tempfile
import time
import uuid

from nplus1_network import exact, freeport, stats


def varint(data, offset):
    value = 0
    for shift in range(0, 70, 7):
        if offset >= len(data):
            raise ValueError('truncated protobuf varint')
        byte = data[offset]
        offset += 1
        value |= (byte & 127) << shift
        if byte < 128:
            return value, offset
    raise ValueError('oversized protobuf varint')


def fields(data):
    offset = 0
    while offset < len(data):
        tag, offset = varint(data, offset)
        number, wire = tag >> 3, tag & 7
        if number == 0:
            raise ValueError('zero protobuf field')
        if wire == 0:
            value, offset = varint(data, offset)
        elif wire in (1, 2, 5):
            if wire == 2:
                length, offset = varint(data, offset)
            else:
                length = 8 if wire == 1 else 4
            value = data[offset:offset + length]
            if len(value) != length:
                raise ValueError('truncated protobuf field')
            offset += length
        else:
            raise ValueError('unsupported protobuf wire type')
        yield number, wire, value


def web_values(body):
    values = {}
    offset = 0
    trailers = b''
    messages = 0
    while offset < len(body):
        if offset + 5 > len(body):
            raise ValueError('short grpc-web frame')
        flag, size = body[offset], int.from_bytes(body[offset + 1:offset + 5], 'big')
        offset += 5
        message = body[offset:offset + size]
        offset += size
        if len(message) != size:
            raise ValueError('short grpc-web payload')
        if flag == 128:
            trailers += message
        elif flag == 0:
            messages += 1
            for number, wire, value in fields(message):
                if number == 3 and wire == 2:
                    item = {n: (w, v) for n, w, v in fields(value)}
                    address = item.get(1, (0, 0))[1]
                    if 2 in item:
                        values[address] = item[2][1] & 0xffffffff
                    elif 3 in item:
                        values[address] = int.from_bytes(item[3][1], 'little')
        else:
            raise ValueError('unsupported grpc-web compression/flags')
    if messages != 1 or not any(line.strip().lower().replace(b' ', b'') == b'grpc-status:0'
                                 for line in trailers.splitlines()):
        raise ValueError('missing successful unary grpc-web result')
    return values


def check_values(values, com_bits):
    if values.get(4) != com_bits:
        raise ValueError(f'COM source time changed: actual={values.get(4)!r}, expected={com_bits}')
    lan, raw = values.get(200), values.get(400)
    if lan != raw or lan is None or not 0x3f000001 <= lan < 0x3f001000:
        raise ValueError(f'incoherent/missing LAN RAW: {lan}, {raw}')
    if values.get(402) != 0x80000000 or values.get(404) != 1:
        raise ValueError('RAW IEEE bits changed')
    return lan


def wait_for_initial_com(read_com, expected, deadline, clock=time.monotonic, pause=time.sleep):
    """Initial zero mirror is pending only before measurements, under startup budget."""
    if expected == 0:
        raise ValueError('readiness fixture must distinguish COM source time from initial zero mirror')
    started = clock()
    attempts = zeros = 0
    while clock() < deadline:
        actual = read_com()
        attempts += 1
        if clock() >= deadline:
            break
        if actual == expected:
            return {'attempts': attempts, 'initial_zero_reads': zeros,
                    'com_bits': actual, 'elapsed_s': clock() - started}
        if actual != 0:
            raise ValueError(f'initial COM mismatch: actual={actual!r}, expected={expected}')
        zeros += 1
        pause(min(.05, max(0, deadline - clock())))
    raise RuntimeError('initial Modbus COM mirror did not become ready within Host startup deadline')


def read_initial_com(connection, deadline, clock=time.monotonic):
    """Read FC03 COM bits, bounding every fragmented I/O by the startup deadline."""
    def remaining():
        budget = deadline - clock()
        if budget <= 0:
            raise RuntimeError('initial Modbus COM read exceeded Host startup deadline')
        return min(3, budget)

    def receive(count):
        result = bytearray()
        while len(result) < count:
            connection.settimeout(remaining())
            part = connection.recv(count - len(result))
            if not part:
                raise ValueError('incomplete initial FC03 response')
            result.extend(part)
        return bytes(result)

    request = struct.pack('>HHHBBHH', 0, 0, 6, 1, 3, 4, 2)
    connection.settimeout(remaining())
    connection.sendall(request)
    header = receive(6)
    body = receive(int.from_bytes(header[4:6], 'big'))
    if header[:4] != request[:4] or body[:3] != bytes([1, 3, 4]) or len(body) != 7:
        raise ValueError('invalid initial FC03 response')
    low, high = struct.unpack('>HH', body[3:])
    return low | high << 16


def consumer(kind, port, hz, duration, com_bits):
    samples = []
    start = time.perf_counter()
    due = start
    connection = None
    try:
        if kind == 'modbus':
            connection = socket.create_connection(('127.0.0.1', port), timeout=3)
            connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        else:
            connection = http.client.HTTPConnection('127.0.0.1', port, timeout=3)
        while time.perf_counter() - start < duration:
            stamp = time.perf_counter_ns()
            if kind == 'modbus':
                # Three FC03 ranges, including fixed COM; each response is coherent.
                values = {}
                for address, count in [(4, 2), (200, 2), (400, 6)]:
                    request = struct.pack('>HHHBBHH', len(samples) % 65536, 0, 6, 1, 3, address, count)
                    connection.sendall(request)
                    header = exact(connection, 6)
                    body = exact(connection, int.from_bytes(header[4:6], 'big'))
                    if header[:4] != request[:4] or body[:3] != bytes([1, 3, count * 2]):
                        raise ValueError('invalid FC03 response')
                    words = struct.unpack('>' + 'H' * count, body[3:])
                    for i in range(0, count, 2):
                        values[address + i] = words[i] | words[i + 1] << 16
                # Separate requests can straddle a publication; require each raw marker valid,
                # and fixed COM/IEEE probes. Web single-reply check proves whole-frame coherence.
                lan = values[200]
                values[200] = values[400]
                try:
                    check_values(values, com_bits)
                except ValueError as error:
                    raise ValueError(f'{kind} {hz}Hz sample={len(samples)}: {error}') from error
                if not 0x3f000001 <= lan < 0x3f001000:
                    raise ValueError('invalid LAN raw marker')
            else:
                connection.request('POST', '/icemdatamonitor.v1.TelemetryService/GetRegisters',
                                   b'\0' * 5, {'Content-Type': 'application/grpc-web+proto', 'X-Grpc-Web': '1'})
                response = connection.getresponse()
                body = response.read()
                if response.status != 200:
                    raise ValueError(f'web status {response.status}')
                values = web_values(body)
                lan = check_values(values, com_bits)
            elapsed = (time.perf_counter_ns() - stamp) / 1000
            samples.append({'elapsed_s': time.perf_counter() - start, 'latency_us': elapsed,
                            'lan_bits': lan, 'raw_bits': values[400]})
            due += 1 / hz
            time.sleep(max(0, due - time.perf_counter()))
    finally:
        if connection is not None:
            connection.close()
    if len(set(row['lan_bits'] for row in samples)) < 3 or len(set(row['raw_bits'] for row in samples)) < 3:
        raise ValueError('consumer did not observe independent updates with fixed COM')
    return {'kind': kind, 'rate_hz': hz, 'roundtrip_scope': 'three FC03 requests' if kind == 'modbus' else 'one GetRegisters all',
            'latency': stats([row['latency_us'] for row in samples]), 'samples': samples}


def slow_disconnect(modbus_port, web_port, duration):
    count = 0
    start = time.monotonic()
    while time.monotonic() - start < duration:
        # Slow partial requests keep a socket occupied for 700ms, then disconnect.
        with socket.create_connection(('127.0.0.1', modbus_port), timeout=3) as modbus:
            with socket.create_connection(('127.0.0.1', web_port), timeout=3) as web:
                modbus.sendall(b'\0\1\0')
                web.sendall(b'POST /icemdatamonitor.v1.TelemetryService/GetRegisters HTTP/1.1\r\nHost: localhost\r\n')
                time.sleep(.7)
        count += 1
    return {'partial_request_disconnect_cycles': count, 'hold_s': .7}


def stop(process):
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def run(args):
    if platform.system() != 'Windows':
        raise RuntimeError('Windows required')
    args.output.mkdir(parents=True, exist_ok=True)
    report = {'status': 'OPEN', 'acquisition_period_ms': 200, 'writer_period_ms': 50,
              'limitations': ['software file-backed MMF; no AddIn/hardware acceptance',
                             'Host acquisition count is not inferred from request/update count',
                             'retention probe is a separate sequential phase/process; its Arc count is not Host-wide',
                             'CPU samples are cumulative process user+kernel time; peak working set is process lifetime, not heap live bytes',
                             'slow clients hold incomplete requests, not an unbounded subscription backlog']}
    children = []
    logs = []
    metric_log = None
    def launch(command, label):
        stream = (args.output / f'{label}.log').open('w')
        logs.append(stream)
        process = subprocess.Popen([str(part) for part in command], stdout=stream, stderr=subprocess.STDOUT)
        children.append(process)
        return process
    temporary = tempfile.mkdtemp(prefix='icem-live-')
    try:
        root = Path(temporary)
        fixture = root / 'bus.bin'
        token = uuid.uuid4().hex
        mutex = 'Local\\icem-live-' + token
        missing_map = 'Local\\icem-live-missing-' + token
        expected = Path('tests/fixtures/bus-v5-minimal.bin').read_bytes()
        writer = launch([args.harness, '--writer', fixture, mutex, 60000,
                         args.output / 'writer.csv', missing_map], 'writer')
        deadline = time.monotonic() + 8
        while not fixture.exists() or fixture.stat().st_size != len(expected):
            if writer.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError('writer did not initialize fixture')
            time.sleep(.05)
        # A completed first write is guaranteed before launching readers.
        time.sleep(.15)
        # Retention is a separate phase: never add a second acquisition owner to Host load.
        probe = launch([args.harness, '--probe', fixture, mutex, 12000,
                        args.output / 'retention.csv', missing_map], 'retention')
        if probe.wait(timeout=18):
            raise RuntimeError('retention probe failed; see retention.log')
        com_words = struct.unpack('>HH', expected[72:76])
        com_bits = com_words[0] | com_words[1] << 16
        p1, p2 = freeport(), freeport()
        while p1 == p2:
            p2 = freeport()
        host = launch([args.binary, '--bus-live', fixture, '--bus-map', missing_map,
                       '--bus-mutex', mutex, '--bind', f'127.0.0.1:{p1}', '--web-bind',
                       f'127.0.0.1:{p2}', '--duration-ms', 30000, '--theme-file', root / 'theme'], 'host')
        deadline = time.monotonic() + 10
        while True:
            try:
                with socket.create_connection(('127.0.0.1', p2), timeout=.1):
                    break
            except OSError:
                if host.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError('live Host failed startup; see host.log')
                time.sleep(.05)
        # Listener readiness does not imply the first acquisition/mirror tick ran.
        # Warm up only initial zero COM under the SAME startup deadline, before
        # the measured 12s workload. Every measured response still checks all bits.
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise RuntimeError('live Host exceeded startup deadline')
        with socket.create_connection(('127.0.0.1', p1), timeout=min(3, remaining)) as initial:
            report['initial_mirror'] = wait_for_initial_com(
                lambda: read_initial_com(initial, deadline), com_bits, deadline)
        # One external sampler, independent from request timings. CPU is cumulative user+kernel.
        metric_log = (args.output / 'process-metrics.jsonl').open('w')
        script = ('$ErrorActionPreference="Stop"; 1..32 | ForEach-Object { '
                  f'$p=Get-Process -Id {host.pid}; '
                  '[pscustomobject]@{utc=[DateTime]::UtcNow.ToString("o");cpu_s=$p.CPU;'
                  'working_set_bytes=$p.WorkingSet64;peak_working_set_bytes=$p.PeakWorkingSet64;'
                  'private_bytes=$p.PrivateMemorySize64;handles=$p.HandleCount;threads=$p.Threads.Count}'
                  ' | ConvertTo-Json -Compress; Start-Sleep -Milliseconds 500 }')
        sampler = subprocess.Popen(['powershell', '-NoProfile', '-Command', script],
                                   stdout=metric_log, stderr=subprocess.STDOUT)
        children.append(sampler)
        workloads = [('modbus', p1, 2), ('modbus', p1, 50), ('web', p2, 1), ('web', p2, 20)]
        started = time.perf_counter()
        with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
            futures = [pool.submit(consumer, kind, port, hz, 12, com_bits) for kind, port, hz in workloads]
            slow = pool.submit(slow_disconnect, p1, p2, 12)
            report['consumers'] = [future.result() for future in futures]
            report['slow_client'] = slow.result()
        report['workload_wall_s'] = time.perf_counter() - started
        if sampler.wait(timeout=10):
            raise RuntimeError('process metrics failed')
        metric_log.close()
        metric_log = None
        metrics = [json.loads(line) for line in (args.output / 'process-metrics.jsonl').read_text().splitlines()]
        if len(metrics) != 32 or any(row['cpu_s'] is None for row in metrics):
            raise RuntimeError('incomplete process metrics')
        report['process'] = {'pid': host.pid, 'cpu_s_first': metrics[0]['cpu_s'], 'cpu_s_last': metrics[-1]['cpu_s'],
                             'cpu_s_delta_during_sampling': metrics[-1]['cpu_s'] - metrics[0]['cpu_s'],
                             'peak_working_set_bytes': max(row['peak_working_set_bytes'] for row in metrics),
                             'max_sampled_private_bytes': max(row['private_bytes'] for row in metrics),
                             'samples': len(metrics), 'sample_period_ms': 500}
        with (args.output / 'retention.csv').open(newline='') as stream:
            retention = list(csv.DictReader(stream))
        if len(retention) < 45:
            raise RuntimeError('too few 5Hz retention samples')
        report['retention_probe'] = {'captures': len(retention), 'peak_live_snapshot_payloads':
                                     max(int(row['live_snapshot_peak']) for row in retention),
                                     'capture_publication': stats([int(row['capture_publication_ns']) / 1000 for row in retention]),
                                     'consumer_rates_hz': [100, 20, 5, 1], 'pinned_old_snapshot': True}
        if host.poll() is not None or writer.poll() is not None:
            raise RuntimeError('Host/writer ended before workload completion')
        report['status'] = 'PASS_LIVE_SOFTWARE_LOAD'
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        for process in reversed(children):
            stop(process)
        if metric_log is not None:
            metric_log.close()
        for stream in logs:
            stream.close()
        try:
            shutil.rmtree(temporary)
        except OSError as error:
            report['cleanup_error'] = str(error)
            report['status'] = 'OPEN'
        (args.output / 'live-summary.json').write_text(json.dumps(report, indent=2) + '\n')
    if report['status'] != 'PASS_LIVE_SOFTWARE_LOAD':
        raise RuntimeError('live load cleanup failed; see summary')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--binary', type=Path, default=Path('target/release/icem-host-dev.exe'))
    parser.add_argument('--harness', type=Path, default=Path('target/release/examples/windows_live_load.exe'))
    parser.add_argument('--output', type=Path, required=True)
    arguments = parser.parse_args()
    arguments.output = arguments.output.resolve()
    run(arguments)
