using System.Globalization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Weesite.Content;
using Weesite.Content.Sources;
using Weesite.Search;
using Weesite.Web.Agents.OAuth;
using Weesite.Web.Analytics;
using Weesite.Web.Visitors;

namespace Weesite.Web.Admin;

/// <summary>What the reset can delete (T-0167), in the order the reset page lists them.</summary>
internal enum ResetPart
{
    /// <summary>Posts and pages.</summary>
    Content,

    /// <summary>Media files.</summary>
    Media,

    /// <summary>Site settings: name, menu, theme, tags, author profiles.</summary>
    Settings,

    /// <summary>The search index's stored embeddings.</summary>
    SearchIndex,

    /// <summary>Comments.</summary>
    Comments,

    /// <summary>Reaction counts and events.</summary>
    Reactions,

    /// <summary>Contact messages.</summary>
    Messages,

    /// <summary>Page views (analytics).</summary>
    Analytics,

    /// <summary>Editors added in the admin; the admins from the deployment always stay.</summary>
    Editors,

    /// <summary>Connected apps: OAuth clients and grants.</summary>
    ConnectedApps,

    /// <summary>The audit log.</summary>
    AuditLog,

    /// <summary>The counters against floods: hourly limits and used proof-of-work challenges.</summary>
    SpamCounters,

    /// <summary>Uploaded backups not imported yet.</summary>
    Imports,
}

/// <summary>The reset's parts: their keys in the form, and which are ticked at first.</summary>
internal static class ResetParts
{
    /// <summary>Gets every part, in the page's order.</summary>
    public static IReadOnlyList<ResetPart> All { get; } = Enum.GetValues<ResetPart>();

    /// <summary>Gets the parts ticked at first: what the seed or a backup puts back (the maintainer, 2026-11-20).</summary>
    public static IReadOnlySet<ResetPart> Defaults { get; } = new HashSet<ResetPart> { ResetPart.Content, ResetPart.Media, ResetPart.Settings, ResetPart.SearchIndex };

    /// <summary>A part's key in the form and in addresses.</summary>
    /// <param name="part">The part.</param>
    /// <returns>Such as <c>search-index</c>.</returns>
    public static string Key(ResetPart part) => part switch
    {
        ResetPart.Content => "content",
        ResetPart.Media => "media",
        ResetPart.Settings => "settings",
        ResetPart.SearchIndex => "search-index",
        ResetPart.Comments => "comments",
        ResetPart.Reactions => "reactions",
        ResetPart.Messages => "messages",
        ResetPart.Analytics => "analytics",
        ResetPart.Editors => "editors",
        ResetPart.ConnectedApps => "connected-apps",
        ResetPart.AuditLog => "audit-log",
        ResetPart.SpamCounters => "spam-counters",
        ResetPart.Imports => "imports",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    /// <summary>Reads keys back into parts, ignoring unknown ones.</summary>
    /// <param name="keys">The keys.</param>
    /// <returns>The parts.</returns>
    public static IReadOnlySet<ResetPart> Parse(IEnumerable<string?>? keys) =>
        (keys ?? []).SelectMany(k => All.Where(p => Key(p) == k)).ToHashSet();
}

/// <summary>Deletes the site's files: content, media, settings and the search index's files.</summary>
internal interface ISiteFiles
{
    /// <summary>Deletes every file in some of the site's containers.</summary>
    /// <param name="containers">The containers, such as <c>content</c>.</param>
    /// <param name="cancellationToken">Stops the deletion.</param>
    /// <returns>How many files were deleted.</returns>
    Task<int> DeleteAsync(IReadOnlyCollection<string> containers, CancellationToken cancellationToken);
}

/// <summary>
/// In Blob Storage: every blob of the containers asked for, but the settings container's version
/// marker, which the replicas watch to reload.
/// </summary>
/// <param name="blobs">The blob service.</param>
internal sealed class BlobSiteFiles(BlobServiceClient blobs) : ISiteFiles
{
    /// <inheritdoc />
    public async Task<int> DeleteAsync(IReadOnlyCollection<string> containers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containers);
        var deleted = 0;
        foreach (var name in containers)
        {
            var container = blobs.GetBlobContainerClient(name);
            if (!await container.ExistsAsync(cancellationToken))
            {
                continue;
            }

            await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, null, cancellationToken))
            {
                if (name == ContentContainers.Settings && blob.Name == ContentContainers.VersionFile)
                {
                    continue;
                }

                if ((await container.DeleteBlobIfExistsAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)).Value)
                {
                    deleted++;
                }
            }
        }

        return deleted;
    }
}

