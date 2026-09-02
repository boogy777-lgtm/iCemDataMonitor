using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Plugins.Icem;

public static class SsfTemplateLoader
{
    private static string TemplatesDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates");

    public static IReadOnlyList<VariableDefinition> Load(string templateName)
    {
        var path = Path.Combine(TemplatesDir, templateName + ".ssf");
        if (!File.Exists(path))
            return Array.Empty<VariableDefinition>();

        var rawRows = new List<(int Col0, string Name, string Desc)>();
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("*"))
                continue;
            var parts = trimmed.Split(',');
            if (parts.Length < 5)
                continue;
            if (!int.TryParse(parts[0], out var pos))
                continue;

            var name = parts[1].Trim();
            var desc = parts[2].Trim();
            rawRows.Add((pos, name, desc));
        }

        // F7ssf: if ALL col0 == 0, assign Position = sequential 1..n, IsSpare=false
        bool allZero = rawRows.Count > 0 && rawRows.All(r => r.Col0 == 0);

        var result = new List<VariableDefinition>(rawRows.Count);
        for (int i = 0; i < rawRows.Count; i++)
        {
            var (col0, name, desc) = rawRows[i];
            var uom = VariableUnitGuesser.GuessUom(name);
            int position;
            bool isSpare;

            if (allZero)
            {
                position = i + 1;      // sequential 1..n
                isSpare = false;
            }
            else
            {
                position = col0;
                isSpare = col0 > 0;
            }

            result.Add(new VariableDefinition(name, desc, uom, "#606060", position, isSpare, "ssf"));
        }
        return result;
    }

    public static DeviceFamily FamilyConfigs(string family)
    {
        var templates = DeviceFamilyRegistry.Families.GetValueOrDefault(family);
        if (templates is null)
            return new DeviceFamily(family, Array.Empty<DeviceConfig>());

        var configs = new List<DeviceConfig>();
        foreach (var tpl in templates)
        {
            var vars = Load(tpl);
            configs.Add(new DeviceConfig(tpl, vars));
        }
        return new DeviceFamily(family, configs);
    }

    public static IReadOnlyList<DeviceFamily> AllFamilies()
    {
        return new[]
        {
            FamilyConfigs("flecs"),
            FamilyConfigs("plc"),
            FamilyConfigs("unipro"),
            FamilyConfigs("compupac"),
            FamilyConfigs("other"),
        };
    }
}
