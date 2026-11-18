namespace Weesite.Search;

/// <summary>
/// Groups tags that are about the same things, for suggested tag colours (T-0142): average-linkage
/// clustering on a similarity between tags, cut where the groups are best separated (the highest
/// mean silhouette), with at most as many groups as there are colours and, from six tags on, at
/// least three. Deterministic: the same
/// tags and similarities give the same groups.
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

        // Two groups score best whenever the tags have one dominant split (on the sample site, the
        // technical tags against the travel ones: 0.48, while seven groups that each make a topic
        // score 0.33), and two colours tell little apart. So from six tags on, at least three.
        var fewest = count >= 6 && maxGroups >= 3 ? 3 : 2;
        var partitions = Merge(sim, count, maxGroups);
        var best = partitions
            .Where(p => p.Count >= fewest && p.Count < count)
            .Select(p => (Groups: p, Score: Silhouette(p, sim, count)))
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Groups.Count)
            .Select(p => p.Groups)
            .FirstOrDefault() ?? partitions[^1];
        return [.. best
            .Select(g => (IReadOnlyList<int>)[.. g.Order()])
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g[0])];
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

    private static double Distance(int tag, IEnumerable<int> others, double[][] sim) => others.Average(o => 1 - sim[tag][o]);

    private static List<List<int>> Snapshot(List<List<int>> clusters, List<int> alive) => [.. alive.Select(c => clusters[c].ToList())];

    // The mean silhouette, with 1 - similarity as the distance: how much closer each tag is to its
    // own group than to the nearest other one. A tag alone in its group counts 0.
    private static double Silhouette(List<List<int>> groups, double[][] sim, int count)
    {
        var groupOf = new int[count];
        for (var g = 0; g < groups.Count; g++)
        {
            foreach (var t in groups[g])
            {
                groupOf[t] = g;
            }
        }

        var total = 0.0;
        for (var t = 0; t < count; t++)
        {
            var own = groups[groupOf[t]];
            if (own.Count == 1)
            {
                continue;
            }

            var inside = Distance(t, own.Where(o => o != t), sim);
            var nearest = double.PositiveInfinity;
            for (var g = 0; g < groups.Count; g++)
            {
                if (g != groupOf[t])
                {
                    nearest = Math.Min(nearest, Distance(t, groups[g], sim));
                }
            }

            var spread = Math.Max(inside, nearest);
            total += spread > 0 ? (nearest - inside) / spread : 0;
        }

        return total / count;
    }
}
