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
using Weesite.Web.Agents.OAuth;
using Weesite.Web.Analytics;
using Weesite.Web.Components;
using Weesite.Web.OpenGraph;
using Weesite.Web.Seo;
using Weesite.Web.Theming;

// The re-index job runs from the same image (brief, Bulk re-indexing; T-0069).
if (args is ["reindex", ..])
{
    Environment.ExitCode = await ReindexJob.RunAsync(args[1..]);
    return;
}

// The sample site's seeder (T-0128): `seed <directory>`, run by the seed workflow against test.
if (args is ["seed", _, ..])
{
    Environment.ExitCode = await SeedCommand.RunAsync(args[1], args[2..]);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// One credential for Key Vault, storage and the Data Protection key, so tokens are cached once
// (F-0107). In Azure it's the site's managed identity: infra/app sets AZURE_TOKEN_CREDENTIALS, so
// the credentials before it in the chain aren't tried.
var credential = new DefaultAzureCredential();

// Secrets (the sign-in clients) from the environment's Key Vault, through the site's managed
// identity (ADR 0004). Locally they come from user secrets instead.
if (builder.Configuration["KeyVault:Uri"] is { Length: > 0 } vaultUri)
{
    var loading = System.Diagnostics.Stopwatch.GetTimestamp();
    builder.Configuration.AddAzureKeyVault(new Uri(vaultUri), credential);
    StartupTimes.KeyVault = System.Diagnostics.Stopwatch.GetElapsedTime(loading);
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
    builder.Services.AddOAuthServer(inMemory: true);
    builder.Services.AddPageViews(inMemory: true);
    builder.Services.AddSingleton<IImportStaging, MemoryImportStaging>();
    builder.Services.AddSingleton<ISiteFiles>(new FileSiteFiles(contentPath));
    builder.Services.AddSingleton<InProcessReindexRequests>();
    builder.Services.AddSingleton<IReindexRequests>(services => services.GetRequiredService<InProcessReindexRequests>());
}
else
{
    builder.AddAzureBlobServiceClient("blobs", settings => settings.Credential = credential);
    builder.AddAzureTableServiceClient("tables", settings => settings.Credential = credential);
    builder.Services.AddContentFromBlobs();
    builder.Services.AddSingleton<IEditorStore, TableEditorStore>();
    builder.Services.AddSingleton<IAuditLog, TableAuditLog>();
    builder.Services.AddOAuthServer(inMemory: false);
    builder.Services.AddPageViews(inMemory: false);
    builder.Services.AddSingleton<IImportStaging, BlobImportStaging>();
    builder.Services.AddSingleton<IVectorStore, BlobVectorStore>();
    builder.Services.AddSingleton<ISiteFiles, BlobSiteFiles>();

    // Re-index requests go to the queue that starts the re-index job ("queues").
    builder.AddAzureQueueServiceClient("queues", settings => settings.Credential = credential);
    builder.Services.AddSingleton<IReindexRequests, QueueReindexRequests>();

    // Data Protection keys (sign-in cookies, antiforgery tokens) in Blob Storage, shared by every
    // replica and kept across revisions (F-0031); in the container they'd be lost on each restart.
    // With DataProtection:KeyId (infra/app sets it) new keys are wrapped with that Key Vault key
    // (T-0078); keys written before stay readable.
    builder.Services.AddDataProtection()
        .SetApplicationName("weesite")
        .PersistKeysToAzureBlobStorage(services =>
        {
            var container = services.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("dataprotection");
            container.CreateIfNotExists();
            return container.GetBlobClient("keys.xml");
        })
        .ProtectKeysWithKeyVaultKey(builder.Configuration, credential);
}

// Importing a backup zip (T-0051), and the factory reset (T-0100).
builder.Services.AddSingleton<SiteImport>();
builder.Services.AddSingleton<SiteReset>();

// Search: an in-memory index of the content, built at startup after the content loads.
builder.Services.AddSearch();

// /health reports ready only once the content and the search index are (T-0054).
builder.Services.AddReadinessChecks();

// Sign-in for /admin: Microsoft and Google, an allowlist, roles (ADR 0004).
builder.AddEditorSignIn();

// UI strings come from Resources/PublicResource*.resx (the public site, translated per language)
// and Resources/AdminResource.resx (the admin, English), in the site language's culture (ADR 0008).
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// Static server rendering only: no interactive render modes, no SignalR, no WebAssembly.
builder.Services.AddRazorComponents();

// Letters of every script go out as they are, not as numeric entities ("á", not "&#xE1;"), for
// sites and items in other languages (ADR 0008); markup characters are still escaped.
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(options =>
    options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));

