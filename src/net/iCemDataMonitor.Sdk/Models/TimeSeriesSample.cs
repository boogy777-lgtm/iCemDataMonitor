namespace iCemDataMonitor.Sdk.Models;

public readonly record struct TimeSeriesSample(
    DateTime Timestamp,
    IReadOnlyDictionary<string, double> Values
);
