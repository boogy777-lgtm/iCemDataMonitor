# Reviewed P7c1 SDK extension

Source: https://github.com/Ronbb/rust_opc
Exact upstream commit: `da44a99265f9706618bbe506c98f714f2aa46f0b`.

This is an external dependency workspace, excluded from the application workspace.
It does not inherit or weaken application `forbid(unsafe_code)`. Only the five
crates required by the DA service are selected: DA, Common, shared ABI, owned
types and utilities. The 42 upstream inputs total 415895 bytes: their Cargo
manifests, source, build scripts, available READMEs, two winmd inputs, LICENSE
and NOTICE. Other protocols, examples, integration fixtures, CI and IDL sources
are not build inputs and are omitted. The original root manifest is reduced to
these members/dependencies. No crate version, optional feature or registry
dependency is added. MIT applies to Rust code; NOTICE preserves the separate
OPC Foundation metadata terms, whose distribution acceptance remains open.
Upstream declares MSRV 1.97/edition 2024. The patched selection declares and is
tested with exact Rust 1.99.1; it makes no compatibility claim for older versions.

`upstream-files.sha256.json` identifies every selected exact upstream input.
`icem.patch` is the complete unified delta against those files. Run
`python third-party/rust_opc/verify.py` from `rust/` to reverse the patch in a
temporary directory and verify every original SHA256, without an upstream
checkout, network, changed CARGO_HOME or git-cache surgery. Extra files and
symlinks fail the check. Local .gitattributes disables newline conversion for
byte-identical provenance on Windows. Regenerate the delta only against the exact upstream
revision and review it together with the manifest.

The delta adds a mandatory owned `GroupState` argument to `DaGroupService::read`.
The adapter snapshots its existing private mutex state once, releases the lock,
then invokes the service through a `forbid(unsafe_code)` helper. Existing SetState
ABI code, validation, output initialization and commit order are unchanged.
Read linearizes at the state snapshot: overlapping SetState may commit before
Read finishes, but Read receives either complete old or complete new fields.
No application callback runs under the SDK state lock. Initial AddGroup state,
SetName and all committed SetState fields use the same snapshot. A missing read
implementation update is a compile error, rather than silently ignoring state.

The generator still consumes the same winmd with locked windows-bindgen 0.66.0,
including the upstream shallow-VARIANT-clone removal. Output and a module wrapper
are written to OUT_DIR, preserving generated module attributes and leaving all
tracked bindings/source unchanged. The tracked bindings remain byte-exact
reference inputs. Three deprecated `fetch_update` calls become equivalent stable
`try_update` calls for warning-free Rust 1.99.1. No unsafe operation, ABI, unsafe
allowance or new native exception is introduced; existing external ABI code is
retained byte-for-byte outside these safe changes.

New SDK tests are `forbid(unsafe_code)`: direct calls to the existing generated
SetState implementation entrypoint with owned arguments, followed by actual safe
COM client Read/GetState, verify state forwarding, rate/client-handle coherence,
failed output-pointer ordering and concurrent state snapshots. These are not
external COM SetState calls: the pinned safe client lacks that method, and adding
it would require a new native operation. Windows runtime must execute
`cargo test -p opc_da_bindings -p icem-opc-da-sidecar --locked --offline` from
the application workspace. Cross-process SetState and callbacks remain separate
Windows acceptance gates. P7c2 callbacks are not implemented here.


P7c2 draft checkpoint (ADR0019): explicitly ours server/callbacks.rs implements
group CPC/CP/AsyncIO2 and GIT-owned bounded dispatch, with safe application25ms
health watchdog. Pure callback_queue.rs/callback_health.rs are owned safe engines.
New files are explicitly allowlisted and included in complete reversible patch.
No application unsafe/policy/dependency changes. Native independent review, client
acceptance implementation and Windows runtime are pending; full P7 remains PARTIAL.
