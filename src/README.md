# src

The .NET 11 solution (`Weesite.slnx` at the repo root, with `global.json`, `Directory.Build.props` and `Directory.Packages.props`):

- `Weesite.Web`: the ASP.NET Core app, Blazor static server rendering only (no interactive render modes, no `blazor.web.js`): public site, later the `/admin` editor, feeds, search and the MCP endpoint
- `Weesite.Content`: the content model (posts and pages from Markdown with YAML front matter, site settings), slug rules, the in-memory catalog, and the sources (Blob Storage, or a directory in tests); the seeder that loads `seed/` into Blob
- `Weesite.AppHost`: Aspire host for local development (`just aspire`): Azurite 3.37 (Blob, Table, Queue) and the web app, which seeds it from `seed/` at startup; the re-index job comes with T-0045
- `Weesite.ServiceDefaults`: OpenTelemetry, health checks (`/health`, `/alive`), service discovery and HTTP resilience, shared by the app and the job

Build settings for every project: `net11.0`, nullable, `AnalysisMode=All` with warnings as errors and code style enforced in the build, output under `artifacts/`. Analyzer exceptions live in `.editorconfig`, each with a reason. Package versions are central in `Directory.Packages.props`.

Routes: `/` (home: `home.show` in `site.json` picks the latest posts, a page, or both), `/blog` (all posts), `/blog/{slug}` (post: date, author, tags), `/{slug}` (content page); unknown slugs get the 404 page, earlier slugs (`redirect_from`) a 301 to the current URL. The menu comes from `site.json` (`aria-current` marks the current page), or, without one, the blog and the pages marked `menu: true`; the footer shows the contact email and links.

Styling (`wwwroot/app.css`): cascade layers (reset, tokens, base, components), every colour a token through `light-dark()`, each built from a `-light` and a `-dark` variable. The header's System/Light/Dark switch is a form post (`POST /theme`, antiforgery) that sets a functional `theme` cookie; the server renders `data-theme` on `<html>` from it, so there's no flash and no JavaScript needed. A `theme` block in `site.json` (token to colour, per mode) becomes `/theme.{hash}.css`, served immutable and linked after app.css; only the nine colour tokens and real colour values get in.

Markdown renders once, at load (`MarkdownRenderer`): raw HTML shows as text, links may only use http(s), mailto or relative addresses, if the text uses `#` headings, they all shift down a level so the page title stays the only `h1`, and fenced code is highlighted on the server by TextMateSharp into `hl-*` classes. Images in `/media/` render with `width`, `height`, a `srcset` of their WebP variants, `loading="lazy"` and `decoding="async"`; an image without alt text is logged as a content problem. `ImageProcessor` (NetVips, Q-0002) prepares images: it applies the orientation, strips all metadata (EXIF, GPS) and writes WebP variants at 480, 960 and 1600 px (`{name}-{width}w.webp`, never upscaled); the seeder runs it today, uploads will in Phase 4. `/media/{hash}-{name}` serves the media container: `nosniff`, a year's immutable caching, inline only for raster images.

Content loads once at startup (ADR 0002): from `Content:Path` if set (tests), otherwise from Blob Storage (the `blobs` connection). With `Seed:Path` set (Aspire, test), the app first uploads that directory into Blob. UI strings are in `src/Weesite.Web/Resources/SharedResource.resx` (English; Hungarian later).

Commands: `just build`, `just test`, `just format`, `just format-check`; `dotnet run --project src/Weesite.Web` serves the site on <http://localhost:5080>.

The container image (`Containerfile` at the repo root) publishes `Weesite.Web` onto the chiseled ASP.NET runtime image with ICU (non-root, no shell), both base images pinned by digest. Build it locally with `docker build -f Containerfile -t weesite .` (or `podman build`); it listens on port 8080. The `app` workflow scans it with grype (fails on high severity) and, on `main`, pushes it to `ghcr.io/<owner>/weesite:<commit>`.
