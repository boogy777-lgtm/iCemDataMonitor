using System;
using System.Buffers.Binary;
using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.Emitters;

namespace Halliburton.iCemEmule.SelfTest
{
    /// <summary>
    /// Self-test suite for E2 transport layer.
    /// Tests frame build/parse roundtrip, rejection of invalid frames,
    /// byte-level header conformance to PROTOCOL.md §1.2, and graceful COM port error.
    /// </summary>
    public static class FrameRoundTrip
    {
        public static int RunAll()
        {
            int failCount = 0;
            Console.WriteLine("=== iCemEmule Transport Self-Test ===");
            Console.WriteLine();

            failCount += Test1_RoundTrip();
            failCount += Test2_RejectInvalid();
            failCount += Test3_HeaderConformance();
            failCount += Test4_ComGracefulError();
            failCount += Test5_ManualEditReachesWire();
            failCount += Test6_FirmwareRegistryLoads();
            failCount += Test7_UnitConversion();
            failCount += Test8_SuperframeFirmwareSvs();
            failCount += Test9_UnitSettingsOverrides();
            failCount += Test10_ComConversion();
            failCount += Test11_ConvertMatrix();
            failCount += Test12_AliasAndCase();
            failCount += Test13_NonConvertible();
            failCount += Test14_RoundTrip();
            failCount += Test15_PresetMatrix();
            failCount += Test16_FirmwareOneToOne();
            failCount += Test17_ProviderByteLevel();
            failCount += Test18_CrossGroupIdentity();
            failCount += Test19_FirmwareNativeGccRegression();
            failCount += Test20_OffsetLaw();
            failCount += CommandChannelTests.RunAll(); // T21..T26: operator command channel

            Console.WriteLine();
            if (failCount == 0)
                Console.WriteLine("ALL TESTS PASSED");
            else
                Console.WriteLine($"{failCount} TEST(S) FAILED");

            return failCount;
        }

        /// <summary>
        /// T1: BuildFrame → TryParseFrame roundtrip.
        /// equipment=0x00A86F46, sv=0x13120206, payload = deterministic (index → byte).
        /// Parsed equipment, sv, and payload must be byte-equal to originals.
        /// </summary>
        private static int Test1_RoundTrip()
        {
            const uint equipment = 0x00A86F46;
            const uint sv = 0x13120206;
            byte[] srcMac = AceFrameBuilder.ParseMac("00:80:2F:23:F4:44");

            // Payload: i → (byte)(i & 0xFF), cyclic fill
            byte[] payload = new byte[AceFrameBuilder.PayloadSize];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i & 0xFF);

            // Build
            byte[] frame = AceFrameBuilder.BuildFrame(srcMac, equipment, sv, payload);

            // Parse
            bool ok = AceFrameBuilder.TryParseFrame(frame, out uint parsedEq, out uint parsedSv, out byte[] parsedPayload);

            if (!ok)
            {
                Console.WriteLine("T1 FAIL: TryParseFrame returned false on valid frame");
                return 1;
            }
            if (parsedEq != equipment)
            {
                Console.WriteLine($"T1 FAIL: equipment mismatch: expected 0x{equipment:X8}, got 0x{parsedEq:X8}");
                return 1;
            }
            if (parsedSv != sv)
            {
                Console.WriteLine($"T1 FAIL: sv mismatch: expected 0x{sv:X8}, got 0x{parsedSv:X8}");
                return 1;
            }
            if (!ByteArrayEqual(payload, parsedPayload))
            {
                Console.WriteLine("T1 FAIL: payload mismatch");
                return 1;
            }

            Console.WriteLine("T1 PASS: BuildFrame→TryParseFrame roundtrip byte-equal");
            return 0;
        }

        /// <summary>
        /// T2: TryParseFrame MUST reject:
        ///   - Wrong magic (flip byte 17)
        ///   - Wrong dst MAC (change first byte)
        ///   - Frame too short (151 bytes)
        /// </summary>
        private static int Test2_RejectInvalid()
        {
            int fails = 0;
            const uint equipment = 0x00A86F46;
            const uint sv = 0x13120206;
            byte[] srcMac = AceFrameBuilder.ParseMac("00:80:2F:23:F4:44");
            byte[] payload = new byte[AceFrameBuilder.PayloadSize];

            byte[] validFrame = AceFrameBuilder.BuildFrame(srcMac, equipment, sv, payload);

            // 2a: Wrong magic — corrupt byte 17 (offset 0x11)
            byte[] badMagic = (byte[])validFrame.Clone();
            badMagic[17] = 0xFF; // was 0x00
            if (AceFrameBuilder.TryParseFrame(badMagic, out _, out _, out _))
            {
                Console.WriteLine("T2 FAIL: wrong magic not rejected (byte 17 corrupted)");
                fails++;
            }

            // 2b: Wrong dst MAC — corrupt first byte
            byte[] badDst = (byte[])validFrame.Clone();
            badDst[0] = 0xFF; // was 0x03
            if (AceFrameBuilder.TryParseFrame(badDst, out _, out _, out _))
            {
                Console.WriteLine("T2 FAIL: wrong dst MAC not rejected");
                fails++;
            }

            // 2c: Frame too short — 151 bytes
            byte[] shortFrame = new byte[151];
            Array.Copy(validFrame, shortFrame, 151);
            if (AceFrameBuilder.TryParseFrame(shortFrame, out _, out _, out _))
            {
                Console.WriteLine("T2 FAIL: short frame (151 bytes) not rejected");
                fails++;
            }

            if (fails == 0)
                Console.WriteLine("T2 PASS: TryParseFrame rejects invalid magic, dst, and short frame");
            return fails;
        }

        /// <summary>
        /// T3: Byte-level header conformance to PROTOCOL.md §1.2.
        /// Build a frame with src MAC 00:80:2F:23:F4:44, equipment 0x00A86F46, sv 0x13120206,
        /// zero payload (all zeros). First 28 bytes must match:
        ///   03 00 01 00 00 20 00 80 2F 23 F4 44 00 8A 03 03 08 00 D0 0D 00 A8 6F 46 13 12 02 06
        /// </summary>
        private static int Test3_HeaderConformance()
        {
            byte[] expected = {
                0x03, 0x00, 0x01, 0x00, 0x00, 0x20,  // dst
                0x00, 0x80, 0x2F, 0x23, 0xF4, 0x44,  // src (NI OUI)
                0x00, 0x8A,                          // length
                0x03, 0x03, 0x08,                     // LLC
                0x00, 0xD0, 0x0D,                     // magic
                0x00, 0xA8, 0x6F, 0x46,               // equipment
                0x13, 0x12, 0x02, 0x06                // sv
            };

            const uint equipment = 0x00A86F46;
            const uint sv = 0x13120206;
            byte[] srcMac = AceFrameBuilder.ParseMac("00:80:2F:23:F4:44");
            byte[] zeroPayload = new byte[AceFrameBuilder.PayloadSize];

            byte[] frame = AceFrameBuilder.BuildFrame(srcMac, equipment, sv, zeroPayload);

            // Compare first 28 bytes
            for (int i = 0; i < 28; i++)
            {
                if (frame[i] != expected[i])
                {
                    Console.WriteLine($"T3 FAIL: header byte mismatch at offset {i} (0x{i:X2}): expected 0x{expected[i]:X2}, got 0x{frame[i]:X2}");
                    Console.WriteLine($"  Expected first 28: {BytesToHex(expected)}");
                    Console.WriteLine($"  Got first 28:      {BytesToHex(frame.AsSpan(0, 28).ToArray())}");
                    return 1;
                }
            }

            // Also verify payload bytes 28..151 are all zero (129+ bytes)
            for (int i = 28; i < AceFrameBuilder.FrameSize; i++)
            {
                if (frame[i] != 0x00)
                {
                    Console.WriteLine($"T3 FAIL: expected zero padding at offset {i}, got 0x{frame[i]:X2}");
                    return 1;
                }
            }

            Console.WriteLine("T3 PASS: header conformance to PROTOCOL.md §1.2 (all 28 header bytes match)");
            return 0;
        }

        /// <summary>
        /// T4: COM port graceful error — Open on nonexistent port must return non-empty error string
        /// and IsOpen must be false. (No COM port hardware required.)
        /// </summary>
        private static int Test4_ComGracefulError()
        {
            using var emitter = new ComSerialEmitter();

            string error = emitter.Open("COM_NONEXISTENT_999");

            if (string.IsNullOrEmpty(error))
            {
                Console.WriteLine("T4 FAIL: Open on nonexistent port returned empty string (expected error)");
                return 1;
            }
            if (emitter.IsOpen)
            {
                Console.WriteLine("T4 FAIL: IsOpen is true after failed Open");
                return 1;
            }

            Console.WriteLine($"T4 PASS: COM graceful error: '{error}'");
            return 0;
        }

