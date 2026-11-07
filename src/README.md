# src

**Placeholder.** The .NET 11 solution arrives in Phase 1 (see the [tracker](../docs/plan/TRACKER.md)):

- `Weesite.Web`: the ASP.NET Core app (Blazor static server rendering), public site, `/admin` editor, feeds, search and the MCP endpoint
- `Weesite.AppHost`: Aspire host for local development (the app, Azurite, the re-index job, seed content)
- `Weesite.ServiceDefaults`: OpenTelemetry, health checks and resilience shared by the app and the job

The solution file (`.slnx`), `global.json`, `Directory.Build.props` and `Directory.Packages.props` sit at the repo root.