/// <summary>
/// In a directory (tests): every file under the folders asked for (<c>content</c>, <c>media</c>,
/// <c>settings</c>). Refuses a directory inside a git working tree, so it can never empty the
/// repository's sample site.
/// </summary>
/// <param name="root">The content root.</param>
internal sealed class FileSiteFiles(string root) : ISiteFiles
{
    /// <inheritdoc />
    public Task<int> DeleteAsync(IReadOnlyCollection<string> containers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containers);
        for (var directory = new DirectoryInfo(Path.GetFullPath(root)); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                throw new InvalidOperationException($"Won't delete the files of {root}: it's inside a git working tree.");
            }
        }

        var deleted = 0;
        foreach (var name in containers)
        {
            var folder = Path.Combine(root, name);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(file);
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }
}

/// <summary>The site's own stores the reset clears, beside its files.</summary>
/// <param name="Comments">The comments.</param>
/// <param name="CommentCatalog">The published comments, cached.</param>
/// <param name="Reactions">The reactions.</param>
/// <param name="Messages">The contact messages.</param>
/// <param name="PageViews">The page views.</param>
/// <param name="Editors">The editors added in the admin.</param>
/// <param name="OAuth">The connected apps.</param>
/// <param name="Audit">The audit log.</param>
/// <param name="Limits">The counters against floods.</param>
/// <param name="Imports">The uploaded backups.</param>
/// <param name="Analytics">The analytics' reads, which keep finished days' reaction events in memory.</param>
/// <param name="Reindex">The re-embedding requests.</param>
/// <param name="Time">The clock, for today's salt.</param>
internal sealed record ResetStores(
    ICommentStore Comments,
    CommentCatalog CommentCatalog,
    IReactionStore Reactions,
    IMessageStore Messages,
    IPageViewStore PageViews,
    IEditorStore Editors,
    IOAuthStore OAuth,
    IAuditLog Audit,
    IVisitorStore Limits,
    IImportStaging Imports,
    AnalyticsData Analytics,
    IReindexRequests Reindex,
    TimeProvider Time);

/// <summary>How far a reset got: what each finished part deleted, and the part it's on (F-0221).</summary>
internal sealed class ResetProgress
{
    /// <summary>Gets how many items each finished part had.</summary>
    public Dictionary<ResetPart, int> Done { get; } = [];

    /// <summary>Gets or sets the part being deleted; null between parts and once they're all done.</summary>
    public ResetPart? Current { get; set; }

    /// <summary>The audit entry's target: each finished part and its count, then the part it stopped at, if it failed.</summary>
    /// <param name="failed">Whether the reset failed.</param>
    /// <returns>Such as <c>content 3, media 2; failed at analytics</c>.</returns>
    public string Summary(bool failed)
    {
        var summary = string.Join(", ", ResetParts.All.Where(Done.ContainsKey).Select(p => string.Create(CultureInfo.InvariantCulture, $"{ResetParts.Key(p)} {Done[p]}")));
        if (!failed)
        {
            return summary;
        }

        var stop = Current is { } part ? "failed at " + ResetParts.Key(part) : "failed";
        return summary.Length == 0 ? stop : summary + "; " + stop;
    }
}

