# Changelog

All notable changes to Weesite are in this file, from release 1.0 on. What came before is in the [tracker archive](docs/history/tracker-1.0.md) and the git history.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Larger uploads from apps:** the editor MCP endpoint's new `create_media_upload` tool gives an app a one-time address to upload a file of up to 20 MB with curl, so the file no longer passes through the model as base64 ([MCP](docs/mcp.md#upload-larger-files)). It can also replace a stored file, as `replace_media` does. Apps that run commands in a sandbox, such as Claude, need the site's domain allowed there.
- **Tags for apps:** the editor MCP endpoint's new `list_tags` tool lists the tags posts use, most used first, with their colours and descriptions, so an app reuses them instead of making up new ones ([MCP](docs/mcp.md)).
- **Changed elsewhere:** the post and page editor says when someone else saved the item while you have it open, in another tab, in the editor or through an app such as Claude. With unsaved changes, it says that saving will show the conflict, and links to the saved version.
- **Tag descriptions in search results:** a tag's description is now its page's meta description, so search engines and link previews show it instead of the site's tagline.
- **Suggested tag colours:** Tags has a Suggest colours page that groups the tags by what their posts are about and gives each group a colour, keeping colours already in use where it can; you can choose another colour for a group. Nothing changes until you tick the suggestions and apply them.
- **Visitors' countries in the analytics:** a person's page view records the country of their IP address, looked up in memory with DB-IP's IP to Country Lite (CC BY 4.0); the address is never stored. On by default; Site settings can turn it off.
- **The `geoip` workflow** downloads, checks and uploads the country database to each environment every month. It needs `infra-apply` for the new `geoip` container and its role ([deploy](docs/deploy.md#visitor-countries-geoip)).
- **A livelier analytics page:** it refreshes every 30 seconds without moving the page, and can be paused; the per-day table opens in a dialog, and the lists (pages, referring sites, bots and AI agents, countries) are tabs.
- **IndexNow:** turned on in Site settings, the site tells Bing and the other engines that take part about a post or page as soon as it's published, changed or taken down, rather than at their next crawl. Off by default ([deploy](docs/deploy.md#search-engines)).
- **JSON, YAML and TOML files in Media:** they upload like the other files and download from their links. A JSON or YAML file must parse; the refusal names the line that doesn't.

### Changed

- **The editor's split view scrolls in step:** side by side, the text and the preview are as tall as the window and each scrolls on its own; scrolling either one brings the other to the same place, so the text no longer runs out halfway down a post with images. Stacked, on a narrow screen, they work as before.
- **The load test is named in the analytics:** its requests count as the bot "Grafana k6" rather than an unnamed bot.
- **Runtime async:** Weesite's own code is compiled with .NET 11's runtime async, as ASP.NET Core and the .NET libraries already are. Async calls allocate less, and stack traces in the logs no longer show the compiler's state machine frames.

### Fixed

- **The editor's reading time matches the site's:** the editor counted the Markdown as typed, so table pipes, list markers and heading marks counted as words, and a post with tables could show a minute more than its page. It now counts the plain text, as the site does.
- **Scheduled posts become published:** when a scheduled post's or page's time comes, it's now saved as published, with an audit entry by the schedule, rather than staying "scheduled" in storage while the site showed it. With IndexNow on, the search engines hear about it then.
- **Ctrl+S in the editor:** saves before you've clicked into a field too. Before, right after the page loaded or saved, it opened the browser's own "Save page" dialog.
- **Saving without changes:** pressing Save with nothing changed, in the post and page editor or on a settings page (site settings, menu, tags, colour theme, your profile), no longer adds a version or an audit entry; the page says nothing had changed. A post written by hand or by an app keeps its formatting until something in it changes. Over MCP, `update_item` answers `unchanged` the same way.
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