        /// <summary>
        /// T5: Manual edit → wire path (DEFECT FIX). A value committed through the
        /// provider's override sink (IManualSignalOverride — what LanTab.CommitEdit
        /// now writes into) MUST appear in the next GetSuperframe payload at the
        /// signal's wire position payload[ByteOffset − 4] (кадр 0x18 + ByteOffset)
        /// with its wire encoding. Byte evidence printed.
        /// Offset law (real cRIO): «Pump A Pressure» O=4 → payload[0]; «Side A Kicked Out?»
        /// O=20 → payload[16]; «Cmnt Ops Track Descript» (UC sz=120 @off=8 ⇒ payload_index 4,
        /// 4+120=124) ТЕПЕРЬ передаваем — позитивный тест: override 1.0 пишет ASCII '1'
        /// NUL-padded на payload[4]. Плюс ClearOverride revert.
        /// </summary>
        private static int Test5_ManualEditReachesWire()
        {
            int fails = 0;
            const uint svPump = 0x13120206; // Lnd Pump Sect
            const uint svOps  = 0x1312020F; // Lnd Ops Track (UC sz=120 живёт здесь)
            const uint equipment = 0x00A86F46;

            var provider = new ManualOverridePayloadProvider(new AcePayloadProvider());

            // 5a: FF signal — "Pump A Pressure" (ByteOffset=4, Size=4, FF, das=1.0 ⇒ payload_index 0)
            if (!provider.CanTransmit(svPump, "Pump A Pressure", out _))
            {
                Console.WriteLine("T5 FAIL: Pump A Pressure must be transmittable");
                return 1;
            }

            // clean baseline (no overrides)
            var cleanVectors = provider.GetSuperframe(equipment, 0.0);
            var cleanPump = FindVector(cleanVectors, svPump);
            if (cleanPump is null)
            {
                Console.WriteLine("T5 FAIL: superframe does not contain sv 0x13120206");
                return 1;
            }
            float cleanValue = BinaryPrimitives.ReadSingleBigEndian(cleanPump.Payload.AsSpan(0, 4));

            // commit an operator-typed edit (what LanTab.CommitEdit now does)
            provider.SetOverride(svPump, "Pump A Pressure", 123.45);

            var vectors = provider.GetSuperframe(equipment, 0.0);
            var v = FindVector(vectors, svPump);
            if (v is null || v.Payload.Length != ManualOverridePayloadProvider.PayloadLength)
            {
                Console.WriteLine("T5 FAIL: payload missing or wrong size after override");
                return 1;
            }
            float edited = BinaryPrimitives.ReadSingleBigEndian(v.Payload.AsSpan(0, 4));
            Console.WriteLine($"T5 EVIDENCE: 'Pump A Pressure' edit 123.45 (O=4) → payload[0] bytes {BytesToHex(v.Payload.AsSpan(0, 4).ToArray())} = {edited.ToString("R")}");
            if (Math.Abs(edited - 123.45f) > 1e-3)
            {
                Console.WriteLine($"T5 FAIL: edited value at payload[0] (O=4) is {edited.ToString("R")}, expected 123.45");
                fails++;
            }

            // 5b: HX signal — "Side A Kicked Out?" (ByteOffset=20, Size=4, HX ⇒ payload_index 16)
            provider.SetOverride(svPump, "Side A Kicked Out?", 3.0);
            vectors = provider.GetSuperframe(equipment, 0.0);
            v = FindVector(vectors, svPump);
            uint hexVal = BinaryPrimitives.ReadUInt32BigEndian(v!.Payload.AsSpan(16, 4));
            Console.WriteLine($"T5 EVIDENCE: 'Side A Kicked Out?' edit 3 (O=20) → payload[16] bytes {BytesToHex(v.Payload.AsSpan(16, 4).ToArray())} (u32 BE)");
            if (hexVal != 3)
            {
                Console.WriteLine($"T5 FAIL: HX override at payload[16] (O=20) is 0x{hexVal:X8}, expected 0x00000003");
                fails++;
            }

            // 5c: "Cmnt Ops Track Descript" (UC, ByteOffset=8, Size=120 ⇒ payload_index 4; 4+120=124)
            //     ТЕПЕРЬ передаваем (реальный cRIO шлёт его). Позитивный тест: override 1.0
            //     должен записать ASCII '1' NUL-padded на payload[4].
            if (!provider.CanTransmit(svOps, "Cmnt Ops Track Descript", out string reason))
            {
                Console.WriteLine($"T5 FAIL: Cmnt Ops Track Descript должен быть передаваем: '{reason}'");
                fails++;
            }
            provider.SetOverride(svOps, "Cmnt Ops Track Descript", 1.0);
            var opsVectors = provider.GetSuperframe(equipment, 0.0);
            var opsFrame = FindVector(opsVectors, svOps);
            if (opsFrame is null || opsFrame.Payload.Length != ManualOverridePayloadProvider.PayloadLength)
            {
                Console.WriteLine("T5 FAIL: sv 0x1312020F payload missing after UC override");
                fails++;
            }
            else
            {
                byte[] descBytes = opsFrame.Payload.AsSpan(4, 120).ToArray();
                Console.WriteLine($"T5 EVIDENCE: 'Cmnt Ops Track Descript' 1.0 (O=8) → payload[4] {BytesToHex(descBytes.AsSpan(0, 16).ToArray())}… (UC sz120, '1' NUL-padded)");
                if (descBytes[0] != (byte)'1' || descBytes[1] != 0x00)
                {
                    Console.WriteLine("T5 FAIL: UC override не записал ASCII '1' NUL-padded на payload[4]");
                    fails++;
                }
            }

            // 5d: ClearOverride ⇒ base value returns
            provider.ClearOverride(svPump, "Pump A Pressure");
            vectors = provider.GetSuperframe(equipment, 0.0);
            v = FindVector(vectors, svPump);
            float reverted = BinaryPrimitives.ReadSingleBigEndian(v!.Payload.AsSpan(0, 4));
            Console.WriteLine($"T5 EVIDENCE: ClearOverride ⇒ payload[0] (O=4) = {reverted.ToString("R")} (baseline {cleanValue.ToString("R")})");
            if (Math.Abs(reverted - cleanValue) > 1e-6)
            {
                Console.WriteLine("T5 FAIL: ClearOverride did not revert to the base value");
                fails++;
            }

            if (fails == 0)
                Console.WriteLine("T5 PASS: manual edit → wire path (byte evidence at payload[ByteOffset−4])");
            return fails;
        }

        /// <summary>
        /// T6: firmware_registry (14 SV-таблиц Beartooth) загружен в LantableRegistry.
        /// Каждая из 14 новых таблиц существует, содержит ≥1 сигнал и умещается в 124 Б
        /// ((ByteOffset−4)+Size ≤ 124 — offset law: payload_index = ByteOffset − 4).
        /// Плюс проверка: legacy 6 sv всё ещё на месте
        /// (мерж аддитивный, приоритет у старых таблиц).
        /// </summary>
        private static int Test6_FirmwareRegistryLoads()
        {
            int fails = 0;
            uint[] firmwareSvs =
            {
                0x13120212, 0x13120215, 0x13120218, 0x1312021B, 0x1312021E, 0x13120221,
                0x13120224, 0x13120227, 0x1312022A, 0x1312022D, 0x13120230, 0x13120233,
                0x13120236, 0x13120239
            };
            uint[] legacySvs = { 0x13120203, 0x13120206, 0x13120200, 0x13120209, 0x1312020C, 0x1312020F };

            var lantable = new LantableRegistry();

            foreach (uint sv in firmwareSvs)
            {
                var svDef = lantable.GetBySvNumber(sv);
                if (svDef is null)
                {
                    Console.WriteLine($"T6 FAIL: firmware SV 0x{sv:X8} not loaded in LantableRegistry");
                    fails++;
                    continue;
                }
                if (svDef.Signals.Length < 1)
                {
                    Console.WriteLine($"T6 FAIL: firmware SV 0x{sv:X8} ('{svDef.Name}') has 0 signals");
                    fails++;
                    continue;
                }
                foreach (var sig in svDef.Signals)
                {
                    // Offset law: payload_index = ByteOffset − 4; (O−4)+Size ≤ 124.
                    if (sig.ByteOffset < 4 || (sig.ByteOffset - 4) + sig.Size > 124)
                    {
                        Console.WriteLine($"T6 FAIL: SV 0x{sv:X8} signal '{sig.Name}' overflows 124 B (O {sig.ByteOffset} ⇒ payload_index {sig.ByteOffset - 4} + sz {sig.Size})");
                        fails++;
                    }
                }
            }

            foreach (uint sv in legacySvs)
            {
                if (lantable.GetBySvNumber(sv) is null)
                {
                    Console.WriteLine($"T6 FAIL: legacy SV 0x{sv:X8} missing after firmware merge");
                    fails++;
                }
            }

            if (fails == 0)
                Console.WriteLine($"T6 PASS: {firmwareSvs.Length} firmware tables loaded (all ≤124 B), 6 legacy SV intact");
            return fails;
        }

