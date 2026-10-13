using Api.Data;
using Microsoft.EntityFrameworkCore;
using Modules.Brokerage;

namespace Api.Authorization;

/// <summary>
/// T037 — the host-side adapter behind <see cref="IMembershipStore"/>.
///
/// <para>
/// Reads membership rows through the host's EF model, which the brokerage module cannot
/// reference itself without creating a project cycle.
/// </para>
/// <para>
/// There is no enum translation here. <c>brokerage_memberships.status</c> and <c>.role</c> map
/// directly onto the module's own <see cref="MembershipStatus"/> and
/// <see cref="MembershipRole"/>, with the database labels declared once in
/// <c>Api.Data.PostgresEnumMappings</c>. An earlier draft duplicated the enums on each side of
/// the boundary and translated between them; one definition with an explicit label table is
/// both smaller and immune to the two copies drifting apart.
/// </para>
/// </para>
/// </summary>
internal sealed class EfMembershipStore : IMembershipStore
{
    private readonly AppDbContext context;

    public EfMembershipStore(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    public async Task<IReadOnlyList<Membership>> GetForAgentAsync(
        Guid agentId,
        CancellationToken cancellationToken)
    {
        // AsNoTracking: these rows are read to make a decision and never mutated through this
        // path, so change tracking would be pure overhead on a query that runs on most
        // requests.
        var rows = await context.BrokerageMemberships
            .AsNoTracking()
            .Where(membership => membership.AgentId == agentId)
            .Select(membership => new
            {
                membership.AgentId,
                membership.BrokerageId,
                membership.Status,
                membership.Role,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new Membership(
                row.AgentId,
                row.BrokerageId,
                row.Status,
                row.Role))
            .ToList();
    }
}
