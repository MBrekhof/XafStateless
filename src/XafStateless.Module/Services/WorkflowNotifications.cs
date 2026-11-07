using System.Globalization;
using System.Text.Json;
using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.EFCore;
using DevExpress.ExpressApp.Security;
using Microsoft.EntityFrameworkCore;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>A notification's text for one recipient (CARD-2597). The runtime stores the Notify template and a snapshot of
/// its values (CARD-2788); the host's dispatcher calls <see cref="Render"/> once per recipient, with a space signed in as
/// that recipient.</summary>
public static class WorkflowNotifications
{
    /// <summary>What a placeholder the recipient may not read renders as.</summary>
    public const string Masked = "*******";

    /// <summary>The notification's shadow column (an EF property without a CLR member, declared by <c>ConfigureXafStateless</c>)
    /// holding its placeholders' texts at notify time and the records they read, unmasked. Being no CLR member it is no XAF
    /// member: no view, criteria, permission or Web API reaches it. The runtime writes it, <see cref="Render"/> reads it with
    /// plain SQL. Not for Middle Tier hosts: XAF's Middle Tier copies shadow properties into the client payload.</summary>
    public const string SnapshotColumn = "Snapshot";

    /// <summary>One placeholder at notify time. <see cref="Live"/>: a value the security system cannot judge from its reads
    /// (a calculated member, a reference without a stored default member); it is filled at render time instead.</summary>
    internal sealed record Entry(string Text, List<Read> Reads, bool Live = false);
    internal sealed record Read(string Type, string Key, string? Member);

    /// <summary>
    /// The body of <paramref name="note"/> as <paramref name="security"/>'s user may read it. Each <c>{Member}</c> or
    /// <c>{Reference.Member}</c> shows its value at notify time; it renders <see cref="Masked"/> when the user may not read
    /// the subject (loaded by key in <paramref name="space"/>), a record the placeholder read (loaded again by key: one
    /// deleted or hidden by their criteria masks), a member on the way, or a reference's default member. A calculated
    /// member is filled from the subject as it is now, as the user's space shows it. <paramref name="space"/> must be an EF
    /// Core object space (anything else masks every placeholder). <paramref name="note"/> may come from another
    /// (non-secured) space. <paramref name="security"/> null renders everything in a non-secured space (for a log, never
    /// for a mail) and masks everything in a secured one. A row written before 0.4.0 holds filled text and is returned as
    /// stored; a 0.4.x row (no snapshot) is filled from the subject as it is now.
    /// </summary>
    public static string Render(WorkflowNotification note, IObjectSpace space, IRequestSecurityStrategy? security)
    {
        if (!note.IsTemplate) return note.Body;
        var placeholders = XafWorkflowEnvironment.Placeholder();
        string AllMasked() => placeholders.Replace(note.Body, Masked);
        // A secured space comes with its strategy, a strategy with its secured space: one without the other judges nothing
        // (the snapshot is unmasked; a non-secured space shows what the strategy denies). A composite, non-persistent or
        // disposed space: not one this can read the snapshot or judge records in.
        if (space is not EFCoreObjectSpace { IsDisposed: false, DbContext: { } ctx } || (security != null) != (space is ISecuredObjectSpace))
            return AllMasked();
        if (!TryReadSnapshot(ctx, note, out var snapshot)) return AllMasked();

        var loaded = new Dictionary<(Type, object), object?>();
        var (typeName, key) = note.Instance is { } i ? (i.SubjectType, i.SubjectId) : ("", Guid.Empty);
        var type = space.TypesInfo.FindTypeInfo(typeName);
        var subject = type is { IsPersistent: true, KeyMembers: [{ MemberType: var kt }] } && kt == typeof(Guid) && space.IsKnownType(type.Type)
            ? Load(space, type, key, loaded) : null;
        if (subject == null) return AllMasked();

        return placeholders.Replace(note.Body, m =>
        {
            var name = m.Groups[1].Value;
            // A path a workflow may not read (CARD-2909): empty, also for a row snapshotted before the rule.
            if (type!.FindMember(name) is { } path && WorkflowMembers.FirstUnreadable(type, path) != null) return "";
            if (snapshot != null)
            {
                if (!snapshot.TryGetValue(name, out var e)) return Masked;
                if (!e.Live) return security == null || e.Reads.All(r => MayRead(space, security, r, loaded)) ? e.Text : Masked;
            }
            var (text, reads, _) = Walk(space.TypesInfo, type!, subject, name);   // a 0.4.x row, or a Live placeholder: now
            return security == null || reads.All(r => security.CanRead(space, r.Owner, r.Member)) ? text : Masked;
        });
    }

