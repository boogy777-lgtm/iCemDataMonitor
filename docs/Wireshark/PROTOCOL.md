# ACE Protocol Specification — Definitive Wire Format

> **Protocol ID:** ACE (Advanced Cementing Equipment) — Halliburton proprietary
> **Platform:** NI cRIO (National Instruments CompactRIO) + LabVIEW RT
> **Classification:** Degenerate LLC I-frame — connectionless broadcast, connection-oriented syntax
> **Lineage:** GE Fanuc raw 802.3 pattern → NI LabVIEW raw Ethernet palette → Halliburton ACE
> **Date:** 2026-08-09
> **Updated:** 2026-08-17 — offset law `payload_index = ByteOffset − 4` fixed from real dump
> `iCemCrio.pcapng.bin` (proofs + golden tables); burst-pacing (5.25 ms, start 0.938) from
> `out\iCemCrio_pacing.txt`; golden verify script `golden_verify.ps1`; default unit preset LanNative.

> **Reader contract.** This document is the *single source of truth* for how a 152-byte ACE LAN
> frame is built. Every constant, offset and golden byte below is backed by ONE of:
> (a) the emulator source (`Emitters\AceFrameBuilder.cs`, `Core\AcePayloadProvider.cs`, …),
> (b) the contract `specs\CONTRACT_FlecsEmu.yaml` (section `ace_wire`),
> (c) the real cRIO capture `Wireshark\iCemCrio.pcapng.bin` and its derived files in `Wireshark\out\`.
> Where a value could not be re-verified from a file, it is marked `[проверить: …]`.

> **Canonical (2026-08-21, owner decree).** THIS file is the single source of truth for the ACE
> wire format (root `PROTOCOL.md` retired to a pointer stub — this variant is the superset:
> golden proofs §8–§9, pacing §9.3, units §12). Wire constants are backed by the emitter code:
> `iCemEmule.Core\Emitters\AceFrameBuilder.cs` / `AceLanEmitter.cs` (canonical emulator home;
> the `FlecsEmu\…` paths cited below are the project-era form — a verbatim copy lives under
> `Transfer\FlecsEmu\`). Divergence between this file and the emitter code = defect → ticket.

---

## 0. What This Protocol Is NOT (Definitive Exclusions)

### 0.1 GE SRTP — Excluded by Byte-Level Analysis

GE SRTP (Service Request Transport Protocol) — proprietary GE Fanuc PLC communication protocol, reverse-engineered by Collin Matthews (GitHub: TheMadHatt3r/ge-ethernet-SRTP, 2020).

```
GE SRTP:                          ACE Protocol:
  TCP port 18245                    Raw 802.3 LLC
  56-byte header                    14-byte header
  INIT_MSG = 56×0x00 → 0x01        No handshake
  Type 0x02 (Transmit) / 0x03       No type field
  SeqNum at offset 2                No sequence numbers
  0x01 at offsets 9, 17             No 0x01 constants
  0xC0 at offset 31                 No 0xC0 anywhere
  Mailbox Src/Dst (8 bytes)         Equipment ID (4 bytes)
  Service Request + Memory Type     Not present
  Time sec/min/hr at 26-28          Not present
  Fragmentation (pkt num/total)     Fixed 152-byte frames
  Client-server, point-to-point     Producer-consumer, multicast
```

**Verdict:** ∀ alignment ∈ [0,56] bytes, ∄ 3+ consecutive bytes where GE_SRTP ≡ ACE. Zero structural DNA. Protocols are as unrelated as HTTP is to ARP.

### 0.2 Other Exclusions

| Protocol | Falsified By |
|---|---|
| GE SRTP | TCP, 56-byte header, 0xC0@31, client-server model |
| GE EGD | UDP:18246, EGD header structure |
| EtherNet/IP | SAP 0xAA (SNAP), CIP encapsulation |
| PROFINET RT | EtherType 0x8892, no LLC |
| NI-PSP | TCP:2343, SVE-managed |
| Modbus TCP | TCP:502, MBAP header |
| BACnet | SAP 0x82 |

---

## 1. Frame Envelope (L2 Wire Format) — Byte-Level Spec, 152 Bytes

### 1.1 Byte Layout

```
Offset   Size  Field               Value (hex)          Meaning / Rule                             Source
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
0x00     6     Dst MAC             03:00:01:00:00:20    L2 multicast, locally administered        AceFrameBuilder.DstMacBytes
0x06     6     Src MAC             00:80:2F:23:F4:44    Real cRIO (OUI 00:80:2F = NI).            dump / CONTRACT src_mac_default
                                                        Emulator: MAC of opened adapter (or       (configurable in settings)
                                                        user-provided srcMac in SendSuperframe)
0x0C     2     Length              0x008A = 138         IEEE 802.3 — NOT EtherType (≤ 0x05DC)     AceFrameBuilder.IeeeLength
0x0E     1     LLC DSAP            0x03                 Group LSAP (I/G=1, addr=1)                AceFrameBuilder.LlcDsap
0x0F     1     LLC SSAP            0x03                 Resp frame (C/R=1, addr=1)                AceFrameBuilder.LlcSsap
0x10     1     LLC Control         0x08                 I-frame: N(S)=4, N(R)=0, P/F=0.          AceFrameBuilder.LlcControlI
                                                        Degenerate (constant, no increment).
                                                        Control 0x09 (S-frame RR) accepted by
                                                        parser, NEVER observed (§2.4).
0x11     1     Magic[0]            0x00                 Filter constant — offsets 17..19 =         AceFrameBuilder.Magic0..2
0x12     1     Magic[1]            0xD0                  00 D0 0D (iCem filter: frame[17]==0x00,
0x13     1     Magic[2]            0x0D                  frame[18]==0xD0, frame[19]==0x0D)
0x14     4     Equipment Number    uint32 BE            Producer ID. Real dump: 0x00A86F46        AceFrameBuilder.EquipmentOffset
                                                        (=11038534). Emulator: configurable.
0x18     4     Service Vector      uint32 BE            Data group ID (exchange). Occupies         AceFrameBuilder.SvOffset
                                                        frame slots 0..3 of the historic
                                                        "signal area" 0x18..0xA0. Hence all
                                                        lantable signals have ByteOffset ≥ 4.
