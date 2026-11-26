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

        // At the default 1,000 (PBKDF2 keys) the worst case is about a second here, a few on CI.
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

    [Test]
    public async Task A_supply_solves_again_after_a_challenge_failed()
    {
        // F-0216: a failed challenge fetch was kept, so every later reaction failed too.
        await Page.OpenAsync(site.At("/"));
        var failNext = 1;
        await Page.RouteAsync("**/visitor/challenge*", async route =>
        {
            if (Interlocked.Decrement(ref failNext) >= 0)
            {
                await route.FulfillAsync(new() { Status = 503, Body = "Down for a moment." });
            }
            else
            {
                await route.ContinueAsync();
            }
        });

        // The answer solved ahead failed before the press: the press solves a new one.
        var afterFailure = await Page.EvaluateAsync<string[]>(
            """
            async () => {
                const { AnswerSupply } = await import('/visitor-proof.js');
                const s = new AnswerSupply('reaction');
                s.prepare();
                await s.pending.answer.catch(() => {});
                const dropped = s.pending === null ? 'dropped' : 'kept';
                const answer = await s.take();
                await s.pending?.answer.catch(() => {});
                return [dropped, answer];
            }
            """);

        // The answer solved ahead fails while the press waits for it: it solves once more.
        Interlocked.Exchange(ref failNext, 1);
        var whileWaiting = await Page.EvaluateAsync<string>(
            "async () => { const { AnswerSupply } = await import('/visitor-proof.js'); const s = new AnswerSupply('reaction'); s.prepare(); return await s.take(); }");

        var proof = site.Services.GetRequiredService<ProofOfWork>();
        await Assert.That(afterFailure[0]).IsEqualTo("dropped");
        await Assert.That(await proof.VerifyAsync("reaction", afterFailure[1], CancellationToken.None)).IsTrue();
        await Assert.That(await proof.VerifyAsync("reaction", whileWaiting, CancellationToken.None)).IsTrue();
    }
}
