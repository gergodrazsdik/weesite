using Weesite.Content;

namespace Weesite.Web.Visitors;

/// <summary>The contact form's fields that bind (ADR 0011); the token, the honeypot and the proof are read from the post itself.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "A Razor component's form parameter must be public.")]
public sealed class ContactFormModel
{
    /// <summary>Gets or sets the sender's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the sender's address.</summary>
    public string? Email { get; set; }

    /// <summary>Gets or sets the message.</summary>
    public string? Message { get; set; }
}

/// <summary>What became of a sent contact form.</summary>
internal enum ContactOutcome
{
    /// <summary>Kept, and on its way to the digest.</summary>
    Sent,

    /// <summary>A bot filled the honeypot: it's told the message was sent, and nothing is kept.</summary>
    Pretended,

    /// <summary>Sent sooner after the page was shown than a person types.</summary>
    TooFast,

    /// <summary>The page was shown too long ago.</summary>
    Expired,

    /// <summary>No token, or one from elsewhere.</summary>
    Invalid,

    /// <summary>Over the hourly limit for the address.</summary>
    TooMany,

    /// <summary>The name is missing, too long, or has a link or markup.</summary>
    BadName,

    /// <summary>The address isn't an email address.</summary>
    BadEmail,

    /// <summary>The message is missing, too long, or breaks the text rules.</summary>
    BadMessage,
}

/// <summary>What became of a sent contact form, and for a refused message, why.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Problem">What's wrong with the name or message, when that was it.</param>
internal sealed record ContactResult(ContactOutcome Outcome, TextProblem Problem = TextProblem.None);

/// <summary>
/// Takes the contact form's messages (ADR 0011), through every layer in order: the honeypot, the
/// form token, the text rules, the token's single use, the hourly limit per address hash, then the
/// proof of work. A message without a valid proof is kept, marked unverified: the form works without
/// JavaScript. Nothing about the sender beyond what they typed is kept or logged.
/// </summary>
/// <param name="store">Where messages go.</param>
/// <param name="guard">The form token and the honeypot.</param>
/// <param name="proof">The proof of work.</param>
/// <param name="hashes">Address hashes.</param>
/// <param name="limits">The counters.</param>
/// <param name="options">The limits.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class ContactInbox(
    IMessageStore store,
    FormGuard guard,
    ProofOfWork proof,
    ClientHashes hashes,
    IVisitorStore limits,
    VisitorOptions options,
    TimeProvider time,
    ILogger<ContactInbox> logger)
{
    /// <summary>The form's purpose, for its token and its proof of work.</summary>
    public const string Purpose = "contact";

    /// <summary>The form token's field.</summary>
    public const string TokenField = "form-token";

    /// <summary>The proof of work's field.</summary>
    public const string ProofField = "proof";

    /// <summary>The longest name.</summary>
    public const int NameMax = 100;

    /// <summary>The longest address.</summary>
    public const int EmailMax = 254;

    /// <summary>The longest message.</summary>
    public const int MessageMax = 1000;

    /// <summary>Makes the token for a form being shown now.</summary>
    /// <returns>The token.</returns>
    public string Token() => guard.Issue(Purpose);

    /// <summary>Takes a sent form.</summary>
    /// <param name="context">The request, with the posted form.</param>
    /// <param name="form">The fields that bound.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>What became of it.</returns>
    public async Task<ContactResult> ReceiveAsync(HttpContext context, ContactFormModel form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(form);
        var posted = context.Request.HasFormContentType ? context.Request.Form : null;
        if (FormGuard.IsHoneypotFilled(posted?[FormGuard.HoneypotField]))
        {
            LogHoneypot(logger);
            return new ContactResult(ContactOutcome.Pretended);
        }

        switch (guard.Check(Purpose, posted?[TokenField]))
        {
            case FormCheck.TooFast:
                return new ContactResult(ContactOutcome.TooFast);
            case FormCheck.Expired:
                return new ContactResult(ContactOutcome.Expired);
            case FormCheck.Invalid:
                return new ContactResult(ContactOutcome.Invalid);
        }

        // Far over the limits: refused before the text is normalized, which copies it several times.
        if (VisitorText.IsFarTooLong(form.Name, NameMax))
        {
            return new ContactResult(ContactOutcome.BadName, TextProblem.TooLong);
        }

        var name = VisitorText.NormalizeLine(form.Name);
        if (VisitorText.Check(name, NameMax, maxLinks: 0) is not TextProblem.None and var nameProblem)
        {
            return new ContactResult(ContactOutcome.BadName, nameProblem);
        }

        var email = (form.Email ?? "").Trim();
        if (!ContactFormSettings.IsAddress(email))
        {
            return new ContactResult(ContactOutcome.BadEmail);
        }

        if (VisitorText.IsFarTooLong(form.Message, MessageMax))
        {
            return new ContactResult(ContactOutcome.BadMessage, TextProblem.TooLong);
        }

        var text = VisitorText.Normalize(form.Message);
        if (VisitorText.Check(text, MessageMax) is not TextProblem.None and var problem)
        {
            return new ContactResult(ContactOutcome.BadMessage, problem);
        }

        // A form token takes one message: sent again, the page's form has a new one.
        if (!await guard.TryUseAsync(posted![TokenField].ToString(), cancellationToken))
        {
            return new ContactResult(ContactOutcome.Expired);
        }

        var now = time.GetUtcNow();
        var hash = await hashes.OfAddressAsync(context, Purpose, cancellationToken);
        if (!await limits.TryCountAsync(Purpose, hash, now, CountWindow.Hour, options.ContactPerHour, cancellationToken))
        {
            LogTooMany(logger);
            return new ContactResult(ContactOutcome.TooMany);
        }

        var verified = await proof.VerifyAsync(Purpose, posted?[ProofField], cancellationToken);
        // Its time is taken just before the save, as a comment's is, for the digest's ranges (F-0250).
        var received = time.GetUtcNow();
        await store.AddAsync(new ContactMessage(ContactMessage.NewId(received), name, email, text, received, Read: false, Unverified: !verified), cancellationToken);
        LogReceived(logger, verified);
        return new ContactResult(ContactOutcome.Sent);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Contact form: a message arrived (verified: {Verified})")]
    private static partial void LogReceived(ILogger logger, bool verified);

    [LoggerMessage(Level = LogLevel.Information, Message = "Contact form: the honeypot was filled; nothing kept")]
    private static partial void LogHoneypot(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Contact form: over the hourly limit for an address")]
    private static partial void LogTooMany(ILogger logger);
}
