using System.Security.Claims;
using Api.Ai;
using Api.Audit;
using Api.Authentication;
using Api.Authorization;
using Api.Concurrency;
using Api.Data;
using Api.Endpoints;
using Api.Hubs;
using Api.Media;
using Api.Pipeline;
using OpenTelemetry.Metrics;
using Pipeline;
using OpenTelemetry.Trace;
using Serilog;

// Feature 002-ai-listing-launch — T007 composition root.
//
// Module registration is added here as each module gains endpoints. Authentication lives in
// Api/Authentication/SupabaseJwtSetup.cs (T034); see that file for why membership state is
// never read from a token claim.

var builder = WebApplication.CreateBuilder(args);

// ───────────────────────────── Logging ─────────────────────────────
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// ───────────────────────────── Observability ─────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

// ───────────────────────────── Data ─────────────────────────────
// EF migrations are disabled here; the SQL files in supabase/migrations own all DDL.
builder.Services.AddAppDbContext(builder.Configuration);

// ───────────────────────────── Authentication ─────────────────────────────
builder.Services.AddSupabaseJwtAuthentication(builder.Configuration, builder.Environment);
// T038: policies are declared in one place, and EveryEndpointHasPolicyTests fails if any
// endpoint is neither covered by one nor listed as a deliberate exception.
builder.Services.AddAuthorization(PolicyRegistry.AddEndpointPolicies);
builder.Services.AddPlatformAdminPolicy();

// FR-063: every endpoint authorizes independently. The API connects with a privileged role
// that bypasses RLS, so the database policies are defence in depth, not the access control.
builder.Services.AddResourceAuthorization();

// FR-066: conditional writes guarded by row version.
builder.Services.AddScoped<IConcurrencyGuard, ConcurrencyGuard>();

// FR-049: append-only audit. The sink only adds rows; the endpoint that owns the transaction
// commits the audit record together with the change it describes.
builder.Services.AddScoped<Modules.Audit.IAuditSink, EfAuditSink>();
builder.Services.AddScoped<Modules.Audit.IAuditWriter, Modules.Audit.AuditWriter>();

// Principle VIII: every AI call goes through AiService, which resolves the model from the
// registry. No vendor, model id or endpoint appears anywhere else in the codebase.
builder.Services.AddAiPlatform(builder.Configuration, builder.Environment);

// ───────────────────────────── Media ─────────────────────────────
// Photo storage (write-once originals), non-generative normalization, public-record pre-fill.
builder.Services.AddMedia(builder.Configuration);

// ───────────────────────────── Pipeline ─────────────────────────────
// FR-053: durable background analysis. Hangfire stores jobs in the dedicated `hangfire` schema
// (0013_hangfire_schema.sql), never in `public`.
builder.Services.AddPipelineJobs(new PipelineOptions(
    builder.Configuration.GetConnectionString("Hangfire")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Hangfire is not configured. It points at the same Supabase "
            + "Postgres as ConnectionStrings:Postgres — see quickstart.md."),
    builder.Configuration.GetValue<int?>("Pipeline:WorkerCount")
        ?? throw new InvalidOperationException(
            "Pipeline:WorkerCount is not configured. Size it against the database connection "
            + "pool: each worker holds a connection while a job runs."),
    builder.Configuration["Pipeline:Queue"] ?? HangfireSetup.PipelineQueue));

// The analysis pipeline's steps, their persistence and the vision sidecar (US2).
builder.Services.AddAnalysisPipeline(builder.Configuration);

// Per-photo progress pushed to the listing owner as each step completes (FR-053).
builder.Services.AddPipelineHub(builder.Configuration);

var app = builder.Build();

// VIII.5: refuse to serve traffic if a prompt file no longer matches the hash recorded for
// its version — every audit record citing that version would otherwise be pointing at text
// that did not produce it.
await AiSetup.VerifyPromptIntegrityAsync(app.Services);

app.UseSerilogRequestLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Before authentication, so a preflight is answered without a token. Only the hub has a policy.
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// Liveness probe. Anonymous, and deliberately not reporting dependency state: a liveness
// endpoint that fails when Postgres blips causes restart storms. Dependency readiness is
// added alongside the first endpoint that actually needs a dependency.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .WithName("Health");

// Confirms the token pipeline end to end: returns the agent id resolved from `sub`, and
// nothing else. No membership, no role — those come from the database per request.
app.MapGet("/whoami", (ClaimsPrincipal user) =>
    {
        var agentId = user.GetAgentId();

        return agentId is null
            ? Results.Unauthorized()
            : Results.Ok(new { agentId });
    })
    .RequireAuthorization(PolicyRegistry.Authenticated)
    .WithName("WhoAmI");

// ───────────────────────────── Endpoints ─────────────────────────────
app.MapAiRegistryEndpoints();
app.MapProfileEndpoints();
app.MapBrokerageEndpoints();
app.MapListingEndpoints();
app.MapListingFieldEndpoints();
app.MapPhotoEndpoints();
app.MapPhotoEditEndpoints();
app.MapPipelineEndpoints();
app.MapContentEndpoints();
app.MapComplianceEndpoints();
app.MapApprovalEndpoints();
app.MapAuditEndpoints();
app.MapPipelineHub();

app.Run();
