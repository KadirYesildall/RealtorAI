namespace RealtorAI.Tests.Integration;

/// <summary>
/// Tests that take table-level locks, run on their own.
///
/// <para>
/// A TRUNCATE takes ACCESS EXCLUSIVE locks — on <c>listings</c>, then <c>audit_events</c>, and
/// on every table its CASCADE reaches — before the guard trigger refuses it. Any test inserting
/// an audit row at the same moment holds <c>audit_events</c> and wants <c>listings</c> for the
/// foreign-key check: the opposite order, and PostgreSQL resolves the cycle by killing one of them
/// with 40P01. That surfaced as an intermittent failure in an unrelated test (research R31).
/// The locks are the point of these tests, so rather than weaken them they run after every
/// parallel collection has finished, with nothing alongside.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TableLockingTests
{
    public const string Name = "Table-locking tests";
}
