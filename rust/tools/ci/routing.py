"""Fail-closed component routing and the single required-check candidate (stdlib only)."""
import argparse
import fnmatch
import json
import os
from pathlib import Path
import re
import subprocess

COMPONENTS = ('legacy', 'rust', 'packaging')
RULES_PATH = Path(__file__).with_name('routing-rules.json')


def categories(path):
    rules = json.loads(RULES_PATH.read_text(encoding='utf-8'))
    if not path or path.startswith('/') or any(p in ('', '.', '..') for p in path.split('/')):
        return {'infrastructure'}
    matched = {rule['category'] for rule in rules['rules']
               if any(fnmatch.fnmatchcase(path, pattern) for pattern in rule['paths'])
               and not any(fnmatch.fnmatchcase(path, pattern) for pattern in rule.get('exclude', []))
               and ('extensions' not in rule or Path(path).suffix in rule['extensions'])}
    return matched or {'infrastructure'}


def classify(paths):
    workloads = json.loads(RULES_PATH.read_text(encoding='utf-8'))['workloads']
    selected = {workload for path in paths for group in categories(path) for workload in workloads[group]}
    return {name: str(name in selected).lower() for name in COMPONENTS}


def git(repo, *args):
    return subprocess.run(['git', '-C', str(repo), *args], check=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout


def changed_paths(repo, event_name, event):
    """None means full fallback; [] is permitted only after a successful verified diff."""
    try:
        if event_name == 'workflow_dispatch':
            return None
        if event_name == 'pull_request':
            base = event['pull_request']['base']['sha']
            head = event['pull_request']['head']['sha']
        elif event_name == 'push':
            base, head = event['before'], event['after']
        else:
            return None
        if any(not isinstance(sha, str) or not re.fullmatch('[0-9a-fA-F]{40}', sha)
               or sha == '0' * 40 for sha in (base, head)):
            return None
        for sha in (base, head):
            git(repo, 'cat-file', '-e', sha + '^{commit}')
        if event_name == 'pull_request':
            base = git(repo, 'merge-base', base, head).decode('ascii').strip()
            if not re.fullmatch('[0-9a-fA-F]{40}', base):
                return None
        # Disable rename detection: both the removed old and added new names must route.
        raw = git(repo, 'diff', '--no-ext-diff', '--no-renames', '--name-only', '-z', base, head, '--')
        if raw and not raw.endswith(b'\0'):
            return None
        return [name.decode('utf-8', errors='strict') for name in raw.split(b'\0')[:-1]]
    except (KeyError, TypeError, ValueError, UnicodeError, subprocess.SubprocessError, OSError):
        return None


def validate_gate(needs):
    expected = {'classify', 'baseline', 'routing-tests', *COMPONENTS}
    if not isinstance(needs, dict) or set(needs) != expected:
        raise ValueError('Unexpected/missing dependency jobs')
    for name in expected:
        job = needs[name]
        if not isinstance(job, dict) or set(job) != {'result', 'outputs'} or not isinstance(job['outputs'], dict):
            raise ValueError('Malformed job: ' + name)
    for name in ('classify', 'baseline', 'routing-tests'):
        if needs[name]['result'] != 'success':
            raise ValueError('Mandatory job did not succeed: ' + name)
    outputs = needs['classify']['outputs']
    if set(outputs) != set(COMPONENTS) or any(value not in ('true', 'false') or not isinstance(value, str) for value in outputs.values()):
        raise ValueError('Malformed classification outputs')
    for name in COMPONENTS:
        expected_result = 'success' if outputs[name] == 'true' else 'skipped'
        if needs[name]['result'] != expected_result:
            raise ValueError(f'{name}: expected {expected_result}, got {needs[name]["result"]}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('classify', 'gate'))
    parser.add_argument('--repo', default='.')
    args = parser.parse_args()
    if args.command == 'gate':
        validate_gate(json.loads(os.environ['CI_NEEDS_JSON']))
        print('CI gate: all selected checks succeeded; unselected checks skipped')
        return
    try:
        event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text(encoding='utf-8'))
    except (OSError, ValueError, KeyError):
        event = None
    paths = changed_paths(args.repo, os.environ.get('GITHUB_EVENT_NAME'), event)
    outputs = classify(paths) if paths is not None else dict.fromkeys(COMPONENTS, 'true')
    print(json.dumps({'mode': 'full fallback' if paths is None else 'verified diff', 'paths': paths, 'categories': sorted({group for path in paths for group in categories(path)}) if paths is not None else ['infrastructure'], 'selected': outputs}, ensure_ascii=True))
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        for name, value in outputs.items():
            output.write(f'{name}={value}\n')


if __name__ == '__main__':
    main()
