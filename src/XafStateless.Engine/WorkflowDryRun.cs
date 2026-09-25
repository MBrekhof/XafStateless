namespace XafStateless.Engine;

public static class WorkflowDryRun
{
    // Walks a path of triggers from the initial state with fixed criteria answers; never persists. Returns steps or the refusal.
    public static (IReadOnlyList<Step> Steps, string? Error) Run(WorkflowDef def, IEnumerable<string> triggers, Func<string, bool> criteria)
    {
        var state = def.InitialState;
        var machine = new WorkflowMachine(def, () => state, v => state = v, criteria);
        var steps = new List<Step>();
        try
        {
            steps.AddRange(machine.EnterInitial());
            foreach (var trigger in triggers)
                steps.AddRange(machine.Fire(trigger));
        }
        catch (InvalidOperationException ex)
        {
            return (steps, ex.Message);
        }
        return (steps, null);
    }
}