/// <summary>
/// The reset (T-0100, its own page since T-0167): deletes the parts an admin ticks, then makes every
/// replica reload. By default what the seed or a backup puts back: content, media, settings and the
/// search index. What visitors sent, the analytics, added editors, connected apps, the audit log and
/// the technical rest only when ticked. Data Protection keys and the country data are never offered.
/// </summary>
/// <param name="files">The site's files.</param>
/// <param name="vectors">The stored vectors.</param>
/// <param name="embedder">The embedding model, whose vectors are stored.</param>
/// <param name="store">The content store, to bump its version.</param>
/// <param name="reloader">Reloads every replica.</param>
/// <param name="stores">The other stores.</param>
internal sealed partial class SiteReset(ISiteFiles files, IVectorStore vectors, IEmbedder embedder, IContentStore store, ContentReloader reloader, ResetStores stores)
{
    /// <summary>
    /// Deletes what's asked, part by part, noting each in <paramref name="progress"/> as it's done,
    /// so a failure partway still says what went (F-0221). Not stopped with the request: once files
    /// are deleted, the version bump and the reload must follow, or replicas keep serving what's
    /// gone (F-0109).
    /// </summary>
    /// <param name="parts">What to delete.</param>
    /// <param name="actor">Who asked, for a re-embed of content that stays.</param>
    /// <param name="progress">Where to note what's done.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(IReadOnlySet<ResetPart> parts, string actor, ResetProgress progress)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(progress);
        var cancellationToken = CancellationToken.None;
        var done = progress.Done;
        try
        {
            foreach (var (part, container) in new[] { (ResetPart.Content, ContentContainers.Content), (ResetPart.Media, ContentContainers.Media), (ResetPart.Settings, ContentContainers.Settings) })
            {
                if (parts.Contains(part))
                {
                    progress.Current = part;
                    done[part] = await files.DeleteAsync([container], cancellationToken);
                }
            }

            if (parts.Contains(ResetPart.SearchIndex))
            {
                progress.Current = ResetPart.SearchIndex;
                var count = await files.DeleteAsync(["index"], cancellationToken);
                foreach (var item in await vectors.ListAsync(embedder.ModelId, cancellationToken))
                {
                    await vectors.DeleteAsync(embedder.ModelId, item, cancellationToken);
                    count++;
                }

                done[ResetPart.SearchIndex] = count;
            }

            if (parts.Contains(ResetPart.Comments))
            {
                progress.Current = ResetPart.Comments;
                var all = await stores.Comments.ListAsync(cancellationToken);
                foreach (var comment in all)
                {
                    await stores.Comments.DeleteAsync(comment.Post, comment.Id, cancellationToken);
                }

                await stores.CommentCatalog.RefreshAsync(cancellationToken);
                done[ResetPart.Comments] = all.Count;
            }

            if (parts.Contains(ResetPart.Reactions))
            {
                progress.Current = ResetPart.Reactions;
                try
                {
                    done[ResetPart.Reactions] = await stores.Reactions.ClearAsync(cancellationToken);
                }
                finally
                {
                    // The analytics keep finished days' events in memory: also when it stopped partway.
                    stores.Analytics.ForgetReactions();
                }

                // The counters remember who reacted today: kept, they'd refuse a reaction again and take
                // back one that's gone (F-0196).
                if (!parts.Contains(ResetPart.SpamCounters))
                {
                    await stores.Limits.CleanUpAsync(DateOnly.MaxValue, cancellationToken);
                }
            }

            if (parts.Contains(ResetPart.Messages))
            {
                progress.Current = ResetPart.Messages;
                done[ResetPart.Messages] = await stores.Messages.DeleteOlderThanAsync(DateTimeOffset.MaxValue, cancellationToken);
            }

            if (parts.Contains(ResetPart.Analytics))
            {
                // Today's salt stays: the recorders hold it, and a new one would count today's visitors twice.
                progress.Current = ResetPart.Analytics;
                var today = DateOnly.FromDateTime(stores.Time.GetUtcNow().UtcDateTime);
                done[ResetPart.Analytics] = await stores.PageViews.CleanUpAsync(DateOnly.MaxValue, today, cancellationToken);
            }

            if (parts.Contains(ResetPart.Editors))
            {
                progress.Current = ResetPart.Editors;
                var added = await stores.Editors.ListAsync(cancellationToken);
                foreach (var editor in added)
                {
                    await stores.Editors.RemoveAsync(editor.Email, cancellationToken);
                }

                done[ResetPart.Editors] = added.Count;
            }

            if (parts.Contains(ResetPart.ConnectedApps))
            {
                progress.Current = ResetPart.ConnectedApps;
                var grants = await stores.OAuth.ListGrantsAsync(null, cancellationToken);
                foreach (var grant in grants)
                {
                    await stores.OAuth.DeleteGrantAsync(grant.Id, cancellationToken);
                }

                var clients = await stores.OAuth.ListClientsAsync(cancellationToken);
                foreach (var client in clients)
                {
                    await stores.OAuth.DeleteClientAsync(client.Id, cancellationToken);
                }

                done[ResetPart.ConnectedApps] = grants.Count + clients.Count;
            }

            if (parts.Contains(ResetPart.AuditLog))
            {
                progress.Current = ResetPart.AuditLog;
                done[ResetPart.AuditLog] = await stores.Audit.ClearAsync(cancellationToken);
            }

            if (parts.Contains(ResetPart.SpamCounters))
            {
                progress.Current = ResetPart.SpamCounters;
                done[ResetPart.SpamCounters] = await stores.Limits.CleanUpAsync(DateOnly.MaxValue, cancellationToken);
            }

            if (parts.Contains(ResetPart.Imports))
            {
                progress.Current = ResetPart.Imports;
                done[ResetPart.Imports] = await stores.Imports.ClearAsync(cancellationToken);
            }

            // The content stays but its vectors went: embed it again.
            if (parts.Contains(ResetPart.SearchIndex) && !parts.Contains(ResetPart.Content))
            {
                progress.Current = ResetPart.SearchIndex;
                await stores.Reindex.RequestAsync(actor, cancellationToken);
            }

            progress.Current = null;
        }
        finally
        {
            if (parts.Overlaps([ResetPart.Content, ResetPart.Media, ResetPart.Settings, ResetPart.SearchIndex]))
            {
                await reloader.ReloadAfterWriteAsync(store);
            }
        }
    }

    /// <summary>
    /// Maps <c>POST /admin/reset</c>: admins only, with an antiforgery token (form fields bind, so
    /// it's checked), the parts ticked and the site's name typed as confirmation. Then it writes the
    /// audit entry (after the audit log was cleared, if it was), also when the reset failed partway:
    /// with what went and where it stopped, and the page says so (F-0221). Only an admin who deleted
    /// themselves, as an added editor, is signed out; the rest go back to the page, which says what
    /// went.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapSiteReset(IEndpointRouteBuilder app) =>
        app.MapPost("/admin/reset", async ([FromForm(Name = "part")] string[]? part, [FromForm] string? confirm, HttpContext context, ContentCatalogProvider content, SiteReset reset, IAuditLog audit, ConfiguredAdmins admins, TimeProvider time, ILogger<SiteReset> logger) =>
        {
            var parts = ResetParts.Parse(part);
            var again = string.Concat(ResetParts.All.Where(parts.Contains).Select(p => "&part=" + ResetParts.Key(p)));
            if (parts.Count == 0)
            {
                return Results.Redirect("/admin/reset?error=nothing" + again);
            }

            if (!string.Equals(confirm?.Trim(), content.Current.Site.Name, StringComparison.Ordinal))
            {
                return Results.Redirect("/admin/reset?error=confirm" + again);
            }

            var actor = EditorClaims.Email(context.User);
            var progress = new ResetProgress();
            var failed = true;
            try
            {
                await reset.RunAsync(parts, actor, progress);
                failed = false;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, progress.Current is { } at ? ResetParts.Key(at) : "reload", e);
            }
            finally
            {
                await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, "site-reset", progress.Summary(failed)), CancellationToken.None);
            }

            if (progress.Done.ContainsKey(ResetPart.Editors) && !admins.Contains(actor))
            {
                await context.SignOutAsync(EditorClaims.Scheme);
                return Results.Redirect("/admin/sign-in?reset=1");
            }

            if (failed)
            {
                var deleted = string.Concat(ResetParts.All.Where(progress.Done.ContainsKey).Select(p => "&deleted=" + ResetParts.Key(p)));
                var at = progress.Current is { } stop ? "&failed=" + ResetParts.Key(stop) : "";
                return Results.Redirect("/admin/reset?error=failed" + at + deleted + again);
            }

            return Results.Redirect("/admin/reset?done=1" + again);
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);

    [LoggerMessage(Level = LogLevel.Error, Message = "The reset failed at {Part}")]
    private static partial void LogFailed(ILogger logger, string part, Exception exception);
}
