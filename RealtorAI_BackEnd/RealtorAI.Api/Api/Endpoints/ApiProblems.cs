using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Api.Endpoints;

/// <summary>
/// The RFC 7807 responses endpoints share (Principle B).
///
/// <para>
/// <b>400 versus 422.</b> A body that cannot be parsed, or a parameter of the wrong type, is
/// rejected by model binding with 400 before a handler runs. Everything a handler rejects is a
/// well-formed request asking for something the rules do not allow, and that is 422 — with the
/// offending fields named, so the client can point at them rather than show a generic error.
/// </para>
/// <para>
/// Problem types are stable identifiers under the same namespace as
/// <see cref="Concurrency.VersionConflict"/>, so clients branch on <c>type</c>, never on the
/// human-facing <c>title</c>.
/// </para>
/// </summary>
internal static class ApiProblems
{
    private const string TypeBase = "https://realtorai.app/problems/";

    internal const string ValidationType = TypeBase + "validation";
    internal const string NotFoundType = TypeBase + "not-found";
    internal const string ConflictType = TypeBase + "conflict";
    internal const string ForbiddenType = TypeBase + "forbidden";

    /// <summary>422 with per-field messages.</summary>
    internal static ProblemHttpResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = ValidationType,
            Title = "The request could not be applied",
            Status = StatusCodes.Status422UnprocessableEntity,
            Detail = "One or more fields break a rule. See errors for which, and why.",
            Extensions = { ["errors"] = errors },
        });

    /// <summary>422 for a single field.</summary>
    internal static ProblemHttpResult Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    internal static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = NotFoundType,
            Title = "Not found",
            Status = StatusCodes.Status404NotFound,
            Detail = detail,
        });

    /// <summary>
    /// 409: the request is valid on its own but collides with the current state of something
    /// else, so resubmitting it unchanged will keep failing until that state changes.
    /// </summary>
    internal static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = ConflictType,
            Title = title,
            Status = StatusCodes.Status409Conflict,
            Detail = detail,
        });

    /// <summary>
    /// 403 from a resource-level decision made inside a handler, where the policy could not see
    /// the resource. Carries a reason the client may show; it never names what the caller would
    /// have seen.
    /// </summary>
    internal static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = ForbiddenType,
            Title = "Not permitted",
            Status = StatusCodes.Status403Forbidden,
            Detail = detail,
        });
}
