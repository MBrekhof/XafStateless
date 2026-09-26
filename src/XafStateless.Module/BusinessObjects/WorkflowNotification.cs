using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafStateless.Module.BusinessObjects;

public enum NotificationState { Pending, Sent, Failed }

[NavigationItem("Workflow"), ImageName("BO_Notifications")]
public class WorkflowNotification : BaseObject
{
    /// <summary>The recipient: a role (Role), or a single user (UserId, with UserName as a snapshot for display). Exactly one is set.</summary>
    public virtual string Role { get; set; } = "";
    [VisibleInListView(false), VisibleInDetailView(false), VisibleInLookupListView(false)]   // a key; UserName is what people read
    public virtual Guid? UserId { get; set; }
    public virtual string? UserName { get; set; }
    public virtual string Body { get; set; } = "";
    public virtual WorkflowInstance? Instance { get; set; }
    public virtual NotificationState State { get; set; }
    public virtual int Attempts { get; set; }
    public virtual string? LastError { get; set; }
    public virtual DateTime CreatedUtc { get; set; }
}