        /// <summary>
        /// T7: unit-конверсия (R7). Множители native→target: bbl/min→gpm ×42, °F→°C (v−32)×5/9,
        /// ppg→g/cc ×0.119826, ft→m ×0.3048, psi→bar ×0.0689476; ToWire обратная; identity для
        /// уже-целевой единицы; GroupOf классифицирует известные единицы.
        /// </summary>
        private static int Test7_UnitConversion()
        {
            int fails = 0;

            void Check(string what, double expected, double actual, double tol = 1e-9)
            {
                if (Math.Abs(actual - expected) > tol)
                {
                    Console.WriteLine($"T7 FAIL: {what}: expected {expected.ToString("R")}, got {actual.ToString("R")}");
                    fails++;
                }
            }

            Check("ToTarget(1.0, Flow, bbl/min, FirmwareDefault) = 42.0 gpm",
                42.0, UnitConversion.ToTarget(1.0, UnitConversion.UnitGroup.Flow, "bbl/min", UnitPreset.FirmwareDefault));
            Check("ToTarget(212, Temperature, °F, FirmwareDefault) = 100.0 °C",
                100.0, UnitConversion.ToTarget(212, UnitConversion.UnitGroup.Temperature, "°F", UnitPreset.FirmwareDefault));
            Check("ToTarget(1.0, Density, ppg, Metric) = 0.119826 g/cc",
                0.119826, UnitConversion.ToTarget(1.0, UnitConversion.UnitGroup.Density, "ppg", UnitPreset.Metric), 1e-6);
            Check("ToTarget(1.0, Level, ft, Metric) = 0.3048 m",
                0.3048, UnitConversion.ToTarget(1.0, UnitConversion.UnitGroup.Level, "ft", UnitPreset.Metric));
            Check("ToTarget(1.0, Pressure, psi, Metric) = 0.0689476 bar",
                0.0689476, UnitConversion.ToTarget(1.0, UnitConversion.UnitGroup.Pressure, "psi", UnitPreset.Metric), 1e-6);
            Check("ToWire(42.0, Flow, bbl/min, FirmwareDefault) = 1.0 (preset→native)",
                1.0, UnitConversion.ToWire(42.0, UnitConversion.UnitGroup.Flow, "bbl/min", UnitPreset.FirmwareDefault));
            Check("identity ToTarget(3.0, Flow, gpm, FirmwareDefault) = 3.0",
                3.0, UnitConversion.ToTarget(3.0, UnitConversion.UnitGroup.Flow, "gpm", UnitPreset.FirmwareDefault));

            if (UnitConversion.GroupOf("bbl/min") != UnitConversion.UnitGroup.Flow)
            { Console.WriteLine("T7 FAIL: GroupOf(\"bbl/min\") != Flow"); fails++; }
            if (UnitConversion.GroupOf("°F") != UnitConversion.UnitGroup.Temperature)
            { Console.WriteLine("T7 FAIL: GroupOf(\"°F\") != Temperature"); fails++; }
            if (UnitConversion.GroupOf("psi") != UnitConversion.UnitGroup.Pressure)
            { Console.WriteLine("T7 FAIL: GroupOf(\"psi\") != Pressure"); fails++; }
            if (UnitConversion.GroupOf("char") != UnitConversion.UnitGroup.None)
            { Console.WriteLine("T7 FAIL: GroupOf(\"char\") != None"); fails++; }

            if (fails == 0)
                Console.WriteLine("T7 PASS: unit conversion 42 / (v−32)*5/9 / 0.119826 / 0.3048 / 0.0689476 + GroupOf");
            return fails;
        }

        /// <summary>
        /// T8: суперфрейм по умолчанию = 6 legacy sv (как реальный cRIO-дамп); при
        /// IncludeFirmwareTables=true — 20 sv (6 legacy в прежнем порядке + 14 firmware
        /// по возрастанию), каждый payload ровно 124 Б. Затем конверсия НА ПРОВОДЕ: фейковый
        /// IAcePayloadProvider (один вектор sv=0x13120200, payload с float32BE(1.0) на payload[4]
        /// — «Recirc Flow Rate», нативная единица bbl/min; O=8 ⇒ payload_index 4) оборачивается
        /// в UnitConvertingPayloadProvider: FirmwareDefault ⇒ байты на payload[4] == float32BE(42.0)
        /// (bbl/min→gpm ×42); Metric ⇒ float32BE(0.158987) (bbl/min→m³/min).
        /// </summary>
        private static int Test8_SuperframeFirmwareSvs()
        {
            int fails = 0;
            const uint equipment = 0x00A86F46;

            uint[] legacySvs =
            {
                0x13120203, 0x13120206, 0x13120200, 0x13120209, 0x1312020C, 0x1312020F
            };
            uint[] firmwareSvs =
            {
                0x13120212, 0x13120215, 0x13120218, 0x1312021B, 0x1312021E, 0x13120221,
                0x13120224, 0x13120227, 0x1312022A, 0x1312022D, 0x13120230, 0x13120233,
                0x13120236, 0x13120239
            };

            // По умолчанию — только 6 legacy (firmware-таблицы off, реальный cRIO-дамп).
            var p = new AcePayloadProvider();
            var vectors = p.GetSuperframe(equipment, 0.0);
            if (vectors.Count != 6)
            {
                Console.WriteLine($"T8 FAIL: default superframe count = {vectors.Count}, expected 6 (legacy only)");
                return 1;
            }
            for (int i = 0; i < legacySvs.Length; i++)
            {
                if (vectors[i].SvNumber != legacySvs[i])
                {
                    Console.WriteLine($"T8 FAIL: default sv order[{i}] = 0x{vectors[i].SvNumber:X8}, expected 0x{legacySvs[i]:X8}");
                    fails++;
                }
                if (vectors[i].Payload.Length != 124)
                {
                    Console.WriteLine($"T8 FAIL: sv 0x{vectors[i].SvNumber:X8} payload length = {vectors[i].Payload.Length}, expected 124");
                    fails++;
                }
            }

            // IncludeFirmwareTables=true ⇒ 20 sv: 6 legacy + 14 firmware по возрастанию.
            var pFull = new AcePayloadProvider { IncludeFirmwareTables = true };
            var full = pFull.GetSuperframe(equipment, 0.0);
            if (full.Count != 20)
            {
                Console.WriteLine($"T8 FAIL: full superframe count = {full.Count}, expected 20 (6+14)");
                return 1;
            }
            for (int i = 0; i < legacySvs.Length; i++)
            {
                if (full[i].SvNumber != legacySvs[i])
                {
                    Console.WriteLine($"T8 FAIL: full sv order[{i}] = 0x{full[i].SvNumber:X8}, expected legacy 0x{legacySvs[i]:X8}");
                    fails++;
                }
            }
            for (int i = 0; i < firmwareSvs.Length; i++)
            {
                int idx = legacySvs.Length + i;
                if (full[idx].SvNumber != firmwareSvs[i])
                {
                    Console.WriteLine($"T8 FAIL: full sv order[{idx}] = 0x{full[idx].SvNumber:X8}, expected firmware 0x{firmwareSvs[i]:X8}");
                    fails++;
                }
                if (full[idx].Payload.Length != 124)
                {
                    Console.WriteLine($"T8 FAIL: firmware sv 0x{full[idx].SvNumber:X8} payload length = {full[idx].Payload.Length}, expected 124");
                    fails++;
                }
            }

            // ── конверсия на проводе (юнит-тест декоратора) ──
            // «Recirc Flow Rate» O=8 ⇒ payload_index 4 (bbl/min); «Recirc Density» O=4 ⇒ payload_index 0 (lb/gal).
            var fwPayload = new byte[124];
            BinaryPrimitives.WriteSingleBigEndian(fwPayload.AsSpan(4, 4), 1.0f); // Recirc Flow Rate = 1 bbl/min
            var fwConv = new UnitConvertingPayloadProvider(
                new FakeSingleSvProvider(fwPayload, 0x13120200), UnitPreset.FirmwareDefault);
            var fwVectors = fwConv.GetSuperframe(equipment, 0.0);
            if (fwVectors.Count != 1)
            {
                Console.WriteLine("T8 FAIL: UnitConvertingPayloadProvider returned unexpected vector count");
                return 1;
            }
            float fwGpm = BinaryPrimitives.ReadSingleBigEndian(fwVectors[0].Payload.AsSpan(4, 4));
            if (Math.Abs(fwGpm - 42.0f) > 1e-4)
            {
                Console.WriteLine($"T8 FAIL: wire bytes @payload[4] (O=8) under FirmwareDefault = {fwGpm.ToString("R")}, expected 42.0 (1 bbl/min → 42 gpm)");
                fails++;
            }

            var metricPayload = new byte[124];
            BinaryPrimitives.WriteSingleBigEndian(metricPayload.AsSpan(4, 4), 1.0f);
            var metricConv = new UnitConvertingPayloadProvider(
                new FakeSingleSvProvider(metricPayload, 0x13120200), UnitPreset.Metric);
            var metricVectors = metricConv.GetSuperframe(equipment, 0.0);
            if (metricVectors.Count != 1)
            {
                Console.WriteLine("T8 FAIL: UnitConvertingPayloadProvider (Metric) returned unexpected vector count");
                return 1;
            }
            float metricVal = BinaryPrimitives.ReadSingleBigEndian(metricVectors[0].Payload.AsSpan(4, 4));
            if (Math.Abs(metricVal - 0.158987f) > 1e-6)
            {
                Console.WriteLine($"T8 FAIL: wire bytes @payload[4] (O=8) under Metric = {metricVal.ToString("R")}, expected 0.158987 (1 bbl/min → m³/min)");
                fails++;
            }

            if (fails == 0)
                Console.WriteLine("T8 PASS: default 6-sv superframe, full 20-sv (order + 124 B), wire conversion bbl/min→gpm / bbl/min→m³/min");
            return fails;
        }

