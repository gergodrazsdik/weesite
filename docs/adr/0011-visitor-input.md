# 0011: Visitor input: contact form, reactions and comments

- Status: accepted (maintainer, 2026-11-19)
- Date: 2026-11-19
- Tracker: T-0154
- Amends: [ADR 0002](0002-public-site.md) (comments were out of scope, Q-0003), [ADR 0010](0010-email-over-smtp.md) (mail beyond invitations)

## Context and problem statement

Up to 1.0, visitors could only read: the site took no input from them, apart from search. Release 1.1 adds three ways in:

- a **contact form**, whose messages reach the site's owner;
- **reactions** to posts (like, love, thanks, insightful, funny), counted in the analytics;
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
| Reactions | A fixed set: like 👍, love ❤, thanks 🙏, insightful 💡, funny 😄 (added 2026-11-20), each with a translated label |
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
- **Deleted and edited** (maintainer, 2026-11-20): a published comment its writer deletes leaves a marker, without its name or text, where a later comment follows it; so does one a moderator rejects once published ("removed by a moderator"), which its writer can then no longer edit or delete. One deleted in moderation goes without a trace. A comment whose text changed after it was published is marked "edited".
- **The look**: a coloured bar on each comment's left (the post's author, editors, the visitor's own, waiting ones; each said in words too), the controls at the top right, dates such as "2 days ago" with the exact time on hover or a click.

## Amendment (2026-11-19): the post's action row

From the maintainer's UAT (T-0162): under a post, one row as social sites have it. React shows the visitor's reaction (or a smiley) and the total, and opens the choices; the comment count goes to the comments, and into the comment box; Share; and on the right the reactions given, the three most used stacked, which open the count of each. `ws-reactions` lays out as its children (`display: contents`), so its parts take their places in the row by order. The analytics' Reactions tab shows each post's comments beside its reactions.

## Amendment (2026-11-23): text rules that let technical text through

From the review of the visitor input (F-0214): visitors' text is always shown escaped, so markup is only a spam signal, and refusing every tag and bracket refused ordinary technical text (`List<string>`, "press <Enter>", `arr[i]`, `[code]`). Refused now: HTML that links, embeds or runs something (tags named `a`, `img`, `script`, `iframe`, `svg` and the like, opening or closing), any tag with an attribute (`<x href=…>`) or a slash straight after its name (`<svg/onload=…>`, `<br/>`), and BBCode's links and images (`[url]`, `[link]`, `[img]`, `[email]`, with or without `=`). Everything else, `<b>` and `[b]` included, is kept and shown as typed.

## Amendment (2026-11-23): remove, and deciding on what was read

From the review of comments (F-0209, F-0210):

- **Remove is its own decision.** Reject is for what waits: a new comment, or the edit beside a published one, whose published text then stays. Taking a published comment off its post, with any edit waiting, is **remove**, for admins only: on the post, and through MCP (`moderate_comment` with `remove`). It leaves the "removed by a moderator" marker the second round describes. Before, an admin's reject of a published comment with an edit waiting dropped only the edit, so the comment stayed up.
- **Changes save only at the version they read.** Every change to a comment (its writer's edit or delete, a moderator's decision, the clean-up) is saved at the version it read, and is read and decided again when someone changed the comment meanwhile. Approving also takes the version the moderator saw (a hidden field on the Comments page, the waiting list on the post, `version` from `list_comments`), so what's published is the text they read; a comment edited since is refused as changed.

## Amendment (2026-11-24): limits from the security review

From T-0177's security review:

- **A form token takes one message or comment.** It's used up just before the save; sent again, it's answered as expired, and the form gets a fresh one. The 3 seconds and 24 hours stay. Binding it to the address instead was left: a phone changing networks while its reader writes would lose the text.
- **Visitors' requests are small:** everything under `/visitor` and the contact form's post refuse a body over 32 KB before reading it, and names and texts far over their limits are refused before they're normalized.
- **Edits and deletions are counted:** a writer's edits and deletions of their own comments count against 20 an hour per address hash (`Visitor:CommentChangesPerHour`); signed-in editors aren't counted.
- **Waiting doesn't last:** a comment, or an edit of a published one, that waited 30 days without a decision goes, as rejected ones do (the published text stays). A digest lists at most 50 items, with a count of the rest.
- **Per-minute limits count an IPv6 client by its /64,** as the hourly limits do.

## Amendment (2026-11-26): PBKDF2 instead of SHA-256 for the proof of work

From the maintainer's question whether another function, or a leading-zero target, would do better (T-0188). The maintainer chose PBKDF2-SHA-256 (2026-11-26).

