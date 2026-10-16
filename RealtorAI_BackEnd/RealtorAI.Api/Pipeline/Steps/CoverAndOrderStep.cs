namespace Pipeline.Steps;

/// <summary>
/// T097 — a suggested cover and a recommended order, each with a stated reason (FR-010; Story 2
/// scenario 6).
///
/// <para>
/// <b>Deterministic, from the classifications.</b> The order is a walk through the house — the
/// front of the property first, then the living spaces, the kitchen, bedrooms, bathrooms, and
/// the outside — read from each photo's room type: the agent's where they overrode it (FR-011),
/// the model's otherwise. A rule the agent can predict is easier to accept or overrule than a
/// second model opinion, and it costs nothing.
/// </para>
/// <para>
/// <b>Suggestions only.</b> Nothing is reordered and no cover is set; the agent applies them or
/// not (FR-011). Each suggestion carries a reason code, <c>rule:…</c>, which the review screen
/// states in the agent's language.
/// </para>
/// </summary>
public sealed class CoverAndOrderStep : IPipelineStep
{
    /// <summary>The walk-through: front first, the way a buyer would arrive.</summary>
    public static IReadOnlyList<string> WalkThrough { get; } =
    [
        "exterior_front",
        "living_room",
        "kitchen",
        "primary_bedroom",
        "bedroom",
        "bathroom",
        "backyard",
        "pool",
        "view",
        "garage",
        "floor_plan",
        "other",
    ];

    /// <summary>Flags that make a photo a poor first impression, whatever its room.</summary>
    private static readonly HashSet<string> DisqualifyingQuality = new(StringComparer.Ordinal)
    {
        QualityFlag.Blurry,
        QualityFlag.TooDark,
        QualityFlag.Overexposed,
        QualityFlag.BelowMinResolution,
        QualityFlag.NearDuplicate,
    };

    private readonly IPipelineStore store;

    public CoverAndOrderStep(IPipelineStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public PipelineStep Kind => PipelineStep.CoverAndOrder;

    public async Task<StepOutcome> ExecuteAsync(PipelineRunContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var photos = await store.ClassifiedPhotosAsync(run.ListingId, cancellationToken);

        if (photos.Count == 0)
        {
            return StepOutcome.Skipped("No photo is classified yet, so there is nothing to order. Order the photos by hand.");
        }

        await store.SaveSuggestionsAsync(Suggest(photos), cancellationToken);

        return StepOutcome.Done;
    }

    /// <summary>The suggestions for a set of classified photos. Pure, so every rule is a unit test.</summary>
    public static IReadOnlyList<OrderSuggestion> Suggest(IReadOnlyList<ClassifiedPhoto> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);

        var ordered = photos
            .OrderBy(photo => Rank(photo.RoomType))
            .ThenBy(photo => photo.OrderIndex)
            .ToList();

        var cover = ChooseCover(ordered);

        return ordered
            .Select((photo, position) => new OrderSuggestion(
                photo.PhotoId,
                photo.PhotoId == cover?.Photo.PhotoId,
                position,
                photo.PhotoId == cover?.Photo.PhotoId ? cover.Value.Reason : $"rule:order:{photo.RoomType}"))
            .ToList();
    }

    private static int Rank(string roomType)
    {
        var index = WalkThrough.ToList().IndexOf(roomType);

        return index < 0 ? WalkThrough.Count : index;
    }

    private static bool Clean(ClassifiedPhoto photo) =>
        !photo.QualityFlags.Any(DisqualifyingQuality.Contains) && photo.ContentFlags.Count == 0;

    /// <summary>
    /// The front of the property, if a clean photo of it exists — the model's recommendation and
    /// confidence break ties. Otherwise a clean photo the model recommended, otherwise the first
    /// clean photo of the walk-through. Null when every photo has an issue to resolve first.
    /// </summary>
    private static (ClassifiedPhoto Photo, string Reason)? ChooseCover(IReadOnlyList<ClassifiedPhoto> ordered)
    {
        var clean = ordered.Where(Clean).ToList();

        var front = clean
            .Where(photo => photo.RoomType == "exterior_front")
            .OrderByDescending(photo => photo.ModelRecommendsCover)
            .ThenByDescending(photo => photo.Confidence ?? 0)
            .FirstOrDefault();

        if (front is not null)
        {
            return (front, "rule:cover:exterior_front");
        }

        if (clean.FirstOrDefault(photo => photo.ModelRecommendsCover) is { } recommended)
        {
            return (recommended, "rule:cover:model_recommended");
        }

        return clean.Count > 0 ? (clean[0], "rule:cover:first_clean") : null;
    }
}
