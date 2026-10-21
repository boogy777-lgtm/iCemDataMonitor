"""Fail-closed component routing and the single required-check candidate (stdlib only)."""
import argparse
from dataclasses import dataclass
import fnmatch
import json
import os
from pathlib import Path
import re
import subprocess

COMPONENTS = ('legacy', 'rust', 'packaging')
RULES_PATH = Path(__file__).with_name('routing-rules.json')
LEDGER_PATH = 'rust/migration/legacy-divergences.json'
MANIFEST_PATH = 'rust/migration/source-manifest.json'
# These small JSON metadata documents are currently below 128 KiB each.
METADATA_LIMIT = 1024 * 1024


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


@dataclass(frozen=True)
class VerifiedDiff:
    base: str
    head: str
    paths: tuple[str, ...]


def verified_diff(repo, event_name, event):
    """Bind paths and object reads to the same validated revisions (PR merge base)."""
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
        return VerifiedDiff(base, head, tuple(name.decode('utf-8', errors='strict')
                                              for name in raw.split(b'\0')[:-1]))
    except (KeyError, TypeError, ValueError, UnicodeError, subprocess.SubprocessError, OSError):
        return None


def changed_paths(repo, event_name, event):
    """Compatibility API: None means fallback; [] requires a verified empty diff."""
    context = verified_diff(repo, event_name, event)
    return list(context.paths) if context is not None else None


def regular_blob(repo, revision, path):
    # Literal pathspecs prevent ledger paths from acting as Git wildcard queries.
    raw = git(repo, '--literal-pathspecs', 'ls-tree', '-l', '-z', revision, '--', path)
    if not raw.endswith(b'\0') or raw.count(b'\0') != 1:
        raise ValueError('Missing or ambiguous blob')
    info, actual = raw[:-1].split(b'\t', 1)
    mode, kind, oid, size = info.split()
    if (actual.decode('utf-8') != path or mode not in (b'100644', b'100755')
            or kind != b'blob' or not lower_hash(oid.decode('ascii'))):
        raise ValueError('Expected regular Git blob')
    return oid.decode('ascii'), int(size)


def lower_hash(value):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{40}', value) is not None


def canonical_path(value):
    return (isinstance(value, str) and bool(value) and not value.startswith('/')
            and not re.search(r'[\x00-\x1f\x7f\\:]', value)
            and all(part not in ('', '.', '..') for part in value.split('/')))


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate JSON key')
        result[key] = value
    return result


def metadata(repo, blob):
    oid, size = blob
    if not 0 <= size <= METADATA_LIMIT:
        raise ValueError('Metadata exceeds routing bound')
    raw = git(repo, 'cat-file', 'blob', oid)
    if len(raw) != size:
        raise ValueError('Metadata size mismatch')
    return json.loads(raw.decode('utf-8'), object_pairs_hook=unique_object)


def manifest_entries(document):
    # Structural eligibility only: Verify-Baseline remains the full policy authority.
    if (not isinstance(document, dict) or set(document) != {
            'source_repository', 'source_commit', 'source_tree', 'retrieved_on',
            'files', 'source_location'} or not lower_hash(document['source_commit'])
            or not lower_hash(document['source_tree'])
            or not isinstance(document['source_repository'], str)
            or re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', document['source_repository']) is None
            or not isinstance(document['retrieved_on'], str)
            or not isinstance(document['source_location'], str)
            or not isinstance(document['files'], list)):
        raise ValueError('Malformed source manifest')
    entries = {}
    for item in document['files']:
        if (not isinstance(item, dict) or set(item) != {
                'path', 'git_blob_sha1', 'size', 'available'}
                or not canonical_path(item['path']) or not lower_hash(item['git_blob_sha1'])
                or type(item['size']) is not int or item['size'] < 0
                or type(item['available']) is not bool or item['path'] in entries):
            raise ValueError('Malformed/duplicate manifest entry')
        entries[item['path']] = item['git_blob_sha1']
    return entries


