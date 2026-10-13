using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Api.Data;

/// <summary>
/// T009 — registration for <see cref="AppDbContext"/>.
/// </summary>
internal static class AppDbContextSetup
{
    internal static IServiceCollection AddAppDbContext(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Fail fast rather than falling back to a default connection string: a silently wrong
        // target database is how a test run ends up writing to a real one.
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not configured. The local value comes from the "
                + "running Supabase stack (default port 54322) — see "
                + "specs/002-ai-listing-launch/quickstart.md.");

        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString, AppDbContext.ConfigureNpgsql);

            // See MigrationsAreOwnedBySqlMigrator: this is what makes "EF migrations
            // disabled" a property of the running application rather than a convention
            // someone has to remember.
            options.ReplaceService<IMigrator, MigrationsAreOwnedBySqlMigrator>();
        });

        return services;
    }
}
