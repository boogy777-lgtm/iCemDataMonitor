namespace iCemDataMonitor.Sdk.Models;

public readonly record struct VariableDefinition(
    string Name,
    string Description,
    string Uom,
    string Color,
    int Position,
    bool IsSpare,
    string Source
);