        /// <summary>
        /// T9: UnitSettings (вкладка «Единицы») — пресет + переопределения групп.
        /// Metric: TargetUnit(Flow)="m³/min", TargetUnit(Pressure)="bar"; после
        /// FlowOverride="gpm" TargetUnit(Flow)="gpm"; общий Convert: bbl/min→gpm ×42,
        /// °F→°C аффинно (=100); LanNative — identity (нативная == целевая);
        /// Metric без override — 1 bbl/min → 0.158987 m³/min; identity для одинаковых
        /// единиц; GroupOf: mA/bpm/char → None (bpm НЕ конвертируется — осознанно),
        /// m → Level, psi → Pressure.
        /// </summary>
        private static int Test9_UnitSettingsOverrides()
        {
            int fails = 0;

            void CheckNum(string what, double expected, double actual, double tol)
            {
                if (Math.Abs(actual - expected) > tol)
                {
                    Console.WriteLine($"T9 FAIL: {what}: expected {expected.ToString("R")}, got {actual.ToString("R")}");
                    fails++;
                }
                else
                {
                    Console.WriteLine($"T9 PASS: {what} = {actual.ToString("R")}");
                }
            }

            void CheckStr(string what, string expected, string actual)
            {
                if (actual != expected)
                {
                    Console.WriteLine($"T9 FAIL: {what}: expected '{expected}', got '{actual}'");
                    fails++;
                }
                else
                {
                    Console.WriteLine($"T9 PASS: {what} = '{actual}'");
                }
            }

            // 9a: пресет Metric без переопределений
            var s = new UnitSettings { Preset = UnitPreset.Metric };
            CheckStr("TargetUnit(Flow) при Metric", "m³/min", s.TargetUnit(UnitConversion.UnitGroup.Flow));
            CheckStr("TargetUnit(Pressure) при Metric", "bar", s.TargetUnit(UnitConversion.UnitGroup.Pressure));

            // 9b: переопределение группы Flow
            s.FlowOverride = "gpm";
            CheckStr("TargetUnit(Flow) после FlowOverride=\"gpm\"", "gpm", s.TargetUnit(UnitConversion.UnitGroup.Flow));

            // 9c: общий Convert
            CheckNum("Convert(1.0, \"bbl/min\", \"gpm\")", 42.0, UnitConversion.Convert(1.0, "bbl/min", "gpm"), 1e-9);
            CheckNum("Convert(212.0, \"°F\", \"°C\")", 100.0, UnitConversion.Convert(212.0, "°F", "°C"), 1e-9);

            // 9d: ToTarget через UnitSettings
            var sLan = new UnitSettings { Preset = UnitPreset.LanNative };
            CheckNum("ToTarget(3.0, \"bbl/min\", LanNative) — identity", 3.0, UnitConversion.ToTarget(3.0, "bbl/min", sLan), 1e-9);
            var sMetric = new UnitSettings { Preset = UnitPreset.Metric };
            CheckNum("ToTarget(1.0, \"bbl/min\", Metric) → m³/min", 0.158987, UnitConversion.ToTarget(1.0, "bbl/min", sMetric), 1e-6);

            // 9e: identity единицы и классификация групп
            CheckNum("Convert(5.0, \"psi\", \"psi\") — identity", 5.0, UnitConversion.Convert(5.0, "psi", "psi"), 1e-9);
            if (UnitConversion.GroupOf("mA") != UnitConversion.UnitGroup.None)
            { Console.WriteLine("T9 FAIL: GroupOf(\"mA\") != None"); fails++; }
            else Console.WriteLine("T9 PASS: GroupOf(\"mA\") = None");
            if (UnitConversion.GroupOf("bpm") != UnitConversion.UnitGroup.None)
            { Console.WriteLine("T9 FAIL: GroupOf(\"bpm\") != None (должно быть неконвертируемым)"); fails++; }
            else Console.WriteLine("T9 PASS: GroupOf(\"bpm\") = None");
            if (UnitConversion.GroupOf("char") != UnitConversion.UnitGroup.None)
            { Console.WriteLine("T9 FAIL: GroupOf(\"char\") != None"); fails++; }
            else Console.WriteLine("T9 PASS: GroupOf(\"char\") = None");
            if (UnitConversion.GroupOf("m") != UnitConversion.UnitGroup.Level)
            { Console.WriteLine("T9 FAIL: GroupOf(\"m\") != Level"); fails++; }
            else Console.WriteLine("T9 PASS: GroupOf(\"m\") = Level");
            if (UnitConversion.GroupOf("psi") != UnitConversion.UnitGroup.Pressure)
            { Console.WriteLine("T9 FAIL: GroupOf(\"psi\") != Pressure"); fails++; }
            else Console.WriteLine("T9 PASS: GroupOf(\"psi\") = Pressure");

            if (fails == 0)
                Console.WriteLine("T9 PASS: UnitSettings (пресет + переопределения) — все проверки зелёные");
            return fails;
        }

        /// <summary>
        /// T10: COM-конверсия — глобальная настройка единиц (UnitsTab.Settings) доходит до
        /// COM-потока. Воспроизводит цикл TrayApp.OnTick (ToTarget если IsConvertible) на
        /// фейковом шаблоне с TemplateVariable UoM="gpm", значения {1.0}: при Metric →
        /// ≈0.00378541 (1 gpm → m³/min), при FirmwareDefault → identity 1.0 (gpm == целевая).
        /// </summary>
        private static int Test10_ComConversion()
        {
            int fails = 0;
            var template = new DeviceTemplate
            {
                Name = "T10 fake",
                DeviceType = "FLECS",
                Variables = new[]
                {
                    new TemplateVariable
                    {
                        Index = 0, Name = "Flow Test", LegacyName = "Flow Test", UoM = "gpm",
                        BaseValue = 1.0, Amplitude = 0.0, Frequency = 0.0, Format = string.Empty,
                        TogglePeriod = 0.0, ExcludeFromTelemetry = false
                    }
                }
            };

            // Тот же цикл, что в TrayApp.OnTick (глобальные единицы → COM-значения).
            static double RunTick(DeviceTemplate t, double value, UnitSettings units)
            {
                var vals = new[] { value };
                var vars = t.Variables;
                for (int i = 0; i < vals.Length && i < vars.Length; i++)
                    if (UnitConversion.IsConvertible(vars[i].UoM))
                        vals[i] = UnitConversion.ToTarget(vals[i], vars[i].UoM, units);
                return vals[0];
            }

            double m = RunTick(template, 1.0, new UnitSettings { Preset = UnitPreset.Metric });
            if (Math.Abs(m - 0.00378541) > 1e-8)
            {
                Console.WriteLine($"T10 FAIL: COM при Metric = {m.ToString("R")}, expected ≈0.00378541 (1 gpm → m³/min)");
                fails++;
            }
            else
            {
                Console.WriteLine($"T10 PASS: COM при Metric = {m.ToString("R")} (1 gpm → 0.00378541 m³/min)");
            }

            double f = RunTick(template, 1.0, new UnitSettings { Preset = UnitPreset.FirmwareDefault });
            if (Math.Abs(f - 1.0) > 1e-12)
            {
                Console.WriteLine($"T10 FAIL: COM при FirmwareDefault = {f.ToString("R")}, expected identity 1.0 (gpm == целевая)");
                fails++;
            }
            else
            {
                Console.WriteLine($"T10 PASS: COM при FirmwareDefault = {f.ToString("R")} (identity, gpm == целевая)");
            }

            if (fails == 0)
                Console.WriteLine("T10 PASS: глобальные единицы (UnitsTab) применяются к COM-значениям");
            return fails;
        }

