namespace Modules.Media;

/// <summary>Two photos of one listing whose embeddings are close.</summary>
public sealed record SimilarPair(Guid First, Guid Second, double Similarity);

/// <summary>
/// T098 — which photos to flag as near-duplicates (FR-009; spec Edge Cases).
///
/// <para>
/// Pairs come from a pgvector cosine query over one listing's embeddings, at the configured
/// threshold. Of each pair, the photo later in the listing's order is flagged — the earlier one
/// is the agent's first choice of that view, so it is the one to keep by default. A flag is all
/// this produces: nothing is hidden or removed, and the agent decides (FR-011). Exact duplicates
/// never arrive here; upload refuses identical bytes.
/// </para>
/// </summary>
public static class NearDuplicateDetector
{
    /// <param name="orderIndex">Each photo's position in the listing.</param>
    public static IReadOnlySet<Guid> PhotosToFlag(
        IEnumerable<SimilarPair> pairs,
        IReadOnlyDictionary<Guid, int> orderIndex,
        double minimumSimilarity)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(orderIndex);

        var flagged = new HashSet<Guid>();

        foreach (var pair in pairs)
        {
            if (pair.First == pair.Second
                || pair.Similarity < minimumSimilarity
                || !orderIndex.TryGetValue(pair.First, out var first)
                || !orderIndex.TryGetValue(pair.Second, out var second))
            {
                continue;
            }

            // Ties in position cannot happen (order_index is unique per listing); the id breaks
            // one anyway, so the outcome never depends on the order the pairs arrive in.
            flagged.Add(first > second || (first == second && pair.First.CompareTo(pair.Second) > 0) ? pair.First : pair.Second);
        }

        return flagged;
    }
}
