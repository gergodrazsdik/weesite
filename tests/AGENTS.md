# Agent instructions: tests/

TUnit tests ([README](README.md)): Content, Search, Web (integration through `WebApplicationFactory`) and Web.EndToEnd (TUnit.Playwright). The rules in the root [AGENTS.md](../AGENTS.md) apply; these add to them.

- `dotnet test` runs as its own call, never piped or from a recipe inside the Claude sandbox (it needs Unix sockets): `dotnet test --solution Weesite.Tests.slnf` for everything but the browser, `dotnet test --project tests/Weesite.Web.EndToEnd` for the browser tests, alone, and `-- --treenode-filter "/*/*/<Class>/*"` for one class.
- Read-only web tests share one app (`SharedSite`); a test that writes starts its own `SiteFactory`. Browser tests that send forms set `Visitor:ProofMaxNumber` to the EndToEnd `SiteFactory.EasyProof`, so the proof of work can't hold them up (F-0243).
- No wall-clock timing: give the app a `TimeProvider` that the test moves (`OAuthTests.TestClock`) instead of counting on how fast a busy CI runner is (F-0265, F-0266). Regexes are bounded and `NonBacktracking` (F-0025).
- Browser tests: role and label locators, no sleeps; every page load through `Navigation.OpenAsync`; check addresses with `Expect(Page).ToHaveURLAsync`, not `WaitForURLAsync`; don't wait for load or NetworkIdle after a click (F-0037, F-0104).
- After a UI change, `just screens` and look at `artifacts/screens/`. Azurite has no blob versioning: version-history behaviour shows only in Azure, so test the empty-site case and say what you couldn't check.
- More gotchas: [engineering notes](../docs/engineering-notes.md#tests-and-flaky-tests).
