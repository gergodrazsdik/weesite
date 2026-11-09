# 0005: Editor and publishing choices for Phase 4

- Status: accepted
- Date: 2026-11-09
- Tracker: T-0047, T-0048, T-0049, T-0069

## Context and problem statement

The [brief](../design/brief.md) describes the editor (a Markdown textarea with server-side preview, front matter fields, uploads, publish and unpublish) and the publishing workflow (drafts with autosave, unlisted previews, scheduling, revisions from Blob versioning, ETag conflict checks). It leaves open how rich the editor is, how a second replica learns about a change, how authors relate to the editors who sign in, and where drafts live.

## Decision outcome

| Topic | Decision | Why |
| --- | --- | --- |
| Editor | A plain textarea with a server-side Preview button, which works without JavaScript; the `ws-md-editor` web component adds a small toolbar, a live preview rendered by the server, autosave and Ctrl+S | small and accessible, no third-party script; the brief's "scripts only enhance" holds in the admin too |
| Freshness across replicas | Every save bumps a version marker blob; each replica checks it every 30 seconds and reloads content and the search index when it changed, or when a scheduled item comes due | prod may run two replicas; half a minute of lag after a publish is acceptable, and the same marker serves the re-index job (T-0069) |
| Authors | Each editor is an author: their profile (display name, short bio, links) lives with their editor entry and is edited in `/admin`; their posts carry them as author, and authors see and edit only their own posts. `site.json`'s authors stay for the seed and imported content | adding an author is adding an editor, nothing else |
| Drafts and scheduling | One Markdown file per item, with `status: draft`, `scheduled` (with `publish_at`) or `published` in its front matter; the public site, feeds, sitemap, search, `llms.txt` and MCP show only published items and scheduled ones whose time has come. Items without a status are published | one copy per item, so Blob versioning gives its full history; nothing to copy at publish time |

### Consequences

- Unpublished text sits in the same container as published text; only the app reads it, and the public site never serves it.
- A reload rebuilds the search index and embeds every chunk again, which takes seconds on the small replica; embedding only what changed comes with the re-index work (T-0069).
- Two replicas can show different content for up to 30 seconds after a change.
- Revisions are kept for 90 days: a lifecycle rule deletes earlier Blob versions after that (Q-0007); the current text always stays.
