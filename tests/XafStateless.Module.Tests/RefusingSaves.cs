using DevExpress.ExpressApp;
using DevExpress.Persistent.Validation;

namespace XafStateless.Module.Tests;

/// <summary>WF-010: a host that validates every commit, as a generated host's save validation does: an
/// <see cref="IObjectSpace.Committing"/> handler that throws <see cref="ValidationException"/> (an IUserFriendlyException,
/// not a UserFriendlyException) when an object about to be saved breaks a rule.</summary>
static class RefusingSaves
{
    public const string Message = "Save refused: a rule is broken";

    /// <param name="error">What the handler throws instead (a host failure that is not a refusal); default: the ValidationException.</param>
    public static IObjectSpace Refuse(this IObjectSpace os, Func<object, bool> breaksRule, Func<Exception>? error = null)
    {
        os.Committing += (_, _) =>
        {
            if (os.ModifiedObjects.Cast<object>().Any(breaksRule))
                throw error?.Invoke() ?? new ValidationException(Message, new RuleSetValidationResult());
        };
        return os;
    }
}
