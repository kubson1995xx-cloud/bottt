using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Apex.Core.Engine;
namespace Apex.Core.Naval;
internal static class NavalBrokerRaw
{
    internal static void Require(JsonElement raw)
    {
        var value=JsonSerializer.Deserialize<EngineState>(raw.GetRawText(),NavalJson.Options)??throw NavalBrokerValidation.Invalid();
        using var canonical=JsonDocument.Parse(NavalJson.Serialize(value));Shape(raw,canonical.RootElement);
    }
    private static void Shape(JsonElement raw,JsonElement expected)
    {
        if(raw.ValueKind!=expected.ValueKind)throw NavalBrokerValidation.Invalid();
        if(raw.ValueKind==JsonValueKind.Object)
        {
            var names=expected.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal);
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in raw.EnumerateObject())
            {if(!names.Remove(p.Name)||!seen.Add(p.Name))throw NavalBrokerValidation.Invalid();Shape(p.Value,expected.GetProperty(p.Name));}
            if(names.Count!=0)throw NavalBrokerValidation.Invalid();
        }
        else if(raw.ValueKind==JsonValueKind.Array)
        {
            if(raw.GetArrayLength()!=expected.GetArrayLength())throw NavalBrokerValidation.Invalid();
            for(int n=0;n<raw.GetArrayLength();n++)Shape(raw[n],expected[n]);
        }
    }
}
