namespace iCemDataMonitor.Transport.Models;

/// <summary>
/// com0com driver/monitor status — frozen contract matching wwwroot/mock/com0com-status.json.
/// </summary>
public sealed class Com0ComStatus
{
    public bool DriverInstalled { get; set; }
    public bool DriverRunning { get; set; }
    public string DriverVersion { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public bool Hub4ComInstalled { get; set; }
    public bool Hub4ComRunning { get; set; }
    public List<Com0ComPair> Pairs { get; set; } = new();
    public string? LastError { get; set; }
    public TopologyMatchStatus TopologyMatch { get; set; } = new();
}

public sealed class Com0ComPair
{
    public int BusId { get; set; }
    public string BusNameA { get; set; } = "";
    public string BusNameB { get; set; } = "";
    public string PortNameA { get; set; } = "";
    public string PortNameB { get; set; } = "";
    public Dictionary<string, string> AttrsA { get; set; } = new();
    public Dictionary<string, string> AttrsB { get; set; } = new();
}

public sealed class TopologyMatchStatus
{
    public bool Matched { get; set; }
    public List<string> Missing { get; set; } = new();
    public List<string> Unexpected { get; set; } = new();
    public List<string> MismatchedRoles { get; set; } = new();
}