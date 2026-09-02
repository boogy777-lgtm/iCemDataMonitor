using iCemDataMonitor.Sdk.Models;

namespace iCemDataMonitor.Sdk;

public interface IVariableCatalog
{
    IReadOnlyList<VariableDefinition> GetCatalog();
    string GuessUom(string variableName);
    string GetColor(int index);
    (string Display, string Uom, int? ModbusOffset)? Lookup(string name);
}
