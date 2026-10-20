using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.Utils;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;
using XafStateless.Module.Services;

namespace XafStateless.Module.BusinessObjects;

public enum DefinitionState { Draft, Published, Retired }

[NavigationItem("Workflow"), ImageName("BO_WorkflowDefinition")]   // built-in DevExpress.Images names (XSM-020)
[DefaultProperty(nameof(Name))]
public class WorkflowDefinition : BaseObject, IStringLookupSource
{
    [RuleRequiredField] public virtual string Name { get; set; } = "";            // stable key
    // full CLR name of an IWorkflowSubject class, picked from the classes that implement it (XSM-022)
    [RuleRequiredField, ImmediatePostData, EditorAlias(StringLookup.Single)] public virtual string TargetTypeName { get; set; } = "";
    [ModelDefault("AllowEdit", "False")] public virtual int Version { get; set; }   // set by NewDraft, or by OnSaving for New
    // Lifecycle fields: only NewDraft and Publish set them (DefinitionGuard enforces it); read-only on screen.
    [ModelDefault("AllowEdit", "False")] public virtual DefinitionState State { get; set; }
    [EditorAlias(StringLookup.Single)] public virtual string InitialStateKey { get; set; } = "";
    [ModelDefault("AllowEdit", "False")] public virtual string? PublishedBy { get; set; }
    [ModelDefault("AllowEdit", "False")] public virtual DateTime? PublishedUtc { get; set; }

    [Aggregated] public virtual IList<WorkflowState> States { get; set; } = new ObservableCollection<WorkflowState>();
    [Aggregated] public virtual IList<WorkflowTransition> Transitions { get; set; } = new ObservableCollection<WorkflowTransition>();
    [Aggregated] public virtual IList<WorkflowRuleSet> RuleSets { get; set; } = new ObservableCollection<WorkflowRuleSet>();

    [NotMapped, Browsable(false)] public Type? TargetType => XafTypesInfo.Instance.FindTypeInfo(TargetTypeName)?.Type;

    bool versionAssigned;   // not mapped: a field, and only for this object's life

    /// <summary>A definition made with the New action never passes through NewDraft: number it here, max+1 per name. The
    /// number is recomputed on every save while the object is new, so a refused save followed by a rename is renumbered.
    /// Kept simple: two new drafts of one name in one commit both get max+1; the one-draft-per-name index refuses that anyway.</summary>
    public override void OnSaving()
    {
        base.OnSaving();
        if (ObjectSpace?.IsNewObject(this) == true && (Version == 0 || versionAssigned))
            (Version, versionAssigned) = (DefinitionService.NextVersion(ObjectSpace, Name), true);
    }

    public IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames) => memberName switch
    {
        nameof(InitialStateKey) => States.Select(s => s.Key),
        nameof(TargetTypeName) => SubjectTypeNames(),
        _ => [],
    };

    public string LookupText(string memberName, string value) => memberName == nameof(TargetTypeName) ? SubjectCaption(value) : value;

    /// <summary>The class caption of a subject type (XSM-025). When another subject type has the same caption, the
    /// caption plus namespace; when that still collides, the full name, so the list never shows two identical entries.
    /// A name that is not a current subject type shows as stored.</summary>
    public static string SubjectCaption(string fullName) => SubjectCaption(fullName, SubjectTypeNames().ToList(), Caption);

    internal static string SubjectCaption(string fullName, IReadOnlyList<string> subjects, Func<string, string> Caption)
    {
        if (!subjects.Contains(fullName)) return fullName;
        var others = subjects.Where(n => n != fullName).ToList();
        var caption = Caption(fullName);
        if (!others.Any(n => Caption(n) == caption)) return caption;
        string WithNamespace(string n) => $"{Caption(n)} ({n[..Math.Max(0, n.LastIndexOf('.'))]})";
        var qualified = WithNamespace(fullName);
        return others.Any(n => WithNamespace(n) == qualified) ? fullName : qualified;
    }

    /// <summary>The model's class caption; without an application model (tests) XAF returns the full name, so fall back to
    /// the same compound-name split XAF uses for default captions.</summary>
    static string Caption(string fullName)
    {
        var caption = CaptionHelper.GetClassCaption(fullName);
        return caption == fullName ? CaptionHelper.ConvertCompoundName(fullName[(fullName.LastIndexOf('.') + 1)..]) : caption;
    }

    /// <summary>Every persistent, non-abstract class implementing <see cref="IWorkflowSubject"/>: the interface is the marker.</summary>
    public static IEnumerable<string> SubjectTypeNames() => XafTypesInfo.Instance.PersistentTypes
        .Where(t => !t.IsAbstract && t.Type != null && typeof(IWorkflowSubject).IsAssignableFrom(t.Type))
        .Select(t => t.FullName).Order(StringComparer.Ordinal);
}
