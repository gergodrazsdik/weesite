using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace Weesite.Web;

/// <summary>
/// Configuration that loads in the background (T-0192, ADR 0004 amendment): the environment's Key
/// Vault secrets, the sign-in clients, the configured admins and the SMTP settings. The host no
/// longer waits for them, about 4 s on a new replica, most of it the managed identity's first
/// token; public pages never need them. What does (sign-in, the admin, the editor MCP endpoint,
/// mail) waits for <see cref="SecretsGate"/>. Until they're in, they're absent from the
/// configuration; once in, the configuration reloads with them. A failed load is retried, with
/// longer waits, until it succeeds.
/// </summary>
internal sealed class BackgroundSecrets : IConfigurationSource
{
    private static readonly TimeSpan[] Retries = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];

    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string?>>> _load;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Exception> _failures = [];
    private Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private int _started;

    /// <summary>Initializes a new instance of the <see cref="BackgroundSecrets"/> class.</summary>
    /// <param name="load">Loads the secrets, as configuration keys and values.</param>
    public BackgroundSecrets(Func<CancellationToken, Task<IReadOnlyDictionary<string, string?>>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        _load = load;
    }

    private event Action? Changed;

    /// <summary>Gets a task that completes once the secrets are in.</summary>
    public Task Loaded => _loaded.Task;

    /// <summary>Gets when the secrets were in, since the process began, once they are.</summary>
    public TimeSpan? LoadedAt { get; private set; }

    /// <summary>Gets how many secrets came in.</summary>
    public int Count => _values.Count;

    /// <summary>Gets the failures so far, newest last, each retried.</summary>
    public IReadOnlyList<Exception> Failures
    {
        get
        {
            lock (_failures)
            {
                return [.. _failures];
            }
        }
    }

    /// <summary>The secrets of a Key Vault: every enabled one, its name's <c>--</c> read as <c>:</c>, as Azure's own provider reads them.</summary>
    /// <param name="vault">The vault.</param>
    /// <param name="credential">The credential.</param>
    /// <returns>The source.</returns>
    public static BackgroundSecrets FromKeyVault(Uri vault, TokenCredential credential)
    {
        var client = new SecretClient(vault, credential);
        return new BackgroundSecrets(async cancellationToken =>
        {
            var names = new List<string>();
            await foreach (var properties in client.GetPropertiesOfSecretsAsync(cancellationToken))
            {
                if (properties.Enabled == true)
                {
                    names.Add(properties.Name);
                }
            }

            var values = await Task.WhenAll(names.Select(async name =>
            {
                var secret = await client.GetSecretAsync(name, cancellationToken: cancellationToken);
                return (Key: name.Replace("--", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal), secret.Value.Value);
            }));
            return values.ToDictionary(v => v.Key, v => (string?)v.Value, StringComparer.OrdinalIgnoreCase);
        });
    }

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(this);

    // Starts the load once, however often the configuration is rebuilt.
    private void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var values = await _load(CancellationToken.None);
                _values = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
                LoadedAt = StartupTimes.SinceProcessStart;
                Changed?.Invoke();
                _loaded.TrySetResult();
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_failures)
                {
                    _failures.Add(ex);
                }

                await Task.Delay(Retries[Math.Min(attempt, Retries.Length - 1)]);
            }
        }
    }

    private sealed class Provider : ConfigurationProvider
    {
        private readonly BackgroundSecrets _source;

        public Provider(BackgroundSecrets source)
        {
            _source = source;
            source.Changed += () =>
            {
                Data = new Dictionary<string, string?>(source._values, StringComparer.OrdinalIgnoreCase);
                OnReload();
            };
        }

        public override void Load()
        {
            Data = new Dictionary<string, string?>(_source._values, StringComparer.OrdinalIgnoreCase);
            _source.Start();
        }
    }
}

/// <summary>
/// Waits for the configuration's <see cref="BackgroundSecrets"/> (T-0192). Without any, as in
/// local runs and most tests, there's nothing to wait for.
/// </summary>
internal sealed class SecretsGate
{
    /// <summary>Gets or sets how long a request or a job waits for the secrets before giving up: 30 s; shorter in tests.</summary>
    public TimeSpan Limit { get; set; } = TimeSpan.FromSeconds(30);

    // The paths that need the secrets: the admin and its sign-in (the providers' clients and the
    // configured admins), the editor MCP endpoint and the OAuth server (editors' roles), and the
    // client ID Entra reads. Public pages don't wait.
    private static readonly string[] Paths = ["/admin", "/signin-microsoft", "/signin-google", "/mcp/editor", "/oauth", "/.well-known/microsoft-identity-association.json"];

    private readonly List<Action> _onLoaded = [];
    private readonly Task _loaded;
    private bool _ran;