0x1C     124   Signal Payload      —                    payload[0..123] = frame 0x1C..0xA0         AceFrameBuilder.PayloadOffset
                                                        (28..151). Decoded per lantable            (PayloadSize = 124)
                                                        registry, offset law §8:
                                                        payload_index = ByteOffset − 4.
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
TOTAL    152   (14 Eth + 3 LLC + 3 Magic + 4 Equipment + 4 SV + 124 Payload)
```

> **Layout check (byte math).** 6+6 (MAC) + 2 (Length) + 3 (LLC) + 3 (Magic) + 4 (Equipment) + 4 (SV) + 124 (Payload) = 152. The IEEE 802.3 Length field (0x008A = 138) counts LLC PDU only: 138 = 3 (LLC) + 3 (Magic) + 4 (Equipment) + 4 (SV) + 124 (Payload).

### 1.2 Raw Hex Sample (First ACE Frame, iCemCrio.pcapng.bin — annotated)

```
03 00 01 00 00 20    ← dst  (multicast group)
00 80 2F 23 F4 44    ← src  (NI cRIO)
00 8A                ← IEEE 802.3 length = 138
03 03 08             ← DSAP=0x03, SSAP=0x03, Ctrl=0x08
00 D0 0D             ← Magic [0x00] [0xD00D]  (offsets 17..19)
00 A8 6F 46          ← Equipment = 11038534
13 12 02 03          ← Sv = 0x13120203 (Lnd Automation)
00 A8 6F 46          ← payload[0..3]  = Unit Number (ByteOffset 4)  → 11038534
00 00 00 01          ← payload[4..7]  = Control Mode (ByteOffset 8) → 1
00 00 00 02          ← payload[8..11] = Mix Mode (ByteOffset 12)    → 2
3B 74 10 5C          ← payload[12..15]= rho12_hat (ByteOffset 16)   → 0.00372
...                    (120 more bytes — see §9.2 for full decode)
```

The full 152-byte first frame is given verbatim in §9.1 (GOLDEN FRAME #1).

### 1.3 Building a Frame from Constants (Emulator Path)

`AceFrameBuilder.BuildFrame(srcMac, equipmentNumber, svNumber, payload124)` (`Emitters\AceFrameBuilder.cs:40`):

1. zero-fill a `byte[152]` (all unused bytes stay 0x00);
2. copy Dst MAC `03 00 01 00 00 20` → bytes 0..5;
3. copy Src MAC → bytes 6..11;
4. write Length `00 8A` → bytes 12..13 (big-endian 0x008A);
5. write LLC `03 03 08` → bytes 14..16;
6. write Magic `00 D0 0D` → bytes 17..19;
7. write Equipment uint32 BE → bytes 20..23 (`frame[0x14]=(b>>24)&FF` … `frame[0x17]=b&FF`);
8. write SV uint32 BE → bytes 24..27;
9. copy up to 124 payload bytes → bytes 28..151 (shorter ⇒ zero-padded, longer ⇒ trimmed).

`TryParseFrame` (`AceFrameBuilder.cs:91`) enforces the reverse invariants: length ≥ 152, dst MAC exact, magic exact at 17..19, LLC DSAP/SSAP exact and Control ∈ {0x08, 0x09}.

### 1.4 Frame Invariants (Check-list for LLM)

| # | Invariant | Golden value | Verified on dump | Test |
|---|---|---|---|---|
| F1 | Frame total length | 152 bytes | 6978/6978 (100%) | T3 |
| F2 | Dst MAC | 03:00:01:00:00:20 | 6978/6978 | T2/T3 |
| F3 | IEEE 802.3 Length | 0x008A | 6978/6978 | T3 |
| F4 | Magic @17..19 | 00 D0 0D | 6978/6978 | T2/T3 |
| F5 | Payload length | 124 bytes | 6978/6978 | T3 |
| F6 | Equipment | 0x00A86F46 on ALL frames | exactly 1 value in dump | T4 (historic) / T3 |
| F7 | Src MAC | 00:80:2F:23:F4:44 (real cRIO) | exactly 1 value in dump | — |

---

## 2. LLC Control Byte: 0x08 is an I-Frame (Critical Correction)

### 2.1 Previous Assumption (Wrong)

```
Control = 0x08 → UI frame, connectionless, Type 1 operation ✗
```

### 2.2 Actual Decoding (Correct)

```
Control = 0x08  →  binary 0000 1000

  bit 0   = 0   →  I-format (Information frame — NOT U-format)
  bits 1-3 = 4  →  N(S) = 4  (send sequence number, 3-bit, mod 8)
  bit 4   = 0   →  P/F = 0   (poll/final bit)
  bits 5-7 = 0  →  N(R) = 0  (receive sequence number, mod 8)
```

**This is an I-frame (LLC Type 2 connection-oriented syntax), NOT a UI frame (LLC Type 1 connectionless).**

### 2.3 Basic Mode — Proven by Byte Math

```
802.3 Length = 0x008A = 138 bytes (LLC PDU after Length)
Basic mode  (1-byte ctrl):  138 − 3 = 135 = 3(magic) + 4(eq) + 4(sv) + 124(payload) ✓
Extended mode (2-byte ctrl): 138 − 4 = 134 ≠ 135 ✗
```

**Conclusion: Basic mode, 3-bit sequence numbers. Extended mode (SABME) refuted by payload size mismatch.**

### 2.4 Control=0x09 (Accepted, Never Observed)

```
Control = 0x09  →  binary 0000 1001

  bits 0-1 = 01 →  S-format (Supervisory)
  bits 2-3 = 00 →  RR (Receive Ready)
  bits 5-7 = 0  →  N(R) = 0
```

Accepted by `AceFrameBuilder.TryParseFrame()` (`Emitters\AceFrameBuilder.cs:115`) as a valid variant but **never observed** (0 frames in 6978). Defensive code path, possibly for future bidirectional acknowledgment.

### 2.5 The Protocol Does NOT Implement LLC Type 2 Semantics

| Property | True LLC Type 2 | ACE Protocol |
|---|---|---|
| Connection establishment | SABM → UA exchange | Not observed |
| Sequence numbers | N(S) increments mod 8 | N(S) = 4 constant |
| Acknowledgments | RR frames with N(R) | Never observed |
| Disconnection | DISC → UA exchange | Not observed |
| Direction | Bidirectional, point-to-point | Unidirectional, multicast |
| Window size | k frames before ack | ∞ (fire-and-forget) |

---

## 3. Handshake Analysis

### 3.1 Evidence Against Handshake

1. **6978 frames, 19.4 min, all unidirectional** — from cRIO → multicast, zero frames TO cRIO
2. **No SABM/UA** — control bytes 0x2F/0x3F (SABM) and 0x63/0x73 (UA) never observed
3. **No RR (0x09)** — zero acknowledgment frames
4. **N(S) never increments** — 6978 frames with N(S)=4, expected increment to N(S)=6
5. **No P/F transitions** — always P/F=0
6. **No DISC** — connection never torn down

### 3.2 Verdict

**No handshake exists (¬ handshake).** Stateless unidirectional multicast. N(S)=4 is a magic constant, not a real sequence number. I-frame syntax is a framing convention, not an operational protocol state machine.

---

## 4. Magic Bytes `00 D0 0D` — Firmware Version Hypothesis

### 4.1 Competing Hypotheses

| # | Hypothesis | Decode | Plausibility |
|---|---|---|---|
| H1 | Firmware version stamp | Major=0x00=0, Minor=0xD0=208, Patch=0x0D=13 → v0.208.13 | **6/10** |
| H2 | 24-bit build number | 0x00D00D = 53261 | 4/10 |
| H3 | Protocol discriminator | FrameType=0x00, VendorID=0xD00D | 4/10 |
| H4 | Pad + 16-bit NI product code | Pad=0x00, ProductCode=0xD00D | 3/10 |
| H5 | Human-readable hex word | "DOOD" leetspeak | 2/10 |

### 4.2 Argument for Firmware Version (H1)

- **Position**: Immediately after LLC header — canonical location for version stamps in embedded telemetry headers
- **Semantics**: EquipmentID + SV + 124-byte data form a complete telemetry frame; no protocol discriminator is NEEDED — LLC SAP already disambiguates
- **Simplicity**: `[LLC] [FwVersion] [Equipment] [SV] [Data]`. No "magic" — just data fields
- **Embedded convention**: industrial embedded systems commonly stamp firmware version in every telemetry frame

### 4.3 Argument Against

- **Minor=208 is unusual**; auto-incrementing build numbers as "minor" is possible
- **NI MAX uses string versions**; on-wire format independent of management UI
- **Single capture** → cannot distinguish constant protocol ID from constant firmware version

### 4.4 How to Test

**Method A (same cRIO, firmware update):** record magic → update firmware → re-capture. Bytes CHANGED ⇒ firmware version; SAME ⇒ protocol discriminator.
**Method B (different cRIO, different firmware):** if magic bytes DIFFER ⇒ firmware version; if SAME ⇒ protocol discriminator.

### 4.5 Current Verdict

**Unresolved.** Pragmatic: treat `00 D0 0D` as a filter constant (code does: `TryParseFrame` checks `frame[17]==0x00 && frame[18]==0xD0 && frame[19]==0x0D`) and be prepared to accept different values from different cRIO installations.

---

## 5. N(S) = 4: Magic Constant, Not Sequence Number

```
6978 frames × mod 8 = 6978 % 8 = 2
Expected N(S) at end = (4 + 2) % 8 = 6
Observed N(S) in ALL frames: 4 (never changes)
```

Bits 1-3 of Control as frame sub-type: `N(S)=4` (0x08) → type 4 = ACE data frame (observed); 0x09 → S-frame RR (accepted, never observed).

---

## 6. Protocol Architecture

### 6.1 Producer → Multicast → Passive Consumers

```
┌─────────────────────┐
│ NI cRIO (LabVIEW RT)│  MAC: 00:80:2f:23:f4:44
│ FPGA/Scan Engine    │  Equipment: 0x00A86F46
│ I/O: C Series mods  │
│ Cycle: 1 ms loop    │
└──────┬──────────────┘
       │  Raw 802.3 LLC multicast (fire-and-forget)
       │  6 frames/s (superframe: 6 sv × 1 Hz)
       │  Dst MAC: 03:00:01:00:00:20
       │
       ▼
