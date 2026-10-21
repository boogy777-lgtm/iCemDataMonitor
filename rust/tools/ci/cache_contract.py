"""Executable contract for the single Windows Rust job's bounded caches."""
from pathlib import Path
import re

from cache_identity import BUILD_PATH, DOWNLOAD_PATHS, COMMON_SCAFFOLD_INPUTS


def require(condition, message):
    if not condition:
        raise ValueError(message)


def verify(workflow, scaffold, inputs):
    require('continue-on-error:' not in workflow, 'Failures must propagate')
    steps = re.split(r'(?m)^      - ', workflow)[1:]
    names = [step.splitlines()[0].removeprefix('name: ') for step in steps]
    require(workflow.count('runs-on: windows-latest') == 1 and workflow.count('\n  verify:') == 1,
            'Keep the existing single Windows job')
    def step(name):
        require(names.count(name) == 1, f'Missing/duplicate step: {name}')
        return steps[names.index(name)]
    order = ('Install pinned Rust toolchain', 'Verify cache contracts and derive identity',
             'Restore Cargo downloads', 'Fetch locked Rust dependencies',
             'Verify all scaffold inputs before debug restore', 'Restore Cargo debug build',
             'Verify owned Host diagnostic window on Windows',
             'Verify Windows file logger boundary before desktop build',
             'Verify Windows emulator integration before desktop build',
             'Verify source and skeleton', 'Check preserved dashboard in Chrome')
    positions = [names.index(name) if name in names else -1 for name in order]
    require(-1 not in positions and positions == sorted(positions), 'Cache/check ordering drift')
    python_step = step('Set up Python for cache contracts')
    require("uses: actions/setup-python@v5\n        with:\n          python-version: '3.12'\n" in python_step,
            'Cache tooling requires explicit stdlib tomllib support')
    require(names.index('Set up Python for cache contracts') < names.index(order[1]), 'Install Python before cache tooling')
    commands = {
        order[0]: ('rustup show', 'rustc --version --verbose', 'cargo --version'),
        order[1]: ('python tools/ci/cache_contract.py', 'python tools/ci/cache_identity.py --output "$env:RUNNER_TEMP/icem-cache-identity.json"'),
        order[3]: ('cargo fetch --locked',),
        order[4]: ('./tools/Verify-ScaffoldInputs.ps1 -RequireCompleteBaseline',),
        order[6]: ('cargo test --locked --offline -p icem-windows-native',),
        order[7]: ('cargo test --locked --offline -p icem-logging',),
        order[8]: ('cargo test --locked --offline -p icem-emulator-app -p icem-transport -p icem-platform-windows',
                   'cargo clippy --locked --offline --all-targets -p icem-emulator-app -p icem-transport -p icem-platform-windows -- -D warnings'),
        order[9]: ('./tools/Verify-Scaffold.ps1 -RequireCompleteBaseline',),
        order[10]: ('npm ci --prefix tools/browser --ignore-scripts --no-audit --no-fund',
                    'node tools/browser/theme-contract.cjs', 'node tools/browser/dashboard.cjs'),
    }
    for name, expected in commands.items():
        block = step(name)
        require(not re.search(r'(?m)^        (if|continue-on-error):', block), 'Checks must execute regardless of cache hit')
        lines = [line.strip().removeprefix('run: ') for line in block.splitlines()]
        actual = tuple(line for line in lines
                       if line.startswith(('cargo ', 'rustup ', 'rustc ', 'python ', './tools/', 'npm ', 'node ')))
        require(actual == expected, f'Command contract drift: {name}')
        for command in expected:
            if not command.startswith('./tools/'):
                require(command + '\n          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\n' in block,
                        f'Command must propagate its immediate exit code: {command}')
    require(workflow.count('cargo fetch ') == 1, 'Fetch exactly once')
    require(workflow.count('uses: actions/cache/') == 4, 'Unexpected cache action')
    for name, kind, action in (
            ('Restore Cargo downloads', 'download', 'restore'),
            ('Restore Cargo debug build', 'build', 'restore'),
            ('Save Cargo downloads after all checks', 'download', 'save'),
            ('Save Cargo debug build after all checks', 'build', 'save')):
        block = step(name)
        require(f'uses: actions/cache/{action}@v5' in block, 'Expected cache v5 restore/save')
        with_block = block.split('        with:\n', 1)[1]
        path_block = with_block.split('          path: ', 1)[1].split('          key:', 1)[0]
        expected_paths = ('|\n' + ''.join('            ${{ steps.cache-identity.outputs.cargo_home }}/' + path + '\n'
                                          for path in DOWNLOAD_PATHS)) if kind == 'download' else BUILD_PATH + '\n'
        require(path_block == expected_paths, 'Cache path allowlist drift')
        expected_with = '          path: ' + expected_paths + '          key: ${{ steps.cache-identity.outputs.' + kind + '_key }}\n'
        if kind == 'build' and action == 'restore':
            expected_with += '          restore-keys: ${{ steps.cache-identity.outputs.build_prefix }}\n'
        require(with_block == expected_with, 'Cache options/fallback must match the exact bounded contract')
        require('          key: ${{ steps.cache-identity.outputs.' + kind + '_key }}\n' in block, 'Cache key drift')
        fallback = re.findall(r'(?m)^          restore-keys: (.+)$', block)
        require(fallback == (['${{ steps.cache-identity.outputs.build_prefix }}'] if kind == 'build' and action == 'restore' else []),
                'Only one compatible debug restore prefix is allowed')
        conditions = re.findall(r'(?m)^        if: (.+)$', block)
        cache_id = 'cargo-downloads' if kind == 'download' else 'cargo-debug'
        require(conditions == ([] if action == 'restore' else
                [f"success() && steps.{cache_id}.outputs.cache-hit != 'true'"]), 'Cache restore/save policy drift')
    require("& (Join-Path $PSScriptRoot 'Verify-ScaffoldInputs.ps1') -RequireCompleteBaseline:$RequireCompleteBaseline" in scaffold,
            'Full Scaffold must repeat every input guard')
    expected_cargo = ('& cargo check --workspace --all-targets --locked --offline',
                      '& cargo build --workspace --locked --offline',
                      '& cargo test --workspace --locked --offline',
                      '& cargo clippy --workspace --all-targets --locked --offline -- -D warnings')
    require(tuple(line.strip() for line in scaffold.splitlines() if line.strip().startswith('& cargo ')) == expected_cargo,
            'Full Scaffold commands drift')
    for command, failure in zip(expected_cargo, ('cargo check failed', 'cargo build failed', 'cargo tests failed', 'clippy failed')):
        require(command + "\n    if ($LASTEXITCODE -ne 0) { throw '" + failure + "' }\n" in scaffold,
                'Full Scaffold must propagate immediate failure')
    expected_guards = ("& (Join-Path $PSScriptRoot 'Test-BaselineGuard.ps1')",
                       "& (Join-Path $PSScriptRoot 'Test-WindowsPackaging.ps1')",
                       "& (Join-Path $PSScriptRoot 'Verify-Baseline.ps1') -RequireComplete:$RequireCompleteBaseline",
                       "& (Join-Path $PSScriptRoot 'Test-DesktopMappingGuard.ps1')",
                       "& (Join-Path $PSScriptRoot 'Verify-DesktopMapping.ps1')")
    require(tuple(line.strip() for line in inputs.splitlines() if line.strip().startswith('& (Join-Path')) == expected_guards,
            'Common scaffold guard drift')
    for command in ('cargo metadata --no-deps --format-version 1 --locked --offline', '& cargo fmt --all --check',
                    "$Package.edition -ne '2024'", '$Package.rust_version -ne $PinnedVersion'):
        require(command in inputs, f'Lost common scaffold policy: {command}')
    require("    if ($LASTEXITCODE -ne 0) { throw 'cargo metadata failed' }" in inputs,
            'Metadata failures must fail the guards')
    require("    if ($LASTEXITCODE -ne 0) { throw 'rustfmt failed' }" in inputs,
            'Formatting failures must fail the guards')
    for text in (scaffold, inputs):
        require(not re.search(r'(?im)^\s*if\s*\([^\n]*(cache|hit)', text), 'No conditional checks')
    for name in ('Upload dashboard evidence', 'Upload emulator differential evidence', 'Record bounded cache evidence', 'Upload Cargo cache evidence'):
        require('        if: always()\n' in step(name), 'Evidence must survive failure')
    for owner in re.findall(r"Join-Path \$PSScriptRoot '([^']+)'", scaffold + inputs):
        require('rust/tools/' + owner in COMMON_SCAFFOLD_INPUTS,
                'Every common scaffold command owner must affect cache identity')


def main():
    root = Path(__file__).resolve().parents[3]
    verify((root / '.github/workflows/rust-migration.yml').read_text(),
           (root / 'rust/tools/Verify-Scaffold.ps1').read_text(),
           (root / 'rust/tools/Verify-ScaffoldInputs.ps1').read_text())
    print('CI-3 cache paths, commands, guards and ordering: PASS')


if __name__ == '__main__':
    main()
