using DevExpress.Persistent.BaseImpl.EF;

namespace XafStateless.Module.BusinessObjects;

public enum TaskState { Open, Decided, Superseded }

public class WorkflowTask : BaseObject
{
    public virtual WorkflowInstance? Instance { get; set; }
    public virtual string StateKey { get; set; } = "";
    public virtual string Role { get; set; } = "";                  // runtime-owned copy of TaskRole, for the "my tasks" filter; users have read-only access
    // No Outcomes column: outcomes = def.From(StateKey) of the instance's pinned definition, so there is no delimiter and nothing user-editable.
    public virtual string Subject { get; set; } = "";               // display text
    public virtual TaskState State { get; set; }
    public virtual string? DecidedBy { get; set; }
    public virtual string? Outcome { get; set; }
    public virtual DateTime? DecidedUtc { get; set; }
}
