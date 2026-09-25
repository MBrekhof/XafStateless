using DevExpress.Persistent.BaseImpl.EF;

namespace XafStateless.Module.BusinessObjects;

public enum NotificationState { Pending, Sent, Failed }

public class WorkflowNotification : BaseObject
{
    public virtual string Role { get; set; } = "";
    public virtual string Body { get; set; } = "";
    public virtual WorkflowInstance? Instance { get; set; }
    public virtual NotificationState State { get; set; }
    public virtual int Attempts { get; set; }
    public virtual string? LastError { get; set; }
    public virtual DateTime CreatedUtc { get; set; }
}