┌──────────────────────────────────────────────┐
│               Ethernet (multicast)            │
└──────────────────────────────────────────────┘
       │                    │
       ▼                    ▼
┌──────────────┐   ┌──────────────────┐
│ iCem Desktop │   │ FlecsEmu         │
│ (passive     │   │ (emulator: raw   │
│  consumer)   │   │  802.3 injector) │
│              │   │ AceLanEmitter    │
└──────────────┘   └──────────────────┘
```

### 6.2 Superframe Cycle

```
Default rate: 6 frames/sec (each sv at exactly 1 Hz). Burst-shaped (see §9.3).

Cycle order (FIXED — do not change; field-verified set):
  0x13120203 (Lnd Automation)    6 signals
→ 0x13120206 (Lnd Pump Sect)    20 signals
→ 0x13120200 (Lnd Mix Section)  27 signals
→ 0x13120209 (Lnd RAWIO Data)   23 signals
→ 0x1312020C (Land Addon IO)    18 signals
→ 0x1312020F (Lnd Ops Track)     2 signals
→ (repeat)

Optional: IncludeFirmwareTables = true adds 14 firmware tables
(0x13120212 … 0x13120239, ascending) → 20 sv total (§6.4, T8).
Each sv exactly once per second: 1163 bursts × 6 frames over 1162 s (dump).
```

Emulator implementation: `AcePayloadProvider.ActiveSvNumbers` (`Core\AcePayloadProvider.cs:53`) returns `LegacySvNumbers` (6) by default, or 6+14 when `IncludeFirmwareTables` is set. Order inside the burst is the vector order = this list.

### 6.3 Signal Encoding (ace_wire.signal_encoding)

| ValueType | Wire Format | Rule | Sentinel |
|---|---|---|---|
| FF | float32 BE, 4 bytes, IEEE 754 single | `float(value)` | −9999.0f |
| UL | uint32 BE | `(uint)round(value × dasFactor)` | — |
| UI | uint32 BE | `(uint)round(value × dasFactor)` | — |
| SI | int32 BE | `(int)round(value × dasFactor)` | — |
| SL | int32 BE | `(int)round(value × dasFactor)` | — |
| HX | uint32 BE | `(uint)round(value)` — hex display | — |
| UC | ASCII, NUL-padded to `size` | `value.ToString("F0", InvariantCulture)`; or literal `stringValue` from variable_registry | — |
| unknown | 4 bytes zeros | default branch | — |

Source: `Core\AcePayloadProvider.cs:150` `EncodeSignal`; contract `ace_wire.signal_encoding`.

**Encoding examples (verified):**

| Value | Type | Wire bytes | Notes |
|---|---|---|---|
| 1.0 | FF | `3F 80 00 00` | float32 BE |
| −9999.0 | FF | `C6 1C 3C 00` | sentinel — same bytes seen in dump (Mix Pump Press etc.) |
| 123.45 | FF | `42 F6 E6 66` | float32 BE |
| 11038534 | UL | `00 A8 6F 46` | dasFactor=1.0 → identity; seen in dump (Unit Number) |
| 150 (raw) | UL | `00 00 00 96` | dasFactor=1.0 |
| 3 | HX | `00 00 00 03` | seen as Auto or Manual Mix = 0x00000001 |
| "Initialize" sz=120 | UC | `49 6E 69 74 69 61 6C 69 7A 65 00 …` (120 B) | ASCII + NUL pad; seen in dump |

**Sentinel semantics:** `−9999.0f` ⇔ "signal not available". `C6 1C 3C 00` is the exact sentinel bit-pattern; it appears in the dump as Mix Pump Press, Mix Water Mnfld Press, Water Pot, Cement Pot (§9.2). All 6 legacy signals have `dasFactor = 1.0` in `lantable_registry.json`, so integer signals are identity (`value = round(sim)`).

### 6.4 LantableNT / lantable_registry.json

The legacy signal registry embedded in `Halliburton.iCem.CementingData.Units.dll` (LantableNT) is mirrored into `FlecsEmu\templates\lantable_registry.json`:

- 213 ServiceVector entries, 194 unique sv numbers (4 duplicates in original XML: 0x03020400, 0x05040500, 0x04050600, 0x0B040100 — first occurrence kept)
- Per signal: `byteOffset`, `size`, `valueType`, `dasFactor`, `name`, `uom`, `unitClassId`
- Equipment types: Pump, Cementer, Blender, Land, Mixer, BatchMixer, N2Plant, …
- **`lantable_registry.json` is the source of names/offsets/types.** The 6 legacy SV (Cementer, equipmentType "Cementer") are the ones actually on the wire in `iCemCrio.pcapng.bin`.

Merged (additively, legacy priority) with `FlecsEmu\templates\firmware_registry.json` — 14 Beartooth firmware tables (RT Data A…Spare AI/FI) used only when `IncludeFirmwareTables = true`; signal names 1:1 from `C:\RE`. `FlecsEmu\Core\LantableRegistry.cs` loads/merges both.

---

## 7. Protocol Identification: What It Is and What It Isn't

### 7.1 NOT a Standard Protocol

| Candidate | Reason for Rejection |
|---|---|
| GE SRTP | TCP port 18245, SRTP header — L3 mismatch |
| GE EGD | UDP port 18246, EGD header — L3+L4 mismatch |
| EtherNet/IP | SAP 0xAA (SNAP), CIP encapsulation |
| PROFINET RT | EtherType 0x8892, no LLC |
| BACnet | SAP 0x82, BVLC header |
| Modbus TCP | TCP port 502, MBAP header |
| EtherCAT | EtherType 0x88A4 |
| NI-PSP | TCP port 2343, SVE-managed |

**No Wireshark dissector exists.** Not registered with IEEE (SAP 0x03 is reserved range, multicast MAC unregistered, EtherType 0xD00D unassigned).

### 7.2 IS: Halliburton-Proprietary ACE Protocol

1. LantableNT — 213 cementing-specific service vectors
2. Internal naming: ACE, LANTableNT, AceFrameBuilder
3. Signal names: Pump A Pressure, Downhole Tub Level, OPKO Setting
4. Magic bytes 00 D0 0D — unique
5. NI cRIO source (MAC OUI 00:80:2F confirmed)
6. LabVIEW raw Ethernet implementation pattern

### 7.3 Lineage

```
1990s ─ GE Fanuc Series 90 raw 802.3 COMMREQ (SAP 0x03 configurable)
  │
2000s ─ GE EGD: producer/exchange model, PDU type 0x0D
  │
2010s ─ NI cRIO + LabVIEW RT: raw Ethernet palette
  │
   └── Halliburton ACE Protocol:
        ├── IEEE 802.3 + LLC I-frame (SAP 0x03)   ← GE Fanuc practice
        ├── Magic 0x0D byte                       ← GE EGD echo
        ├── Equipment + ServiceVector              ← producer/exchange model
        ├── LabVIEW raw Ethernet send              ← NI platform
        └── LantableNT.xml                         ← Halliburton signal registry