        /// <summary>
        /// T11: матрица конверсии единиц (R7) — полный набор множителей между каноническими
        /// единицами групп: Flow/Volume/Density/Level/Pressure — линейные, Temperature — аффинная.
        /// Допуски: «≈»-случаи — относительный 1e-5 (публичные множители даны с 5–6 значащими
        /// цифрами, их точные обратные отличаются до ~4e-6: 1/0.119826=8.34543 vs 8.3454,
        /// 1/0.158987=6.28982 vs 6.28981); «==»-случаи — 1e-9; температура — 1e-9, точные — 0.
        /// </summary>
        private static int Test11_ConvertMatrix()
        {
            int fails = 0;

            // Относительный допуск (scale = max(1, |expected|)): маленькие/точные величины
            // сравниваются почти как абсолютные, большие — как относительные.
            void Check(string what, double expected, double actual, double relTol)
            {
                double scale = Math.Max(1.0, Math.Abs(expected));
                if (Math.Abs(actual - expected) > relTol * scale)
                {
                    Console.WriteLine($"T11 FAIL: {what}: expected {expected.ToString("R")}, got {actual.ToString("R")}");
                    fails++;
                }
            }

            // Flow (канон bbl/min; 1 bbl = 42 gal = 0.158987 m³)
            Check("(1, bbl/min, gpm)", 42.0, UnitConversion.Convert(1.0, "bbl/min", "gpm"), 1e-9);
            Check("(42, gpm, bbl/min)", 1.0, UnitConversion.Convert(42.0, "gpm", "bbl/min"), 1e-9);
            Check("(1, bbl/min, m3/min)", 0.158987, UnitConversion.Convert(1.0, "bbl/min", "m3/min"), 1e-5);
            Check("(1, gpm, m3/min)", 0.00378541, UnitConversion.Convert(1.0, "gpm", "m3/min"), 1e-5);
            Check("(1, m3/min, gpm)", 264.172, UnitConversion.Convert(1.0, "m3/min", "gpm"), 1e-5);
            Check("(1, m3/min, bbl/min)", 6.28981, UnitConversion.Convert(1.0, "m3/min", "bbl/min"), 1e-5);

            // Volume (канон bbl)
            Check("(1, bbl, gal)", 42.0, UnitConversion.Convert(1.0, "bbl", "gal"), 1e-9);
            Check("(1, gal, bbl)", 1.0 / 42.0, UnitConversion.Convert(1.0, "gal", "bbl"), 1e-9);
            Check("(1, bbl, m3)", 0.158987, UnitConversion.Convert(1.0, "bbl", "m3"), 1e-5);
            Check("(1, gal, m3)", 0.00378541, UnitConversion.Convert(1.0, "gal", "m3"), 1e-5);

            // Density (канон ppg ≡ lb/gal)
            Check("(1, ppg, g/cc)", 0.119826, UnitConversion.Convert(1.0, "ppg", "g/cc"), 1e-5);
            Check("(1, g/cc, ppg)", 8.3454, UnitConversion.Convert(1.0, "g/cc", "ppg"), 1e-5);
            Check("(1, ppg, kg/m3)", 119.826, UnitConversion.Convert(1.0, "ppg", "kg/m3"), 1e-5);
            Check("(1, g/cc, kg/m3)", 1000.0, UnitConversion.Convert(1.0, "g/cc", "kg/m3"), 1e-9);

            // Level (канон ft)
            Check("(1, ft, m)", 0.3048, UnitConversion.Convert(1.0, "ft", "m"), 1e-9);
            Check("(1, m, ft)", 3.28084, UnitConversion.Convert(1.0, "m", "ft"), 1e-5);

            // Pressure (канон psi)
            Check("(1, psi, bar)", 0.0689476, UnitConversion.Convert(1.0, "psi", "bar"), 1e-5);
            Check("(1, psi, kpa)", 6.89476, UnitConversion.Convert(1.0, "psi", "kpa"), 1e-5);
            Check("(1, psi, mpa)", 0.00689476, UnitConversion.Convert(1.0, "psi", "mpa"), 1e-5);
            Check("(1, bar, kpa)", 100.0, UnitConversion.Convert(1.0, "bar", "kpa"), 1e-9);

            // Temperature (аффинная)
            Check("(212, °f, °c)", 100.0, UnitConversion.Convert(212.0, "°f", "°c"), 1e-9);
            Check("(100, °c, °f)", 212.0, UnitConversion.Convert(100.0, "°c", "°f"), 1e-9);
            Check("(-40, °f, °c)", -40.0, UnitConversion.Convert(-40.0, "°f", "°c"), 0.0);
            Check("(32, °f, °c)", 0.0, UnitConversion.Convert(32.0, "°f", "°c"), 0.0);

            if (fails == 0)
                Console.WriteLine("T11 PASS: матрица конверсии (Flow/Volume/Density/Level/Pressure/Temp)");
            return fails;
        }

        /// <summary>
        /// T12: алиасы и регистр — GroupOf/CanonicalUom распознают gpm/ppg/psia/degF/degC
        /// регистронезависимо, пробелы обрезаются; псевдонимы дают те же множители, что и эталон.
        /// Примечание: Convert(1,"m3","gal") = 264.172 (1 м³ = 264.172 гал; 42 гал = 1 bbl) —
        /// согласуется с T11 (1,"gal","m3")≈0.00378541 и (1,"m3/min","gpm")≈264.172.
        /// </summary>
        private static int Test12_AliasAndCase()
        {
            int fails = 0;

            void Check(string what, double expected, double actual, double relTol)
            {
                double scale = Math.Max(1.0, Math.Abs(expected));
                if (Math.Abs(actual - expected) > relTol * scale)
                {
                    Console.WriteLine($"T12 FAIL: {what}: expected {expected.ToString("R")}, got {actual.ToString("R")}");
                    fails++;
                }
            }

            if (UnitConversion.GroupOf("GPM") != UnitConversion.UnitGroup.Flow)
            { Console.WriteLine("T12 FAIL: GroupOf(\"GPM\") != Flow"); fails++; }
            if (UnitConversion.GroupOf("PPg") != UnitConversion.UnitGroup.Density)
            { Console.WriteLine("T12 FAIL: GroupOf(\"PPg\") != Density"); fails++; }
            if (UnitConversion.GroupOf("PSI") != UnitConversion.UnitGroup.Pressure)
            { Console.WriteLine("T12 FAIL: GroupOf(\"PSI\") != Pressure"); fails++; }
            if (UnitConversion.GroupOf("KPA") != UnitConversion.UnitGroup.Pressure)
            { Console.WriteLine("T12 FAIL: GroupOf(\"KPA\") != Pressure"); fails++; }
            if (UnitConversion.GroupOf(" degf ") != UnitConversion.UnitGroup.Temperature)
            { Console.WriteLine("T12 FAIL: GroupOf(\" degf \") != Temperature (trim)"); fails++; }

            Check("(1, gpm, bbl/min)", 1.0 / 42.0, UnitConversion.Convert(1.0, "gpm", "bbl/min"), 1e-9);
            Check("(1, lb/gal, g/cc)", 0.119826, UnitConversion.Convert(1.0, "lb/gal", "g/cc"), 1e-5);
            Check("(1, g/cm3, ppg)", 8.3454, UnitConversion.Convert(1.0, "g/cm3", "ppg"), 1e-5);
            Check("(1, kg/m3, ppg)", 1.0 / 119.826, UnitConversion.Convert(1.0, "kg/m3", "ppg"), 1e-5);
            Check("(1, m3, gal)", 264.172, UnitConversion.Convert(1.0, "m3", "gal"), 1e-5);
            Check("(1, psia, bar)", 0.0689476, UnitConversion.Convert(1.0, "psia", "bar"), 1e-5);
            Check("(1, degf, °c) == (1, f, °c)",
                UnitConversion.Convert(1.0, "f", "°c"), UnitConversion.Convert(1.0, "degf", "°c"), 1e-12);

            if (fails == 0)
                Console.WriteLine("T12 PASS: алиасы/регистр (GPM/PPg/PSI/KPA/degF/psia/trim)");
            return fails;
        }

