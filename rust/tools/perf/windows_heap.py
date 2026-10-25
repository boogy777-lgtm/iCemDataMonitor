"""Separate external Windows heap profile; never use profiled timings as latency.
Raw xperf totals are the authority for counts and sizes (reported by xperf in KB).
Unsupported tools, active recordings, trace loss or unknown output leave status OPEN.
"""
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import uuid


def loss_counters(text):
    """Fail closed: require actual event and buffer loss fields from trace stats."""
    counters = {}
    for label, value in re.findall(
            r'(?im)^\s*((?:(?:real time|log)\s+)?(?:events?|buffers?)\s+lost)\s*[:=,]\s*(\d+)\s*$', text):
        counters.setdefault(label.lower(), []).append(int(value))
    if not any('event' in key for key in counters) or not any('buffer' in key for key in counters):
        raise RuntimeError('trace statistics lack recognized event/buffer loss counters; inspect raw stats')
    if any(value for values in counters.values() for value in values):
        raise RuntimeError(f'ETW lost events/buffers: {counters}')
    return counters


def heap_totals(text, require_positive=False):
    """Recognize explicit native count/KB columns; unknown layouts remain OPEN.

    Never turn a PID/header digit or a missing event table into an allocation count.
    Keep each native row rather than summing potentially overlapping heap totals.
    """
    lines = text.splitlines()
    for index, line in enumerate(lines):
        split = (lambda value: next(csv.reader([value]))) if ',' in line else (
            lambda value: re.split(r'\t+|\s{2,}', value.strip()))
        header = [cell.strip().lower() for cell in split(line)]
        counts = [i for i, cell in enumerate(header)
                  if re.fullmatch(r'alloc(?:ation)?[ _]*(?:count|number|num|#)', cell)]
        sizes = [i for i, cell in enumerate(header)
                 if re.fullmatch(r'alloc(?:ation)?[ _]*size\s*\(?kb\)?', cell)]
        if len(counts) != 1 or len(sizes) != 1:
            continue
        rows = []
        for raw in lines[index + 1:]:
            cells = [cell.strip() for cell in split(raw)]
            if len(cells) != len(header):
                continue
            count, size = cells[counts[0]], cells[sizes[0]]
            if not re.fullmatch(r'\d+', count) or not re.fullmatch(r'\d+(?:\.\d+)?', size):
                continue
            rows.append({'allocation_calls': int(count), 'allocated_size_KB_native': size,
                         'native_row': cells})
        if rows and (not require_positive or any(row['allocation_calls'] > 0 for row in rows)):
            return rows
    raise RuntimeError('missing/unknown heap allocation count + KB table, or zero capture allocations; inspect raw totals')


