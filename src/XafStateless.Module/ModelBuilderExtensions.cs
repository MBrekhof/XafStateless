using Microsoft.EntityFrameworkCore;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module;

public static class ModelBuilderExtensions
{
    // Every unique index excludes soft-deleted rows (BaseObject.GCRecord, UseDeferredDeletion),
    // so a deleted definition/instance does not block its replacement.
    const string Live = "[GCRecord] = 0";

    public static ModelBuilder ConfigureXafStateless(this ModelBuilder mb)
    {
        mb.Entity<WorkflowDefinition>(b =>
        {
            b.Property(d => d.Name).HasMaxLength(100);   // indexed: SQL Server cannot index nvarchar(max)
            b.HasIndex(d => new { d.Name, d.Version }).IsUnique().HasFilter(Live);
            // Two indexes on the same column need the named overload, or EF merges them into one.
            b.HasIndex(d => d.Name, "IX_WorkflowDefinition_Name_OneDraft").IsUnique()
                .HasFilter($"[State] = {(int)DefinitionState.Draft} AND {Live}");
            b.HasIndex(d => d.Name, "IX_WorkflowDefinition_Name_OnePublished").IsUnique()
                .HasFilter($"[State] = {(int)DefinitionState.Published} AND {Live}");
        });
        mb.Entity<WorkflowInstance>(b =>
        {
            b.Property(i => i.SubjectType).HasMaxLength(400);
            b.HasIndex(i => new { i.SubjectType, i.SubjectId }).IsUnique().HasFilter(Live);
            b.HasIndex(i => i.DueUtc);
        });
        mb.Entity<WorkflowTask>(b =>
        {
            b.Property(t => t.Role).HasMaxLength(100);
            b.HasIndex(t => new { t.Role, t.State });
            b.HasIndex(t => new { t.UserId, t.State });   // the task list's "assigned to me" (CARD-2615)
        });
        // A notification's values at notify time (CARD-2788): a shadow property, so it never becomes an XAF member.
        mb.Entity<WorkflowNotification>(b => b.Property<string?>(WorkflowNotifications.SnapshotColumn));
        return mb;
    }
}