        /// <summary>
        /// T13: неконвертируемые единицы — GroupOf == None ⇒ Convert identity.
        /// Регресс-пины: bpm НЕ алиас, m ≠ mA, "m3"/"m3/min" конвертируемые, null UoM — identity.
        /// </summary>
        private static int Test13_NonConvertible()
        {
            int fails = 0;

            void CheckGroup(string uom)
            {
                if (UnitConversion.GroupOf(uom) != UnitConversion.UnitGroup.None)
                {
                    Console.WriteLine($"T13 FAIL: GroupOf(\"{uom}\") != None");
                    fails++;
                }
            }

            void Check(string what, double expected, double actual, double tol)
            {
                if (Math.Abs(actual - expected) > tol)
                {
                    Console.WriteLine($"T13 FAIL: {what}: expected {expected.ToString("R")}, got {actual.ToString("R")}");
                    fails++;
                }
            }

            CheckGroup("mA");
            CheckGroup("none");
            CheckGroup("char");
            CheckGroup("%");
            CheckGroup("Hz");
            CheckGroup("bpm");          // регресс-пин: pump-rate единица прошивки
            CheckGroup("sacks");
            CheckGroup("f3/sack");      // составная (не из белого списка)
            CheckGroup("gal/sack");
            CheckGroup("psi/ma");
            CheckGroup("ft/min");
            CheckGroup("");
            CheckGroup("mA");           // повтор: m/mA различимы

            if (UnitConversion.GroupOf("m") != UnitConversion.UnitGroup.Level)
            { Console.WriteLine("T13 FAIL: GroupOf(\"m\") != Level"); fails++; }
            if (UnitConversion.GroupOf("m3") != UnitConversion.UnitGroup.Volume)
            { Console.WriteLine("T13 FAIL: GroupOf(\"m3\") != Volume"); fails++; }
            if (UnitConversion.GroupOf("m3/min") != UnitConversion.UnitGroup.Flow)
            { Console.WriteLine("T13 FAIL: GroupOf(\"m3/min\") != Flow"); fails++; }

            Check("Convert(5.0, mA, gpm)", 5.0, UnitConversion.Convert(5.0, "mA", "gpm"), 1e-12);
            Check("Convert(5.0, bpm, gpm)", 5.0, UnitConversion.Convert(5.0, "bpm", "gpm"), 1e-12);
            Check("Convert(5.0, null, °c)", 5.0, UnitConversion.Convert(5.0, null!, "°c"), 1e-12);

            if (fails == 0)
                Console.WriteLine("T13 PASS: неконвертируемые единицы — identity (bpm/mA/char/составные)");
            return fails;
        }

        /// <summary>
        /// T14: roundtrip native → целевая (ToTarget) → native (ToWire) для каждой конвертируемой
        /// группы и каждого из 4 пресетов. |w−v|/|v| &lt; 1e-9 (для v &gt; 0).
        /// Плюс −40°F roundtrip точно и 98.6°F → °C → °F в пределах 1e-9.
        /// </summary>
        private static int Test14_RoundTrip()
        {
            int fails = 0;

            var cases = new (UnitConversion.UnitGroup Group, string Native, double Value)[]
            {
                (UnitConversion.UnitGroup.Flow,       "bbl/min", 3.0),
                (UnitConversion.UnitGroup.Temperature, "°f",     98.6),
                (UnitConversion.UnitGroup.Density,    "ppg",     15.8),
                (UnitConversion.UnitGroup.Volume,     "bbl",     120.0),
                (UnitConversion.UnitGroup.Level,      "ft",      6.5),
                (UnitConversion.UnitGroup.Pressure,   "psi",     2500.0)
            };
            UnitPreset[] presets =
            {
                UnitPreset.FirmwareDefault, UnitPreset.LanNative, UnitPreset.Metric, UnitPreset.Imperial
            };

            foreach (var preset in presets)
            {
                var s = new UnitSettings { Preset = preset };
                foreach (var c in cases)
                {
                    double t = UnitConversion.ToTarget(c.Value, c.Native, s);
                    double w = UnitConversion.ToWire(t, c.Native, s);
                    double rel = Math.Abs(w - c.Value) / Math.Abs(c.Value);
                    if (rel >= 1e-9)
                    {
                        Console.WriteLine($"T14 FAIL: roundtrip {c.Native} ({preset}): v={c.Value.ToString("R")} → {t.ToString("R")} → {w.ToString("R")}, rel={rel.ToString("R")}");
                        fails++;
                    }
                }
            }

            // −40°F roundtrip точно
            double m40 = UnitConversion.Convert(-40.0, "°f", "°c");
            double m40Back = UnitConversion.Convert(m40, "°c", "°f");
            if (m40Back != -40.0)
            {
                Console.WriteLine($"T14 FAIL: -40°F roundtrip: got {m40Back.ToString("R")}");
                fails++;
            }

            // 98.6°F → °C → °F в пределах 1e-9
            double f98 = UnitConversion.Convert(98.6, "°f", "°c");
            double f98Back = UnitConversion.Convert(f98, "°c", "°f");
            if (Math.Abs(f98Back - 98.6) > 1e-9)
            {
                Console.WriteLine($"T14 FAIL: 98.6°F→°C→°F: got {f98Back.ToString("R")}");
                fails++;
            }

            if (fails == 0)
                Console.WriteLine("T14 PASS: roundtrip все группы × 4 пресета + -40°F точно");
            return fails;
        }

        /// <summary>
        /// T15: пресет-матрица 4×6 — PresetUnit/DisplayUnit для каждой группы каждого пресета;
        /// каждый элемент UnitsOf(g) классифицируется в свою группу; переопределение FlowOverride.
        /// </summary>
        private static int Test15_PresetMatrix()
        {
            int fails = 0;

            void CheckStr(string what, string expected, string actual)
            {
                if (actual != expected)
                {
                    Console.WriteLine($"T15 FAIL: {what}: expected '{expected}', got '{actual}'");
                    fails++;
                }
            }

            var matrix = new (UnitPreset P, UnitConversion.UnitGroup G, string Unit)[]
            {
                (UnitPreset.FirmwareDefault, UnitConversion.UnitGroup.Flow,       "gpm"),
                (UnitPreset.FirmwareDefault, UnitConversion.UnitGroup.Temperature, "°C"),
                (UnitPreset.FirmwareDefault, UnitConversion.UnitGroup.Density,    "ppg"),
                (UnitPreset.FirmwareDefault, UnitConversion.UnitGroup.Volume,     "bbl"),
                (UnitPreset.FirmwareDefault, UnitConversion.UnitGroup.Level,      "ft"),
                (UnitPreset.FirmwareDefault, UnitConversion.UnitGroup.Pressure,   "psi"),
                (UnitPreset.LanNative,       UnitConversion.UnitGroup.Flow,       "bbl/min"),
                (UnitPreset.LanNative,       UnitConversion.UnitGroup.Temperature, "°F"),
                (UnitPreset.LanNative,       UnitConversion.UnitGroup.Density,    "lb/gal"),
                (UnitPreset.LanNative,       UnitConversion.UnitGroup.Volume,     "bbl"),
                (UnitPreset.LanNative,       UnitConversion.UnitGroup.Level,      "ft"),
                (UnitPreset.LanNative,       UnitConversion.UnitGroup.Pressure,   "psi"),
                (UnitPreset.Metric,          UnitConversion.UnitGroup.Flow,       "m³/min"),
                (UnitPreset.Metric,          UnitConversion.UnitGroup.Temperature, "°C"),
                (UnitPreset.Metric,          UnitConversion.UnitGroup.Density,    "g/cc"),
                (UnitPreset.Metric,          UnitConversion.UnitGroup.Volume,     "m³"),
                (UnitPreset.Metric,          UnitConversion.UnitGroup.Level,      "m"),
                (UnitPreset.Metric,          UnitConversion.UnitGroup.Pressure,   "bar"),
                (UnitPreset.Imperial,        UnitConversion.UnitGroup.Flow,       "bbl/min"),
                (UnitPreset.Imperial,        UnitConversion.UnitGroup.Temperature, "°F"),
                (UnitPreset.Imperial,        UnitConversion.UnitGroup.Density,    "ppg"),
                (UnitPreset.Imperial,        UnitConversion.UnitGroup.Volume,     "bbl"),
                (UnitPreset.Imperial,        UnitConversion.UnitGroup.Level,      "ft"),
                (UnitPreset.Imperial,        UnitConversion.UnitGroup.Pressure,   "psi")
            };

            foreach (var m in matrix)
            {
                CheckStr($"PresetUnit({m.G}, {m.P})", m.Unit, UnitConversion.PresetUnit(m.G, m.P));
                CheckStr($"DisplayUnit({m.G}, {m.P})", m.Unit, UnitConversion.DisplayUnit(m.G, m.P));
            }

            // Каждый элемент UnitsOf(g) классифицируется в свою группу.
            foreach (UnitConversion.UnitGroup g in Enum.GetValues(typeof(UnitConversion.UnitGroup)))
            {
                if (g == UnitConversion.UnitGroup.None) continue;
                foreach (var el in UnitConversion.UnitsOf(g))
                {
                    if (UnitConversion.GroupOf(el) != g)
                    {
                        Console.WriteLine($"T15 FAIL: UnitsOf({g}) содержит '{el}', но GroupOf != {g}");
                        fails++;
                    }
                }
            }

            // Переопределение: FlowOverride="gpm" при Metric.
            var s = new UnitSettings { Preset = UnitPreset.Metric, FlowOverride = "gpm" };
            CheckStr("TargetUnit(Flow) с FlowOverride", "gpm", s.TargetUnit(UnitConversion.UnitGroup.Flow));
            CheckStr("TargetUnit(Pressure) при Metric", "bar", s.TargetUnit(UnitConversion.UnitGroup.Pressure));

            if (fails == 0)
                Console.WriteLine("T15 PASS: пресет-матрица 4×6 + UnitsOf + override");
            return fails;
        }