def approvals(document, manifest, baseline):
    if (not isinstance(document, dict) or set(document) != {'schema_version', 'source_commit', 'changes'}
            or type(document['schema_version']) is not int or document['schema_version'] != 1
            or document['source_commit'] != manifest['source_commit']
            or not isinstance(document['changes'], list)):
        raise ValueError('Malformed divergence ledger')
    entries = {}
    pull_prefix = 'https://github.com/' + manifest['source_repository'] + '/pull/'
    for item in document['changes']:
        if (not isinstance(item, dict) or set(item) != {
                'path', 'baseline_blob_sha1', 'approved_blob_sha1', 'provenance'}
                or not canonical_path(item['path']) or item['path'] in entries
                or item['path'] not in baseline
                or item['baseline_blob_sha1'] != baseline[item['path']]
                or not lower_hash(item['approved_blob_sha1'])
                or item['approved_blob_sha1'] == item['baseline_blob_sha1']):
            raise ValueError('Malformed/duplicate approval')
        provenance = item['provenance']
        if (not isinstance(provenance, dict) or set(provenance) != {'pull_request', 'commit', 'reason'}
                or not lower_hash(provenance['commit'])
                or not isinstance(provenance['pull_request'], str)
                or re.fullmatch(re.escape(pull_prefix) + '[1-9][0-9]*', provenance['pull_request']) is None
                or not isinstance(provenance['reason'], str) or not provenance['reason'].strip()):
            raise ValueError('Malformed approval provenance')
        entries[item['path']] = item
    return entries


def delegated_approvals(repo, context):
    """Return associated sources only after an exact base/head dependency proof.

    Failure leaves the ledger in the original path set, selecting every workload.
    No source contents, network provenance, or pointed commit availability is needed.
    """
    if LEDGER_PATH not in context.paths:
        return ()
    try:
        base_manifest = regular_blob(repo, context.base, MANIFEST_PATH)
        if base_manifest != regular_blob(repo, context.head, MANIFEST_PATH):
            return ()
        manifest = metadata(repo, base_manifest)
        baseline = manifest_entries(manifest)
        old, new = (approvals(metadata(repo, regular_blob(repo, revision, LEDGER_PATH)),
                              manifest, baseline) for revision in (context.base, context.head))
        if not old.keys() <= new.keys():
            return ()
        changed = tuple(path for path in new if new[path] != old.get(path))
        if not changed:
            return ()
        for path in changed:
            previous = old.get(path)
            expected_base = previous['approved_blob_sha1'] if previous else baseline[path]
            expected_head = new[path]['approved_blob_sha1']
            if (path not in context.paths or expected_base == expected_head
                    or regular_blob(repo, context.base, path)[0] != expected_base
                    or regular_blob(repo, context.head, path)[0] != expected_head):
                return ()
        return changed
    except (KeyError, TypeError, ValueError, UnicodeError, RecursionError,
            subprocess.SubprocessError, OSError):
        return ()


def classify_verified(repo, context):
    delegated = delegated_approvals(repo, context)
    paths = [path for path in context.paths if not (delegated and path == LEDGER_PATH)]
    return classify(paths), delegated


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
    context = verified_diff(args.repo, os.environ.get('GITHUB_EVENT_NAME'), event)
    paths = list(context.paths) if context is not None else None
    outputs, delegated = (classify_verified(args.repo, context) if context is not None
                          else (dict.fromkeys(COMPONENTS, 'true'), ()))
    print(json.dumps({'mode': 'full fallback' if paths is None else 'verified diff', 'paths': paths, 'categories': sorted({group for path in paths for group in categories(path)}) if paths is not None else ['infrastructure'], 'selected': outputs, 'metadata_delegation': {LEDGER_PATH: list(delegated)} if delegated else {}}, ensure_ascii=True))
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        for name, value in outputs.items():
            output.write(f'{name}={value}\n')


if __name__ == '__main__':
    main()
