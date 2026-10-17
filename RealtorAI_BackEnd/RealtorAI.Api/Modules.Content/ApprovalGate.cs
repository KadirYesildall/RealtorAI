namespace Modules.Content;

/// <summary>The contract's <c>ApprovalBlocked.blockers[].code</c> values.</summary>
public static class BlockerCodes
{
    public const string BlockingFindingOpen = "blocking_finding_open";
    public const string UnconfirmedAiFact = "unconfirmed_ai_fact";
    public const string RequiredFieldMissing = "required_field_missing";
    public const string MembershipNotApproved = "membership_not_approved";
    public const string RuleProfileIncomplete = "rule_profile_incomplete";
    public const string ContentChangedSinceReview = "content_changed_since_review";
    public const string ComplianceNotRun = "compliance_not_run";
    public const string ContentNotApproved = "content_not_approved";
    public const string AnalysisInProgress = "analysis_in_progress";
}

/// <summary>One reason an output cannot be approved.</summary>
/// <param name="Detail">English, for logs and API clients; the UI states the code in the agent's language.</param>
/// <param name="TargetId">What the agent must act on: a finding, a field or a draft; null when it is the output itself.</param>
public sealed record Blocker(string Code, string Detail, Guid? TargetId = null);

/// <summary>An open blocking finding, as the gate reads it.</summary>
public sealed record GateFinding(Guid Id, string TargetType, Guid TargetId, string RuleId, string Reason);

/// <summary>A draft offered for approval: the selected version of its kind.</summary>
/// <param name="Hash">SHA-256 of its body now.</param>
/// <param name="CheckedHash">The hash of the body the last compliance check read; null if never checked.</param>
/// <param name="UnconfirmedFacts">The fields its claims rest on that still need confirming (FR-014).</param>
public sealed record DraftSubject(
    Guid Id,
    string Kind,
    string Hash,
    string? CheckedHash,
    bool Approved,
    IReadOnlyList<(Guid FieldId, string Key)> UnconfirmedFacts);

/// <summary>The listing offered for approval: its facts and photos, and the content it publishes.</summary>
/// <param name="Hash">The review hash of its facts and photos now.</param>
/// <param name="UnconfirmedFields">AI-derived fields nobody has confirmed (FR-006).</param>
public sealed record ListingSubject(
    Guid Id,
    string Hash,
    string? CheckedHash,
    IReadOnlyList<DraftSubject> SelectedDrafts,
    IReadOnlyList<(Guid FieldId, string Key)> UnconfirmedFields,
    IReadOnlyList<string> MissingRequiredKeys);

/// <summary>
/// T147 — what stands between an output and its approval (FR-014, FR-022, FR-026, SC-011).
///
/// <para>
/// <b>A draft</b> needs a compliance check of its exact words, no open blocking finding on it,
/// no claim resting on an unconfirmed AI fact, and a verified MLS profile.
/// </para>
/// <para>
/// <b>The listing</b> — its facts and photos, and the copy it publishes — needs all of that for
/// everything it carries: a check of its facts and photos as they are, no open blocking finding
/// anywhere on it, every required field present and every AI fact confirmed, and each selected
/// draft approved, a description among them.
/// </para>
/// <para>
/// Pure, so every rule is a table of cases. Whether the content changed since the agent looked is
/// the endpoint's to decide, from the hash the screen sent.
/// </para>
/// </summary>
public static class ApprovalGate
{
    public const string ProfileIncompleteRule = "mls.profile_incomplete";

    public static IReadOnlyList<Blocker> ForDraft(DraftSubject draft, IReadOnlyList<GateFinding> openBlocking, bool runInProgress)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(openBlocking);

        var blockers = Common(runInProgress, openBlocking);

        if (draft.CheckedHash != draft.Hash)
        {
            blockers.Add(new Blocker(BlockerCodes.ComplianceNotRun, "These words have not been through a compliance check yet.", draft.Id));
        }

        blockers.AddRange(openBlocking
            .Where(finding => finding.TargetId == draft.Id && finding.RuleId != ProfileIncompleteRule)
            .Select(finding => new Blocker(BlockerCodes.BlockingFindingOpen, finding.Reason, finding.Id)));

        blockers.AddRange(draft.UnconfirmedFacts.Select(fact =>
            new Blocker(BlockerCodes.UnconfirmedAiFact, $"A sentence rests on {fact.Key}, which has not been confirmed.", fact.FieldId)));

        return blockers;
    }

    public static IReadOnlyList<Blocker> ForListing(ListingSubject listing, IReadOnlyList<GateFinding> openBlocking, bool runInProgress)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(openBlocking);

        var blockers = Common(runInProgress, openBlocking);

        if (listing.CheckedHash != listing.Hash)
        {
            blockers.Add(new Blocker(BlockerCodes.ComplianceNotRun, "The listing's facts and photos have changed since the last compliance check, or have never been checked.", listing.Id));
        }

        var draftIds = listing.SelectedDrafts.Select(draft => draft.Id).ToHashSet();

        blockers.AddRange(openBlocking
            .Where(finding => finding.RuleId != ProfileIncompleteRule
                && (finding.TargetType is "listing" or "photo" || draftIds.Contains(finding.TargetId)))
            .Select(finding => new Blocker(BlockerCodes.BlockingFindingOpen, finding.Reason, finding.Id)));

        blockers.AddRange(listing.MissingRequiredKeys.Select(key =>
            new Blocker(BlockerCodes.RequiredFieldMissing, $"{key} is required before the listing can be approved.")));

        blockers.AddRange(listing.UnconfirmedFields.Select(field =>
            new Blocker(BlockerCodes.UnconfirmedAiFact, $"{field.Key} was detected by AI and has not been confirmed.", field.FieldId)));

        if (!listing.SelectedDrafts.Any(draft => draft.Kind == ContentKinds.PublicRemarks))
        {
            blockers.Add(new Blocker(BlockerCodes.ContentNotApproved, "The listing has no description chosen for publication."));
        }

        blockers.AddRange(listing.SelectedDrafts
            .Where(draft => !draft.Approved)
            .Select(draft => new Blocker(BlockerCodes.ContentNotApproved, $"The {draft.Kind.Replace('_', ' ')} chosen for publication is not approved.", draft.Id)));

        return blockers;
    }

    private static List<Blocker> Common(bool runInProgress, IReadOnlyList<GateFinding> openBlocking)
    {
        var blockers = new List<Blocker>();

        if (runInProgress)
        {
            blockers.Add(new Blocker(BlockerCodes.AnalysisInProgress, "The listing's pipeline is running. Approve once it has finished."));
        }

        if (openBlocking.FirstOrDefault(finding => finding.RuleId == ProfileIncompleteRule) is { } incomplete)
        {
            blockers.Add(new Blocker(BlockerCodes.RuleProfileIncomplete, incomplete.Reason, incomplete.Id));
        }

        return blockers;
    }
}
