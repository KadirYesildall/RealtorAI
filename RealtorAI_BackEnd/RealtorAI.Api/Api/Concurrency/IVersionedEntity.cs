namespace Api.Concurrency;

/// <summary>
/// An entity that participates in optimistic concurrency (FR-066).
///
/// <para>
/// Implemented by <c>Listing</c>, <c>ListingField</c> and <c>ContentDraft</c> — the three types
/// FR-066 names, because they are the three a human edits while a background job may also be
/// writing them. Photos are not versioned: uploads are append-only and reordering is a single
/// atomic rewrite of the whole order, so there is no partial-overwrite hazard to guard.
/// </para>
/// <para>
/// <c>RowVersion</c> is an ordinary integer the application increments, not a
/// database-generated token, and the EF model deliberately does not configure it with
/// <c>IsRowVersion()</c>. EF's own concurrency handling throws
/// <c>DbUpdateConcurrencyException</c> from deep inside <c>SaveChanges</c>, which gives the
/// HTTP layer no clean way to answer 409 with the current server state — and returning that
/// state is half of what FR-066 asks for.
/// </para>
/// </summary>
public interface IVersionedEntity
{
    Guid Id { get; }

    int RowVersion { get; set; }
}
