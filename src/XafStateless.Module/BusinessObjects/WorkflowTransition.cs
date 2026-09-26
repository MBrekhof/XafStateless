using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp.ConditionalAppearance;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Editors;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;
using XafStateless.Engine;
using XafStateless.Module.Services;

namespace XafStateless.Module.BusinessObjects;

// A role guards only a manual transition (Task/Wait/Decision states leave by task, timer or criteria); Order ranks a Decision's branches.
[Appearance("WorkflowTransition.RequiredRole", AppearanceItemType = "ViewItem", TargetItems = nameof(RequiredRole), Context = "DetailView",
    Criteria = "FromKind In (##Enum#XafStateless.Engine.StateKind,Task#, ##Enum#XafStateless.Engine.StateKind,Wait#, ##Enum#XafStateless.Engine.StateKind,Decision#)",
    Visibility = ViewItemVisibility.Hide)]
[Appearance("WorkflowTransition.Order", AppearanceItemType = "ViewItem", TargetItems = nameof(Order), Context = "DetailView",
    Criteria = "FromKind Is Null Or FromKind != ##Enum#XafStateless.Engine.StateKind,Decision#", Visibility = ViewItemVisibility.Hide)]
public class WorkflowTransition : BaseObject, IStringLookupSource
{
    public virtual WorkflowDefinition? Definition { get; set; }
    [RuleRequiredField, ImmediatePostData, EditorAlias(StringLookup.Single)] public virtual string FromKey { get; set; } = "";
    // XSM-022: the stored name is the trigger; on screen it reads as what users press.
    [RuleRequiredField, XafDisplayName("Action name"),
     ToolTip("What users press: the Transition button for a manual state, or a Decide outcome for a task. Decision and wait exits use it as the branch name.")]
    public virtual string Trigger { get; set; } = "";
    [RuleRequiredField, EditorAlias(StringLookup.Single)] public virtual string ToKey { get; set; } = "";
    [CriteriaOptions(nameof(TargetType))] public virtual string? Criteria { get; set; }
    [EditorAlias(StringLookup.Single)] public virtual string? RequiredRole { get; set; }
    public virtual int Order { get; set; }

    [NotMapped, Browsable(false)] public Type? TargetType => Definition?.TargetType;

    /// <summary>The kind of the from-state, for the appearance rules.</summary>
    [NotMapped, Browsable(false)] public StateKind? FromKind => Definition?.States.FirstOrDefault(s => s.Key == FromKey)?.Kind;

    public IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames) => memberName switch
    {
        nameof(FromKey) or nameof(ToKey) => Definition?.States.Select(s => s.Key) ?? [],
        nameof(RequiredRole) => roleNames(),
        _ => [],
    };
}