    /// <summary><paramref name="template"/> filled from <paramref name="subject"/>, unsecured (a Run action's argument).</summary>
    internal static string Fill(IObjectSpace os, ITypeInfo type, object subject, string template) =>
        XafWorkflowEnvironment.Placeholder().Replace(template, m => Walk(os.TypesInfo, type, subject, m.Groups[1].Value).Text);

    /// <summary>The snapshot of <paramref name="template"/>'s placeholders on <paramref name="subject"/>, read in the command's
    /// (non-secured) space <paramref name="os"/>: each text and the records it read, by runtime type and invariant key.</summary>
    internal static string Snapshot(IObjectSpace os, ITypeInfo type, object subject, string template)
    {
        var entries = new Dictionary<string, Entry>();
        foreach (var name in XafWorkflowEnvironment.Placeholder().Matches(template).Select(m => m.Groups[1].Value).Distinct())
        {
            var (text, reads, judged) = Walk(os.TypesInfo, type, subject, name);
            // A record with a composite key cannot be found again by one key, nor one whose key the store has yet to generate
            // (created in this command, IDENTITY: CARD-2809): judged at render time instead.
            judged = judged && reads.All(r => os.TypesInfo.FindTypeInfo(r.Owner.GetType()).KeyMembers.Count == 1 && !Temporary(os, r.Owner, r.Member));
            entries[name] = judged
                ? new(text, reads.Select(r => new Read(os.TypesInfo.FindTypeInfo(r.Owner.GetType()).FullName,
                    Convert.ToString(os.GetKeyValue(r.Owner), CultureInfo.InvariantCulture) ?? "", r.Member)).ToList())
                : new("", [], Live: true);
        }
        return JsonSerializer.Serialize(entries);
    }

    /// <summary>True when <paramref name="record"/> is new and its key is still EF's temporary value (the store generates it
    /// on save: the key written now would not find it afterwards), or when the member read holds a temporary value (a foreign
    /// key to such a record: the text would be wrong). False outside an EF Core space or for a type its model does not map.</summary>
    static bool Temporary(IObjectSpace os, object record, string? member) =>
        os is EFCoreObjectSpace { DbContext: { } ctx } && ctx.Model.FindRuntimeEntityType(record.GetType()) != null   // proxies too
        && ctx.Entry(record) is var e
        && ((e.State == EntityState.Added && e.Metadata.FindPrimaryKey()?.Properties.Any(p => e.Property(p).IsTemporary) == true)
            || (member != null && e.Metadata.FindProperty(member) is { } m && e.Property(m).IsTemporary));

    /// <summary>True when the host's model has the snapshot column (it called <c>ConfigureXafStateless</c>).</summary>
    internal static bool HasSnapshot(DbContext ctx) =>
        ctx.Model.FindEntityType(typeof(WorkflowNotification))?.FindProperty(SnapshotColumn) != null;

