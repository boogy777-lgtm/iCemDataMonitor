# Fixtures

`bus-v5-minimal.bin` is synthetic, not a captured production file. Its 1,364 bytes
are layout **5**, count 650 (not the legacy layout-2 file of the same length).
Header: `STNB`, protocol=1, layout=5, N=650; COM tick=638000000000000000,
flag=1, count=42; LAN flag=0, RAW flag=1. Register 0=7, reg 3=32000.
Pairs: 4=1700000000 signed Unix seconds; 6=IEEE 1.0; 8=negative zero;
10=NaN bits 0x7fc01234. Header fields LE, body u16 BE, CDAB low word first.
The Rust test checks fixed independent wire values and every truncated prefix.
It is not a substitute for .NET differential captures or Windows MMF tests.
