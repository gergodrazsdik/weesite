# tests

All tests use [TUnit](https://tunit.dev/) on Microsoft.Testing.Platform (`just test`).

- `Weesite.Content.Tests`: front matter, slugs and the catalog; the sample site through the file source and, against Azurite (Testcontainers, `[Category("containers")]`: needs Docker or Podman), through the Blob source.
- `Weesite.Web.Tests`: smoke tests on `WebApplicationFactory` (pages render, no framework script, 404 page, health endpoints, noindex), reading the sample site from `seed/` (`SiteFactory`).
- `Weesite.Web.EndToEnd`: TUnit.Playwright in Chromium against the real app on a free Kestrel port (`SiteServer`): accessibility-tree snapshots of the page structure, axe checks, keyboard use, pages with JavaScript off, and `Screens`, which writes screenshots to `artifacts/screens/` (`just screens`). Screenshots are for looking at, not compared pixel by pixel: fonts differ between machines and CI. Install Chromium once with `just browsers`.

The rest arrive with the features:

| Layer | Tooling |
|---|---|
| Unit | TUnit |
| Integration | TUnit + `WebApplicationFactory` + Azurite |
| End-to-end | TUnit.Playwright against the real app on a free Kestrel port, Chromium |
| Accessibility | `Deque.AxeCore.Playwright`, on every page |
| Visual | Verify with its Playwright extension, phone and desktop widths, light and dark |

See the [brief](../docs/design/brief.md#testing-and-frontend-verification) for details.
