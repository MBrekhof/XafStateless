namespace XafStateless.Engine;

public interface IWorkflowEnvironment
{
    bool RoleExists(string role);
    string? CheckCriteria(string criteria);      // null = ok, else the error text
    string? CheckEffect(EffectDef effect);       // null = ok (member exists, writable, value converts; template placeholders exist)
    string? CheckMember(string member);          // null = ok (a member effects and locks may use)
    string? CheckRuleSet(RuleSetDef ruleSet) => null;   // null = ok (expressions parse and target members/types match)
}

/// <summary>One validation finding. <see cref="State"/> names the state it is about (null: the definition as a whole, or
/// several states), so a designer UI can mark that state.</summary>
public sealed record Finding(string Message, string? State = null);

public static class WorkflowValidator
{
    /// <summary>The findings' messages; empty when the definition is valid.</summary>
    public static IReadOnlyList<string> Validate(WorkflowDef def, IWorkflowEnvironment env) =>
        Check(def, env).Select(f => f.Message).ToArray();

    /// <summary>Every finding, each tagged with its state where it has one. The finishing rules report a cause once (XSM-024):
    /// no Final state at all is one definition-level finding; a dead end is reported on itself, and the states whose paths
    /// only lead into it are not blamed; "can never finish" is left for genuine loops without an exit.</summary>
    public static IReadOnlyList<Finding> Check(WorkflowDef def, IWorkflowEnvironment env)
    {
        var errs = new List<Finding>();
        // Required texts, checked here because validation also runs on unsaved edits, before XAF's required-field rules.
        if (string.IsNullOrWhiteSpace(def.Name)) errs.Add(new("the definition has no name"));
        foreach (var s in def.States)
        {
            if (string.IsNullOrWhiteSpace(s.Key)) errs.Add(new($"state '{s.Caption}' has no key"));
            else if (string.IsNullOrWhiteSpace(s.Caption)) errs.Add(new($"state '{s.Key}' has no caption", s.Key));
        }
        foreach (var t in def.Transitions.Where(t => string.IsNullOrWhiteSpace(t.Trigger)))
            errs.Add(new($"a transition from '{t.From}' has no action name", string.IsNullOrWhiteSpace(t.From) ? null : t.From));
        var keys = new HashSet<string>();
        foreach (var s in def.States) if (!keys.Add(s.Key)) errs.Add(new($"duplicate state '{s.Key}'", s.Key));
        if (!keys.Contains(def.InitialState)) errs.Add(new($"initial state '{def.InitialState}' does not exist"));
        foreach (var t in def.Transitions)
            foreach (var k in new[] { t.From, t.To }.Where(k => !keys.Contains(k)))
                errs.Add(new($"transition '{t.Trigger}' refers to unknown state '{k}'", keys.Contains(t.From) ? t.From : null));
        foreach (var g in def.Transitions.GroupBy(t => (t.From, t.Trigger)).Where(g => g.Count() > 1))
            errs.Add(new($"duplicate trigger '{g.Key.Trigger}' from '{g.Key.From}'", keys.Contains(g.Key.From) ? g.Key.From : null));
        if (errs.Count > 0) return errs;   // graph rules below assume a well-formed graph

        CheckRuleSets(def, env, errs);

        var reach = Bfs([def.InitialState], k => def.From(k).Select(t => t.To));
        IEnumerable<string> Into(string k) => def.Transitions.Where(t => t.To == k).Select(t => t.From);
        var finals = def.States.Where(s => s.Kind == StateKind.Final).Select(s => s.Key).ToList();
        var deadEnds = def.States.Where(s => s.Kind != StateKind.Final && !def.From(s.Key).Any()).Select(s => s.Key).ToList();
        var finishers = Bfs(finals, Into);
        var intoDeadEnd = Bfs(deadEnds, Into);   // a state that can reach a dead end: the dead end is the cause
        if (finals.Count == 0) errs.Add(new("no state is Final, so the workflow can never end: set the Kind of the state where it ends to Final"));
        foreach (var s in def.States)
        {
            var outs = def.From(s.Key).ToList();
            void Add(string message) => errs.Add(new(message, s.Key));
            if (!reach.Contains(s.Key)) Add($"'{s.Key}' is unreachable");
            if (s.Kind == StateKind.Final) { if (outs.Count > 0) Add($"final state '{s.Key}' has transitions"); }
            else if (outs.Count == 0) Add($"'{s.Key}' has no way out: make it Final, or add a transition from it");
            else if (finals.Count > 0 && !finishers.Contains(s.Key) && !intoDeadEnd.Contains(s.Key))
                Add($"'{s.Key}' can never finish: its paths loop without reaching a Final state");
            if (s.Kind is StateKind.Decision or StateKind.Wait && outs.Any(t => t.To == s.Key))
                Add($"'{s.Key}' cannot transition to itself");
            switch (s.Kind)
            {
                case StateKind.Wait:
                    if (outs.Count != 1 || outs[0].Criteria != null || outs[0].RequiredRole != null)
                        Add($"wait '{s.Key}' needs exactly one unguarded exit");
                    if (s.WaitMinutes is not > 0) Add($"wait '{s.Key}' needs a duration");
                    break;
                case StateKind.Task:
                    if (string.IsNullOrWhiteSpace(s.TaskRole)) Add($"task '{s.Key}' needs a role");
                    else if (!env.RoleExists(s.TaskRole)) Add($"role '{s.TaskRole}' does not exist");
                    foreach (var t in outs.Where(t => t.RequiredRole != null))
                        Add($"task '{s.Key}' outcome '{t.Trigger}' cannot require a role");
                    break;
                case StateKind.Decision when outs.Count > 0:
                    foreach (var g in outs.GroupBy(t => t.Order).Where(g => g.Count() > 1))
                        Add($"decision '{s.Key}' has duplicate order {g.Key}");
                    foreach (var t in outs.Where(t => t.RequiredRole != null))
                        Add($"decision '{s.Key}' branch '{t.Trigger}' cannot require a role");
                    if (outs[^1].Criteria != null) Add($"decision '{s.Key}' needs a final else-branch without criteria");
                    foreach (var t in outs.Take(outs.Count - 1).Where(t => t.Criteria == null))
                        Add($"decision '{s.Key}' branch '{t.Trigger}' needs criteria");
                    break;
            }
            foreach (var e in s.Effects)
            {
                if (e.Kind == EffectKind.Notify && e.Target == EffectDef.Requester)
                {
                    if (env.RoleExists(EffectDef.Requester)) Add($"a role named '{EffectDef.Requester}' conflicts with the requester recipient");
                }
                else if (e.Kind == EffectKind.Notify && !env.RoleExists(e.Target)) Add($"role '{e.Target}' does not exist");
                else if (e.Kind == EffectKind.RunRules && def.RuleSet(e.Target) == null)
                    Add($"effect on '{s.Key}': rule set '{e.Target}' does not exist");
                if (env.CheckEffect(e) is { } err) Add($"effect on '{s.Key}': {err}");
            }
            foreach (var m in s.LockedMembers)
                if (env.CheckMember(m) is { } err) Add($"lock on '{m}': {err}");
        }
        foreach (var t in def.Transitions)
        {
            if (t.RequiredRole != null && !env.RoleExists(t.RequiredRole)) errs.Add(new($"role '{t.RequiredRole}' does not exist", t.From));
            if (t.Criteria != null && env.CheckCriteria(t.Criteria) is { } err) errs.Add(new($"criteria on '{t.Trigger}': {err}", t.From));
        }
        // Spans several states; one nullable State cannot mark them all, so it stays definition-level.
        if (DecisionLoop(def) is { } loop) errs.Add(new($"decisions {string.Join(" → ", loop.Select(k => $"'{k}'"))} form a loop"));
        return errs;
    }

