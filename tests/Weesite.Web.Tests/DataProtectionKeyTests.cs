using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Weesite.Web.Tests;

/// <summary>Wrapping the Data Protection keys with a Key Vault key (T-0078), and revoking those from before it (T-0122).</summary>
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

    [Test]
    public async Task With_wrapping_on_the_unwrapped_keys_are_revoked_and_the_wrapped_ones_kept()
    {
        var (services, repository) = Build();
        await using var _ = services;
        var keys = services.GetRequiredService<IKeyManager>();
        var plain = keys.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90)).KeyId;
        var wrapped = Guid.NewGuid();
        repository.StoreElement(WrappedKey(wrapped), "wrapped");

        await Revoker(services, "https://kv.example/keys/dp").StartAsync(CancellationToken.None);

        var revoked = Revocations(repository);
        await Assert.That(revoked).Contains(plain.ToString());
        await Assert.That(revoked).DoesNotContain(wrapped.ToString());

        // A second start doesn't revoke it again.
        await Revoker(services, "https://kv.example/keys/dp").StartAsync(CancellationToken.None);
        await Assert.That(Revocations(repository).Count(id => id == plain.ToString())).IsEqualTo(1);
    }

    [Test]
    public async Task Without_wrapping_nothing_is_revoked()
    {
        var (services, repository) = Build();
        await using var _ = services;
        services.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

        await Revoker(services, null).StartAsync(CancellationToken.None);

        await Assert.That(Revocations(repository)).IsEmpty();
    }

    private static (ServiceProvider Services, IXmlRepository Repository) Build()
    {
        var repository = new MemoryRepository();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddDataProtection().SetApplicationName("weesite-test");
        collection.Configure<KeyManagementOptions>(o => o.XmlRepository = repository);
        return (collection.BuildServiceProvider(), repository);
    }

    private static UnwrappedKeyRevoker Revoker(IServiceProvider services, string? keyId) =>
        new(
            services.GetRequiredService<IOptions<KeyManagementOptions>>(),
            services.GetRequiredService<IKeyManager>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataProtection:KeyId"] = keyId }).Build(),
            NullLogger<UnwrappedKeyRevoker>.Instance);

    // A key element as Key Vault wrapping writes it: its secret is an <encryptedSecret>.
    private static XElement WrappedKey(Guid id) =>
        new("key",
            new XAttribute("id", id),
            new XAttribute("version", 1),
            new XElement("creationDate", DateTimeOffset.UtcNow),
            new XElement("activationDate", DateTimeOffset.UtcNow),
            new XElement("expirationDate", DateTimeOffset.UtcNow.AddDays(90)),
            new XElement("descriptor", new XElement("descriptor", new XElement("encryptedSecret", new XAttribute("decryptorType", "Azure.Extensions.AspNetCore.DataProtection.Keys.AzureKeyVaultXmlDecryptor")))));

    private static List<string> Revocations(IXmlRepository repository) =>
        [.. repository.GetAllElements().Where(e => e.Name.LocalName == "revocation").Select(e => e.Element("key")?.Attribute("id")?.Value).OfType<string>()];

    private sealed class MemoryRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_elements)
            {
                return [.. _elements.Select(e => new XElement(e))];
            }
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements)
            {
                _elements.Add(new XElement(element));
            }
        }
    }
}
