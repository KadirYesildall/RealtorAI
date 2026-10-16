using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Modules.Listings.PropertyData;
using Modules.Media;
using RealtorAI.Tests.Contract.Infrastructure;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// The media services as the real host composes them (T057, T058, T060).
/// </summary>
public sealed class MediaRegistrationTests
{
    /// <summary>The host with the storage key read from a variable this test controls.</summary>
    private sealed class Factory(string keyVariable) : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Storage:ServiceKeyEnvVar", keyVariable);
        }
    }

    private static string UniqueVariable() => "REALTORAI_TEST_STORAGE_KEY_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void TheNormalizer_IsRegisteredFromConfiguration()
    {
        using var factory = new Factory(UniqueVariable());

        var normalizer = factory.Services.GetRequiredService<ImageNormalizer>();

        Assert.NotNull(normalizer);
    }

    /// <summary>No public-record source is configured in Slice A (research R5).</summary>
    [Fact]
    public void PropertyData_IsTheUnconfiguredProvider()
    {
        using var factory = new Factory(UniqueVariable());

        Assert.IsType<NullPropertyDataProvider>(factory.Services.GetRequiredService<IPropertyDataProvider>());
    }

    [Fact]
    public void Storage_Resolves_WhenTheKeyVariableIsSet()
    {
        var variable = UniqueVariable();
        Environment.SetEnvironmentVariable(variable, "local-test-key");

        try
        {
            using var factory = new Factory(variable);
            using var scope = factory.Services.CreateScope();

            Assert.IsType<StorageService>(scope.ServiceProvider.GetRequiredService<IPhotoStorage>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// A missing key fails the first storage use with a message naming the variable — not the
    /// host start, which would take down every endpoint that never touches storage.
    /// </summary>
    [Fact]
    public void Storage_WithoutTheKey_FailsOnFirstUse_NamingTheVariable()
    {
        var variable = UniqueVariable();
        using var factory = new Factory(variable);
        using var scope = factory.Services.CreateScope();

        var exception = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IPhotoStorage>());

        Assert.Contains(variable, exception.Message, StringComparison.Ordinal);
    }
}
