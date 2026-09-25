using System.Text;

namespace XafStateless.Engine;

public static class WorkflowMermaid
{
    // Our own stateDiagram-v2 text straight from the definition. It needs no machine, so a broken draft still draws.
    // ponytail: own formatter instead of Stateless.Graph.MermaidGraph, because its escaping of spaces is unverified
    // and it needs a constructible machine.
    public static string Format(WorkflowDef def)
    {
        var ids = def.States.Select((s, i) => (s.Key, s.Caption, Id: $"s{i}")).ToList();
        var idOf = ids.ToDictionary(x => x.Key, x => x.Id);

        var sb = new StringBuilder("stateDiagram-v2\n");
        foreach (var s in ids)
            sb.Append($"    state \"{Escape(s.Caption)}\" as {s.Id}\n");
        sb.Append($"    [*] --> {idOf[def.InitialState]}\n");
        foreach (var t in def.Transitions)
            if (idOf.TryGetValue(t.From, out var from) && idOf.TryGetValue(t.To, out var to))
                sb.Append($"    {from} --> {to} : {Escape(t.Trigger)}\n");
        foreach (var s in def.States)
            if (s.Kind == StateKind.Final)
                sb.Append($"    {idOf[s.Key]} --> [*]\n");
        return sb.ToString();
    }

    static string Escape(string s) => s.Replace("\"", "'").Replace(":", " ").Replace("\r", " ").Replace("\n", " ");
}
