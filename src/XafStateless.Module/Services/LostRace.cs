using DevExpress.ExpressApp;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace XafStateless.Module.Services;

/// <summary>Classifies a failed commit as a lost race (another writer got there first).</summary>
public static class LostRace
{
    /// <summary>The recorded shape (Task 4b): XAF's UserFriendlyException wrapping DbUpdateConcurrencyException;
    /// fallback: a DbUpdateConcurrencyException anywhere in the chain; or a unique-index violation.</summary>
    public static bool Is(Exception ex) =>
        ex is UserFriendlyException { InnerException: DbUpdateConcurrencyException }
        || Chain(ex).Any(e => e is DbUpdateConcurrencyException)
        || IsUniqueViolation(ex);

    /// <summary>SQL Server 2601 (duplicate key in unique index) or 2627 (unique constraint) anywhere in the inner-exception chain.</summary>
    public static bool IsUniqueViolation(Exception ex) =>
        Chain(ex).OfType<SqlException>().Any(e => e.Number is 2601 or 2627);

    static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException) yield return ex;
    }
}
