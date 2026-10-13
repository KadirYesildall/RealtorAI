using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Libs.Ai;

/// <summary>What one listing has consumed so far.</summary>
/// <param name="TokensUsed">Prompt plus completion tokens across all calls.</param>
/// <param name="ImagesUsed">Images sent to a model.</param>
public sealed record AiConsumption(int TokensUsed, int ImagesUsed);

/// <summary>Per-listing ceilings (FR-055).</summary>
/// <param name="MaxTokens">Token ceiling for one listing.</param>
/// <param name="MaxImages">Image ceiling for one listing.</param>
public sealed record AiBudget(int MaxTokens, int MaxImages);

/// <summary>Records and reads usage. Implemented in the host.</summary>
public interface IAiUsageStore
{
    Task<AiConsumption> GetConsumptionAsync(Guid listingId, CancellationToken cancellationToken);

    /// <summary>The brokerage's configured budget, or null to use the application default.</summary>
    Task<AiBudget?> GetBudgetForListingAsync(Guid listingId, CancellationToken cancellationToken);

    Task RecordAsync(
        Guid listingId,
        string taskKey,
        string modelId,
        int tokensIn,
        int tokensOut,
        int imageCount,
        bool cacheHit,
        CancellationToken cancellationToken);
}

/// <summary>
/// Raised <b>before</b> a call is dispatched, when it would exceed the listing's budget.
/// </summary>
public sealed class AiBudgetExceededException : Exception
{
    public AiBudgetExceededException(Guid listingId, string detail)
        : base($"The AI budget for listing {listingId} would be exceeded: {detail}")
    {
        ListingId = listingId;
    }

    public AiBudgetExceededException()
        : base("The AI budget for this listing would be exceeded.")
    {
    }

    public AiBudgetExceededException(string message)
        : base(message)
    {
    }

    public AiBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Guid ListingId { get; }
}

/// <summary>
/// T049 — budget enforcement, usage recording, and response caching by input hash (FR-055).
/// </summary>
public interface ICostTracker
{
    /// <summary>
    /// Checks the budget before dispatch and throws if the call would exceed it.
    /// </summary>
    Task EnsureWithinBudgetAsync(
        Guid listingId,
        int estimatedTokens,
        int imageCount,
        CancellationToken cancellationToken);

    Task RecordAsync(
        Guid listingId,
        string taskKey,
        string modelId,
        int tokensIn,
        int tokensOut,
        int imageCount,
        bool cacheHit,
        CancellationToken cancellationToken);

    /// <summary>A cached response for this exact input, or null.</summary>
    string? TryGetCachedResponse(string inputHash);

    void CacheResponse(string inputHash, string response);

    /// <summary>
    /// Hashes everything that determines a response, so a cache hit is only ever returned for
    /// an identical request.
    /// </summary>
    string ComputeInputHash(string taskKey, string modelId, string promptHash, string input);
}

/// <summary>
/// Tracks AI spend per listing and short-circuits repeated identical calls.
///
/// <para>
/// <b>The budget check is pre-dispatch, and that is the whole point.</b> Checking afterwards
/// would record an overspend rather than prevent one — the tokens are already bought by the
/// time a response arrives. So the estimate is required up front, and the check is conservative:
/// it refuses a call whose estimate would cross the ceiling rather than one that already has.
/// </para>
/// <para>
/// <b>Budgets are in tokens and images, not dollars.</b> Provider pricing has not been verified
/// (research R16) and <c>ai_usage.cost_usd</c> is deliberately nullable. A budget denominated in
/// a currency figure we had guessed would enforce a limit nobody could reason about; tokens and
/// images are measured directly from the response.
/// </para>
/// <para>
/// <b>Why cache at all.</b> Re-running analysis on an unchanged photo, or regenerating identical
/// copy from unchanged fields, is the most common way a listing burns budget for no new
/// information — an agent clicking regenerate, or a retried background job. The hash covers the
/// task, the model and the prompt hash as well as the input, so changing the model or editing
/// the prompt correctly misses the cache instead of serving a result the new configuration never
/// produced.
/// </para>
/// </summary>
public sealed class CostTracker : ICostTracker
{
    /// <summary>
    /// Long enough to absorb a retry storm or an impatient agent, short enough that a cached
    /// response cannot outlive the data it was derived from by much. Responses are also keyed
    /// by input, so a changed photo or field misses regardless.
    /// </summary>
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private const string CacheKeyPrefix = "ai:response:";

    private readonly IAiUsageStore store;
    private readonly IMemoryCache cache;
    private readonly AiBudget defaultBudget;

    public CostTracker(IAiUsageStore store, IMemoryCache cache, AiBudget defaultBudget)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(defaultBudget);

        if (defaultBudget.MaxTokens <= 0 || defaultBudget.MaxImages <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultBudget),
                defaultBudget,
                "A zero or negative default budget would refuse every AI call. If the intent "
                + "is to disable AI, disable the registry entries instead — that is the "
                + "supported switch and it is auditable.");
        }

        this.store = store;
        this.cache = cache;
        this.defaultBudget = defaultBudget;
    }

    public async Task EnsureWithinBudgetAsync(
        Guid listingId,
        int estimatedTokens,
        int imageCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(imageCount);

        var budget = await store.GetBudgetForListingAsync(listingId, cancellationToken)
            ?? defaultBudget;

        var consumed = await store.GetConsumptionAsync(listingId, cancellationToken);

        if (consumed.TokensUsed + estimatedTokens > budget.MaxTokens)
        {
            throw new AiBudgetExceededException(
                listingId,
                $"{consumed.TokensUsed} of {budget.MaxTokens} tokens already used, and this "
                + $"call is estimated at {estimatedTokens} more. The listing remains "
                + "completable by hand.");
        }

        if (consumed.ImagesUsed + imageCount > budget.MaxImages)
        {
            throw new AiBudgetExceededException(
                listingId,
                $"{consumed.ImagesUsed} of {budget.MaxImages} images already analysed, and this "
                + $"call would add {imageCount}.");
        }
    }

    public Task RecordAsync(
        Guid listingId,
        string taskKey,
        string modelId,
        int tokensIn,
        int tokensOut,
        int imageCount,
        bool cacheHit,
        CancellationToken cancellationToken) =>
        store.RecordAsync(
            listingId,
            taskKey,
            modelId,
            tokensIn,
            tokensOut,
            imageCount,
            cacheHit,
            cancellationToken);

    public string? TryGetCachedResponse(string inputHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputHash);

        return cache.TryGetValue(CacheKeyPrefix + inputHash, out string? response)
            ? response
            : null;
    }

    public void CacheResponse(string inputHash, string response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputHash);
        ArgumentNullException.ThrowIfNull(response);

        cache.Set(CacheKeyPrefix + inputHash, response, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,

            // Sized by payload length so a few large responses cannot evict everything else,
            // and so the cache's memory use is bounded by its configured size limit.
            Size = Math.Max(1, response.Length / 1024),
        });
    }

    public string ComputeInputHash(string taskKey, string modelId, string promptHash, string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Length-prefixed for the same reason the audit payload hash is: without it, moving a
        // character between adjacent components would leave the digest unchanged and two
        // different requests would share a cache entry.
        var canonical = new StringBuilder();

        foreach (var part in new[] { taskKey, modelId, promptHash, input })
        {
            canonical
                .Append(Encoding.UTF8.GetByteCount(part ?? string.Empty)
                    .ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(part)
                .Append(';');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
