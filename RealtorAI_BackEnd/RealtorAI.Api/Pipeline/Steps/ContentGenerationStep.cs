using Libs.Ai;
using Libs.Ai.Tasks;
using Libs.Reso;
using Modules.Content;

namespace Pipeline.Steps;

/// <summary>Content generation settings.</summary>
/// <param name="EstimatedTokensPerCall">The pre-dispatch budget estimate for one call (FR-055).</param>
public sealed record ContentGenerationOptions(int EstimatedTokensPerCall);

/// <summary>
/// T110/T111 — content generation: structured RESO fields, then the copy, kind by kind (FR-012,
/// FR-013, FR-014, FR-015; Story 3).
///
/// <para>
/// <b>Structured first, in an analysis run.</b> The model proposes values for the six enumerated
/// fields from what the photos showed, choosing among the lookups the MLS accepts. Values
/// outside them are dropped (scenario 1), and what remains is stored as unconfirmed AI-detected
/// facts, which the copy may then cite. A regeneration rewrites copy only and leaves the facts
/// alone.
/// </para>
/// <para>
/// <b>Then each kind, to its limit.</b> Every variant is grounded (<see cref="GroundingService"/>)
/// and measured. A variant over the limit is discarded, however it reads. When none survives,
/// the kind fails and the current drafts stay as they were (FR-015).
/// </para>
/// <para>
/// <b>Fails closed on the profile.</b> With no verified limit there is nothing to generate to,
/// and the step is skipped with that reason (research R2). The photo steps before it are
/// unaffected, and the agent can write the copy by hand.
/// </para>
/// </summary>
public sealed class ContentGenerationStep : IPipelineStep
{
    private const string NoModelReason =
        "Writing listing content isn't available right now. Write the description by hand, or generate it later.";

    private const string BudgetReason =
        "This listing's AI budget is used up, so no content was generated. Write the description by hand.";

    private readonly IContentStore store;
    private readonly IAiService ai;
    private readonly ContentGenerationOptions options;

    public ContentGenerationStep(IContentStore store, IAiService ai, ContentGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(options);

        this.store = store;
        this.ai = ai;
        this.options = options;
    }

    public PipelineStep Kind => PipelineStep.ContentGeneration;

    public async Task<StepOutcome> ExecuteAsync(PipelineRunContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        ContentSources sources;

        try
        {
            sources = await store.LoadSourcesAsync(run.ListingId, cancellationToken);
        }
        catch (FormatException)
        {
            return StepOutcome.Skipped(ContentRules.ProfileIncompleteReason);
        }

        var kinds = run.ContentKind is { } only ? [only] : ContentKinds.All;
        var writable = kinds.Where(kind => ContentRules.LimitFor(kind, sources.Profile) is not null).ToList();

        if (writable.Count == 0)
        {
            return StepOutcome.Skipped(ContentRules.ProfileIncompleteReason);
        }

        var failures = kinds.Except(writable).Select(kind => $"{kind}: the MLS has no verified limit for it").ToList();

        try
        {
            if (run.ContentKind is null)
            {
                sources = await ProposeStructuredAsync(run, sources, failures, cancellationToken);
            }

            foreach (var kind in writable)
            {
                if (await GenerateAsync(run, sources, kind, cancellationToken) is { } failure)
                {
                    failures.Add($"{kind}: {failure}");
                }
            }
        }
        catch (ModelUnavailableException)
        {
            return StepOutcome.Skipped(NoModelReason);
        }
        catch (AiBudgetExceededException)
        {
            return StepOutcome.Failed(BudgetReason);
        }

        return failures.Count == 0
            ? StepOutcome.Done
            : StepOutcome.Failed("Some content could not be generated (" + string.Join("; ", failures) + "). Write it by hand, or generate it again.");
    }

    private async Task<ContentSources> ProposeStructuredAsync(
        PipelineRunContext run,
        ContentSources sources,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        var allowed = ResoLookups.EnumeratedFields.ToDictionary(field => field, sources.Lookups.Allowed, StringComparer.Ordinal);

        GeneratedStructure proposal;

        try
        {
            proposal = await ai.ProposeStructuredFieldsAsync(Input(run, sources), allowed, options.EstimatedTokensPerCall, cancellationToken);
        }
        catch (StructuredOutputException)
        {
            failures.Add("structured fields: the model's answer was not usable, even after a retry");
            return sources;
        }

        var permitted = proposal.Values
            .Where(field => ResoLookups.IsEnumerated(field.Key))
            .Select(field => (field.Key, Values: ResoLookups.Permitted(field.Key, field.Value, sources.Lookups)))
            .Where(field => field.Values.Count > 0)
            .ToDictionary(field => field.Key, field => field.Values, StringComparer.Ordinal);

        await store.SaveStructuredAsync(run, permitted, proposal.Call, cancellationToken);

        // The copy may cite the facts just stored, so it is written from them.
        return await store.LoadSourcesAsync(run.ListingId, cancellationToken);
    }

    /// <summary>Generates one kind and stores it, or returns why nothing was stored.</summary>
    private async Task<string?> GenerateAsync(PipelineRunContext run, ContentSources sources, string kind, CancellationToken cancellationToken)
    {
        var limit = ContentRules.LimitFor(kind, sources.Profile)!.Value;
        var tone = run.Tone ?? sources.DefaultTone;

        GeneratedText text;

        try
        {
            text = await ai.GenerateListingContentAsync(
                Input(run, sources),
                kind,
                tone,
                ContentRules.VariantsFor(kind, run.VariantCount),
                limit,
                options.EstimatedTokensPerCall,
                cancellationToken);
        }
        catch (StructuredOutputException)
        {
            return "the model's answer was not usable, even after a retry";
        }

        var facts = sources.Facts.ToDictionary(
            fact => fact.FieldId,
            fact => new GroundingFact(
                fact.FieldId,
                fact.Key,
                fact.Value,
                Sourced: fact.Provenance is "agent_entered" or "public_record",
                NeedsConfirmation: fact.Provenance is "ai_detected_from_photo" or "ai_suggested" && !fact.Confirmed));

        var basicNumbers = new[] { sources.Basics.ListPrice, sources.Basics.Bedrooms, sources.Basics.Bathrooms }
            .OfType<decimal>()
            .ToList();

        var drafts = new List<GeneratedDraft>();
        var removed = 0;
        var overLimit = 0;

        foreach (var variant in text.Variants)
        {
            var grounded = GroundingService.Ground(variant.Sentences, facts, basicNumbers);
            removed += grounded.RemovedSentences;

            if (grounded.Body.Length == 0)
            {
                continue;
            }

            if (grounded.Body.Length > limit)
            {
                overLimit++;
                continue;
            }

            drafts.Add(new GeneratedDraft(drafts.Count, grounded.Body, grounded.Claims));
        }

        if (drafts.Count == 0)
        {
            var outcome = overLimit > 0 ? "every version was over the MLS's limit" : "no version survived grounding";
            await store.AuditUnusedCallAsync(run, kind, text.Call, outcome, cancellationToken);

            return outcome;
        }

        await store.SaveGenerationAsync(run, new KindGeneration(kind, tone, limit, drafts, text.Call, removed, overLimit), cancellationToken);

        return null;
    }

    private static ListingContentInput Input(PipelineRunContext run, ContentSources sources) =>
        new(run.ListingId, run.StartedBy, run.RunId, sources.Basics, sources.Facts, sources.Photos);
}
