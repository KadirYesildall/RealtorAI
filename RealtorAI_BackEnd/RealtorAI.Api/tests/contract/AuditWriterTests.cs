using Modules.Audit;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T043 — the audit writer's contract (FR-049, Principle VIII.5).
///
/// <para>
/// Two things are under test. First, that the writer refuses events that would be useless as
/// evidence: unattributed actions, half-identified targets, and AI calls that do not name the
/// model and prompt that produced them. Second, that the payload hash is deterministic and
/// unambiguous — a hash that changed between runs would make every stored one unverifiable, and
/// one that collided between different events would make it meaningless.
/// </para>
/// </summary>
public sealed class AuditWriterTests
{
    private static readonly Guid Actor = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid Listing = Guid.Parse("99990000-0000-4000-8000-000000000001");

    /// <summary>Captures what reached the sink, so the test can assert on it.</summary>
    private sealed class RecordingSink : IAuditSink
    {
        internal List<AuditRecord> Records { get; } = [];

        public Task AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private static (AuditWriter Writer, RecordingSink Sink) Build()
    {
        var sink = new RecordingSink();
        return (new AuditWriter(sink), sink);
    }

    private static AuditEntry ValidEntry(AuditAction action = AuditAction.FieldEdit) => new()
    {
        Action = action,
        ActorId = Actor,
        ListingId = Listing,
        TargetType = "listing_field",
        TargetId = Guid.Parse("33330000-0000-4000-8000-000000000003"),
        BeforeValue = "{\"value\":1850}",
        AfterValue = "{\"value\":1900}",
    };

    // ───────────────────────────── Happy path ─────────────────────────────

    [Fact]
    public async Task WriteAsync_AppendsOneRecordWithAHash()
    {
        var (writer, sink) = Build();

        await writer.WriteAsync(ValidEntry(), default);

        var record = Assert.Single(sink.Records);
        Assert.Equal(AuditAction.FieldEdit, record.Entry.Action);

        // SHA-256 as lower-case hex.
        Assert.Equal(64, record.PayloadHash.Length);
        Assert.Matches("^[0-9a-f]{64}$", record.PayloadHash);
    }

    // ───────────────────────────── Attribution ─────────────────────────────

    [Fact]
    public async Task WriteAsync_RejectsAnEventWithNoActor()
    {
        var (writer, sink) = Build();

        var entry = ValidEntry() with { ActorId = null };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(entry, default));
        Assert.Empty(sink.Records);
    }

