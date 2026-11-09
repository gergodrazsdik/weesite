using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;

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
    /// <returns>The builder.</returns>
    public static IDataProtectionBuilder ProtectKeysWithKeyVaultKey(this IDataProtectionBuilder builder, IConfiguration configuration) =>
        configuration["DataProtection:KeyId"] is { Length: > 0 } keyId
            ? builder.ProtectKeysWithAzureKeyVault(new Uri(keyId), new DefaultAzureCredential())
            : builder;
}
