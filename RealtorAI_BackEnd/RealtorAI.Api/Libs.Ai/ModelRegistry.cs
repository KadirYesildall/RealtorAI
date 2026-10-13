namespace Libs.Ai;

/// <summary>
/// One row of the AI model registry, as the dispatcher needs it.
/// </summary>
/// <param name="Id">Registry row id, recorded on every audit event and usage row.</param>
/// <param name="TaskKey">The capability this entry serves.</param>
/// <param name="Provider">Provider identifier, used to key the circuit breaker.</param>
/// <param name="ModelId">The provider's model identifier.</param>
/// <param name="Endpoint">Base URL, or null to use the provider's configured default.</param>
/// <param name="Parameters">Provider call parameters as a JSON object.</param>
/// <param name="FallbackRegistryId">Entry to try when this one fails, or null.</param>
/// <param name="License">The recorded license. Non-null whenever <paramref name="Enabled"/> is true.</param>
/// <param name="EvalStatus">
/// Whether the evaluation harness has passed for this entry (VIII.4.b). Carried because it is
/// the gate a <i>fallback</i> must clear: a fallback cannot be <paramref name="Enabled"/> — the
/// <c>registry_one_enabled_per_task</c> partial unique index permits only one enabled row per
/// task — so enablement is the wrong question to ask of it.
/// </param>
/// <param name="Enabled">Whether this entry is the one currently dispatched to for its task.</param>
public sealed record ModelRegistryEntry(
    Guid Id,
    string TaskKey,
    string Provider,
    string ModelId,
    string? Endpoint,
    string Parameters,
    Guid? FallbackRegistryId,
    string? License,
    ModelEvalStatus EvalStatus,
    bool Enabled);

/// <summary>Reads registry rows. Implemented in the host, where the EF model lives.</summary>
public interface IModelRegistryStore
{
    /// <summary>The enabled entry for a task, or null when none is enabled.</summary>
    Task<ModelRegistryEntry?> GetEnabledForTaskAsync(string taskKey, CancellationToken cancellationToken);

    /// <summary>One entry by id, used to walk a fallback chain.</summary>
    Task<ModelRegistryEntry?> GetByIdAsync(Guid registryId, CancellationToken cancellationToken);
}

/// <summary>
/// Raised when a task has no usable model.
///
/// <para>
/// A distinct exception type because this is an expected, recoverable condition rather than a
/// bug: every registry entry ships disabled under VIII.4, so "no model enabled" is the default
/// state of a fresh deployment. Callers catch it and degrade — the listing stays completable by
/// hand and the UI says the capability is unavailable (SC-010) — instead of failing the request.
/// </para>
/// </summary>
public sealed class ModelUnavailableException : Exception
{
    public ModelUnavailableException(string taskKey, string reason)
        : base($"No usable model for AI task '{taskKey}': {reason}")
    {
        TaskKey = taskKey;
        Reason = reason;
    }

    public ModelUnavailableException()
        : base("No usable model for the requested AI task.")
    {
        TaskKey = string.Empty;
        Reason = string.Empty;
    }

    public ModelUnavailableException(string message)
        : base(message)
    {
        TaskKey = string.Empty;
        Reason = message;
    }

    public ModelUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
        TaskKey = string.Empty;
        Reason = message;
    }

    public string TaskKey { get; }

    public string Reason { get; }
}

/// <summary>
/// T045 — resolves a task to the model that should serve it, and to its fallback chain.
/// </summary>
public interface IModelRegistry
{
    /// <summary>
    /// The enabled entry for a task.
    /// </summary>
    /// <exception cref="ModelUnavailableException">No entry is enabled for the task.</exception>
    Task<ModelRegistryEntry> ResolveAsync(string taskKey, CancellationToken cancellationToken);

