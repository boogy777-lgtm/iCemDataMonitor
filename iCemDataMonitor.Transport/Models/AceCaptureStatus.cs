namespace iCemDataMonitor.Transport.Models;

/// <summary>
/// Capture-to-file status — designed for B5 REST consumption.
/// No existing mock fixture; additive definition.
/// </summary>
public sealed class AceCaptureStatus
{
    public bool Capturing { get; set; }
    public int FramesCaptured { get; set; }
    public double ElapsedSec { get; set; }
    public string? TargetPath { get; set; }
    public string? StartedAt { get; set; } // ISO 8601 UTC
}