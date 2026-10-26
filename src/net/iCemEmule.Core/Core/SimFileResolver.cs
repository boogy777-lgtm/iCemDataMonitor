// SimFileResolver — emulator-override law (2026-08-31, shared-install merge).
//
// CONTEXT: the installer now lays the emulator FLAT next to the host (one
// shared WinUI/.NET runtime set — shell vs emulator payloads were 94%
// byte-identical). At the flat root there is exactly ONE
// templates\variable_registry.json and it is the HOST's full production
// registry (3507 variables). The emulator's tuned sim registry (140
// variables — all present BY NAME in the host file, but 25 simulation.base
// values intentionally differ) ships RENAMED to
// variable_registry.emulator.json and MUST win for the emulator process,
// otherwise the sim starts from the host's baselines (silent drift).
//
// RULE: when a loader asks for "variable_registry.json" and the emulator
// override file exists next to it, the override is returned. Every other
// file name resolves unchanged. The host folder never carries the override
// file, so host-side loading is untouched even if this assembly is shared.
namespace Halliburton.iCemEmule.Core;

internal static class SimFileResolver
{
    internal const string RegistryOverrideName = "variable_registry.emulator.json";

    internal static string Resolve(string templatesDir, string fileName)
    {
        if (string.Equals(fileName, "variable_registry.json", StringComparison.OrdinalIgnoreCase))
        {
            var emu = Path.Combine(templatesDir, RegistryOverrideName);
            if (File.Exists(emu)) return emu;
        }
        return Path.Combine(templatesDir, fileName);
    }
}
