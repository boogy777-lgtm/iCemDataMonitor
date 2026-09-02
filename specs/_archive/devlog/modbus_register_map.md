# iCem POC v4 — Modbus Register Map

> ## ⚠ LEGACY — python server (`server\`) register map, v4 epoch. NOT the live source of truth.
> **Актуальный Modbus TCP :502 = server-dotnet:**
> `server-dotnet\Station.Sdk\Bus\BusLayout.cs` (bus register map v5.1) +
> `server-dotnet\Station.Transport\ModbusTcpPublisher.cs` (BusReader → memcpy-mirror,
> runtime-sized register file). Карта ниже — историческая, от python-сервера.
>
> **Ключевые расхождения v4→v5 (этот документ ↔ живой код):**
> - Heartbeat: reg 0 (wrap 0..32767) → **reg 3** (wrap 32000→0), в блоке состояния
>   0..3 {comPortActive, caseState, caseLanState, heartbeat} — весь блок AddIn-owned;
> - Порядок слов float32: ABCD (MSW первым) → **CDAB** (reg[N] = младшее слово);
> - Зоны v5.1: com_stream 4..199 / lan_stream_crio **200**..399 / raw_com_line 400..599
>   (здесь «lan base=400» из v4 больше не верен — 400..599 = raw_com_line);
>   ace_full reserve 600..10000 — DEFERRED, никто не пишет;
> - Активация конфига (`POST /api/config/activate`, flecs_ver4p53…) **отменена**:
>   источник правды = bus MMF (шапка self-describing regCount), карта runtime-sized;
> - Mongo вне контура (служба :33036 остановлена); живой стек = bus MMF + Modbus :502 + Kestrel :8090.
>
> **Verdict: ARCHIVE-PROPOSAL** — документ актуален только для legacy python-сервера;
> рекомендуется перенести в архив, правок живого стека сюда не вносить.

## Layout (16-bit Holding Registers)

| Reg | Name | Type | Description |
|-----|------|------|-------------|
| 0 | Heartbeat | U16 | Counter +1/sec, wraps 0..32767 |
| 1 | Reserved | U16 | Always 0 |
| 2 | var[0]_hi | U16 | Variable 0, high 16 bits |
| 3 | var[0]_lo | U16 | Variable 0, low 16 bits |
| 4 | var[1]_hi | U16 | Variable 1, high 16 bits |
| 5 | var[1]_lo | U16 | Variable 1, low 16 bits |
| ... | ... | ... | ... |
| 2+2N | var[N]_hi | U16 | Variable N |
| 2+2N+1 | var[N]_lo | U16 | Variable N |

## Active Config: flecs_ver4p53_ppg_bpm_psi (default)

| Reg | Variable | Description | UoM | Value encoding |
|-----|----------|-------------|-----|----------------|
| 0 | Heartbeat | HB counter | — | 0..32767 |
| 1 | (reserved) | — | — | 0 |
| 2-3 | Recirc Density | Recirc Density | ppg | float32 IEEE-754 (ABCD) |
| 4-5 | DH Density | Downhole Density | ppg | float32 IEEE-754 (ABCD) |
| 6-7 | Mix Water Rate | Mix Water Rate | bpm | float32 IEEE-754 (ABCD) |
| 8-9 | Comb Pump Rate | Combined Pump Rate | bpm | float32 IEEE-754 (ABCD) |
| 10-11 | PS Pump Press | Pass-Side Pump Press | psi | float32 IEEE-754 (ABCD) |
| 12-13 | DS Pump Press | Driv-Side Pump Press | psi | float32 IEEE-754 (ABCD) |
| 14-15 | Water Stg Tot | Mix Water Stage Total | bbl | float32 IEEE-754 (ABCD) |
| 16-17 | Mix Water Total | Mix Water Total | bbl | float32 IEEE-754 (ABCD) |
| 18-19 | Pump Stg Tot | Pump Stage Total | bbl | float32 IEEE-754 (ABCD) |
| 20-21 | Comb Pump Total | Combined Pump Total | bbl | float32 IEEE-754 (ABCD) |
| 22-23 | Cmnt Valve Psn | Cement Valve Position | % | float32 IEEE-754 (ABCD) |
| 24-25 | Water Valve Psn | Water Valve Position | % | float32 IEEE-754 (ABCD) |
| 26-27 | PS Pump Rate | Pass-Side Pump Rate | bpm | float32 IEEE-754 (ABCD) |
| 28-29 | DS Pump Rate | Driv-Side Pump Rate | bpm | float32 IEEE-754 (ABCD) |
| 30-31 | Dig out | Digital Out | — | raw int |
| 32-33 | HalWin Event | HalWin Event Number | — | raw int |
| 34-35 | Temperature | Temperature | degF | float32 IEEE-754 (ABCD) |
| 36-37 | rho12_hat | rho12_hat | ppg | float32 IEEE-754 (ABCD) |
| 38-39 | h2_hat | h2_hat | ft | float32 IEEE-754 (ABCD) |
| 40-41 | vhp_density_HP | Vhp_density_HP | ppg | float32 IEEE-754 (ABCD) |
| 42-43 | Tub Height | Tub Height | ft | float32 IEEE-754 (ABCD) |
| 44-45 | Extra Press 1 | Extra Press 1 | psi | float32 IEEE-754 (ABCD) |
| 46-47 | Extra Press 2 | Extra Press 2 | psi | float32 IEEE-754 (ABCD) |
| 48-49 | Extra Rate 1 | Extra Rate 1 | bpm | float32 IEEE-754 (ABCD) |
| 50-51 | Extra Total 1 | Extra Total 1 | bbl | float32 IEEE-754 (ABCD) |
| 52-53 | Extra Rate 2 | Extra Rate 2 | bpm | float32 IEEE-754 (ABCD) |
| 54-55 | Extra Total 2 | Extra Total 2 | bbl | float32 IEEE-754 (ABCD) |
| 56-57 | Wtr/slry Ratio | Water/Slurry Ratio | % | float32 IEEE-754 (ABCD) |
| 58-59 | MudCup Density | MudCup Density | ppg | float32 IEEE-754 (ABCD) |
| 60-61 | PS Pmp Stg Tot | PS Pmp Stage Total | bbl | float32 IEEE-754 (ABCD) |
| 62-63 | DS Pmp Stg Tot | DS Pmp Stage Total | bbl | float32 IEEE-754 (ABCD) |
| 64-65 | VolumetricMeter | Volumetric Meter Flow Rate | bpm | float32 IEEE-754 (ABCD) |

