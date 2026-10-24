"""Optional real Mosquitto interoperability gate; no Python MQTT dependency.

Use external mosquitto and mosquitto_sub executables. Isolated temporary config,
loopback ephemeral port, nonpersistent broker; never uses a user's broker.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time


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
            deadline = time.monotonic() + 3
            while True:
                try:
                    with socket.create_connection(("127.0.0.1", port), timeout=.2):
                        return process
                except OSError:
                    if process.poll() is not None or time.monotonic() >= deadline:
                        if process.poll() is None:
                            process.terminate(); process.wait(timeout=3)
                        raise RuntimeError("Mosquitto failed to listen")
                    time.sleep(.02)

        def receive():
            result = subprocess.run([str(args.subscriber), "-h", "127.0.0.1", "-p", str(port), "-t", "icem/acceptance/state", "-q", "2", "-C", "1", "-W", "4", "-F", "%r|%p"], capture_output=True, text=True, timeout=6, check=True)
            retain, payload = result.stdout.strip().split("|", 1)
            return int(retain), json.loads(payload)

        try:
            broker = boot()
            host = subprocess.Popen([str(args.host), "--modbus-fixture", str(args.fixture), "--bind", "127.0.0.1:0", "--duration-ms", "4000", "--mqtt-config", str(config)], stdout=host_log, stderr=subprocess.STDOUT)
            _, first = receive()
            broker.terminate(); broker.wait(timeout=3); broker = None
            time.sleep(.45)
            broker = boot()
            _, reconnected = receive()
            assert reconnected["capture"]["generation"] > first["capture"]["generation"]
            assert reconnected["com_source_time"] == first["com_source_time"]
            assert reconnected["zones"] == first["zones"]
            assert reconnected["variables"] == first["variables"]
            assert host.wait(timeout=6) == 0
            retained_flag, retained = receive()
            assert retained_flag == 1
            assert retained["capture"]["generation"] >= reconnected["capture"]["generation"]
            assert retained["com_source_time"] == first["com_source_time"]
            result = dict(result="PASS", broker_version=subprocess.run([str(args.mosquitto), "-h"], capture_output=True, text=True).stdout.splitlines()[0], qos=2, retain_flag_after_host_exit=retained_flag, first_generation=first["capture"]["generation"], reconnect_generation=reconnected["capture"]["generation"], retained_generation=retained["capture"]["generation"], source_clocks_and_bits_preserved=True, host_exit=0, scope="Linux loopback real Mosquitto; fixture source, not Windows MMF/hardware")
            args.output.write_text(json.dumps(result, indent=2) + "\n")
            print(json.dumps(result))
        except Exception:
            host_log.flush(); broker_log.flush()
            print((folder / "host.log").read_text(errors="replace"))
            print((folder / "broker.log").read_text(errors="replace"))
            raise
        finally:
            for process in [host, broker]:
                if process is not None and process.poll() is None:
                    process.terminate(); process.wait(timeout=3)
            host_log.close(); broker_log.close()


if __name__ == "__main__":
    main()