```

---

## 8. OFFSET LAW — payload_index = ByteOffset − 4 (MAIN RULE)

> This is the single most important layout rule. Any LLM verifying frame construction MUST use it.

### 8.1 The Law

```
payload_index = ByteOffset − 4
absolute_frame_offset = 0x1C + payload_index = 0x18 + ByteOffset
```

- Each lantable signal with registry `byteOffset = O` is written to **payload[O − 4]**,
  i.e. to **frame byte 0x18 + O** (payload area starts at frame 0x1C = 28).
- **Why O ≥ 4:** the sv u32 occupies frame bytes 0x18..0x1B — exactly the slots that would
  be payload_index −4 … −1. Lantable therefore starts signals at O = 4 (payload[0]).
- **Payload footprint:** 124 bytes = frame 0x1C..0xA0 (28..151).
- **Anti-rule (rejected):** signals are NOT at `payload[ByteOffset]`. If they were, the
  first signal of every SV would land at payload[4] and payload[0..3] would be zero — the dump proves otherwise (§8.2).

### 8.2 Proof #1 — "Unit Number" @ frame 28 (= payload[0])

Real frame, SV **0x13120203** (Lnd Automation), first payload from `Wireshark\out\13120203_00A86F46.hex`:

```
payload[0..3]  = 00 A8 6F 46        → frame bytes 28..31
```

- Registry: `Unit Number` O = **4**, type UL, dasFactor 1.0.
- Decode as uint32 BE: `0x00A86F46` = **11038534** = equipment number. Physically plausible (producer self-ID).
- **Contrapositive proof:** under the WRONG law `payload[ByteOffset]`, Unit Number would sit at frame bytes 32..35, and bytes 28..31 would be `00 00 00 00`. The dump has `00 A8 6F 46` at bytes 28..31 — therefore the correct law is `payload_index = ByteOffset − 4`.

### 8.3 Proof #2 — "Cmnt Ops Track IDNum" @ frame 28 and "Cmnt Ops Track Descript" @ frame 32

Real frame, SV **0x1312020F** (Lnd Ops Track), first payload from `Wireshark\out\1312020F_00A86F46.hex`:

```
payload[0..3]  = 46 1C 3C 00   → frame byte 28   = "Cmnt Ops Track IDNum"   (O = 4, UL)
payload[4..123]= 49 6E 69 74 69 61 6C 69 7A 65 00 …  → frame byte 32..151 = "Cmnt Ops Track Descript" (O = 8, UC sz 120)
```

- IDNum as uint32 BE = `0x461C3C00` = 1176255488. The same 4 bytes interpreted as float32 BE = **+9999.0** (a "positive sentinel" — no active job). Both readings of the raw slot are valid; the registry types it UL, the wire carries the float bits of 9999.0.
- Descript = ASCII `Initialize` then NUL-padded to 120 bytes (frame 32..151). Exactly the string reported by the operator console.

### 8.4 Transmittability Rule

```
A signal fits the 124-byte payload iff  (O − 4) + Size ≤ 124
```

- **Example (transmittable, and the real cRIO sends it):** `Cmnt Ops Track Descript`, O = 8, Size = 120 → (8−4)+120 = **124** ⇒ occupies payload[4..123], the whole remaining payload.
- **Counter-example (skipped):** any signal with (O−4)+Size > 124 is dropped by the overflow guard (`Core\AcePayloadProvider.cs:103`). None of the 6 legacy SV overflow; all firmware tables are likewise verified ≤ 124 (T6).
- Code guard verbatim: `if (sig.ByteOffset < 4 || (sig.ByteOffset - 4) + sig.Size > 124) continue;`

### 8.5 Registry → payload_index → Frame Offset Mapping

All 6 legacy SV use 4-byte-wide signals, so the offset ladder is uniform:

```
Registry ByteOffset O   →   payload_index (O−4)   →   absolute frame byte (0x1C + O−4)
          4                  0                        28 (0x1C)
          8                  4                        32 (0x20)
         12                  8                        36 (0x24)
         ...                ...                       ...
         108                104                       132 (0x84)
