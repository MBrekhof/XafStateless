using System.Text;

namespace XafStateless.Engine;

public static class WorkflowMermaid
{
    // Our own stateDiagram-v2 text straight from the definition. It needs no machine, so a broken draft still draws.
    // ponytail: own formatter instead of Stateless.Graph.MermaidGraph, because its escaping of spaces is unverified
    // and it needs a constructible machine.
    public static string Format(WorkflowDef def)
    {
        // A broken draft (duplicate keys, an unresolved initial state) must still draw: assign one
        // id per distinct key (first occurrence wins) and skip anything that doesn't resolve.
        var idOf = new Dictionary<string, string>();
        var captionOf = new List<(string Caption, string Id)>();
        foreach (var s in def.States)
        {
            if (!idOf.TryAdd(s.Key, $"s{idOf.Count}")) continue;
            captionOf.Add((s.Caption, idOf[s.Key]));
        }

        var sb = new StringBuilder("stateDiagram-v2\n");
        foreach (var (caption, id) in captionOf)
            sb.Append($"    state \"{Escape(caption)}\" as {id}\n");
        if (idOf.TryGetValue(def.InitialState, out var initial))
            sb.Append($"    [*] --> {initial}\n");
        foreach (var t in def.Transitions)
            if (idOf.TryGetValue(t.From, out var from) && idOf.TryGetValue(t.To, out var to))
                sb.Append($"    {from} --> {to} : {Escape(t.Trigger)}\n");
        foreach (var s in def.States)
            if (s.Kind == StateKind.Final && idOf.TryGetValue(s.Key, out var final))
                sb.Append($"    {final} --> [*]\n");
        return sb.ToString();
    }

    static string Escape(string s) => s.Replace("\"", "'").Replace(":", " ").Replace("\r", " ").Replace("\n", " ");
}
