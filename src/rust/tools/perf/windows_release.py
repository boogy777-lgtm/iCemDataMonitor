"""Windows Release fixture measurements. All raw samples survive in the artifact.
This does not count allocator calls, emulate a live producer, or establish hardware acceptance.
"""
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys


def command(*args):
    return subprocess.check_output(args, text=True, stderr=subprocess.STDOUT).strip()


def summarize(values):
    values = sorted(values)
    return {"samples": len(values), "p50_ns": values[len(values) // 2],
            "p95_ns": values[min(len(values) - 1, int(len(values) * .95))],
            "p99_ns": values[min(len(values) - 1, int(len(values) * .99))]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if platform.system() != 'Windows':
        raise RuntimeError('Windows required')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    evidence = {
        'status': 'RUNNING', 'source': command('git', 'rev-parse', 'HEAD'),
        'tree': command('git', 'rev-parse', 'HEAD^{tree}'),
        'platform': platform.platform(), 'python': sys.version,
        'cpu': command('powershell', '-NoProfile', '-Command',
                       'Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name'),
        'cpu_count': os.cpu_count(), 'compiler': command('rustc', '--version', '--verbose'),
        'profile': 'workspace Release: opt-level=s, thin LTO, codegen-units=1; no target-cpu override',
        'allocation_calls': 'SEPARATE_PROFILE: heap/heap-summary.json and raw PID xperf totals; no allocator hook',
        'limitations': ['CI VM; no CPU isolation or production hardware',
                        'MMF: static 650-register file-backed mapping, uncontended unique named mutex',
                        'Lock hold upper bound includes release and Instant overhead; no contention distribution',
                        'Network: independent synthetic fixture Host, not producer-to-client latency',
                        'Tight-loop microbenchmark is not the production 5Hz acquisition workload'],
    }
    report = output / 'summary.json'
    try:
        csv_path = output / 'mmf.csv'
        subprocess.run(['target/release/examples/windows_release_measure.exe', str(csv_path)], check=True)
        with csv_path.open(newline='') as stream:
            rows = list(csv.DictReader(stream))
        if len(rows) != 1000:
            raise RuntimeError('wrong MMF sample count')
        evidence['mmf'] = {key: summarize([int(row[key]) for row in rows]) for key in rows[0]}
        network = output / 'network.json'
        subprocess.run([sys.executable, 'tools/perf/nplus1_network.py', '--binary',
                        'target/release/icem-host-dev.exe', '--fixture', 'tests/fixtures/bus-v5-minimal.bin',
                        '--output', str(network)], check=True)
        evidence['network'] = json.loads(network.read_text())
        evidence['raw_sha256'] = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                                  for p in (csv_path, network)}
        evidence['status'] = 'PASS_LATENCY_MEASUREMENTS_HEAP_SEPARATE'
    except Exception as error:
        evidence['status'] = 'FAIL'
        evidence['error'] = str(error)
        raise
    finally:
        report.write_text(json.dumps(evidence, indent=2) + '\n')
        print(json.dumps(evidence, indent=2))


if __name__ == '__main__':
    main()
