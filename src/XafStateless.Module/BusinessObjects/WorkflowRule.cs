using System.ComponentModel;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;
using XafStateless.Module.Services;

namespace XafStateless.Module.BusinessObjects;

[DefaultProperty(nameof(Name))]
public class WorkflowRule : BaseObject, IStringLookupSource
{
    public virtual WorkflowRuleSet? RuleSet { get; set; }
    public virtual int Order { get; set; }
    [RuleRequiredField] public virtual string Name { get; set; } = "";
    [RuleRequiredField] public virtual string Expression { get; set; } = "";
    [RuleRequiredField, EditorAlias(StringLookup.Single)] public virtual string TargetMember { get; set; } = "";
    [RuleRequiredField] public virtual string OutputExpression { get; set; } = "";

    public IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames) =>
        memberName == nameof(TargetMember) && XafTypesInfo.Instance.FindTypeInfo(RuleSet?.Definition?.TargetTypeName ?? "") is { } target
            ? WorkflowMembers.Supported(target).Select(m => m.Name)
            : [];
}
