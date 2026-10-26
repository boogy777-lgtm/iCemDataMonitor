import copy
import itertools
import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import routing

ROOT = Path(__file__).resolve().parents[3]


class RoutingTests(unittest.TestCase):
    def test_component_matrix(self):
        cases = {
            'rust/apps/host/src/main.rs': ('rust', 'packaging'),
            'rust/crates/icem-bus/src/lib.rs': routing.COMPONENTS,
            'rust/Cargo.lock': ('rust', 'packaging'),
            'rust/rust-toolchain.toml': ('rust', 'packaging'),
            'rust/tools/browser/dashboard.cjs': ('rust', 'packaging'),
            'iCemDataMonitor.Shell.WinUI/MainWindow.xaml': ('legacy',),
            'iCemDataMonitor.Core/New.cs': ('legacy',),
            'iCemDataMonitor.Sdk/Bus/BusLayout.cs': routing.COMPONENTS,
            'iCemDataMonitor.Data/Bus/BusReader.cs': routing.COMPONENTS,
            'iCemDataMonitor.Transport/ModbusTcpPublisher.cs': routing.COMPONENTS,
            'iCemDataMonitor.Host/Grpc/TelemetryServiceImpl.cs': routing.COMPONENTS,
            'iCemDataMonitor.Host.Core/Endpoints/RegistryEndpoints.cs': routing.COMPONENTS,
            'iCemDataMonitor.Contracts/Protos/telemetry.proto': routing.COMPONENTS,
            'iCemEmule.Core/Control/NewOracleInput.cs': routing.COMPONENTS,
            'Burservis.iCem.DataMonitor/NewSdkInput.cs': routing.COMPONENTS,
            'Burservis.iCem.DataMonitor/future.targets': routing.COMPONENTS,
            'Burservis.iCem.DataMonitor/future.resx': routing.COMPONENTS,
            'Burservis.iCem.DataMonitor/future.unknown': routing.COMPONENTS,
            'installer/gen-payload.ps1': routing.COMPONENTS,
            'installer/future.psm1': routing.COMPONENTS,
            'installer/future.unknown': routing.COMPONENTS,
            'rust/installer/windows-payload.json': ('rust', 'packaging'),
            '.github/workflows/new-workflow.yml': routing.COMPONENTS,
            'rust/tools/ci/routing.py': routing.COMPONENTS,
            'rust/migration/legacy-divergences.json': routing.COMPONENTS,
            'docs/Wireshark/iCemCrio.pcapng.bin': routing.COMPONENTS,
            'docs/Wireshark/notes.md': routing.COMPONENTS,
            'specs/dotnet-server/Э5_config.csv': routing.COMPONENTS,
            'iCemDataMonitor.Host/wwwroot/main.css': routing.COMPONENTS,
            'Directory.Build.targets': routing.COMPONENTS,
            'README.md': (), 'rust/docs/CI_CD.ru.md': (), 'docs/development.md': (),
            'docs/images/software-architecture.svg': (),
            'docs/images/next-architecture.svg': (),
            'docs/images/new.binary': routing.COMPONENTS,
            'docs/images/new.ps1': routing.COMPONENTS,
            'docs/runtime/new.svg': routing.COMPONENTS,
            'assets/new.svg': routing.COMPONENTS,
            'rust/assets/new.svg': ('rust', 'packaging'),
            'rust/docs/new.binary': ('rust', 'packaging'),
            'docs/new.binary': routing.COMPONENTS,
            'NewProject/New.csproj': routing.COMPONENTS,
            'future.config': routing.COMPONENTS,
            'iCemDataMonitor.Core/future.unknown': routing.COMPONENTS,
            '../README.md': routing.COMPONENTS,
        }
        for path, selected in cases.items():
            with self.subTest(path=path):
                self.assertEqual(routing.classify([path]), {c: str(c in selected).lower() for c in routing.COMPONENTS})
        self.assertEqual(routing.classify(['rust/README.md', 'iCemDataMonitor.Core/New.cs', 'rust/apps/new.rs']), dict.fromkeys(routing.COMPONENTS, 'true'))
        self.assertEqual(routing.categories('docs/images/software-architecture.svg'), {'documentation'})
        self.assertEqual(routing.categories('docs/images/next-architecture.svg'), {'documentation'})
        self.assertEqual(routing.categories('rust/crates/icem-bus/src/lib.rs'), {'rust', 'shared-contracts'})
        self.assertEqual(routing.categories('installer/Product.wxs'), {'legacy', 'packaging'})
        self.assertEqual(routing.categories('rust/migration/source-manifest.json'), {'rust', 'shared-contracts', 'infrastructure'})
        self.assertEqual(routing.classify([]), dict.fromkeys(routing.COMPONENTS, 'false'))

    def test_rule_schema(self):
        document = json.loads(routing.RULES_PATH.read_text())
        self.assertEqual(set(document), {'workloads', 'rules'})
        self.assertEqual(set(document['workloads']), {'legacy', 'rust', 'shared-contracts', 'packaging', 'infrastructure', 'documentation'})
        for workloads in document['workloads'].values():
            self.assertLessEqual(set(workloads), set(routing.COMPONENTS))
        for rule in document['rules']:
            self.assertIn(rule['category'], document['workloads'])
            self.assertTrue(rule['paths'])
            self.assertLessEqual(set(rule), {'category', 'paths', 'exclude', 'extensions'})

    def test_tracked_path_coverage(self):
        # Newly added project/root/extension cannot silently enter the optimized set.
        # Known infrastructure and intentionally unknown root config always select full.
        paths = routing.git(ROOT, 'ls-files', '-z').decode().split('\0')[:-1]
        full_roots = ('.github/', 'rust/migration/', 'rust/tools/ci/', '.vscode/')
        full_files = {'.editorconfig', '.gitattributes', '.gitignore'}
        uncovered = [p for p in paths if routing.categories(p) == {'infrastructure'} and p not in full_files and not p.startswith(full_roots)]
        self.assertEqual(uncovered, [], 'Review new tracked paths and routing dependencies')

    def test_consumer_dependencies(self):
        # Derive root file dependencies from actual consumers, not a second path list.
        found = set()
        for source in (ROOT / 'rust').rglob('*.rs'):
            if 'target' in source.parts:
                continue
            text = source.read_text(encoding='utf-8')
            for relative in re.findall(r'include_(?:str|bytes)!\(\s*"([^"]+)"', text):
                target = (source.parent / relative).resolve()
                if target.is_relative_to(ROOT) and not target.is_relative_to(ROOT / 'rust'):
                    found.add(target.relative_to(ROOT).as_posix())
        for source in (ROOT / 'rust/tests/support').glob('*.cs'):
            for relative in re.findall(r'Path.Combine\(root,\s*"([^"]+)"', source.read_text()):
                found.add(relative)
        self.assertTrue(found, 'No oracle/source dependencies discovered')
        for path in found:
            self.assertEqual(routing.classify([path]), dict.fromkeys(routing.COMPONENTS, 'true'), path)
        payload = json.loads((ROOT / 'rust/installer/windows-payload.json').read_text())
        for item in payload['files'] + payload['directories']:
            path = item['source'] + ('/future.json' if item in payload['directories'] else '')
            outputs = routing.classify([path])
            self.assertEqual((outputs['rust'], outputs['packaging']), ('true', 'true'), path)
        # New files in any C# oracle source family inherit the shared closure.
        for family in ('Core', 'Control', 'Dfb', 'Emitters'):
            self.assertEqual(routing.categories(f'iCemEmule.Core/{family}/Future.cs'), {'legacy', 'shared-contracts'})

    def test_legacy_diagnostics_contract(self):
        legacy = (ROOT / '.github/workflows/legacy-build.yml').read_text()
        # Scope each assertion to the real step; another step cannot satisfy it.
        steps = dict(part.split('\n', 1) for part in re.split(r'(?m)^      - name: ', legacy)[1:])
        prepare = steps['Prepare CI diagnostics']
        provenance = re.search(r'@\(\n(.*?)\n          \) \| Set-Content \(Join-Path \$diag "provenance.txt"\)', prepare, re.S)
        self.assertIsNotNone(provenance)
        self.assertEqual(provenance.group(1).splitlines(), [
            f'            "{key}=$env:{env}"' for key, env in (
                ('sha', 'GITHUB_SHA'), ('run_id', 'GITHUB_RUN_ID'),
                ('run_attempt', 'GITHUB_RUN_ATTEMPT'), ('runner_os', 'RUNNER_OS'),
                ('runner_arch', 'RUNNER_ARCH'), ('image_os', 'ImageOS'),
                ('image_version', 'ImageVersion'))])
        self.assertIn('Join-Path $env:RUNNER_TEMP "legacy-ci-diagnostics"', prepare)
        commands = (
            (prepare, 'dotnet --info', '(Join-Path $diag "dotnet-info.txt")', 'infoExit'),
            (steps['Build solution'], 'dotnet build CI.slnf -c Release --nologo',
             '(Join-Path $env:RUNNER_TEMP "legacy-ci-diagnostics/build.log")', 'buildExit'),
            (steps['Run tests'], 'dotnet test CI.slnf -c Release --no-build --nologo --blame-hang-timeout 10m --filter "Category!=Live" --logger trx',
             '(Join-Path $env:RUNNER_TEMP "legacy-ci-diagnostics/test.log")', 'testExit'),
        )
        for step, command, output, variable in commands:
            self.assertIn(f'{command} 2>&1 | Tee-Object -FilePath {output}\n'
                          f'          ${variable} = $LASTEXITCODE\n'
                          f'          exit ${variable}\n', step)
            self.assertNotIn('continue-on-error:', step)
        upload = steps['Upload test diagnostics']
        self.assertIn('        if: always()\n', upload)
        self.assertIn('        uses: actions/upload-artifact@v5\n', upload)
        self.assertIn('          name: legacy-ci-diagnostics-${{ github.run_id }}-${{ github.run_attempt }}\n', upload)
        patterns = re.search(r'          path: \|\n(.*?)\n          if-no-files-found:', upload, re.S)
        self.assertIsNotNone(patterns)
        self.assertEqual(patterns.group(1).splitlines(), [
            '            ${{ runner.temp }}/legacy-ci-diagnostics/*.txt',
            '            ${{ runner.temp }}/legacy-ci-diagnostics/*.log',
            '            **/TestResults/**/*.trx',
            '            **/TestResults/**/*Sequence.xml',
            '            **/TestResults/**/*.log'])
        self.assertIn('          if-no-files-found: warn\n', upload)
        self.assertIn('          retention-days: 7\n', upload)

    def test_workflow_wiring(self):
        workflows = ROOT / '.github/workflows'
        coordinator = (workflows / 'ci.yml').read_text()
        self.assertNotRegex(coordinator, r'(?m)^    paths(?:-ignore)?:')
        self.assertIn('fetch-depth: 0', coordinator)
        self.assertIn('cancel-in-progress: ${{ github.event_name == \'pull_request\' }}', coordinator)
        ids = set(re.findall(r'^  ([a-z][a-z-]*):\n', coordinator.split('jobs:\n', 1)[1], re.M))
        self.assertEqual(ids, {'classify', 'baseline', 'routing-tests', 'legacy', 'rust', 'packaging', 'gate'})
        gate = coordinator.split('  gate:\n', 1)[1]
        self.assertIn('name: CI gate', gate)
        self.assertIn('if: always()', gate)
        self.assertIn('needs: [classify, baseline, routing-tests, legacy, rust, packaging]', gate)
        self.assertIn('CI_NEEDS_JSON: ${{ toJSON(needs) }}', gate)
        self.assertIn('Test-BaselineGuard.ps1', coordinator)
        self.assertIn('Verify-Baseline.ps1 -RequireComplete', coordinator)
        for job, file in [('legacy', 'legacy-build.yml'), ('rust', 'rust-migration.yml'), ('packaging', 'rust-installer-contracts.yml')]:
            # Job body extends until next two-space job key (not four-space fields).
            block = re.split(r'\n  [a-z][a-z-]*:\n', coordinator.split(f'  {job}:\n', 1)[1], maxsplit=1)[0]
            self.assertIn(f"if: needs.classify.outputs.{job} == 'true'", block)
            self.assertIn(f'uses: ./.github/workflows/{file}', block)
            called = (workflows / file).read_text()
            self.assertIn('  workflow_call:', called)
            self.assertNotRegex(called, r'(?m)^  (push|pull_request):')
            self.assertNotIn('concurrency:', called)  # caller owns cancellation
        legacy = (workflows / 'legacy-build.yml').read_text()
        for command in ('dotnet build CI.slnf', 'dotnet test CI.slnf', 'installer/build-msi.ps1 -MsiVersion 0.0.0 -NoPause'):
            self.assertIn(command, legacy)
        rust = (workflows / 'rust-migration.yml').read_text()
        for command in ('Verify-Scaffold.ps1 -RequireCompleteBaseline', 'cargo fetch --locked', 'theme-contract.cjs', 'dashboard.cjs', 'host-sniffer-differential-report.json'):
            self.assertIn(command, rust)
        release = (workflows / 'rust-windows-release.yml').read_text()
        self.assertNotIn('  pull_request:', release)
        self.assertIn('uses: ./.github/workflows/rust-installer-contracts.yml', release)
        self.assertIn('cancel-in-progress: false', release)
        self.assertIn('group: rust-windows-release-manual', release)
        packaging = (workflows / 'rust-installer-contracts.yml').read_text()
        for command in ('Test-WindowsPackaging.ps1', 'Test-WindowsInstaller.ps1', 'if: always()', 'installer-contract-evidence'):
            self.assertIn(command, packaging)


class GitFixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.run_git('init', '-q')
        self.run_git('config', 'user.email', 'ci-fixture@example.invalid')
        self.run_git('config', 'user.name', 'Routing fixture')
        self.write('docs/notes.md')
        self.base = self.commit()

    def run_git(self, *args):
        return routing.git(self.repo, *args).decode().strip()

    def write(self, name):
        p = self.repo / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text('fixture\n')

    def commit(self):
        self.run_git('add', '-A')
        self.run_git('commit', '-qm', 'fixture')
        return self.run_git('rev-parse', 'HEAD')

    def paths(self, head, base=None, event='push'):
        base = base or self.base
        data = {'before': base, 'after': head} if event == 'push' else {'pull_request': {'base': {'sha': base}, 'head': {'sha': head}}}
        return routing.changed_paths(self.repo, event, data)


class GitDiffTests(GitFixture):
    def test_verified_empty_and_dispatch(self):
        self.assertEqual(self.paths(self.base), [])
        self.assertIsNone(routing.changed_paths(self.repo, 'workflow_dispatch', {}))

    def test_rename_and_deleted_old_path_selects_shared(self):
        self.write('specs/contract.yaml')
        old = self.commit()
        self.run_git('mv', 'specs/contract.yaml', 'docs/contract.md')
        head = self.commit()
        paths = self.paths(head, old)
        self.assertEqual(set(paths), {'specs/contract.yaml', 'docs/contract.md'})
        self.assertEqual(routing.classify(paths), dict.fromkeys(routing.COMPONENTS, 'true'))
        (self.repo / 'docs/contract.md').unlink()
        deleted = self.commit()
        self.assertEqual(self.paths(deleted, head), ['docs/contract.md'])

    def test_nul_names_with_tabs_newlines(self):
        # Windows prohibits these filenames; still test byte parsing there using Git output.
        raw = b'rust/apps/name\tpart.rs\0specs/name\npart.yaml\0'
        with patch.object(routing, 'git', side_effect=[b'', b'', raw]):
            self.assertEqual(self.paths('1' * 40), ['rust/apps/name\tpart.rs', 'specs/name\npart.yaml'])
        if __import__('os').name != 'nt':
            self.write('rust/apps/name\tpart.rs')
            self.write('specs/name\npart.yaml')
            paths = self.paths(self.commit())
            self.assertEqual(set(paths), {'rust/apps/name\tpart.rs', 'specs/name\npart.yaml'})

    def test_pr_uses_merge_base(self):
        self.run_git('checkout', '-qb', 'topic')
        self.write('rust/apps/change.rs')
        head = self.commit()
        self.run_git('checkout', '-q', '--detach', self.base)
        self.write('iCemDataMonitor.Core/main.cs')
        base_tip = self.commit()
        self.assertEqual(self.paths(head, base_tip, 'pull_request'), ['rust/apps/change.rs'])

    def test_missing_malformed_failed_and_incomplete_diff(self):
        for event, data in [('push', {}), ('push', {'before': '0' * 40, 'after': self.base}), ('push', {'before': 'bad', 'after': self.base}), ('push', {'before': 'f' * 40, 'after': self.base}), ('pull_request', {'pull_request': []}), ('unknown', {})]:
            self.assertIsNone(routing.changed_paths(self.repo, event, data))
        with patch.object(routing, 'git', side_effect=[b'', b'', b'not-a-merge-base']):
            self.assertIsNone(self.paths(self.base, event='pull_request'))
        with patch.object(routing, 'git', side_effect=subprocess.CalledProcessError(1, 'git')):
            self.assertIsNone(self.paths(self.base))
        for raw in (b'path-without-terminator', b'\xff\0'):
            with patch.object(routing, 'git', side_effect=[b'', b'', raw]):
                self.assertIsNone(self.paths(self.base))

    def test_cli_event_file_fallback(self):
        import os
        event_file = self.repo / 'event.json'
        output = self.repo / 'outputs.txt'
        for name, content in [('workflow_dispatch', '{}'), ('push', '{broken'), ('push', json.dumps({'before': '$(exit 0)', 'after': self.base}))]:
            event_file.write_text(content)
            output.write_text('')
            env = dict(os.environ, GITHUB_EVENT_NAME=name, GITHUB_EVENT_PATH=str(event_file), GITHUB_OUTPUT=str(output))
            result = subprocess.run([__import__('sys').executable, str(Path(routing.__file__)), 'classify', '--repo', str(self.repo)], env=env, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(output.read_text().splitlines(), ['legacy=true', 'rust=true', 'packaging=true'])
            self.assertEqual(json.loads(result.stdout)['mode'], 'full fallback')


class LedgerRoutingTests(GitFixture):
    A = 'iCemDataMonitor.Core/A.cs'
    B = 'iCemDataMonitor.UI.Tests/B.cs'
    SHARED = 'iCemDataMonitor.Data/Bus/BusReader.cs'
    FROZEN = 'iCemDataMonitor.Host/wwwroot/index.html'

    def setUp(self):
        super().setUp()
        self.manifest = {
            'source_repository': 'fixture/repository', 'source_commit': 'a' * 40,
            'source_tree': 'b' * 40, 'retrieved_on': '2026-10-21',
            'source_location': 'repository root (parent of rust/)', 'files': []}
        for path in (self.A, self.B, self.SHARED, self.FROZEN):
            self.write(path)
            self.manifest['files'].append({'path': path, 'git_blob_sha1': self.hash(path),
                                           'size': 8, 'available': True})
        self.write_content(self.A, 'old approval\n')
        self.old = {'schema_version': 2, 'source_commit': 'a' * 40,
                    'changes': [self.approval(self.A)]}
        self.write_json(routing.MANIFEST_PATH, self.manifest)
        self.write_json(routing.LEDGER_PATH, self.old)
        self.base = self.commit()

    def write_content(self, path, content):
        target = self.repo / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content if isinstance(content, bytes) else content.encode('utf-8'))

    def write_json(self, path, document):
        self.write_content(path, json.dumps(document))

    def hash(self, path):
        # Match git add's path-aware clean conversion (including Windows CRLF).
        return self.run_git('hash-object', '--path=' + path, '--', str(self.repo / path))

    def approval(self, path):
        baseline = next(item['git_blob_sha1'] for item in self.manifest['files'] if item['path'] == path)
        return {'operation': 'modify', 'path': path, 'baseline_blob_sha1': baseline,
                'approved_blob_sha1': self.hash(path),
                'provenance': {'pull_request': 'https://github.com/fixture/repository/pull/1',
                               'commit': 'c' * 40, 'reason': 'Fixture approved source change'}}

    def update(self, *paths):
        new = copy.deepcopy(self.old)
        for path in paths or (self.A,):
            self.write_content(path, 'new approval\n')
            new['changes'] = [item for item in new['changes'] if item['path'] != path]
            new['changes'].append(self.approval(path))
        self.write_json(routing.LEDGER_PATH, new)
        return new

    def selection(self, head, base=None, event='push'):
        base = base or self.base
        data = ({'before': base, 'after': head} if event == 'push' else
                {'pull_request': {'base': {'sha': base}, 'head': {'sha': head}}})
        context = routing.verified_diff(self.repo, event, data)
        self.assertIsNotNone(context)
        return routing.classify_verified(self.repo, context)

    def assert_full(self, head, base=None):
        selected, delegated = self.selection(head, base)
        self.assertEqual(selected, dict.fromkeys(routing.COMPONENTS, 'true'))
        self.assertEqual(delegated, ())

    def test_updated_and_added_approvals_keep_component_closures(self):
        for paths, extra in (((self.A,), None), ((self.B,), None), ((self.A, self.B), None),
                             ((self.SHARED,), None), ((self.FROZEN,), None),
                             ((self.A,), 'rust/apps/host/src/main.rs'),
                             ((self.A,), 'Future/unknown.input')):
            with self.subTest(paths=paths, extra=extra):
                self.run_git('reset', '--hard', '-q', self.base)
                self.update(*paths)
                if extra:
                    self.write(extra)
                head = self.commit()
                selected, delegated = self.selection(head)
                expected_paths = list(paths) + ([extra] if extra else [])
                self.assertEqual(selected, routing.classify(expected_paths))
                self.assertEqual(set(delegated), set(paths))
                # Raw paths never acquire metadata exemption.
                self.assertEqual(routing.classify(self.paths(head)), dict.fromkeys(routing.COMPONENTS, 'true'))

    def delete_approval(self, *paths):
        new = copy.deepcopy(self.old)
        for path in paths:
            baseline = next(item['git_blob_sha1'] for item in self.manifest['files'] if item['path'] == path)
            item = {'operation': 'delete', 'path': path, 'baseline_blob_sha1': baseline,
                    'provenance': copy.deepcopy(self.old['changes'][0]['provenance'])}
            new['changes'] = [entry for entry in new['changes'] if entry['path'] != path] + [item]
            (self.repo / path).unlink()
        self.write_json(routing.LEDGER_PATH, new)
        return new

    def test_baseline_and_previously_modified_deletions(self):
        for paths in ((self.A,), (self.B,), (self.A, self.B)):
            with self.subTest(paths=paths):
                self.run_git('reset', '--hard', '-q', self.base)
                self.delete_approval(*paths)
                selected, delegated = self.selection(self.commit())
                self.assertEqual(selected, routing.classify(paths))
                self.assertEqual(set(delegated), set(paths))

    def test_mixed_modify_delete_and_unchanged_existing_delete(self):
        new = self.delete_approval(self.B)
        self.write_content(self.A, 'updated alongside deletion\n')
        new['changes'][0] = self.approval(self.A)
        self.write_json(routing.LEDGER_PATH, new)
        head = self.commit()
        self.assertEqual(set(self.selection(head)[1]), {self.A, self.B})
        self.old = new
        self.base = head
        self.update(self.A)
        self.assertEqual(self.selection(self.commit())[1], (self.A,))

    def test_historical_schema1_base_schema2_head(self):
        historical = copy.deepcopy(self.old)
        historical['schema_version'] = 1
        for item in historical['changes']:
            del item['operation']
        self.write_json(routing.LEDGER_PATH, historical)
        self.base = self.commit()
        self.delete_approval(self.A, self.B)
        self.assertEqual(set(self.selection(self.commit())[1]), {self.A, self.B})
        self.run_git('reset', '--hard', '-q', self.base)
        new = self.update()
        new['schema_version'] = 1
        for item in new['changes']:
            del item['operation']
        self.write_json(routing.LEDGER_PATH, new)
        self.assert_full(self.commit())

    def test_delete_ambiguities_keep_full_workload(self):
        variants = ('hash-field', 'wrong-baseline', 'operation', 'missing-operation',
                    'wrong-base', 'missing-base', 'absent-both', 'base-symlink',
                    'head-file', 'head-tree', 'head-symlink', 'head-submodule',
                    'ledger-only', 'source-only', 'provenance-field')
        for variant in variants:
            with self.subTest(variant=variant):
                self.run_git('reset', '--hard', '-q', self.base)
                base = self.base
                if variant in ('wrong-base', 'missing-base', 'absent-both', 'base-symlink'):
                    if variant == 'wrong-base':
                        self.write_content(self.B, 'unexpected base\n')
                    elif variant == 'base-symlink':
                        self.run_git('update-index', '--cacheinfo', '120000,' + self.hash(self.B) + ',' + self.B)
                        self.run_git('commit', '-qm', 'base link')
                    else:
                        (self.repo / self.B).unlink()
                    if variant != 'base-symlink':
                        base = self.commit()
                    else:
                        base = self.run_git('rev-parse', 'HEAD')
                    if not (self.repo / self.B).exists():
                        self.write_content(self.B, 'temporary deletion source\n')
                new = self.delete_approval(self.B)
                item = new['changes'][-1]
                if variant == 'hash-field': item['approved_blob_sha1'] = 'd' * 40
                elif variant == 'wrong-baseline': item['baseline_blob_sha1'] = 'd' * 40
                elif variant == 'operation': item['operation'] = 'remove'
                elif variant == 'missing-operation': del item['operation']
                elif variant == 'provenance-field': item['provenance']['extra'] = True
                elif variant in ('head-file', 'ledger-only'):
                    self.run_git('restore', '--source', self.base, '--', self.B)
                elif variant == 'head-tree': self.write_content(self.B + '/child.cs', 'tree\n')
                elif variant == 'source-only': new = self.old
                self.write_json(routing.LEDGER_PATH, new)
                if variant in ('head-symlink', 'head-submodule'):
                    self.run_git('add', '-A')
                    mode, oid = ('120000', self.hash(self.A)) if variant == 'head-symlink' else ('160000', self.base)
                    self.run_git('update-index', '--add', '--cacheinfo', mode + ',' + oid + ',' + self.B)
                    self.run_git('commit', '-qm', 'nonregular head')
                    head = self.run_git('rev-parse', 'HEAD')
                else:
                    head = self.commit()
                if variant == 'source-only':
                    # No metadata path exists to delegate; source keeps its normal closure.
                    self.assertEqual(self.selection(head, base)[1], ())
                else:
                    self.assert_full(head, base)

    def test_deletion_provenance_edit_and_resurrection_fail_closed(self):
        new = self.delete_approval(self.B)
        deleted = self.commit()
        for resurrection in (False, True):
            self.run_git('reset', '--hard', '-q', deleted)
            edited = copy.deepcopy(new)
            item = edited['changes'][-1]
            if resurrection:
                self.write_content(self.B, 'resurrected\n')
                item['operation'] = 'modify'
                item['approved_blob_sha1'] = self.hash(self.B)
            else:
                item['provenance']['reason'] = 'Changed deletion provenance'
            self.write_json(routing.LEDGER_PATH, edited)
            self.assert_full(self.commit(), deleted)

    def test_crlf_checkout_hashes_match_new_and_updated_git_approvals(self):
        self.run_git('config', 'core.autocrlf', 'true')
        # Rebuild the fixture with explicit CRLF bytes, independent of host OS defaults.
        for path in (self.A, self.B):
            self.write_content(path, b'fixture\r\n')
            item = next(item for item in self.manifest['files'] if item['path'] == path)
            item['git_blob_sha1'] = self.hash(path)
            item['size'] = 8  # Canonical Git bytes are fixture + LF.
        self.write_content(self.A, b'old approval\r\n')
        self.old['changes'] = [self.approval(self.A)]
        self.write_json(routing.MANIFEST_PATH, self.manifest)
        self.write_json(routing.LEDGER_PATH, self.old)
        # Canonical content can equal the previous LF fixture exactly.
        self.run_git('add', '-A')
        self.run_git('commit', '--allow-empty', '-qm', 'CRLF baseline fixture')
        self.base = self.run_git('rev-parse', 'HEAD')
        self.assertEqual(self.run_git('rev-parse', self.base + ':' + self.A), self.hash(self.A))
        baseline = next(item['git_blob_sha1'] for item in self.manifest['files'] if item['path'] == self.B)
        self.assertEqual(self.run_git('rev-parse', self.base + ':' + self.B), baseline)
        self.assertNotEqual(self.run_git('hash-object', '--no-filters', '--', str(self.repo / self.B)), baseline)
        new = copy.deepcopy(self.old)
        for path in (self.A, self.B):
            self.write_content(path, b'new approval\r\n')
        new['changes'] = [self.approval(path) for path in (self.A, self.B)]
        self.write_json(routing.LEDGER_PATH, new)
        head = self.commit()
        for item in new['changes']:
            self.assertEqual(self.run_git('rev-parse', head + ':' + item['path']), item['approved_blob_sha1'])
        selected, delegated = self.selection(head)
        self.assertEqual(selected, routing.classify([self.A, self.B]))
        self.assertEqual(set(delegated), {self.A, self.B})

    def test_nonmember_changes_do_not_delegate(self):
        for variant in ('format', 'reorder', 'provenance', 'unrelated-source', 'removal',
                        'unchanged-source', 'wrong-base', 'wrong-head', 'missing-source', 'symlink-source'):
            with self.subTest(variant=variant):
                self.run_git('reset', '--hard', '-q', self.base)
                new = self.update()
                if variant in ('format', 'reorder', 'provenance', 'unchanged-source'):
                    self.run_git('restore', '--source', self.base, '--', self.A)
                if variant == 'format':
                    self.write_content(routing.LEDGER_PATH, json.dumps(self.old, indent=4))
                elif variant == 'reorder':
                    self.write_json(routing.LEDGER_PATH, dict(reversed(list(self.old.items()))))
                elif variant == 'provenance':
                    new = copy.deepcopy(self.old)
                    new['changes'][0]['provenance']['reason'] = 'Only provenance changed'
                    self.write_json(routing.LEDGER_PATH, new)
                elif variant == 'unrelated-source':
                    self.run_git('restore', '--source', self.base, '--', self.A)
                    self.write_content(self.B, 'unrelated source\n')
                elif variant == 'removal':
                    new['changes'] = []
                    self.write_json(routing.LEDGER_PATH, new)
                elif variant == 'wrong-base':
                    old = copy.deepcopy(self.old)
                    old['changes'][0]['approved_blob_sha1'] = 'd' * 40
                    self.write_json(routing.LEDGER_PATH, old)
                    self.run_git('restore', '--source', self.base, '--', self.A)
                    bad_base = self.commit()
                    new = self.update()
                    self.assert_full(self.commit(), bad_base)
                    continue
                elif variant == 'wrong-head':
                    new['changes'][0]['approved_blob_sha1'] = 'd' * 40
                    self.write_json(routing.LEDGER_PATH, new)
                elif variant == 'missing-source':
                    (self.repo / self.A).unlink()
                elif variant == 'symlink-source':
                    # Build the mode with Git, including on Windows without symlink privilege.
                    self.run_git('add', '-A')
                    self.run_git('update-index', '--cacheinfo', '120000,' + self.hash(self.A) + ',' + self.A)
                    self.run_git('commit', '-qm', 'symlink fixture')
                    self.assert_full(self.run_git('rev-parse', 'HEAD'))
                    continue
                self.assert_full(self.commit())

    def test_invalid_ledger_metadata_at_either_revision(self):
        variants = ('version', 'bool-version', 'source-commit', 'extra-field', 'entry-field',
                    'baseline-hash', 'approved-hash', 'baseline-equals-approved', 'outside-manifest',
                    'noncanonical-path', 'duplicate-entry', 'provenance-field', 'pr-url',
                    'provenance-commit', 'empty-reason', 'duplicate-json', 'bad-utf8',
                    'missing', 'oversize', 'symlink', 'malformed-json')
        for revision, variant in itertools.product(('base', 'head'), variants):
            with self.subTest(revision=revision, variant=variant):
                self.run_git('reset', '--hard', '-q', self.base)
                new = self.update()
                good_head = self.commit()
                document = copy.deepcopy(self.old if revision == 'base' else new)
                if revision == 'base':
                    self.run_git('reset', '--hard', '-q', self.base)
                item = document['changes'][0]
                if variant == 'version': document['schema_version'] = 3
                elif variant == 'bool-version': document['schema_version'] = True
                elif variant == 'source-commit': document['source_commit'] = 'd' * 40
                elif variant == 'extra-field': document['extra'] = True
                elif variant == 'entry-field': item['extra'] = True
                elif variant == 'baseline-hash': item['baseline_blob_sha1'] = 'd' * 40
                elif variant == 'approved-hash': item['approved_blob_sha1'] = 'D' * 40
                elif variant == 'baseline-equals-approved': item['approved_blob_sha1'] = item['baseline_blob_sha1']
                elif variant == 'outside-manifest': item['path'] = 'iCemDataMonitor.Core/Absent.cs'
                elif variant == 'noncanonical-path': item['path'] = 'iCemDataMonitor.Core/../A.cs'
                elif variant == 'duplicate-entry': document['changes'].append(copy.deepcopy(item))
                elif variant == 'provenance-field': item['provenance']['extra'] = True
                elif variant == 'pr-url': item['provenance']['pull_request'] += '/bad'
                elif variant == 'provenance-commit': item['provenance']['commit'] = 'invalid'
                elif variant == 'empty-reason': item['provenance']['reason'] = '   '
                self.write_json(routing.LEDGER_PATH, document)
                if variant == 'duplicate-json':
                    self.write_content(routing.LEDGER_PATH, json.dumps(document).replace('"schema_version": 2', '"schema_version": 2, "schema_version": 2'))
                elif variant == 'bad-utf8': self.write_content(routing.LEDGER_PATH, b'\xff')
                elif variant == 'malformed-json': self.write_content(routing.LEDGER_PATH, '{')
                elif variant == 'missing': (self.repo / routing.LEDGER_PATH).unlink()
                elif variant == 'oversize': self.write_content(routing.LEDGER_PATH, ' ' * (routing.METADATA_LIMIT + 1))
                if variant == 'symlink':
                    self.run_git('add', '-A')
                    self.run_git('update-index', '--cacheinfo', '120000,' + self.hash(routing.LEDGER_PATH) + ',' + routing.LEDGER_PATH)
                    self.run_git('commit', '-qm', 'symlink metadata')
                    bad = self.run_git('rev-parse', 'HEAD')
                else:
                    bad = self.commit()
                if revision == 'base':
                    self.assert_full(good_head, bad)
                else:
                    self.assert_full(bad)

    def test_manifest_identity_and_invalid_manifest(self):
        for variant in ('identity', 'source-commit', 'duplicate-path', 'duplicate-json',
                        'entry-hash', 'schema', 'bad-utf8', 'missing'):
            with self.subTest(variant=variant):
                self.run_git('reset', '--hard', '-q', self.base)
                manifest = copy.deepcopy(self.manifest)
                if variant == 'source-commit': manifest['source_commit'] = 'invalid'
                elif variant == 'duplicate-path': manifest['files'].append(copy.deepcopy(manifest['files'][0]))
                elif variant == 'entry-hash': manifest['files'][0]['git_blob_sha1'] = 'D' * 40
                elif variant == 'schema': manifest['files'][0]['extra'] = True
                self.write_json(routing.MANIFEST_PATH, manifest)
                if variant == 'duplicate-json':
                    self.write_content(routing.MANIFEST_PATH, json.dumps(manifest).replace('"source_tree":', '"source_tree": "b", "source_tree":'))
                elif variant == 'bad-utf8': self.write_content(routing.MANIFEST_PATH, b'\xff')
                elif variant == 'missing': (self.repo / routing.MANIFEST_PATH).unlink()
                if variant == 'identity':
                    self.write_content(routing.MANIFEST_PATH, json.dumps(manifest, indent=4))
                    base = self.base
                else:
                    base = self.commit()
                self.update()
                self.assert_full(self.commit(), base)

    def test_pr_proof_uses_merge_base_and_worktree_is_irrelevant(self):
        self.run_git('checkout', '-qb', 'approval-topic')
        self.update()
        head = self.commit()
        self.run_git('checkout', '-q', '--detach', self.base)
        self.write_content(self.A, 'different base tip approval\n')
        base_tip = self.commit()
        # Worktree metadata and source also differ from the PR head; only Git objects count.
        self.write_content(routing.LEDGER_PATH, 'invalid JSON')
        self.write_content(routing.MANIFEST_PATH, 'invalid JSON')
        selected, delegated = self.selection(head, base_tip, 'pull_request')
        self.assertEqual(selected, routing.classify([self.A]))
        self.assertEqual(delegated, (self.A,))
        self.assert_full(head, base_tip)  # push uses the tip, whose source hash differs

    def test_metadata_git_failure_keeps_ledger(self):
        self.update()
        head = self.commit()
        context = routing.verified_diff(self.repo, 'push', {'before': self.base, 'after': head})
        with patch.object(routing, 'git', side_effect=OSError('Git unavailable')):
            selected, delegated = routing.classify_verified(self.repo, context)
        self.assertEqual(selected, dict.fromkeys(routing.COMPONENTS, 'true'))
        self.assertEqual(delegated, ())

    def test_literal_paths_and_regular_source_base(self):
        # A wildcard-looking approval must query exactly that source, never its siblings.
        path = 'iCemDataMonitor.Core/[A].cs'
        self.write(path)
        self.manifest['files'].append({'path': path, 'git_blob_sha1': self.hash(path),
                                      'size': 8, 'available': True})
        self.write_json(routing.MANIFEST_PATH, self.manifest)
        self.base = self.commit()
        self.update(path)
        self.assertEqual(self.selection(self.commit())[1], (path,))
        self.run_git('reset', '--hard', '-q', self.base)
        self.run_git('update-index', '--cacheinfo', '120000,' + self.hash(path) + ',' + path)
        self.run_git('commit', '-qm', 'base symlink source')
        base = self.run_git('rev-parse', 'HEAD')
        self.update(path)
        self.assert_full(self.commit(), base)

    def test_cli_real_proof_diagnostics_and_outputs(self):
        import os
        import sys
        self.update(self.A, self.B)
        head = self.commit()
        event = self.repo / 'event.json'
        output = self.repo / 'outputs.txt'
        event.write_text(json.dumps({'before': self.base, 'after': head}))
        env = dict(os.environ, GITHUB_EVENT_NAME='push', GITHUB_EVENT_PATH=str(event), GITHUB_OUTPUT=str(output))
        result = subprocess.run([sys.executable, str(Path(routing.__file__)), 'classify', '--repo', str(self.repo)],
                                env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(output.read_text().splitlines(), ['legacy=true', 'rust=false', 'packaging=false'])
        diagnostic = json.loads(result.stdout)
        self.assertEqual(diagnostic['mode'], 'verified diff')
        self.assertEqual(set(diagnostic['paths']), {self.A, self.B, routing.LEDGER_PATH})
        self.assertEqual(set(diagnostic['metadata_delegation'][routing.LEDGER_PATH]), {self.A, self.B})
        self.assertIn('infrastructure', diagnostic['categories'])


class GateTests(unittest.TestCase):
    def needs(self, selected):
        jobs = {name: {'result': 'success', 'outputs': {}} for name in ('classify', 'baseline', 'routing-tests')}
        jobs['classify']['outputs'] = {name: str(name in selected).lower() for name in routing.COMPONENTS}
        jobs.update({name: {'result': 'success' if name in selected else 'skipped', 'outputs': {}} for name in routing.COMPONENTS})
        return jobs

    def test_every_selection_and_result(self):
        states = ('success', 'failure', 'cancelled', 'skipped', 'neutral', 'timed_out', '', None)
        for bits in itertools.product((False, True), repeat=3):
            selected = {n for n, bit in zip(routing.COMPONENTS, bits) if bit}
            good = self.needs(selected)
            routing.validate_gate(good)
            for job in good:
                for state in states:
                    if state == good[job]['result']:
                        continue
                    bad = copy.deepcopy(good)
                    bad[job]['result'] = state
                    with self.assertRaises(ValueError, msg=(selected, job, state)):
                        routing.validate_gate(bad)

    def test_malformed_schema(self):
        for value in (None, [], {}, {'unexpected': {}}):
            with self.assertRaises(ValueError):
                routing.validate_gate(value)
        for job in self.needs(set()):
            for value in (None, {}, {'result': 'success'}, {'result': 'success', 'outputs': []}):
                bad = self.needs(set()); bad[job] = value
                with self.assertRaises(ValueError):
                    routing.validate_gate(bad)
        for outputs in ({}, {'legacy': 'true'}, {'legacy': 'false', 'rust': 'false', 'packaging': 'false', 'extra': 'true'}, *({'legacy': x, 'rust': 'false', 'packaging': 'false'} for x in ('True', '', True, None, 1))):
            bad = self.needs(set()); bad['classify']['outputs'] = outputs
            with self.assertRaises(ValueError):
                routing.validate_gate(bad)


if __name__ == '__main__':
    unittest.main()
