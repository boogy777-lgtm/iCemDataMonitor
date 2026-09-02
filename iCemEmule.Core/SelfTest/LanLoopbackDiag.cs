using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Halliburton.iCemEmule.Core;
using Halliburton.iCemEmule.Emitters;
using SharpPcap;

namespace Halliburton.iCemEmule.SelfTest
{
    /// <summary>
    /// LAN loopback diagnostic: open adapter for capture + send superframes,
    /// count how many are actually seen on the wire.
    /// Tests with fake NI MAC and real adapter MAC to detect driver src-spoof filtering.
    ///
    /// Usage: iCemEmule.exe --landiag "<adapterSubstring>"
    /// </summary>
    public static class LanLoopbackDiag
    {
        public static int Run(string adapterSubstring)
        {
            Console.WriteLine("=== LAN Loopback Diagnostic ===");
            Console.WriteLine($"Target adapter substring: '{adapterSubstring}'");
            Console.WriteLine();

            // ── Find adapter ──────────────────────────────────────────────
            ILiveDevice? sendDevice = null;
            ILiveDevice? captureDevice = null;
            try
            {
                var allDevices = CaptureDeviceList.Instance;
                Console.WriteLine($"Available adapters ({allDevices.Count}):");
                foreach (var d in allDevices)
                    Console.WriteLine($"  {d.Description}  [{d.Name}]  MAC={d.MacAddress}");

                sendDevice = FindAdapter(allDevices, adapterSubstring);
                if (sendDevice == null)
                {
                    Console.WriteLine($"FAIL: Adapter not found for '{adapterSubstring}'");
                    return 1;
                }

                // Get a SEPARATE device instance for capture via New()
                // (cannot Open the same ILiveDevice object twice)
                var newList = CaptureDeviceList.New();
                captureDevice = FindAdapter(newList, adapterSubstring);
                if (captureDevice == null)
                {
                    Console.WriteLine("FAIL: Could not get separate capture device instance");
                    return 1;
                }

                string adapterMac = sendDevice.MacAddress != null
                    ? string.Join(":", sendDevice.MacAddress.GetAddressBytes().Select(b => b.ToString("X2")))
                    : "UNKNOWN";

                Console.WriteLine($"Send device:    {sendDevice.Description} [{sendDevice.Name}] MAC={adapterMac}");
                Console.WriteLine($"Capture device: {captureDevice.Description} [{captureDevice.Name}]");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL enumerating adapters: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine("Npcap may not be installed or accessible. Install from https://npcap.com/");
                return 1;
            }

            // ── Test variant A: fake NI src MAC ──────────────────────────
            string fakeMac = "00:80:2F:23:F4:44";
            string realMac = sendDevice.MacAddress != null
                ? string.Join(":", sendDevice.MacAddress.GetAddressBytes().Select(b => b.ToString("X2")))
                : "00:00:00:00:00:00";

            Console.WriteLine("─── Variant A: fake NI src MAC ───");
            int capturedA = RunCaptureSendCycle(captureDevice, sendDevice, fakeMac, adapterSubstring);
            Console.WriteLine($"  sent=12  captured={capturedA}");

            Thread.Sleep(1500); // settle between tests

            // ── Test variant B: real adapter src MAC ─────────────────────
            Console.WriteLine();
            Console.WriteLine("─── Variant B: real adapter src MAC ───");
            int capturedB = RunCaptureSendCycle(captureDevice, sendDevice, realMac, adapterSubstring);
            Console.WriteLine($"  sent=12  captured={capturedB}");

            Console.WriteLine();
            Console.WriteLine("=== DIAGNOSIS ===");
            if (capturedA == 0 && capturedB == 0)
            {
                Console.WriteLine("RESULT: ZERO frames captured on wire.");
                Console.WriteLine("ROOT CAUSE: Send path failure — frames are not leaving the NIC.");
                Console.WriteLine("POSSIBLE FIXES:");
                Console.WriteLine("  1. Npcap 'Restrict to Administrators'? Run as Administrator.");
                Console.WriteLine("  2. DeviceConfiguration may need specific flags.");
                Console.WriteLine("  3. Check Npcap service: 'sc query npcap' or 'sc query npf'.");
            }
            else if (capturedA == 0 && capturedB > 0)
            {
                Console.WriteLine("RESULT: Only real MAC frames pass. Spoofed src MAC rejected by driver/NIC.");
                Console.WriteLine("FIX: Use real adapter MAC as default src MAC for LAN emission.");
            }
            else if (capturedA > 0 && capturedB > 0)
            {
                Console.WriteLine("RESULT: Both src MAC variants seen on wire. Frames ARE going out.");
                Console.WriteLine("NEXT: If iCem doesn't see them, the issue is in iCem configuration.");
                Console.WriteLine("  Check iCem serial/lan tab: adapter selected? equipment number? unit number?");
            }

            return 0;
        }

