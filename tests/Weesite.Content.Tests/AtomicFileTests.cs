using Weesite.Content.Sources;

namespace Weesite.Content.Tests;

/// <summary>File writes that readers never see half-done (F-0061).</summary>
internal sealed class AtomicFileTests
{
    [Test]
    public async Task Readers_see_the_old_or_the_new_text_while_a_file_is_rewritten()
    {
        var root = Directory.CreateTempSubdirectory("weesite-atomic-");
        try
        {
            var store = new FileContentStore(root.FullName);
            var version = await store.WriteAsync("posts/one.md", new string('a', 50_000), null, CancellationToken.None);
            using var stop = new CancellationTokenSource();
            var seen = new List<int>();

            var reader = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    seen.Add((await store.ReadAsync("posts/one.md", CancellationToken.None))?.Text.Length ?? -1);
                }
            });
            for (var i = 0; i < 50; i++)
            {
                version = await store.WriteAsync("posts/one.md", new string((char)('b' + (i % 20)), 50_000 + i), version, CancellationToken.None);
            }

            await stop.CancelAsync();
            await reader;

            await Assert.That(seen).IsNotEmpty();
            await Assert.That(seen.All(length => length >= 50_000)).IsTrue();
            await Assert.That(root.GetFiles("*.tmp", SearchOption.AllDirectories)).IsEmpty();
            await Assert.That(root.GetFiles(".*", SearchOption.AllDirectories)).IsEmpty();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task A_failed_write_leaves_the_file_and_no_temporary_file()
    {
        var root = Directory.CreateTempSubdirectory("weesite-atomic-");
        try
        {
            var file = Path.Combine(root.FullName, "site.json");
            await File.WriteAllTextAsync(file, "{}");
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.That(async () => await AtomicFile.WriteAllTextAsync(file, "new", cancelled.Token)).Throws<OperationCanceledException>();

            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("{}");
            await Assert.That(root.GetFiles()).Count().IsEqualTo(1);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
