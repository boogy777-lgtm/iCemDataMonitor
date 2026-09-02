// UnitConversion — конвертер единиц измерений для LAN-потока (ace_wire) и UI.
// Единый выпадающий список «Единицы измерений»: пересчёт ВСЕХ полей с единицами
// выполняется как на проводе (внешний декоратор UnitConvertingPayloadProvider),
// так и в UI (LanTab). Нативные единицы сигнала берутся из lantable_registry.json
// (sig.UoM). Контракт пересчёта: native → целевая-единица (ToTarget/Convert) и обратно
// целевая → native (ToWire, для CommitEdit). Целевая единица задаётся пресетом
// (UnitPreset) либо детальными настройками UnitSettings (новая вкладка UI «Единицы»).
// R1: никаких ссылок на MongoDB.

namespace Halliburton.iCemEmule.Core;

/// <summary>Доступные наборы единиц измерений (dropdown «Единицы измерений»).</summary>
public enum UnitPreset
{
    FirmwareDefault = 0, // прошивка: gpm, °C, ppg, psi
    LanNative = 1,       // нативные единицы LAN: bbl/min, °F, lb/gal
    Metric = 2,          // метрика: m³/min, °C, g/cc, bar
    Imperial = 3         // имперская: bbl/min, °F, ppg, psi
}

/// <summary>Детальные настройки единиц измерений (новая вкладка UI «Единицы»).
/// Preset задаёт базовый набор единиц; по-групповые поля *Override (null/пусто =
/// следовать пресету) позволяют точечно переопределить единицу конкретной группы,
/// напр. оставить gpm при Metric. Применяются в UnitConvertingPayloadProvider
/// (свойство Settings) и в LanTab (ToTarget/ToWire с UnitSettings).</summary>
public sealed class UnitSettings
{
    /// <summary>Базовый пресет (единицы по умолчанию для групп без Override).
    /// По умолчанию LanNative — реальный провод cRIO несёт нативные единицы (°F/bbl/min/lb-gal).</summary>
    public UnitPreset Preset { get; set; } = UnitPreset.LanNative;

    public string? FlowOverride { get; set; }          // null/пусто = следовать пресету
    public string? TemperatureOverride { get; set; }
    public string? DensityOverride { get; set; }
    public string? VolumeOverride { get; set; }
    public string? LevelOverride { get; set; }
    public string? PressureOverride { get; set; }

    /// <summary>Целевая единица группы: непустой Override иначе единица пресета. Для None → ""</summary>
    public string TargetUnit(UnitConversion.UnitGroup group)
    {
        string? ov = group switch
        {
            UnitConversion.UnitGroup.Flow => FlowOverride,
            UnitConversion.UnitGroup.Temperature => TemperatureOverride,
            UnitConversion.UnitGroup.Density => DensityOverride,
            UnitConversion.UnitGroup.Volume => VolumeOverride,
            UnitConversion.UnitGroup.Level => LevelOverride,
            UnitConversion.UnitGroup.Pressure => PressureOverride,
            _ => null
        };
        return string.IsNullOrWhiteSpace(ov) ? UnitConversion.PresetUnit(group, Preset) : ov;
    }

    /// <summary>Глубокая копия всех полей (snapshot настроек для вкладки UI).</summary>
    public UnitSettings Clone()
    {
        return new UnitSettings
        {
            Preset = Preset,
            FlowOverride = FlowOverride,
            TemperatureOverride = TemperatureOverride,
            DensityOverride = DensityOverride,
            VolumeOverride = VolumeOverride,
            LevelOverride = LevelOverride,
            PressureOverride = PressureOverride
        };
    }
}

/// <summary>Статический конвертер единиц (см. требования «Единый dropdown»).</summary>
public static class UnitConversion
{
    public enum UnitGroup { None, Flow, Temperature, Density, Volume, Level, Pressure }

