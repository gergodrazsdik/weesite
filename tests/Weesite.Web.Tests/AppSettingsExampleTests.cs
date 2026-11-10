using Microsoft.Extensions.Configuration;

namespace Weesite.Web.Tests;

/// <summary>appsettings.example.json: the commented reference of every setting.</summary>
internal sealed class AppSettingsExampleTests
{
    private static readonly string ExamplePath =
        Path.GetFullPath(Path.Combine(SiteFactory.SeedPath, "..", "src", "Weesite.Web", "appsettings.example.json"));

    [Test]
    [Arguments("Content:Path")]
    [Arguments("Content:ReloadSeconds")]
    [Arguments("Seed:Path")]
    [Arguments("Site:BaseUrl")]
    [Arguments("Site:NoIndex")]
    [Arguments("Site:Admins")]
    [Arguments("Authentication:Microsoft:ClientId")]
    [Arguments("Authentication:Microsoft:ClientSecret")]
    [Arguments("Authentication:Microsoft:TrustedTenants")]
    [Arguments("Authentication:Google:ClientId")]
    [Arguments("Authentication:Google:ClientSecret")]
    [Arguments("KeyVault:Uri")]
    [Arguments("DataProtection:KeyId")]
    [Arguments("Search:ModelPath")]
    [Arguments("Search:MinSimilarity")]
    [Arguments("Search:EmbedLimit")]
    [Arguments("RateLimits:SearchPerMinute")]
    [Arguments("RateLimits:McpPerMinute")]
    [Arguments("RateLimits:SignInPerMinute")]
    [Arguments("Analytics:Enabled")]
    [Arguments("Telemetry:SamplingRatio")]
    [Arguments("APPLICATIONINSIGHTS_CONNECTION_STRING")]
    [Arguments("OTEL_EXPORTER_OTLP_ENDPOINT")]
    [Arguments("ConnectionStrings:blobs")]
    [Arguments("Logging:LogLevel:Azure.Core")]
    public async Task Every_setting_the_app_reads_is_described(string key)
    {
        // The configuration reader accepts the comments; a key it can't find isn't documented.
        var configuration = new ConfigurationBuilder().AddJsonFile(ExamplePath, optional: false).Build();

        await Assert.That(configuration.GetSection(key).Exists()).IsTrue();
    }

    [Test]
    public async Task The_defaults_in_the_example_match_the_code()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(ExamplePath, optional: false).Build();

        await Assert.That(configuration.GetValue<int>("Search:EmbedLimit")).IsEqualTo(Weesite.Search.SearchIndexProvider.DefaultEmbedLimit);
        await Assert.That(configuration.GetValue<float>("Search:MinSimilarity")).IsEqualTo(Weesite.Search.HybridSearch.DefaultMinSimilarity);
        await Assert.That(configuration["Logging:LogLevel:Azure.Core"]).IsEqualTo("Error");
    }
}
