// Local development: `aspire run` (or `just aspire`) starts Azurite, loads the sample site from
// seed/ into it, then starts the web app. Deployment is OpenTofu and GitHub Actions, not Aspire.
var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var blobs = storage.AddBlobs("blobs");
var tables = storage.AddTables("tables");
var queues = storage.AddQueues("queues");

var seedPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "seed"));
var seed = builder.AddProject<Projects.Weesite_Seed>("seed")
    .WithReference(blobs)
    .WaitFor(blobs)
    .WithEnvironment("Seed__Path", seedPath);

builder.AddProject<Projects.Weesite_Web>("web")
    .WithReference(blobs)
    .WithReference(tables)
    .WithReference(queues)
    .WaitForCompletion(seed)
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
