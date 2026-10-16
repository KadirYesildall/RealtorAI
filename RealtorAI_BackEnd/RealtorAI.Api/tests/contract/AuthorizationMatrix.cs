using Api.Authorization;
using Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Modules.Brokerage;
using Npgsql;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T039 — the authorization matrix for listing access (FR-050, FR-063).
///
/// <para>
/// Every caller relationship the requirements distinguish gets a row here: the owner, an
/// unrelated agent, a pending member of the same brokerage, an approved broker admin of the
/// same brokerage, an admin of a different brokerage, and a platform administrator. The cases
/// that matter most are the ones where a plausible implementation would say yes — an admin
/// reading a pending agent's listing, or a platform admin reading listing content — so those
/// are asserted explicitly rather than left to follow from the code.
/// </para>
///
/// <para>
/// <b>Isolation.</b> Each test runs inside a transaction that is rolled back, and the personas
/// it needs beyond the seed are created inside that transaction. The authorizer reads through
/// the same <see cref="AppDbContext"/>, so it sees those uncommitted rows while nothing
/// persists afterwards. The membership cache is fresh per test for the same reason: a cached
/// decision leaking between cases would make the suite order-dependent.
/// </para>
/// </summary>
public sealed class AuthorizationMatrix
{
    // Seed identities from supabase/seed.sql.
    private static readonly Guid AgentA = new("aaaaaaaa-0000-4000-8000-000000000001");  // approved broker_admin
    private static readonly Guid AgentB = new("bbbbbbbb-0000-4000-8000-000000000002");  // pending agent
    private static readonly Guid AgentC = new("cccccccc-0000-4000-8000-000000000003");  // approved agent, other brokerage
    private static readonly Guid PlatformAdmin = new("dddddddd-0000-4000-8000-000000000004");
    private static readonly Guid PacificCrest = new("11110000-0000-4000-8000-000000000001");
    private static readonly Guid HarborPoint = new("22220000-0000-4000-8000-000000000002");

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                Environment.GetEnvironmentVariable("REALTORAI_TEST_POSTGRES")
                ?? "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres;Maximum Pool Size=10",
                AppDbContext.ConfigureNpgsql)
            .Options;

        return new AppDbContext(options);
    }

    /// <summary>
    /// A fresh authorizer with its own cache, so no decision survives into the next test.
    /// </summary>
    private static ResourceAuthorizer CreateAuthorizer(AppDbContext context)
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        var resolver = new MembershipResolver(new EfMembershipStore(context), cache);

        return new ResourceAuthorizer(context, resolver);
    }

    /// <summary>
    /// Creates an agent: an <c>auth.users</c> row, which <c>profiles.id</c> references and
    /// whose insert trigger creates the matching profile.
    /// </summary>
    /// <remarks>
    /// The four token columns are written as empty strings rather than left null. They are
    /// nullable in the schema but GoTrue scans them into non-nullable Go strings, so a null
    /// breaks sign-in for that user with an opaque "Database error querying schema" — a trap
    /// documented in seed.sql and repeated here so a persona created by a test behaves like a
    /// real one.
    /// </remarks>
    private static async Task CreateAgentAsync(AppDbContext context, Guid agentId, string label)
    {
        await context.Database.ExecuteSqlAsync(
            $"""
            insert into auth.users
                (id, instance_id, aud, role, email, encrypted_password, email_confirmed_at,
                 created_at, updated_at, confirmation_token, recovery_token, email_change,
                 email_change_token_new)
            values ({agentId}, '00000000-0000-0000-0000-000000000000', 'authenticated',
                    'authenticated', {label + "@matrix.test"}, 'x', timezone('utc', now()),
                    timezone('utc', now()), timezone('utc', now()), '', '', '', '')
            """);

        // The profile row already exists: feature 001's `on_auth_user_created` trigger on
        // auth.users creates one for every new user. So this names it rather than inserting it
        // — an insert collides on profiles_pkey, which is how the trigger was rediscovered.
        await context.Database.ExecuteSqlAsync(
            $"update public.profiles set full_name = {label} where id = {agentId}");
    }

    private static async Task CreateMembershipAsync(
        AppDbContext context,
        Guid agentId,
        Guid brokerageId,
        MembershipStatus status,
        MembershipRole role)
    {
        await context.Database.ExecuteSqlAsync(
            $"""
            insert into public.brokerage_memberships (agent_id, brokerage_id, status, role)
            values ({agentId}, {brokerageId}, {status.ToString().ToLowerInvariant()}::membership_status,
                    {(role == MembershipRole.BrokerAdmin ? "broker_admin" : "agent")}::membership_role)
            """);
    }

    private static async Task<Guid> CreateListingAsync(
        AppDbContext context,
        Guid ownerAgentId,
        Guid? brokerageId)
    {
        var listingId = Guid.CreateVersion7();

        await context.Database.ExecuteSqlAsync(
            $"""
            insert into public.listings
                (id, agent_id, brokerage_id, address_line1, city, state, postal_code, property_type)
            values ({listingId}, {ownerAgentId}, {brokerageId}, '1 Matrix Way', 'Testville',
                    'CA', '90001', 'Residential')
            """);

        return listingId;
    }

    // ═════════════════════════════ Owner ═════════════════════════════

    [Fact]
    public async Task Owner_GetsReadAndWrite()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var listingId = await CreateListingAsync(context, AgentA, PacificCrest);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(AgentA, listingId, default);

        Assert.Equal(ListingAccess.ReadWrite, decision.Access);
        Assert.Equal(AuthorizationReason.Owner, decision.Reason);
        Assert.True(decision.CanRead);
        Assert.True(decision.CanWrite);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// A pending agent owns their own drafts outright. This is the permissive half of FR-061:
    /// private preparation work does not wait on a brokerage decision.
    /// </summary>
    [Fact]
    public async Task Owner_WithAPendingMembership_StillGetsReadAndWrite()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var listingId = await CreateListingAsync(context, AgentB, PacificCrest);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(AgentB, listingId, default);

        Assert.Equal(ListingAccess.ReadWrite, decision.Access);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// A soft-deleted listing stays readable so its audit trail remains queryable (FR-067),
    /// but is not writable.
    /// </summary>
    [Fact]
    public async Task Owner_OfAnArchivedListing_GetsReadOnly()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var listingId = await CreateListingAsync(context, AgentA, PacificCrest);

        await context.Database.ExecuteSqlAsync(
            $"""
            update public.listings
            set status = 'archived', deleted_at = timezone('utc', now())
            where id = {listingId}
            """);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(AgentA, listingId, default);

        Assert.Equal(ListingAccess.Read, decision.Access);
        Assert.Equal(AuthorizationReason.ListingArchived, decision.Reason);
        Assert.False(decision.CanWrite);

        await transaction.RollbackAsync();
    }

    // ═════════════════════════════ Unrelated callers ═════════════════════════════

    [Fact]
    public async Task UnrelatedAgentAtAnotherBrokerage_GetsNothing()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var listingId = await CreateListingAsync(context, AgentA, PacificCrest);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(AgentC, listingId, default);

        Assert.Equal(ListingAccess.None, decision.Access);
        Assert.False(decision.CanRead);

        // Reported as 404, not 403: a 403 on an id the caller has no relationship to would
        // confirm the id exists and turn the endpoint into an enumeration oracle.
        Assert.True(decision.ShouldReportAsNotFound);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// Same brokerage is not enough. An ordinary member — even an approved one — has no claim
    /// on a colleague's listing.
    /// </summary>
    [Fact]
    public async Task ApprovedOrdinaryMemberOfTheSameBrokerage_GetsNothing()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var colleague = Guid.CreateVersion7();
        await CreateAgentAsync(context, colleague, "approved-colleague");
        await CreateMembershipAsync(
            context, colleague, PacificCrest, MembershipStatus.Approved, MembershipRole.Agent);

        var listingId = await CreateListingAsync(context, AgentA, PacificCrest);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(colleague, listingId, default);

        Assert.Equal(ListingAccess.None, decision.Access);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task PendingMemberOfTheSameBrokerage_GetsNothing()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var listingId = await CreateListingAsync(context, AgentA, PacificCrest);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(AgentB, listingId, default);

        Assert.Equal(ListingAccess.None, decision.Access);

        await transaction.RollbackAsync();
    }

    // ═════════════════════════════ Broker admins ═════════════════════════════

    /// <summary>
    /// Read, not write. Oversight does not include editing content the agent is licensed for,
    /// which mirrors the <c>listings_broker_admin_select</c> policy in the migration.
    /// </summary>
    [Fact]
    public async Task ApprovedBrokerAdminOfTheSameBrokerage_GetsReadOnly()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var secondAdmin = Guid.CreateVersion7();
        await CreateAgentAsync(context, secondAdmin, "second-admin");
        await CreateMembershipAsync(
            context, secondAdmin, PacificCrest, MembershipStatus.Approved, MembershipRole.BrokerAdmin);

        var listingId = await CreateListingAsync(context, AgentA, PacificCrest);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(secondAdmin, listingId, default);

        Assert.Equal(ListingAccess.Read, decision.Access);
        Assert.Equal(AuthorizationReason.ApprovedBrokerAdmin, decision.Reason);
        Assert.True(decision.CanRead);
        Assert.False(decision.CanWrite);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// <b>FR-050's second condition, and the subtlest rule in the matrix.</b> An approved admin
    /// may not read the listing of an agent whose own membership is still pending — otherwise a
    /// brokerage would gain visibility into an agent's private work simply because that agent
    /// typed its name, before anyone confirmed the relationship exists.
    /// </summary>
    [Fact]
    public async Task ApprovedBrokerAdmin_CannotReadAPendingAgentsListing()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        // Agent B's membership at Pacific Crest is pending in the seed.
        var listingId = await CreateListingAsync(context, AgentB, PacificCrest);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(AgentA, listingId, default);

        Assert.Equal(ListingAccess.None, decision.Access);
        Assert.Equal(AuthorizationReason.SubjectMembershipNotApproved, decision.Reason);
        Assert.True(decision.ShouldReportAsNotFound);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// A revoked admin loses access immediately, not when their token expires. This is why
    /// membership is resolved from the database per request instead of from a JWT claim.
    /// </summary>
    [Fact]
    public async Task RevokedBrokerAdmin_GetsNothing()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var revokedAdmin = Guid.CreateVersion7();
        await CreateAgentAsync(context, revokedAdmin, "revoked-admin");
        await CreateMembershipAsync(
            context, revokedAdmin, PacificCrest, MembershipStatus.Revoked, MembershipRole.BrokerAdmin);

        var listingId = await CreateListingAsync(context, AgentA, PacificCrest);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(revokedAdmin, listingId, default);

        Assert.Equal(ListingAccess.None, decision.Access);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task BrokerAdminOfADifferentBrokerage_GetsNothing()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var otherAdmin = Guid.CreateVersion7();
        await CreateAgentAsync(context, otherAdmin, "other-brokerage-admin");
        await CreateMembershipAsync(
            context, otherAdmin, HarborPoint, MembershipStatus.Approved, MembershipRole.BrokerAdmin);

        var listingId = await CreateListingAsync(context, AgentA, PacificCrest);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(otherAdmin, listingId, default);

        Assert.Equal(ListingAccess.None, decision.Access);

        await transaction.RollbackAsync();
    }

    // ═════════════════════════════ Platform administrator ═════════════════════════════

    /// <summary>
    /// <b>A platform administrator gets no listing access at all.</b>
    ///
    /// <para>
    /// Deliberate, and the most likely thing for someone to "fix" later. <c>platform_admin</c>
    /// governs the AI model registry and prompt versions (FR-064) — it is a configuration role.
    /// Letting it read listing content would quietly turn the operator of the platform into a
    /// reader of every brokerage's confidential work, which no requirement asks for and no
    /// agent consented to.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PlatformAdministrator_GetsNoListingAccess()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var listingId = await CreateListingAsync(context, AgentA, PacificCrest);

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(PlatformAdmin, listingId, default);

        Assert.Equal(ListingAccess.None, decision.Access);
        Assert.False(decision.CanRead);

        await transaction.RollbackAsync();
    }

    // ═════════════════════════════ Listings with no brokerage ═════════════════════════════

    /// <summary>
    /// A listing with no brokerage is reachable only by its owner: there is no brokerage for an
    /// admin to derive authority from.
    /// </summary>
    [Fact]
    public async Task ListingWithNoBrokerage_IsReachableOnlyByItsOwner()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var listingId = await CreateListingAsync(context, AgentA, brokerageId: null);
        var authorizer = CreateAuthorizer(context);

        var owner = await authorizer.AuthorizeListingAsync(AgentA, listingId, default);
        Assert.Equal(ListingAccess.ReadWrite, owner.Access);

        var secondAdmin = Guid.CreateVersion7();
        await CreateAgentAsync(context, secondAdmin, "admin-no-brokerage-case");
        await CreateMembershipAsync(
            context, secondAdmin, PacificCrest, MembershipStatus.Approved, MembershipRole.BrokerAdmin);

        var admin = await CreateAuthorizer(context)
            .AuthorizeListingAsync(secondAdmin, listingId, default);

        Assert.Equal(ListingAccess.None, admin.Access);
        Assert.Equal(AuthorizationReason.NotRelated, admin.Reason);

        await transaction.RollbackAsync();
    }

    // ═════════════════════════════ Missing listing ═════════════════════════════

    [Fact]
    public async Task UnknownListing_IsReportedAsNotFound()
    {
        await using var context = CreateContext();

        var decision = await CreateAuthorizer(context)
            .AuthorizeListingAsync(AgentA, Guid.CreateVersion7(), default);

        Assert.Equal(ListingAccess.None, decision.Access);
        Assert.Equal(AuthorizationReason.ListingNotFound, decision.Reason);
        Assert.True(decision.ShouldReportAsNotFound);
    }

    /// <summary>
    /// Nothing the authorizer returns on a denial should distinguish "exists but not yours"
    /// from "does not exist", or the endpoint built on it cannot help leaking that difference.
    /// </summary>
    [Fact]
    public async Task EveryDenialReportsAsNotFound()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var listingId = await CreateListingAsync(context, AgentA, PacificCrest);
        var authorizer = CreateAuthorizer(context);

        foreach (var caller in new[] { AgentB, AgentC, PlatformAdmin })
        {
            var decision = await authorizer.AuthorizeListingAsync(caller, listingId, default);

            Assert.Equal(ListingAccess.None, decision.Access);
            Assert.True(
                decision.ShouldReportAsNotFound,
                $"Caller {caller} was denied with reason {decision.Reason}, which does not map "
                + "to 404 — that difference is observable and confirms the listing exists.");
        }

        await transaction.RollbackAsync();
    }
}
