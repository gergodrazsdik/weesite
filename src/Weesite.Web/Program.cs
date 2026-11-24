using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
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
using Weesite.Web.Visitors;

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

// The tests run in Development, which reads this machine's user secrets: `just local-signin` puts
// test's sign-in there. They set Testing:IgnoreUserSecrets, so the app never sees them (F-0229).
if (builder.Configuration.GetValue("Testing:IgnoreUserSecrets", false))
{
    foreach (var secrets in builder.Configuration.Sources.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>().Where(s => s.Path == "secrets.json").ToList())
    {
        builder.Configuration.Sources.Remove(secrets);
    }
}

// No "Server: Kestrel" header: it tells nobody anything useful (T-0122).
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

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
    builder.Services.AddVisitorInput(builder.Configuration, inMemory: true);
    builder.Services.AddDigests(inMemory: true);
    builder.Services.AddContactForm(inMemory: true);
    builder.Services.AddReactions(inMemory: true);
    builder.Services.AddComments(inMemory: true);
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
    builder.Services.AddVisitorInput(builder.Configuration, inMemory: false);
    builder.Services.AddDigests(inMemory: false);
    builder.Services.AddContactForm(inMemory: false);
    builder.Services.AddReactions(inMemory: false);
    builder.Services.AddComments(inMemory: false);
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
    builder.Services.AddHostedService<UnwrappedKeyRevoker>();
}

// Importing a backup zip (T-0051), and the factory reset (T-0100).
builder.Services.AddSingleton<SiteImport>();
builder.Services.AddSingleton<ResetStores>();
builder.Services.AddSingleton<SiteReset>();
builder.Services.AddSingleton<RenderGate>();

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
    .WithTools<EditorTools>()
    .WithTools<CommentTools>();

// Absolute URLs: Site:BaseUrl (a deployment's canonical domain) or the request's origin.
builder.Services.AddSingleton<SiteUrls>();
builder.Services.AddIndexNow();
builder.Services.AddSingleton<ScheduledPublishing>();
builder.Services.AddHostedService(s => s.GetRequiredService<ScheduledPublishing>());

// Container Apps ends TLS at its ingress and passes the original scheme in X-Forwarded-Proto and
// the client's address in X-Forwarded-For; absolute URLs need the one, the limits the other. Which
// proxies are trusted is ForwardedHeaders:* (ClientAddress). The Host header isn't taken.
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));
builder.Services.AddRateLimits(builder.Configuration);
builder.Services.AddPerformance();
builder.Services.AddClientAddress(builder.Configuration);

var app = builder.Build();
StartupTimes.LogWhenStarted(app);

// Fail at startup, not on the first request, when Site:BaseUrl is malformed, and say so outside
// local runs when it isn't set (T-0177).
_ = app.Services.GetRequiredService<SiteUrls>();
SiteUrls.WarnWithoutBaseUrl(app.Configuration, app.Environment, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.Startup"));

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

// What visitors send is small: larger bodies are refused before they're read.
app.UseVisitorBodyLimit();
app.UseProtocolBodyLimit();

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
app.UseEditorMarker();

// The authorization server's endpoints and the editor MCP endpoint answer browser-based MCP
// clients from any origin, without credentials (ADR 0007).
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Kept explicit, though ASP.NET Core 11 adds cross-origin CSRF protection by default: since 11,
// Blazor's forms carry antiforgery tokens only when this middleware runs, and the endpoints that
// take a form outside Blazor (the backup import, media uploads) validate those tokens (T-0122).
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
app.MapIndexNowKey();
app.MapLegalLinks();
app.MapSiteIcons();
app.MapAgentText();
app.MapVisitorChallenge();
app.MapReactions();
app.MapComments();
app.MapMcp(AuthorizationRequests.ResourcePath).RequireAuthorization(EditorBearerHandler.Policy).RequireCors(OAuthEndpoints.McpCorsPolicy);
app.MapMcp("/mcp");
MediaUploads.Map(app);
app.MapOAuthServer();
app.MapAdminSignIn();
MessageEndpoints.MapMessages(app);
CommentModeration.MapCommentModeration(app);
app.MapEditorManagement();
app.MapEditorPreview();
app.MapEditorState();
app.MapExport();
app.MapExportBundle();
app.MapBackup();
SiteImport.MapImport(app);
app.MapReindex();
SiteReset.MapSiteReset(app);
Deletion.MapDeletion(app);
app.MapMediaUpload();
app.MapAuditExport();
app.MapPostReport();
app.MapAnalyticsReport();
app.MapRazorComponents<App>();
app.MapDefaultEndpoints();

await app.RunAsync();
