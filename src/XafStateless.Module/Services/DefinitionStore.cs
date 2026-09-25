using System.Collections.Concurrent;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using Microsoft.EntityFrameworkCore;
using System.Transactions;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>Singleton. Published and Retired definitions never change (DefinitionGuard), so their snapshots are cached
/// by id with no invalidation. A snapshot is built from the database rows (<see cref="RawSql"/>), never from the object
/// space's tracked entities, which may hold unsaved or rolled-back state. It is cached only when read outside any
/// transaction (a DbTransaction, an ambient or an explicitly enlisted System.Transactions one) (so it is committed data) and the definition is not a Draft.</summary>
public sealed class DefinitionStore
{
    readonly ConcurrentDictionary<Guid, WorkflowDef> cache = new();

    public WorkflowDef Get(IObjectSpace os, Guid definitionId)
    {
        if (cache.TryGetValue(definitionId, out var cached)) return cached;
        var ctx = (os as EFCoreObjectSpace)?.DbContext ?? throw new NotSupportedException("DefinitionStore needs an EF Core object space.");
        var (def, state) = Read(ctx, definitionId);
        return state == DefinitionState.Draft || InTransaction(ctx) ? def : cache.GetOrAdd(definitionId, def);
    }

    public WorkflowDefinition? Published(IObjectSpace os, string name) =>
        os.FirstOrDefault<WorkflowDefinition>(d => d.Name == name && d.State == DefinitionState.Published);

    static bool InTransaction(DbContext ctx) =>
        System.Transactions.Transaction.Current != null || ctx.Database.CurrentTransaction != null || ctx.Database.GetEnlistedTransaction() != null;

    static (WorkflowDef Def, DefinitionState State) Read(DbContext ctx, Guid id)
    {
        const string Id = nameof(WorkflowDefinition.ID);
        var head = RawSql.Rows<WorkflowDefinition>(ctx, Id, [id], nameof(WorkflowDefinition.Name), nameof(WorkflowDefinition.Version),
                nameof(WorkflowDefinition.InitialStateKey), nameof(WorkflowDefinition.State)).SingleOrDefault()
            ?? throw new InvalidOperationException($"Workflow definition {id} does not exist.");
        var states = RawSql.Rows<WorkflowState>(ctx, RawSql.ForeignKey<WorkflowState>(ctx, nameof(WorkflowState.Definition)), [id],
            Id, nameof(WorkflowState.Key), nameof(WorkflowState.Caption), nameof(WorkflowState.Kind), nameof(WorkflowState.TaskRole), nameof(WorkflowState.WaitMinutes), nameof(WorkflowState.LockedMembers));
        var stateFk = RawSql.ForeignKey<WorkflowEffect>(ctx, nameof(WorkflowEffect.State));
        var effects = RawSql.Rows<WorkflowEffect>(ctx, stateFk, states.Select(s => (Guid)s[0]!),
                stateFk, nameof(WorkflowEffect.Order), nameof(WorkflowEffect.Kind), nameof(WorkflowEffect.Target), nameof(WorkflowEffect.Value))
            .ToLookup(e => (Guid)e[0]!);
        var transitions = RawSql.Rows<WorkflowTransition>(ctx, RawSql.ForeignKey<WorkflowTransition>(ctx, nameof(WorkflowTransition.Definition)), [id],
            nameof(WorkflowTransition.FromKey), nameof(WorkflowTransition.Trigger), nameof(WorkflowTransition.ToKey),
            nameof(WorkflowTransition.Criteria), nameof(WorkflowTransition.RequiredRole), nameof(WorkflowTransition.Order));

        var def = new WorkflowDef((string)head[0]!, (int)head[1]!, (string)head[2]!,
            states.Select(s => new StateDef((string)s[1]!, (string)s[2]!, (StateKind)(int)s[3]!, DefinitionMapper.Opt((string?)s[4]), (int?)s[5],
                effects[(Guid)s[0]!].OrderBy(e => (int)e[1]!)
                    .Select(e => new EffectDef((EffectKind)(int)e[2]!, (string)e[3]!, (string)e[4]!)).ToList(),
                DefinitionMapper.Members((string?)s[6]))).ToList(),
            transitions.Select(t => new TransitionDef((string)t[0]!, (string)t[1]!, (string)t[2]!,
                DefinitionMapper.Opt((string?)t[3]), DefinitionMapper.Opt((string?)t[4]), (int)t[5]!)).ToList());
        return (def, (DefinitionState)(int)head[3]!);
    }
}
