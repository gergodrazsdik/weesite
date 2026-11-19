# 0011: Visitor input: contact form, reactions and comments

- Status: accepted (maintainer, 2026-11-19)
- Date: 2026-11-19
- Tracker: T-0154
- Amends: [ADR 0002](0002-public-site.md) (comments were out of scope, Q-0003), [ADR 0010](0010-email-over-smtp.md) (mail beyond invitations)

## Context and problem statement

Up to 1.0, visitors could only read: the site took no input from them, apart from search. Release 1.1 adds three ways in:

- a **contact form**, whose messages reach the site's owner;
- **reactions** to posts (like, love, thanks, insightful), counted in the analytics;
- **comments** on posts, public only after moderation.

None of them has a visitor account: no sign-in, no cookie. Each one invites spam, and each one takes personal data (a name, an address, an IP address on every request). The privacy baseline from 1.0 holds: no raw IP address is stored in a table or written to a log, and the analytics stay cookie-free.

## Considered options

| Question | Options considered |
| --- | --- |
| Bot check | our own invisible proof of work; the ALTCHA widget (v3, MIT); Cap (self-hosted service); Friendly Captcha (hosted); Cloudflare Turnstile, hCaptcha, reCAPTCHA; no proof of work |
| Without JavaScript | forms post without it, unverified input flagged; forms need it; forms post without it, no flag |
| Contact form's place | a fixed `/contact` page with an intro from a content page; a Markdown directive placing it in any page |
| Messages go to | a new private setting; the public contact address; every admin |
| Comment edits | back to moderation and hidden; the approved text stays until the edit is approved; edits only while pending |
| Comment moderators | admins, and authors on their own posts; admins only |
| Editor MCP tools | comments only; comments and messages |

Proof-of-work options in November 2026:

