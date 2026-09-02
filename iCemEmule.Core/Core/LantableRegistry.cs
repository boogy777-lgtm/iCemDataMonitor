// LantableRegistry — loads templates\lantable_registry.json
// plus (additively) templates\firmware_registry.json.
// Indexed by svNumber. Provides signal definitions for AcePayloadProvider.
// Firmware tables are merged in: legacy svNumbers keep priority on collision,
// so existing tables are never overwritten.

using System.Text.Json;

namespace Halliburton.iCemEmule.Core;

public sealed class LantableRegistry
{
    public sealed record SignalDef(
        int ByteOffset,
        int Size,
        string ValueType,      // FF, UL, SI, SL, UI, HX, UC
        double DasFactor,
        string Name,
        string UoM
    );

    public sealed record ServiceVectorDef(
        uint SvNumber,
        string Name,
        string EquipmentType,
        SignalDef[] Signals
    );

    private readonly Dictionary<uint, ServiceVectorDef> _bySv = new();

    public IReadOnlyDictionary<uint, ServiceVectorDef> All => _bySv;

    public LantableRegistry()
    {
        Load();
    }

    private void Load()
    {
        LoadFile("lantable_registry.json");
        LoadFirmwareFile();
    }

    private void LoadFile(string fileName)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates", fileName);
        if (!File.Exists(path))
        {
            LogLoad($"{fileName} not found at {path}");
            return;
        }

        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            var svArray = doc.RootElement.GetProperty("serviceVectors");

            foreach (var svElem in svArray.EnumerateArray())
            {
                var svNumber = svElem.GetProperty("svNumber").GetUInt32();
                var name = svElem.GetProperty("name").GetString() ?? string.Empty;
                var equipmentType = svElem.TryGetProperty("equipmentType", out var et)
                    ? (et.GetString() ?? string.Empty) : string.Empty;

                var sigs = new List<SignalDef>();
                if (svElem.TryGetProperty("signals", out var sigsElem))
                {
                    foreach (var s in sigsElem.EnumerateArray())
                    {
                        sigs.Add(new SignalDef(
                            ByteOffset: s.GetProperty("byteOffset").GetInt32(),
                            Size: s.GetProperty("size").GetInt32(),
                            ValueType: s.GetProperty("valueType").GetString() ?? string.Empty,
                            DasFactor: s.TryGetProperty("dasFactor", out var df) ? df.GetDouble() : 1.0,
                            Name: s.GetProperty("name").GetString() ?? string.Empty,
                            UoM: s.TryGetProperty("uom", out var u) ? (u.GetString() ?? string.Empty) : string.Empty
                        ));
                    }
                }

                // first occurrence kept per spec (duplicates exist in original)
                if (!_bySv.ContainsKey(svNumber))
                {
                    _bySv[svNumber] = new ServiceVectorDef(svNumber, name, equipmentType, sigs.ToArray());
                }
            }
        }
        catch (Exception ex)
        {
            LogLoad($"Failed to parse {fileName}: {ex.Message}");
        }
    }

    // firmware_registry.json — аддитивный источник SV-таблиц прошивки.
    // При совпадении svNumber приоритет у старых (legacy) таблиц, чтобы не сломать существующий трафик.
    private void LoadFirmwareFile()
    {
        const string fileName = "firmware_registry.json";
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates", fileName);
        if (!File.Exists(path))
        {
            LogLoad($"{fileName} not found at {path}");
            return;
        }

        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            var svArray = doc.RootElement.GetProperty("serviceVectors");

            var added = 0;
            var skipped = 0;
            foreach (var svElem in svArray.EnumerateArray())
            {
                var svNumber = svElem.GetProperty("svNumber").GetUInt32();
                var name = svElem.GetProperty("name").GetString() ?? string.Empty;
                var equipmentType = svElem.TryGetProperty("equipmentType", out var et)
                    ? (et.GetString() ?? string.Empty) : string.Empty;

                var sigs = new List<SignalDef>();
                if (svElem.TryGetProperty("signals", out var sigsElem))
                {
                    foreach (var s in sigsElem.EnumerateArray())
                    {
                        sigs.Add(new SignalDef(
                            ByteOffset: s.GetProperty("byteOffset").GetInt32(),
                            Size: s.GetProperty("size").GetInt32(),
                            ValueType: s.GetProperty("valueType").GetString() ?? string.Empty,
                            DasFactor: s.TryGetProperty("dasFactor", out var df) ? df.GetDouble() : 1.0,
                            Name: s.GetProperty("name").GetString() ?? string.Empty,
                            UoM: s.TryGetProperty("uom", out var u) ? (u.GetString() ?? string.Empty) : string.Empty
                        ));
                    }
                }

                // Мерж: добавляем только те svNumber, которых ещё нет (приоритет у старых таблиц)
                if (_bySv.ContainsKey(svNumber))
                {
                    skipped++;
                    continue;
                }

                _bySv[svNumber] = new ServiceVectorDef(svNumber, name, equipmentType, sigs.ToArray());
                added++;
            }

            LogLoad($"{fileName}: merged {added} firmware SV tables, skipped {skipped} duplicates (legacy priority)");
        }
        catch (Exception ex)
        {
            LogLoad($"Failed to parse {fileName}: {ex.Message}");
        }
    }

    public ServiceVectorDef? GetBySvNumber(uint svNumber)
    {
        _bySv.TryGetValue(svNumber, out var sv);
        return sv;
    }

    private static void LogLoad(string msg)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "iCemEmule.templates.log");
            File.AppendAllText(logPath, $"{DateTime.UtcNow:O} LantableRegistry: {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
