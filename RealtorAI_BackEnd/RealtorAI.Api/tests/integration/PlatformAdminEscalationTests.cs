namespace RealtorAI.Tests.Integration;

/// <summary>
/// T031 — FR-064: <c>profiles.platform_admin</c> is not settable through the application.
///
/// <para>
/// This is the privilege-escalation path that matters most in this schema. A platform admin can
/// read and change the AI model registry, which is what decides whether a model is enabled at
/// all; an agent who could flip their own flag could switch on an unlicensed, unevaluated model
/// and defeat Principle VIII.4 from the inside.
/// </para>
/// <para>
/// Two independent mechanisms are verified separately, because each covers a hole the other
/// leaves:
/// </para>
/// <list type="bullet">
///   <item><description>
///     0004 revokes blanket UPDATE on <c>profiles</c> and re-grants it column by column,
///     omitting this one — so an <c>authenticated</c> caller is refused by column privileges.
///   </description></item>
///   <item><description>
///     A BEFORE UPDATE trigger rejects any change to the value — which is what still holds when
///     the connection is privileged enough to ignore grants, as the API's own connection is.
///   </description></item>
/// </list>
/// </summary>
public sealed class PlatformAdminEscalationTests
{
    [Fact]
    public async Task AuthenticatedAgent_CannotGrantThemselvesPlatformAdmin()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await SupabaseDatabase.BeginAsAgentAsync(
            connection,
            SupabaseDatabase.AgentA);

        // 42501 is insufficient_privilege: the authenticated role holds no UPDATE privilege on
        // this column at all, so the statement never reaches the trigger or a policy.
        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "update public.profiles set platform_admin = true where id = $1",
            "42501",
            SupabaseDatabase.AgentA);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task PrivilegedConnection_IsAlsoRefused_ByTheTrigger()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // No `set local role`: this runs as postgres, which bypasses RLS and column grants.
        // The trigger's WHEN clause still matches, because a superuser counts as a member of
        // every role.
        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "update public.profiles set platform_admin = true where id = $1",
            "platform_admin cannot be changed",
            SupabaseDatabase.AgentA);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// Revoking the flag is blocked too. The trigger compares for any difference rather than
    /// only for escalation, so a compromised or buggy caller cannot strip an admin's access
    /// either — demotion is as much a privileged act as promotion.
    /// </summary>
    [Fact]
    public async Task RevokingPlatformAdmin_IsAlsoRefused()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "update public.profiles set platform_admin = false where id = $1",
            "platform_admin cannot be changed",
            SupabaseDatabase.PlatformAdmin);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The per-column grant must not have locked the table down entirely. Without this, a
    /// migration that revoked UPDATE and forgot to re-grant the editable columns would pass
    /// every other test in this class while breaking profile editing for all users.
    /// </summary>
    [Fact]
    public async Task AuthenticatedAgent_CanStillUpdateTheirOwnEditableColumns()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await SupabaseDatabase.BeginAsAgentAsync(
            connection,
            SupabaseDatabase.AgentA);

        var affected = await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            "update public.profiles set full_name = 'Renamed In Test' where id = $1",
            SupabaseDatabase.AgentA);

        Assert.Equal(1, affected);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// And RLS still scopes the update to the caller's own row — the column grant is about
    /// which columns, the policy is about which rows, and both are needed.
    /// </summary>
    [Fact]
    public async Task AuthenticatedAgent_CannotUpdateAnotherAgentsProfile()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await SupabaseDatabase.BeginAsAgentAsync(
            connection,
            SupabaseDatabase.AgentA);

        var affected = await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            "update public.profiles set full_name = 'Hijacked' where id = $1",
            SupabaseDatabase.AgentC);

        // RLS filters the row out rather than raising, so the honest assertion is that nothing
        // was changed. A test expecting an exception here would fail for the wrong reason.
        Assert.Equal(0, affected);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The flag is also not readable-and-writable via a round trip through the seeded admin:
    /// confirms the repository ships exactly one platform admin, so a stray second one shows up
    /// as a failure here.
    /// </summary>
    [Fact]
    public async Task ExactlyOnePlatformAdminIsSeeded()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var count = await SupabaseDatabase.ScalarAsync<long?>(
            connection,
            transaction: null,
            "select count(*) from public.profiles where platform_admin");

        Assert.Equal(1, count);
    }
}