    /// <summary>
    /// <c>Guid.Empty</c> is the value you get from a defaulted struct, so it is the shape an
    /// unresolved agent id actually takes. Accepting it would store an event attributed to
    /// nobody while looking attributed.
    /// </summary>
    [Fact]
    public async Task WriteAsync_RejectsAnEmptyGuidActor()
    {
        var (writer, sink) = Build();

        var entry = ValidEntry() with { ActorId = Guid.Empty };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(entry, default));
        Assert.Empty(sink.Records);
    }

    /// <summary>
    /// The one system-initiated action. The retention purge runs out of band with its own
    /// credential and has no user behind it, so it is the single permitted exception (FR-065).
    /// </summary>
    [Fact]
    public async Task WriteAsync_AllowsOriginalsPurgedWithNoActor()
    {
        var (writer, sink) = Build();

        var entry = new AuditEntry
        {
            Action = AuditAction.OriginalsPurged,
            ActorId = null,
            ListingId = Listing,
        };

        await writer.WriteAsync(entry, default);

        Assert.Single(sink.Records);
    }

    // ───────────────────────────── Target identification ─────────────────────────────

    [Fact]
    public async Task WriteAsync_RejectsATargetTypeWithNoId()
    {
        var (writer, _) = Build();

        var entry = ValidEntry() with { TargetId = null };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(entry, default));
    }

    [Fact]
    public async Task WriteAsync_RejectsATargetIdWithNoType()
    {
        var (writer, _) = Build();

        var entry = ValidEntry() with { TargetType = null };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(entry, default));
    }

    // ───────────────────────────── Model provenance (VIII.5) ─────────────────────────────

    [Fact]
    public async Task WriteAsync_RejectsAnAiCallWithNoModelId()
    {
        var (writer, _) = Build();

        var entry = new AuditEntry
        {
            Action = AuditAction.AiCall,
            ActorId = Actor,
            ListingId = Listing,
            PromptVersion = "v1",
            PromptHash = "abc",
        };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(entry, default));
    }

    /// <summary>
    /// The prompt hash is required alongside the version, not instead of it. A version number
    /// alone is a reference to mutable text: edit the prompt and every past record citing "v1"
    /// now points at something else (Principle VIII.5).
    /// </summary>
    [Theory]
    [InlineData(null, "hash")]
    [InlineData("v1", null)]
    [InlineData(null, null)]
    public async Task WriteAsync_RejectsAnAiCallMissingPromptProvenance(
        string? promptVersion,
        string? promptHash)
    {
        var (writer, _) = Build();

        var entry = new AuditEntry
        {
            Action = AuditAction.AiCall,
            ActorId = Actor,
            ListingId = Listing,
            ModelId = "some-provider/some-model",
            PromptVersion = promptVersion,
            PromptHash = promptHash,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(entry, default));
    }

    [Fact]
    public async Task WriteAsync_AcceptsAFullyProvenancedAiCall()
    {
        var (writer, sink) = Build();

        var entry = new AuditEntry
        {
            Action = AuditAction.AiCall,
            ActorId = Actor,
            ListingId = Listing,
            ModelId = "some-provider/some-model",
            PromptVersion = "v3",
            PromptHash = "9f2c",
            InputRef = "ai-io/in/abc",
            OutputRef = "ai-io/out/abc",
        };

        await writer.WriteAsync(entry, default);

        Assert.Single(sink.Records);
    }

    // ───────────────────────────── Payload hash ─────────────────────────────

    [Fact]
    public void PayloadHash_IsDeterministic()
    {
        var entry = ValidEntry();

        Assert.Equal(
            AuditWriter.ComputePayloadHash(entry),
            AuditWriter.ComputePayloadHash(entry));
    }

    [Fact]
    public void PayloadHash_ChangesWhenAnyFieldChanges()
    {
        var baseline = AuditWriter.ComputePayloadHash(ValidEntry());

        var variants = new[]
        {
            ValidEntry() with { Action = AuditAction.FieldConfirm },
            ValidEntry() with { ActorId = Guid.NewGuid() },
            ValidEntry() with { ListingId = Guid.NewGuid() },
            ValidEntry() with { BrokerageId = Guid.NewGuid() },
            ValidEntry() with { TargetType = "photo" },
            ValidEntry() with { TargetId = Guid.NewGuid() },
            ValidEntry() with { BeforeValue = "{\"value\":1851}" },
            ValidEntry() with { AfterValue = "{\"value\":1901}" },
            ValidEntry() with { ModelId = "m" },
            ValidEntry() with { PromptVersion = "v9" },
            ValidEntry() with { PromptHash = "ff" },
            ValidEntry() with { InputRef = "in" },
            ValidEntry() with { OutputRef = "out" },
        };

        foreach (var variant in variants)
        {
            Assert.NotEqual(baseline, AuditWriter.ComputePayloadHash(variant));
        }
    }

    /// <summary>
    /// The reason each value is length-prefixed in the canonical form. Without it, shifting a
    /// character from one adjacent field to the next would leave the concatenation — and so the
    /// digest — unchanged, and two genuinely different events would share a hash.
    /// </summary>
    [Fact]
    public void PayloadHash_DistinguishesValuesThatConcatenateIdentically()
    {
        var first = new AuditEntry
        {
            Action = AuditAction.FieldEdit,
            ActorId = Actor,
            ModelId = "ab",
            PromptVersion = "c",
            PromptHash = "x",
        };

        var second = first with { ModelId = "a", PromptVersion = "bc" };

        Assert.NotEqual(
            AuditWriter.ComputePayloadHash(first),
            AuditWriter.ComputePayloadHash(second));
    }

    /// <summary>
    /// "Not recorded" and "recorded as empty" are different claims about an event, so they must
    /// not hash alike.
    /// </summary>
    [Fact]
    public void PayloadHash_DistinguishesNullFromEmptyString()
    {
        var withNull = ValidEntry() with { TargetType = null, TargetId = null };
        var withEmpty = ValidEntry() with { TargetType = string.Empty, TargetId = null };

        Assert.NotEqual(
            AuditWriter.ComputePayloadHash(withNull),
            AuditWriter.ComputePayloadHash(withEmpty));
    }

    [Fact]
    public void Constructor_RequiresASink()
    {
        Assert.Throws<ArgumentNullException>(() => new AuditWriter(null!));
    }
}
