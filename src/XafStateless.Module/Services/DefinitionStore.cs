using System.Collections.Concurrent;
using DevExpress.ExpressApp;
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
        // An object space with pending changes may hold unsaved edits of this definition: map it, never cache it.
        return d.State == DefinitionState.Draft || os.IsModified ? def : cache.GetOrAdd(definitionId, def);
    }

    public WorkflowDefinition? Published(IObjectSpace os, string name) =>
        os.FirstOrDefault<WorkflowDefinition>(d => d.Name == name && d.State == DefinitionState.Published);
}
