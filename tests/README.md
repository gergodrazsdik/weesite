# tests

All tests use [TUnit](https://tunit.dev/) on Microsoft.Testing.Platform (`just test`). `Weesite.Web.Tests` holds the smoke tests (pages render, no framework script, 404 page, health endpoints) on `WebApplicationFactory`; the rest arrive with the features:

| Layer | Tooling |
|---|---|
| Unit | TUnit |
| Integration | TUnit + `WebApplicationFactory` + Azurite |
| End-to-end | TUnit.Playwright against the real app on a free Kestrel port, Chromium |
| Accessibility | `Deque.AxeCore.Playwright`, on every page |
| Visual | Verify with its Playwright extension, phone and desktop widths, light and dark |

See the [brief](../docs/design/brief.md#testing-and-frontend-verification) for details.
