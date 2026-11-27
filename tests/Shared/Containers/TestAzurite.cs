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
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.38.0@sha256:c26024d62bf4a5b88a40968afd217234c00e1fc9a4790dc6105d1ca7979f6eec";

    /// <summary>Creates a container, not started.</summary>
    /// <returns>The container.</returns>
    /// <remarks>
    /// The Azure Storage SDKs send their newest service version, which Azurite often accepts only a
    /// release later (Azure.Storage.Blobs 12.30 sends 2026-10-06, which Azurite 3.37.0 refused and
    /// 3.38.0 knows). The tests don't depend on what a new version adds, so Azurite skips the check.
    /// </remarks>
    public static AzuriteContainer Create() =>
        new AzuriteBuilder(Image).WithCommand("--skipApiVersionCheck").Build();
}
