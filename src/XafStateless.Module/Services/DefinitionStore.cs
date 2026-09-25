using System.Collections.Concurrent;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using Microsoft.EntityFrameworkCore;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>Singleton. Published and Retired definitions never change (DefinitionGuard), so their snapshots are cached
/// by id with no invalidation; drafts are mapped fresh on every call.</summary>
public sealed class DefinitionStore
{
    readonly ConcurrentDictionary<Guid, WorkflowDef> cache = new();

    public WorkflowDef Get(IObjectSpace os, Guid definitionId)
    {
        if (cache.TryGetValue(definitionId, out var cached)) return cached;
        var d = os.GetObjectByKey<WorkflowDefinition>(definitionId) ?? throw new InvalidOperationException($"Workflow definition {definitionId} does not exist.");
        var def = DefinitionMapper.ToDef(d);
        // Never cache what might not be the committed truth: a draft, unsaved edits in this object space, or a read inside an
        // open transaction (which may yet roll back, e.g. a publish that fails later).
        return d.State == DefinitionState.Draft || os.IsModified || InTransaction(os) ? def : cache.GetOrAdd(definitionId, def);
    }

    static bool InTransaction(IObjectSpace os) =>
        System.Transactions.Transaction.Current != null || (os is EFCoreObjectSpace ef && ef.DbContext.Database.CurrentTransaction != null);

    public WorkflowDefinition? Published(IObjectSpace os, string name) =>
        os.FirstOrDefault<WorkflowDefinition>(d => d.Name == name && d.State == DefinitionState.Published);
}
