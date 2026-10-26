# CI-only OPC DA RPC fixtures

Exact source: https://github.com/Ronbb/rust_opc at
`da44a99265f9706618bbe506c98f714f2aa46f0b`. `upstream.json` records byte sizes,
paths and SHA256 for unmodified IDL. These are separate test inputs, not additions
to the 42 runtime SDK inputs. `.gitattributes` preserves their original bytes,
including non-UTF8 comment characters. All original notices remain intact.

OPC Foundation copyright and metadata terms in these files apply separately
from the upstream Rust MIT license; see `../../third-party/rust_opc/NOTICE`.
The DA3 specification License Grant (p2) is at
https://advosol.com/opcspecs/opcda3.00.pdf . No legal certification, general
redistribution acceptance or full DA3 compliance is asserted.

`tools/Test-OpcDaInterop.ps1` builds a matching Windows x64 proxy from these IDLs
using installed Windows SDK MIDL and MSVC, with a private proxy-factory CLSID.
It runs only on ephemeral Actions runners, refuses existing OPC mappings and
cleans up its own mappings even on failure. Artifacts stay under RUNNER_TEMP,
never installer payload. See ADR0019 for the native test boundary and limits.
