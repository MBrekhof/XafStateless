using System.Collections.ObjectModel;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ConditionalAppearance;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Editors;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;
using XafStateless.Engine;
using XafStateless.Module.Services;

namespace XafStateless.Module.BusinessObjects;

[Appearance("WorkflowState.TaskRole", AppearanceItemType = "ViewItem", TargetItems = nameof(TaskRole), Context = "DetailView",
    Criteria = "Kind != ##Enum#XafStateless.Engine.StateKind,Task#", Visibility = ViewItemVisibility.Hide)]
[Appearance("WorkflowState.WaitMinutes", AppearanceItemType = "ViewItem", TargetItems = nameof(WaitMinutes), Context = "DetailView",
    Criteria = "Kind != ##Enum#XafStateless.Engine.StateKind,Wait#", Visibility = ViewItemVisibility.Hide)]
[ImageName("BO_State")]
public class WorkflowState : BaseObject, IStringLookupSource
{
    public virtual WorkflowDefinition? Definition { get; set; }
    [RuleRequiredField] public virtual string Key { get; set; } = "";
    [RuleRequiredField] public virtual string Caption { get; set; } = "";
    [ImmediatePostData] public virtual StateKind Kind { get; set; }
    [EditorAlias(StringLookup.Single)] public virtual string? TaskRole { get; set; }
    public virtual int? WaitMinutes { get; set; }
    /// <summary>Subject members locked in this state, joined with <see cref="StringLookup.Separator"/> (member names cannot contain it).</summary>
    [EditorAlias(StringLookup.Multi)] public virtual string? LockedMembers { get; set; }

    [Aggregated] public virtual IList<WorkflowEffect> Effects { get; set; } = new ObservableCollection<WorkflowEffect>();

    /// <summary>TaskRole: role names. LockedMembers: the subject's members that effects and locks may use.</summary>
    public IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames) => memberName switch
    {
        nameof(TaskRole) => roleNames(),
        nameof(LockedMembers) => XafTypesInfo.Instance.FindTypeInfo(Definition?.TargetTypeName ?? "") is { } target
            ? WorkflowMembers.Supported(target).Select(m => m.Name) : [],
        _ => [],
    };
}
