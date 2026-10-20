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


class GitDiffTests(unittest.TestCase):
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
