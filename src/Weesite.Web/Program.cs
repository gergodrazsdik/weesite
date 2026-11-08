using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Weesite.Content;
using Weesite.Search;
using Weesite.ServiceDefaults;
using Weesite.Web;
using Weesite.Web.Admin;
using Weesite.Web.Agents;
using Weesite.Web.Components;
using Weesite.Web.OpenGraph;
using Weesite.Web.Seo;
using Weesite.Web.Theming;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Secrets (the sign-in clients) from the environment's Key Vault, through the site's managed
// identity (ADR 0004). Locally they come from user secrets instead.
if (builder.Configuration["KeyVault:Uri"] is { Length: > 0 } vaultUri)
{
    builder.Configuration.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential());
}

// Content from a directory (tests: Content:Path) or from Blob Storage (Aspire and Azure: the
// "blobs" connection, Entra ID through the managed identity). It loads at startup (ADR 0002).
// The editors and the audit log live in Table Storage beside it ("tables"); with content from
// files they're kept in memory.
var contentPath = builder.Configuration["Content:Path"];
if (!string.IsNullOrWhiteSpace(contentPath))
{
    builder.Services.AddContentFromFiles(contentPath);
    builder.Services.AddSingleton<IEditorStore, MemoryEditorStore>();
    builder.Services.AddSingleton<IAuditLog, MemoryAuditLog>();
}
else
{
    builder.AddAzureBlobServiceClient("blobs");
    builder.AddAzureTableServiceClient("tables");
    builder.Services.AddContentFromBlobs();
    builder.Services.AddSingleton<IEditorStore, TableEditorStore>();
    builder.Services.AddSingleton<IAuditLog, TableAuditLog>();

    // Data Protection keys (sign-in cookies, antiforgery tokens) in Blob Storage, shared by every
    // replica and kept across revisions (F-0031); in the container they'd be lost on each restart.
    builder.Services.AddDataProtection()
        .SetApplicationName("weesite")
        .PersistKeysToAzureBlobStorage(services =>
        {
            var container = services.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("dataprotection");
            container.CreateIfNotExists();
            return container.GetBlobClient("keys.xml");
        });
}

// Search: an in-memory index of the content, built at startup after the content loads.
builder.Services.AddSearch();

// Sign-in for /admin: Microsoft and Google, an allowlist, roles (ADR 0004).
builder.AddEditorSignIn();

// UI strings come from Resources/SharedResource.resx; English only for now (Hungarian later).
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// Static server rendering only: no interactive render modes, no SignalR, no WebAssembly.
builder.Services.AddRazorComponents();

// Open Graph images: generated cards render on first request and stay in memory.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OpenGraphImages>();

// MCP for agents (/mcp): read-only content tools, stateless Streamable HTTP.
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<ContentTools>();

// Absolute URLs: Site:BaseUrl (a deployment's canonical domain) or the request's origin.
builder.Services.AddSingleton<SiteUrls>();

// Container Apps ends TLS at its ingress and passes the original scheme in X-Forwarded-Proto;
// absolute URLs (Open Graph, later canonical links and feeds) need it. The ingress is the only
// way in, so its address isn't checked. Host and client IP headers aren't taken.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Fail at startup, not on the first request, when Site:BaseUrl is malformed.
_ = app.Services.GetRequiredService<SiteUrls>();

app.UseForwardedHeaders();

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
app.UseMarkdownNegotiation();
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseAdminHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapMedia();
app.MapTheme();
OpenGraphImages.Map(app);
app.MapFeeds();
app.MapCrawlers();
app.MapAgentText();
app.MapMcp("/mcp");
app.MapAdminSignIn();
app.MapEditorManagement();
app.MapRazorComponents<App>();
app.MapDefaultEndpoints();

await app.RunAsync();
