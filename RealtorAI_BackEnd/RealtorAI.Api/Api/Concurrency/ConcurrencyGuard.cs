using System.Data;
using Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Api.Concurrency;

/// <summary>How a conditional update ended.</summary>
public enum ConcurrencyOutcome
{
    /// <summary>The expected version matched and the change was committed.</summary>
    Applied,

    /// <summary>
    /// The row has moved on since the caller read it. The endpoint answers 409 with the
    /// current state so the user can see what changed (FR-066).
    /// </summary>
    Conflict,

    /// <summary>No row with that id exists.</summary>
    NotFound,
}

/// <summary>
/// Result of a conditional update.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Entity">The entity, on <see cref="ConcurrencyOutcome.Applied"/>.</param>
/// <param name="CurrentRowVersion">
/// The version the server actually holds. Populated on both success (the new version, for the
/// response <c>ETag</c>) and conflict (the version the caller must reconcile against).
/// </param>
public sealed record ConcurrencyResult<TEntity>(
    ConcurrencyOutcome Outcome,
    TEntity? Entity,
    int? CurrentRowVersion)
    where TEntity : class, IVersionedEntity;

/// <summary>
/// T040 — applies a change to a versioned row only if the caller's expected version is still
/// current (FR-066).
/// </summary>
public interface IConcurrencyGuard
{
    Task<ConcurrencyResult<TEntity>> MutateAsync<TEntity>(
        Guid id,
        int expectedRowVersion,
        Action<TEntity> mutate,
        CancellationToken cancellationToken)
        where TEntity : class, IVersionedEntity;
}

/// <summary>
/// Read-compare-write inside a <see cref="IsolationLevel.RepeatableRead"/> transaction.
///
/// <para>
/// <b>Why this shape rather than a conditional UPDATE.</b> A single
/// <c>update ... where row_version = @expected</c> is atomic and tells you whether it matched,
/// but it does not tell you what the current version is — and FR-066 requires rejecting a
/// stale write "with the current server state", not merely rejecting it. Reading first gives
/// the state to report; <c>REPEATABLE READ</c> is what keeps the read and the write from being
/// separable by a concurrent writer.
/// </para>
/// <para>
/// Under PostgreSQL's <c>REPEATABLE READ</c>, two transactions updating the same row cannot
/// both commit: the second fails with <c>40001</c> (<i>could not serialize access due to
/// concurrent update</i>). That is the real guard, and it is handled below as a conflict rather
/// than allowed to surface as a 500 — a lost update would otherwise be possible in exactly the
/// interleaving this class exists to prevent.
/// </para>
/// <para>
/// The version is incremented here, in the same transaction as the change. Doing it in the
/// caller would make "forgot to bump the version" a silent correctness bug that no test would
/// notice until two users collided in production.
/// </para>
/// </summary>
internal sealed class ConcurrencyGuard : IConcurrencyGuard
{
    /// <summary>PostgreSQL: could not serialize access due to concurrent update.</summary>
    private const string SerializationFailure = "40001";

    private readonly AppDbContext context;

    public ConcurrencyGuard(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    public async Task<ConcurrencyResult<TEntity>> MutateAsync<TEntity>(
        Guid id,
        int expectedRowVersion,
        Action<TEntity> mutate,
        CancellationToken cancellationToken)
        where TEntity : class, IVersionedEntity
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var entity = await context.Set<TEntity>()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (entity is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConcurrencyResult<TEntity>(ConcurrencyOutcome.NotFound, null, null);
        }

        if (entity.RowVersion != expectedRowVersion)
        {
            await transaction.RollbackAsync(cancellationToken);

            return new ConcurrencyResult<TEntity>(
                ConcurrencyOutcome.Conflict,
                null,
                entity.RowVersion);
        }

        mutate(entity);

        // Restored after the mutation, so a caller that sets RowVersion itself — by copying a
        // whole DTO onto the entity, say — cannot accidentally hold the version still or move
        // it backwards.
        entity.RowVersion = expectedRowVersion + 1;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);

            // Another transaction committed a change to this row between our read and our
            // write. Functionally identical to a stale If-Match from the caller's point of
            // view, so it gets the same answer. The current version is re-read on a fresh
            // transaction because ours is now aborted and can serve no queries.
            return new ConcurrencyResult<TEntity>(
                ConcurrencyOutcome.Conflict,
                null,
                await ReadCurrentVersionAsync<TEntity>(id, cancellationToken));
        }

        return new ConcurrencyResult<TEntity>(
            ConcurrencyOutcome.Applied,
            entity,
            entity.RowVersion);
    }

    /// <summary>
    /// Re-reads the version after a serialization failure. Deliberately tolerant: if this read
    /// also fails or the row has since gone, the conflict is still reported — the caller needs
    /// to know their write was rejected far more than they need the exact current number.
    /// </summary>
    private async Task<int?> ReadCurrentVersionAsync<TEntity>(
        Guid id,
        CancellationToken cancellationToken)
        where TEntity : class, IVersionedEntity
    {
        // The failed transaction left tracked entities in the change tracker; clearing them
        // keeps a stale instance from being handed back by the identity map on this read.
        context.ChangeTracker.Clear();

        return await context.Set<TEntity>()
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => (int?)candidate.RowVersion)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
