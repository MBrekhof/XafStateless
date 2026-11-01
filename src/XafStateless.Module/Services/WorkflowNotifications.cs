using System.Globalization;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Security;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>A notification's text for one recipient (CARD-2597). The runtime stores the Notify template; the host's
/// dispatcher calls <see cref="Render"/> once per recipient, with a space signed in as that recipient.</summary>
public static class WorkflowNotifications
{
    /// <summary>What a placeholder the recipient may not read renders as.</summary>
    public const string Masked = "*******";

    /// <summary>
    /// The body of <paramref name="note"/> as <paramref name="security"/>'s user may read it: each <c>{Member}</c> or
    /// <c>{Reference.Member}</c> filled from the subject loaded by key in <paramref name="space"/>. A placeholder renders
    /// <see cref="Masked"/> when any hop, or a reference value's default member, may not be read, or when the subject is
    /// not found in <paramref name="space"/> (or its type is unknown there). Values are those at render time.
    /// <paramref name="note"/> may come from another (non-secured) space: only its subject's type name and key are read.
    /// <paramref name="security"/> null renders everything (for a log, never for a mail). A row written before 0.4.0
    /// holds text the runtime already filled: it is returned as stored.
    /// </summary>
    public static string Render(WorkflowNotification note, IObjectSpace space, IRequestSecurityStrategy? security)
    {
        if (!note.IsTemplate) return note.Body;
        var (typeName, key) = note.Instance is { } i ? (i.SubjectType, i.SubjectId) : ("", Guid.Empty);
        var type = space.TypesInfo.FindTypeInfo(typeName);
        var subject = type is { IsPersistent: true } && space.IsKnownType(type.Type) ? space.GetObjectByKey(type.Type, key) : null;
        return subject == null
            ? XafWorkflowEnvironment.Placeholder().Replace(note.Body, Masked)
            : Fill(type!, subject, note.Body, security == null ? null : (o, m) => security.CanRead(space, o, m));
    }

    /// <summary>
    /// <paramref name="template"/>'s placeholders read from <paramref name="subject"/>. <paramref name="mayRead"/> (owner,
    /// member name; null member = the object itself) is asked for each hop and for a reference value's default member;
    /// null asks nothing. A null hop renders "", an unknown member "" (validation refuses one).
    /// </summary>
    internal static string Fill(ITypeInfo type, object subject, string template, Func<object, string?, bool>? mayRead) =>
        XafWorkflowEnvironment.Placeholder().Replace(template, m =>
        {
            if (type.FindMember(m.Groups[1].Value) is not { } path) return "";
            object? value = subject;
            foreach (var hop in path.GetPath())
            {
                if (value == null) return "";
                if (mayRead?.Invoke(value, hop.Name) == false) return Masked;
                value = hop.GetValue(value);
            }
            // A reference shows as its default member (the record's name): reading it needs that member, or the object.
            // The declared type: the value may be an EF proxy.
            if (mayRead != null && value != null && path.MemberTypeInfo is { IsPersistent: true } target && !mayRead(value, target.DefaultMember?.Name))
                return Masked;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        });
}
