using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weesite.Seed;
using Weesite.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureBlobServiceClient("blobs");

using var host = builder.Build();

var source = builder.Configuration["Seed:Path"]
    ?? throw new InvalidOperationException("Set Seed:Path (Seed__Path) to the seed directory.");
var blobs = host.Services.GetRequiredService<BlobServiceClient>();
var logger = host.Services.GetRequiredService<ILogger<ContentSeeder>>();

await new ContentSeeder(blobs, logger).SeedAsync(source, CancellationToken.None);