def remove_owned_executable(exe, cleanup_errors):
    """Keep a cleanup failure from suppressing the evidence summary."""
    if exe is not None:
        try:
            exe.unlink(missing_ok=True)
        except OSError as error:
            cleanup_errors.append(f'remove owned executable {exe}: {error}')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    report = {'status': 'OPEN', 'runs': [], 'commands': [],
              'scope': 'PID-filtered process-lifetime heap allocation events; startup, harness, '
                       'fixture setup/checks/teardown included. Separate zero-capture control; '
                       'no exact per-capture attribution or network request heap claim.',
              'latency': 'separate unprofiled run; no timings from this profile are accepted'}
    active = configured = owned_key = False
    exe = None
    wpr = None
    registry = None
    key_path = None

    def run(*command):
        result = subprocess.run([str(c) for c in command], text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        report['commands'].append({'argv': [str(c) for c in command],
                                   'exit': result.returncode, 'output': result.stdout})
        if result.returncode:
            raise RuntimeError(f'{command[0]} exited {result.returncode}: {result.stdout}')
        return result.stdout

    try:
        if platform.system() != 'Windows':
            raise RuntimeError('Windows required')
        import winreg as registry
        toolkit = Path(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)')) / 'Windows Kits/10/Windows Performance Toolkit'
        xperf = shutil.which('xperf.exe') or str(toolkit / 'xperf.exe')
        wpr = shutil.which('wpr.exe') or str(toolkit / 'wpr.exe')
        if not Path(xperf).is_file() or not Path(wpr).is_file():
            raise RuntimeError('installed Windows Performance Toolkit wpr/xperf unavailable; no downloads attempted')
        report['source'] = run('git', 'rev-parse', 'HEAD').strip()
        report['tree'] = run('git', 'rev-parse', 'HEAD^{tree}').strip()
        report['compiler'] = run('rustc', '--version', '--verbose')
        report['platform'] = platform.platform()
        report['toolkit'] = {'wpr': wpr, 'xperf': xperf}
        # Start itself also rejects an existing WPR recording. Never cancel one.
        status = run(wpr, '-status')
        if not re.search(r'(not recording|no trace profiles.*running)', status, re.I):
            raise RuntimeError('cannot establish idle WPR; refusing existing/unknown recording')
        loggers = run(xperf, '-loggers')
        (output / 'loggers-before.txt').write_text(loggers)
        if re.search(r'Logger Name\s*:\s*[^\r\n]*(WPR|HeapSession)', loggers, re.I):
            raise RuntimeError('existing WPR/heap ETW session; refusing to modify it')
        exe = output / f'icem_heap_{uuid.uuid4().hex}.exe'
        shutil.copy2('target/release/examples/windows_release_measure.exe', exe)
        report['binary_sha256'] = hashlib.sha256(exe.read_bytes()).hexdigest()
        key_path = 'SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\' + exe.name
        access = registry.KEY_READ | registry.KEY_WRITE | registry.KEY_WOW64_64KEY
        try:
            with registry.OpenKey(registry.HKEY_LOCAL_MACHINE, key_path, 0, access):
                pass
        except FileNotFoundError:
            pass
        else:
            raise RuntimeError('IFEO key already exists; refusing to touch it')
        with registry.CreateKeyEx(registry.HKEY_LOCAL_MACHINE, key_path, 0, access):
            owned_key = True
        # Mark before invocation: partial enable still needs our own key cleaned.
        configured = True
        run(wpr, '-HeapTracingConfig', exe.name, 'enable')
        for label, iterations in [('startup-control', 0), ('capture-publication', 10000)]:
            item = {'label': label, 'iterations': iterations}
            report['runs'].append(item)
            etl = output / f'{label}.etl'
            status = run(wpr, '-status')
            if not re.search(r'(not recording|no trace profiles.*running)', status, re.I):
                raise RuntimeError('WPR became active; refusing to add to an existing recording')
            run(wpr, '-start', 'Heap', '-filemode')
            active = True
            with (output / f'{label}-workload.txt').open('w') as stream:
                process = subprocess.Popen([str(exe), '--heap', str(iterations)], stdout=stream,
                                           stderr=subprocess.STDOUT)
                item['pid'] = process.pid
                try:
                    item['exit'] = process.wait(timeout=120)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                    raise
            run(wpr, '-stop', etl)
            active = False
            if item['exit']:
                raise RuntimeError(f'heap workload failed: {item}')
            totals = output / f'{label}-totals.txt'
            stats = output / f'{label}-tracestats.txt'
            run(xperf, '-i', etl, '-o', totals, '-a', 'heap', '-pid', str(item['pid']), '-totals')
            run(xperf, '-i', etl, '-o', stats, '-a', 'tracestats', '-detail')
            item['loss_counters'] = loss_counters(stats.read_text(errors='replace'))
            raw_totals = totals.read_text(errors='replace')
            item['native_allocation_rows'] = heap_totals(raw_totals, require_positive=iterations > 0)
            item['totals_file'] = totals.name
            item['raw_totals'] = raw_totals
            item['size_unit'] = 'KB as reported by xperf; raw report precision, not exact byte attribution'
            with etl.open('rb') as trace:
                item['trace_sha256'] = hashlib.file_digest(trace, 'sha256').hexdigest()
        # Counts stay in their native raw table: no guessed locale/header conversion.
        report['status'] = 'RECORDED_RAW_HEAP_TOTALS'
    except Exception as error:
        report['error'] = str(error)
    finally:
        cleanup_errors = []
        if active:
            try:
                run(wpr, '-stop', output / 'interrupted.etl')
            except Exception as error:
                cleanup_errors.append(str(error))
        if configured:
            try:
                run(wpr, '-HeapTracingConfig', exe.name, 'disable')
            except Exception as error:
                cleanup_errors.append(str(error))
        if owned_key:
            try:
                # DeleteKey only removes an empty key; never recursively remove foreign state.
                registry.DeleteKeyEx(registry.HKEY_LOCAL_MACHINE, key_path, registry.KEY_WOW64_64KEY)
            except FileNotFoundError:
                pass
            except Exception as error:
                cleanup_errors.append(str(error))
        remove_owned_executable(exe, cleanup_errors)
        if cleanup_errors:
            report['cleanup_errors'] = cleanup_errors
            report['status'] = 'OPEN'
        (output / 'heap-summary.json').write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps(report, indent=2))
    return 0 if report['status'] == 'RECORDED_RAW_HEAP_TOTALS' else 1


if __name__ == '__main__':
    raise SystemExit(main())
