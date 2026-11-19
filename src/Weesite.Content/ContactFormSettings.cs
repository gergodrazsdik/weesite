using System.Net.Mail;

namespace Weesite.Content;

/// <summary>
/// The contact form's settings (ADR 0011): whether <c>/contact</c> shows the form, the content
/// page above it, and where its messages go. The recipient is private: it's never shown on the
/// site, only used for the hourly digest.
/// </summary>
public sealed record ContactFormSettings
{
    /// <summary>Gets a value indicating whether the form is on.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the slug of the content page shown above the form, if any.</summary>
    public string? Page { get; init; }

    /// <summary>Gets the address the messages' digest goes to ("Messages go to").</summary>
    public string? Recipient { get; init; }

    /// <summary>Gets a value indicating whether the form takes messages: on, with a recipient that's an address.</summary>
    public bool IsOn => Enabled && IsAddress(Recipient);

    /// <summary>Whether a text is a plain email address.</summary>
    /// <param name="address">The text.</param>
    /// <returns>True for an address such as <c>owner@example.com</c>.</returns>
    public static bool IsAddress(string? address) =>
        address is { Length: > 0 and <= 254 } && MailAddress.TryCreate(address, out var parsed) && parsed.Address == address;
}