- [ALTCHA](https://github.com/altcha-org/altcha): MIT, about 34 kB gzipped, PBKDF2 and SHA challenges (Argon2 and scrypt as separate workers), claims WCAG 2.2 AA. No .NET server library, and strict CSP needs its "external" build with each worker registered by hand.
- [Cap](https://trycap.dev/): Apache 2.0, but a separate Node service with Redis or Valkey.
- [Friendly Captcha](https://friendlycaptcha.com/): proof of work, but verification is a hosted service, and its EU-only endpoint is a paid plan.
- Turnstile, hCaptcha and reCAPTCHA send every visitor to a third party, need its scripts in the CSP and rely on fingerprinting: against this site's privacy promise.
- Privacy Pass and Private Access Tokens need an issuer Weesite can't run.

Proof of work alone slows bots that run JavaScript but doesn't detect them, so it's one layer among several, not the whole defence.

## Decision outcome

The maintainer chose (2026-11-19):

| Question | Decision |
| --- | --- |
| Bot check | Our own invisible proof of work, in ALTCHA's scheme: no dependency, nothing to see or solve |
| Without JavaScript | The contact form posts without it; such a message is kept and marked **unverified**. The reaction buttons and the comment form appear only with JavaScript, and their endpoints refuse input without a solved challenge. Approved comments are part of the post's HTML, so they're readable without JavaScript |
| Contact form's place | A fixed `/contact` page; its intro is a content page chosen in Site settings, as the privacy policy is |
| Messages go to | A new Site setting, **Messages go to**, never shown on the site |
| Message retention | Until an admin deletes them, at most 12 months |
| Reactions | A fixed set: like 👍, love ❤, thanks 🙏, insightful 💡, each with a translated label |
| Comment edits | The approved text stays public until the edit is approved |
| Comment replies | None in 1.1 |
| Comment text | Up to 2,000 characters, plain text; addresses become links with `rel="ugc nofollow noopener"` |
| Comments on | A Site setting for every post (off by default); `comments: false` in a post's front matter turns them off there |
| Comment moderators | Admins for every post; authors for their own posts |
| Editor MCP tools | Comment moderation only; contact messages never reach an AI app |
| Limits | As below |

### Layers against spam

Every form has all of them; reactions skip the ones about text.

| Layer | What it does |
| --- | --- |
| Proof of work | `GET /visitor/challenge` returns a salt, a SHA-256 hash and a signature. The salt carries the purpose and an expiry (10 minutes); the signature is Data Protection over the hash, salt and purpose, so every replica can check it. The browser finds the number (0 to 100,000 by default) whose hash with the salt matches, in a Web Worker with Web Crypto, while the visitor types. Each challenge works once: its hash is kept until it expires |
| Form token | A Data Protection token in the form with its purpose and render time. Sent within 3 seconds: an error that keeps the text ("wait a moment and send again"). Older than 24 hours: the same, asking to send again |
| Honeypot | A text field hidden with a stylesheet class (`display: none`), `tabindex="-1"` and `autocomplete="off"`, with a name autofill doesn't fill. Filled in: the answer looks like success and nothing is kept |
| Text rules | Plain text only: HTML tags (`<a …>`) and BBCode (`[url=`, `[b]`) are refused with an explanation; Markdown stays as typed and isn't rendered. At most 3 links. Text is normalized (NFC, control characters removed, line endings unified) before its length is checked |
| Rate limits | Counted in Table Storage, so every replica sees the same count: 5 contact messages and 5 comments per hour per address hash; per post, one reaction per day per address-and-browser hash, and 30 reactions an hour per hash (120 per address hash: see the amendment). All configurable (`Visitor:*`). The in-memory per-minute limiter covers the new endpoints too |
| Moderation | Comments only: nothing is public before an editor approves it |

### Address hashes, never addresses

- A hash is HMAC-SHA256 with the day's salt (the analytics salt, shared by every replica and deleted after two days), over the purpose and the address, or the address and the User-Agent for reactions. An IPv6 address is cut to its /64 first, since a client can change addresses within one. 16 hex digits are kept.
- Counters are rows per window and hash; they and the used challenges are deleted after two days. So a hash can't be followed from one day to the next, and nothing stored holds an address.
- Stored messages and comments keep no hash at all.
- The logs, traces and audit entries get no address either. Behind the ingress, `Connection.RemoteIpAddress` (OpenTelemetry's `client.address`) is the ingress's own address, since `X-Forwarded-For` isn't applied, and Azure Monitor masks client IPs. Tests check that the new endpoints log no address.

### Contact form

- Fields: name (100 characters), email (254, checked), message (1,000). Available when it's turned on and **Messages go to** is set; the page says so otherwise.
- Stored in the `messages` table: name, email, text, received, read, unverified. Nothing else about the sender.
- The admin's **Messages** page (admins only): newest first, unread marked and counted in the menu; read, delete, delete every read message. Deletions are audited.
- **Hourly digest** to **Messages go to**: every message since the last digest, as escaped plain text with an escaped HTML alternative, each sender's address as a `mailto:` link. Sender input never goes into a mail header. The first message after a quiet hour goes out within a minute; later ones wait for the hour. A `digests` row (ETag) lets one replica send. Test scales to zero: a waiting digest goes out when the app next wakes. Without SMTP there's no digest, only the Messages page.
- Kept until deleted, and deleted automatically after 12 months.

### Reactions

- Below a post, with JavaScript only: a button per reaction (`aria-pressed`, the emoji hidden from screen readers, the label and count read out) and its count. The browser remembers what it gave in `localStorage`; a second press takes it back. The server counts a take-back only if it has that hash's reaction from the same day; otherwise only the browser forgets, as it's best effort.
- Tables: `reactions`, the totals per post (ETag increments), and `reactionevents`, one row per reaction or take-back in day partitions without any hash, kept 13 months like page views.
- The analytics page gets the reactions in the period by kind and by day, and the posts with the most.

### Comments

- The form under a post, with JavaScript: name (60 characters, public) and comment (2,000). Each comment waits for moderation, and the page says so.
- **Its token**: 32 random bytes, returned once; the table keeps only its SHA-256. The browser keeps it in `localStorage` with the comment's ID and shows Edit and Delete on its own comments, and its own pending ones as "waiting for moderation". The token goes in a header, never an address. Delete removes the comment at once. An edit waits for moderation while the approved text stays public; a rejected edit leaves the approved text.
- Stored in the `comments` table per post, keyed by the post's source path, which a slug change doesn't change: name, text, pending text, status, created, edited, token hash. Rejected comments are deleted after 30 days.
- Public: approved comments in order, each an `<article>` with the name and date, line breaks kept, part of the post's HTML. Each replica keeps the approved comments in memory and reloads them when a change marker in the table moves, clearing the output cache of the post. Comments aren't in search, feeds, `llms.txt` or Markdown answers.
- The admin's **Comments** page: new comments and edits first; approve, reject, delete. Admins moderate every post's comments, authors their own posts'. Audited.
- An hourly digest to each moderator with comments waiting, in the same way as messages: an admin gets all, an author those on their posts.
- Editor MCP tools, with the same rights: `list_comments`, `moderate_comment` (approve or reject), `delete_comment`.

### Accessibility

- The honeypot is hidden with `display: none`, which takes it out of the tab order and the accessibility tree, so keyboard and screen reader users never meet it. Off-screen positioning or `opacity` would leave it announced; they aren't used.
- Proof of work needs no perception or puzzle (WCAG 1.1.1's note on CAPTCHAs). If a visitor sends before it's done, the button says so in an `aria-live` region and sends when it is.
- The minimum time isn't a time limit in WCAG 2.2.1's sense, and its error keeps the input and says what to do (3.3.1, 3.3.3). The 24-hour expiry is over 2.2.1's 20-hour exception.
- Axe checks every form in the browser tests.

### Consequences

- Visitors get mail-like personal data into the site: the privacy policy names each kind, why, how long it's kept, and the mail provider as a processor for digests. `localStorage` holds reactions given and comment tokens; it's not a cookie and never leaves the browser except as the token header.
- Five new tables in the existing storage account: `messages`, `comments`, `reactions`, `reactionevents`, `visitorlimits` (with the used challenges and the digest state). No infrastructure change.
- ADR 0010's "nothing to visitors, no notifications" changes: digests go to the messages address and to moderators. Still nothing to visitors.
- ADR 0002's decision on comments is reversed for comments only; a newsletter stays out of scope.
- The public site is no longer fully usable without JavaScript: reactions and commenting need it. Reading stays possible without it.
- Replies to comments, by the author or by visitors, may come later.

## Amendment (2026-11-19): one reaction per post, and times in the site's zone

From the maintainer's first UAT (T-0157, T-0158, F-0188):

- **One reaction per post.** A visitor gives one reaction to a post and can change or remove it, as on social sites. The post shows the reactions given as emoji with their counts (the names for screen readers and as tooltips), only the kinds given, without a total; a React button opens the four to choose from, as a disclosure (`aria-expanded`, the choices `aria-pressed`, Escape and the arrow keys). A change is one request naming the reaction it replaces, which the server takes back only if that hash gave it today. The server counts one reaction per post per day from an address-and-browser hash.
- **A limit per address alone.** Besides 30 an hour per address-and-browser hash, 120 an hour per address hash (`Visitor:ReactionsPerHourPerAddress`), so a script that changes its User-Agent stops there. The hourly limits are checked before a replaced reaction is taken back, so a refused change keeps the old one.
- **The analytics** break each post's reactions down by kind, with their total.
- **Times** are stored in UTC and shown in the site's time zone and clock (Site settings): the Messages and Comments pages, the digests and the analytics days.

## Amendment (2026-11-19): comments, second round

From the maintainer's second UAT round (T-0161, T-0155 simplified):

- **1,000 characters**, as for contact messages, and the name a visitor writes under is kept in their browser (`weesite:commenter`).
- **Ten at a time**, oldest first: the rest are in the page, hidden, and **Show more** opens ten more; without JavaScript, `?comments=all` shows them all.
- **Replies are quotes**: lines starting with `>` or `|` show as quotes, and Reply quotes a comment into the form, as in mail. No threads.
- **Editors comment under their own name** when signed in (the editor cookie reaches `/visitor`), marked Author on their own post and Editor elsewhere; an admin's, and the post's author's, are public at once, without the proof of work or the limits, as they'd approve them anyway.
- **Moderating on the post**: a signed-in moderator sees the post's waiting comments there with Approve and Reject (`/visitor/comments/moderation`: the editor cookie, and JSON, which no other site can send without asking first). Admins can reject a published comment, there or through MCP; the admin's Comments page drops its Published tab, which wouldn't scale.
- **The look**: a coloured bar on each comment's left (the post's author, editors, the visitor's own, waiting ones; each said in words too), the controls at the top right, dates such as "2 days ago" with the exact time on hover or a click.

## More information

- [ALTCHA](https://github.com/altcha-org/altcha) and its [server integration](https://altcha.org/docs/integration/server/)
- [Self-hosted CAPTCHA comparison, 2026](https://privatecaptcha.com/blog/self-hosted-captcha-comparison/) (written by a vendor)
- [WCAG 2.2: Non-text Content, CAPTCHA](https://www.w3.org/WAI/WCAG22/Understanding/non-text-content.html)
- [MDN: Web Crypto `digest`](https://developer.mozilla.org/docs/Web/API/SubtleCrypto/digest)
