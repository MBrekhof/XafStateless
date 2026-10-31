using System.ComponentModel;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafStateless.Module.BusinessObjects;

public enum TaskState { Open, Decided, Superseded }

[NavigationItem("Workflow"), ImageName("BO_Task")]
[DefaultProperty(nameof(Subject))]   // declared: XAF's name heuristic would pick UserName (empty for a role's task)
public class WorkflowTask : BaseObject
{
    public virtual WorkflowInstance? Instance { get; set; }
    public virtual string StateKey { get; set; } = "";
    public virtual string Role { get; set; } = "";                  // runtime-owned copy of TaskRole, for the "my tasks" filter; users have read-only access; "" for a user's task
    /// <summary>The user a task addressed to a user member (<c>@Member</c>) is assigned to, fixed when the task opens (with
    /// <see cref="UserName"/> as a snapshot for display); null for a role's task.</summary>
    [VisibleInListView(false), VisibleInDetailView(false), VisibleInLookupListView(false)]   // a key; UserName is what people read
    public virtual Guid? UserId { get; set; }
    public virtual string? UserName { get; set; }
    // No Outcomes column: outcomes = def.From(StateKey) of the instance's pinned definition, so there is no delimiter and nothing user-editable.
    public virtual string Subject { get; set; } = "";               // display text
    public virtual TaskState State { get; set; }
    public virtual string? DecidedBy { get; set; }
    public virtual string? Outcome { get; set; }
    public virtual DateTime? DecidedUtc { get; set; }
}
