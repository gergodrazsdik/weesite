# Changelog

All notable changes to Weesite are in this file, from release 1.0 on. What came before is in the [tracker archive](docs/history/tracker-1.0.md) and the git history.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Larger uploads from apps:** the editor MCP endpoint's new `create_media_upload` tool gives an app a one-time address to upload a file of up to 20 MB with curl, so the file no longer passes through the model as base64 ([MCP](docs/mcp.md#upload-larger-files)). It can also replace a stored file, as `replace_media` does. Apps that run commands in a sandbox, such as Claude, need the site's domain allowed there.

### Changed

- **Runtime async:** Weesite's own code is compiled with .NET 11's runtime async, as ASP.NET Core and the .NET libraries already are. Async calls allocate less, and stack traces in the logs no longer show the compiler's state machine frames.

### Fixed

- **Uploaded photos kept as they are when possible:** a JPEG or WebP without metadata is stored as uploaded unless re-encoding makes it smaller. Before, every one was re-encoded at quality 85, so a photo compressed harder grew and lost a little quality. Photos with metadata (camera, location) are still re-encoded without it.
- **HEAD requests:** media files, the feeds, the sitemap, `robots.txt`, `llms.txt`, the Markdown versions of posts and the social cards answer HEAD as they answer GET. Before, they answered 404 or 405, so link checkers and crawlers that probe with HEAD reported them as missing.

## [1.0.0] - 2026-11-16

The first release: a personal site on Azure that you deploy from your own fork, with an editor, search and agent support. [Install Weesite](docs/install.md) walks through a first install.

### Added

- **The public site:** posts and pages from Markdown, tags with colours, related posts, a one-level menu and a home page you set up. Reading aids: "On this page", heading links, a top link, images that open larger, code highlighted on the server with an optional file name, footnotes and print styles. Light, dark and system themes, with your own colours. It works without JavaScript.
- **Search:** keyword and meaning search in the app itself (SQLite FTS5 and a local embedding model, merged by rank), English by default and about a hundred languages with the multilingual model. No outside search service.
- **Search engines and feeds:** canonical addresses, structured data, social cards drawn for every post, a sitemap, `robots.txt`, Atom and JSON Feed.
- **Agent support:** `llms.txt`, every page as Markdown (`Accept: text/markdown`), a read-only MCP endpoint for the public content, and an MCP endpoint for editors (`/mcp/editor`) with OAuth sign-in, three consent levels, Client ID Metadata Documents and dynamic registration.
- **The editor at `/admin`:** sign-in with Microsoft or Google; admins and authors; a Markdown editor with a toolbar, live preview and autosave; drafts, scheduling, archiving and deleting; versions with a diff and restore; media with alt text, WebP sizes and references; the menu, theme, tags, site and footer settings; editors with email invitations; an audit log with a CSV export; backups to download and import; a factory reset; and HTML export of a post for publishing elsewhere.
- **Analytics:** page views counted on the server, without cookies and without storing IP addresses, AI agents included; an editor's own visits are left out.
- **Languages:** one site language, any language; the public text in English and Hungarian; posts in another language marked as such.
- **Infrastructure:** OpenTofu for Azure Container Apps, Blob and Table Storage, Key Vault and monitoring, test and prod, with a bootstrap that sets up the state, its encryption, the identities and the GitHub environments. A second install fits in the same subscription.
- **Delivery:** GitHub Actions that lint, test (unit, integration and browser), scan and publish the image, plan and apply the infrastructure with a policy baseline (Rego, TFLint, Checkov), deploy to test, and promote the same image to prod after approval.
- **Security and accessibility:** a strict CSP with no inline code, `__Host-` session cookies with a seven-day limit, rate limits, upload and import checks, audit logs for Key Vault, storage and the state, and WCAG 2.2 AA checked with axe in the browser tests and by hand with a screen reader.
