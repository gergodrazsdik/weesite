var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Weesite_Web>("web")
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
