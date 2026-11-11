using System;

namespace Burservis.iCem.DataMonitor.Bus
{
    /// <summary>
    /// D1 first-valid-wins latch (bus register map v5, reg 0) — vendor-free,
    /// self-testable state machine. The dll locks onto the FIRST COM device
    /// delivering parseable telemetry; lines from other devices are DISCARDED;
    /// re-arbitration happens ONLY after the latch is released on stream loss
    /// (release is the caller's policy: port state loss ∧/∨ silence timeout).
    /// </summary>
    internal sealed class ComFeedLatch
    {
        private string _portKey;
        private int _portNumber;
        private DateTime _lastEvidenceUtc = DateTime.MinValue;
        private bool _latched;

        /// <summary>True while a device holds the latch.</summary>
        public bool IsLatched
        {
            get { return _latched; }
        }

        /// <summary>EquipmentPort number (1..16) of the latched device; 0 when not latched.</summary>
        public int PortNumber
        {
            get { return _latched ? _portNumber : 0; }
        }

        /// <summary>UTC time of the last accepted line from the latched device.</summary>
        public DateTime LastEvidenceUtc
        {
            get { return _lastEvidenceUtc; }
        }

        /// <summary>
        /// Feed one parseable line from a device. D1: the first device to deliver
        /// wins the latch; its subsequent lines refresh the evidence; lines from
        /// ANY other device are discarded (returns false — caller must NOT write
        /// them to raw_com_line).
        /// </summary>
        /// <param name="portKey">Stable device identity (e.g. EquipmentPort string).</param>
        /// <param name="portNumber">EquipmentPort number (1..16).</param>
        /// <returns>True ⇔ this device owns the latch (line belongs to raw_com_line).</returns>
        public bool OnLine(string portKey, int portNumber)
        {
            if (string.IsNullOrEmpty(portKey))
            {
                return false;
            }
            if (!_latched)
            {
                _latched = true;
                _portKey = portKey;
                _portNumber = portNumber;
            }
            if (string.Equals(_portKey, portKey, StringComparison.Ordinal))
            {
                _lastEvidenceUtc = DateTime.UtcNow;
                return true;
            }
            return false; // another device — discarded (D1)
        }

        /// <summary>True while latched AND no accepted line within the idle window
        /// (stream-loss evidence for the caller's re-arbitration policy).</summary>
        public bool IsSilentBeyond(DateTime nowUtc, TimeSpan idleTimeout)
        {
            return _latched && (nowUtc - _lastEvidenceUtc) > idleTimeout;
        }

        /// <summary>Release the latch — the next line from any device re-arbitrates
        /// (first-valid-wins again).</summary>
        public void Release()
        {
            _latched = false;
            _portKey = null;
            _portNumber = 0;
            _lastEvidenceUtc = DateTime.MinValue;
        }
    }
}
