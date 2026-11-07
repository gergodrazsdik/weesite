# src

The .NET 11 solution (`Weesite.slnx` at the repo root, with `global.json`, `Directory.Build.props` and `Directory.Packages.props`):

- `Weesite.Web`: the ASP.NET Core app, Blazor static server rendering only (no interactive render modes, no `blazor.web.js`): public site, later the `/admin` editor, feeds, search and the MCP endpoint
- `Weesite.AppHost`: Aspire host for local development (`just aspire`): Azurite (Blob, Table, Queue), the seed step, then the web app; the re-index job comes with T-0045
- `Weesite.Seed`: uploads `seed/` into Azurite (each top-level directory a container) and exits; the web app waits for it
- `Weesite.ServiceDefaults`: OpenTelemetry, health checks (`/health`, `/alive`), service discovery and HTTP resilience, shared by the app and the job

Build settings for every project: `net11.0`, nullable, `AnalysisMode=All` with warnings as errors and code style enforced in the build, output under `artifacts/`. Analyzer exceptions live in `.editorconfig`, each with a reason. Package versions are central in `Directory.Packages.props`.

Commands: `just build`, `just test`, `just format`, `just format-check`; `dotnet run --project src/Weesite.Web` serves the site on <http://localhost:5080>.

The container image (`Containerfile` at the repo root) publishes `Weesite.Web` onto the chiseled ASP.NET runtime image with ICU (non-root, no shell), both base images pinned by digest. Build it locally with `docker build -f Containerfile -t weesite .` (or `podman build`); it listens on port 8080. The `app` workflow scans it with grype (fails on high severity) and, on `main`, pushes it to `ghcr.io/<owner>/weesite:<commit>`.