        private static int RunCaptureSendCycle(
            ILiveDevice captureDevice,
            ILiveDevice sendDevice,
            string srcMac,
            string adapterSubstring)
        {
            int captured = 0;
            bool captureRunning = false;

            try
            {
                // Open capture device
                var capConfig = new DeviceConfiguration
                {
                    Mode = DeviceModes.Promiscuous,  // need promiscuous to see multicast we sent
                    ReadTimeout = 500,
                    Snaplen = 200  // only need first 152 bytes
                };

                captureDevice.Open(capConfig);
                Console.WriteLine($"  Capture opened: {captureDevice.Description} (promisc)");

                captureDevice.OnPacketArrival += (_, e) =>
                {
                    var raw = e.GetPacket();
                    if (raw.Data.Length >= AceFrameBuilder.FrameSize)
                    {
                        // Quick pre-check: dst MAC starts with 03:00:01
                        if (raw.Data[0] == 0x03 && raw.Data[1] == 0x00 && raw.Data[2] == 0x01)
                        {
                            uint eq, sv; byte[] pl;
                            if (AceFrameBuilder.TryParseFrame(raw.Data, out eq, out sv, out pl))
                            {
                                Interlocked.Increment(ref captured);
                            }
                        }
                    }
                };

                captureDevice.StartCapture();
                captureRunning = true;

                // Sleep to ensure capture is active before sending
                Thread.Sleep(200);

                // Send 2 superframes (6 sv each = 12 frames) via AceLanEmitter
                // Use separate emitter instance
                var emitter = new AceLanEmitter();
                string result = emitter.Open(adapterSubstring);
                if (!string.IsNullOrEmpty(result))
                {
                    Console.WriteLine($"  Emitter Open FAILED: {result}");
                    return captured;
                }

                Console.WriteLine($"  Emitter opened OK. Sending superframes...");

                // Build test vectors (legacy 6 sv — диагностический фикс-набор;
                // не зависит от AcePayloadProvider.ActiveSvNumbers, default = 6 legacy)
                var vectors = BuildTestVectors();
                const uint equipment = 0x00A86F46;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                emitter.SendSuperframe(vectors, equipment, srcMac);
                Thread.Sleep(100);
                emitter.SendSuperframe(vectors, equipment, srcMac);
                sw.Stop();
                Console.WriteLine($"  Sent 2 superframes in {sw.ElapsedMilliseconds}ms. FramesSent={emitter.FramesSent}");

                emitter.Close();
                emitter.Dispose();

                // Wait for capture to collect (remaining time to ~5s total)
                int remainingMs = Math.Max(500, 5000 - (int)sw.ElapsedMilliseconds);
                Thread.Sleep(remainingMs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR: {ex.GetType().Name}: {ex.Message}");
                if (ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine("  HINT: Try running as Administrator.");
            }
            finally
            {
                if (captureRunning)
                {
                    try { captureDevice.StopCapture(); } catch { }
                }
                try { captureDevice.Close(); } catch { }
            }

            return captured;
        }

        private static IReadOnlyList<AceVectorData> BuildTestVectors()
        {
            // 6 legacy service vectors (диагностический фикс-набор; НЕ часть 20-sv superframe)
            uint[] svs = { 0x13120203, 0x13120206, 0x13120200, 0x13120209, 0x1312020C, 0x1312020F };
            var list = new List<AceVectorData>();
            byte[] detPayload = new byte[AceFrameBuilder.PayloadSize];
            for (int i = 0; i < detPayload.Length; i++)
                detPayload[i] = (byte)(i & 0xFF);

            foreach (uint sv in svs)
            {
                var payload = new byte[AceFrameBuilder.PayloadSize];
                Array.Copy(detPayload, payload, AceFrameBuilder.PayloadSize);
                // Tag payload with sv for debugging
                payload[0] = (byte)((sv >> 24) & 0xFF);
                payload[1] = (byte)((sv >> 16) & 0xFF);
                payload[2] = (byte)((sv >> 8) & 0xFF);
                payload[3] = (byte)(sv & 0xFF);
                list.Add(new AceVectorData(sv, $"SV_{sv:X8}", payload));
            }
            return list;
        }

        private static ILiveDevice? FindAdapter(IReadOnlyList<ILiveDevice> devices, string substring)
        {
            foreach (var d in devices)
            {
                string desc = d.Description ?? "";
                string name = d.Name ?? "";
                if (desc.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return d;
                }
            }
            return null;
        }
    }
}
