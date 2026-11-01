using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using XafStateless.Module.Services;
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
    /// <summary>The Notify text with its {Member} placeholders (<see cref="IsTemplate"/>); a host renders it per recipient with
    /// <see cref="Services.WorkflowNotifications.Render"/>. Views show <see cref="Text"/>.</summary>
    [VisibleInListView(false), VisibleInDetailView(false), VisibleInLookupListView(false)]
    public virtual string Body { get; set; } = "";

    /// <summary>The text as the user whose space loaded this row may read it (CARD-2597): rendered in that space with its
    /// security strategy, so the Workflow Notification list shows each participant their own text. Read per row.</summary>
    // ponytail: one subject load per row shown; a participant's list is short. Batch by subject if lists grow.
    [NotMapped]
    public string Text => ObjectSpace is { IsDisposed: false } os
        ? WorkflowNotifications.Render(this, os, os.ServiceProvider?.GetService(typeof(ISecurityStrategyBase)) as IRequestSecurityStrategy)
        : Body;
    /// <summary>True on rows written since 0.4.0 (CARD-2597): <see cref="Body"/> is a template. Older rows hold filled text.</summary>
    [VisibleInListView(false), VisibleInDetailView(false), VisibleInLookupListView(false)]
    public virtual bool IsTemplate { get; set; }
    public virtual WorkflowInstance? Instance { get; set; }
    public virtual NotificationState State { get; set; }
    public virtual int Attempts { get; set; }
    public virtual string? LastError { get; set; }
    public virtual DateTime CreatedUtc { get; set; }
}
