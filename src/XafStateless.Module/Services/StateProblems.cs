using System.Runtime.CompilerServices;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>The current validation problems of each state entity, keyed by the instance (XSM-027): every editing session has
/// its own instances, so sessions never see each other's unsaved problems, and a closed view's entries are collected with it.
/// Written by the definition's controller after each validation; read by <see cref="WorkflowState.Problem"/>.</summary>
public static class StateProblems
{
    static readonly ConditionalWeakTable<WorkflowState, string> Table = new();

    public static void Set(WorkflowState state, string? problem)
    {
        if (problem is null) Table.Remove(state);
        else Table.AddOrUpdate(state, problem);
    }

    public static string? Get(WorkflowState state) => Table.TryGetValue(state, out var problem) ? problem : null;
}
