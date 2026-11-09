# Weesite redesign: "Ledger" with accent + topic colours and a banner

> In the repo: this README, the reference stylesheet (`site.css`) and the mockup pages (`pages/`). The 61 target screenshots, the seed photos and the fonts aren't copied here: the fonts live in the app (`src/Weesite.Web/wwwroot/fonts/`), the photos are the seed's, and the screenshots stay with the maintainer. Decisions about this design are in [ADR 0006](../../adr/0006-redesign.md).

These are design mockups, not app output. `screens/` has the same 61 file names, widths (390 and
1280 px), themes and page set as `just screens` (`tests/Weesite.Web.EndToEnd/Screens.cs`) plus
`media-picker-1280-light.png`. They were drawn as static HTML from the seed content in `seed/`, so
the text, posts, tags and media match the e2e site. Use them as the target when changing
`src/Weesite.Web/wwwroot/app.css` and the Razor components, then compare with a fresh `just screens`.

## What's in the package

- `screens/`: the 61 target images.
- `mockup/site.css`: the stylesheet the mockups use. It is the reference for `app.css`: tokens,
  components and the phone breakpoint (720 px).
- `mockup/pages/*.html`: one static page per screen, with the markup the CSS expects. Open them in a
  browser; light/dark follows the OS (`prefers-color-scheme`).
- `mockup/assets/`: the banner graphics (`topo-light.png`, `topo-dark.png`), the seed media and logo,
  and IBM Plex Sans and Mono (`fonts/`, SIL Open Font License 1.1, see `fonts/OFL.txt`).

## The design in short

- **Type:** IBM Plex Sans for text, IBM Plex Mono for labels, dates, counts, tags, slugs and code.
  Self-host the woff2 files (no third-party font requests).
- **Colour:** the existing theme tokens (background, surface, text, muted, accent, links, borders,
  code background, focus ring), with slightly adjusted defaults. The accent is used sparingly:
  section labels, the year underline, the active menu item, row hover, the Subscribe box, primary
  buttons. Everything else stays neutral.
- **Topic colours:** tags get one of four muted colour pairs (infra blue, dev violet, web teal,
  life amber) as small labels, sidebar dots and a key next to the first year. Every pair passes
  4.5 : 1 in both themes (light 5.1 to 5.8, dark 7.4 to 8.9).
- **Banner:** the home page opens with a 260 px banner (150 px on phones), the site logo in a round
  frame overlapping it, then the site name, tagline and link buttons.
- **Header:** logo + name, menu (current item underlined in the accent), search field, and the
  System / Light / Dark switch as three icon buttons with visually hidden text labels.
- **Footer:** one mono line: copyright, contact, site links, feeds, "built with Weesite".

## Page by page

- **home**: banner and intro; "Latest posts" grouped by year; each row has the date and reading
  time on the left, the title and summary in the middle, tags on the right (under the summary on
  phones); "All posts, by year" link. Sidebar: top 10 tags with counts, Subscribe box.
- **blog**: all posts grouped by year, all tags in the sidebar, feeds at the bottom.
- **tag**: breadcrumb, the tag as a coloured label in the heading, a one-line count, the tag's own
  feeds in the Subscribe box, other tags.
- **post / photo-post**: breadcrumb, title, mono meta line, a larger first paragraph, code blocks
  with a language label and Copy button, figures with captions from the alt text, tags, author card,
  older/newer links; sidebar "On this page" and "Related posts" (stacked under the article on phones).
- **search**: big field, All / Posts / Pages filter, results with kind, date and matching section,
  highlighted snippets.
- **page**: title, summary as a lede, text.
- **not-found**: large "404", the existing text and link, a search field.
- **admin** (all editor pages): a left sidebar replaces the public header: logo + "editor", "view
  site", nav in two groups (Content: Posts, Pages, Media, Your profile; Site: Site settings, Menu,
  Colour theme, Editors, Audit log) with counts, theme switch, signed-in user and Sign out. On phones
  it becomes a top bar with a scrolling nav.
- **admin-posts**: heading, filter field, "+ New post"; status tabs with counts; table with title +
  slug, status label (Published / Draft / Scheduled with time), date, tags, Edit. Rows on phones.
- **editor**: top bar (breadcrumb, status, saved time, View, Preview, Save); main column: Title,
  Summary with a character count, Markdown with toolbar, "Insert image or file", Write / Split /
  Preview; right column: Status, Dates, Address (slug with a fixed /blog/ prefix), Tags as removable
  labels, Social card preview with Image and alt text, Earlier versions. The right column moves
  under the editor on phones.
- **media-picker**: the same editor with the picker as a modal: upload (drop area + alt text) and
  stored files as a thumbnail grid, Cancel / Insert.
- **admin-media**: drop area with the rules and alt text field; files as a card grid with
  thumbnail, size and the Markdown snippet with Copy.
- **admin-profile, admin-settings, admin-menu**: grouped sections with mono section labels, two
  columns where it helps, numbered link rows, home page choice as three option cards, menu rows with
  arrow buttons and a live preview.
- **admin-theme**: Light and Dark as two cards (fields, swatches and the existing preview), contrast
  table with Passes labels.

## New ideas in the mockups (not in the app yet)

Decide per item whether to build them or drop them from the design:

- banner image (needs a site setting; the contour graphic is a stand-in);
- topic colours (needs a tag-to-colour mapping, ideally a setting next to the colour theme);
- search filters (All / Posts / Pages);
- "On this page" contents and older/newer links on posts;
- code block Copy button (needs JavaScript; the page must still work without it);
- posts table: filter field, status tabs and the Tags column;
- editor: Summary character count, Write / Split / Preview switch, diff and restore on versions,
  dropping files onto the editor;
- media: card grid with Copy buttons and a filter field;
- menu preview; "all 18 checks pass" summary on the theme page.

Public pages must keep working without JavaScript, and the axe checks in the e2e tests still apply.