        /// <summary>
        /// T16: 1:1-гарантия firmware — под FirmwareDefault НИ ОДИН сигнал 14 firmware-таблиц
        /// не требует конверсии (NeedsConversion == false, проверено ~259 сигналов). Плюс
        /// legacy-проверки: bbl/min и °F конвертируются (legacy-нативы), lb/gal и g/cc — нет.
        /// </summary>
        private static int Test16_FirmwareOneToOne()
        {
            int fails = 0;
            uint[] firmwareSvs =
            {
                0x13120212, 0x13120215, 0x13120218, 0x1312021B, 0x1312021E, 0x13120221,
                0x13120224, 0x13120227, 0x1312022A, 0x1312022D, 0x13120230, 0x13120233,
                0x13120236, 0x13120239
            };

            var lantable = new LantableRegistry();
            var fwDefault = new UnitSettings { Preset = UnitPreset.FirmwareDefault };
            int checkedSignals = 0;

            foreach (uint sv in firmwareSvs)
            {
                var svDef = lantable.GetBySvNumber(sv);
                if (svDef is null)
                {
                    Console.WriteLine($"T16 FAIL: SV 0x{sv:X8} не загружен");
                    fails++;
                    continue;
                }
                foreach (var sig in svDef.Signals)
                {
                    checkedSignals++;
                    if (UnitConversion.NeedsConversion(sig.UoM, fwDefault))
                    {
                        Console.WriteLine($"T16 FAIL: SV 0x{sv:X8} сигнал '{sig.Name}' uom '{sig.UoM}' требует конверсии под FirmwareDefault");
                        fails++;
                    }
                }
            }

            // Legacy-нативы (передаются в bbl/min / °F) — конвертируются под FirmwareDefault.
            if (!UnitConversion.NeedsConversion("bbl/min", fwDefault))
            { Console.WriteLine("T16 FAIL: NeedsConversion(\"bbl/min\", FirmwareDefault) должен быть true"); fails++; }
            if (!UnitConversion.NeedsConversion("f", fwDefault))
            { Console.WriteLine("T16 FAIL: NeedsConversion(\"f\", FirmwareDefault) должен быть true"); fails++; }

            // Firmware-нативы — identity.
            if (UnitConversion.NeedsConversion("lb/gal", fwDefault))
            { Console.WriteLine("T16 FAIL: NeedsConversion(\"lb/gal\", FirmwareDefault) должен быть false"); fails++; }
            if (UnitConversion.NeedsConversion("g/cc", fwDefault))
            { Console.WriteLine("T16 FAIL: NeedsConversion(\"g/cc\", FirmwareDefault) должен быть false"); fails++; }

            if (fails == 0)
                Console.WriteLine($"T16 PASS: 1:1-гарантия firmware ({checkedSignals} сигналов 14 таблиц) + legacy bbl/min/°F ↔ firmware-native");
            return fails;
        }

        /// <summary>
        /// T17: байт-уровень декоратора на legacy-таблице 0x13120200 («Lnd Mix Section»):
        /// «Recirc Flow Rate» O=8 → payload[4] (bbl/min), «Recirc Density» O=4 → payload[0] (lb/gal).
        /// FirmwareDefault: flow bbl/min→gpm ×42 ⇒ 42.0f; density lb/gal — firmware-native ⇒ identity 15.8f.
        /// Metric (через Settings): flow ⇒ 0.158987f; density ⇒ 15.8×0.119826.
        /// UC-сигнал «Cmnt Ops Track Descript» O=8 sz120 (sv 0x1312020F) ⇒ payload_index 4,
        /// 4+120=124 — передаваем, но UC не конвертируется ⇒ байты не тронуты.
        /// </summary>
        private static int Test17_ProviderByteLevel()
        {
            int fails = 0;
            const uint equipment = 0x00A86F46;
            const uint svMix = 0x13120200;
            const uint svOps = 0x1312020F;

            // payload 0x13120200: 1.0 bbl/min @payload[4] (O=8), 15.8 lb/gal @payload[0] (O=4)
            var mixPayload = new byte[124];
            BinaryPrimitives.WriteSingleBigEndian(mixPayload.AsSpan(4, 4), 1.0f);
            BinaryPrimitives.WriteSingleBigEndian(mixPayload.AsSpan(0, 4), 15.8f);

            // payload 0x1312020F: UC «Cmnt Ops Track Descript» O=8 sz120 → payload[4..]
            // (4+120=124 — передаваем). UC не конвертируется ⇒ байты не трогаем.
            var opsPayload = new byte[124];
            for (int i = 4; i < opsPayload.Length; i++)
                opsPayload[i] = 0xAB;

            var vectors = new AceVectorData[]
            {
                new AceVectorData(svMix, "Lnd Mix Section", mixPayload),
                new AceVectorData(svOps, "Lnd Ops Track", opsPayload)
            };

            // ── FirmwareDefault: flow bbl/min→gpm ×42, density lb/gal identity ──
            var fwConv = new UnitConvertingPayloadProvider(new FakeSingleSvProvider(vectors), UnitPreset.FirmwareDefault);
            var fwOut = fwConv.GetSuperframe(equipment, 0.0);
            var fwMix = FindVector(fwOut, svMix);
            if (fwMix is null) { Console.WriteLine("T17 FAIL: sv 0x13120200 отсутствует в FirmwareDefault"); return 1; }
            float fwRate = BinaryPrimitives.ReadSingleBigEndian(fwMix.Payload.AsSpan(4, 4));
            float fwDens = BinaryPrimitives.ReadSingleBigEndian(fwMix.Payload.AsSpan(0, 4));
            if (Math.Abs(fwRate - 42.0f) > 1e-4)
            { Console.WriteLine($"T17 FAIL: FirmwareDefault flow @payload[4] (O=8) = {fwRate.ToString("R")}, expected 42.0"); fails++; }
            if (fwDens != 15.8f)
            { Console.WriteLine($"T17 FAIL: FirmwareDefault density @payload[0] (O=4) = {fwDens.ToString("R")}, expected identity 15.8"); fails++; }

            // ── Metric (через Settings): flow bbl/min→m³/min, density lb/gal→g/cc ──
            var metricConv = new UnitConvertingPayloadProvider(new FakeSingleSvProvider(vectors), UnitPreset.Metric);
            metricConv.Settings = new UnitSettings { Preset = UnitPreset.Metric };
            var metricOut = metricConv.GetSuperframe(equipment, 0.0);
            var metricMix = FindVector(metricOut, svMix);
            if (metricMix is null) { Console.WriteLine("T17 FAIL: sv 0x13120200 отсутствует в Metric"); return 1; }
            float mRate = BinaryPrimitives.ReadSingleBigEndian(metricMix.Payload.AsSpan(4, 4));
            float mDens = BinaryPrimitives.ReadSingleBigEndian(metricMix.Payload.AsSpan(0, 4));
            if (Math.Abs(mRate - 0.158987f) > 0.001f)
            { Console.WriteLine($"T17 FAIL: Metric flow @payload[4] (O=8) = {mRate.ToString("R")}, expected ≈0.158987"); fails++; }
            float expectedDens = 15.8f * 0.119826f;
            if (Math.Abs(mDens - expectedDens) > 0.001f)
            { Console.WriteLine($"T17 FAIL: Metric density @payload[0] (O=4) = {mDens.ToString("R")}, expected ≈{expectedDens.ToString("R")}"); fails++; }

            // ── UC (0x1312020F) не тронут: UC не конвертируется (payload_index 4..124) ──
            var ops = FindVector(fwOut, svOps);
            if (ops is null) { Console.WriteLine("T17 FAIL: sv 0x1312020F отсутствует"); return 1; }
            bool untouched = true;
            for (int i = 4; i < ops.Payload.Length; i++)
                if (ops.Payload[i] != 0xAB) { untouched = false; break; }
            if (!untouched)
            { Console.WriteLine("T17 FAIL: UC/overflow сигнал 0x1312020F изменён"); fails++; }

            if (fails == 0)
                Console.WriteLine("T17 PASS: байт-уровень (FirmwareDefault ×42/identity; Metric 0.158987/15.8×0.119826; UC не тронут)");
            return fails;
        }

        /// <summary>
        /// T18: регресс БАГА A — кросс-групповая конверсия = identity для ВСЕХ групп
        /// (GroupOf(to) != group ⇒ вернуть value; аффинная температура не трогает чужие группы).
        /// </summary>
        private static int Test18_CrossGroupIdentity()
        {
            int fails = 0;

            void Check(string what, double expected, double actual)
            {
                if (Math.Abs(actual - expected) > 1e-12)
                {
                    Console.WriteLine($"T18 FAIL: {what}: expected {expected.ToString("R")}, got {actual.ToString("R")}");
                    fails++;
                }
            }

            Check("Convert(1.0, °f, gpm)", 1.0, UnitConversion.Convert(1.0, "°f", "gpm"));
            Check("Convert(212.0, °f, bbl)", 212.0, UnitConversion.Convert(212.0, "°f", "bbl"));
            Check("Convert(1.0, bbl/min, psi)", 1.0, UnitConversion.Convert(1.0, "bbl/min", "psi"));
            Check("Convert(1.0, ppg, ft)", 1.0, UnitConversion.Convert(1.0, "ppg", "ft"));
            Check("Convert(1.0, ft, kpa)", 1.0, UnitConversion.Convert(1.0, "ft", "kpa"));
            Check("Convert(1.0, psi, °c)", 1.0, UnitConversion.Convert(1.0, "psi", "°c"));

            if (fails == 0)
                Console.WriteLine("T18 PASS: кросс-групповая конверсия — identity (регресс БАГА A)");
            return fails;
        }

