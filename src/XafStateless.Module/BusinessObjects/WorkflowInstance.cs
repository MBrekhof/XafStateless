using System.ComponentModel;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafStateless.Module.BusinessObjects;

[DefaultProperty(nameof(StateCaption))]   // a subject's Workflow link shows its current state
public class WorkflowInstance : BaseObject
{
    public virtual string SubjectType { get; set; } = "";
    public virtual Guid SubjectId { get; set; }
    public virtual WorkflowDefinition? Definition { get; set; }      // pinned version
    public virtual string StateKey { get; set; } = "";
    public virtual string StateCaption { get; set; } = "";
    public virtual DateTime EnteredUtc { get; set; }
    public virtual DateTime? DueUtc { get; set; }
}
