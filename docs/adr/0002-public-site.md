# 0002: Public site choices for Phase 2

- Status: accepted
- Date: 2026-11-08
- Tracker: T-0032 to T-0039, Q-0003

## Context and problem statement

The [brief](../design/brief.md) leaves several choices for the public site open or only proposed: the URL scheme, the feed formats, raw HTML in Markdown, code highlighting, how content reaches the environments before the editor exists, the default look, Open Graph images and how the app notices content changes. Phase 2 builds on all of them, so they are settled up front.

## Considered options

| Question | Options considered |
| --- | --- |
| URL scheme | the brief's (`/blog/{slug}`, `/{slug}`, `/blog/tags/{tag}`); dated post URLs; posts at the root |
| Feeds | Atom only; Atom and RSS 2.0; Atom and JSON Feed |
| Raw HTML in Markdown | not allowed; allowed through a sanitizer |
| Code highlighting | TextMateSharp; ColorCode; none for now |
| Content before the editor | test seeded and prod uploaded by hand; both by hand; test seeded and prod empty |
| Default look | neutral with a blue accent; warm; monochrome |
| Open Graph images | generated cards rendered with NetVips; front-matter images only |
| Content changes | reload changed blobs every few minutes; load at startup |
| Comments and newsletter (Q-0003) | in scope; out of scope |

## Decision outcome

| Question | Decision | Because |
| --- | --- | --- |
| URL scheme | `/blog/{slug}` for posts, `/{slug}` for pages, `/blog/tags/{tag}` for tags; Hungarian later under `/hu/` | short, dateless URLs; a changed slug leaves a 301 behind |
| Feeds | Atom (`/feed.xml`) and JSON Feed 1.1 (`/feed.json`), site-wide and per tag (`/blog/tags/{tag}/feed.xml`, `.json`) | Atom for every reader; JSON Feed for scripts and agents |
| Raw HTML | not allowed: Markdig renders Markdown only, HTML in the source shows as text | nothing to sanitize and nothing to slip past the strict CSP; embeds, if ever needed, become Markdown extensions |
| Highlighting | TextMateSharp with the VS Code grammars, rendered on the server into CSS classes coloured by the design tokens | accurate for the languages the site will show (C#, TypeScript, shell, YAML, HCL, Rego), no JavaScript |
| Content before the editor | test loads the sample site from `seed/` at every start; prod stays empty until the editor (Phase 4) | the test environment shows the full site; real content never goes through the public repo |
| Default look | neutral backgrounds and text with one blue accent, AA in light and dark | calm and readable; editable later in the theme editor |
| Open Graph images | a card (title and site name) from an SVG template, rasterised to PNG by NetVips and cached in Blob; a post's own image wins | link previews for every post, with the same library as the image pipeline (Q-0002) |
| Content changes | content and settings load at startup; a change needs a new revision or restart until the editor and re-index job (T-0045) trigger reloads | simplest; content rarely changes before the editor exists |
| Comments and newsletter | out of scope (Q-0003) | confirmed by the maintainer |

### Consequences

- Content is read through a content source: Blob Storage in Azure and locally, a directory in tests. Tests need no containers except the one that covers the Blob source.
- Seeding moves into the web app: with `Seed:Path` set, it loads that directory into Blob before reading. Test sets it to the sample site baked into the image; the Aspire AppHost uses the same path, so the separate seed project goes away.
- The container image carries the sample site, unused outside test.
- NetVips needs fonts for the cards; the image ships one font file rather than relying on the base image.

### Amendment, 2026-11-08 (T-0037)

- Cards are composed from libvips text layers rather than an SVG template: text is drawn with the bundled font files directly (Inter Bold and Regular, OFL), so wrapping and shrinking long titles are exact and nothing depends on SVG font lookup.
- Cards render on the first request to `/og/{key}.png` and stay in memory, rather than being cached in Blob. The key is a hash of the title, site name, colours and layout version, so the URL changes with the card and responses are immutable. Rendering at publish time and keeping cards in Blob comes with the editor (T-0045), when content changes at run time.
- A post or page with an `image` in its front matter uses that image instead.

## More information

- [Atom (RFC 4287)](https://datatracker.ietf.org/doc/html/rfc4287), [JSON Feed 1.1](https://www.jsonfeed.org/version/1.1/)
- [Markdig](https://github.com/xoofx/markdig), [TextMateSharp](https://github.com/danipen/TextMateSharp), [NetVips](https://github.com/kleisauke/net-vips)