```

Exception: `Cmnt Ops Track Descript` (O = 8, Size = 120) → payload[4..123] → frame 32..151.

Per-signal mapping tables (O / payload index / HEX bytes / decoded value) for the first payload of each SV are in §9.2.

---

## 9. Golden Proofs from the Real Dump (iCemCrio.pcapng.bin)

Source files (all under `Wireshark\`):
- `iCemCrio.pcapng.bin` — 7225 packets, 6978 ACE frames, 19.4 min
- `out\summary.txt` — aggregate statistics (frames, sv balance, pacing)
- `out\iCemCrio_pacing.txt` — burst timing measurements
- `out\{svhex}_00A86F46.hex` — first payload per SV (hex string, 124 bytes)
  (примечание: `*_all.txt` — все 1163 payload'а — удалены 2026-08-17 как избыточные; полная статистика в `summary.txt`)

### 9.1 GOLDEN FRAME #1 (SV 0x13120203, Equipment 0x00A86F46, Src 00:80:2F:23:F4:44)

Built from the constants of §1.1 (header) + payload from `out\13120203_00A86F46.hex`. This is byte-for-byte what the emulator must produce for the first frame of the first superframe.

```
Offset  Bytes (hex)
────────────────────────────────────────────────────────────────────────────
0000    03 00 01 00 00 20 00 80 2F 23 F4 44 00 8A 03 03
0010    08 00 D0 0D 00 A8 6F 46 13 12 02 03 00 A8 6F 46
0020    00 00 00 01 00 00 00 02 3B 74 10 5C 00 00 00 00
0030    00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
0040    00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
0050    00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
0060    00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
0070    00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
0080    00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
0090    00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
────────────────────────────────────────────────────────────────────────────
TOTAL 152 bytes
```

Full hex line:

```
03000100002000802F23F444008A03030800D00D00A86F461312020300A86F4600000001000000023B74105C000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000
```

Byte-by-byte breakdown of the first 28 header bytes (identical to T3 expectation for sv 0x13120206, differing only in sv):

| Frame offset | Bytes | Field |
|---|---|---|
| 0..5 | `03 00 01 00 00 20` | Dst MAC (multicast) |
| 6..11 | `00 80 2F 23 F4 44` | Src MAC (real cRIO) |
| 12..13 | `00 8A` | IEEE 802.3 Length = 138 |
| 14..16 | `03 03 08` | LLC DSAP/SSAP/Control |
| 17..19 | `00 D0 0D` | Magic |
| 20..23 | `00 A8 6F 46` | Equipment = 11038534 |
| 24..27 | `13 12 02 03` | SV = 0x13120203 |
| 28..31 | `00 A8 6F 46` | payload[0..3] = Unit Number = 11038534 (O=4 ⇒ payload_index 0) |
| 32..35 | `00 00 00 01` | payload[4..7] = Control Mode = 1 (O=8) |
| 36..39 | `00 00 00 02` | payload[8..11] = Mix Mode = 2 (O=12) |
| 40..43 | `3B 74 10 5C` | payload[12..15] = rho12_hat ≈ 0.00372 (O=16) |
| 44..151 | `00 …` | remaining payload (h2_hat, vhp_density_HP = 0) |

### 9.2 Golden Decode Tables (First Payload per SV)

Decoded with `golden_verify.ps1` (offset law §8, encoding §6.3). HEX bytes are read verbatim from `out\{sv}_00A86F46.hex`. Comment legend: *правдоподобно* = physically plausible telemetry; **СЕНТИНЕЛ** = −9999.0f (`C6 1C 3C 00`); *idle* = zero (signal not driven).

#### SV 0x13120203 — Lnd Automation (6 signals)

| Signal | ByteOffset | payload idx | HEX bytes | Decoded | Unit | Comment |
|---|---|---|---|---|---|---|
| Unit Number | 4 | 0 | `00 A8 6F 46` | 11038534 | none | правдоподобно (equipment ID) |
| Control Mode | 8 | 4 | `00 00 00 01` | 1 | none | правдоподобно |
| Mix Mode | 12 | 8 | `00 00 00 02` | 2 | none | правдоподобно |
| rho12_hat | 16 | 12 | `3B 74 10 5C` | 0.00372411963 | lb/gal | правдоподобно (≈0.0037) |
| h2_hat | 20 | 16 | `00 00 00 00` | 0 | ft | idle |
| vhp_density_HP | 24 | 20 | `00 00 00 00` | 0 | lb/gal | idle |

#### SV 0x13120206 — Lnd Pump Sect (20 signals)

| Signal | ByteOffset | payload idx | HEX bytes | Decoded | Unit | Comment |
|---|---|---|---|---|---|---|
| Pump A Pressure | 4 | 0 | `42 FB DD D5` | 125.933266 | psi | правдоподобно (≈125.9) |
| Raw Pmp A Pressure | 8 | 4 | `42 FD 09 73` | 126.518456 | psi | правдоподобно (≈126.5) |
| OPKO A Setting | 12 | 8 | `43 16 00 00` | 150 | psi | правдоподобно (setpoint) |
| Side A Pressure At KO | 16 | 12 | `00 00 00 00` | 0 | psi | idle |
| Side A Kicked Out? | 20 | 16 | `00 00 00 00` | 0x00000000 | hex | idle |
| Pump A Rate | 24 | 20 | `00 00 00 00` | 0 | bbl/min | idle |
| Pump A Stage Total | 28 | 24 | `00 00 00 00` | 0 | bbl | idle |
| Pump A Job Total | 32 | 28 | `00 00 00 00` | 0 | bbl | idle |
| Pump B Pressure | 36 | 32 | `41 52 0F 84` | 13.128788 | psi | правдоподобно (≈13.1) |
| Raw Pmp B Pressure | 40 | 36 | `41 52 AC CD` | 13.1671877 | psi | правдоподобно |
| OPKO B Setting | 44 | 40 | `43 16 00 00` | 150 | psi | правдоподобно (setpoint) |
| Side B Pressure At KO | 48 | 44 | `00 00 00 00` | 0 | psi | idle |
| Side B Kicked Out? | 52 | 48 | `00 00 00 00` | 0x00000000 | hex | idle |
| Pump B Rate | 56 | 52 | `00 00 00 00` | 0 | bbl/min | idle |
| Pump B Stage Total | 60 | 56 | `00 00 00 00` | 0 | bbl | idle |
| Pump B Job Total | 64 | 60 | `00 00 00 00` | 0 | bbl | idle |
| Combined Pump Rate | 68 | 64 | `00 00 00 00` | 0 | bbl/min | idle |
| Combined Stage Total | 72 | 68 | `00 00 00 00` | 0 | bbl | idle |
| Combined Job Total | 76 | 72 | `00 00 00 00` | 0 | bbl | idle |
| Highest Pump Pressure | 80 | 76 | `00 00 00 00` | 0 | psi | idle |

#### SV 0x13120200 — Lnd Mix Section (27 signals)

| Signal | ByteOffset | payload idx | HEX bytes | Decoded | Unit | Comment |
|---|---|---|---|---|---|---|
| Recirc Density | 4 | 0 | `3B 73 DD 3C` | 0.00372107234 | lb/gal | правдоподобно (≈0.0037) |
| Recirc Flow Rate | 8 | 4 | `00 00 00 00` | 0 | bbl/min | idle |
| Recirc Flow Vlv Cmd | 12 | 8 | `00 00 00 00` | 0 | % | idle |
| Downhole Density | 16 | 12 | `00 00 00 00` | 0 | lb/gal | idle |
| Mix Water Rate | 20 | 16 | `00 00 00 00` | 0 | bbl/min | idle |
| Mix Water Stage Total | 24 | 20 | `00 00 00 00` | 0 | bbl | idle |
| Mix Water Job Total | 28 | 24 | `00 00 00 00` | 0 | bbl | idle |
| Downhole Tub Level | 32 | 28 | `C0 20 04 46` | −2.50026083 | ft | правдоподобно (отрицательный уровень) |
| Mix Pump Press | 36 | 32 | `C6 1C 3C 00` | −9999 | psi | **СЕНТИНЕЛ** |
| Mix Water Mnfld Press | 40 | 36 | `C6 1C 3C 00` | −9999 | psi | **СЕНТИНЕЛ** |
| Auto or Manual Mix | 44 | 40 | `00 00 00 01` | 0x00000001 | hex | правдоподобно (Auto=1) |
| Water Pot | 48 | 44 | `C6 1C 3C 00` | −9999 | % | **СЕНТИНЕЛ** |
| Cement Pot | 52 | 48 | `C6 1C 3C 00` | −9999 | % | **СЕНТИНЕЛ** |
| Cement Vlv Cmd | 56 | 52 | `00 00 00 00` | 0 | % | idle |
| Water Vlv Cmd | 60 | 56 | `00 00 00 00` | 0 | % | idle |
| Vol Water Percent | 64 | 60 | `00 00 00 00` | 0 | % | idle |
| Vol Meter Rate | 68 | 64 | `00 00 00 00` | 0 | bbl/min | idle |
| Vol Meter Stage Total | 72 | 68 | `00 00 00 00` | 0 | bbl | idle |
| Vol Meter Job Total | 76 | 72 | `00 00 00 00` | 0 | bbl | idle |
| Stage Sacks of Cement | 80 | 76 | `00 00 00 00` | 0 | sacks | idle |
| Job Sacks of Cement | 84 | 80 | `00 00 00 00` | 0 | sacks | idle |
| Mud Cup Density | 88 | 84 | `00 00 00 00` | 0 | lb/gal | idle |
| Current Blend Density | 92 | 88 | `41 79 99 9A` | 15.6 | lb/gal | правдоподобно (плотность суспензии) |
| Current Blend Yield | 96 | 92 | `40 C0 00 00` | 6 | f3/sack | правдоподобно |
| Current Blend Wtr Req | 100 | 96 | `3F 9D 70 A4` | 1.23 | gal/sack | правдоподобно |
| Current Blend Mix Rate | 104 | 100 | `00 00 00 00` | 0 | bbl/min | idle |
| Event Number | 108 | 104 | `00 00 00 00` | 0 | none | idle |

#### SV 0x13120209 — Lnd RAWIO Data (23 signals)

| Signal | ByteOffset | payload idx | HEX bytes | Decoded | Unit | Comment |
|---|---|---|---|---|---|---|
| Relay Out | 4 | 0 | `00 00 00 00` | 0 | none | idle |
| Digital Inputs | 8 | 4 | `00 00 00 00` | 0 | none | idle |
| Digital Outputs | 12 | 8 | `00 00 00 00` | 0 | none | idle |
| Display Temperature | 16 | 12 | `42 F4 00 00` | 122 | °F (registry uom "f") | правдоподобно |
| Controller Temperature | 20 | 16 | `42 8E 4C CD` | 71.15 | °F | правдоподобно (≈71.2) |
| Analog Input 1 | 24 | 20 | `B9 DA C0 00` | −0.0004172325 | none | ≈0 |
| Analog Input 2 | 28 | 24 | `40 84 51 89` | 4.134953 | none | правдоподобно |
| Analog Input 3 | 32 | 28 | `40 80 73 0E` | 4.014045 | none | правдоподобно |
| Analog Input 4 | 36 | 32 | `BD 11 62 C0` | −0.035494566 | none | ≈0 |
| Analog Input 5 | 40 | 36 | `BD 1E B1 00` | −0.03874302 | none | ≈0 |
| Analog Input 6 | 44 | 40 | `BD 13 B4 80` | −0.03606081 | none | ≈0 |
| Analog Input 7 | 48 | 44 | `BD 0E 36 40` | −0.0347197056 | none | ≈0 |
| Analog Input 8 | 52 | 48 | `BD 15 4A C0` | −0.03644824 | none | ≈0 |
| Analog Output 1 | 56 | 52 | `3B A3 D7 0A` | 0.005 | none | ≈0 |
| Analog Output 2 | 60 | 56 | `3B B0 F2 7C` | 0.0054 | none | ≈0 |
| Analog Output 3 | 64 | 60 | `00 00 00 00` | 0 | none | idle |
| Analog Output 4 | 68 | 64 | `00 00 00 00` | 0 | none | idle |
| Count Input 1 | 72 | 68 | `00 00 00 00` | 0 | none | idle |
| Count Input 2 | 76 | 72 | `00 00 00 00` | 0 | none | idle |
| Count Input 3 | 80 | 76 | `00 00 00 00` | 0 | none | idle |
| Count Input 4 | 84 | 80 | `00 00 00 00` | 0 | none | idle |
| Count Input 5 | 88 | 84 | `00 00 00 00` | 0 | none | idle |
| Count Input 6 | 92 | 88 | `00 00 00 00` | 0 | none | idle |

#### SV 0x1312020C — Land Addon IO (18 signals)

| Signal | ByteOffset | payload idx | HEX bytes | Decoded | Unit | Comment |
|---|---|---|---|---|---|---|
| Spare Analog In 1 | 4 | 0 | `00 00 00 00` | 0 | none | idle |
| Spare Analog In 2 | 8 | 4 | `C0 21 8C BA` | −2.52421427 | none | правдоподобно (≈−2.52) |
| Spare Analog In 3 | 12 | 8 | `C0 21 71 43` | −2.522538 | none | правдоподобно (≈−2.52) |
| Spare Analog In 4 | 16 | 12 | `C0 21 63 88` | −2.5217 | none | правдоподобно (≈−2.52) |
| Spare Counter Input 1 | 20 | 16 | `00 00 00 00` | 0 | none | idle |
| Spare Stage Total 1 | 24 | 20 | `00 00 00 00` | 0 | none | idle |
| Spare Job Total 1 | 28 | 24 | `00 00 00 00` | 0 | none | idle |
| Spare Counter Input 2 | 32 | 28 | `00 00 00 00` | 0 | none | idle |
| Spare Stage Total 2 | 36 | 32 | `00 00 00 00` | 0 | none | idle |
| Spare Job Total 2 | 40 | 36 | `00 00 00 00` | 0 | none | idle |
| Spare Counter Input 3 | 44 | 40 | `00 00 00 00` | 0 | none | idle |
| Spare Stage Total 3 | 48 | 44 | `00 00 00 00` | 0 | none | idle |
| Spare Job Total 3 | 52 | 48 | `00 00 00 00` | 0 | none | idle |
| Spare Counter Input 4 | 56 | 52 | `00 00 00 00` | 0 | none | idle |
| Spare Stage Total 4 | 60 | 56 | `00 00 00 00` | 0 | none | idle |
| Spare Job Total 4 | 64 | 60 | `00 00 00 00` | 0 | none | idle |
| Spare Combined 1 | 68 | 64 | `00 00 00 00` | 0 | none | idle |
| Spare Combined 2 | 72 | 68 | `00 00 00 00` | 0 | none | idle |

#### SV 0x1312020F — Lnd Ops Track (2 signals)

| Signal | ByteOffset | payload idx | HEX bytes | Decoded | Unit | Comment |
|---|---|---|---|---|---|---|
| Cmnt Ops Track IDNum | 4 | 0 | `46 1C 3C 00` | UL: 1176255488 (=0x461C3C00); те же байты = float32BE **+9999.0** | none | sentinel-like (нет активного job) |
| Cmnt Ops Track Descript | 8 | 4 | `49 6E 69 74 69 61 6C 69 7A 65 00 …` (120 B) | "Initialize" | char | правдоподобно (строка состояния) |

### 9.3 Pacing Proofs (from `out\iCemCrio_pacing.txt` + `out\summary.txt`)

| Metric | Dump value | Emulator constant |
|---|---|---|
| ACE frames | 6978 | — |
| Superframes (bursts) | 1163, each exactly 6 frames | superframe = `ActiveSvNumbers.Count` (6 or 20) |
| SV balance | 1163 frames per each of 6 SV | — |
| Burst order | [03, 06, 00, 09, 0C, 0F] in **1162/1163** bursts; 1 reorder [03, 00, 06, 09, 0C, 0F] | `LegacySvNumbers` order |
| Within-burst Δt | n=5815, p10=5.0073 ms, **med=5.25 ms**, p90=5.5456 ms, max=8.332 ms (≈190 Hz) | `BurstFrameIntervalMs = 5.25` (`Emitters\AceLanEmitter.cs:50`), implemented as `round(5.25)=5` ms sleep |
| Between-burst Δt (пауза) | n=1162, min=968.1 ms, **med=973.7 ms**, avg=973.7 ms, max=982.7 ms; no gap > 5000 ms | 1000 − (first-sleep + 5×5) ≈ 970 ms |
| Burst start fraction-of-second | min=0.934284, **med=0.938239**, max=0.943236 | `BurstStartFraction = 0.938` → `initialSleepMs = 938` (`AceLanEmitter.cs:51,228`) |
| Span | 1162.0 s = 19.4 min | — |
| Header anomalies | 0 (100% of 6978: len=0x008A, magic=00D00D, payload=124 B, total=152) | — |

First 12 frames of the dump (fractions of second — the burst shape):

```
#  sv           t (frac-of-second)
1  0x13120203   0.938640      ← first frame: ~938 ms silence, then burst
2  0x13120206   0.943888
3  0x13120200   0.949151
4  0x13120209   0.954370
5  0x1312020C   0.960052
6  0x1312020F   0.964993
7  0x13120203   0.938737      ← next second
...
```

> **Emulator implementation note.** `AceLanEmitter.SendSuperframe` (`Emitters\AceLanEmitter.cs:172`):
> `burstIntervalMs = max(1, round(5.25)) = 5 ms`; `initialSleepMs = (int)(0.938×1000) = 938 ms`.
> When `vectors.Count > 6` (i.e. 20 SV with firmware tables), the burst start is pulled earlier so the
> tail does not cross the 1.0 s boundary: `startFraction = min(0.938, 1.0 − N×5.25/1000 − 0.01)`
> (for N=20 ⇒ ≈0.885). Contract `ace_wire.pacing` comment ("равномерно, interval 1000/N ms") is
> **stale** — the actual on-wire shape is the burst above (copied from the cRIO dump).

### 9.4 Anomalies & Notes

1. **1 superframe reorder** out of 1163 (0.086%): `[03, 00, 06, 09, 0C, 0F]` — sv 0x13120206 and 0x13120200 swapped. Random OS jitter in a ~5 ms window; the protocol tolerates it.
2. **DHCP DISCOVER, no reply.** cRIO (00:80:2F:23:F4:44) sent 117× DHCP DISCOVER (0.0.0.0:68 → 255.255.255.255:67, ~1/10 s). No DHCP reply was captured ⇒ **the cRIO had no IP address**. Consequence: **no TCP/Modbus/VI-Server** anywhere in the dump — the ONLY transport is raw 802.3 ACE (plus DHCP). This is why the capture contains no Modbus TCP frames.
3. **Payload variability.** 4786 unique payloads of 6978 (2192 duplicates) — real varying telemetry; the first payload of each SV (in `out\*.hex`) is a snapshot used as golden reference.
4. **Firmware SV absent.** No frames with SV 0x13120212..0x13120239 — the real unit runs legacy tables only; the emulator adds them only when `IncludeFirmwareTables = true`.

---

## 10. LLM / Engineer Verification Procedure (How to Verify)

Reproducible, step-by-step. Every step produces a checkable artifact.

### Step 1 — Rebuild a frame from constants and compare to GOLDEN FRAME #1

1. Take the constants of §1.1 (dst MAC, len 0x008A, LLC 03 03 08, magic 00 D0 0D, equipment 0x00A86F46, sv 0x13120203).
2. Take the payload hex from `out\13120203_00A86F46.hex` (124 bytes).
3. Concatenate 28-byte header + 124-byte payload → must equal the 152-byte hex of §9.1 **byte-for-byte**.
4. Automated alternative: `AceFrameBuilder.BuildFrame` + compare, or replicate header in any language (the header is pure constants; §9.1 shows the expected output).

### Step 2 — Decode payload by the offset law (§8) and compare to §9.2

1. For each SV, load its registry signals (`lantable_registry.json` → `serviceVectors[].signals[]`).
2. For each signal compute `pidx = byteOffset − 4`; read 4 bytes (or `size`) from `payload[pidx..]`.
3. Decode per type (§6.3) and compare against the golden table §9.2.
4. **Hand-calculation examples (hex → float32 BE → number):**

   a) `Pump A Pressure` (SV 0x13120206, O=4 → payload[0..3]) = `42 FB DD D5`:
      - bits: sign=0, exponent=`10000101`=0x85=133 → 2^(133−127)=64, mantissa≈1.96771
      - value ≈ 1.96771 × 64 ≈ **125.93 psi** ✓ (golden table; exact decode 125.933266)
   b) `rho12_hat` (SV 0x13120203, O=16 → payload[12..15]) = `3B 74 10 5C`:
      - exponent=`01110110`=0x76=118 → 2^(118−127)=2^−9≈0.001953125, mantissa≈1.90675
      - value ≈ 1.90675 × 0.001953125 ≈ **0.00372 lb/gal** ✓ (exact decode 0.00372411963)
   c) `Mix Pump Press` (SV 0x13120200, O=36 → payload[32..35]) = `C6 1C 3C 00`:
      - sign=1, exponent=0x8C=140 → 2^13=8192, mantissa≈1.22058 → value ≈ −1.22058×8192 ≈ **−9999.0 = sentinel** ✓

### Step 3 — Check encoding per type (§6.3)

- Verify `1.0f → 3F 80 00 00`, `−9999.0f → C6 1C 3C 00`, `123.45f → 42 F6 E6 66` (§6.3 examples) with any IEEE-754 float32 converter.
- Verify UL/HX/UC rules: `11038534 → 00 A8 6F 46`; `3 → 00 00 00 03`; `"Initialize" → 49 6E … 00×110`.
- Confirm all 6 legacy signals have `dasFactor = 1.0` in the registry (so integer encoding is identity).

### Step 4 — Run the self-test suite

```
cd FlecsEmu
dotnet run --project FlecsEmu.csproj -- --selftest     # or: bin\FlecsEmu.exe --selftest
```

Expected: `ALL TESTS PASSED`, exit code 0. Test → rule mapping (§11):

| Test | Locks |
|---|---|
| T1 | BuildFrame→TryParseFrame roundtrip (frame layout §1, encoding §6.3) |
| T2 | TryParseFrame rejects bad magic / dst / short frame (§1.4) |
| T3 | Header conformance to §1.2 (28 header bytes byte-exact) |
| T5 | Manual override reaches payload[ByteOffset−4]; UC 120 transmittable (§8.4) |
| T6 | 14 firmware tables loaded, all ≤ 124 B (offset law guard §8) |
| T7 | Unit conversion factors 42 / (v−32)·5÷9 / 0.119826 / 0.3048 / 0.0689476 (§12) |
| T8 | Default 6-sv superframe, full 20-sv order; wire conversion bbl/min→gpm / m³/min (§9.3, §12) |
| T9 | UnitSettings presets/overrides; bpm → None (§12) |
| T11 | Conversion matrix (Flow/Volume/Density/Level/Pressure linear, Temperature affine) |
| T12 | Aliases & case-insensitivity (gpm/ppg/psia/degF) |
| T13 | Non-convertible units identity (bpm/mA/char/composite) |
| T14 | native→target→native roundtrip, all groups × 4 presets, −40 °F exact |
| T15 | Preset matrix 4×6 (§12) |
| T16 | 1:1 firmware guarantee under FirmwareDefault (~259 signals) (§12) |
| T17 | Byte-level decorator: Recirc Flow Rate O=8→payload[4]; density O=4→payload[0]; UC untouched |
| T18 | Cross-group conversion = identity |
| T19 | MB_DensHPVT g/cc 1:1 regression (§12) |
| T20 | Offset law on bytes: payload[ByteOffset−4] (§8) |

### Step 5 — Run the emulator and capture packets (Wireshark/Npcap)

1. Enable LAN tab, pick adapter, equipment 0x00A86F46 (default), src MAC empty (⇒ adapter MAC) or 00:80:2F:23:F4:44.
2. Capture on the adapter with Wireshark; filter by raw 802.3 (e.g. `eth.addr == 03:00:01:00:00:20`).
3. Control points:
   - every frame 152 bytes, dst `03:00:01:00:00:20`, Length `0x008A`, LLC `03 03 08`, Magic `00 D0 0D`;
   - Equipment `00 A8 6F 46`; SV cycles `13 12 02 03 → 06 → 00 → 09 → 0C → 0F`;
   - frame bytes 28..31 (SV 0x13120203) = `00 A8 6F 46` (Unit Number @ payload[0]);
   - burst pacing: ~938 ms silence, then 6 frames with ~5 ms gaps, then ~970 ms silence (§9.3);
   - first payload of SV 0x13120203 equals `out\13120203_00A86F46.hex`.

### Step 6 — Golden verify script (automatic)

`Wireshark\golden_verify.ps1` (shipped next to this file) reads the 6 `out\*_00A86F46.hex` payloads,
decodes them with `lantable_registry.json` (offset law §8, encoding §6.3) and prints the value tables of §9.2:

```
powershell -NoProfile -ExecutionPolicy Bypass -File Wireshark\golden_verify.ps1
```

It exits with "ALL FILES OK (124 B each)" and reproduces every decoded value in §9.2
(Unit Number 11038534, Pump A Pressure 125.933266, OPKO 150, sentinel −9999, Display Temperature 122,
Current Blend Density 15.6, IDNum 0x461C3C00, "Initialize", …). Treat any mismatch against §9.2 as a
spec/implementation regression.

---

## 11. Document ↔ Code ↔ Tests Cross-Reference

| Rule | PROTOCOL.md | Code (file:line / method) | Test (T#) |
|---|---|---|---|
| Frame header (152 B, dst, len 0x008A, LLC, magic, equipment, sv) | §1.1 | `Emitters\AceFrameBuilder.cs:40` `BuildFrame` | T1, T2, T3 |
| Frame parse/validate (reject bad magic/dst/short, Control ∈ {0x08,0x09}) | §1.4, §2.4 | `AceFrameBuilder.cs:91` `TryParseFrame` | T1, T2 |
| Offset law + overflow guard (`payload_index = O − 4`; (O−4)+Size ≤ 124) | §8 | `Core\AcePayloadProvider.cs:91` `BuildPayload`; guard `:103` | T5, T6, T20 (T8/T17 byte-level) |
| Signal encoding FF/UL/UI/SI/SL/HX/UC | §6.3 | `AcePayloadProvider.cs:150` `EncodeSignal` | T1, T5, T20 |
| Superframe sv-set & order (6 legacy; +14 firmware) | §6.2, §9.3 | `AcePayloadProvider.cs:53` `ActiveSvNumbers`, `:26` `LegacySvNumbers` | T8 |
| Burst pacing (5.25 ms, start 0.938, >6 sv re-start) | §9.3 | `Emitters\AceLanEmitter.cs:50` `BurstFrameIntervalMs`, `:51` `BurstStartFraction`, `:224` | — (нет автотеста; ручная проверка Wireshark/--landiag) |
| Unit conversion (×42, affine °F, 0.119826, 0.3048, 0.0689476) | §12 | `Core\UnitConversion.cs:305` `Convert` | T7, T11, T14 |
| Unit grouping / bpm identity / aliases | §12 | `UnitConversion.cs:149` `GroupOf`, `:179` `CanonicalUom` | T7, T9, T12, T13 |
| Preset matrix; default preset LanNative | §12 | `UnitConversion.cs:13` `UnitPreset`, `:201` `TargetUom`, `:241` `DisplayUnit`, `:30` `UnitSettings.Preset` | T9, T15 |
| Firmware-native identity (NeedsConversion under FirmwareDefault) | §12 | `UnitConversion.cs:353` `NeedsConversion` | T16, T19 |
| Wire conversion decorator (payload patched in target units) | §12 | `Core\UnitConvertingPayloadProvider.cs:59` `Patch` | T8, T17, T19 |
| UC not converted, overflow signals untouched | §8.4, §12 | `UnitConvertingPayloadProvider.cs:72` guard | T17 |

---

## 12. Units on the Wire (What the Real Wire Carries)

**The real LAN wire carries LAN-native units.** Evidence from the dump (§9.2) and the registry:

- **Temperature = °F** — `Display Temperature` = 122.0 (registry uom `"f"` → canonical `°f`). If the wire carried °C, a physically sane value would be ~50 °C, not 122; °F interpretation is the only plausible one.
- **Flow = bbl/min** — all flow fields in the 6 legacy tables have uom `bbl/min` (Recirc Flow Rate, Mix Water Rate, Vol Meter Rate, Pump A/B Rate, Combined Pump Rate, Current Blend Mix Rate).
- **Density = lb/gal** — Recirc Density, Downhole Density, Mud Cup Density, Current Blend Density, rho12_hat, vhp_density_HP (uom `lb/gal`; ppg ≡ lb/gal alias).
- **Pressure = psi** — Pump A/B Pressure, OPKO settings, Mix Pump/Mnfd Pressures.
- **Level = ft** — Downhole Tub Level (−2.50 ft), h2_hat.
- **Volume = bbl** — totals (Mix Water/Pump/Vol Meter/Combined totals).

| Group | Unit on the wire (LAN-native) | Registry uom example | RT Data fields in `C:\RE` | Match |
|---|---|---|---|---|
| Flow | bbl/min | Recirc Flow Rate, Pump A Rate | firmware RT tables use firmware-native flow `gal/min` [проверить: сопоставление полей RT Data с C:\RE недоступно агенту] | LAN-native on legacy SV; firmware-native on RT SV (identity under FirmwareDefault, T16) |
| Temperature | °F | Display Temperature, Controller Temperature (`f`) | RT Data °C/°F [проверить] | °F on legacy SV (122.0 °F proven) |
| Density | lb/gal | Recirc Density, Current Blend Density | firmware-native lb/gal / g/cc (identity, T16/T19) | ppg ≡ lb/gal |
| Pressure | psi | Pump A Pressure, OPKO Setting | firmware-native psi | psi |
| Level | ft | Downhole Tub Level, h2_hat | firmware-native ft | ft |
| Volume | bbl | Mix Water Job Total, Vol Meter Stage Total | firmware-native bbl | bbl |

**Conversion rules (emulator, `Core\UnitConversion.cs`):**

- Default emulator preset = **LanNative** (`UnitSettings.Preset` default, `UnitConversion.cs:30`) → wire bytes already in native units; conversion is identity. This is the preset that reproduces the dump 1:1.
- Under **FirmwareDefault**: only legacy natives `bbl/min` (Flow) and `°F` (Temperature) are converted; firmware-native units `{gal/min, °c, lb/gal, g/cc, bbl, ft, psi}` stay identity (`NeedsConversion`, `UnitConversion.cs:353`; T16/T19).
- **`bpm` is deliberately NOT converted** (pump-rate signal; GroupOf → None; protects PS/DS rate from a bogus bbl/min factor).
- Composite/relationship units (`psi/ma`, `gal/sack`, `ft/min`, `degF/ma`, …) and `mA`, `%`, `Hz`, `char` → GroupOf None → identity.
- Temperature is affine (°F↔°C, `(v−32)×5/9`); all other groups are linear `value × factor(to) / factor(from)`; cross-group target ⇒ identity (guard, T18).

---

## 13. Test Harness

### 13.1 Current (canonical): `FlecsEmu` self-test

- Location: `FlecsEmu\SelfTest\FrameRoundTrip.cs`
- Run: `FlecsEmu.exe --selftest` (or `dotnet run --project FlecsEmu.csproj -- --selftest`); exit code = number of failures; `ALL TESTS PASSED` on success.
- Coverage: T1..T20 — frame build/parse roundtrip, header conformance, invalid-frame rejection, manual-edit→wire path, offset law, unit conversion, superframe order, firmware 1:1, byte-level provider checks (see §10 Step 4 for the mapping).

### 13.2 Historical (superseded): `tools\ace_replay`

Original .NET 8 console regression suite (33 tests: T1–T12 + TN1–TN2): PcapngReader (EPB/linktype 1), AceFrameParser (filter/eq/sv/decode), LantableExtractor, golden/summary.json lock on first 10 superframes. The 6978-frame dataset, sv balance and `dt=1.00 s` results are the same data now carried by `out\summary.txt` / `out\iCemCrio_pacing.txt`.

---

## 14. Open Questions

| # | Question | Status |
|---|---|---|
| 1 | Origin of magic bytes 00 D0 0D (exact LabVIEW VI/function) | Unknown — no public NI docs |
| 2 | Control=0x09 (RR frames) — do they ever occur? | Never observed; code accepts defensively |
| 3 | DASConversionFactor ≠ 1.0 variants | Not tested on wire (not in current dump; all 6 legacy dasFactor = 1.0) |
| 4 | Pump kickout service vectors (0x0302xxxx) | In LantableNT but not in capture |
| 5 | Multi-equipment scenario | Only 1 equipment in capture (0x00A86F46) |
| 6 | LabVIEW source code for cRIO | Unavailable to POC team |
| 7 | Protocol versioning mechanism | None observed (FrameType=0x00 only) |
| 8 | Exact mapping of legacy LAN-native fields ↔ `C:\RE` RT Data field names | [проверить: требуется доступ к C:\RE] |

---

## 15. References

### 15.1 Internal
- `Wireshark\iCemCrio.pcapng.bin` — 19.4 min, 6978 ACE frames, 1 equipment, 6 sv, real cRIO
- `Wireshark\out\summary.txt` — aggregate stats (frames, sv balance, pacing, DHCP)
- `Wireshark\out\iCemCrio_pacing.txt` — burst-pacing measurements
- `Wireshark\out\{svhex}_00A86F46.hex` — first payload per SV (golden, 124 bytes)
- `Wireshark\golden_verify.ps1` — decode/verify script (§10 Step 6)
- `FlecsEmu\templates\lantable_registry.json` — legacy signal registry (213 SV, names/offsets/types)
- `FlecsEmu\templates\firmware_registry.json` / `firmware_variables.json` — 14 Beartooth RT tables
- `FlecsEmu\templates\variable_registry.json` — simulation values (wireName/alias mapping to lantable signals)
- `FlecsEmu\specs\CONTRACT_FlecsEmu.yaml` — contract (`ace_wire` section = frozen wire constants)
- `tools\ace_replay\lantable_extracted.xml` — full LantableNT registry (source of `lantable_registry.json`)
- `C:\Program Files\Halliburton\iCem\Bin\Halliburton.iCem.CementingData.Units.dll` — vendor DLL containing LantableNT

### 15.2 External Standards
- IEEE 802.2 (ISO/IEC 8802-2): LLC specification
- IEEE 802.3: CSMA/CD Ethernet framing
- IEEE SA LLC Public Listing: SAP 0x03 = Group DSAP, Reserved JTC1/SC6
- GE Fanuc GFK-1541: Series 90 Ethernet Interface User Manual (COMMREQ raw send)
- Wireshark: packet-llc.c, packet-srtp.c, packet-egd.c source references

---

## 16. Change Log

| Date | Change |
|---|---|
| 2026-08-17 | [FIXED 2026-08-17] 4-byte emitter misalignment устранён: сигналы пишутся на payload[ByteOffset−4] (кадр 0x18+O), как у реального cRIO; "Cmnt Ops Track Descript" (UC 120) снова передаваем |
| 2026-08-17 | §1.1 corrected: signal area starts at frame offset 0x18 (24), NOT 0x1C. The sv u32 occupies signal-area slots 0..3 (all lantable signals have ByteOffset ≥ 4). Verified against iCemCrio.pcapng.bin: Unit Number (o=4) @ frame 28, Mix Mode (o=12) @ frame 36, Pump A Pressure (o=4) @ frame 28 = 125.93 psi (golden T8). The 0x1C row was a documentation error that caused a 4-byte emitter misalignment (FlecsEmu AceFrameBuilder.PayloadOffset) — raw int bits appearing in float register slots on the lan bus. |
| 2026-08-17 | **[2026-08-17] Offset law `payload_index = ByteOffset − 4` фиксирован по дампу iCemCrio.pcapng.bin:** proof #1 (Unit Number @ frame 28 = 00 A8 6F 46 = 11038534), proof #2 (Cmnt Ops Track IDNum @ frame 28 = 0x461C3C00, Descript "Initialize" @ frame 32); подтверждён тестами T5/T20; правило передаваемости (O−4)+Size ≤ 124 (Descript 4+120=124 — передаваем). |
| 2026-08-17 | **Бёрст-pacing по замерам дампа:** within-burst med 5.25 ms (p10–p90 5.0073–5.5456), между бёрстами med 973.7 ms, старт бёрста med 0.938239 доли секунды; эмулятор: BurstFrameIntervalMs=5.25 / BurstStartFraction=0.938 (`Emitters\AceLanEmitter.cs`); 1163 суперфрейма за 19.4 мин, порядок [03,06,00,09,0C,0F] в 1162/1163, 1 перестановка. |
| 2026-08-17 | **Golden-таблицы добавлены (§9):** GOLDEN FRAME #1 (152 Б, SV 0x13120203) + полные декоды первого payload всех 6 SV из `out\{sv}_00A86F46.hex` (Unit Number=11038534, PS=125.93 psi, OPKO=150, Tub Level=−2.50, sentinel −9999 ×4, Blend Density=15.6/Yield=6.0/WtrReq=1.23, Display Temp=122.0 °F, IDNum=0x461C3C00, "Initialize"); скрипт верификации `Wireshark\golden_verify.ps1`. |
| 2026-08-17 | **Пресет по умолчанию = LanNative** (`Core\UnitConversion.cs:30`) — реальный провод несёт нативные единицы (°F/bbl-min/lb-gal/psi/ft); правило firmware-native identity под FirmwareDefault (T16/T19). |
| 2026-08-09 | Initial spec. Corrected Control=0x08 from UI to I-frame. Handshake refuted. |
| 2026-08-09 | GE SRTP definitively excluded (byte-level comparison). Firmware version hypothesis added (H1-H5). |
| 2026-08-09 | README B4 update: Mongo writer excluded — write target = SQLite (icem_poc.db), legacy MongoDB RealtimeDB is READ-ONLY. |
