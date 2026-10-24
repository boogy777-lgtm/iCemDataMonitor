"""Bounded Windows Cargo cache identity. No cache changes which checks execute."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tomllib


# Hash the actual command owners, including their common pre-restore guards.
COMMON_SCAFFOLD_INPUTS = (
    '.github/workflows/rust-migration.yml',
    'rust/tools/Verify-Scaffold.ps1',
    'rust/tools/Verify-ScaffoldInputs.ps1',
    'rust/tools/Test-BaselineGuard.ps1',
    'rust/tools/Test-WindowsPackaging.ps1',
    'rust/tools/Verify-Baseline.ps1',
    'rust/tools/Test-DesktopMappingGuard.ps1',
    'rust/tools/Verify-DesktopMapping.ps1',
    'rust/tools/ci/cache_identity.py',
    'rust/tools/ci/cache_contract.py',
    'rust/third-party/rust_opc/verify.py',
    'rust/third-party/rust_opc/upstream-files.sha256.json',
)
DOWNLOAD_PATHS = ('registry/index', 'registry/cache', 'registry/src', 'git/db', 'git/checkouts')
BUILD_PATH = 'rust/target/debug'
NATIVE_OVERRIDE_FAMILIES = frozenset({
    'CC', 'CXX', 'AR', 'RANLIB', 'RC', 'CFLAGS', 'CXXFLAGS', 'ARFLAGS',
    'RANLIBFLAGS', 'CPPFLAGS', 'LDFLAGS', 'CC_MASM_ASM', 'CXXSTDLIB', 'CXXSTDLIB_STATIC',
})


def reject_overrides(env):
    allowed = {'CARGO_HOME', 'CARGO_TERM_COLOR', 'RUSTUP_HOME'}
    native = {'CL', '_CL_', 'LINK', '_LINK_', 'CMAKE_TOOLCHAIN_FILE',
              'CRATE_CC_NO_DEFAULTS', 'CC_FORCE_DISABLE', 'CC_SHELL_ESCAPED_FLAGS', 'CROSS_COMPILE'}
    for name, value in env.items():
        native_alias = any(name in (family, 'HOST_' + family, 'TARGET_' + family)
                           or name.startswith(family + '_') for family in NATIVE_OVERRIDE_FAMILIES)
        if native_alias or (value and ((name.startswith(('CARGO_', 'RUST')) and name not in allowed)
                                      or name in native)):
            raise ValueError(f'Unsupported compiler/Cargo override: {name}')


def digest_inputs(root, cargo_home):
    paths = set(COMMON_SCAFFOLD_INPUTS)
    paths.update(('rust/Cargo.toml', 'rust/Cargo.lock', 'rust/rust-toolchain.toml'))
    for directory, children, files in os.walk(root / 'rust'):
        children[:] = sorted(name for name in children if name not in ('target', 'node_modules', '.git', 'dist'))
        if 'Cargo.toml' in files:
            paths.add((Path(directory) / 'Cargo.toml').relative_to(root).as_posix())
    # Cargo discovers config in the invocation directory and every ancestor.
    configs = [parent / '.cargo' / name for parent in (root / 'rust', root, *root.parents)
               for name in ('config', 'config.toml')]
    configs.extend(cargo_home / name for name in ('config', 'config.toml'))
    for path in configs:
        if path.is_file() and not path.is_relative_to(root):
            raise ValueError('External Cargo config is unsupported in this CI cache')
        if path.is_relative_to(root):
            paths.add(path.relative_to(root).as_posix())
        if path.is_file() and tomllib.loads(path.read_text(encoding='utf-8')):
            raise ValueError('Cargo configuration overrides are unsupported in this CI cache')
    records = []
    for name in sorted(paths):
        path = root / name
        if path.is_symlink():
            raise ValueError(f'Symlink cache input is unsupported: {name}')
        if not path.is_file() and '/.cargo/' not in name and not name.startswith('.cargo/'):
            raise ValueError(f'Required cache input is missing: {name}')
        records.append((name, hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else 'ABSENT'))
    return hashlib.sha256(json.dumps(records, separators=(',', ':')).encode()).hexdigest()


def identity(root, cargo_home, env, compiler, cargo, source):
    reject_overrides(env)
    if not re.fullmatch(r'[0-9a-f]{40}', source):
        raise ValueError('Expected exact source SHA')
    inputs_digest = digest_inputs(root, cargo_home)
    pin = re.search(r'(?m)^channel\s*=\s*"(\d+\.\d+\.\d+)"',
                    (root / 'rust/rust-toolchain.toml').read_text())
    if not pin or not compiler.startswith('rustc ' + pin[1] + ' '):
        raise ValueError('Compiler differs from exact stable pin')
    host = re.search(r'(?m)^host: (.+)$', compiler)
    if not host or host[1] != 'x86_64-pc-windows-msvc' or env.get('RUNNER_OS') != 'Windows' or env.get('RUNNER_ARCH') != 'X64':
        raise ValueError('This cache supports Windows runners only')
    runner = {name: env.get(name, '') for name in ('RUNNER_OS', 'RUNNER_ARCH', 'ImageOS', 'ImageVersion')}
    if not all(runner.values()):
        raise ValueError('Runner image/architecture identity is incomplete')
    native_env = {name: env.get(name, '') for name in
                  ('WindowsSdkDir', 'WindowsSDKVersion', 'WindowsSDKLibVersion', 'WindowsSdkBinPath',
                   'WindowsSdkVerBinPath', 'VCToolsInstallDir', 'VCToolsVersion', 'VCINSTALLDIR',
                   'VSINSTALLDIR', 'VSCMD_VER', 'VSCMD_ARG_TGT_ARCH', 'VSCMD_ARG_HOST_ARCH',
                   'INCLUDE', 'LIB', 'LIBPATH', 'SDKROOT', 'UCRTVersion', 'UniversalCRTSdkDir')}
    facts = dict(runner, compiler=compiler, cargo=cargo, host=host[1], pin=pin[1],
                 inputs=inputs_digest,
                 native_environment=hashlib.sha256(json.dumps(native_env, sort_keys=True).encode()).hexdigest())
    compat = hashlib.sha256(json.dumps(facts, sort_keys=True).encode()).hexdigest()
    prefix = f'ci3-debug-{compat}-'
    return dict(facts, download_key=f'ci3-download-{compat}', build_key=prefix + source,
                build_prefix=prefix, source=source)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[3]
    cargo_home = Path(os.environ.get('CARGO_HOME', str(Path.home() / '.cargo'))).resolve()
    def version(command):
        return subprocess.check_output(command, cwd=root / 'rust', text=True).strip()
    result = identity(root, cargo_home, os.environ, version(['rustc', '--version', '--verbose']),
                      version(['cargo', '--version']), os.environ.get('GITHUB_SHA', ''))
    Path(args.output).write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        for key in ('download_key', 'build_key', 'build_prefix'):
            output.write(f'{key}={result[key]}\n')
        output.write(f'cargo_home={cargo_home.as_posix()}\n')


if __name__ == '__main__':
    main()
