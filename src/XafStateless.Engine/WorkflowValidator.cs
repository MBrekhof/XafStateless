namespace XafStateless.Engine;

public interface IWorkflowEnvironment
{
    bool RoleExists(string role);
    string? CheckCriteria(string criteria);      // null = ok, else the error text
    string? CheckEffect(EffectDef effect);       // null = ok (member exists, writable, value converts; template placeholders exist)
}

public static class WorkflowValidator
{
    public static IReadOnlyList<string> Validate(WorkflowDef def, IWorkflowEnvironment env)
    {
        var errs = new List<string>();
        var keys = new HashSet<string>();
        foreach (var s in def.States) if (!keys.Add(s.Key)) errs.Add($"duplicate state '{s.Key}'");
        if (!keys.Contains(def.InitialState)) errs.Add($"initial state '{def.InitialState}' does not exist");
        foreach (var t in def.Transitions)
            foreach (var k in new[] { t.From, t.To }.Where(k => !keys.Contains(k)))
                errs.Add($"transition '{t.Trigger}' refers to unknown state '{k}'");
        foreach (var g in def.Transitions.GroupBy(t => (t.From, t.Trigger)).Where(g => g.Count() > 1))
            errs.Add($"duplicate trigger '{g.Key.Trigger}' from '{g.Key.From}'");
        if (errs.Count > 0) return errs;   // graph rules below assume a well-formed graph

        var reach = Bfs([def.InitialState], k => def.From(k).Select(t => t.To));
        var finishers = Bfs(def.States.Where(s => s.Kind == StateKind.Final).Select(s => s.Key),
                             k => def.Transitions.Where(t => t.To == k).Select(t => t.From));
        foreach (var s in def.States)
        {
            var outs = def.From(s.Key).ToList();
            if (!reach.Contains(s.Key)) errs.Add($"'{s.Key}' is unreachable");
            if (s.Kind == StateKind.Final) { if (outs.Count > 0) errs.Add($"final state '{s.Key}' has transitions"); }
            else if (outs.Count == 0) errs.Add($"'{s.Key}' has no way out");
            else if (!finishers.Contains(s.Key)) errs.Add($"'{s.Key}' can never finish");
            if (s.Kind is StateKind.Decision or StateKind.Wait && outs.Any(t => t.To == s.Key))
                errs.Add($"'{s.Key}' cannot transition to itself");
            switch (s.Kind)
            {
                case StateKind.Wait:
                    if (outs.Count != 1 || outs[0].Criteria != null || outs[0].RequiredRole != null)
                        errs.Add($"wait '{s.Key}' needs exactly one unguarded exit");
                    if (s.WaitMinutes is not > 0) errs.Add($"wait '{s.Key}' needs a duration");
                    break;
                case StateKind.Task:
                    if (string.IsNullOrWhiteSpace(s.TaskRole)) errs.Add($"task '{s.Key}' needs a role");
                    else if (!env.RoleExists(s.TaskRole)) errs.Add($"role '{s.TaskRole}' does not exist");
                    foreach (var t in outs.Where(t => t.RequiredRole != null))
                        errs.Add($"task '{s.Key}' outcome '{t.Trigger}' cannot require a role");
                    break;
                case StateKind.Decision when outs.Count > 0:
                    foreach (var g in outs.GroupBy(t => t.Order).Where(g => g.Count() > 1))
                        errs.Add($"decision '{s.Key}' has duplicate order {g.Key}");
                    foreach (var t in outs.Where(t => t.RequiredRole != null))
                        errs.Add($"decision '{s.Key}' branch '{t.Trigger}' cannot require a role");
                    if (outs[^1].Criteria != null) errs.Add($"decision '{s.Key}' needs a final else-branch without criteria");
                    foreach (var t in outs.Take(outs.Count - 1).Where(t => t.Criteria == null))
                        errs.Add($"decision '{s.Key}' branch '{t.Trigger}' needs criteria");
                    break;
            }
            foreach (var e in s.Effects)
            {
                if (e.Kind == EffectKind.Notify && !env.RoleExists(e.Target)) errs.Add($"role '{e.Target}' does not exist");
                if (env.CheckEffect(e) is { } err) errs.Add($"effect on '{s.Key}': {err}");
            }
        }
        foreach (var t in def.Transitions)
        {
            if (t.RequiredRole != null && !env.RoleExists(t.RequiredRole)) errs.Add($"role '{t.RequiredRole}' does not exist");
            if (t.Criteria != null && env.CheckCriteria(t.Criteria) is { } err) errs.Add($"criteria on '{t.Trigger}': {err}");
        }
        if (DecisionLoop(def) is { } loop) errs.Add($"decisions {string.Join(" → ", loop.Select(k => $"'{k}'"))} form a loop");
        return errs;
    }

    static HashSet<string> Bfs(IEnumerable<string> start, Func<string, IEnumerable<string>> next)
    {
        var seen = new HashSet<string>(start); var q = new Queue<string>(seen);
        while (q.TryDequeue(out var k)) foreach (var n in next(k)) if (seen.Add(n)) q.Enqueue(n);
        return seen;
    }

    static List<string>? DecisionLoop(WorkflowDef def)
    {
        var decisions = def.States.Where(s => s.Kind == StateKind.Decision).Select(s => s.Key).ToHashSet();
        var done = new HashSet<string>();
        List<string>? Dfs(string k, List<string> path)
        {
            if (path.Contains(k)) return path[path.IndexOf(k)..];
            if (!done.Add(k)) return null;
            path.Add(k);
            foreach (var t in def.From(k).Where(t => decisions.Contains(t.To)))
                if (Dfs(t.To, path) is { } c) return c;
            path.RemoveAt(path.Count - 1);
            return null;
        }
        return decisions.Select(d => Dfs(d, [])).FirstOrDefault(c => c != null);
    }
}
