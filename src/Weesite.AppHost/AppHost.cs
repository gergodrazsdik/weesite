// Local development: `aspire run` (or `just aspire`) starts Azurite and the web app, which
// loads the sample site from seed/ into it at startup (Seed__Path) before reading it.
// Deployment is OpenTofu and GitHub Actions, not Aspire.
var builder = DistributedApplication.CreateBuilder(args);

// Azurite 3.37 or later: Azure.Storage.Blobs 12.29 uses storage API version 2026-06-06, which
// Aspire's default Azurite tag (3.35) rejects.
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithImageTag("3.37.0"));
var blobs = storage.AddBlobs("blobs");
var tables = storage.AddTables("tables");
var queues = storage.AddQueues("queues");

var seedPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "seed"));

var web = builder.AddProject<Projects.Weesite_Web>("web")
    .WithReference(blobs)
    .WithReference(tables)
    .WithReference(queues)
    .WaitFor(blobs)
    .WithEnvironment("Seed__Path", seedPath)
    .WithHttpHealthCheck("/health");

// The re-index job (T-0069): the same project started with `reindex`, on demand from the
// dashboard, as Container Apps starts it from the queue. "Re-embed all" in the admin queues a
// request; start this resource to process it.
builder.AddProject<Projects.Weesite_Web>("reindex", launchProfileName: null)
    .WithArgs("reindex")
    .WithReference(blobs)
    .WithReference(queues)
    .WaitFor(web)
    .WithExplicitStart();

await builder.Build().RunAsync();
