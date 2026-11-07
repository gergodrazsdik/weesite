# src

The .NET 11 solution (`Weesite.slnx` at the repo root, with `global.json`, `Directory.Build.props` and `Directory.Packages.props`):

- `Weesite.Web`: the ASP.NET Core app, Blazor static server rendering only (no interactive render modes, no `blazor.web.js`): public site, later the `/admin` editor, feeds, search and the MCP endpoint
- `Weesite.AppHost`: Aspire host for local development (`aspire run`); Azurite, seed content and the re-index job come with T-0025
- `Weesite.ServiceDefaults`: OpenTelemetry, health checks (`/health`, `/alive`), service discovery and HTTP resilience, shared by the app and the job

Build settings for every project: `net11.0`, nullable, `AnalysisMode=All` with warnings as errors and code style enforced in the build, output under `artifacts/`. Analyzer exceptions live in `.editorconfig`, each with a reason. Package versions are central in `Directory.Packages.props`.

Commands: `just build`, `just test`, `just format`, `just format-check`; `dotnet run --project src/Weesite.Web` serves the site on <http://localhost:5080>.
