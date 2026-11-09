# 0006: The "Ledger" redesign

- Status: accepted
- Date: 2026-11-10
- Tracker: T-0084 to T-0091

## Context and problem statement

The first design was a plain token-based layout, enough to build on. The maintainer worked out a full visual design for the public site and the admin, delivered as static mockups ([docs/design/redesign/](../design/redesign/README.md): the reference stylesheet and one HTML page per screen of `just screens`). The mockups add features the app doesn't have yet, and in a few places they differ from earlier decisions; this ADR records what was decided about both.

## Decision outcome

| Topic | Decision | Why |
| --- | --- | --- |
| Adopt the design | The mockups are the target for every page; changes are checked against a fresh `just screens` | one coherent look for the site and the admin |
| New features in the mockups | All are built: on the public site the banner, topic colours, tag descriptions, breadcrumbs, year-grouped lists, the tag sidebar and Subscribe box, "On this page", older/newer links, code language labels with a Copy button, search filters; in the admin a sidebar with counts, the posts filter and status tabs, the two-column editor with Write/Split/Preview, tag labels with suggestions, a social card preview, version diffs, drag-and-drop uploads, a media card grid and a menu preview | the maintainer's choice |
| Type | IBM Plex Sans and IBM Plex Mono, self-hosted as woff2 (SIL Open Font License 1.1, in `NOTICE`); no third-party font requests | the design's voice; privacy and the strict CSP |
| Stylesheets | `app.css` splits into `site.css` (tokens, base and the public site, loaded everywhere) and `admin.css` (the admin, loaded only under `/admin`) | public pages don't download admin styles |
| Colour tokens | The app keeps its token names (`--color-bg`, `--color-text`, ...), which the theme editor and `/theme.css` use; the defaults move to the design's values, and new tokens (soft accent, status colours, topic colours, banner) are added with AA contrast in both modes | the theme editor keeps working unchanged |
| Topic colours | Four fixed colour slots (blue, violet, teal, amber) that pass 4.5 : 1 in both modes; `site.json` names the topics, gives each a slot and its tags, and an admin Tags page edits them, with tag descriptions | the design's labels (infra, dev, web, life) belong to the sample site, not the engine |
| Banner | A site setting: a media image, or the built-in contour graphic when none is set, or none | the mockups' contour drawing is a stand-in |
| Dates in year-grouped lists | Under a year heading, lists show month then day ("Oct 12"), since the year is already there; everywhere else dates follow the `dateFormat` setting (`yyyy-MM-dd` by default) | no duplicated year; the maintainer prefers month-first |
| Theme switch | System, Light and Dark as three icon buttons with visually hidden text labels, still a form post that works without JavaScript; T-0076 (a sun/moon toggle) is dropped | the design's choice |
| Theme presets | The presets and "Reset to default" stay on the theme page, restyled; the mockup leaving them out was an omission | the brief asks for them |
| Image captions | A figure's caption comes from the Markdown image title (`![alt](src "caption")`), not from its alt text | a caption equal to the alt text is read twice by screen readers |
| Implementation details the mockups don't decide | The skip link stays; mockup inline styles become classes (strict CSP); public pages keep working without JavaScript (the Copy button and filters only enhance) | WCAG 2.2 AA and the CSP are requirements (AGENTS.md) |

### Consequences

- About 500 KB of fonts in the repo; a first visit loads only the faces and subsets a page uses, about 120 KB (IBM's split files, T-0098), cached for a year after that; `font-display: swap` keeps text visible while they load, and the body and heading faces are preloaded.
- Screenshots and several tests that check exact markup or text change with each redesign PR.
- `site.json` gains `banner`, `topics` and `tags`.

## Amendment (2026-11-10): colours per tag, no topics

Topics are dropped. Each tag picks one of ten fixed colours (blue, violet, teal, green, amber, orange, red, pink, cyan, slate), or none, on the admin Tags page next to its description; `site.json` keeps them under `tags` (`colour`, `description`). The colour key next to the first year goes with the topics: the colours are decoration only, so nothing needs a key. Every colour passes 4.5 : 1 against its soft background in both modes, checked by a test. Simpler to manage than named topics, at the cost of colours that carry no meaning.
