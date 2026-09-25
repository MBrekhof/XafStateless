using DevExpress.ExpressApp;
using XafStateless.Module;

namespace XafStateless.Blazor;

/// <summary>The Blazor half of XafStateless: the string lookup property editors (found by their [PropertyEditor] attributes).</summary>
public sealed class XafStatelessBlazorModule : ModuleBase
{
    public XafStatelessBlazorModule() => RequiredModuleTypes.Add(typeof(XafStatelessModule));
}
