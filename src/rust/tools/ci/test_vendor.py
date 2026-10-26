"""Byte-exact vendor verification under Windows Git newline policies."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[4]
VENDOR = Path('src/rust/third-party/rust_opc')


class VendorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='icem-vendor-test-')
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.repo = self.base / 'repo'
        self.vendor = self.repo / VENDOR
        shutil.copytree(ROOT / VENDOR, self.vendor)
        # Environment config models Windows Git even on a Linux test runner.
        self.env = dict(os.environ, GIT_CONFIG_COUNT='3',
                        GIT_CONFIG_KEY_0='core.autocrlf', GIT_CONFIG_VALUE_0='true',
                        GIT_CONFIG_KEY_1='core.eol', GIT_CONFIG_VALUE_1='crlf',
                        GIT_CONFIG_KEY_2='core.attributesFile',
                        GIT_CONFIG_VALUE_2=str(self.base / 'global-attributes'))
        (self.base / 'global-attributes').write_bytes(b'* text eol=crlf\n')

    def git(self, *args, cwd=None):
        return subprocess.run(['git', *args], cwd=cwd or self.repo, env=self.env,
                              check=True, capture_output=True)

    def verify(self):
        return subprocess.run([sys.executable, str(self.vendor / 'verify.py')],
                              cwd=self.repo, env=self.env, capture_output=True, text=True)

    def test_checkout_and_reconstruction_preserve_exact_bytes(self):
        (self.repo / '.gitattributes').write_bytes(b'* text=auto\n')
        self.git('init', '-q')
        self.git('add', '.')
        before = {p.relative_to(self.vendor): p.read_bytes()
                  for p in self.vendor.rglob('*') if p.is_file()}
        # Force a real checkout; the nested -text policy must preserve every file.
        for path in before:
            (self.vendor / path).unlink()
        self.git('checkout-index', '--all', '--force')
        self.assertEqual(before, {p.relative_to(self.vendor): p.read_bytes()
                                  for p in self.vendor.rglob('*') if p.is_file()})

        # Prove the original failure with every current patch input, including
        # reviewed SDK additions. Omit checkout attributes deliberately so plain
        # git apply still demonstrates the original CRLF reconstruction defect.
        originals = json.loads((self.vendor / 'upstream-files.sha256.json').read_text())
        scratch = self.base / 'plain-reconstruction'
        shutil.copytree(self.vendor, scratch, ignore=shutil.ignore_patterns('.gitattributes'))
        self.git('apply', '--reverse', str(self.vendor / 'icem.patch'), cwd=scratch)
        reconstructed = (scratch / 'Cargo.toml').read_bytes()
        self.assertIn(b'\r\n', reconstructed)
        self.assertNotEqual(hashlib.sha256(reconstructed).hexdigest(), originals['Cargo.toml'])

        result = self.verify()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('42 exact upstream inputs', result.stdout)
        self.assertEqual(before, {p.relative_to(self.vendor): p.read_bytes()
                                  for p in self.vendor.rglob('*') if p.is_file()})

    def test_mutations_still_fail_closed(self):
        for name in ('Cargo.toml', 'icem.patch', 'upstream-files.sha256.json',
                     'opc_da_bindings/src/server/callback_queue.rs'):
            with self.subTest(name=name):
                path = self.vendor / name
                original = path.read_bytes()
                if name == 'upstream-files.sha256.json':
                    manifest = json.loads(original)
                    manifest['Cargo.toml'] = '0' * 64
                    path.write_text(json.dumps(manifest), encoding='utf-8')
                elif name == 'icem.patch':
                    path.write_bytes(original.replace(b'[workspace]', b'[workspacX]'))
                else:
                    path.write_bytes(original + b'\n# unreviewed mutation\n')
                try:
                    self.assertNotEqual(path.read_bytes(), original)
                    result = self.verify()
                    self.assertNotEqual(result.returncode, 0, result.stdout)
                finally:
                    path.write_bytes(original)


if __name__ == '__main__':
    unittest.main()
