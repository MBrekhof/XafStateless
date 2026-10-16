using System.Text;

namespace XafStateless.Engine;

/// <summary>How the diagram marks states (XSM-027/028): colored by kind, problem states in red, the current state with a
/// thick border, and optional state/transition details (roles, wait duration, locked fields, criteria). Null parts draw nothing extra.</summary>
public sealed record DiagramStyle(bool ByKind = false, IReadOnlySet<string>? Problems = null, string? Current = null, bool Details = false);

public static class WorkflowMermaid
{
    // Pastel fills with explicit dark text, so the diagram reads the same on XAF's light and dark themes.
    static readonly Dictionary<StateKind, string> KindStyle = new()
    {
        [StateKind.Manual] = "fill:#e3f2fd,stroke:#1e88e5,color:#0d2a4a",
        [StateKind.Task] = "fill:#fff3e0,stroke:#fb8c00,color:#4a2a00",
        [StateKind.Wait] = "fill:#f3e5f5,stroke:#8e24aa,color:#3a0f45",
        [StateKind.Decision] = "fill:#fffde7,stroke:#c0a000,color:#3d3300",
        [StateKind.Final] = "fill:#e8f5e9,stroke:#43a047,color:#12361a",
    };
    const string ProblemStyle = "fill:#fde2e2,stroke:#c62828,stroke-width:3px,color:#7f0000";
    const string CurrentStyle = "stroke:#0d47a1,stroke-width:5px";

    /// <summary>The style Mermaid applies to a state of <paramref name="kind"/>; the legend uses the same colors.</summary>
    public static string StyleOf(StateKind kind) => KindStyle[kind];

    // Our own stateDiagram-v2 text straight from the definition. It needs no machine, so a broken draft still draws.
    // Kept simple: own formatter instead of Stateless.Graph.MermaidGraph, because its escaping of spaces is unverified
    // and it needs a constructible machine.
    public static string Format(WorkflowDef def, DiagramStyle? style = null)
    {
        // A broken draft (duplicate keys, an unresolved initial state) must still draw: assign one
        // id per distinct key (first occurrence wins) and skip anything that doesn't resolve.
        var details = style?.Details == true;
        var idOf = new Dictionary<string, string>();
        var captionOf = new List<(string Label, string Id)>();
        foreach (var s in def.States)
        {
            if (!idOf.TryAdd(s.Key, $"s{idOf.Count}")) continue;
            captionOf.Add((StateLabel(s, details), idOf[s.Key]));
        }

        var sb = new StringBuilder("stateDiagram-v2\n");
        foreach (var (label, id) in captionOf)
            sb.Append($"    state \"{label}\" as {id}\n");
        if (idOf.TryGetValue(def.InitialState, out var initial))
            sb.Append($"    [*] --> {initial}\n");
        foreach (var t in def.Transitions)
            if (idOf.TryGetValue(t.From, out var from) && idOf.TryGetValue(t.To, out var to))
                sb.Append($"    {from} --> {to} : {TransitionLabel(t, details)}\n");
        foreach (var s in def.States)
            if (s.Kind == StateKind.Final && idOf.TryGetValue(s.Key, out var final))
                sb.Append($"    {final} --> [*]\n");
        if (style != null) AppendStyle(sb, def, idOf, style);
        return sb.ToString();
    }

    static string StateLabel(StateDef s, bool details)
    {
        var lines = new List<string> { Escape(s.Caption) };
        if (details)
        {
            if (s.Kind == StateKind.Task && !string.IsNullOrWhiteSpace(s.TaskRole)) lines.Add(Escape(s.TaskRole));
            if (s.Kind == StateKind.Wait && s.WaitMinutes is > 0) lines.Add($"{s.WaitMinutes} min");
            if (s.LockedMembers.Count > 0) lines.Add($"locks {string.Join(", ", s.LockedMembers.Select(Escape))}");
        }
        return string.Join("<br/>", lines);
    }

    static string TransitionLabel(TransitionDef t, bool details)
    {
        var parts = new List<string> { Escape(t.Trigger) };
        if (details)
        {
            if (!string.IsNullOrWhiteSpace(t.RequiredRole)) parts.Add($"({Escape(t.RequiredRole)})");
            if (!string.IsNullOrWhiteSpace(t.Criteria)) parts.Add($"[{Escape(t.Criteria)}]");
        }
        return string.Join(" ", parts);
    }

    // classDef/class statements, defined in the order kind, problem, current, so a later style wins where they overlap.
    static void AppendStyle(StringBuilder sb, WorkflowDef def, Dictionary<string, string> idOf, DiagramStyle style)
    {
        void Mark(string name, string css, IEnumerable<string> keys)
        {
            var ids = keys.Where(idOf.ContainsKey).Select(k => idOf[k]).Distinct().ToList();
            if (ids.Count == 0) return;
            sb.Append($"    classDef {name} {css}\n");
            sb.Append($"    class {string.Join(",", ids)} {name}\n");
        }
        if (style.ByKind)
            foreach (var kind in Enum.GetValues<StateKind>())
                Mark($"kind{kind}", KindStyle[kind], def.States.Where(s => s.Kind == kind).Select(s => s.Key));
        if (style.Problems != null) Mark("problem", ProblemStyle, style.Problems);
        if (style.Current != null) Mark("current", CurrentStyle, [style.Current]);
    }

    static string Escape(string s) => s.Replace("\"", "'").Replace(":", " ").Replace(";", " ").Replace("\r", " ").Replace("\n", " ");
}
