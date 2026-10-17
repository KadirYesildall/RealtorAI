using Libs.Ai;
using Libs.Ai.Tasks;
using Libs.Reso;
using Modules.Content;

namespace Pipeline;

/// <summary>What content generation draws on for one listing, read when the step starts.</summary>
/// <param name="Profile">The listing's MLS rule profile, or null when it has no target MLS.</param>
/// <param name="Lookups">The profile's changes to the standard RESO lookups.</param>
/// <param name="DefaultTone">The brokerage's default tone, used when the run names none.</param>
public sealed record ContentSources(
    ContentBasics Basics,
    IReadOnlyList<ContentFact> Facts,
    IReadOnlyList<PhotoObservation> Photos,
    ContentProfile? Profile,
    ResoLookupMapping Lookups,
    string DefaultTone);

/// <summary>One grounded, within-limit variant ready to store.</summary>
public sealed record GeneratedDraft(int VariantIndex, string Body, IReadOnlyList<Claim> Claims);

/// <summary>Everything one kind's generation produced, for storing and auditing together.</summary>
/// <param name="RemovedSentences">Sentences grounding removed (T106), counted in the audit.</param>
/// <param name="OverLimitVariants">Variants discarded for exceeding the limit (FR-013).</param>
public sealed record KindGeneration(
    string Kind,
    string Tone,
    int CharacterLimit,
    IReadOnlyList<GeneratedDraft> Drafts,
    AiResult Call,
    int RemovedSentences,
    int OverLimitVariants);

/// <summary>
/// The persistence content generation needs, implemented by the host where the EF model lives
/// (research R20).
/// </summary>
public interface IContentStore
{
    /// <exception cref="FormatException">The MLS profile's lookup mapping does not parse; the profile is broken.</exception>
    Task<ContentSources> LoadSourcesAsync(Guid listingId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the permitted structured values as unconfirmed <c>ai_detected_from_photo</c>
    /// fields, never over a value the agent entered, a public record, or one the agent confirmed,
    /// and audits the call and each field written.
    /// </summary>
    Task SaveStructuredAsync(
        PipelineRunContext run,
        IReadOnlyDictionary<string, IReadOnlyList<string>> values,
        AiResult aiCall,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores the drafts as the kind's next version and supersedes the current one: its drafts go
    /// stale, an approval of any of them is revoked, and an approved listing returns to review.
    /// Audits the call and the generation, in one transaction.
    /// </summary>
    Task SaveGenerationAsync(PipelineRunContext run, KindGeneration generation, CancellationToken cancellationToken);

    /// <summary>Audits a call whose answer left nothing to store. Every call is on the record (VIII.5).</summary>
    Task AuditUnusedCallAsync(PipelineRunContext run, string kind, AiResult aiCall, string outcome, CancellationToken cancellationToken);
}
