// Local development: `aspire run` (or `just aspire`) starts Azurite and the web app, which
// loads the sample site from seed/ into it at startup (Seed__Path) before reading it.
// Deployment is OpenTofu and GitHub Actions, not Aspire.
var builder = DistributedApplication.CreateBuilder(args);

// Azurite 3.38, told not to check the API version (as tests/Shared/Containers/TestAzurite.cs does):
// the Storage SDKs send their newest service version, which Azurite often learns a release later
// (3.38 learnt the Blob 2026-10-06 that Azure.Storage.Blobs 12.30 sends). Pinned by digest as
// well, which a moved tag can't change.
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator
        .WithImageTag("3.38.0")
        .WithImageSHA256("c26024d62bf4a5b88a40968afd217234c00e1fc9a4790dc6105d1ca7979f6eec")
        .WithArgs("--skipApiVersionCheck"));
var blobs = storage.AddBlobs("blobs");
var tables = storage.AddTables("tables");
var queues = storage.AddQueues("queues");

var seedPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "seed"));

// The web project by path (Aspire 17's AddDotnetProject; AddProject<Projects.*> is obsolete).
// Relative to the AppHost directory. Aspire builds it before starting it.
const string WebProject = "../Weesite.Web/Weesite.Web.csproj";

// Its default launch profile ("http", port 5080) gives the endpoint.
var web = builder.AddDotnetProject("web", WebProject)
    .WithReference(blobs)
    .WithReference(tables)
    .WithReference(queues)
    .WaitFor(blobs)
    .WithEnvironment("Seed__Path", seedPath)
    .WithHttpHealthCheck("/health");

// The country database for the analytics, when one was downloaded for local runs (`just
// local-geoip`, which `just aspire` and `just uat` pass on as Analytics__CountryDatabase).
if (builder.Configuration["Analytics:CountryDatabase"] is { Length: > 0 } countries)
{
    web.WithEnvironment("Analytics__CountryDatabase", countries);
}

// The re-index job (T-0069): the same project started with `reindex`, on demand from the
// dashboard, as Container Apps starts it from the queue. "Re-embed all" in the admin queues a
// request; start this resource to process it. No launch profile: it serves nothing.
builder.AddDotnetProject("reindex", WebProject, options => options.ExcludeLaunchProfile = true)
    .WithArgs("reindex")
    .WithReference(blobs)
    .WithReference(queues)
    .WaitFor(web)
    .WithExplicitStart();

await builder.Build().RunAsync();
