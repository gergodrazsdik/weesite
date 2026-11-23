namespace Weesite.Search;

/// <summary>
/// Groups tags that are about the same things, for suggested tag colours (T-0142, T-0144):
/// average-linkage clustering on a similarity between tags, cut finely (about a group per three
/// tags, at most one per colour), then each tag left alone joins the group it's closest to. The
/// best-separated cut (by silhouette) was too coarse: it split the technical tags from the rest
/// and stopped there. Deterministic: the same tags and similarities give the same groups.
/// <para>
/// Average linkage takes time in the cube of the number of tags and memory in its square (two
/// n × n matrices of doubles): a few hundred tags, as a personal site has, take a fraction of a
/// second; thousands would take seconds and tens of megabytes. Callers keep the result while the
/// tags don't change (<see cref="SearchIndex.GroupTags"/>, F-0225).
/// </para>
/// </summary>
public static class TagGroups
{
    /// <summary>Groups tags.</summary>
    /// <param name="count">How many tags there are.</param>
    /// <param name="similarity">The similarity of two tags by index, symmetric, higher for closer; 1 for a tag with itself.</param>
    /// <param name="maxGroups">The most groups to make, such as the number of colours.</param>
    /// <returns>The groups, as tag indices: the largest first, each in ascending order.</returns>
    public static IReadOnlyList<IReadOnlyList<int>> Group(int count, Func<int, int, double> similarity, int maxGroups)
    {
        ArgumentNullException.ThrowIfNull(similarity);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGroups, 1);
        if (count == 0)
        {
            return [];
        }

        if (maxGroups == 1)
        {
            return [[.. Enumerable.Range(0, count)]];
        }

        var sim = new double[count][];
        for (var i = 0; i < count; i++)
        {
            sim[i] = new double[count];
            for (var j = 0; j < count; j++)
            {
                sim[i][j] = i == j ? 1 : similarity(i, j);
            }
        }

        // Too few tags to compare groupings: one group each, up to the limit.
        if (count <= 2)
        {
            return count <= maxGroups ? [.. Enumerable.Range(0, count).Select(i => (IReadOnlyList<int>)[i])] : [[.. Enumerable.Range(0, count)]];
        }

        // Cut finely: about a group per three tags, up to one per colour. Average linkage leaves
        // tags unlike the rest on their own there; each joins the group it's closest to.
        var target = Math.Clamp(count / 3, 2, maxGroups);
        var partitions = Merge(sim, count, maxGroups);
        var cut = partitions.Where(p => p.Count >= target).OrderBy(p => p.Count).First();
        var groups = Fold(cut, sim);
        return [.. groups
            .Select(g => (IReadOnlyList<int>)[.. g.Order()])
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g[0])];
    }

    // Moves each tag that's alone in its group into the group it's closest to on average, one at a
    // time, until none is alone (or one group is left).
    private static List<List<int>> Fold(List<List<int>> groups, double[][] sim)
    {
        groups = [.. groups.Select(g => g.ToList())];
        while (groups.Count > 1 && groups.FirstOrDefault(g => g.Count == 1) is { } alone)
        {
            var tag = alone[0];
            groups.Remove(alone);
            var nearest = groups.OrderByDescending(g => g.Average(o => sim[tag][o])).ThenBy(g => g.Min()).First();
            nearest.Add(tag);
        }

        return groups;
    }

    // Average linkage from single tags down to two groups, keeping every partition of at most
    // maxGroups groups. Cluster similarities follow Lance and Williams' update for the average.
    private static List<List<List<int>>> Merge(double[][] sim, int count, int maxGroups)
    {
        var clusters = Enumerable.Range(0, count).Select(i => new List<int> { i }).ToList();
        var link = sim.Select(row => (double[])row.Clone()).ToArray();
        var alive = Enumerable.Range(0, count).ToList();
        var partitions = new List<List<List<int>>>();
        if (count <= maxGroups)
        {
            partitions.Add(Snapshot(clusters, alive));
        }

        while (alive.Count > 2)
        {
            var (a, b, closest) = (-1, -1, double.NegativeInfinity);
            for (var x = 0; x < alive.Count; x++)
            {
                for (var y = x + 1; y < alive.Count; y++)
                {
                    if (link[alive[x]][alive[y]] > closest)
                    {
                        (a, b, closest) = (alive[x], alive[y], link[alive[x]][alive[y]]);
                    }
                }
            }

            foreach (var c in alive.Where(c => c != a && c != b))
            {
                var merged = ((clusters[a].Count * link[a][c]) + (clusters[b].Count * link[b][c])) / (clusters[a].Count + clusters[b].Count);
                link[a][c] = link[c][a] = merged;
            }

            clusters[a].AddRange(clusters[b]);
            alive.Remove(b);
            if (alive.Count <= maxGroups)
            {
                partitions.Add(Snapshot(clusters, alive));
            }
        }

        return partitions;
    }

    private static List<List<int>> Snapshot(List<List<int>> clusters, List<int> alive) => [.. alive.Select(c => clusters[c].ToList())];
}
