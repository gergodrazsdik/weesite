using Weesite.Content;
using Weesite.ServiceDefaults;
using Weesite.Web;
using Weesite.Web.Components;
using Weesite.Web.Theming;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Content from a directory (tests: Content:Path) or from Blob Storage (Aspire and Azure: the
// "blobs" connection, Entra ID through the managed identity). It loads at startup (ADR 0002).
var contentPath = builder.Configuration["Content:Path"];
if (!string.IsNullOrWhiteSpace(contentPath))
{
    builder.Services.AddContentFromFiles(contentPath);
}
else
{
    builder.AddAzureBlobServiceClient("blobs");
    builder.Services.AddContentFromBlobs();
}

// UI strings come from Resources/SharedResource.resx; English only for now (Hungarian later).
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// Static server rendering only: no interactive render modes, no SignalR, no WebAssembly.
builder.Services.AddRazorComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("en")
    .AddSupportedCultures("en")
    .AddSupportedUICultures("en"));

// Test and other non-public environments stay out of search engines (Site:NoIndex).
if (app.Configuration.GetValue<bool>("Site:NoIndex"))
{
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        await next(context);
    });
}

app.UseContentRedirects();
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseAntiforgery();

app.MapStaticAssets();
app.MapMedia();
app.MapTheme();
app.MapRazorComponents<App>();
app.MapDefaultEndpoints();

await app.RunAsync();
