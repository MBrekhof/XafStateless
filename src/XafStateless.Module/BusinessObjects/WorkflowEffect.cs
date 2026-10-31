using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;
using XafStateless.Engine;
using XafStateless.Module.Services;

namespace XafStateless.Module.BusinessObjects;

public class WorkflowEffect : BaseObject, IStringLookupSource
{
    public virtual WorkflowState? State { get; set; }
    public virtual int Order { get; set; }
    [ImmediatePostData] public virtual EffectKind Kind { get; set; }
    [RuleRequiredField, EditorAlias(StringLookup.Single)] public virtual string Target { get; set; } = "";
    public virtual string Value { get; set; } = "";

    public IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames) => LookupValues(memberName, roleNames, () => []);

    /// <summary>Target: the requester, the subject's user members and role names for Notify, the host's action names for RunAction, the subject's settable members for SetField,
    /// its counters for Increment.</summary>
    public IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames, Func<IReadOnlyList<string>> actionNames) =>
        memberName != nameof(Target) ? []
        : Kind switch
        {
            EffectKind.Notify => [EffectDef.Requester, .. WorkflowState.UserTargets(State?.Definition), .. roleNames()],
            EffectKind.RunAction => actionNames(),
            EffectKind.RunRules => State?.Definition?.RuleSets.Select(r => r.Key).Where(k => !string.IsNullOrWhiteSpace(k)) ?? [],
            _ => XafTypesInfo.Instance.FindTypeInfo(State?.Definition?.TargetTypeName ?? "") is { } target
                ? (Kind == EffectKind.Increment ? WorkflowMembers.Counters(target) : WorkflowMembers.Supported(target)).Select(m => m.Name)
                : [],
        };
}