    /// <summary>
    /// One placeholder read from <paramref name="subject"/>: its text, every (record, member) it read (a null member is the
    /// record itself), and whether those reads judge the text. A null hop, or a hop declared on a subtype the value is not,
    /// renders "" (an unknown member too: validation refuses one). A value of a non-persistent type (<c>{Title.Length}</c>)
    /// is read without a check: its owner's member was. A record shows as its runtime type's default member (FindTypeInfo
    /// unwraps EF proxies), followed while that is a reference. Not judged: a calculated member or a collection on a record
    /// (a getter reads members no check names; a count includes rows the user's criteria hide), or a record without a
    /// default member (it shows as its ToString).
    /// </summary>
    static (string Text, List<(object Owner, string? Member)> Reads, bool Judged) Walk(ITypesInfo types, ITypeInfo type, object subject, string path)
    {
        var (reads, judged) = (new List<(object, string?)>(), true);
        if (type.FindMember(path) is not { } member) return ("", reads, true);
        // A hidden member or a user's secret (CARD-2909): refused at publish; a template published before shows nothing and
        // stores nothing. Judged on the declared path first (nothing read yet), then on each record's own type.
        if (WorkflowMembers.FirstUnreadable(type, member) != null) return ("", [], false);
        object? value = subject;
        foreach (var hop in member.GetPath())
        {
            if (value == null || !hop.Owner.Type.IsInstanceOfType(value)) return ("", reads, judged);
            if (WorkflowMembers.IsSecret(value.GetType(), hop.Name)) return ("", [], false);
            if (types.FindTypeInfo(value.GetType()) is { IsPersistent: true })
            {
                reads.Add((value, hop.Name));
                judged &= hop.IsPersistent && !hop.IsList;
            }
            value = hop.GetValue(value);
        }
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (value != null && types.FindTypeInfo(value.GetType()) is { IsPersistent: true } target)
        {
            if (!seen.Add(value) || target.DefaultMember is not { } shown)
            {
                reads.Add((value, null));
                judged = false;
                if (target.DefaultMember != null) value = null;   // a loop: BaseObject.ToString would follow it into a stack overflow
                break;
            }
            if ((!shown.IsVisible && !shown.IsKey) || WorkflowMembers.IsSecret(target.Type, shown.Name) || WorkflowMembers.IsSecret(shown.Owner.Type, shown.Name))
                return ("", [], false);   // shown as a hidden member or a user's secret (XAF's key fallback stays allowed)
            reads.Add((value, shown.Name));
            judged &= shown.IsPersistent && !shown.IsList;
            value = shown.GetValue(value);
        }
        return (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", reads, judged);
    }

    /// <summary>The note's snapshot, read with plain SQL: null when it has none (a 0.4.x row, a host without the column).
    /// False when there is no such row (deleted, unsaved) or the snapshot cannot be read as one (damaged JSON, a missing text
    /// or key): every placeholder masks.</summary>
    static bool TryReadSnapshot(DbContext ctx, WorkflowNotification note, out Dictionary<string, Entry>? snapshot)
    {
        snapshot = null;
        if (!HasSnapshot(ctx)) return true;
        var rows = RawSql.Rows<WorkflowNotification>(ctx, nameof(WorkflowNotification.ID), [note.ID], SnapshotColumn);
        if (rows.Count == 0) return false;
        if (rows[0][0] is not string json) return true;
        try { snapshot = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json); }
        catch (JsonException) { return false; }
        // System.Text.Json does not enforce the records' non-null parameters.
        return snapshot != null && snapshot.Values.All(e => e is { Text: not null, Reads: not null } && e.Reads.All(r => r is { Type: not null, Key: not null }));
    }

    static bool MayRead(IObjectSpace space, IRequestSecurityStrategy security, Read read, Dictionary<(Type, object), object?> loaded) =>
        space.TypesInfo.FindTypeInfo(read.Type) is { IsPersistent: true, KeyMembers: [var keyMember] } type
        && space.IsKnownType(type.Type)
        && ParseKey(keyMember.MemberType, read.Key) is { } key
        && Load(space, type, key, loaded) is { } record
        && security.CanRead(space, record, read.Member);

    /// <summary>The record by key through a query, never GetObjectByKey: that returns an entity the space already tracks (one
    /// prefetched through another row) without asking the database, so a record the user's criteria hide would pass.</summary>
    static object? Load(IObjectSpace space, ITypeInfo type, object key, Dictionary<(Type, object), object?> loaded)
    {
        if (!loaded.TryGetValue((type.Type, key), out var record))
        {
            var list = space.GetObjects(type.Type, new BinaryOperator(type.KeyMember.Name, key));
            using (list as IDisposable)   // an EFCoreCollection subscribes to the space's ObjectChanged until disposed
                loaded[(type.Type, key)] = record = list.Cast<object>().FirstOrDefault();
        }
        return record;
    }

    /// <summary>A key written invariantly, read back by the key member's type; null (masks) for anything else.</summary>
    static object? ParseKey(Type type, string text)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        try
        {
            if (type == typeof(Guid)) return Guid.Parse(text);
            if (type == typeof(string)) return text;
            return type.IsPrimitive || type == typeof(decimal) ? Convert.ChangeType(text, type, CultureInfo.InvariantCulture) : null;
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException) { return null; }
    }
}
