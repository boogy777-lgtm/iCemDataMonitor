namespace iCemDataMonitor.Sdk.Models;

public readonly record struct ModbusRegisterEntry(
    string VariableName,
    int RegisterAddress
);
