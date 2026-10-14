using Api.Concurrency;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace RealtorAI.Tests.Unit;

/// <summary>
/// The shapes a PostgreSQL serialization failure actually arrives in (research R31). The guard
/// first matched only the bare <see cref="PostgresException"/>, which never reaches it: EF and
/// Npgsql's default execution strategy each add a layer, so a genuine race became a 500.
/// </summary>
public sealed class SerializationFailureDetectionTests
{
    private static PostgresException Postgres(string sqlState) =>
        new("could not serialize access due to concurrent update", "ERROR", "ERROR", sqlState);

    [Fact]
    public void TheShapeEfAndNpgsqlActuallyThrow_IsRecognised()
    {
        var thrown = new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.",
            new DbUpdateException("An error occurred while saving the entity changes.", Postgres("40001")));

        Assert.True(ConcurrencyGuard.IsSerializationFailure(thrown));
    }

    [Fact]
    public void ADbUpdateExceptionWrappingIt_IsRecognised()
    {
        Assert.True(ConcurrencyGuard.IsSerializationFailure(new DbUpdateException("save failed", Postgres("40001"))));
    }

    [Fact]
    public void TheBarePostgresException_IsRecognised()
    {
        Assert.True(ConcurrencyGuard.IsSerializationFailure(Postgres("40001")));
    }

    /// <summary>
    /// Only a serialization failure means "someone else wrote first". A deadlock, a constraint
    /// violation or anything else must keep propagating rather than be reported as a version
    /// conflict the client would retry against.
    /// </summary>
    [Theory]
    [InlineData("40P01")] // deadlock
    [InlineData("23505")] // unique violation
    [InlineData("23514")] // check violation
    public void OtherDatabaseErrors_AreNot(string sqlState)
    {
        var thrown = new InvalidOperationException("transient", new DbUpdateException("save failed", Postgres(sqlState)));

        Assert.False(ConcurrencyGuard.IsSerializationFailure(thrown));
    }

    [Fact]
    public void AnExceptionWithNoDatabaseCause_IsNot()
    {
        Assert.False(ConcurrencyGuard.IsSerializationFailure(new InvalidOperationException("unrelated")));
    }
}
