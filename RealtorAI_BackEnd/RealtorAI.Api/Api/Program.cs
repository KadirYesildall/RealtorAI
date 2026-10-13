using System.Security.Claims;
using Api.Authentication;
using Api.Data;
using OpenTelemetry.Metrics;
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
builder.Services.AddAuthorization();

// ───────────────────────────── Pipeline ─────────────────────────────
var app = builder.Build();

app.UseSerilogRequestLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

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
    .RequireAuthorization()
    .WithName("WhoAmI");

app.Run();
