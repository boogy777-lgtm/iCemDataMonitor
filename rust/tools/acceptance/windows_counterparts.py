"""Fetch/build test-only pinned counterparts; run the existing external contracts.

No machine installation/service/registry changes. All build tools, venv and logs
live below --work; only our bounded child processes are started by the probes.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tarfile
import time
import urllib.request
import venv

HERE = Path(__file__).resolve().parent


def fetch(url, digest, path):
    with urllib.request.urlopen(url, timeout=60) as source, path.open('wb') as target:
        shutil.copyfileobj(source, target, length=1024 * 1024)
    if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
        raise ValueError(f'SHA256 mismatch: {path.name}')


def run(command, log, timeout=300, env=None):
    with log.open('w', encoding='utf-8') as output:
        subprocess.run([str(x) for x in command], stdout=output,
                       stderr=subprocess.STDOUT, timeout=timeout, check=True, env=env)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    args = parser.parse_args()
    if sys.platform != 'win32' or sys.version_info[:2] != (3, 12) or platform.machine() != 'AMD64':
        raise RuntimeError('Requires CPython 3.12 on Windows x64; no cross-runtime PASS')
    work = args.work.resolve()
    # Fail on existing directory: never overwrite/clean another run's resources.
    work.mkdir(parents=True, exist_ok=False)
    evidence = work / 'evidence'
    evidence.mkdir()
    manifest = json.loads((HERE / 'windows-counterparts.json').read_text())
    result = dict(result='FAIL', source_sha=os.environ.get('GITHUB_SHA', ''),
                  platform=platform.platform(), python=sys.version,
                  counterpart_manifest_sha256=hashlib.sha256((HERE / 'windows-counterparts.json').read_bytes()).hexdigest(),
                  mosquitto=manifest['mosquitto'], wheels=manifest['wheels'],
                  scope='Windows loopback fixture; no live MMF, AddIn or hardware claim')
    started = time.monotonic()
    try:
        archive = work / 'mosquitto.tar.gz'
        mosq = manifest['mosquitto']
        fetch(mosq['url'], mosq['sha256'], archive)
        with tarfile.open(archive) as bundle:
            bundle.extractall(work / 'source', filter='data')
        source = work / 'source' / ('mosquitto-' + mosq['commit'])
        for name in ('LICENSE.txt', 'NOTICE.md', 'edl-v10', 'epl-v20'):
            shutil.copyfile(source / name, evidence / ('mosquitto-' + name))
        build = work / 'build'
        run(['cmake', '-S', source, '-B', build, '-A', 'x64',
             '-DCMAKE_PROJECT_mosquitto_INCLUDE=' + str(HERE / 'mosquitto-no-tls.cmake'),
             *['-D' + option for option in mosq['cmake_options']]], evidence / 'cmake-configure.log')
        run(['cmake', '--build', build, '--config', 'Release', '--target', 'mosquitto', 'mosquitto_sub', '--parallel', '2'],
            evidence / 'cmake-build.log')
        # Executables from the same reviewed build; DLL beside the CLI for Windows loader.
        binaries = work / 'bin'
        binaries.mkdir()
        for name in ('mosquitto.exe', 'mosquitto_sub.exe', 'mosquitto.dll'):
            matches = list(build.rglob(name))
            if len(matches) != 1:
                raise RuntimeError(f'Expected one built {name}, found {matches}')
            shutil.copyfile(matches[0], binaries / name)
        wheels = work / 'wheels'
        wheels.mkdir()
        for wheel in manifest['wheels']:
            fetch(wheel['url'], wheel['sha256'], wheels / wheel['filename'])
        environment = work / 'venv'
        venv.create(environment, with_pip=True)
        python = environment / 'Scripts/python.exe'
        env = dict(os.environ, PYTHONDONTWRITEBYTECODE='1', PYTHONNOUSERSITE='1')
        # No index/no dependencies: closure is fully enumerated and hash-checked.
        run([python, '-m', 'pip', '--isolated', 'install', '--no-index', '--no-deps', '--require-hashes',
             '--only-binary=:all:', '--find-links', wheels, '-r', HERE / 'windows-asyncua.requirements.txt'],
            evidence / 'pip-install.log', env=env)
        run([python, '-m', 'pip', '--isolated', 'check'], evidence / 'pip-check.log', env=env)
        run([python, '-m', 'pip', '--isolated', 'list', '--format=json'], evidence / 'python-versions.json', env=env)
        for script, output, extra in (
            ('nplus1_mqtt_broker.py', 'mqtt.json', ['--mosquitto', binaries / 'mosquitto.exe', '--subscriber', binaries / 'mosquitto_sub.exe']),
            ('nplus1_opcua_client.py', 'opcua.json', [])):
            report = evidence / output
            run([python, HERE / script, '--host', args.host.resolve(), '--fixture', args.fixture.resolve(),
                 '--output', report, *extra], evidence / (script + '.log'), timeout=100, env=env)
            data = json.loads(report.read_text())
            if data['result'] != 'PASS' or data['host_exit'] != 0:
                raise RuntimeError(f'Failed counterpart: {output}')
            result[output.removesuffix('.json')] = data
        result['result'] = 'PASS'
    except Exception as error:
        result['error'] = str(error)
        raise
    finally:
        result['elapsed_seconds'] = round(time.monotonic() - started, 3)
        (evidence / 'summary.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        # Bounded text survives even if artifact download is unavailable to operators.
        print(json.dumps(result), flush=True)
        if result['result'] != 'PASS':
            for path in sorted(evidence.glob('*.log')):
                print(f'--- {path.name} (last 12000 characters) ---\n' + path.read_text(errors='replace')[-12000:])


if __name__ == '__main__':
    main()
