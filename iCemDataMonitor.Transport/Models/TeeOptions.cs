namespace iCemDataMonitor.Transport.Models;

public class TeeOptions
{
    public const string Section = "Tee";

    public bool Enabled { get; set; } = false;
    public string PhysicalPort { get; set; } = "COM2";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public System.IO.Ports.Parity Parity { get; set; } = System.IO.Ports.Parity.None;
    public System.IO.Ports.StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;
    public string VirtualOutPort { get; set; } = "COM1";
    public bool Bidirectional { get; set; } = true;
}