# 0005: Editor and publishing choices for Phase 4

- Status: accepted
- Date: 2026-11-09
- Tracker: T-0047, T-0048, T-0049, T-0069

## Context and problem statement

The [brief](../history/brief.md) describes the editor (a Markdown textarea with server-side preview, front matter fields, uploads, publish and unpublish) and the publishing workflow (drafts with autosave, unlisted previews, scheduling, revisions from Blob versioning, ETag conflict checks). It leaves open how rich the editor is, how a second replica learns about a change, how authors relate to the editors who sign in, and where drafts live.

## Decision outcome

| Topic | Decision | Why |
| --- | --- | --- |
| Editor | A plain textarea with a server-side Preview button, which works without JavaScript; the `ws-md-editor` web component adds a small toolbar, a live preview rendered by the server, autosave and Ctrl+S | small and accessible, no third-party script; the brief's "scripts only enhance" holds in the admin too |
| Freshness across replicas | Every save bumps a version marker blob; each replica checks it every 30 seconds and reloads content and the search index when it changed, or when a scheduled item comes due | prod may run two replicas; half a minute of lag after a publish is acceptable, and the same marker serves the re-index job (T-0069) |
| Authors | Each editor is an author: their profile (display name, short bio, links) lives with their editor entry and is edited in `/admin`; their posts carry them as author, and authors see and edit only their own posts. `site.json`'s authors stay for the seed and imported content | adding an author is adding an editor, nothing else |
| Drafts and scheduling | One Markdown file per item, with `status: draft`, `scheduled` (with `publish_at`) or `published` in its front matter; the public site, feeds, sitemap, search, `llms.txt` and MCP show only published items and scheduled ones whose time has come. Items without a status are published | one copy per item, so Blob versioning gives its full history; nothing to copy at publish time |

### Consequences

- Unpublished text sits in the same container as published text; only the app reads it, and the public site never serves it.
- A reload rebuilds the search index. Chunk vectors are stored in Blob by a hash of their text, so a rebuild embeds only new or changed chunks (T-0069).
- Two replicas can show different content for up to 30 seconds after a change.
- Revisions are kept for 90 days: a lifecycle rule deletes earlier Blob versions after that (Q-0007); the current text always stays. Superseded by the amendment of 2026-11-23 below.

## Amendment (2026-11-09): where profiles live

Profiles live in `settings/authors.json`, keyed by author ID (`editor-` and a hash of the address), not in the editor's Table Storage entry. Bylines are rendered on the public site, in feeds and in JSON-LD from the content catalog, so the profiles load with the content: no Table lookup on public pages, the same Blob versioning and replica reload as `site.json`, and they travel with an export (T-0051). The file holds no email addresses. Each editor edits their own profile at `/admin/profile`; an author in `site.json` with the same ID wins.

## Amendment (2026-11-09): the admin may require JavaScript

The admin no longer has to work without JavaScript; that rule stays for the public site. Admin features may rely on scripts where that makes editing better (richer editors, live checks, drag and drop), written as clean, modern ES modules and `ws-` web components, with no heavy or problematic dependencies. What's already built keeps its server-side fallbacks, which are no longer required for new work. Accessibility (WCAG 2.2 AA) applies to the admin as much as to the public site.

## Amendment (2026-11-23): the app keeps the revision history

The lifecycle rule counted a version's 90 days from when its text was written, not from when it was replaced, so editing a post unchanged for months lost its earlier text within a day (F-0240). The app now keeps the history itself: on each save of a post, page or settings file it deletes that file's earlier versions that are both beyond the newest 50 and replaced more than 90 days ago (the app settings `Content:VersionsKept` and `Content:VersionDays`), and the lifecycle rule no longer covers the `content` and `settings` containers. This answers Q-0007 anew: the newest 50 earlier versions always stay, older ones at least 90 days after they were replaced. A deleted file's versions stay until a file is saved under the same name again. Deleting a version needs `deleteBlobVersion`, which Storage Blob Data Contributor lacks, so the bootstrap adds a custom role with that action alone, assigned to the web identity on those two containers. Media versions, which nothing in the app shows, keep the lifecycle rule.
