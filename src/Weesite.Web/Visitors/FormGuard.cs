using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Weesite.Web.Visitors;

/// <summary>What <see cref="FormGuard.Check"/> found.</summary>
internal enum FormCheck
{
    /// <summary>Sent in time, by the form it was shown for.</summary>
    Ok,

    /// <summary>Sent sooner after showing than a person types: probably a bot; the visitor may send again.</summary>
    TooFast,

    /// <summary>Shown too long ago; the visitor may send again.</summary>
    Expired,

    /// <summary>No token, or one that isn't this site's or this form's.</summary>
    Invalid,
}

/// <summary>
/// The form token and the honeypot (ADR 0011). The token is Data Protection over the form's
/// purpose and when it was shown: a form sent within <see cref="VisitorOptions.MinimumTime"/> is
/// refused, but with the visitor's text kept, so a person only presses Send again. A token takes
/// one message or comment (<see cref="TryUseAsync"/>): the form gets a fresh one for the next. The
/// honeypot is a field people never see (hidden with a stylesheet class, so neither the tab order
/// nor screen readers reach it); a bot that fills every field fills it too.
/// </summary>
/// <param name="protection">Data Protection, whose keys every replica shares.</param>
/// <param name="time">The clock.</param>
/// <param name="options">The minimum time and the lifetime.</param>
/// <param name="store">Where used tokens are marked, as used proof-of-work challenges are.</param>
internal sealed class FormGuard(IDataProtectionProvider protection, TimeProvider time, VisitorOptions options, IVisitorStore store)
{
    /// <summary>The honeypot field's name: one autofill doesn't fill, though bots like it.</summary>
    public const string HoneypotField = "website";

    private readonly IDataProtector _protector = protection.CreateProtector("Weesite.Visitors.FormToken");

    /// <summary>Makes the token for a form being shown now.</summary>
    /// <param name="purpose">Which form, such as <c>contact</c>.</param>
    /// <returns>The token.</returns>
    public string Issue(string purpose) =>
        _protector.Protect(string.Create(CultureInfo.InvariantCulture, $"{purpose}\n{time.GetUtcNow().ToUnixTimeMilliseconds()}"));

    /// <summary>Checks a sent form's token.</summary>
    /// <param name="purpose">Which form.</param>
    /// <param name="token">The token it carried.</param>
    /// <returns>What was found.</returns>
    public FormCheck Check(string purpose, string? token)
    {
        if (string.IsNullOrEmpty(token) || Read(token) is not { } read || !string.Equals(read.Purpose, purpose, StringComparison.Ordinal))
        {
            return FormCheck.Invalid;
        }

        var age = time.GetUtcNow() - read.Shown;
        return age < options.MinimumTime ? FormCheck.TooFast
            : age > options.FormLifetime ? FormCheck.Expired
            : FormCheck.Ok;
    }

    /// <summary>
    /// Marks a token that passed <see cref="Check"/> used, just before what it carried is kept: a
    /// token takes one message or comment. A form refused for its text keeps its token.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>True the first time; false when it took one before.</returns>
    public Task<bool> TryUseAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        // Marked in the partition of the day the token expires, the same for every use of it, and
        // kept until then: the clean-up deletes the rows of days before yesterday.
        var expires = (Read(token)?.Shown ?? time.GetUtcNow()) + options.FormLifetime;
        var key = "form-" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        return store.TryUseChallengeAsync(key, expires, cancellationToken);
    }

    // The token's purpose and when its form was shown; null when it isn't a token of this site's.
    private (string Purpose, DateTimeOffset Shown)? Read(string token)
    {
        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var parts = payload.Split('\n');
        return parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var shown)
            ? (parts[0], DateTimeOffset.FromUnixTimeMilliseconds(shown))
            : null;
    }

    /// <summary>Whether the honeypot was filled in.</summary>
    /// <param name="value">The honeypot field's value.</param>
    /// <returns>True when a bot filled it.</returns>
    public static bool IsHoneypotFilled(string? value) => !string.IsNullOrWhiteSpace(value);
}
