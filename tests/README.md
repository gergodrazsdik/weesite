# tests

**Placeholder.** Test projects arrive with the solution in Phase 1. All tests use [TUnit](https://tunit.dev/):

| Layer | Tooling |
|---|---|
| Unit | TUnit |
| Integration | TUnit + `WebApplicationFactory` + Azurite |
| End-to-end | TUnit.Playwright against the real app on a free Kestrel port, Chromium |
| Accessibility | `Deque.AxeCore.Playwright`, on every page |
| Visual | Verify with its Playwright extension, phone and desktop widths, light and dark |

See the [brief](../docs/design/brief.md#testing-and-frontend-verification) for details.
