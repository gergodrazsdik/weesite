using Microsoft.Extensions.Configuration;

namespace Weesite.Web.Tests;

/// <summary>appsettings.example.json: the commented reference of every setting.</summary>
internal sealed partial class AppSettingsExampleTests
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
    [Arguments("Search:EmbeddingModel")]
    [Arguments("Search:ModelPath")]
    [Arguments("Search:MinSimilarity")]
    [Arguments("Search:MinLead")]
    [Arguments("Search:EmbedLimit")]
    [Arguments("RateLimits:SearchPerMinute")]
    [Arguments("RateLimits:McpPerMinute")]
    [Arguments("RateLimits:SignInPerMinute")]
    [Arguments("RateLimits:McpEditorPerMinute")]
    [Arguments("RateLimits:OAuthPerMinute")]
    [Arguments("Email:From")]
    [Arguments("Email:Smtp:Host")]
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
    public async Task Every_setting_read_in_the_code_is_described()
    {
        // F-0118: the list above is written by hand, and missed the email settings. Every key the
        // code reads by name (configuration["…"], GetValue("…")) must be in the example.
        var root = Path.GetFullPath(Path.Combine(SiteFactory.SeedPath, "..", "src"));
        var keys = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => SettingRead().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
            .Where(k => k.Contains(':', StringComparison.Ordinal) || k.Contains('_', StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var configuration = new ConfigurationBuilder().AddJsonFile(ExamplePath, optional: false).Build();

        await Assert.That(keys).Contains("Email:Smtp:Host");
        await Assert.That(keys.Where(k => !configuration.GetSection(k).Exists())).IsEmpty();
    }

    [System.Text.RegularExpressions.GeneratedRegex("(?:(?:configuration|Configuration)\\??\\[|GetValue(?:<[^>]+>)?\\()\"(?<key>[A-Za-z_.]+(?::[A-Za-z_.]+)*)\"")]
    private static partial System.Text.RegularExpressions.Regex SettingRead();

    [Test]
    public async Task The_defaults_in_the_example_match_the_code()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(ExamplePath, optional: false).Build();

        await Assert.That(configuration.GetValue<int>("Search:EmbedLimit")).IsEqualTo(Weesite.Search.SearchIndexProvider.DefaultEmbedLimit);
        await Assert.That(configuration.GetValue<float>("Search:MinSimilarity")).IsEqualTo(Weesite.Search.HybridSearch.DefaultMinSimilarity);
        await Assert.That(configuration.GetValue<float>("Search:MinLead")).IsEqualTo(Weesite.Search.HybridSearch.DefaultMinLead);
        await Assert.That(configuration["Logging:LogLevel:Azure.Core"]).IsEqualTo("Error");
    }
}
