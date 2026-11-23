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
        var match = AppHostEmulatorTag().Match(appHost);

        await Assert.That(match.Success).IsTrue();
        await Assert.That(TestAzurite.Image).IsEqualTo($"mcr.microsoft.com/azure-storage/azurite:{match.Groups["tag"].Value}");
    }

    [GeneratedRegex("""RunAsEmulator\(.*WithImageTag\("(?<tag>[^"]+)"\)""")]
    private static partial Regex AppHostEmulatorTag();
}
