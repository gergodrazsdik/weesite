namespace Weesite.Web.Visitors;

/// <summary>Wires up the contact form and the hourly digests (ADR 0011).</summary>
internal static class ContactExtensions
{
    /// <summary>
    /// Adds the hourly digests' sender and its state (Table Storage, or memory with content from
    /// files). Each digest registers itself as an <see cref="IDigest"/>.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="inMemory">Whether to keep the state in memory.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddDigests(this IServiceCollection services, bool inMemory)
    {
        if (inMemory)
        {
            services.AddSingleton<IDigestStateStore, MemoryDigestStateStore>();
        }
        else
        {
            services.AddSingleton<IDigestStateStore, TableDigestStateStore>();
        }

        services.AddSingleton<DigestSender>();
        services.AddHostedService(s => s.GetRequiredService<DigestSender>());
        return services;
    }

    /// <summary>
    /// Adds the contact form: its messages (Table Storage, or memory), the inbox that takes them,
    /// their digest, and the daily clean-up of messages older than 12 months.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="inMemory">Whether to keep the messages in memory.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddContactForm(this IServiceCollection services, bool inMemory)
    {
        if (inMemory)
        {
            services.AddSingleton<MemoryMessageStore>();
            services.AddSingleton<IMessageStore>(s => s.GetRequiredService<MemoryMessageStore>());
        }
        else
        {
            services.AddSingleton<IMessageStore, TableMessageStore>();
        }

        services.AddSingleton<ContactInbox>();
        services.AddSingleton<MessagesDigest>();
        services.AddSingleton<IDigest>(s => s.GetRequiredService<MessagesDigest>());
        services.AddHostedService<MessageCleanUp>();
        return services;
    }
}
