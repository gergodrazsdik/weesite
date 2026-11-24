using Testcontainers.Azurite;

namespace Weesite.Tests;

/// <summary>
/// The Azurite container the storage tests run against; linked into the test projects that use
/// Testcontainers. The image is pinned here, once.
/// </summary>
internal static class TestAzurite
{
    /// <summary>
    /// The Azurite image, by tag and digest: a tag can be moved, a digest can't. Keep both in step
    /// with the AppHost's emulator (<c>crane digest &lt;image&gt;:&lt;tag&gt;</c> gives the digest).
    /// </summary>
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.37.0@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5";

    /// <summary>Creates a container, not started.</summary>
    /// <returns>The container.</returns>
    /// <remarks>
    /// The Azure Storage SDK sends its newest service version, which Azurite often accepts only a
    /// release later (Azure.Storage.Blobs 12.30 sends 2026-10-06; Azurite 3.37.0 refuses it). The
    /// tests don't depend on what a new version adds, so Azurite skips the check.
    /// </remarks>
    public static AzuriteContainer Create() =>
        new AzuriteBuilder(Image).WithCommand("--skipApiVersionCheck").Build();
}
