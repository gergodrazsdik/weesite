# tests

All tests use [TUnit](https://tunit.dev/) on Microsoft.Testing.Platform (`just test`).

- `Weesite.Content.Tests`: front matter, slugs and the catalog; the sample site through the file source and, against Azurite (Testcontainers, `[Category("containers")]`: needs Docker or Podman), through the Blob source.
- `Weesite.Search.Tests`: embeddings, hybrid search, the vector store and search quality on the sample site; they need the embedding models (`just model`).
- `Weesite.Web.Tests`: the app's behaviour end to end in process on `WebApplicationFactory` (pages, admin, editor, MCP, OAuth, feeds and more), reading the sample site from `seed/` (`SiteFactory`). Read-only tests share one app per test session (`SharedSite`); tests that write start their own `SiteFactory`. The tests marked `[Category("containers")]` start Azurite or Mailpit (Testcontainers: needs Docker or Podman).
- `Weesite.Web.EndToEnd`: TUnit.Playwright in Chromium against the real app on a free Kestrel port (`SiteServer`): accessibility-tree snapshots of the page structure, axe checks, keyboard use, pages with JavaScript off, and `Screens`, which writes screenshots to `artifacts/screens/` (`just screens`). Screenshots are for looking at, not compared pixel by pixel: fonts differ between machines and CI. Install Chromium once with `just browsers`.

`Shared/` holds helpers every test project links in (`RepoPaths`: the repository's root, `seed/` and `artifacts/`); `Weesite.Web.Tests/Forms.cs` reads forms in rendered pages.

| Layer | Tooling |
|---|---|
| Unit | TUnit |
| Integration | TUnit + `WebApplicationFactory` + Azurite |
| End-to-end | TUnit.Playwright against the real app on a free Kestrel port, Chromium |
| Accessibility | `Deque.AxeCore.Playwright`, on every page |
| Visual | `just screens`: screenshots to look at, not compared |

See the [brief](../docs/design/brief.md#testing-and-frontend-verification) for details.
