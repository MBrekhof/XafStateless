using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ConditionalAppearance;
using DevExpress.ExpressApp.Validation;

namespace XafStateless.Module;

public sealed class XafStatelessModule : ModuleBase
{
    public XafStatelessModule()
    {
        RequiredModuleTypes.Add(typeof(ConditionalAppearanceModule));
        RequiredModuleTypes.Add(typeof(ValidationModule));
    }
}