**Decision.** The challenge stays an exact match (ALTCHA's "deterministic mode"): the server picks a number from 0 to `Visitor:ProofMaxNumber` and sends the key it derives. The key is now PBKDF2-SHA-256 of the number as decimal text, with the salt as PBKDF2's salt, 5,000 iterations and 32 bytes, instead of one SHA-256 of the salt and the number. `ProofMaxNumber` goes from 100,000 to 1,000 (at most 100,000). The challenge says `algorithm: "PBKDF2/SHA-256"` and `cost: 5000`. The browser derives with Web Crypto's `deriveBits` in a module worker, one key at a time; the server with `Rfc2898DeriveBytes.Pbkdf2`. No new dependency, and the CSP is unchanged.

**Why.** What matters is how much a bot pays for each second a visitor waits. With one SHA-256 per try, each `crypto.subtle.digest` call has a fixed cost far above the hashing, so a native solver was about 33 times faster than the browser. PBKDF2 pays that cost once per key, as Web Crypto runs the iterations in native code, and the gap shrinks to about 1.35 times. At the defaults a visitor waits about 0.5 s on average (1 s at most) on a laptop, close to the 0.26 s before, and a native solver pays about 0.35 s of CPU for each answer instead of about 8 ms: about 25 times more for each second a visitor waits.

**Measured** on one laptop (i7-11850H, 16 threads, WSL2), 2026-11-26: headless Chromium 156 in Web Workers; native .NET 11 and Node 26 (OpenSSL 3.5); WASM through hash-wasm 4.12.0 in Node 22's V8. One core each:

| Function | Browser | Native | Native's lead | Server's check |
| --- | --- | --- | --- | --- |
| SHA-256, Web Crypto (before) | 195,000/s | 6.5 million/s (SHA-NI, OpenSSL); 2.1 million/s (.NET) | 33× | about 0.5 µs |
| SHA-256, WASM | 966,000/s | 6.5 million/s | 7× | about 0.5 µs |
| SHA-256, plain JavaScript | 298,000/s | 6.5 million/s | 22× | about 0.5 µs |
| PBKDF2-SHA-256, 5,000 iterations, Web Crypto (now) | 1,060/s | 1,450/s (.NET), 1,400/s (Node) | 1.35× | 0.7 ms |
| PBKDF2-SHA-256, 1,000 iterations, Web Crypto | 2,900/s | 6,300/s (.NET) | 2.2× | 0.16 ms |
| scrypt N=2¹⁴, r=8, p=1 (16 MiB), WASM | 33 ms | 27 ms | 1.2× | 27 ms and 16 MiB |
| scrypt N=2¹⁵, r=8, p=1 (32 MiB), WASM | 65 ms | 66 ms | 1.0× | 66 ms and 32 MiB |
| Argon2id 19 MiB, t=2, p=1, WASM | 54 ms | 22 ms | 2.4× | 22 ms and 19 MiB |
| Argon2id 64 MiB, t=1, p=1, WASM | 138 ms | 69 ms | 2.0× | 69 ms and 64 MiB |

- Fewer iterations leave more of each try to Web Crypto's fixed cost per call (about 0.2 ms), which a native solver doesn't pay: 1,000 iterations widen the gap to 2.2 times.
- Chromium doesn't derive PBKDF2 keys faster in parallel: one worker made 1,060 a second, two 1,360, four 1,280, eight 1,130, and 16 overlapping calls in one worker 1,110. A native solver uses every core. SHA-256 through Web Crypto did scale (one worker 222,000 a second, eight 478,000).
- PBKDF2 and SHA-256 suit GPUs; scrypt and Argon2id don't, which is their only advantage here.

**Leading zeros instead of an exact match.** At the same mean, from 3,000 challenges each (about 65,500 tries on average):

| Target | p50 | p90 | p99 | p99.9 | Worst |
| --- | --- | --- | --- | --- | --- |
| Exact match, numbers 0 to 131,072 | 1.0× mean | 1.8× | 2.0× | 2.0× | 2× mean |
| At least 16 leading zero bits | 0.7× | 2.3× | 4.4× | 6.3× | no limit |

A bot pays the same on average either way, but the slowest visitors wait over twice as long with leading zeros. The difficulty also only doubles (16 times with ALTCHA's hex prefix `00`). To match the 50,000 tries on average of before takes about 16 bits: 0.34 s on average and 1.5 s at p99 on the laptop. ALTCHA recommends its deterministic mode too.

**Not chosen.**

- **Argon2id or scrypt**, which ALTCHA's current version supports: they need WASM in the browser (a dependency, and `'wasm-unsafe-eval'` in the CSP's `script-src`) and a NuGet package on the server, since .NET 11 has neither. Each check costs the server their memory and tens of milliseconds of a core, and a replica of the web app has 0.25 vCPU and 0.5 GiB at the smallest size; challenges are free, so anyone can send wrong answers to real ones and make the server check them. They'd help against GPUs, which spam on a personal site rarely uses.
- **ALTCHA's example PBKDF2 setting** (5,000 iterations, the number from 5,000 to 10,000): about 7 s in one Chromium worker.
- **SHA-256 in WASM or plain JavaScript**: 5 or 1.5 times faster in the browser than Web Crypto, but native code stays 7 to 22 times faster, and WASM needs the CSP change.

**Consequences.** The server spends about 0.7 ms of a core on each answer it checks, after the signature, so only answers to its own challenges cost it that. Tests that solve many challenges set a small `ProofMaxNumber`. A site that set `Visitor:ProofMaxNumber` should lower it: each number now costs about as much as 5,000 hashes did. A page loaded before the upgrade keeps the SHA-256 solver, which finds no answer to the new challenges: its comments and reactions fail until it's reloaded, and its contact messages arrive marked unverified. Firefox, Safari and phones weren't measured; a phone may take several times as long.

## More information

- [ALTCHA](https://github.com/altcha-org/altcha) and its [server integration](https://altcha.org/docs/integration/server/)
- [Self-hosted CAPTCHA comparison, 2026](https://privatecaptcha.com/blog/self-hosted-captcha-comparison/) (written by a vendor)
- [WCAG 2.2: Non-text Content, CAPTCHA](https://www.w3.org/WAI/WCAG22/Understanding/non-text-content.html)
- [MDN: Web Crypto `digest`](https://developer.mozilla.org/docs/Web/API/SubtleCrypto/digest) and [`deriveBits`](https://developer.mozilla.org/docs/Web/API/SubtleCrypto/deriveBits)
- [ALTCHA's proof of work](https://altcha.org/docs/integration/proof-of-work-captcha/) (key derivation functions, deterministic mode) and [altcha-lib's options](https://github.com/altcha-org/altcha-lib/blob/main/docs/configuration-options.md)
