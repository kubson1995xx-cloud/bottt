using System.Text.Json;
namespace Apex.Adapters.Okx.Execution;

internal static partial class OkxObservationJson
{
    // A friend-only, structure-only observer. Never values, raw JSON, indices,
    // recursive properties or a changed parser result. No ordinary caller opts in.
    private static void NotifyPrivateSchema(JsonElement row,string knownFields,Action<string?,string,int> observer)
    {
        try
        {
            var schema=knownFields.Split(' ').ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groups=new Dictionary<(string Name,string Kind),int>();int properties=0;
            foreach(var p in row.EnumerateObject())
            {
                if(++properties>256){observer(null,"LIMIT_WITHHELD",0);return;}
                string name=p.Name;
                if(schema.Contains(name))continue;
                if(name.Length is <2 or >48||name[0] is <'a' or >'z'||name.Any(c=>c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z')))
                {observer(null,"IDENTIFIER_WITHHELD",0);return;}
                var key=(name,Kind(p.Value));
                if(!groups.ContainsKey(key)&&groups.Count==32){observer(null,"LIMIT_WITHHELD",0);return;}
                groups[key]=groups.GetValueOrDefault(key)+1;
            }
            foreach(var g in groups.OrderBy(g=>g.Key.Name,StringComparer.Ordinal).ThenBy(g=>g.Key.Kind,StringComparer.Ordinal))
                observer(g.Key.Name,g.Key.Kind,g.Value);
        }
        catch { /* Observer failures cannot alter strict rejection or diagnostics. */ }
    }
}
