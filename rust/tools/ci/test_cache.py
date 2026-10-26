"""CI-3 identity and negative workflow mutations, using only stdlib."""
import copy
from pathlib import Path
import shutil
import tempfile
import tomllib
import unittest

import cache_contract
import cache_identity as cache

ROOT = Path(__file__).resolve().parents[3]


class IdentityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'repo'
        self.home = Path(self.temp.name) / 'cargo'
        self.home.mkdir()
        self.env = dict(RUNNER_OS='Windows', RUNNER_ARCH='X64', ImageOS='win25', ImageVersion='20261001.1')
        for name in (*cache.COMMON_SCAFFOLD_INPUTS, 'rust/rust-toolchain.toml', 'rust/Cargo.lock', 'rust/Cargo.toml'):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, path)
        self.pin = tomllib.loads((self.root / 'rust/rust-toolchain.toml').read_text())['toolchain']['channel']
        self.compiler = f'rustc {self.pin} (fixture 2026-09-01)\nhost: x86_64-pc-windows-msvc\nLLVM version: 23.1'

    def identity(self, **kwargs):
        args = dict(root=self.root, cargo_home=self.home, env=self.env,
                    compiler=self.compiler, cargo=f'cargo {self.pin} fixture', source='a' * 40)
        args.update(kwargs)
        return cache.identity(**args)

    def test_each_common_input_and_manifest_lock_pin_affects_compatibility(self):
        baseline = self.identity()
        for path in self.root.rglob('*'):
            if not path.is_file():
                continue
            with self.subTest(path=path.relative_to(self.root)):
                original = path.read_bytes()
                path.write_bytes(original + b'\n# changed input\n')
                self.assertNotEqual(baseline['build_prefix'], self.identity()['build_prefix'])
                path.write_bytes(original)
        manifest = self.root / 'rust/crates/next/Cargo.toml'
        manifest.parent.mkdir(parents=True)
        manifest.write_text('[package]\nname="next"\n')
        self.assertNotEqual(baseline['build_prefix'], self.identity()['build_prefix'])

    def test_source_only_changes_primary_build_key(self):
        first, next_commit = self.identity(), self.identity(source='b' * 40)
        self.assertEqual(first['build_prefix'], next_commit['build_prefix'])
        self.assertEqual(first['download_key'], next_commit['download_key'])
        self.assertNotEqual(first['build_key'], next_commit['build_key'])
        self.assertEqual(first['build_key'], first['build_prefix'] + 'a' * 40)

    def test_each_runner_and_compiler_input_affects_compatibility(self):
        baseline = self.identity()['build_prefix']
        for name in ('ImageOS', 'ImageVersion', 'VCToolsVersion', 'WindowsSDKVersion', 'VSCMD_VER',
                     'WindowsSdkDir', 'VCToolsInstallDir', 'INCLUDE', 'LIB', 'LIBPATH'):
            with self.subTest(name=name):
                self.assertNotEqual(baseline, self.identity(env=dict(self.env, **{name: 'changed'}))['build_prefix'])
        self.assertNotEqual(baseline, self.identity(compiler=self.compiler + '\nextra compiler fact')['build_prefix'])
        self.assertNotEqual(baseline, self.identity(cargo='cargo changed')['build_prefix'])
        for name in ('RUNNER_OS', 'RUNNER_ARCH', 'ImageOS', 'ImageVersion'):
            env = copy.copy(self.env)
            del env[name]
            with self.subTest(missing=name), self.assertRaises(ValueError):
                self.identity(env=env)
        for kwargs in (dict(compiler=self.compiler.replace(f'rustc {self.pin} ', 'rustc mismatch ', 1)),
                       dict(compiler=self.compiler.replace('x86_64', 'aarch64')), dict(source='bad')):
            with self.assertRaises(ValueError):
                self.identity(**kwargs)

    def test_absent_config_identity_and_unsupported_config(self):
        baseline = self.identity()['build_prefix']
        for name in ('rust/.cargo/config', 'rust/.cargo/config.toml', '.cargo/config', '.cargo/config.toml'):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('# Explicitly empty config\n')
            self.assertNotEqual(baseline, self.identity()['build_prefix'])
            path.write_text('[build]\nrustflags=["--cfg=surprise"]\n')
            with self.assertRaises(ValueError):
                self.identity()
            path.unlink()
            self.assertEqual(baseline, self.identity()['build_prefix'])
        (self.home / 'config.toml').write_text('# external config\n')
        with self.assertRaises(ValueError):
            self.identity()

    def test_required_inputs_must_exist(self):
        for name in (*cache.COMMON_SCAFFOLD_INPUTS, 'rust/Cargo.toml', 'rust/Cargo.lock', 'rust/rust-toolchain.toml'):
            path = self.root / name
            data = path.read_bytes()
            path.unlink()
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.identity()
            path.write_bytes(data)

    def test_documentation_is_not_a_compatibility_input(self):
        baseline = self.identity()['build_prefix']
        (self.root / 'rust/docs').mkdir()
        (self.root / 'rust/docs/CI_CD.ru.md').write_text('Measured evidence update\n')
        self.assertEqual(baseline, self.identity()['build_prefix'])

    def test_fail_closed_overrides(self):
        for name in ('RUSTFLAGS', 'CARGO_ENCODED_RUSTFLAGS', 'RUSTC', 'RUSTC_WRAPPER',
                     'RUSTC_WORKSPACE_WRAPPER', 'RUSTDOCFLAGS', 'CARGO_TARGET_DIR', 'RUSTUP_TOOLCHAIN',
                     'CARGO_BUILD_TARGET', 'CARGO_PROFILE_DEV_OPT_LEVEL', 'CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS',
                     'CC', 'CXX', 'AR', 'CFLAGS', 'CXXFLAGS', 'LDFLAGS', 'CL', '_CL_', 'LINK', 'CC_x86_64_pc_windows_msvc'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.identity(env=dict(self.env, **{name: 'override'}))

    def test_fail_closed_archive_resource_aliases(self):
        # Regression probes confirmed against locked cc and embed-resource readers.
        for name in ('ARFLAGS', 'HOST_ARFLAGS', 'TARGET_ARFLAGS',
                     'ARFLAGS_x86_64-pc-windows-msvc', 'ARFLAGS_x86_64_pc_windows_msvc',
                     'RC_x86_64-pc-windows-msvc', 'RC_x86_64_pc_windows_msvc'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.identity(env=dict(self.env, **{name: 'override'}))
        # embed-resource distinguishes an empty tool override from an absent one.
        with self.assertRaises(ValueError):
            self.identity(env=dict(self.env, RC_x86_64_pc_windows_msvc=''))

    def test_native_tool_and_flag_aliases(self):
        for family in cache.NATIVE_OVERRIDE_FAMILIES:
            for name in (family, 'HOST_' + family, 'TARGET_' + family,
                         family + '_x86_64-pc-windows-msvc', family + '_x86_64_pc_windows_msvc'):
                with self.subTest(name=name), self.assertRaises(ValueError):
                    self.identity(env=dict(self.env, **{name: 'override'}))
                with self.subTest(empty=name), self.assertRaises(ValueError):
                    self.identity(env=dict(self.env, **{name: ''}))
        for name in ('WindowsSdkDir', 'VCToolsInstallDir', 'INCLUDE', 'LIB', 'LIBPATH',
                     'CCACHE_DIR', 'RESOURCE_NAME', 'MY_ARFLAGS'):
            with self.subTest(nonmember=name):
                cache.reject_overrides({name: 'value'})


class ContractTests(unittest.TestCase):
    def setUp(self):
        self.workflow = (ROOT / '.github/workflows/rust-migration.yml').read_text()
        self.scaffold = (ROOT / 'rust/tools/Verify-Scaffold.ps1').read_text()
        self.inputs = (ROOT / 'rust/tools/Verify-ScaffoldInputs.ps1').read_text()

    def verify(self, workflow=None, scaffold=None, inputs=None):
        cache_contract.verify(workflow or self.workflow, scaffold or self.scaffold, inputs or self.inputs)

    def test_actual_contract(self):
        self.verify()

    def test_negative_cache_path_fallback_and_condition_mutations(self):
        for before, after in (
                ('path: rust/target/debug', 'path: rust/target'),
                ('path: rust/target/debug', 'path: rust/target/release'),
                ('/registry/src', ''),
                ('restore-keys: ${{ steps.cache-identity.outputs.build_prefix }}', 'restore-keys: ci3-debug-'),
                ('restore-keys: ${{ steps.cache-identity.outputs.build_prefix }}', 'restore-keys: |\n            ${{ steps.cache-identity.outputs.build_prefix }}\n            ci3-debug-'),
                ('key: ${{ steps.cache-identity.outputs.download_key }}', 'key: download-any-lock'),
                ('name: Verify source and skeleton\n', "name: Verify source and skeleton\n        if: steps.cargo-debug.outputs.cache-hit != 'true'\n"),
                ('name: Verify owned Host diagnostic window on Windows\n', 'name: Verify owned Host diagnostic window on Windows\n        continue-on-error: true\n'),
                ('actions/cache/restore@v5', 'actions/cache/restore@v4'),
                ('path: rust/target/debug', 'path: rust/target/debug\n          lookup-only: true'),
                ('if: success() && steps.cargo-debug', 'if: always() && steps.cargo-debug')):
            self.assertIn(before, self.workflow)
            with self.subTest(mutation=before), self.assertRaises(ValueError):
                self.verify(workflow=self.workflow.replace(before, after))

    def test_negative_every_workflow_command_and_flag(self):
        for line in self.workflow.splitlines():
            if line.strip().startswith(('cargo ', 'rustup ', 'rustc ', 'python ', 'npm ', 'node ', 'git ', 'run: ./tools/')):
                with self.subTest(command=line), self.assertRaises(ValueError):
                    self.verify(workflow=self.workflow.replace(line, line.replace(line.strip(), '# removed command'), 1))
        for flag in ('--locked', '--offline', '-- -D warnings'):
            with self.subTest(flag=flag), self.assertRaises(ValueError):
                self.verify(workflow=self.workflow.replace(flag, '', 1))

    def test_independent_counterparts_cannot_be_skipped_or_reordered(self):
        name = 'Verify independent MQTT and OPC UA counterparts on Windows'
        for replacement in ('# removed counterpart gate', name + "\n        if: success() && false"):
            with self.subTest(replacement=replacement), self.assertRaises(ValueError):
                self.verify(workflow=self.workflow.replace('name: ' + name, 'name: ' + replacement))
        before = 'Verify source and skeleton'
        with self.assertRaises(ValueError):
            self.verify(workflow=self.workflow.replace(before, 'TEMP').replace(name, before).replace('TEMP', name))
        with self.assertRaises(ValueError):
            self.verify(workflow=self.workflow.replace('name: Upload independent counterpart evidence\n        if: always()',
                                                       'name: Upload independent counterpart evidence\n        if: success()'))

    def test_release_measurements_are_mandatory(self):
        for before, after in (('uses: ./.github/workflows/rust-nplus1-measurements.yml', '# removed measurement job'),
                              ('  release-measurements:\n', '  release-measurements:\n    if: false\n')):
            with self.subTest(before=before), self.assertRaises(ValueError):
                self.verify(workflow=self.workflow.replace(before, after))

    def test_negative_scaffold_commands_and_common_guards(self):
        for text, keyword in ((self.scaffold, 'scaffold'), (self.inputs, 'inputs')):
            for line in text.splitlines():
                if any(command in line for command in ('& cargo ', '& python ', '& (Join-Path', 'cargo metadata', '$Package.edition')):
                    with self.subTest(owner=keyword, command=line), self.assertRaises(ValueError):
                        self.verify(**{keyword: text.replace(line, '# removed common check', 1)})

    def test_negative_exit_propagation_and_save_conditions(self):
        for text, keyword in ((self.workflow, 'workflow'), (self.scaffold, 'scaffold'), (self.inputs, 'inputs')):
            for index, line in enumerate(text.splitlines(keepends=True)):
                if '$LASTEXITCODE -ne 0' in line:
                    lines = text.splitlines(keepends=True)
                    lines[index] = line.replace('-ne 0', '-eq 999')
                    with self.subTest(owner=keyword, line=index), self.assertRaises(ValueError):
                        self.verify(**{keyword: ''.join(lines)})
        for mutation in ("if: success() || always() && steps.cargo-debug.outputs.cache-hit != 'true'",
                         "if: success() && steps.cargo-debug.outputs.cache-hit != 'true' || always()"):
            with self.assertRaises(ValueError):
                self.verify(workflow=self.workflow.replace("if: success() && steps.cargo-debug.outputs.cache-hit != 'true'", mutation))


if __name__ == '__main__':
    unittest.main()
