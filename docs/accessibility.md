# Accessibility

Weesite aims at [WCAG 2.2](https://www.w3.org/TR/WCAG22/) level AA on the public site and in the admin. This page says what the tests check on every change and what a person checks by hand, with a checklist for a screen reader pass.

## Checked by the tests

`tests/Weesite.Web.EndToEnd/AccessibilityTests.cs` runs in CI with the other browser tests:

| Check | Pages | WCAG |
|---|---|---|
| axe, with the WCAG 2.0 to 2.2 A and AA rules and axe's best practices | Every public page in both themes at 320 and 1280 pixels; every admin page, light at 320 and dark at 1280 | Many: names, contrast, structure, ARIA, target size (2.5.8) |
| No sideways scrolling at 320 CSS pixels (400 % zoom) and at 640 (200 % on a 1280 screen); tables and code may scroll in their own region | The same pages | 1.4.10 Reflow, 1.4.4 Resize text |
| Tabbing through a page: every stop shows the focus, in view and not covered, and the focus comes back round | Home, a post, search, and the admin's posts, editor, media and settings | 2.4.7 Focus visible, 2.4.11 Focus not obscured, 2.1.2 No keyboard trap |
| No animation or smooth scrolling when the reader asks for reduced motion | Home and a post | 2.3.3 Animation from interactions |

Other browser tests cover the dialogs (share, enlarge an image, the editor's dialogs, confirmations): focus goes into them, Escape closes them, and focus comes back. Locators go by role and accessible name, so a missing name fails a test. Screenshots (`just screens`) show both themes at both widths.

## Checked by hand

Automated checks find perhaps a third of the problems. These need a person:

- How a screen reader announces things: names that are present but unclear, status messages, the order things are read in.
- Text spacing (1.4.12): set line height 1.5, paragraph spacing 2, letter spacing 0.12 and word spacing 0.16 with a bookmarklet or extension; nothing may be cut off or overlap.
- Windows contrast themes (forced colours): focus, buttons, the tabs and the current item still show.
- Zoom in the browser to 200 % and 400 % on a real screen, and text-only zoom in Firefox.
- A phone's own screen reader (VoiceOver or TalkBack) on the public site.

## Screen reader checklist (NVDA)

NVDA (free, nvaccess.org) with Chrome or Firefox on Windows, on the test site. Use the keyboard only. Useful keys: H and 1 to 6 (headings), D (landmarks), K (links), F (form fields), B (buttons), Insert+F7 (lists of headings, links and landmarks), Insert+Space (switch between browse and focus mode), Insert+Up (say the current line). Note each problem with the page, what you did, what NVDA said and what you expected.

### Public site

- [ ] **Home:** the page title is read on load. "Skip to content" is the first Tab and lands on the main content. Landmarks (D): banner, navigation, search, main, content info. Headings (H) give a sensible outline, one level 1.
- [ ] **Theme switch:** each button says its name and whether it's pressed; choosing one says nothing odd and keeps focus.
- [ ] **Search:** the field is announced as a search field with its label. Enter searches; the results page says how many results, and each result's link names the post.
- [ ] **Blog list and paging:** each post is a link with its title; the date is read as a date; the pager says which page is current.
- [ ] **A post:** the breadcrumbs are a navigation named "Breadcrumbs" and the current page is marked. "On this page" (open the list at the top, or the side column) moves to the section. Each section heading's link is announced as "Link to this section: …". Code blocks: the Copy button says it copied. An enlargeable image is a button named "Show at full size: …"; the dialog has the image's name, Escape closes it, focus comes back.
- [ ] **Share:** the button opens a dialog with a name; the options are buttons or links with clear names; copying the link says so; Escape closes it.
- [ ] **Tags:** tag links say the tag; the tags page lists them with counts.
- [ ] **A page in Hungarian** (or another language): NVDA switches voice for the text marked with that language.
- [ ] **Not found and error pages:** the title and heading say what happened, with a way on.

### Admin

- [ ] **Sign in:** the providers' buttons have names; an error is announced.
- [ ] **Posts and pages lists:** the filter field has a name and Enter filters; the status tabs say which is current and their counts; each row's links and actions name the item.
- [ ] **Editor:** every field has a label (F through them); the summary's help gives its limit of about 160 characters (the visual count is hidden from screen readers); the Markdown toolbar is one Tab stop and the arrow keys move along it, each button with its name; Write, Split and Preview say which is chosen; the live preview is a region you can read.
- [ ] **Saving:** Ctrl+S saves; "Saved." is announced; a save that fails is announced and "Go to field" moves to the field, which says it's invalid.
- [ ] **Status:** Draft, Published, Scheduled and Archived are radio buttons in a group; the publish time field appears for Scheduled.
- [ ] **Tags field:** adding and removing a tag is announced ("Added #web."); each tag's remove button names it.
- [ ] **Media picker and social card image:** the dialogs have names; the file list says how many files; choosing a file is announced; the description field has its help.
- [ ] **Media page:** the tabs (All, In use, Unused, Archived) say which is current; each card's actions name the file; Copy says it copied; Replace, Archive and Delete open pages or dialogs that say what will happen.
- [ ] **Versions and the comparison:** the version table reads by row; on the comparison, each changed line says whether it was removed or added; the two version lists have labels.
- [ ] **Settings, menu, tags, theme, profile:** each field has a label and help; saving says "Saved."; errors are announced.
- [ ] **Editors and analytics:** tables have headers and read by row; the invitation dialog has a name and returns focus.
- [ ] **Leaving with unsaved text:** the browser's "Leave site?" question is announced.

Record the results in `docs/plan/TRACKER.md` under T-0123, one finding per problem.