        /// <summary>
        /// T19: регресс БАГА B на байт-уровне — MB_DensHPVT (g/cc, sv 0x1312021B «RT Totals &amp; HPVT»)
        /// под FirmwareDefault остаётся 1:1 (НЕ конвертируется в ppg ×8.3454). Плюс NeedsConversion.
        /// </summary>
        private static int Test19_FirmwareNativeGccRegression()
        {
            int fails = 0;
            const uint equipment = 0x00A86F46;
            const uint svHpvt = 0x1312021B;

            var lantable = new LantableRegistry();
            var fwDefault = new UnitSettings { Preset = UnitPreset.FirmwareDefault };

            // NeedsConversion: g/cc под FirmwareDefault — identity.
            if (UnitConversion.NeedsConversion("g/cc", fwDefault))
            {
                Console.WriteLine("T19 FAIL: NeedsConversion(\"g/cc\", FirmwareDefault) должен быть false");
                return 1;
            }

            var svDef = lantable.GetBySvNumber(svHpvt);
            if (svDef is null)
            {
                Console.WriteLine("T19 FAIL: SV 0x1312021B не загружен");
                return 1;
            }
            var mb = Array.Find(svDef.Signals, s => s.Name == "MB_DensHPVT");
            if (mb is null)
            {
                Console.WriteLine("T19 FAIL: сигнал MB_DensHPVT не найден в 0x1312021B");
                return 1;
            }

            var payload = new byte[124];
            // Offset law: MB_DensHPVT записываем на payload[ByteOffset − 4] (кадр 0x18 + ByteOffset).
            BinaryPrimitives.WriteSingleBigEndian(payload.AsSpan(mb.ByteOffset - 4, 4), 1.19f);

            var conv = new UnitConvertingPayloadProvider(
                new FakeSingleSvProvider(payload, svHpvt), UnitPreset.FirmwareDefault);
            var outVectors = conv.GetSuperframe(equipment, 0.0);
            if (outVectors.Count != 1)
            {
                Console.WriteLine("T19 FAIL: provider вернул неожиданное число векторов");
                return 1;
            }
            float v = BinaryPrimitives.ReadSingleBigEndian(outVectors[0].Payload.AsSpan(mb.ByteOffset - 4, 4));
            if (v != 1.19f)
            {
                Console.WriteLine($"T19 FAIL: MB_DensHPVT @payload[{mb.ByteOffset - 4}] (O={mb.ByteOffset}) = {v.ToString("R")}, expected identity 1.19 (g/cc НЕ в ppg)");
                fails++;
            }

            if (fails == 0)
                Console.WriteLine($"T19 PASS: MB_DensHPVT (g/cc) @payload[{mb.ByteOffset - 4}] (O={mb.ByteOffset}) — 1:1 с прошивкой (регресс БАГА B)");
            return fails;
        }

        /// <summary>
        /// T20: offset law (payload_index = ByteOffset − 4) на байтах.
        /// (а) ManualOverridePayloadProvider поверх AcePayloadProvider: «Pump A Pressure» (O=4)
        ///     override 123.45 ⇒ значение на payload[0], payload[4] (O=8, следующий сигнал)
        ///     остаётся байтами базы;
        /// (б) CanTransmit("Cmnt Ops Track Descript") == true (O=8, sz 120 ⇒ 4+120=124);
        /// (в) для SV 0x13120203 «Unit Number» (O=4) ⇒ payload[0] = 11038534 (0x00A86F46).
        /// </summary>
        private static int Test20_OffsetLaw()
        {
            int fails = 0;
            const uint equipment = 0x00A86F46;
            const uint svPump = 0x13120206;   // Lnd Pump Sect
            const uint svOps  = 0x1312020F;   // Lnd Ops Track
            const uint svAuto = 0x13120203;   // Lnd Automation (Unit Number)

            var provider = new ManualOverridePayloadProvider(new AcePayloadProvider());

            // (а) «Pump A Pressure» (O=4 ⇒ payload_index 0): override 123.45.
            var basePump = FindVector(new AcePayloadProvider().GetSuperframe(equipment, 0.0), svPump);
            if (basePump is null)
            {
                Console.WriteLine("T20 FAIL: sv 0x13120206 отсутствует в базовом superframe");
                return 1;
            }
            provider.SetOverride(svPump, "Pump A Pressure", 123.45);
            var vectors = provider.GetSuperframe(equipment, 0.0);
            var v = FindVector(vectors, svPump);
            if (v is null || v.Payload.Length != ManualOverridePayloadProvider.PayloadLength)
            {
                Console.WriteLine("T20 FAIL: payload sv 0x13120206 missing/wrong size after override");
                return 1;
            }
            float edited = BinaryPrimitives.ReadSingleBigEndian(v.Payload.AsSpan(0, 4));
            Console.WriteLine($"T20 EVIDENCE: 'Pump A Pressure' 123.45 (O=4) → payload[0] = {BytesToHex(v.Payload.AsSpan(0, 4).ToArray())} = {edited.ToString("R")}");
            if (Math.Abs(edited - 123.45f) > 1e-3)
            {
                Console.WriteLine($"T20 FAIL: payload[0] (O=4) = {edited.ToString("R")}, expected 123.45");
                fails++;
            }
            // payload[4] (O=8 — следующий сигнал) должен остаться байтами базы.
            if (!ByteArrayEqual(v.Payload.AsSpan(4, 4).ToArray(), basePump.Payload.AsSpan(4, 4).ToArray()))
            {
                Console.WriteLine("T20 FAIL: payload[4] (O=8) изменён от override «Pump A Pressure» (ожидались байты базы)");
                fails++;
            }

            // (б) CanTransmit("Cmnt Ops Track Descript") == true (O=8, sz 120 ⇒ payload_index 4, 4+120=124).
            if (!provider.CanTransmit(svOps, "Cmnt Ops Track Descript", out string reason))
            {
                Console.WriteLine($"T20 FAIL: Cmnt Ops Track Descript должен быть передаваем: '{reason}'");
                fails++;
            }

            // (в) SV 0x13120203 «Unit Number» (O=4) → payload[0].
            provider.SetOverride(svAuto, "Unit Number", 11038534.0);
            vectors = provider.GetSuperframe(equipment, 0.0);
            v = FindVector(vectors, svAuto);
            if (v is null)
            {
                Console.WriteLine("T20 FAIL: sv 0x13120203 отсутствует в superframe");
                return 1;
            }
            uint unit = BinaryPrimitives.ReadUInt32BigEndian(v.Payload.AsSpan(0, 4));
            Console.WriteLine($"T20 EVIDENCE: 'Unit Number' 11038534 (O=4) → payload[0] = 0x{unit:X8} ({unit})");
            if (unit != 11038534)
            {
                Console.WriteLine($"T20 FAIL: Unit Number = {unit}, expected 11038534 (0x00A86F46)");
                fails++;
            }

            if (fails == 0)
                Console.WriteLine("T20 PASS: offset law (payload_index = ByteOffset − 4) подтверждён на байтах");
            return fails;
        }

        /// <summary>Фейковый IAcePayloadProvider для юнит-проверки провода (T8/T17/T19):
        /// один вектор с готовым payload либо набор векторов.</summary>
        private sealed class FakeSingleSvProvider : IAcePayloadProvider
        {
            private readonly IReadOnlyList<AceVectorData> _vectors;

            public FakeSingleSvProvider(byte[] payload, uint sv)
                : this(new[] { new AceVectorData(sv, $"SV_{sv:X8}", payload) }) { }

            public FakeSingleSvProvider(IReadOnlyList<AceVectorData> vectors) { _vectors = vectors; }

            public IReadOnlyList<uint> ActiveSvNumbers
            {
                get
                {
                    var svs = new List<uint>(_vectors.Count);
                    foreach (var v in _vectors)
                        svs.Add(v.SvNumber);
                    return svs;
                }
            }

            public IReadOnlyList<AceVectorData> GetSuperframe(uint equipmentNumber, double elapsedSeconds)
                => _vectors;
        }

        private static AceVectorData? FindVector(IReadOnlyList<AceVectorData> vectors, uint svNumber)
        {
            foreach (var v in vectors)
            {
                if (v.SvNumber == svNumber)
                    return v;
            }
            return null;
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static bool ByteArrayEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static string BytesToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace('-', ' ');
        }
    }
}