// Open Graph images: generated cards render on first request and stay in memory.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OpenGraphImages>();

// MCP for agents, stateless Streamable HTTP: read-only content tools for anyone at /mcp, and
// the editor tools at /mcp/editor for apps an editor allowed (ADR 0007). Each request gets its
// own options, so each endpoint keeps only its own tools.
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = true;
        options.ConfigureSessionOptions = (context, server, _) =>
        {
            var editor = context.Request.Path.StartsWithSegments(AuthorizationRequests.ResourcePath, StringComparison.OrdinalIgnoreCase);
            AgentIdentity.Describe(server, context, editor);
            foreach (var tool in server.ToolCollection?.ToArray() ?? [])
            {
                if (EditorTools.Names.Contains(tool.ProtocolTool.Name) != editor)
                {
                    server.ToolCollection!.Remove(tool);
                }
            }

            return Task.CompletedTask;
        };
    })
    .WithTools<ContentTools>()
    .WithTools<EditorTools>();

// Absolute URLs: Site:BaseUrl (a deployment's canonical domain) or the request's origin.
builder.Services.AddSingleton<SiteUrls>();

// Container Apps ends TLS at its ingress and passes the original scheme in X-Forwarded-Proto;
// absolute URLs (Open Graph, later canonical links and feeds) need it. The ingress is the only
// way in, so its address isn't checked. Host and client IP headers aren't taken.
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));
builder.Services.AddRateLimits(builder.Configuration);
builder.Services.AddPerformance();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
StartupTimes.LogWhenStarted(app);

// Fail at startup, not on the first request, when Site:BaseUrl is malformed.
_ = app.Services.GetRequiredService<SiteUrls>();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);

    // HTTPS only from now on, for a year (behind the ingress, X-Forwarded-Proto says it's HTTPS).
    app.UseHsts();
}

// www and the canonical domain redirect to the domain (Site:BaseUrl, T-0057).
app.UseWwwRedirect();

// CSP, nosniff, referrer and permissions policies on every response (T-0053).
app.UseSecurityHeaders();

// Per-address limits on search, /mcp and sign-in (T-0053), before anything counts the request.
app.UseRateLimiter();

// Brotli or gzip for dynamic responses outside /admin (T-0056).
app.UsePublicCompression();

// 404 and 410 answers stay out of browser caches, so an item that comes back shows (F-0075).
app.UseUncachedMissingPages();

// Every request in the culture of the site's language (ADR 0008).
app.UseSiteCulture();

// Test and other non-public environments stay out of search engines (Site:NoIndex).
if (app.Configuration.GetValue<bool>("Site:NoIndex"))
{
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        await next(context);
    });
}

// Page views: around everything that serves pages, Markdown for agents included (brief, Analytics).
app.UsePageViews();
app.UseContentRedirects();
app.UseMarkdownNegotiation();
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseAdminHeaders();

// The authorization server's endpoints answer browser-based MCP clients from any origin (ADR 0007).
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Public pages from memory until the content reloads (T-0056); after authentication, so signed-in
// requests skip it.
app.UseOutputCache();

app.UseImmutableFonts();
app.MapStaticAssets();
app.MapMedia();
app.MapTheme();
OpenGraphImages.Map(app);
app.MapFeeds();
app.MapCrawlers();
app.MapLegalLinks();
app.MapSiteIcons();
app.MapAgentText();
app.MapMcp(AuthorizationRequests.ResourcePath).RequireAuthorization(EditorBearerHandler.Policy);
app.MapMcp("/mcp");
app.MapOAuthServer();
app.MapAdminSignIn();
app.MapEditorManagement();
app.MapEditorPreview();
app.MapExport();
app.MapExportBundle();
app.MapBackup();
SiteImport.MapImport(app);
app.MapReindex();
SiteReset.MapSiteReset(app);
Deletion.MapDeletion(app);
app.MapMediaUpload();
app.MapRazorComponents<App>();
app.MapDefaultEndpoints();

await app.RunAsync();
