using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pipeline;
using RealtorAI.Tests.Contract.Infrastructure;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T056 — the pipeline progress hub (FR-053).
///
/// <para>
/// Covers who may connect, who may subscribe to which listing, that a subscriber hears only the
/// listing it subscribed to, the wire shape of the two messages, and that stateful reconnect —
/// the feature CVE-2026-70159 abuses — is off.
/// </para>
/// <para>
/// The subscription rule is owner-only, narrower than the polling endpoint. The case that shows
/// the narrowing is an approved broker admin following an approved agent's listing: the polling
/// endpoint lets them read it, the hub does not, because a subscription would outlive a
/// membership revocation. That persona does not exist in the seed, so this suite creates one.
/// </para>
/// </summary>
public sealed class PipelineHubTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait before concluding a message will not arrive.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(2);

    private readonly ApiFactory factory;
    private readonly List<Guid> listings = [];
    private readonly List<Guid> agents = [];

    public PipelineHubTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var listing in listings)
        {
            await TestDatabase.ExecuteAsync("delete from public.listings where id = $1", listing);
        }

        foreach (var agent in agents)
        {
            // Cascades to the profile and its memberships.
            await TestDatabase.ExecuteAsync("delete from auth.users where id = $1", agent);
        }
    }

    // ───────────────────────────── Arrangement ─────────────────────────────

    private async Task<Guid> CreateListingAsync(Guid owner, Guid? brokerage)
    {
        var id = Guid.CreateVersion7();
        listings.Add(id);

        await TestDatabase.ExecuteAsync(
            """
            insert into public.listings
                (id, agent_id, brokerage_id, address_line1, city, state, postal_code, property_type)
            values ($1, $2, $3, '1 Hub Way', 'Testville', 'CA', '90001', 'Residential')
            """,
            id,
            owner,
            brokerage);

        return id;
    }

    /// <summary>An agent with an APPROVED membership at Pacific Crest.</summary>
    private async Task<Guid> CreateApprovedAgentAsync()
    {
        var id = Guid.CreateVersion7();
        agents.Add(id);

        // Token columns as empty strings, as in seed.sql: GoTrue cannot scan NULL into them.
        await TestDatabase.ExecuteAsync(
            """
            insert into auth.users
                (id, instance_id, aud, role, email, encrypted_password, email_confirmed_at,
                 created_at, updated_at, confirmation_token, recovery_token, email_change,
                 email_change_token_new)
            values ($1, '00000000-0000-0000-0000-000000000000', 'authenticated', 'authenticated',
                    $2, 'x', timezone('utc', now()), timezone('utc', now()), timezone('utc', now()),
                    '', '', '', '')
            """,
            id,
            $"hub-{id:N}@contract.test");

        await TestDatabase.ExecuteAsync(
            """
            insert into public.brokerage_memberships
                (agent_id, brokerage_id, status, role, decided_by, decided_at)
            values ($1, $2, 'approved', 'agent', $3, timezone('utc', now()))
            """,
            id,
            Seed.PacificCrest,
            Seed.AgentA);

        return id;
    }

    private HubConnection Connect(Guid agentId) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, "hubs/pipeline"),
                options =>
                {
                    // The in-memory test server has no WebSocket upgrade path over this handler;
                    // long polling exercises the same hub and authorization pipeline.
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(TestTokens.For(agentId));
                })
            .Build();

    private IPipelineProgressNotifier Notifier() =>
        factory.Services.GetRequiredService<IPipelineProgressNotifier>();

    private static Task<JsonElement> Next(HubConnection connection, string method)
    {
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(method, message => received.TrySetResult(message));

        return received.Task;
    }

    // ═════════════════════════════ Connecting ═════════════════════════════

    [Fact]
    public async Task Negotiate_WithoutAToken_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsync(
            new Uri("/hubs/pipeline/negotiate?negotiateVersion=1", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Browsers send the hub token in the query string, so the hub path must accept it there.
    /// </summary>
    [Fact]
    public async Task Negotiate_WithATokenInTheQueryString_Is200()
    {
        using var client = factory.CreateAnonymousClient();
        var token = TestTokens.For(Seed.AgentB);

        var response = await client.PostAsync(
            new Uri($"/hubs/pipeline/negotiate?negotiateVersion=1&access_token={token}", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// ...and nowhere else. A query-string token on an ordinary endpoint is ignored, even a
    /// valid platform-administrator token, so tokens cannot drift into URLs across the API.
    /// </summary>
    [Fact]
    public async Task AQueryStringToken_IsIgnoredOutsideTheHub()
    {
        using var client = factory.CreateAnonymousClient();
        var token = TestTokens.For(Seed.PlatformAdmin);

        var response = await client.GetAsync(
            new Uri($"/api/admin/ai/registry?access_token={token}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ═════════════════════════════ Subscribing ═════════════════════════════

    /// <summary>
    /// The owner hears both messages, with the contract's lower-snake-case state and step names
    /// rather than enum ordinals.
    /// </summary>
    [Fact]
    public async Task Owner_Subscribes_AndReceivesPhotoAndStepProgress()
    {
        var listing = await CreateListingAsync(Seed.AgentB, Seed.PacificCrest);
        var photo = Guid.CreateVersion7();

        await using var connection = Connect(Seed.AgentB);
        var photoMessage = Next(connection, "PhotoProgress");
        var stepMessage = Next(connection, "StepProgress");
        await connection.StartAsync();

        await connection.InvokeAsync("SubscribeToListing", listing);

        await Notifier().PhotoChangedAsync(new PhotoProgress(listing, photo, PhotoState.Analyzing, null), default);
        await Notifier().StepChangedAsync(
            new StepProgress(listing, PipelineStep.TechnicalQuality, StepState.Skipped, "No model is enabled."),
            default);

        var photoProgress = await photoMessage.WaitAsync(Delivery);
        Assert.Equal(listing, photoProgress.GetProperty("listingId").GetGuid());
        Assert.Equal(photo, photoProgress.GetProperty("photoId").GetGuid());
        Assert.Equal("analyzing", photoProgress.GetProperty("state").GetString());

        var stepProgress = await stepMessage.WaitAsync(Delivery);
        Assert.Equal("technical_quality", stepProgress.GetProperty("step").GetString());
        Assert.Equal("skipped", stepProgress.GetProperty("state").GetString());
        Assert.Equal("No model is enabled.", stepProgress.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task AnUnrelatedAgent_CannotSubscribe()
    {
        var listing = await CreateListingAsync(Seed.AgentB, Seed.PacificCrest);

        await using var connection = Connect(Seed.AgentC);
        await connection.StartAsync();

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("SubscribeToListing", listing));
    }

    /// <summary>
    /// The narrowing, shown directly: this admin may poll this listing's status, but may not
    /// subscribe to it.
    /// </summary>
    [Fact]
    public async Task AnApprovedBrokerAdmin_CannotSubscribe_EvenToAListingTheyMayRead()
    {
        var owner = await CreateApprovedAgentAsync();
        var listing = await CreateListingAsync(owner, Seed.PacificCrest);

        await using var connection = Connect(Seed.AgentA);
        await connection.StartAsync();

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("SubscribeToListing", listing));
    }

    /// <summary>
    /// An id that does not exist is refused with the same message as one the caller does not
    /// own, so the hub cannot be used to discover which listing ids exist.
    /// </summary>
    [Fact]
    public async Task AnUnknownListing_IsRefusedIndistinguishablyFromAForeignOne()
    {
        var foreign = await CreateListingAsync(Seed.AgentB, Seed.PacificCrest);

        await using var connection = Connect(Seed.AgentC);
        await connection.StartAsync();

        var unknown = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("SubscribeToListing", Guid.CreateVersion7()));
        var notOwned = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("SubscribeToListing", foreign));

        Assert.Equal(notOwned.Message, unknown.Message);
    }

    /// <summary>A refused subscriber hears nothing about the listing it asked for.</summary>
    [Fact]
    public async Task ARefusedSubscriber_ReceivesNothing()
    {
        var listing = await CreateListingAsync(Seed.AgentB, Seed.PacificCrest);

        await using var connection = Connect(Seed.AgentC);
        var received = Next(connection, "PhotoProgress");
        await connection.StartAsync();

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("SubscribeToListing", listing));

        await Notifier().PhotoChangedAsync(
            new PhotoProgress(listing, Guid.CreateVersion7(), PhotoState.Done, null),
            default);

        await Task.WhenAny(received, Task.Delay(Silence));
        Assert.False(received.IsCompleted, "A refused subscriber received another agent's progress.");
    }

    [Fact]
    public async Task ASubscriber_HearsOnlyTheListingTheySubscribedTo()
    {
        var mine = await CreateListingAsync(Seed.AgentB, Seed.PacificCrest);
        var other = await CreateListingAsync(Seed.AgentB, Seed.PacificCrest);

        await using var connection = Connect(Seed.AgentB);
        var received = Next(connection, "PhotoProgress");
        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeToListing", mine);

        await Notifier().PhotoChangedAsync(
            new PhotoProgress(other, Guid.CreateVersion7(), PhotoState.Done, null),
            default);

        await Task.WhenAny(received, Task.Delay(Silence));
        Assert.False(received.IsCompleted, "A subscriber received progress for a listing it did not subscribe to.");
    }

    [Fact]
    public async Task Unsubscribing_StopsDelivery()
    {
        var listing = await CreateListingAsync(Seed.AgentB, Seed.PacificCrest);

        await using var connection = Connect(Seed.AgentB);
        var received = Next(connection, "PhotoProgress");
        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeToListing", listing);
        await connection.InvokeAsync("UnsubscribeFromListing", listing);

        await Notifier().PhotoChangedAsync(
            new PhotoProgress(listing, Guid.CreateVersion7(), PhotoState.Done, null),
            default);

        await Task.WhenAny(received, Task.Delay(Silence));
        Assert.False(received.IsCompleted);
    }

    /// <summary>
    /// A notification with no subscribers, or one that cannot be delivered, must not throw into
    /// the pipeline step that sent it.
    /// </summary>
    [Fact]
    public async Task Notifying_WithNoSubscribers_DoesNotThrow()
    {
        await Notifier().StepChangedAsync(
            new StepProgress(Guid.CreateVersion7(), PipelineStep.Normalize, StepState.Done, null),
            default);
    }

    // ═════════════════════════════ CVE-2026-70159 ═════════════════════════════

    /// <summary>
    /// Read from the routing table the server actually serves, not from the setup method: this
    /// fails if anyone re-enables stateful reconnect for the hub by any route.
    /// </summary>
    [Fact]
    public void StatefulReconnect_IsDisabledOnTheHub()
    {
        var hubEndpoints = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/hubs/pipeline", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(hubEndpoints);

        var options = hubEndpoints
            .Select(endpoint => endpoint.Metadata.GetMetadata<HttpConnectionDispatcherOptions>())
            .Where(found => found is not null)
            .ToList();

        Assert.NotEmpty(options);
        Assert.All(options, found => Assert.False(found!.AllowStatefulReconnects));
    }
}
