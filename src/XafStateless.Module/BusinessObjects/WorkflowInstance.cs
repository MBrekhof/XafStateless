using DevExpress.Persistent.Base;
using System.ComponentModel;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafStateless.Module.BusinessObjects;

[ImageName("BO_StateMachine"), DefaultProperty(nameof(StateCaption))]   // a subject's Workflow link shows its current state
public class WorkflowInstance : BaseObject
{
    public virtual string SubjectType { get; set; } = "";
    public virtual Guid SubjectId { get; set; }
    public virtual WorkflowDefinition? Definition { get; set; }      // pinned version
    public virtual string StateKey { get; set; } = "";
    public virtual string StateCaption { get; set; } = "";
    public virtual DateTime EnteredUtc { get; set; }
    public virtual DateTime? DueUtc { get; set; }
    /// <summary>The requester: the user who started the workflow (null for a start without a user). Fixed once the instance
    /// exists; Notify effects addressed to <see cref="XafStateless.Engine.EffectDef.Requester"/> go to this user.</summary>
    [VisibleInListView(false), VisibleInDetailView(false), VisibleInLookupListView(false)]
    public virtual Guid? StartedById { get; set; }
    public virtual string? StartedBy { get; set; }
}
