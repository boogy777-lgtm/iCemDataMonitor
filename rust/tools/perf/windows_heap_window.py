"""Attach a uniquely owned ETW heap session after warmup, stop before teardown.
Raw dumper events carry exact Size/NewSize bytes; unfamiliar schema fails closed.
No subtraction of startup/control totals and no Web/network allocation claim.
"""
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import platform
import queue
import re
import shutil
import subprocess
import threading
import uuid

from windows_heap import existing_profile_loggers, loss_counters


def event_totals(text, pid, require_positive=True):
    """Decode recognized per-event xperf dumper headers, including PID attribution.

    Heap realloc contributes one realloc call and its NewSize (not net live bytes).
    Accept only explicit numeric byte fields; never reinterpret KB/unknown columns.
    """
    headers = {}
    counts = {'allocation_calls': 0, 'allocated_bytes': 0,
              'reallocation_calls': 0, 'reallocated_new_bytes': 0}
    names = {'HeapAlloc': ('allocation_calls', 'allocated_bytes', 'Size'),
             'HeapRealloc': ('reallocation_calls', 'reallocated_new_bytes', 'NewSize')}
    def integer(value):
        value = value.strip()
        if not re.fullmatch(r'(?:0[xX][0-9a-fA-F]+|[0-9]+)', value):
            raise ValueError(f'unknown integer field {value!r}')
        return int(value, 16 if value.lower().startswith('0x') else 10)
    for row in csv.reader(text.splitlines()):
        row = [cell.strip() for cell in row]
        if not row or row[0] not in names:
            continue
        event = row[0]
        count_key, bytes_key, size_label = names[event]
        if size_label in row:
            pid_columns = [i for i, column in enumerate(row)
                           if column in ('ProcessID', 'Process ID', 'PID', 'Process Name ( PID)')]
            if len(pid_columns) != 1 or row.count(size_label) != 1:
                raise ValueError(f'unknown {event} header/PID schema: {row}')
            headers[event] = (len(row), pid_columns[0], row.index(size_label), row[pid_columns[0]])
            continue
        if event not in headers:
            raise ValueError(f'{event} data without recognized event header')
        length, pid_column, size_column, pid_label = headers[event]
        if len(row) != length:
            raise ValueError(f'{event} row/header length mismatch')
        if pid_label == 'Process Name ( PID)':
            match = re.search(r'\(\s*(\d+)\s*\)\s*$', row[pid_column])
            if match is None:
                raise ValueError('unknown process name/PID field')
            event_pid = int(match.group(1))
        else:
            event_pid = integer(row[pid_column])
        if event_pid == pid:
            counts[count_key] += 1
            counts[bytes_key] += integer(row[size_column])
    if 'HeapAlloc' not in headers or (require_positive and counts['allocation_calls'] == 0):
        raise ValueError('missing HeapAlloc byte/PID schema or no workload allocation events')
    return counts


