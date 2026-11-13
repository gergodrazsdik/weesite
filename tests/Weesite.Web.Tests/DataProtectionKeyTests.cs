using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Weesite.Web.Tests;

/// <summary>Wrapping the Data Protection keys with a Key Vault key (T-0078).</summary>
internal sealed class DataProtectionKeyTests
{
    [Test]
    public async Task A_configured_key_wraps_new_keys()
    {
        var options = KeyManagement(new() { ["DataProtection:KeyId"] = "https://example.vault.azure.net/keys/dataprotection" });

        await Assert.That(options.XmlEncryptor).IsNotNull();
        await Assert.That(options.XmlEncryptor!.GetType().Name).Contains("AzureKeyVault");
    }

    [Test]
    public async Task Without_a_key_nothing_changes()
    {
        var options = KeyManagement([]);

        await Assert.That(options.XmlEncryptor).IsNull();
    }

    private static KeyManagementOptions KeyManagement(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddDataProtection().ProtectKeysWithKeyVaultKey(configuration, new Azure.Identity.DefaultAzureCredential());
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
    }
}
