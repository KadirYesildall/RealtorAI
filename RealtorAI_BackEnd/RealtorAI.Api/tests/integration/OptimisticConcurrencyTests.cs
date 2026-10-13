using Api.Concurrency;
using Api.Data;
using Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T040/T041 — optimistic concurrency against the real database (FR-066).
///
/// <para>
/// The lost-update behaviour these tests pin down cannot be verified in memory. Whether two
/// concurrent writers can both commit is a property of PostgreSQL's isolation level, not of the
/// C# around it, so the interesting cases here run two real transactions against the real
/// server.
/// </para>
/// </summary>
public sealed class OptimisticConcurrencyTests : IAsyncLifetime
{
    private Guid listingId;


    /// <summary>
    /// Creates a listing that survives the test, because these tests commit on purpose — a
    /// rolled-back transaction cannot demonstrate a conflict between two committed writers.
    /// Cleanup is a soft delete, since FR-065 leaves no hard-delete path.
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var context = SupabaseDatabase.CreateContext();

        listingId = Guid.CreateVersion7();

        context.Listings.Add(new Listing
        {
            Id = listingId,
            AgentId = SupabaseDatabase.AgentA,
            BrokerageId = SupabaseDatabase.BrokeragePacificCrest,
            AddressLine1 = "1 Concurrency Way",
            City = "Testville",
            State = "CA",
            PostalCode = "90001",
            PropertyType = "Residential",
        });

        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var context = SupabaseDatabase.CreateContext();

