# src

The .NET 11 solution (`Weesite.slnx` at the repo root, with `global.json`, `Directory.Build.props` and `Directory.Packages.props`):

- `Weesite.Web`: the ASP.NET Core app, Blazor static server rendering only (no interactive render modes, no `blazor.web.js`): public site, later the `/admin` editor, feeds, search and the MCP endpoint
- `Weesite.Content`: the content model (posts and pages from Markdown with YAML front matter, site settings), slug rules, the in-memory catalog, and the sources (Blob Storage, or a directory in tests); the seeder that loads `seed/` into Blob
- `Weesite.AppHost`: Aspire host for local development (`just aspire`): Azurite 3.37 (Blob, Table, Queue) and the web app, which seeds it from `seed/` at startup; the re-index job comes with T-0045
- `Weesite.ServiceDefaults`: OpenTelemetry, health checks (`/health`, `/alive`), service discovery and HTTP resilience, shared by the app and the job

Build settings for every project: `net11.0`, nullable, `AnalysisMode=All` with warnings as errors and code style enforced in the build, output under `artifacts/`. Analyzer exceptions live in `.editorconfig`, each with a reason. Package versions are central in `Directory.Packages.props`.

Content loads once at startup (ADR 0002): from `Content:Path` if set (tests), otherwise from Blob Storage (the `blobs` connection). With `Seed:Path` set (Aspire, test), the app first uploads that directory into Blob. UI strings are in `src/Weesite.Web/Resources/SharedResource.resx` (English; Hungarian later).

Commands: `just build`, `just test`, `just format`, `just format-check`; `dotnet run --project src/Weesite.Web` serves the site on <http://localhost:5080>.

The container image (`Containerfile` at the repo root) publishes `Weesite.Web` onto the chiseled ASP.NET runtime image with ICU (non-root, no shell), both base images pinned by digest. Build it locally with `docker build -f Containerfile -t weesite .` (or `podman build`); it listens on port 8080. The `app` workflow scans it with grype (fails on high severity) and, on `main`, pushes it to `ghcr.io/<owner>/weesite:<commit>`.
