using DevExpress.ExpressApp;
using XafStateless.Module;

namespace XafStateless.Blazor;

/// <summary>The Blazor half of XafStateless: the string lookup property editors (found by their [PropertyEditor]
/// attributes) and the Diagram view item (found by its [ViewItem] attribute; placed on WorkflowDefinition_DetailView
/// by this project's embedded Model.DesignedDiffs.xafml).</summary>
public sealed class XafStatelessBlazorModule : ModuleBase
{
    public XafStatelessBlazorModule() => RequiredModuleTypes.Add(typeof(XafStatelessModule));
}
