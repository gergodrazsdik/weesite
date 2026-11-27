using Azure.Core;

namespace Weesite.Web.Tests;

/// <summary>The first tokens asked for beside Key Vault's wait, and what the log says of them (ADR 0012, T-0190).</summary>
internal sealed class StartupTimesTests
{
    [Test]
    public async Task Each_scope_gets_its_first_token_asked_for_and_timed_and_a_failure_is_only_recorded()
    {
        var credential = new FakeCredential();

        StartupTimes.AskForTokens(credential, ["https://answers.test/.default", "https://refuses.test/.default"]);

        var times = await WaitForAsync(() => StartupTimes.TokenTimes.Where(t => t.Contains(".test", StringComparison.Ordinal)).ToList(), 2);
        await Assert.That(credential.Scopes.Order(StringComparer.Ordinal)).IsEquivalentTo(["https://answers.test/.default", "https://refuses.test/.default"]);
        await Assert.That(times[0]).Matches(@"^answers\.test [0-9]+ ms \(asked at [0-9]+ ms\)$");
        await Assert.That(times[1]).Matches(@"^refuses\.test failed after [0-9]+ ms \(InvalidOperationException\)$");
    }

    private static async Task<List<string>> WaitForAsync(Func<List<string>> read, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var values = read();
            if (values.Count >= count)
            {
                return values;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    // Answers one host, refuses the other; records the scopes asked for.
    private sealed class FakeCredential : TokenCredential
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _scopes = [];

        public IEnumerable<string> Scopes => _scopes;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var scope = requestContext.Scopes.Single();
            _scopes.Add(scope);
            await Task.Yield();
            return scope.Contains("refuses", StringComparison.Ordinal)
                ? throw new InvalidOperationException("No token here.")
                : new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        }
    }
}
