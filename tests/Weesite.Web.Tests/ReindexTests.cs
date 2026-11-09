using System.Net;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Content;
using Weesite.Content.Sources;
using Weesite.Search;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Re-embed all and the re-index job (T-0069).</summary>
internal sealed partial class ReindexTests
{
    [Test]
    public async Task Reembed_all_is_for_admins_and_without_a_queue_runs_in_the_web_app()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory);
        using var author = Client(factory, EditorRole.Author);

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var empty = new FormUrlEncodedContent([]);
        using var withoutToken = await admin.PostAsync(new Uri("/admin/search/reindex", UriKind.Relative), empty);
        using var asAuthor = await PostFormAsync(author, "/admin/search/reindex", page);
        using var requested = await PostFormAsync(admin, "/admin/search/reindex", page);
        await factory.Services.GetRequiredService<InProcessReindexRequests>().Last;

        await Assert.That(page).Contains("Re-embed all");
        await Assert.That(withoutToken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(asAuthor.Headers.Location?.AbsolutePath).IsEqualTo("/admin/denied");
        await Assert.That(requested.Headers.Location?.OriginalString).IsEqualTo("/admin/settings?reindex=1");
        var model = factory.Services.GetRequiredService<IEmbedder>().ModelId;
        var stored = await factory.Services.GetRequiredService<IVectorStore>().ListAsync(model, CancellationToken.None);
        var content = factory.Services.GetRequiredService<ContentCatalogProvider>().Current;
        await Assert.That(stored.Count).IsEqualTo(content.Posts.Count + content.Pages.Count);
        await Assert.That((await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None)).Select(e => e.Action)).Contains("reindex-requested");
        await Assert.That(await admin.GetStringAsync(new Uri("/admin/settings?reindex=1", UriKind.Relative))).Contains("Re-embedding requested.");
    }

    [Test]
    public async Task Past_the_embed_limit_chunks_wait_for_the_job_and_one_request_is_sent()
    {
        var content = new ContentCatalogProvider();
        string[] slugs = ["one", "two", "three"];
        content.Set(ContentCatalog.Build(SiteSettings.Empty, slugs.Select(slug =>
            new ParseResult(new ContentItem(ContentKind.Post, slug, slug, "en", $"Text of {slug}.", $"posts/{slug}.md") { Date = new DateOnly(2026, 1, 1) }, null))));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new("Search:EmbedLimit", "1")]).Build();
        var requests = new CountingRequests();
        using var provider = new SearchIndexProvider(content, new HashEmbedder(), store: new MemoryVectorStore(), configuration: configuration, reindex: requests);

        var index = provider.Current;
        content.Set(ContentCatalog.Build(SiteSettings.Empty, content.Current.Posts.Select(p => new ParseResult(p, null))));
        _ = provider.Current;
        await requests.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(index.EmbeddedCount).IsEqualTo(1);
        await Assert.That(index.MissingCount).IsEqualTo(2);
        await Assert.That(provider.Current.MissingCount).IsEqualTo(1);
        await Assert.That(requests.Count).IsEqualTo(1);
    }

    [Test]
    [Category("containers")]
    public async Task The_job_takes_its_requests_embeds_everything_and_removes_stale_vectors()
    {
        await using var azurite = new Testcontainers.Azurite.AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").Build();
        await azurite.StartAsync();
        var connection = azurite.GetConnectionString();
        var blobs = new BlobServiceClient(connection);
        var queues = new QueueServiceClient(connection);
        await new ContentSeeder(blobs, NullLogger<ContentSeeder>.Instance).SeedAsync(SiteFactory.SeedPath, CancellationToken.None);
        var store = new BlobVectorStore(blobs);
        var model = $"{OnnxEmbedder.ModelName}-int8";
        await store.SaveAsync(model, "post/gone", new Dictionary<string, float[]> { ["x"] = [1f] }, CancellationToken.None);
        await new QueueReindexRequests(queues, TimeProvider.System).RequestAsync("test", CancellationToken.None);

        var exit = await ReindexJob.RunAsync([$"--ConnectionStrings:blobs={connection}", $"--ConnectionStrings:queues={connection}"]);

        await Assert.That(exit).IsEqualTo(0);
        var stored = await store.ListAsync(model, CancellationToken.None);
        await Assert.That(stored).DoesNotContain("post/gone");
        await Assert.That(stored).Contains("page/about");
        await Assert.That(stored.Count(k => k.StartsWith("post/", StringComparison.Ordinal))).IsGreaterThan(10);
        var waiting = await queues.GetQueueClient(QueueReindexRequests.QueueName).PeekMessagesAsync(32);
        await Assert.That(waiting.Value).IsEmpty();
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string page)
    {
        var token = Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role = EditorRole.Admin)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private sealed class CountingRequests : IReindexRequests
    {
        private int _count;

        public int Count => _count;

        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestAsync(string requestedBy, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            Sent.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
