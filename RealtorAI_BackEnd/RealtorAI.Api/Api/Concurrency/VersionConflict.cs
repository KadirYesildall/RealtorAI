using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Api.Concurrency;

/// <summary>
/// T040 — the HTTP shape of a concurrency rejection (FR-066).
///
/// <para>
/// Problem types are absolute URIs under a project-owned namespace so clients can branch on a
/// stable identifier rather than on <c>title</c>, which is human-facing and may be reworded or
/// localised. The URIs are identifiers, not addresses that must resolve.
/// </para>
/// </summary>
public static class VersionConflict
{
    private const string TypeBase = "https://realtorai.app/problems/";

    /// <summary>A write was attempted against a row version that is no longer current.</summary>
    public const string VersionConflictType = TypeBase + "version-conflict";

    /// <summary>The <c>If-Match</c> header was absent or malformed.</summary>
    public const string PreconditionRequiredType = TypeBase + "precondition-required";

    /// <summary>
    /// The content hash supplied with an approval does not match what the server now holds
    /// (FR-066). Separate from a version conflict because the consequence is different: the
    /// agent must re-read the content before approving, not merely retry.
    /// </summary>
    public const string ContentChangedType = TypeBase + "approval-content-changed";

    /// <summary>
    /// 409 with the version the server actually holds, so the client can refetch and show the
    /// user what moved rather than silently retrying — a blind retry would overwrite an edit
    /// nobody saw, which is the failure FR-066 exists to prevent.
    /// </summary>
    public static ProblemHttpResult Conflict(int? currentRowVersion) =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = VersionConflictType,
            Title = "The record changed since you loaded it",
            Status = StatusCodes.Status409Conflict,
            Detail = currentRowVersion is { } version
                ? $"Your change was based on an older version of this record. The current "
                    + $"version is {version}. Reload it, review what changed, and apply your "
                    + "edit again."
                : "Your change was based on an older version of this record. Reload it and "
                    + "apply your edit again.",
            Extensions = { ["currentRowVersion"] = currentRowVersion },
        });

    /// <summary>
    /// 428, not 400. The request is well-formed; what is missing is the precondition that makes
    /// it safe to apply, and 428 tells the client specifically to retry with one.
    /// </summary>
    public static ProblemHttpResult PreconditionRequired(string error) =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = PreconditionRequiredType,
            Title = "A version precondition is required",
            Status = StatusCodes.Status428PreconditionRequired,
            Detail = error,
        });

    /// <summary>
    /// 409 for an approval whose reviewed content no longer matches (FR-066). The audit trail
    /// records the attempt as <c>approval_rejected_hash_mismatch</c>, so a near-miss is
    /// evidence rather than a silent retry.
    /// </summary>
    public static ProblemHttpResult ContentChanged() =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = ContentChangedType,
            Title = "The content changed before you approved it",
            Status = StatusCodes.Status409Conflict,
            Detail = "What you reviewed is not what the listing now contains, so the approval "
                + "was not recorded. Review the current content and approve again — an "
                + "approval has to apply to content you actually saw.",
        });
}