    static void CheckRuleSets(WorkflowDef def, IWorkflowEnvironment env, List<Finding> errs)
    {
        var rsKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rs in def.RuleSetList)
        {
            if (string.IsNullOrWhiteSpace(rs.Key)) { errs.Add(new("a rule set has no key")); continue; }
            if (!rsKeys.Add(rs.Key)) errs.Add(new($"duplicate rule set '{rs.Key}'"));
            if (rs.Rules.Count == 0) errs.Add(new($"rule set '{rs.Key}' has no rules"));

            var paramNames = new HashSet<string>(StringComparer.Ordinal);
            var structuralOk = rs.Rules.Count > 0;
            foreach (var p in rs.Params)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) { errs.Add(new($"rule set '{rs.Key}' has a parameter with no name")); structuralOk = false; }
                else if (p.Name == RuleSetEvaluator.SubjectParamName) { errs.Add(new($"rule set '{rs.Key}' parameter '{RuleSetEvaluator.SubjectParamName}' is reserved")); structuralOk = false; }
                else if (!paramNames.Add(p.Name)) { errs.Add(new($"rule set '{rs.Key}' has duplicate parameter '{p.Name}'")); structuralOk = false; }
                if (string.IsNullOrWhiteSpace(p.Expression)) { errs.Add(new($"rule set '{rs.Key}' parameter '{p.Name}' has no expression")); structuralOk = false; }
            }

            var ruleNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rs.Rules)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) { errs.Add(new($"rule set '{rs.Key}' has a rule with no name")); structuralOk = false; }
                else if (!ruleNames.Add(r.Name)) { errs.Add(new($"rule set '{rs.Key}' has duplicate rule '{r.Name}'")); structuralOk = false; }
                if (string.IsNullOrWhiteSpace(r.Expression)) { errs.Add(new($"rule set '{rs.Key}' rule '{r.Name}' has no condition")); structuralOk = false; }
                if (string.IsNullOrWhiteSpace(r.TargetMember)) { errs.Add(new($"rule set '{rs.Key}' rule '{r.Name}' has no target member")); structuralOk = false; }
                if (string.IsNullOrWhiteSpace(r.OutputExpression)) { errs.Add(new($"rule set '{rs.Key}' rule '{r.Name}' has no output expression")); structuralOk = false; }
            }
            foreach (var g in rs.Rules.GroupBy(r => r.Order).Where(g => g.Count() > 1))
                errs.Add(new($"rule set '{rs.Key}' has duplicate order {g.Key}"));

            if (structuralOk && env.CheckRuleSet(rs) is { } envErr)
                errs.Add(new(envErr));
        }
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
