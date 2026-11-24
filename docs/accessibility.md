# Accessibility

Weesite aims at [WCAG 2.2](https://www.w3.org/TR/WCAG22/) level AA on the public site and in the admin. This page says what the tests check on every change and what a person checks by hand, with a checklist for a screen reader pass.

## Checked by the tests

`tests/Weesite.Web.EndToEnd/AccessibilityTests.cs` runs in CI with the other browser tests:

| Check | Pages | WCAG |
|---|---|---|
| axe, with the WCAG 2.0 to 2.2 A and AA rules and axe's best practices | Every public page in both themes at 320 and 1280 pixels; every admin page, light at 320 and dark at 1280 | Many: names, contrast, structure, ARIA, target size (2.5.8) |
| No sideways scrolling at 320 CSS pixels (400 % zoom) and at 640 (200 % on a 1280 screen); tables and code may scroll in their own region | The same pages | 1.4.10 Reflow, 1.4.4 Resize text |
| The small links outside the text (menu, tags, footer, breadcrumbs, contents, the lists' feet) at least 24 × 24 pixels | The home page, the blog, a post, the tags and the privacy policy at 390 pixels | 2.5.8 Target Size, without the spacing exception |
| Tabbing through a page: every stop shows the focus (an outline or shadow on it or a near ancestor that's there only while it's focused), in view and not covered, and the focus comes back round | Home, a post, search, and the admin's posts, editor, media and settings | 2.4.7 Focus visible, 2.4.11 Focus not obscured, 2.1.2 No keyboard trap |
| No animation or smooth scrolling when the reader asks for reduced motion | Home and a post | 2.3.3 Animation from interactions |

Other browser tests cover the dialogs (share, enlarge an image, the editor's dialogs, confirmations): focus goes into them, Escape closes them, and focus comes back. Locators go by role and accessible name, so a missing name fails a test. Screenshots (`just screens`) show both themes at both widths.

### The contact form's protection

The contact form ([ADR 0011](adr/0011-visitor-input.md)) holds spam back in ways people never have to deal with, and `ContactTests` checks them:

- **The honeypot** is a text field hidden with `display: none` in the stylesheet, which takes it out of the tab order and the accessibility tree, so keyboard and screen reader users never reach it (its label, "Leave this empty", is a last resort). Hiding it off-screen or with `opacity` would leave it announced; neither is used.
- **The proof of work** needs no perception or puzzle, so nothing has to be seen, heard or solved (the CAPTCHA note under 1.1.1). If someone sends before the browser has finished, a status message says the message goes in a moment, and it does.
- **The minimum time** (3 seconds between showing the form and sending it) isn't a time limit in 2.2.1's sense: nothing runs out. Sent sooner, the form comes back with the text kept and a message that says to press Send again (3.3.1, 3.3.3). A form shown more than 24 hours ago asks the same, past 2.2.1's 20-hour exception.
- **Errors** name the field, link to it ("Go to the field"), mark it invalid, and take the focus.

The comment form on posts has the same honeypot and proof of work (`CommentTests`). It's sent with the page's script and checked by it (`novalidate`, so the site's own messages show, in its language, not the browser's): each error shows beside its field, which points at it with `aria-describedby` and is marked invalid, and in an alert above the form with "Go to the field"; the first field at fault takes the focus. A writer's own comments carry Edit and Delete buttons, beside Reply as on the others; Delete asks again with Cancel and a second button ("Delete for good", focused) rather than a dialog, and the status line says what happened. A comment sent, edited or approved shows in place, and the focus moves to it. Approved comments are plain text in the page, each an `<article>`, and readable without JavaScript. The time beside a comment is a toggle button named by the words it shows ("2 days ago"), the exact time in its tooltip.

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
- [ ] **Blog list:** every post, by year: each year is a heading, each post a heading with its link, then its tags; titles in another language are read in that language's voice. (There's no pager here: the admin's lists have one.)
- [ ] **A post:** the breadcrumbs are a navigation named "Breadcrumbs" and the current page is marked. "On this page" (open the list at the top, or the side column) moves to the section. Each section heading's link is announced as "Link to this section: …". Code blocks: the Copy button says it copied. An enlargeable image is a button named "Show at full size: …"; the dialog has the image's name, Escape closes it, focus comes back.
- [ ] **Share:** the button opens a dialog with a name; the options are buttons or links with clear names; copying the link says so; Escape closes it.
- [ ] **Tags:** tag links say the tag; the tags page lists them with counts.
- [ ] **A page in Hungarian** (or another language): NVDA switches voice for its title, its text, its section headings under "On this page" and its address in the breadcrumbs; the site's own words ("On this page", "Share", "Link to this section") stay in the site's language. The browser tab's title is read in the site's language: a page title can't be marked.
- [ ] **Not found and error pages:** the title and heading say what happened, with a way on.
- [ ] **Contact:** F reaches name, email and message, each with its label and help, and never the hidden "Leave this empty" field. Sending says the message is on its way; an error is read first, "Go to the field" moves to the field, and the field says it's invalid. Sending at once says "Sending in a moment…".

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
- [ ] **Messages:** the menu link says how many are unread; the table reads by row, with "new" and "unverified" as words; a message's page gives the sender, the time and the text; Delete asks first.
- [ ] **Leaving with unsaved text:** the browser's "Leave site?" question is announced.

Record each problem as a finding in `docs/plan/TRACKER.md`. The first pass, before 1.0, is under T-0123 in the [tracker archive](history/tracker-1.0.md).
