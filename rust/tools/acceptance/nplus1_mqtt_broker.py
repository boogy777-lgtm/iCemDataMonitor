"""Independent real Mosquitto interoperability gate; no Python MQTT dependency.

Use external mosquitto and mosquitto_sub executables. Isolated temporary config,
loopback ephemeral port, nonpersistent broker; never uses a user's broker.
"""
import argparse
import json
import os
import platform
import shutil
from pathlib import Path
import socket
import struct
import subprocess
import tempfile
import time


def stop(process):
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=3)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--mosquitto", type=Path, required=True)
    parser.add_argument("--subscriber", type=Path, required=True)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    for name in ["host", "mosquitto", "subscriber", "fixture"]:
        setattr(args, name, getattr(args, name).resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    startup_seconds, test_seconds = 15, 15
    host_duration_ms = (2 * startup_seconds + test_seconds + 5) * 1000
    fixture = args.fixture.read_bytes()
    count = struct.unpack_from('<H', fixture, 8)[0]

    def verify_source(document):
        assert document['schema'] == 'icem.mqtt.state.v1'
        assert document['com_source_time']['unix_seconds'] == 1700000000
        addresses = [*range(4), *range(4, 600, 2), *range(600, count)]
        variables = document['variables']
        assert [value['id'] for value in variables] == [f'var{address}' for address in addresses]
        for address, value in zip(addresses, variables):
            word = struct.unpack_from('>H', fixture, 64 + address * 2)[0]
            bits = word
            if 4 <= address < 600:
                bits |= struct.unpack_from('>H', fixture, 66 + address * 2)[0] << 16
            assert value['register_address'] == address
            assert value['raw_bits'] == bits
            assert value['type'] == ('i32_com_unix_seconds' if address == 4 else 'ieee754_binary32' if 4 < address < 600 else 'u16')
        for zone, offset in zip(document['zones'], [10, 19, 28]):
            assert zone['last_write_utc_ticks'] == struct.unpack_from('<q', fixture, offset)[0]
            assert zone['upstream_measurement_time'] is None

    broker = host = None
    with tempfile.TemporaryDirectory(prefix="icem-mqtt-") as folder:
        folder = Path(folder)
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        broker_config = folder / "mosquitto.conf"
        broker_config.write_text(f"listener {port} 127.0.0.1\nallow_anonymous true\npersistence false\n" + ("user root\n" if hasattr(os, "geteuid") and os.geteuid() == 0 else ""))
        config = folder / "mqtt.json"
        config.write_text(json.dumps(dict(broker=f"127.0.0.1:{port}", client_id="icem-p6-interop", topic="icem/acceptance/state", qos=2, retain=True, publish_interval_ms=50, reconnect_delay_ms=50)))
        broker_log = open(folder / "broker.log", "wb")
        host_log = open(folder / "host.log", "wb")

        def boot():
            process = subprocess.Popen([str(args.mosquitto), "-c", str(broker_config)], stdout=broker_log, stderr=subprocess.STDOUT)
            deadline = time.monotonic() + startup_seconds
            while True:
                try:
                    with socket.create_connection(("127.0.0.1", port), timeout=.2):
                        return process
                except OSError:
                    if process.poll() is not None or time.monotonic() >= deadline:
                        if process.poll() is None:
                            stop(process)
                        raise RuntimeError("Mosquitto failed to listen")
                    time.sleep(.02)

        def receive():
            result = subprocess.run([str(args.subscriber), "-h", "127.0.0.1", "-p", str(port), "-t", "icem/acceptance/state", "-q", "2", "-C", "1", "-W", str(test_seconds), "-F", "%r|%p"], capture_output=True, text=True, timeout=test_seconds + 2, check=True)
            retain, payload = result.stdout.strip().split("|", 1)
            return int(retain), json.loads(payload)

        try:
            broker = boot()
            host = subprocess.Popen([str(args.host), "--modbus-fixture", str(args.fixture), "--bind", "127.0.0.1:0", "--duration-ms", str(host_duration_ms), "--mqtt-config", str(config)], stdout=host_log, stderr=subprocess.STDOUT)
            _, first = receive()
            verify_source(first)
            broker.terminate(); broker.wait(timeout=3); broker = None
            time.sleep(.45)
            broker = boot()
            _, reconnected = receive()
            verify_source(reconnected)
            assert reconnected["capture"]["generation"] > first["capture"]["generation"]
            assert reconnected["com_source_time"] == first["com_source_time"]
            assert reconnected["zones"] == first["zones"]
            assert reconnected["variables"] == first["variables"]
            assert host.wait(timeout=host_duration_ms / 1000 + 5) == 0
            retained_flag, retained = receive()
            verify_source(retained)
            assert retained_flag == 1
            assert retained["capture"]["generation"] >= reconnected["capture"]["generation"]
            assert retained["com_source_time"] == first["com_source_time"]
            result = dict(result="PASS", broker_version=subprocess.run([str(args.mosquitto), "-h"], capture_output=True, text=True).stdout.splitlines()[0], qos=2, retain_flag_after_host_exit=retained_flag, first_generation=first["capture"]["generation"], reconnect_generation=reconnected["capture"]["generation"], retained_generation=retained["capture"]["generation"], source_clocks_and_bits_preserved=True, host_exit=0, platform=platform.platform(), python_version=platform.python_version(), scope=f"{platform.system()} loopback real Mosquitto; fixture source, no live MMF/hardware claim")
            args.output.write_text(json.dumps(result, indent=2) + "\n")
            print(json.dumps(result))
        except Exception as error:
            args.output.write_text(json.dumps(dict(result="FAIL", platform=platform.platform(), error=str(error), host_exit=host.poll() if host else None), indent=2) + "\n")
            host_log.flush(); broker_log.flush()
            print((folder / "host.log").read_text(errors="replace"))
            print((folder / "broker.log").read_text(errors="replace"))
            raise
        finally:
            for process in [host, broker]:
                stop(process)
            host_log.close(); broker_log.close()
            shutil.copyfile(folder / "host.log", args.output.with_suffix(".host.log"))
            shutil.copyfile(folder / "broker.log", args.output.with_suffix(".broker.log"))


if __name__ == "__main__":
    main()
