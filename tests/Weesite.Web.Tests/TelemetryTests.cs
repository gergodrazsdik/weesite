using System.Net;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Weesite.ServiceDefaults;

namespace Weesite.Web.Tests;

/// <summary>Telemetry to Application Insights (T-0054).</summary>
internal sealed class TelemetryTests
{
    // A connection string whose ingestion endpoint refuses connections: nothing leaves the machine.
    private const string ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://127.0.0.1:9/";

    [Test]
    public async Task A_connection_string_turns_on_the_exporter_with_a_quarter_of_the_traces()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder => builder.UseSetting("APPLICATIONINSIGHTS_CONNECTION_STRING", ConnectionString));
        using var client = factory.CreateClient();

        using var home = await client.GetAsync(new Uri("/", UriKind.Relative));
        var options = factory.Services.GetRequiredService<IOptionsMonitor<AzureMonitorOptions>>().Get(Options.DefaultName);

        await Assert.That(home.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(options.SamplingRatio).IsEqualTo(0.25f);
    }

    // Application Insights takes telemetry only through Entra ID (T-0177). The host isn't started,
    // so nothing tries to sign in or send.
    [Test]
    public async Task The_exporter_sends_with_the_managed_identity()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ConnectionString;
        builder.Configuration["AZURE_CLIENT_ID"] = "11111111-1111-1111-1111-111111111111";
        builder.AddServiceDefaults();
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptionsMonitor<AzureMonitorOptions>>().Get(Options.DefaultName);

        await Assert.That(options.Credential).IsTypeOf<ManagedIdentityCredential>();
    }
}
