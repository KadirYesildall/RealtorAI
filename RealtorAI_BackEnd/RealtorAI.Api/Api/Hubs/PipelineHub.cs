using Api.Authentication;
using Api.Authorization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Pipeline;

namespace Api.Hubs;

/// <summary>Messages the server sends to a pipeline subscriber.</summary>
public interface IPipelineClient
{
    Task StepProgress(StepProgress progress);

    Task PhotoProgress(PhotoProgress progress);
}

/// <summary>
/// T056 — live pipeline progress (FR-053): per-photo results reach the agent as each photo
/// completes, rather than in one batch at the end.
///
/// <para>
/// <b>Who may subscribe: the listing's owner, and nobody else.</b> This is deliberately
/// narrower than the polling endpoint, which also admits an approved broker admin. The
/// difference is when authorization happens. A poll is authorized on every request, so a
/// revoked membership takes effect on the next one. A subscription is authorized once, when it
/// is made, and then receives broadcasts for as long as the connection lives — so a broker
/// admin whose membership was revoked would go on receiving an agent's progress until they
/// happened to disconnect, which FR-050/FR-060 do not allow. Ownership, by contrast, never
/// changes, so an owner-only subscription cannot outlive its authorization. Broker admins read
/// progress through the polling endpoint.
/// </para>
/// <para>
/// <b>What is pushed.</b> State changes only — step and photo states and a failure reason. No
/// listing content, photo analysis or generated text travels over this channel; the client
/// re-reads those through the REST endpoints, which authorize every read.
/// </para>
/// </summary>
internal sealed class PipelineHub : Hub<IPipelineClient>
{
    internal const string Path = "/hubs/pipeline";

    private readonly IResourceAuthorizer authorizer;

    public PipelineHub(IResourceAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        this.authorizer = authorizer;
    }

    internal static string GroupFor(Guid listingId) => "listing:" + listingId.ToString("N");

    /// <summary>Starts receiving progress for one listing.</summary>
    /// <exception cref="HubException">
    /// The caller does not own the listing. The message is the same whether the listing exists
    /// or not, so the hub cannot be used to probe for listing ids.
    /// </exception>
    public async Task SubscribeToListing(Guid listingId)
    {
        var agentId = Context.User?.GetAgentId()
            ?? throw new HubException("Not authenticated.");

        var decision = await authorizer.AuthorizeListingAsync(
            agentId,
            listingId,
            Context.ConnectionAborted);

        if (decision.Reason != AuthorizationReason.Owner)
        {
            throw new HubException(
                "You can follow pipeline progress only for listings you own.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(listingId), Context.ConnectionAborted);
    }

    /// <summary>Stops receiving progress for one listing. Unsubscribing from anything is harmless.</summary>
    public Task UnsubscribeFromListing(Guid listingId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(listingId), Context.ConnectionAborted);
}

/// <summary>Delivers pipeline progress to subscribers over <see cref="PipelineHub"/>.</summary>
internal sealed partial class SignalRPipelineProgressNotifier : IPipelineProgressNotifier
{
    private readonly IHubContext<PipelineHub, IPipelineClient> hub;
    private readonly ILogger<SignalRPipelineProgressNotifier> logger;

    public SignalRPipelineProgressNotifier(
        IHubContext<PipelineHub, IPipelineClient> hub,
        ILogger<SignalRPipelineProgressNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(logger);
        this.hub = hub;
        this.logger = logger;
    }

    public async Task StepChangedAsync(StepProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        try
        {
            await hub.Clients.Group(PipelineHub.GroupFor(progress.ListingId)).StepProgress(progress);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort by contract (IPipelineProgressNotifier): polling reads persisted
            // state, so a lost notification is a stale screen, never a lost result.
            LogStepDeliveryFailed(logger, exception, progress.Step, progress.State, progress.ListingId);
        }
    }

    public async Task PhotoChangedAsync(PhotoProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        try
        {
            await hub.Clients.Group(PipelineHub.GroupFor(progress.ListingId)).PhotoProgress(progress);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPhotoDeliveryFailed(logger, exception, progress.PhotoId, progress.State, progress.ListingId);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not deliver step progress {Step}={State} for listing {ListingId}")]
    private static partial void LogStepDeliveryFailed(
        ILogger logger,
        Exception exception,
        PipelineStep step,
        StepState state,
        Guid listingId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not deliver photo progress {PhotoId}={State} for listing {ListingId}")]
    private static partial void LogPhotoDeliveryFailed(
        ILogger logger,
        Exception exception,
        Guid photoId,
        PhotoState state,
        Guid listingId);
}

internal static class PipelineHubSetup
{
    /// <summary>The CORS policy the hub, and only the hub, is reachable under.</summary>
    internal const string CorsPolicy = "pipeline-hub";

    internal static IServiceCollection AddPipelineHub(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The hub is the one API path a browser calls directly (FR-053): everything else goes
        // through the Next.js server. Origins are configuration, and credentials are not allowed
        // — the token travels as access_token on /hubs paths, never as a cookie, so a cross-site
        // page gains nothing by connecting.
        var origins = configuration.GetSection("Cors:PipelineHubOrigins").Get<string[]>() ?? [];

        services.AddCors(options => options.AddPolicy(
            CorsPolicy,
            policy => policy.WithOrigins(origins).AllowAnyHeader().WithMethods("GET", "POST")));

        services.AddSignalR();
        services.AddSingleton<IPipelineProgressNotifier, SignalRPipelineProgressNotifier>();

        return services;
    }

    internal static IEndpointRouteBuilder MapPipelineHub(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // No WithName: MapHub registers two endpoints (negotiate and connect), and one name on
        // both would make endpoint-name lookup ambiguous.
        app.MapHub<PipelineHub>(PipelineHub.Path, ConfigureHub)
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .RequireCors(CorsPolicy);

        return app;
    }

    /// <summary>
    /// Connection options for the hub.
    ///
    /// <para>
    /// <b>Stateful reconnect stays off — this is a security setting.</b> CVE-2026-70159
    /// (GHSA-334f-vvp4-7g6p, CVSS 7.5): a stateful-reconnect client can block broadcasts
    /// indefinitely through <c>MessageBuffer.ResendAsync</c> backpressure and pin server
    /// resources. It affects the ASP.NET Core shared framework through 10.0.12, and the runtime
    /// this host runs on must be 10.0.13 or later. Until every deployment is confirmed on a
    /// patched runtime, the vulnerable path is closed here: with stateful reconnect disabled no
    /// client can reach it. The feature is not needed either way — on reconnect the client
    /// re-subscribes and re-reads status from the polling endpoint. See research R29.
    /// </para>
    /// </summary>
    internal static void ConfigureHub(HttpConnectionDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AllowStatefulReconnects = false;
    }
}
