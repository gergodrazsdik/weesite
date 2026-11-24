using System.Text.RegularExpressions;
using Weesite.Tests;

namespace Weesite.Web.Tests;

/// <summary>The container images pinned in code, which no bot updates (F-0244).</summary>
internal sealed partial class ContainerImageTests
{
    [Test]
    public async Task The_tests_and_the_apphost_run_the_same_azurite()
    {
        var appHost = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "src", "Weesite.AppHost", "AppHost.cs"));
        var match = AppHostEmulator().Match(appHost);

        await Assert.That(match.Success).IsTrue();
        await Assert.That(TestAzurite.Image).IsEqualTo($"mcr.microsoft.com/azure-storage/azurite:{match.Groups["tag"].Value}@sha256:{match.Groups["digest"].Value}");
    }

    // A tag can be moved to another image; a digest can't (T-0177).
    [Test]
    [Arguments(TestAzurite.Image)]
    [Arguments(EmailTests.MailpitImage)]
    public async Task Test_images_are_pinned_by_digest(string image) =>
        await Assert.That(PinnedByDigest().IsMatch(image)).IsTrue();

    [GeneratedRegex("""RunAsEmulator\(.*?WithImageTag\("(?<tag>[^"]+)"\)\s*\.WithImageSHA256\("(?<digest>[0-9a-f]{64})"\)""", RegexOptions.Singleline)]
    private static partial Regex AppHostEmulator();

    [GeneratedRegex("^[^@]+:[^@:/]+@sha256:[0-9a-f]{64}$")]
    private static partial Regex PinnedByDigest();
}
