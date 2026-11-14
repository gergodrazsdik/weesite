using Testcontainers.Azurite;

namespace Weesite.Tests;

/// <summary>
/// The Azurite container the storage tests run against; linked into the test projects that use
/// Testcontainers. The image is pinned here, once.
/// </summary>
internal static class TestAzurite
{
    /// <summary>The Azurite image; keep in step with the AppHost's emulator tag.</summary>
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.37.0";

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
