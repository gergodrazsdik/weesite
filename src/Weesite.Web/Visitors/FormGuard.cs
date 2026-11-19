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
/// refused, but with the visitor's text kept, so a person only presses Send again. The honeypot is
/// a field people never see (hidden with a stylesheet class, so neither the tab order nor screen
/// readers reach it); a bot that fills every field fills it too.
/// </summary>
/// <param name="protection">Data Protection, whose keys every replica shares.</param>
/// <param name="time">The clock.</param>
/// <param name="options">The minimum time and the lifetime.</param>
internal sealed class FormGuard(IDataProtectionProvider protection, TimeProvider time, VisitorOptions options)
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
        if (string.IsNullOrEmpty(token))
        {
            return FormCheck.Invalid;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return FormCheck.Invalid;
        }

        var parts = payload.Split('\n');
        if (parts.Length != 2 || !string.Equals(parts[0], purpose, StringComparison.Ordinal)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var shown))
        {
            return FormCheck.Invalid;
        }

        var age = time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(shown);
        return age < options.MinimumTime ? FormCheck.TooFast
            : age > options.FormLifetime ? FormCheck.Expired
            : FormCheck.Ok;
    }

    /// <summary>Whether the honeypot was filled in.</summary>
    /// <param name="value">The honeypot field's value.</param>
    /// <returns>True when a bot filled it.</returns>
    public static bool IsHoneypotFilled(string? value) => !string.IsNullOrWhiteSpace(value);
}
