using System.Security.Claims;
using System.Text.Json;
using Api.Authentication;
using Api.Authorization;
using Api.Data;
using Api.Data.Entities;
using Libs.Ai;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Npgsql;

namespace Api.Endpoints;

/// <summary>
/// A registry row as the admin API reads and writes it (contracts/api.openapi.yaml
/// <c>RegistryEntry</c>).
/// </summary>
/// <remarks>
/// <c>EvalStatus</c>, <c>EvalResult</c> and <c>VerifiedAt</c> are accepted on input only so a
/// client can send back what it read. They are owned by the evaluation harness, and a request
/// that tries to change them is refused — see <see cref="AiRegistryEndpoints"/>.
/// </remarks>
internal sealed record RegistryEntryDto(
    Guid? Id,
    string? TaskKey,
    string? Provider,
    string? ModelId,
    string? Endpoint,
    JsonElement? Parameters,
    Guid? FallbackRegistryId,
    string? License,
    string? EvalStatus,
    JsonElement? EvalResult,
    bool? Enabled,
    DateTimeOffset? VerifiedAt);

/// <summary>
/// T050 — the model registry admin endpoints (FR-064, Principle VIII.2 and VIII.4).
///
/// <para>
/// <b>Who.</b> Platform administrators only, resolved from <c>profiles.platform_admin</c> per
/// request by <see cref="PlatformAdminHandler"/>. Broker admins are not platform administrators;
/// the two roles are unrelated.
/// </para>
/// <para>
/// <b>What a change may and may not do.</b> VIII.4 permits enabling a model only with a recorded
/// license <i>and</i> a passing evaluation. The database enforces that with a check constraint,
/// which this endpoint cannot bypass — but a check constraint only sees column values, so three
/// ways of satisfying it without meeting its intent are closed here:
/// </para>
/// <list type="number">
///   <item>
///     <b>Self-certifying an evaluation.</b> <c>evalStatus</c>, <c>evalResult</c> and
///     <c>verifiedAt</c> are written by the evaluation harness. If this endpoint accepted
///     <c>evalStatus: "passed"</c>, typing the word would satisfy the constraint.
///   </item>
///   <item>
///     <b>Swapping the model under a pass.</b> An evaluation and a license describe one model.
///     Changing the provider, model id or endpoint resets the evaluation, clears the license
///     unless a new one is recorded in the same change, and leaves the row disabled. A request
///     to change the model <i>and</i> keep it enabled is refused rather than silently disabled.
///   </item>
///   <item>
///     <b>Moving a row to another task.</b> The task key is fixed; a pass for photo analysis
///     says nothing about compliance judging.
///   </item>
/// </list>
/// <para>
/// <b>Audit.</b> Every effective change is recorded as <c>registry_changed</c> with the acting
/// administrator and the row before and after, committed in the same transaction as the
/// change. A request that changes nothing records nothing.
/// </para>
/// </summary>
internal static class AiRegistryEndpoints
{
    private const string AuditTargetType = "ai_model_registry";

    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web);

    internal static IEndpointRouteBuilder MapAiRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/admin/ai/registry")
            .RequireAuthorization(PolicyRegistry.PlatformAdmin)
            .WithTags("AI Registry");

        group.MapGet(string.Empty, ListAsync).WithName("ListAiRegistry");
        group.MapPut(string.Empty, UpdateAsync).WithName("UpdateAiRegistryEntry");

        return app;
    }

    private static async Task<Ok<List<RegistryEntryDto>>> ListAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var rows = await context.AiModelRegistry
            .AsNoTracking()
            .OrderBy(entry => entry.TaskKey)
            .ThenByDescending(entry => entry.Enabled)
            .ThenBy(entry => entry.ModelId)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<RegistryEntryDto>, ProblemHttpResult, UnauthorizedHttpResult>> UpdateAsync(
        RegistryEntryDto request,
        ClaimsPrincipal user,
        AppDbContext context,
        IAuditWriter audit,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } actorId)
        {
            return TypedResults.Unauthorized();
        }

        if (request.Id is not { } id)
        {
            return ApiProblems.Validation("id", "The registry entry id is required.");
        }

        var entry = await context.AiModelRegistry
            .SingleOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (entry is null)
        {
            return ApiProblems.NotFound($"No registry entry has id {id}.");
        }

        var errors = await ValidateAsync(request, entry, context, cancellationToken);

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        var before = Snapshot(entry);

        Apply(request, entry);

        var after = Snapshot(entry);

        if (before == after)
        {
            // Nothing changed. Saying so with a 200 keeps a read-modify-write client simple;
            // recording it would bury real changes under empty ones.
            return TypedResults.Ok(ToDto(entry));
        }

        // The gate, checked here so the administrator gets a precise reason. The database
        // check constraint below remains the guarantee; this is the explanation.
        if (entry.Enabled
            && (entry.License is null || entry.EvalStatus != ModelEvalStatus.Passed))
        {
            return ApiProblems.Validation(
                "enabled",
                "A model can be enabled only once its commercial license is recorded and the "
                + "evaluation harness has passed it (Principle VIII.4). This entry has "
                + (entry.License is null ? "no license recorded" : "a license recorded")
                + $" and evaluation status '{FormatEvalStatus(entry.EvalStatus)}'.");
        }

        if (entry.Enabled
            && await context.AiModelRegistry.AnyAsync(
                other => other.Enabled && other.TaskKey == entry.TaskKey && other.Id != entry.Id,
                cancellationToken))
        {
            return EnabledConflict(entry.TaskKey);
        }

        await audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.RegistryChanged,
                ActorId = actorId,
                TargetType = AuditTargetType,
                TargetId = entry.Id,
                BeforeValue = before,
                AfterValue = after,
            },
            cancellationToken);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres)
        {
            // A concurrent change can still reach the database between the checks above and
            // this save. The constraints decide; these translate their answer.
            switch (postgres.ConstraintName)
            {
                case "registry_one_enabled_per_task":
                    return EnabledConflict(entry.TaskKey);

                case "registry_enable_requires_license_and_eval":
                    return ApiProblems.Validation(
                        "enabled",
                        "The database refused to enable this entry: it requires a recorded "
                        + "license and a passing evaluation (Principle VIII.4).");

                case "registry_no_self_fallback":
                    return ApiProblems.Validation(
                        "fallbackRegistryId",
                        "An entry cannot be its own fallback.");

                default:
                    throw;
            }
        }

        return TypedResults.Ok(ToDto(entry));
    }

    private static ProblemHttpResult EnabledConflict(string taskKey) =>
        ApiProblems.Conflict(
            "Another model is already enabled for this task",
            $"Only one model may be enabled for '{taskKey}' at a time, so dispatch is never "
            + "ambiguous. Disable the current one first; the two changes are audited "
            + "separately.");

    // ───────────────────────────── Validation ─────────────────────────────

    private static async Task<Dictionary<string, string[]>> ValidateAsync(
        RegistryEntryDto request,
        AiModelRegistryEntry entry,
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.TaskKey is not null && request.TaskKey != entry.TaskKey)
        {
            errors["taskKey"] =
            [
                "The task key cannot be changed. An evaluation and a license apply to the task "
                + "they were recorded for; create a separate entry for another task.",
            ];
        }

        if (string.IsNullOrWhiteSpace(request.Provider))
        {
            errors["provider"] = ["A provider is required."];
        }

        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            errors["modelId"] = ["A model id is required."];
        }

        if (request.Endpoint is { Length: > 0 } endpoint
            && !string.IsNullOrWhiteSpace(endpoint)
            && (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
        {
            errors["endpoint"] = ["The endpoint must be an absolute http or https URL."];
        }

        if (request.Parameters is not { ValueKind: JsonValueKind.Object })
        {
            errors["parameters"] = ["Parameters must be a JSON object."];
        }

        if (request.License is not null && string.IsNullOrWhiteSpace(request.License))
        {
            errors["license"] =
            [
                "A license must be the actual license identifier, or null if none is recorded.",
            ];
        }

        // Harness-owned fields: echoing what was read is fine, changing it is not.
        if (request.EvalStatus is not null && request.EvalStatus != FormatEvalStatus(entry.EvalStatus))
        {
            errors["evalStatus"] = [HarnessOwned("evalStatus")];
        }

        if (request.EvalResult is { ValueKind: not JsonValueKind.Null } evalResult
            && (entry.EvalResult is null
                || !JsonElement.DeepEquals(evalResult, Parse(entry.EvalResult))))
        {
            errors["evalResult"] = [HarnessOwned("evalResult")];
        }

        if (request.VerifiedAt is { } verifiedAt && verifiedAt != entry.VerifiedAt)
        {
            errors["verifiedAt"] = [HarnessOwned("verifiedAt")];
        }

        if (request.Enabled is null)
        {
            errors["enabled"] = ["State whether the entry is enabled."];
        }
        else if (request.Enabled.Value && ChangesModelIdentity(request, entry))
        {
            errors["enabled"] =
            [
                "Changing the provider, model id or endpoint resets this entry's evaluation, so "
                + "it cannot stay enabled. Save the change with enabled=false, run the "
                + "evaluation harness against the new model, then enable it.",
            ];
        }

        if (request.FallbackRegistryId is { } fallbackId)
        {
            if (fallbackId == entry.Id)
            {
                errors["fallbackRegistryId"] = ["An entry cannot be its own fallback."];
            }
            else
            {
                var fallbackTask = await context.AiModelRegistry
                    .AsNoTracking()
                    .Where(row => row.Id == fallbackId)
                    .Select(row => row.TaskKey)
                    .SingleOrDefaultAsync(cancellationToken);

                if (fallbackTask is null)
                {
                    errors["fallbackRegistryId"] = [$"No registry entry has id {fallbackId}."];
                }
                else if (fallbackTask != entry.TaskKey)
                {
                    errors["fallbackRegistryId"] =
                    [
                        $"The fallback serves '{fallbackTask}', not '{entry.TaskKey}'. A fallback "
                        + "must be able to answer the same task.",
                    ];
                }
            }
        }

        return errors;
    }

    private static string HarnessOwned(string field) =>
        $"'{field}' is written by the evaluation harness and cannot be set through this API. "
        + "Principle VIII.4 requires a model to pass the harness, not to be declared as having "
        + "passed. Send back the value you read, or omit it.";

    private static bool ChangesModelIdentity(RegistryEntryDto request, AiModelRegistryEntry entry) =>
        request.Provider?.Trim() != entry.Provider
        || request.ModelId?.Trim() != entry.ModelId
        || NormalizeEndpoint(request.Endpoint) != entry.Endpoint;

    // ───────────────────────────── Mutation ─────────────────────────────

    /// <summary>
    /// Applies a validated request. The model-identity rule lives here so it cannot be skipped
    /// by a path that validates differently.
    /// </summary>
    private static void Apply(RegistryEntryDto request, AiModelRegistryEntry entry)
    {
        var identityChanged = ChangesModelIdentity(request, entry);
        var licenseChanged = request.License != entry.License;

        entry.Provider = request.Provider!.Trim();
        entry.ModelId = request.ModelId!.Trim();
        entry.Endpoint = NormalizeEndpoint(request.Endpoint);
        entry.FallbackRegistryId = request.FallbackRegistryId;

        // Re-serialized rather than stored as received, so formatting differences between
        // clients cannot register as a change.
        var parameters = request.Parameters!.Value;
        if (!JsonElement.DeepEquals(parameters, Parse(entry.Parameters)))
        {
            entry.Parameters = parameters.GetRawText();
        }

        entry.License = request.License?.Trim();
        entry.Enabled = request.Enabled!.Value;

        if (identityChanged)
        {
            // The evaluation was of the previous model. So was the license, unless the
            // administrator recorded a new one as part of this same change.
            entry.EvalStatus = ModelEvalStatus.Unverified;
            entry.EvalResult = null;
            entry.VerifiedAt = null;
            entry.Enabled = false;

            if (!licenseChanged)
            {
                entry.License = null;
            }
        }
    }

    private static string? NormalizeEndpoint(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();

    // ───────────────────────────── Representation ─────────────────────────────

    /// <summary>
    /// The audited form of a row. Serialized with parsed jsonb values rather than their raw
    /// text, so two snapshots compare equal exactly when the row's meaning is the same.
    /// </summary>
    private static string Snapshot(AiModelRegistryEntry entry) =>
        JsonSerializer.Serialize(ToDto(entry), SnapshotOptions);

    private static RegistryEntryDto ToDto(AiModelRegistryEntry entry) => new(
        entry.Id,
        entry.TaskKey,
        entry.Provider,
        entry.ModelId,
        entry.Endpoint,
        Parse(entry.Parameters),
        entry.FallbackRegistryId,
        entry.License,
        FormatEvalStatus(entry.EvalStatus),
        entry.EvalResult is null ? null : Parse(entry.EvalResult),
        entry.Enabled,
        entry.VerifiedAt);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The contract's spelling of the status — the same labels as the <c>model_eval_status</c>
    /// database enum.
    /// </summary>
    private static string FormatEvalStatus(ModelEvalStatus status) => status switch
    {
        ModelEvalStatus.Unverified => "unverified",
        ModelEvalStatus.Passed => "passed",
        ModelEvalStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped evaluation status."),
    };
}
