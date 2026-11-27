using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weesite.ServiceDefaults;

namespace Weesite.Web.Tests;

/// <summary>
/// Nothing of Weesite's is logged before the host starts: the Azure Monitor exporter attaches its
/// log processor when its hosted service starts, and Application Insights never gets what came
/// before (F-0264). The jobs start their hosts for the telemetry alone.
/// </summary>
internal sealed class StartupLogTests
{
    private const string Starting = "-- the host starts";

    [Test]
    public async Task Nothing_is_logged_before_the_host_starts()
    {
        using var log = new Lines();
        var secrets = new BackgroundSecrets(_ => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { ["Site:Admins"] = "admin@example.com" }));
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(b =>
        {
            // Outside Development, so the start says what it says in Azure (the base address unset).
            b.UseEnvironment(Environments.Production);
            b.ConfigureAppConfiguration((_, configuration) => configuration.Add(secrets));
            b.ConfigureLogging(l => l.AddProvider(log));
            b.ConfigureServices(s => s.AddSingleton<IHostedService>(new StartMark(log)));
        });
        _ = factory.Services;
        var lines = await log.AfterSecretsAsync();

        var mark = lines.IndexOf(Starting);
        await Assert.That(mark).IsGreaterThanOrEqualTo(0);
        await Assert.That(lines.Take(mark).Where(l => l.StartsWith("Weesite.", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(lines.Skip(mark)).Contains(l => l.StartsWith("Weesite.Startup Warning: Site:BaseUrl isn't set", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_job_host_keeps_only_the_telemetrys_hosted_services()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:9/";
        builder.AddServiceDefaults();
        builder.Services.AddHostedService<Idle>();
        builder.Services.AddHostedService(_ => new Idle());

        builder.Services.KeepOnlyTelemetryHostedServices();
        using var host = builder.Build();
        var assemblies = host.Services.GetServices<IHostedService>().Select(s => s.GetType().Assembly.GetName().Name).ToList();

        await Assert.That(assemblies).Contains("OpenTelemetry.Extensions.Hosting");
        await Assert.That(assemblies).Contains("Azure.Monitor.OpenTelemetry.Exporter");
        await Assert.That(assemblies).DoesNotContain(typeof(Idle).Assembly.GetName().Name);
    }

    private sealed class Idle : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Marks where the host begins to start: StartingAsync runs before any hosted service's StartAsync.
    private sealed class StartMark(Lines log) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken)
        {
            log.Add(Starting);
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Lines : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly TaskCompletionSource _secrets = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(string line)
        {
            _lines.Enqueue(line);
            if (line.StartsWith("Weesite.Secrets ", StringComparison.Ordinal))
            {
                _secrets.TrySetResult();
            }
        }

        // Once the secrets' line is in, the last of the start's.
        public async Task<List<string>> AfterSecretsAsync()
        {
            await _secrets.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return [.. _lines];
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(Lines owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Add($"{category} {logLevel}: {formatter(state, exception)}");
        }
    }
}
