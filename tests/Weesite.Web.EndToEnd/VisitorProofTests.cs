using Microsoft.Extensions.DependencyInjection;
using TUnit.Playwright;
using Weesite.Web.Visitors;

namespace Weesite.Web.EndToEnd;

/// <summary>The browser's side of the invisible proof of work (ADR 0011): its answers satisfy the server.</summary>
[ClassDataSource<SiteServer>(Shared = SharedType.PerTestSession)]
internal sealed class VisitorProofTests(SiteServer site) : PageTest
{
    [Test]
    public async Task The_worker_solves_a_challenge_the_server_accepts_once_under_the_strict_policy()
    {
        await Page.WatchCspAsync();
        await Page.OpenAsync(site.At("/"));

        var started = DateTime.UtcNow;
        var answer = await Page.EvaluateAsync<string>("() => import('/visitor-proof.js').then(m => m.solve('contact'))");
        var took = DateTime.UtcNow - started;

        var proof = site.Services.GetRequiredService<ProofOfWork>();
        await Assert.That(answer).Matches("^[0-9]+:.+$");
        await Assert.That(await proof.VerifyAsync("contact", answer, CancellationToken.None)).IsTrue();
        await Assert.That(await proof.VerifyAsync("contact", answer, CancellationToken.None)).IsFalse();
        await Assert.That(await Page.CspViolationsAsync()).IsEmpty();

        // At the default 100,000 the worst case is a few seconds on CI; typically well under one.
        await Assert.That(took).IsLessThan(TimeSpan.FromSeconds(20));
    }

    [Test]
    public async Task A_supply_has_the_next_answer_ready()
    {
        await Page.OpenAsync(site.At("/"));

        var answers = await Page.EvaluateAsync<string[]>(
            "async () => { const { AnswerSupply } = await import('/visitor-proof.js'); const s = new AnswerSupply('reaction'); s.prepare(); return [await s.take(), await s.take()]; }");

        var proof = site.Services.GetRequiredService<ProofOfWork>();
        await Assert.That(answers[1]).IsNotEqualTo(answers[0]);
        await Assert.That(await proof.VerifyAsync("reaction", answers[0], CancellationToken.None)).IsTrue();
        await Assert.That(await proof.VerifyAsync("reaction", answers[1], CancellationToken.None)).IsTrue();
    }
}
