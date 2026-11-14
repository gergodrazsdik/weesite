using Azure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;

namespace Weesite.Web;

/// <summary>Wraps the Data Protection keys with a Key Vault key (T-0078).</summary>
internal static class DataProtectionKeys
{
    /// <summary>
    /// Wraps new keys with the Key Vault key in <c>DataProtection:KeyId</c> (its versionless
    /// address, so a new key version is used once it exists; each wrapped key records the version
    /// it needs). Without the setting nothing changes: keys are stored unwrapped, as before.
    /// </summary>
    /// <param name="builder">The Data Protection builder.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="credential">The site's credential.</param>
    /// <returns>The builder.</returns>
    public static IDataProtectionBuilder ProtectKeysWithKeyVaultKey(this IDataProtectionBuilder builder, IConfiguration configuration, TokenCredential credential) =>
        configuration["DataProtection:KeyId"] is { Length: > 0 } keyId
            ? builder.ProtectKeysWithAzureKeyVault(new Uri(keyId), credential)
            : builder;
}

/// <summary>
/// Revokes the Data Protection keys written before they were wrapped with the Key Vault key
/// (T-0078): Data Protection keeps every key and still reads with an expired one, so a key that sat
/// unwrapped in Blob Storage would keep working for anyone who once read it, to forge a sign-in
/// cookie for instance. Runs at start when <c>DataProtection:KeyId</c> is set; the next request
/// creates a wrapped key, and everyone signs in once more (T-0122).
/// </summary>
internal sealed partial class UnwrappedKeyRevoker(
    IOptions<KeyManagementOptions> options,
    IKeyManager keys,
    IConfiguration configuration,
    ILogger<UnwrappedKeyRevoker> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration["DataProtection:KeyId"] is not { Length: > 0 } || options.Value.XmlRepository is not { } repository)
        {
            return Task.CompletedTask;
        }

        var elements = repository.GetAllElements();
        var revoked = elements
            .Where(e => e.Name.LocalName == "revocation")
            .Select(e => e.Element("key")?.Attribute("id")?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in elements.Where(e => e.Name.LocalName == "key"))
        {
            // A wrapped key's secret is an <encryptedSecret>; an unwrapped one's is plain.
            if (key.Attribute("id")?.Value is { } id && Guid.TryParse(id, out var keyId) && !revoked.Contains(id)
                && !key.Descendants().Any(d => d.Name.LocalName == "encryptedSecret"))
            {
                keys.RevokeKey(keyId, "Written before keys were wrapped with the Key Vault key (T-0122).");
                LogRevoked(logger, keyId);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revoked the unwrapped Data Protection key {KeyId}: everyone signs in again once")]
    private static partial void LogRevoked(ILogger logger, Guid keyId);
}