    /// <summary>Initializes a new instance of the <see cref="SecretsGate"/> class.</summary>
    /// <param name="configuration">The app's configuration, whose sources it looks through.</param>
    public SecretsGate(IConfiguration configuration)
    {
        var sources = configuration is IConfigurationBuilder builder ? builder.Sources.OfType<BackgroundSecrets>().ToList() : [];
        Sources = sources;
        if (sources.Count == 0)
        {
            _ran = true;
            _loaded = Task.CompletedTask;
            return;
        }

        // What waits for the secrets to set something up (the sign-in providers' schemes) runs
        // before anything waiting here goes on.
        _loaded = Task.WhenAll(sources.Select(s => s.Loaded)).ContinueWith(
            _ =>
            {
                Action[] actions;
                lock (_onLoaded)
                {
                    _ran = true;
                    actions = [.. _onLoaded];
                }

                foreach (var action in actions)
                {
                    action();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Gets the sources it waits for.</summary>
    public IReadOnlyList<BackgroundSecrets> Sources { get; }

    /// <summary>Gets whether the secrets are in.</summary>
    public bool IsLoaded => _loaded.IsCompletedSuccessfully;

    /// <summary>
    /// Runs something once the secrets are in, before anything waiting for them goes on: at once
    /// when they're in already.
    /// </summary>
    /// <param name="action">What to run.</param>
    public void OnLoaded(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_onLoaded)
        {
            if (!_ran)
            {
                _onLoaded.Add(action);
                return;
            }
        }

        action();
    }

    /// <summary>Gets whether a request's path needs the secrets.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True when the request waits for them.</returns>
    public static bool Needs(PathString path) =>
        Paths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Waits for the secrets, at most <see cref="Limit"/>.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes once they're in.</returns>
    /// <exception cref="SecretsUnavailableException">They didn't come in time.</exception>
    public async Task WhenLoadedAsync(CancellationToken cancellationToken)
    {
        if (IsLoaded)
        {
            return;
        }

        try
        {
            await _loaded.WaitAsync(Limit, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new SecretsUnavailableException(ex);
        }
    }
}

/// <summary>The secrets didn't come in within <see cref="SecretsGate.Limit"/>.</summary>
internal sealed class SecretsUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SecretsUnavailableException"/> class.</summary>
    public SecretsUnavailableException()
        : base("The Key Vault secrets aren't in yet; the log says why.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretsUnavailableException"/> class.</summary>
    /// <param name="message">The message.</param>
    public SecretsUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretsUnavailableException"/> class.</summary>
    /// <param name="inner">The timeout.</param>
    public SecretsUnavailableException(Exception inner)
        : base("The Key Vault secrets aren't in yet; the log says why.", inner)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretsUnavailableException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public SecretsUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Holds back the requests that need the secrets until they're in (T-0192).</summary>
internal static partial class SecretsGateMiddleware
{
    /// <summary>
    /// Makes the admin, sign-in, the editor MCP endpoint and the OAuth server wait for the secrets,
    /// at most <see cref="SecretsGate.Limit"/>, then answer 503 with a <c>Retry-After</c>.
    /// Before authentication, so the providers' schemes are in place by then.
    /// </summary>
    /// <param name="app">The app.</param>
    /// <returns>The app.</returns>
    public static IApplicationBuilder UseSecretsGate(this IApplicationBuilder app)
    {
        var gate = app.ApplicationServices.GetRequiredService<SecretsGate>();
        if (gate.Sources.Count == 0)
        {
            return app;
        }

        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.Secrets");
        return app.Use(async (context, next) =>
        {
            if (!gate.IsLoaded && SecretsGate.Needs(context.Request.Path))
            {
                try
                {
                    await gate.WhenLoadedAsync(context.RequestAborted);
                }
                catch (SecretsUnavailableException)
                {
                    LogUnavailable(logger, context.Request.Path);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers.RetryAfter = "10";
                    await context.Response.WriteAsync("Sign-in isn't available yet. Try again in a few seconds.", context.RequestAborted);
                    return;
                }
            }

            await next();
        });
    }

    /// <summary>Logs the secrets' load once it's done, and each failed attempt (T-0192).</summary>
    /// <param name="app">The app, just built.</param>
    public static void LogSecretsWhenLoaded(this WebApplication app)
    {
        var gate = app.Services.GetRequiredService<SecretsGate>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.Secrets");
        foreach (var source in gate.Sources)
        {
            _ = source.Loaded.ContinueWith(
                _ =>
                {
                    foreach (var failure in source.Failures)
                    {
                        LogRetried(logger, failure);
                    }

                    LogLoaded(logger, source.Count, (long)(source.LoadedAt ?? TimeSpan.Zero).TotalMilliseconds);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _ = Task.Run(async () =>
            {
                // A load still failing after the limit is logged then, not only once it succeeds.
                await Task.Delay(gate.Limit);
                if (!source.Loaded.IsCompleted && source.Failures is [.., var last])
                {
                    LogStillFailing(logger, last, source.Failures.Count);
                }
            });
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Key Vault secrets in at {At} ms after the process began ({Count} secrets)")]
    private static partial void LogLoaded(ILogger logger, int count, long at);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Loading the Key Vault secrets failed and was tried again")]
    private static partial void LogRetried(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Key Vault secrets still aren't in after {Attempts} attempts: sign-in, the admin and mail wait for them, and answer 503 meanwhile")]
    private static partial void LogStillFailing(ILogger logger, Exception exception, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Path} answered 503: the Key Vault secrets aren't in yet")]
    private static partial void LogUnavailable(ILogger logger, PathString path);
}
