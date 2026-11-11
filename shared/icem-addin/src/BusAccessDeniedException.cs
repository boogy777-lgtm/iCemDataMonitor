using System;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// Fatal-fast open failure of the PERSISTED bus mapping — CAPACITY-SKEW mode
    /// (INC-03): the on-disk file upgrade (FULL MIGRATION expansion to the v5
    /// dictionary, 10001 regs / 20066 B) ran fine, but the LIVE named section was
    /// created by an older-dictionary producer (or a stale consumer) at a smaller
    /// capacity and keeps that capacity until EVERY handle closes. Windows reports
    /// ACCESS DENIED for views beyond the section capacity, so this surfaces as a
    /// permission error while the real cause is a dictionary-size skew between the
    /// live section and the expanded on-disk file.
    /// Remediation is an ORDERED RESTART (INC-03): stop ALL iCemDataMonitor processes
    /// incl. the producer (iCem.exe), then start the producer FIRST — the AddIn
    /// must be the first opener so the expansion runs under its own section —
    /// then the POC consumers.
    /// </summary>
    public sealed class BusAccessDeniedException : UnauthorizedAccessException
    {
        public BusAccessDeniedException(string message, Exception inner = null)
            : base(message, inner)
        {
        }

        /// <summary>Ordered-restart runbook (INC-03) — producer first, consumers next.</summary>
        public const string OrderedRestartRunbook =
            "Ordered-restart law (INC-03): the iCemDataMonitor bus named section capacity is frozen until " +
            "ALL handles close. The on-disk file was upgraded to the v5 dictionary (10001 regs / " +
            "20066 B), but the live section is still held at the old capacity by another process. " +
            "Remediation: stop ALL iCemDataMonitor processes incl. the producer (iCem.exe), then start the " +
            "producer FIRST (iCem.exe — the AddIn must be the first opener so the expansion runs " +
            "under its section), then the POC consumers.";

        /// <summary>Section-capacity flavor (mirrors the POC BusAccessDeniedException.
        /// ForSectionCapacity semantics): path + required bytes + the runbook.</summary>
        public static BusAccessDeniedException ForSectionCapacity(string path, long requiredBytes, Exception inner = null)
        {
            string message = string.Join(Environment.NewLine, new[]
            {
                "iCemDataMonitor bus: ACCESS DENIED — the LIVE named section capacity is SMALLER than this process requires.",
                "  Path:            " + path,
                "  Required bytes:  " + requiredBytes.ToString(),
                "Cause: the on-disk file is fine, but the live named section was created by an",
                "older-dictionary producer at a smaller capacity and keeps that capacity until",
                "every handle closes (Windows reports ACCESS DENIED for views beyond the section",
                "capacity — a dictionary-size skew, NOT a permission problem).",
                OrderedRestartRunbook,
            });
            return new BusAccessDeniedException(message, inner);
        }
    }
}
