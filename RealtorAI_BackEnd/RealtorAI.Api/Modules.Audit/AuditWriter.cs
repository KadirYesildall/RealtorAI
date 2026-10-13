using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Audit;

/// <summary>
/// Persists audit records. Implemented in the host, where the EF model lives.
///
/// <para>
/// Insert-only by contract. There is no update or delete method, so there is no API through
/// which an audit record can be altered — the database enforces the same thing with an
/// INSERT-only grant and a statement-level trigger, but a sink that simply cannot express a
/// mutation means no caller has to remember not to.
/// </para>
/// </summary>
public interface IAuditSink
{
    Task AppendAsync(AuditRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// An <see cref="AuditEntry"/> after the writer has validated it and computed its payload hash.
/// This, not the entry, is what reaches storage.
/// </summary>
/// <param name="Entry">The validated event.</param>
/// <param name="PayloadHash">Lower-case hex SHA-256 over the event's canonical form.</param>
public sealed record AuditRecord(AuditEntry Entry, string PayloadHash);

/// <summary>Writes audit records (FR-049).</summary>
public interface IAuditWriter
{
    /// <summary>
    /// Validates, hashes and appends one event.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The entry is missing evidence its action requires. Thrown rather than silently storing a
    /// partial record: an audit trail with unattributed or unidentifiable events is worse than
    /// a failed request, because the gap is invisible afterwards.
    /// </exception>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// T042 — the append-only audit writer.
///
/// <para>
/// Two jobs: refuse events that would be useless as evidence, and compute a payload hash that
/// ties each record to the content it describes.
/// </para>
///
/// <para>
/// <b>What the hash is for.</b> The rows are already immutable — the database refuses UPDATE,
/// DELETE and TRUNCATE on them. The hash addresses a different question: whether a record still
/// corresponds to the thing it claims to describe. Recomputing it over the stored fields shows
/// the row has not been corrupted in transit or by a restore, and a hash over the before/after
/// values lets two records be compared without reading their full payloads.
/// </para>
/// <para>
/// <b>Why the canonical form is built by hand</b> rather than by serialising the entry. A hash
/// is only useful if the same event always produces the same digest, and JSON serialisation is
/// not stable enough to rely on for that: property order, naming policy and number formatting
/// can all change under a library upgrade or a configuration tweak, and every past hash would
/// then fail to reproduce. An explicit field-by-field encoding with a length prefix on each
/// value is verbose, and it is the thing that keeps hashes computed years apart comparable.
/// </para>
/// </summary>
public sealed class AuditWriter : IAuditWriter
{
    private readonly IAuditSink sink;

    public AuditWriter(IAuditSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        this.sink = sink;
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Validate(entry);

        await sink.AppendAsync(
            new AuditRecord(entry, ComputePayloadHash(entry)),
            cancellationToken);
    }

    /// <summary>
    /// Rejects events that could not serve as evidence later.
    /// </summary>
    private static void Validate(AuditEntry entry)
    {
        // Attribution. Everything except the out-of-band purge has a human or a service
        // identity behind it; an event with no actor cannot answer "who did this".
        if (entry.ActorId is null && entry.Action != AuditAction.OriginalsPurged)
        {
            throw new ArgumentException(
                $"An audit event for {entry.Action} must record an actor. Only "
                + $"{nameof(AuditAction.OriginalsPurged)} is system-initiated and may omit one.",
                nameof(entry));
        }

        if (entry.ActorId == Guid.Empty)
        {
            throw new ArgumentException(
                "Guid.Empty is not an actor. This usually means an agent id was defaulted "
                + "rather than resolved from the request.",
                nameof(entry));
        }

        // Subject. An event about a target must say which one, or it records that something
        // happened to something.
        if (entry.TargetType is not null && entry.TargetId is null)
        {
            throw new ArgumentException(
                $"An audit event with a target type ('{entry.TargetType}') must also record a "
                + "target id.",
                nameof(entry));
        }

        if (entry.TargetId is not null && entry.TargetType is null)
        {
            throw new ArgumentException(
                "An audit event with a target id must also record its target type, or the id "
                + "cannot be resolved to a row later.",
                nameof(entry));
        }

        // Model provenance. Principle VIII.5: an AI call is only traceable if the model and
        // the exact prompt version that produced it are both named.
        if (entry.Action == AuditAction.AiCall)
        {
            if (string.IsNullOrWhiteSpace(entry.ModelId))
            {
                throw new ArgumentException(
                    "An ai_call audit event must record the model id that served it "
                    + "(Principle VIII.5).",
                    nameof(entry));
            }

            if (string.IsNullOrWhiteSpace(entry.PromptVersion)
                || string.IsNullOrWhiteSpace(entry.PromptHash))
            {
                throw new ArgumentException(
                    "An ai_call audit event must record both the prompt version and its hash. "
                    + "The version alone is not enough: an edited prompt would leave past "
                    + "records citing a version that no longer means what it did "
                    + "(Principle VIII.5).",
                    nameof(entry));
            }
        }
    }

    /// <summary>
    /// Canonical SHA-256 over the event's fields.
    ///
    /// <para>
    /// Each field is written as its name, then the UTF-8 byte length of its value, then the
    /// value. The length prefix is what makes the encoding unambiguous: without it, an event
    /// with <c>TargetType = "ab"</c> and <c>ModelId = "c"</c> would hash identically to one
    /// with <c>"a"</c> and <c>"bc"</c>, and two different events sharing a digest defeats the
    /// purpose of having one.
    /// </para>
    /// </summary>
    internal static string ComputePayloadHash(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var canonical = new StringBuilder();

        Append(canonical, "action", entry.Action.ToString());
        Append(canonical, "actor", entry.ActorId?.ToString());
        Append(canonical, "listing", entry.ListingId?.ToString());
        Append(canonical, "brokerage", entry.BrokerageId?.ToString());
        Append(canonical, "targetType", entry.TargetType);
        Append(canonical, "targetId", entry.TargetId?.ToString());
        Append(canonical, "before", entry.BeforeValue);
        Append(canonical, "after", entry.AfterValue);
        Append(canonical, "modelId", entry.ModelId);
        Append(canonical, "promptVersion", entry.PromptVersion);
        Append(canonical, "promptHash", entry.PromptHash);
        Append(canonical, "inputRef", entry.InputRef);
        Append(canonical, "outputRef", entry.OutputRef);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));

        return Convert.ToHexStringLower(digest);
    }

    private static void Append(StringBuilder builder, string field, string? value)
    {
        builder.Append(field).Append('=');

        if (value is null)
        {
            // Distinguished from the empty string on purpose: "this field was not recorded"
            // and "this field was recorded as empty" are different claims about an event.
            builder.Append("null;");
            return;
        }

        builder
            .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append(';');
    }
}