**Total: 32 variables = 64 data registers (2-65) + heartbeat (0) + reserved (1) = 66 registers**

## Selecting a different config

Call `POST /api/config/activate` with `{"family": "flecs", "name": "flecs_ver4p53_ppg_bpm_psi"}`.
The register map rebuilds automatically — variables are laid out sequentially starting at reg 2,
skipping "Time & Date" and "Unit Num" columns and spare/logical fields.

See `/api/devices` for the full list of families and configs.

---

## 2026-08: Family base offsets (server-dotnet, deterministic rule)

Реализовано в `Station.Data\InMemoryConfigStore.BuildDeterministicModbusMap`:

**`address = familyModbusBase + orderInMerge × 2`**

- `familyModbusBase` — из `templates\variable_registry.json` → `families.<family>.modbusBase`:
  `flecs = 2`, `plc = 200` (неизвестная family ⇒ 2);
- `orderInMerge` — порядок в активном шаблоне (template vars первыми, в порядке SSF-файла),
  затем live-only переменные (discovery path) продолжают последовательность;
- детерминировано: одинаковый вход ⇒ одинаковая карта независимо от порядка discovery;
- «шаблоны идут последовательно со своим смещением»: смена активного конфига переключает
  адресное окно целиком (flecs ⇒ 2.., plc ⇒ 200..).

### PLC family (plc_basic), base = 200

| Reg | Variable | UoM | Value encoding |
|-----|----------|-----|----------------|
| 200-201 | (первая template/live переменная активного PLC конфига) | — | float32 IEEE-754 (ABCD) |
| 202-203 | (вторая) | — | float32 IEEE-754 (ABCD) |
| ... | ... | ... | ... |

При активном конфиге `plc/plc` (plc.ssf): 12 template vars на 200-223, live-only
(например переменные эмулятора plc_basic: PLC Status, Suction Pressure, …,
Valve Position) продолжают с 224 в алфавитном порядке.

### Проверено на проводе (2026-08-09)

- Heartbeat reg0 инкрементится (Modbus TCP :502, unit 1, func 0x03);
- flecs active ⇒ данные с reg 2; plc active ⇒ template-блок с reg 200,
  live-only PLC-значения (Suction Pressure 120.02 → float32 IEEE-754) с reg 224+;
- кодировка float32 IEEE-754, big-endian word order (ABCD convention):
  reg[N] = bytes[0..1] (MSW), reg[N+1] = bytes[2..3] (LSW).
- precision: ~7 значащих цифр (стандартная float32, без квантования ×10).

### Зарезервировано (2026-08-09, см. README §12) — ✅ АКТИВИРОВАНО 2026-08-10

- `lan` family (ACE/LAN-канал, ПЛК NI cRIO, 6 сервисных векторов Lnd *) ⇒
  base = 400. РЕАЛИЗОВАНО: B1 (LantableRegistry + 96 алиасов "Lan/..." в
  variable_registry.json) + B4 (SnifferBufferService пишет LAN-тики в нашу
  SQLite → ModbusTcpPublisher подхватывает через GetCurrentAsync).
  GUI Modbus Map уже умеет base 400 (фикстура mock/modbus-preview.json).
  Полевая проверка живой карты регистров 400+ — на стенде с реальным cRIO.
