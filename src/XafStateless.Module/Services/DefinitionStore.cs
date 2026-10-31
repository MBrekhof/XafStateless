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

    /// <summary>The Published definitions for <paramref name="subject"/>'s entity type (never a proxy type): what a subject without
    /// a workflow is locked by (CARD-2713). Read from the database, so it needs no Read on definitions.</summary>
    public IReadOnlyList<WorkflowDef> PublishedFor(IObjectSpace os, object subject)
    {
        var ctx = (os as EFCoreObjectSpace)?.DbContext ?? throw new NotSupportedException("DefinitionStore needs an EF Core object space.");
        var type = ctx.Model.FindRuntimeEntityType(subject.GetType())?.ClrType ?? subject.GetType();
        return PublishedFor(ctx, [type.FullName!])[type.FullName!].Select(d => Get(os, d.Id)).ToList();
    }

    /// <summary>Per subject type name (a full CLR name): its Published definitions' ids and initial states, from the database.</summary>
    internal static ILookup<string, (Guid Id, string InitialState)> PublishedFor(DbContext ctx, IEnumerable<string> typeNames) =>
        RawSql.Rows<WorkflowDefinition>(ctx, nameof(WorkflowDefinition.TargetTypeName), typeNames,
                nameof(WorkflowDefinition.ID), nameof(WorkflowDefinition.TargetTypeName), nameof(WorkflowDefinition.InitialStateKey), nameof(WorkflowDefinition.State))
            .Where(r => (DefinitionState)(int)r[3]! == DefinitionState.Published)
            .ToLookup(r => (string)r[1]!, r => ((Guid)r[0]!, (string)r[2]!));

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
        var ruleSets = RawSql.Rows<WorkflowRuleSet>(ctx, RawSql.ForeignKey<WorkflowRuleSet>(ctx, nameof(WorkflowRuleSet.Definition)), [id],
            Id, nameof(WorkflowRuleSet.Key));
        var rsIds = ruleSets.Select(r => (Guid)r[0]!).ToList();
        var paramFk = RawSql.ForeignKey<WorkflowRuleParam>(ctx, nameof(WorkflowRuleParam.RuleSet));
        var ruleParams = RawSql.Rows<WorkflowRuleParam>(ctx, paramFk, rsIds,
                paramFk, nameof(WorkflowRuleParam.Order), nameof(WorkflowRuleParam.Name), nameof(WorkflowRuleParam.Expression))
            .ToLookup(p => (Guid)p[0]!);
        var ruleFk = RawSql.ForeignKey<WorkflowRule>(ctx, nameof(WorkflowRule.RuleSet));
        var rules = RawSql.Rows<WorkflowRule>(ctx, ruleFk, rsIds,
                ruleFk, nameof(WorkflowRule.Order), nameof(WorkflowRule.Name), nameof(WorkflowRule.Expression), nameof(WorkflowRule.TargetMember), nameof(WorkflowRule.OutputExpression))
            .ToLookup(r => (Guid)r[0]!);

        var def = new WorkflowDef((string)head[0]!, (int)head[1]!, (string)head[2]!,
            states.Select(s => new StateDef((string)s[1]!, (string)s[2]!, (StateKind)(int)s[3]!, DefinitionMapper.Opt((string?)s[4]), (int?)s[5],
                effects[(Guid)s[0]!].OrderBy(e => (int)e[1]!)
                    .Select(e => new EffectDef((EffectKind)(int)e[2]!, (string)e[3]!, (string)e[4]!)).ToList(),
                DefinitionMapper.Members((string?)s[6]))).ToList(),
            transitions.Select(t => new TransitionDef((string)t[0]!, (string)t[1]!, (string)t[2]!,
                DefinitionMapper.Opt((string?)t[3]), DefinitionMapper.Opt((string?)t[4]), (int)t[5]!)).ToList(),
            ruleSets.Select(rs => new RuleSetDef((string)rs[1]!,
                ruleParams[(Guid)rs[0]!].OrderBy(p => (int)p[1]!)
                    .Select(p => new RuleParamDef((string)p[2]!, (string)p[3]!)).ToList(),
                rules[(Guid)rs[0]!].OrderBy(r => (int)r[1]!)
                    .Select(r => new RuleItemDef((string)r[2]!, (string)r[3]!, (string)r[4]!, (string)r[5]!, (int)r[1]!)).ToList())).ToList());
        return (def, (DefinitionState)(int)head[3]!);
    }
}
