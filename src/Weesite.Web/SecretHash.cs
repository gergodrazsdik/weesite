using System.Security.Cryptography;
using System.Text;

namespace Weesite.Web;

/// <summary>
/// The hash a random secret is stored as: OAuth codes and refresh tokens, upload addresses and
/// comment tokens (F-0227). SHA-256, in lowercase hex. The secrets are random, so no salt or
/// stretching is needed.
/// </summary>
internal static class SecretHash
{
    /// <summary>Gets a secret's hash.</summary>
    /// <param name="secret">The secret.</param>
    /// <returns>64 lowercase hex digits.</returns>
    public static string Of(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }
}