    // ── базовые коэффициенты: сколько данной единицы содержится в одной канонической ──
    // (формула общего Convert: value × factor(to) / factor(from)).
    // Flow канон = bbl/min; 1 bbl/min = 42 gal/min (gpm) = 0.158987 m³/min.
    private static readonly IReadOnlyDictionary<string, double> FlowFactors =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["bbl/min"] = 1.0,
            ["gpm"]     = 42.0,              // 1 bbl = 42 gal
            ["gal/min"] = 42.0,              // gpm == gal/min (псевдонимы)
            ["m³/min"]  = 0.158987,
            ["m3/min"]  = 0.158987
        };

    // Volume канон = bbl; 1 bbl = 42 gal = 0.158987 m³.
    private static readonly IReadOnlyDictionary<string, double> VolumeFactors =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["bbl"] = 1.0,
            ["gal"] = 42.0,
            ["m³"]  = 0.158987,
            ["m3"]  = 0.158987
        };

    // Density канон = ppg (≡ lb/gal); 1 ppg = 0.119826 g/cc = 119.826 kg/m³.
    private static readonly IReadOnlyDictionary<string, double> DensityFactors =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["ppg"]    = 1.0,
            ["lb/gal"] = 1.0,                // ppg ≡ lb/gal (псевдонимы)
            ["g/cc"]   = 0.119826,
            ["g/cm3"]  = 0.119826,           // g/cm3 == g/cc (псевдонимы)
            ["kg/m³"]  = 119.826,
            ["kg/m3"]  = 119.826
        };

    // Level канон = ft; 1 ft = 0.3048 m.
    private static readonly IReadOnlyDictionary<string, double> LevelFactors =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["ft"] = 1.0,
            ["m"]  = 0.3048
        };

    // Pressure канон = psi; 1 psi = 0.0689476 bar = 6.89476 kPa = 0.00689476 MPa.
    private static readonly IReadOnlyDictionary<string, double> PressureFactors =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["psi"] = 1.0,
            ["bar"] = 0.0689476,
            ["kpa"] = 6.89476,               // case-insensitive: «kPa» попадает сюда же
            ["mpa"] = 0.00689476
        };

    // ── публичный API ────────────────────────────────────────────────────

    /// <summary>Человекочитаемая метка пресета для dropdown.</summary>
    public static string PresetLabel(UnitPreset preset)
    {
        return preset switch
        {
            UnitPreset.FirmwareDefault => "Прошивка (gpm, °C, ppg, psi)",
            UnitPreset.LanNative => "LAN native (bbl/min, °F, lb/gal)",
            UnitPreset.Metric => "Метрика (m³/min, °C, g/cc, bar)",
            UnitPreset.Imperial => "Имперская (bbl/min, °F, ppg, psi)",
            _ => "Прошивка (gpm, °C, ppg, psi)"
        };
    }

    /// <summary>Определяет группу единицы по строке из реестра (case-insensitive).
    /// «Отношенческие» составные единицы вида "X/Y" (масштабные коэффициенты:
    /// psi/ma, gal/sack, ft/min, degF/ma ...) НЕ конвертируются — только известный
    /// список конвертируемых составных (bbl/min, gal/min, m³/min, lb/gal, g/cc, kg/m³).
    /// Неконвертируемые бытовые единицы (mA, none, char, %, Hz, bpm) → None.</summary>
    public static UnitGroup GroupOf(string uom)
    {
        var u = CanonicalUom(uom);
        if (string.IsNullOrEmpty(u))
            return UnitGroup.None;

        if (u.Contains('/') && u is not ("bbl/min" or "gal/min" or "m3/min" or "lb/gal" or "g/cc" or "kg/m3"))
            return UnitGroup.None;

        if (u is ("bbl/min" or "gal/min" or "m3/min"))
            return UnitGroup.Flow;
        if (u is ("°f" or "°c"))
            return UnitGroup.Temperature;
        if (u is ("lb/gal" or "g/cc" or "kg/m3"))
            return UnitGroup.Density;
        if (u is ("bbl" or "gal" or "m3"))
            return UnitGroup.Volume;
        if (u is ("ft" or "m")) // точное совпадение: «m» не путаем с gpm/rpm/mph и «mA»
            return UnitGroup.Level;
        if (u is ("psi" or "bar" or "kpa" or "mpa"))
            return UnitGroup.Pressure;
        return UnitGroup.None;
    }

    /// <summary>True ⇔ сигнал подлежит конвертации (поле с единицами).</summary>
    public static bool IsConvertible(string uom) => GroupOf(uom) != UnitGroup.None;

    /// <summary>Каноническая форма единицы (алиасы → один эталон): gpm→gal/min,
    /// ppg→lb/gal, degF/f→°f, degC/c→°c, m³→m3, g/cm3→g/cc, kg/m³→kg/m3, psia→psi.
    /// «bpm» сознательно НЕ алиас (pump-rate сигналы не конвертируются).</summary>
    public static string CanonicalUom(string uom)
    {
        if (string.IsNullOrWhiteSpace(uom)) return string.Empty;
        var u = uom.Trim().ToLowerInvariant();
        return u switch
        {
            "gpm" => "gal/min",
            "m³/min" => "m3/min",
            "ppg" => "lb/gal",
            "g/cm3" => "g/cc",
            "kg/m³" => "kg/m3",
            "m³" => "m3",
            "degf" => "°f",
            "f" => "°f",
            "degc" => "°c",
            "c" => "°c",
            "psia" => "psi",
            _ => u
        };
    }

    /// <summary>Единица пресета (каноническая форма) для конвертируемой группы.</summary>
    public static string TargetUom(UnitGroup group, UnitPreset preset)
    {
        return group switch
        {
            UnitGroup.Flow => preset switch
            {
                UnitPreset.FirmwareDefault => "gal/min",
                UnitPreset.Metric => "m3/min",
                _ => "bbl/min"
            },
            UnitGroup.Temperature => preset switch
            {
                UnitPreset.LanNative or UnitPreset.Imperial => "°f",
                _ => "°c"
            },
            UnitGroup.Density => preset switch
            {
                UnitPreset.Metric => "g/cc",
                _ => "lb/gal"
            },
            UnitGroup.Volume => preset switch
            {
                UnitPreset.Metric => "m3",
                _ => "bbl"
            },
            UnitGroup.Level => preset switch
            {
                UnitPreset.Metric => "m",
                _ => "ft"
            },
            UnitGroup.Pressure => preset switch
            {
                UnitPreset.Metric => "bar",
                _ => "psi"
            },
            _ => string.Empty
        };
    }

    /// <summary>Отображаемая метка единицы пресета для колонки «UoM» (gpm/°C/ppg/bar...).</summary>
    public static string DisplayUnit(UnitGroup group, UnitPreset preset)
    {
        return group switch
        {
            UnitGroup.Flow => preset switch
            {
                UnitPreset.FirmwareDefault => "gpm",
                UnitPreset.Metric => "m³/min",
                _ => "bbl/min"
            },
            UnitGroup.Temperature => preset switch
            {
                UnitPreset.LanNative or UnitPreset.Imperial => "°F",
                _ => "°C"
            },
            UnitGroup.Density => preset switch
            {
                UnitPreset.LanNative => "lb/gal",
                UnitPreset.Metric => "g/cc",
                _ => "ppg"
            },
            UnitGroup.Volume => preset switch
            {
                UnitPreset.Metric => "m³",
                _ => "bbl"
            },
            UnitGroup.Level => preset switch
            {
                UnitPreset.Metric => "m",
                _ => "ft"
            },
            UnitGroup.Pressure => preset switch
            {
                UnitPreset.Metric => "bar",
                _ => "psi"
            },
            _ => string.Empty
        };
    }

    /// <summary>Единица пресета для конвертируемой группы (отображаемая форма, напр.
    /// "gpm", "°C", "m³"). Совпадает с DisplayUnit; результат Convert понимает через
    /// CanonicalUom, поэтому формы могут быть и «человеческими», и каноническими.</summary>
    public static string PresetUnit(UnitGroup group, UnitPreset preset) => DisplayUnit(group, preset);

    /// <summary>Допустимые единицы группы (для dropdown/фильтра вкладки «Единицы»).</summary>
    public static string[] UnitsOf(UnitGroup group)
    {
        return group switch
        {
            UnitGroup.Flow => new[] { "bbl/min", "gpm", "gal/min", "m³/min" },
            UnitGroup.Temperature => new[] { "°F", "°C" },
            UnitGroup.Density => new[] { "ppg", "lb/gal", "g/cc", "kg/m³" },
            UnitGroup.Volume => new[] { "bbl", "gal", "m³" },
            UnitGroup.Level => new[] { "ft", "m" },
            UnitGroup.Pressure => new[] { "psi", "bar", "kPa" },
            _ => Array.Empty<string>()
        };
    }

    /// <summary>Универсальная конверсия из fromUom в toUom. Пустые/неизвестные единицы,
    /// одна и та же единица (case-insensitive) или неконвертируемая группа → identity.
    /// Температура — аффинная (°F↔°C, НЕ линейный множитель); остальные группы —
    /// линейный множитель value × factor(to) / factor(from).</summary>
    public static double Convert(double value, string fromUom, string toUom)
    {
        var from = CanonicalUom(fromUom);
        var to = CanonicalUom(toUom);
        var group = GroupOf(from);

        if (group == UnitGroup.None ||
            string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) ||
            string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return value;

        // БАГ A: кросс-групповая конверсия — identity. Если целевая единица принадлежит
        // ДРУГОЙ группе (напр. Convert(v, "°F", "gpm")), возвращаем значение как есть:
        // аффинная ветка температуры применяется ТОЛЬКО к температурам, линейные группы
        // используют множители СВОЕЙ таблицы (чужая единица ⇒ factor 1.0, но делитель
        // factor(from) ≠ 1.0 искажал бы результат). Guard для ВСЕХ групп.
        if (GroupOf(to) != group)
            return value;

        if (group == UnitGroup.Temperature)
            return from == "°f" ? (value - 32.0) * 5.0 / 9.0   // °F → °C
                                : value * 9.0 / 5.0 + 32.0;     // °C → °F

        var factors = FactorsOf(group);
        return value * Factor(to, factors) / Factor(from, factors);
    }

    /// <summary>Нативное значение (nativeUom) → значение в целевых единицах настроек UnitSettings.</summary>
    public static double ToTarget(double value, string nativeUom, UnitSettings s)
        => Convert(value, nativeUom, s.TargetUnit(GroupOf(nativeUom)));

    /// <summary>Значение в целевых единицах настроек UnitSettings → нативное значение (nativeUom).</summary>
    public static double ToWire(double presetValue, string nativeUom, UnitSettings s)
        => Convert(presetValue, s.TargetUnit(GroupOf(nativeUom)), nativeUom);

    /// <summary>Нативное значение (nativeUom) → значение в единицах пресета.</summary>
    public static double ToTarget(double value, UnitGroup group, string nativeUom, UnitPreset preset)
        => Convert(value, nativeUom, PresetUnit(group, preset));

    /// <summary>Значение в единицах пресета → нативное значение (nativeUom),
    /// для CommitEdit: оператор вводит в единицах пресета, в sink храним wire-единицы.</summary>
    public static double ToWire(double presetValue, UnitGroup group, string nativeUom, UnitPreset preset)
        => Convert(presetValue, PresetUnit(group, preset), nativeUom);

    /// <summary>True ⇔ сигнал нужно конвертировать при заданных настройках.
    /// Пресет FirmwareDefault = «как передаёт прошивка»: конвертируются только
    /// legacy-нативные единицы (bbl/min, °F); firmware-native единицы
    /// (gal/min, °c, lb/gal, g/cc, bbl, ft, psi) остаются identity.</summary>
    public static bool NeedsConversion(string nativeUom, UnitSettings s)
    {
        var g = GroupOf(nativeUom);
        if (g == UnitGroup.None) return false;
        var native = CanonicalUom(nativeUom);
        var target = CanonicalUom(s.TargetUnit(g));
        if (string.Equals(native, target, StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Preset == UnitPreset.FirmwareDefault)
            return native is not ("gal/min" or "°c" or "lb/gal" or "g/cc" or "bbl" or "ft" or "psi");
        return true;
    }

    // ── внутренняя конверсия (в обе стороны) ─────────────────────────────

    // Таблица множителей группы (для Convert). Для конвертируемых групп всегда одна из пяти.
    private static IReadOnlyDictionary<string, double> FactorsOf(UnitGroup group) => group switch
    {
        UnitGroup.Flow => FlowFactors,
        UnitGroup.Volume => VolumeFactors,
        UnitGroup.Density => DensityFactors,
        UnitGroup.Level => LevelFactors,
        UnitGroup.Pressure => PressureFactors,
        _ => FlowFactors // недостижимо: группа проверена выше (None → identity)
    };

    // Неизвестная единица → множитель 1.0 (identity).
    private static double Factor(string unit, IReadOnlyDictionary<string, double> factors)
        => factors.TryGetValue(unit, out var f) ? f : 1.0;
}