def read_protocol(process, expected, timeout=120):
    # A daemon reader avoids an indefinitely blocking readline on failed workloads.
    events = queue.Queue()
    def read():
        try:
            events.put(process.stdout.readline())
        except Exception as error:
            events.put(error)
    threading.Thread(target=read, daemon=True).start()
    value = events.get(timeout=timeout)
    if isinstance(value, Exception) or value.strip() != expected:
        raise RuntimeError(f'heap handshake expected {expected!r}, got {value!r}')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    report = {'status': 'OPEN', 'runs': [], 'commands': [],
              'scope': 'warmed capture+publication+latest OR prebuilt FC03 100-word codec requests; '
                       'one single-thread workload, no Web or network I/O attribution',
              'units': 'ETW HeapAlloc Size bytes and HeapRealloc NewSize bytes; not live heap size',
              'limitations': ['window includes START read and DONE write; zero-operation control separately recorded',
                              'no exact operation attribution unless zero-operation control has zero events',
                              'schema and ETW loss validation mandatory; no rounded-KB conversion']}
    active = None
    kernel_instance = None
    process = None
    xperf = None
    wpr = None
    def run(*command):
        result = subprocess.run([str(c) for c in command], text=True, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, timeout=180)
        report['commands'].append({'argv': [str(c) for c in command], 'exit': result.returncode,
                                   'output': result.stdout})
        if result.returncode:
            raise RuntimeError(f'command failed: {command}: {result.stdout}')
        return result.stdout
    try:
        if platform.system() != 'Windows':
            raise RuntimeError('Windows required')
        toolkit = Path(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)')) / 'Windows Kits/10/Windows Performance Toolkit'
        xperf = shutil.which('xperf.exe') or str(toolkit / 'xperf.exe')
        wpr = shutil.which('wpr.exe') or str(toolkit / 'wpr.exe')
        if not Path(xperf).is_file() or not Path(wpr).is_file():
            raise RuntimeError('installed xperf/wpr unavailable')
        report['source'] = run('git', 'rev-parse', 'HEAD').strip()
        report['tree'] = run('git', 'rev-parse', 'HEAD^{tree}').strip()
        inventory = run(xperf, '-loggers')
        (output / 'loggers-before.txt').write_text(inventory)
        if existing_profile_loggers(inventory):
            raise RuntimeError('existing WPR/heap session; refusing to modify it')
        for kind, iterations in [('capture', 0), ('capture', 10000), ('modbus', 0), ('modbus', 10000)]:
            label = f'{kind}-{iterations}'
            item = {'kind': kind, 'operations': iterations, 'label': label}
            report['runs'].append(item)
            kernel_instance = 'icem_process_' + uuid.uuid4().hex
            # Unique WPR instance owns process/thread metadata from before launch.
            # Never start/stop the global NT Kernel Logger or another WPR instance.
            run(wpr, '-start', 'CPU.Light', '-filemode', '-instancename', kernel_instance)
            item['process_recording_status'] = run(wpr, '-status', '-instancename', kernel_instance)
            kernel = output / f'{label}-process.etl'
            stderr = (output / f'{label}-stderr.txt').open('w')
            try:
                process = subprocess.Popen(['target/release/examples/windows_release_measure.exe',
                                            '--heap-window', kind, str(iterations)], stdin=subprocess.PIPE,
                                           stdout=subprocess.PIPE, stderr=stderr, text=True, bufsize=1)
                item['pid'] = process.pid
                read_protocol(process, 'WARM')
                process.stdin.write('WARMUP\n')
                process.stdin.flush()
                read_protocol(process, f'READY {process.pid}')
                session = 'icem_heap_' + uuid.uuid4().hex
                heap = output / f'{label}-heap.etl'
                # Start only our uniquely named heap logger; never start/stop a global kernel session.
                # Set ownership before call: a partially successful start still needs bounded cleanup.
                active = session
                run(xperf, '-start', session, '-heap', '-pids', process.pid,
                    '-BufferSize', 1024, '-MinBuffers', 32, '-MaxBuffers', 32, '-f', heap)
                process.stdin.write('START\n')
                process.stdin.flush()
                read_protocol(process, f'DONE {iterations}')
                run(xperf, '-stop', session)
                active = None
                process.stdin.write('RELEASE\n')
                process.stdin.flush()
                if process.wait(timeout=10):
                    raise RuntimeError('heap window workload failed')
                process.stdin.close()
                process.stdout.close()
                process = None
                run(wpr, '-stop', kernel, '-instancename', kernel_instance)
                kernel_instance = None
            finally:
                stderr.close()
            etl = output / f'{label}.etl'
            run(xperf, '-merge', heap, kernel, etl)
            events = output / f'{label}-events.csv'
            trace_stats = output / f'{label}-tracestats.txt'
            run(xperf, '-i', etl, '-o', events, '-a', 'dumper')
            run(xperf, '-i', etl, '-o', trace_stats, '-a', 'tracestats', '-detail')
            item['events_sha256'] = hashlib.sha256(events.read_bytes()).hexdigest()
            item['events_file'] = events.name
            try:
                # Validate both original sessions as well as the merged trace:
                # merging must never hide loss in one source.
                item['loss'] = {}
                for source_name, trace in [('heap', heap), ('process', kernel)]:
                    raw = output / f'{label}-{source_name}-events.csv'
                    run(xperf, '-i', trace, '-o', raw, '-a', 'dumper')
                    item['loss'][source_name] = loss_counters(raw.read_text(errors='replace'))
                item['loss']['merged'] = loss_counters(events.read_text(errors='replace'))
                item['recorded_window'] = event_totals(events.read_text(errors='replace'), item['pid'], iterations > 0)
            except (ValueError, RuntimeError) as error:
                item['validation_error'] = str(error)
        if any('validation_error' in item for item in report['runs']):
            raise RuntimeError('raw windows recorded; event schema/loss validation remains OPEN')
        controls = {item['kind']: item['recorded_window'] for item in report['runs'] if item['operations'] == 0}
        for item in report['runs']:
            if item['operations']:
                item['control_zero_events'] = not any(controls[item['kind']].values())
                item['recorded_window_per_operation_including_overhead'] = {
                    key: value / item['operations'] for key, value in item['recorded_window'].items()}
                if item['control_zero_events']:
                    item['per_operation'] = {key: value / item['operations'] for key, value in item['recorded_window'].items()}
        report['status'] = ('RECORDED_ISOLATED_OPERATION_WINDOWS' if all(
            item.get('control_zero_events', True) for item in report['runs']) else 'RECORDED_WINDOWS_OVERHEAD_OPEN')
    except Exception as error:
        report['error'] = str(error)
    finally:
        errors = []
        if active is not None:
            try:
                run(xperf, '-stop', active)
            except Exception as error:
                errors.append(str(error))
        if process is not None:
            try:
                process.kill()
                process.wait(timeout=10)
            except Exception as error:
                errors.append(f'child stop: {error}')
            for stream in (process.stdin, process.stdout):
                try:
                    stream.close()
                except Exception as error:
                    errors.append(f'child stream close: {error}')
        if kernel_instance is not None:
            try:
                run(wpr, '-stop', output / 'interrupted-process.etl',
                    '-instancename', kernel_instance)
            except Exception as error:
                errors.append(str(error))
        report['cleanup_errors'] = errors
        if errors:
            report['status'] = 'OPEN'
        (output / 'heap-window-summary.json').write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps(report, indent=2))
    return 0 if report['status'].startswith('RECORDED_') else 1


if __name__ == '__main__':
    raise SystemExit(main())
