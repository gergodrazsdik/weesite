using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Weesite.Web.Tests;

/// <summary>
/// The UI strings (ADR 0008): the public site's in <c>PublicResource</c>, translated per language,
/// the admin's in <c>AdminResource</c>. A key that's missing renders as its own name, so these
/// check the files against each other and against the components.
/// </summary>
internal sealed partial class ResourceTests
{
    private static readonly string Web = Path.Combine(SiteFactory.SeedPath, "..", "src", "Weesite.Web");

    [Test]
    [Arguments("hu")]
    public async Task A_translation_has_every_public_key_with_the_same_placeholders(string language)
    {
        var english = Strings("PublicResource.resx");
        var translated = Strings($"PublicResource.{language}.resx");

        await Assert.That(translated.Keys.Order()).IsEquivalentTo(english.Keys.Order());
        foreach (var (key, text) in english)
        {
            await Assert.That(Placeholders(translated[key])).IsEquivalentTo(Placeholders(text)).Because(key);
            await Assert.That(translated[key]).IsNotEmpty().Because(key);
        }
    }

    [Test]
    public async Task Every_key_a_component_uses_is_in_its_resource_file()
    {
        var files = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["PublicResource"] = Strings("PublicResource.resx"),
            ["AdminResource"] = Strings("AdminResource.resx"),
        };
        var missing = new List<string>();
        foreach (var component in Directory.EnumerateFiles(Path.Combine(Web, "Components"), "*.razor", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(component);
            if (Localizer().Match(text) is not { Success: true } match)
            {
                continue;
            }

            var strings = files[match.Groups[1].Value];
            missing.AddRange(Key().Matches(text).Select(m => m.Groups[1].Value)
                .Where(key => !strings.ContainsKey(key))
                .Select(key => $"{Path.GetFileName(component)}: {key}"));
        }

        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task A_hungarian_site_shows_hungarian_and_its_admin_stays_english()
    {
        using var copy = new SiteCopy();
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"language\": \"en\",", "\"language\": \"hu\",", StringComparison.Ordinal));
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var reader = factory.CreateClient();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        var home = await reader.GetStringAsync(new Uri("/", UriKind.Relative));
        var post = await reader.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var posts = await admin.GetStringAsync(new Uri("/admin/posts", UriKind.Relative));

        await Assert.That(home).Contains("Legújabb bejegyzések");
        await Assert.That(home).Contains("Ugrás a tartalomhoz");
        await Assert.That(post).Contains("1 perc olvasás");
        await Assert.That(post).Contains("data-copy=\"Másolás\"");
        await Assert.That(posts).Contains("<h1>Posts</h1>");
        await Assert.That(posts).Contains("New post");
    }

    private static Dictionary<string, string> Strings(string file) =>
        XDocument.Load(Path.Combine(Web, "Resources", file)).Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? "", StringComparer.Ordinal);

    private static string[] Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"IStringLocalizer<(PublicResource|AdminResource)>")]
    private static partial Regex Localizer();

    // Literal keys only (T["Key"] or T["Key", ...]), not prefixes (T["Token_" + name]); keys built
    // at run time are checked by the pages' tests.
    [GeneratedRegex(@"\bT\[""([A-Za-z][A-Za-z0-9_]*)""(?!\s*\+)")]
    private static partial Regex Key();
}