        // Archive rather than delete: listings has no DELETE privilege for client roles and no
        // hard-delete path at all (0012, FR-065). The archive constraint requires the status
        // and the timestamp to move together.
        await context.Database.ExecuteSqlAsync(
            $"""
            update public.listings
            set status = 'archived', deleted_at = timezone('utc', now())
            where id = {listingId}
            """);
    }

    [Fact]
    public async Task MutateAsync_AppliesWhenTheExpectedVersionMatches()
    {
        await using var context = SupabaseDatabase.CreateContext();
        var guard = new ConcurrencyGuard(context);

        var result = await guard.MutateAsync<Listing>(
            listingId,
            expectedRowVersion: 1,
            listing => listing.City = "Updated Once",
            default);

        Assert.Equal(ConcurrencyOutcome.Applied, result.Outcome);
        Assert.Equal(2, result.CurrentRowVersion);
        Assert.Equal("Updated Once", result.Entity!.City);
    }

    /// <summary>
    /// The version must advance on every write, or a second stale write would be accepted.
    /// </summary>
    [Fact]
    public async Task MutateAsync_IncrementsTheRowVersion()
    {
        for (var expected = 1; expected <= 3; expected++)
        {
            await using var context = SupabaseDatabase.CreateContext();
            var guard = new ConcurrencyGuard(context);

            var result = await guard.MutateAsync<Listing>(
                listingId,
                expected,
                listing => listing.City = $"Pass {expected}",
                default);

            Assert.Equal(ConcurrencyOutcome.Applied, result.Outcome);
            Assert.Equal(expected + 1, result.CurrentRowVersion);
        }
    }

    [Fact]
    public async Task MutateAsync_RejectsAStaleVersionAndReportsTheCurrentOne()
    {
        await using (var context = SupabaseDatabase.CreateContext())
        {
            var guard = new ConcurrencyGuard(context);

            await guard.MutateAsync<Listing>(
                listingId,
                1,
                listing => listing.City = "First Writer Wins",
                default);
        }

        await using var second = SupabaseDatabase.CreateContext();
        var staleGuard = new ConcurrencyGuard(second);

        var result = await staleGuard.MutateAsync<Listing>(
            listingId,
            expectedRowVersion: 1, // what the second caller read before the first write landed
            listing => listing.City = "Second Writer Should Fail",
            default);

        Assert.Equal(ConcurrencyOutcome.Conflict, result.Outcome);

        // FR-066 requires rejecting "with the current server state", so the caller can show the
        // user what changed instead of blindly retrying over an edit nobody saw.
        Assert.Equal(2, result.CurrentRowVersion);
        Assert.Null(result.Entity);
    }

    [Fact]
    public async Task MutateAsync_DoesNotApplyTheChangeOnConflict()
    {
        await using (var context = SupabaseDatabase.CreateContext())
        {
            var guard = new ConcurrencyGuard(context);
            await guard.MutateAsync<Listing>(listingId, 1, listing => listing.City = "Kept", default);
        }

        await using (var stale = SupabaseDatabase.CreateContext())
        {
            var guard = new ConcurrencyGuard(stale);
            await guard.MutateAsync<Listing>(listingId, 1, listing => listing.City = "Discarded", default);
        }

        await using var verify = SupabaseDatabase.CreateContext();

        var city = await verify.Listings
            .AsNoTracking()
            .Where(listing => listing.Id == listingId)
            .Select(listing => listing.City)
            .SingleAsync();

        Assert.Equal("Kept", city);
    }

    [Fact]
    public async Task MutateAsync_ReportsNotFoundForAnUnknownId()
    {
        await using var context = SupabaseDatabase.CreateContext();
        var guard = new ConcurrencyGuard(context);

        var result = await guard.MutateAsync<Listing>(
            Guid.CreateVersion7(),
            1,
            listing => listing.City = "Nowhere",
            default);

        Assert.Equal(ConcurrencyOutcome.NotFound, result.Outcome);
        Assert.Null(result.CurrentRowVersion);
    }

    /// <summary>
    /// A caller that writes <c>RowVersion</c> itself — by copying a whole request DTO onto the
    /// entity, say — must not be able to hold the version still or move it backwards, which
    /// would make every subsequent stale write look current.
    /// </summary>
    [Fact]
    public async Task MutateAsync_OverridesACallerThatTampersWithTheRowVersion()
    {
        await using var context = SupabaseDatabase.CreateContext();
        var guard = new ConcurrencyGuard(context);

        var result = await guard.MutateAsync<Listing>(
            listingId,
            1,
            listing =>
            {
                listing.City = "Tampered";
                listing.RowVersion = 1;
            },
            default);

        Assert.Equal(ConcurrencyOutcome.Applied, result.Outcome);
        Assert.Equal(2, result.CurrentRowVersion);
    }

    /// <summary>
    /// The real lost-update test. Two transactions both read version 1, both see it as current,
    /// and both try to write — the interleaving a version check alone cannot catch, because each
    /// one's check passes.
    ///
    /// <para>
    /// Exactly one must succeed. Under PostgreSQL <c>REPEATABLE READ</c> the loser fails with
    /// <c>40001</c>, which the guard translates into a conflict rather than letting it surface
    /// as a 500 — and translating it is what makes this a usable answer instead of an error.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoConcurrentWritersAtTheSameVersion_OnlyOneSucceeds()
    {
        await using var firstContext = SupabaseDatabase.CreateContext();
        await using var secondContext = SupabaseDatabase.CreateContext();

        var firstGuard = new ConcurrencyGuard(firstContext);
        var secondGuard = new ConcurrencyGuard(secondContext);

        // Started together so both read version 1 before either commits.
        var first = firstGuard.MutateAsync<Listing>(
            listingId,
            1,
            listing => listing.City = "Writer A",
            default);

        var second = secondGuard.MutateAsync<Listing>(
            listingId,
            1,
            listing => listing.City = "Writer B",
            default);

        var results = await Task.WhenAll(first, second);

        var applied = results.Count(result => result.Outcome == ConcurrencyOutcome.Applied);
        var conflicted = results.Count(result => result.Outcome == ConcurrencyOutcome.Conflict);

        Assert.Equal(1, applied);
        Assert.Equal(1, conflicted);

        // And the version advanced exactly once, so the losing write left no trace.
        await using var verify = SupabaseDatabase.CreateContext();

        var rowVersion = await verify.Listings
            .AsNoTracking()
            .Where(listing => listing.Id == listingId)
            .Select(listing => listing.RowVersion)
            .SingleAsync();

        Assert.Equal(2, rowVersion);
    }

    /// <summary>
    /// The guard is generic over versioned entities, so the three types FR-066 names are all
    /// covered. <c>content_drafts</c> is exercised here because it is the one a background
    /// regeneration writes while the agent is editing — the collision the requirement was
    /// written for.
    /// </summary>
    [Fact]
    public async Task MutateAsync_WorksForContentDrafts()
    {
        var draftId = Guid.CreateVersion7();

        await using (var setup = SupabaseDatabase.CreateContext())
        {
            setup.ContentDrafts.Add(new ContentDraft
            {
                Id = draftId,
                ListingId = listingId,
                Kind = ContentKind.PublicRemarks,
                Body = "Original body.",
            });

            await setup.SaveChangesAsync();
        }

        await using var context = SupabaseDatabase.CreateContext();
        var guard = new ConcurrencyGuard(context);

        var applied = await guard.MutateAsync<ContentDraft>(
            draftId,
            1,
            draft => draft.Body = "Edited body.",
            default);

        Assert.Equal(ConcurrencyOutcome.Applied, applied.Outcome);
        Assert.Equal(2, applied.CurrentRowVersion);

        var stale = await guard.MutateAsync<ContentDraft>(
            draftId,
            1,
            draft => draft.Body = "Stale edit.",
            default);

        Assert.Equal(ConcurrencyOutcome.Conflict, stale.Outcome);
    }
}
