using Api.Data;
using Libs.Ai;
using Microsoft.EntityFrameworkCore;

namespace Api.Ai;

/// <summary>
/// T045 — host-side adapter reading <c>ai_model_registry</c>.
///
/// <para>
/// <c>Libs.Ai</c> cannot reference the host's EF model without a project cycle, so the registry
/// reader works against this port. The practical benefit is that the dispatch and fallback
/// logic — which is where the Principle VIII.4 gate is enforced — is unit-testable against a
/// fake store, and those tests found a real bug in the fallback chain (research R23).
/// </para>
/// </summary>
internal sealed class EfModelRegistryStore : IModelRegistryStore
{
    private readonly AppDbContext context;

    public EfModelRegistryStore(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    public async Task<ModelRegistryEntry?> GetEnabledForTaskAsync(
        string taskKey,
        CancellationToken cancellationToken)
    {
        // SingleOrDefault, not FirstOrDefault: registry_one_enabled_per_task guarantees at most
        // one enabled row per task, so more than one means the index is gone and dispatch has
        // become ambiguous. Failing loudly is better than silently picking one.
        return await Project(context.AiModelRegistry
                .AsNoTracking()
                .Where(entry => entry.Enabled && entry.TaskKey == taskKey))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<ModelRegistryEntry?> GetByIdAsync(
        Guid registryId,
        CancellationToken cancellationToken)
    {
        return await Project(context.AiModelRegistry
                .AsNoTracking()
                .Where(entry => entry.Id == registryId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static IQueryable<ModelRegistryEntry> Project(
        IQueryable<Data.Entities.AiModelRegistryEntry> query) =>
        query.Select(entry => new ModelRegistryEntry(
            entry.Id,
            entry.TaskKey,
            entry.Provider,
            entry.ModelId,
            entry.Endpoint,
            entry.Parameters,
            entry.FallbackRegistryId,
            entry.License,
            entry.EvalStatus,
            entry.Enabled));
}

/// <summary>T046 — host-side adapter reading recorded prompt hashes.</summary>
internal sealed class EfPromptVersionStore : IPromptVersionStore
{
    private readonly AppDbContext context;

    public EfPromptVersionStore(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    public async Task<IReadOnlyList<(string TaskKey, int Version, string PromptHash)>> GetAllAsync(
        CancellationToken cancellationToken)
    {
        var rows = await context.PromptVersions
            .AsNoTracking()
            .Select(version => new { version.TaskKey, version.Version, version.PromptHash })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => (row.TaskKey, row.Version, row.PromptHash))
            .ToList();
    }
}

/// <summary>
/// T049 — host-side adapter for usage accounting and budgets.
/// </summary>
internal sealed class EfAiUsageStore : IAiUsageStore
{
    private readonly AppDbContext context;

    public EfAiUsageStore(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    public async Task<AiConsumption> GetConsumptionAsync(
        Guid listingId,
        CancellationToken cancellationToken)
    {
        // Cache hits are excluded from consumption: they cost no tokens, and counting them
        // would let the cache — an optimisation — exhaust a listing's budget.
        var rows = await context.AiUsage
            .AsNoTracking()
            .Where(usage => usage.ListingId == listingId && !usage.CacheHit)
            .Select(usage => new { usage.TokensIn, usage.TokensOut, usage.ImageCount })
            .ToListAsync(cancellationToken);

        return new AiConsumption(
            rows.Sum(row => row.TokensIn + row.TokensOut),
            rows.Sum(row => row.ImageCount));
    }

    public async Task<AiBudget?> GetBudgetForListingAsync(
        Guid listingId,
        CancellationToken cancellationToken)
    {
        // A listing with no brokerage, or a brokerage with no settings row, falls back to the
        // application default rather than being refused.
        var budget = await context.Listings
            .AsNoTracking()
            .Where(listing => listing.Id == listingId && listing.BrokerageId != null)
            .Join(
                context.BrokerageSettings.AsNoTracking(),
                listing => listing.BrokerageId,
                settings => settings.BrokerageId,
                (listing, settings) => new { settings.AiBudgetTokens, settings.AiBudgetImages })
            .SingleOrDefaultAsync(cancellationToken);

        return budget is null
            ? null
            : new AiBudget(budget.AiBudgetTokens, budget.AiBudgetImages);
    }

    public async Task RecordAsync(
        Guid listingId,
        string taskKey,
        string modelId,
        int tokensIn,
        int tokensOut,
        int imageCount,
        bool cacheHit,
        CancellationToken cancellationToken)
    {
        context.AiUsage.Add(new Data.Entities.AiUsage
        {
            Id = Guid.CreateVersion7(),
            ListingId = listingId,
            TaskKey = taskKey,
            ModelId = modelId,
            TokensIn = tokensIn,
            TokensOut = tokensOut,
            ImageCount = imageCount,
            CacheHit = cacheHit,

            // CostUsd stays null: provider pricing is unverified (research R16), and a
            // fabricated figure would be worse than none. Budgets are enforced in tokens and
            // images, which are measured.
        });

        await context.SaveChangesAsync(cancellationToken);
    }
}
