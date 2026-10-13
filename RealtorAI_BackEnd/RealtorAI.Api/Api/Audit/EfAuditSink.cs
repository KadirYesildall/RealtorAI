using Api.Data;
using Api.Data.Entities;
using Modules.Audit;

namespace Api.Audit;

/// <summary>
/// T042 — the host-side adapter behind <see cref="IAuditSink"/>.
///
/// <para>
/// Adds the row and nothing else. It deliberately does not call <c>SaveChanges</c>: the audit
/// record has to commit in the same transaction as the change it describes, or the two can
/// diverge — a committed audit row for a rolled-back edit is a false record, and a committed
/// edit with a rolled-back audit row is an untraceable one. The endpoint that owns the
/// transaction saves both together.
/// </para>
/// <para>
/// <c>created_at</c> is left to the database default (<c>timezone('utc', now())</c>) rather
/// than set here, so every record is stamped by one clock. Application servers disagree with
/// each other about the time; an audit trail ordered by a mix of their clocks can show effects
/// before their causes.
/// </para>
/// </summary>
internal sealed class EfAuditSink : IAuditSink
{
    private readonly AppDbContext context;

    public EfAuditSink(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    public Task AppendAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var entry = record.Entry;

        context.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            Action = entry.Action,
            ActorId = entry.ActorId,
            ListingId = entry.ListingId,
            BrokerageId = entry.BrokerageId,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            BeforeValue = entry.BeforeValue,
            AfterValue = entry.AfterValue,
            ModelId = entry.ModelId,
            PromptVersion = entry.PromptVersion,
            PromptHash = entry.PromptHash,
            InputRef = entry.InputRef,
            OutputRef = entry.OutputRef,
            PayloadHash = record.PayloadHash,
        });

        return Task.CompletedTask;
    }
}
