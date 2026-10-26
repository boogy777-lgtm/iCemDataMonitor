# Fixtures

`bus-v5-minimal.bin` is synthetic, not a captured production file. Its 1,364 bytes
are layout **5**, count 650 (not the legacy layout-2 file of the same length).
Header: `STNB`, protocol=1, layout=5, N=650; COM tick=638000000000000000,
flag=1, count=42; LAN flag=0, RAW flag=1. Register 0=7, reg 3=32000.
Pairs: 4=1700000000 signed Unix seconds; 6=IEEE 1.0; 8=negative zero;
10=NaN bits 0x7fc01234. Header fields LE, body u16 BE, CDAB low word first.
The Rust test checks fixed independent wire values and every truncated prefix.
It is not a substitute for .NET differential captures or Windows MMF tests.


`emulator-v1-e19-golden.json`: 43 actual Rust E19 Engine frames (all 21 commands),
fixed mock emitters and explicit clock 123.25 s. Full immutable production head/tree
and capture worktree commit are embedded in provenance. Temporary capture harness
was removed; only status.data.pid is normalized to 0. Runtime test replays the same
ordered lines and compares full parsed envelope/data/ACK; backend_unknown_vars
separately freezes direct-backend unknown-scope success with data:{} (the external
parser correctly rejects unknown vars scope before calling the backend). This protects existing
Rust v1 projection independently of the synthetic C# parser backend fixture; it is
not a legacy Engine or physical hardware parity claim.
