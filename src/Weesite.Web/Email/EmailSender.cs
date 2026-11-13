using System.Globalization;
using System.Net;
using System.Net.Mail;
using MailKit.Security;
using MimeKit;
using MailKitSmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace Weesite.Web.Email;

/// <summary>A mail to send: plain text with an HTML alternative (ADR 0010).</summary>
/// <param name="To">The recipient's address.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Text">The plain text body.</param>
/// <param name="Html">The HTML body.</param>
/// <param name="FromName">The sender's display name, such as the site's name.</param>
internal sealed record EmailMessage(string To, string Subject, string Text, string Html, string FromName);

/// <summary>Sends email (ADR 0010).</summary>
internal interface IEmailSender
{
    /// <summary>Gets a value indicating whether sending is set up.</summary>
    bool IsConfigured { get; }

    /// <summary>Sends a mail; throws when it can't.</summary>
    /// <param name="message">The mail.</param>
    /// <param name="cancellationToken">Stops the send.</param>
    /// <returns>A task.</returns>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>No sending: the settings aren't there (locally, in tests, a deployment without a provider).</summary>
internal sealed class NoEmailSender : IEmailSender
{
    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Email isn't set up (Email:Smtp:Host and Email:From).");
}

/// <summary>
/// The SMTP settings (ADR 0010): <c>Email:Smtp:Host</c>, <c>Port</c> (587), <c>Security</c>
/// (<c>StartTls</c>, or <c>SslOnConnect</c> for 465; <c>None</c> only for a server on this
/// machine, such as Mailpit), <c>UserName</c>, <c>Password</c>, and <c>Email:From</c>.
/// </summary>
/// <param name="Host">The server.</param>
/// <param name="Port">The port.</param>
/// <param name="Security">How the connection is encrypted.</param>
/// <param name="UserName">The user name, or null to send without signing in.</param>
/// <param name="Password">The password.</param>
/// <param name="From">The sender's address.</param>
internal sealed record SmtpSettings(string Host, int Port, SecureSocketOptions Security, string? UserName, string? Password, string From)
{
    /// <summary>Reads the settings; null when the host or the sender is missing.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The settings, or null.</returns>
    /// <exception cref="InvalidOperationException">A setting that is there is wrong.</exception>
    public static SmtpSettings? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var host = configuration["Email:Smtp:Host"]?.Trim();
        var from = configuration["Email:From"]?.Trim();
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(from))
        {
            return null;
        }

        if (!MailAddress.TryCreate(from, out var address) || address.Address != from)
        {
            throw new InvalidOperationException($"Email:From must be a plain address, such as noreply@example.com; got '{from}'.");
        }

        var portText = configuration["Email:Smtp:Port"];
        var port = 587;
        if (!string.IsNullOrWhiteSpace(portText) && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            throw new InvalidOperationException($"Email:Smtp:Port must be a port number; got '{portText}'.");
        }

        var security = configuration["Email:Smtp:Security"]?.Trim() switch
        {
            null or "" or "StartTls" => SecureSocketOptions.StartTls,
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "None" when IsLocal(host) => SecureSocketOptions.None,
            var other => throw new InvalidOperationException($"Email:Smtp:Security must be StartTls or SslOnConnect (None only for a server on this machine); got '{other}'."),
        };

        var userName = configuration["Email:Smtp:UserName"];
        return new SmtpSettings(host, port, security, string.IsNullOrEmpty(userName) ? null : userName, configuration["Email:Smtp:Password"], from);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)} ({Security})";

    private static bool IsLocal(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
}

/// <summary>Sends through an SMTP server with MailKit (ADR 0010), within 15 seconds.</summary>
/// <param name="settings">The settings.</param>
internal sealed class SmtpEmailSender(SmtpSettings settings) : IEmailSender
{
    /// <summary>How long a send may take, connecting included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        using var mime = Build(message, settings.From);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var client = new MailKitSmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        await client.ConnectAsync(settings.Host, settings.Port, settings.Security, timeout.Token);
        if (settings.UserName is { } user)
        {
            await client.AuthenticateAsync(user, settings.Password ?? "", timeout.Token);
        }

        await client.SendAsync(mime, timeout.Token);
        await client.DisconnectAsync(quit: true, timeout.Token);
    }

    /// <summary>Builds the MIME message: plain text with an HTML alternative.</summary>
    /// <param name="message">The mail.</param>
    /// <param name="from">The sender's address.</param>
    /// <returns>The message.</returns>
    internal static MimeMessage Build(EmailMessage message, string from)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.FromName, from));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html }.ToMessageBody();
        return mime;
    }
}

/// <summary>Registers the sender.</summary>
internal static class EmailServices
{
    /// <summary>Adds <see cref="IEmailSender"/>: SMTP when it's set up, otherwise none.</summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<IEmailSender>(SmtpSettings.Read(configuration) is { } settings ? new SmtpEmailSender(settings) : new NoEmailSender());
}