    /// <summary>
    /// The dispatch order for a task: the enabled entry, then its fallback, and so on.
    /// </summary>
    Task<IReadOnlyList<ModelRegistryEntry>> ResolveChainAsync(
        string taskKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// T045 — the registry reader (Principle VIII.1, VIII.4).
///
/// <para>
/// <b>It re-checks the enablement gate it just read.</b> The database already enforces
/// <c>enabled ⇒ license recorded AND eval_status = 'passed'</c> with a check constraint, so a
/// row that fails it cannot exist. This verifies it anyway, because the cost of being wrong is
/// dispatching to a model whose license has not been cleared for commercial use — and a
/// constraint that is dropped or altered by a future migration would otherwise fail silently
/// here. The duplicated check is cheap; discovering it was needed is not.
/// </para>
/// <para>
/// <b>No caching.</b> Deliberate: enabling or disabling a model is an administrative act that
/// must take effect immediately (FR-064), and the registry is read once per AI call, which is
/// already a network round trip to a model. Caching here would trade a guarantee for a
/// saving nobody would notice.
/// </para>
/// </summary>
public sealed class ModelRegistry : IModelRegistry
{
    /// <summary>
    /// Bounds a fallback chain, so a cycle in <c>fallback_registry_id</c> cannot spin forever.
    /// The database forbids an entry pointing at itself, but not a longer loop.
    /// </summary>
    private const int MaxChainLength = 4;

    private readonly IModelRegistryStore store;

    public ModelRegistry(IModelRegistryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public async Task<ModelRegistryEntry> ResolveAsync(
        string taskKey,
        CancellationToken cancellationToken)
    {
        AiTaskKey.EnsureKnown(taskKey);

        var entry = await store.GetEnabledForTaskAsync(taskKey, cancellationToken)
            ?? throw new ModelUnavailableException(
                taskKey,
                "no registry entry is enabled. Every entry ships disabled until its license is "
                + "recorded and the evaluation harness passes (Principle VIII.4), so this is "
                + "the expected state until a platform administrator enables one.");

        EnsureEnablementGateHolds(entry);

        return entry;
    }

    public async Task<IReadOnlyList<ModelRegistryEntry>> ResolveChainAsync(
        string taskKey,
        CancellationToken cancellationToken)
    {
        var primary = await ResolveAsync(taskKey, cancellationToken);

        var chain = new List<ModelRegistryEntry> { primary };
        var visited = new HashSet<Guid> { primary.Id };

        var current = primary;

        while (current.FallbackRegistryId is { } fallbackId && chain.Count < MaxChainLength)
        {
            if (!visited.Add(fallbackId))
            {
                // A cycle. Stop rather than throw: the entries already collected are usable,
                // and refusing to serve a request because of a misconfigured fallback would
                // turn a configuration mistake into an outage.
                break;
            }

            var fallback = await store.GetByIdAsync(fallbackId, cancellationToken);

            if (fallback is null)
            {
                break;
            }

            // A fallback must clear the VIII.4 gate — license recorded and evaluation passed —
            // but must NOT be required to be `enabled`.
            //
            // This was a real bug, caught by ResolveChain_FollowsTheFallbackChain. The original
            // check required fallback.Enabled, and the registry_one_enabled_per_task partial
            // unique index allows exactly one enabled row per task. So no second entry for the
            // same task can ever be enabled, and the entire fallback chain was unreachable:
            // failover silently did nothing. Asking the right question — has this model been
            // licensed and evaluated — is both what VIII.4 actually requires and what makes
            // fallback work.
            if (!HasClearedEnablementGate(fallback))
            {
                break;
            }

            if (!string.Equals(fallback.TaskKey, taskKey, StringComparison.Ordinal))
            {
                // A fallback for a different task would answer a different question in the
                // same shape, which is worse than having no fallback at all.
                break;
            }

            chain.Add(fallback);
            current = fallback;
        }

        return chain;
    }

    /// <summary>
    /// Principle VIII.4: a model may be used only if its license permits commercial use and is
    /// recorded, and it has passed the evaluation harness for its task. Deliberately does not
    /// consider <c>Enabled</c> — that flag answers "is this the current choice for the task",
    /// which is a different question from "is this model cleared for use".
    /// </summary>
    private static bool HasClearedEnablementGate(ModelRegistryEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.License) && entry.EvalStatus == ModelEvalStatus.Passed;

    private static void EnsureEnablementGateHolds(ModelRegistryEntry entry)
    {
        if (!entry.Enabled)
        {
            throw new ModelUnavailableException(
                entry.TaskKey,
                $"registry entry {entry.Id} is not enabled.");
        }

        if (!HasClearedEnablementGate(entry))
        {
            throw new ModelUnavailableException(
                entry.TaskKey,
                $"registry entry {entry.Id} is enabled but has not cleared the VIII.4 gate "
                + $"(license: {entry.License ?? "none"}, eval: {entry.EvalStatus}). This should be "
                + "impossible — the registry_enable_requires_license_and_eval constraint in "
                + "0008_ai_platform.sql forbids it — so the constraint has most likely been "
                + "dropped or altered. Do not dispatch; investigate the schema.");
        }
    }
}
