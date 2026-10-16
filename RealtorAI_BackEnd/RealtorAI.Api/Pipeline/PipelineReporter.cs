namespace Pipeline;

/// <summary>
/// Records a state change and then announces it, in that order (FR-053).
///
/// <para>
/// The persisted state is the record — <c>GET /pipeline</c> reads it — and the notification is a
/// hint to re-read. Recording first means a client that refreshes on the hint always finds the
/// state it was told about; announcing first could send it to read a state not yet written.
/// </para>
/// </summary>
public sealed class PipelineReporter
{
    private readonly IPipelineStore store;
    private readonly IPipelineProgressNotifier notifier;

    public PipelineReporter(IPipelineStore store, IPipelineProgressNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(notifier);

        this.store = store;
        this.notifier = notifier;
    }

    public async Task StepAsync(PipelineRunContext run, PipelineStep step, StepState state, string? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await store.SetStepStateAsync(run.RunId, step, state, reason, cancellationToken);
        await notifier.StepChangedAsync(new StepProgress(run.ListingId, step, state, reason), cancellationToken);
    }

    public async Task PhotoAsync(PipelineRunContext run, Guid photoId, PhotoState state, string? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await store.SetPhotoStateAsync(run.RunId, photoId, state, reason, cancellationToken);
        await notifier.PhotoChangedAsync(new PhotoProgress(run.ListingId, photoId, state, reason), cancellationToken);
    }
}
