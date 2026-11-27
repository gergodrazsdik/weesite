# Weesite work tracker

What's open and next, from release 1.1 on. Agents keep it current; humans skim it. Rules are in [AGENTS.md](../../AGENTS.md#work-tracker); decisions are in the [ADRs](../adr/README.md). The IDs that code comments and docs cite are explained in the archives: from the first commit to 1.0 in [tracker-1.0.md](../history/tracker-1.0.md), from 1.0 to 1.1 in [tracker-1.1.md](../history/tracker-1.1.md); the lessons to 1.0 are in the [engineering notes](../engineering-notes.md).

Legend: [ ] todo · [~] in progress · [x] done · [-] dropped · [!] blocked/needs a decision

Next free IDs: T-0193, F-0262, Q-0010.

## Now

<!-- the next 3–5 items, in order; agents pick work from here -->

1. Faster starts (ADR 0012): the Rust highlighter (T-0189), then the secrets in the background once prod has logged the first tokens' times (T-0192); ONNX Runtime's telemetry (F-0261)
1. Tool updates as they come due (T-0182), Aspire 17 from 2026-12-02 (T-0149)
1. Waiting: the runtime base image's rebuild (T-0133), Checkov's dependencies by 2026-12-14 (T-0132), and dynamic registration off in July 2027 (T-0134)
1. Later: moderating many comments at once (T-0184); a `stable` branch for forks when they need one (T-0183)

## Tasks

- [ ] T-0149 Aspire 17 (released 2026-11-18; restorable and proposed by Dependabot from 2026-12-02, after the 14-day cooldowns): a migration, not a bump, so the grouped Dependabot PR won't pass as it is. The AppHost moves from the deprecated `Aspire.AppHost.Sdk` (ASPIRE012) to `Microsoft.NET.Sdk` with package references, which `aspire update` doesn't do (microsoft/aspire#22383); `AddProject<Projects.Weesite_Web>` (web, reindex) becomes path-based registration (CS0618); C# AppHosts need the Aspire CLI bundle to build, which the ASPIRE010 suppression no longer covers: install the CLI in CI or leave the AppHost out of CI's build. The client packages (`Aspire.Azure.*`, in production too) and ServiceDefaults move with it. Check `just aspire` and the `aspire` MCP server locally, and update the maintainer's Aspire CLI to 17 at the same time, not before
- [ ] T-0132 Check Checkov's dependencies again, with each Checkov update and by 2026-12-14 at the latest (maintainer, 2026-11-14): whether Checkov still pins asteval 1.0.6 (the lock in `.github/actions/setup-infra-tools/requirements.txt` overrides it to 1.0.9; drop the override once Checkov allows a fixed version), and whether ecdsa has a fixed version for GHSA-wj6h-64fc-37mp (allowed in `dependency-review.yml`; remove the exception then). Regenerate the lock with the commands at its top, run `grype dir:` on an environment installed from it, and check that Checkov still scans `infra/app`
  - Checked 2026-11-16: Checkov's newest, 3.4.5, still pins asteval 1.0.6, so the override stays; no advisory affects asteval 1.0.9. ecdsa still has no fixed version (the lock has the newest, 0.19.2), so the exception stays. Checkov updated to 3.4.3, the newest release two weeks old, with only Checkov moving in the lock — 2026-11-16, [#338](https://github.com/gergodrazsdik/weesite/pull/338)
  - Checked 2026-11-18: no change. Checkov's newest is still 3.4.5, pinning asteval 1.0.6; GHSA-wj6h-64fc-37mp still has no fixed ecdsa version (newest 0.19.2)
  - Checked 2026-11-23: no change. Checkov's newest, 3.4.7, still pins asteval 1.0.6; GHSA-wj6h-64fc-37mp still has no fixed ecdsa version (newest 0.19.2)
- [ ] T-0133 Take Microsoft's rebuild of the runtime base image (`aspnet:11.0.0-resolute-chiseled-extra`) once it fixes the Medium findings in libc and OpenSSL that grype reports: Dependabot opens the PR when the digest changes; check it with `grype` before merging
  - Checked 2026-11-16: no rebuild yet (the pinned digest is still the current one, built 2026-11-10). grype finds 8 Medium in libc6 2.43-2ubuntu2.4, 6 of them fixed by Ubuntu in 2.43-2ubuntu2.7, and 5 Medium in OpenSSL 3.5.5-1ubuntu3.7, none fixed yet. The chiseled image has no package manager, so Weesite's build can't patch them. Next look: after Patch Tuesday, 2026-12-08
  - Checked 2026-11-18: no rebuild yet (the pinned digest is still the current one, built 2026-11-10) and no 11.0.1. Next look: after Patch Tuesday, 2026-12-08
  - Checked 2026-11-23: no rebuild yet (the pinned digest is still the current one, built 2026-11-10) and no 11.0.1. Next look: after Patch Tuesday, 2026-12-08
  - Checked 2026-11-24 (T-0176): no rebuild yet (the pinned digest is still the current one) and no 11.0.1. Next look: after Patch Tuesday, 2026-12-08
- [ ] T-0134 Turn off dynamic client registration (`/oauth/register`) in July 2027 (maintainer, 2026-11-15): the MCP specification of 2026-07-28 deprecated it in favour of Client ID Metadata Documents, which Weesite supports; check first which connected apps still register themselves (Connected apps, "registered itself"), and drop `registration_endpoint` from the metadata ([ADR 0007](../adr/0007-mcp-for-editors.md#amendment-2026-11-15-openiddict-looked-at-again-dynamic-registration-deprecated))
- [ ] T-0182 Tool updates Dependabot doesn't watch, as they come due (7 days, a major 14: T-0181); `just tool-updates` says what's due, and the `tool-updates` skill how to take each (checked 2026-11-24)
  - [ ] Checkov 3.4.6 from 2026-11-26 (3.4.10 from 2026-12-01): regenerate the lock, and check its asteval pin and ecdsa (T-0132)
  - [ ] Azurite 3.38.0 from 2026-11-26, with its digest and the AppHost's emulator tag
  - [ ] markdownlint-cli2 0.24.2 from 2026-11-27
  - [ ] zizmor 1.33.1 from 2026-11-29, with a zizmor-action release that knows it (F-0178)
  - [ ] OpenTofu 1.13.10 from 2026-11-30, with `TOFU_SHA256` from its checked SHA256SUMS
- [ ] T-0183 If forks that run releases only want GitHub's Sync fork button (T-0175): a `stable` branch that `just release` moves to each release, a ruleset that lets only fast-forwards in, a repository variable that makes the workflows deploy from it, test's environment allowing that branch in the bootstrap, and the docs (maintainer, 2026-11-23: later, when such forks show up)
- [ ] T-0184 Moderating many comments at once: select several in the admin's Comments list and reject (or approve) them together (security review, 2026-11-24: after 1.1)
- [x] T-0185 An SEO title (`seo_title`) for the `<title>` element only, as Bing flags titles over 70 characters (maintainer, 2026-11-26) — 2026-11-26, [#496](https://github.com/gergodrazsdik/weesite/pull/496)
- [x] T-0186 `set_tags`: an editor tool that adds and removes an item's tags at its current version, as an app that resent the whole file with `update_item` to change a tag made mistakes in it (maintainer, 2026-11-26) — 2026-11-26, [#497](https://github.com/gergodrazsdik/weesite/pull/497)
- [x] T-0187 Preview links for apps: `create_preview_link`, as the preview page needs a browser signed in to /admin and an agent couldn't look at its draft (maintainer, 2026-11-26; ADR 0007 amendment) — 2026-11-26, [#498](https://github.com/gergodrazsdik/weesite/pull/498)
- [x] T-0188 The proof of work derives PBKDF2-SHA-256 keys (5,000 iterations, numbers 0 to 1,000) instead of single SHA-256 hashes: a native solver was about 33 times faster than a browser, now about 1.35 times; Argon2id, scrypt and leading zeros measured and not taken (maintainer, 2026-11-26; ADR 0011 amendment) — 2026-11-26, [#502](https://github.com/gergodrazsdik/weesite/pull/502)
- [~] T-0189 Highlight code with syntect in a Rust library called through P/Invoke, with bat's syntaxes, instead of TextMateSharp: its grammars took 3.4–4.1 s of a prod start and about 134 MB (ADR 0012, maintainer, 2026-11-27). A native stack:
  - [x] The crate: the library and its C interface, built in the `Containerfile` and CI with a pinned toolchain and `Cargo.lock`; cargo in Dependabot and the tool updates; its dependencies and licences reviewed — 2026-11-27, [#509](https://github.com/gergodrazsdik/weesite/pull/509)
  - [ ] `CodeHighlighter` on the library: the same `hl-*` classes, the line and block limits, a budget checked between lines and Oniguruma's retry limit; plain code and one log line when the library can't load
  - [ ] Grammars: bat's set, plus Just from its Sublime package and Bicep and plain HCL converted; every language highlighted today kept, Weesite's fence names too; `grammars.md` lists the set
  - [ ] TextMateSharp removed; `NOTICE` and `dev-setup.md` updated
- [x] T-0190 Use Key Vault's wait (ADR 0012): the first line of `Program` starts the embedding model's load, the highlighter's warm-up and the storage token, and the app logs each scope's first token time. The wait is the managed identity's first token: 3.05 s at the median on test, 2.69 s on prod. In Development the model still loads when search first needs it — 2026-11-27, [#506](https://github.com/gergodrazsdik/weesite/pull/506)
- [x] T-0191 The first search index without the embedding model (ADR 0012): keyword-only while it loads, so the replica reports ready, then a build adds the embeddings. On test the model (7.0 s) outlasts the content (5.8 s) — 2026-11-27, [#506](https://github.com/gergodrazsdik/weesite/pull/506)
- [ ] T-0192 Key Vault's secrets in the background, once T-0190 has logged the first token's time per scope: sign-in providers registered whatever their secrets and checked at each sign-in, the email sender chosen when it sends; sign-in and mail wait for the secrets, public pages don't. With an amendment to ADR 0004

## Findings

<!-- problems found along the way; ID F-NNNN -->

- [x] F-0239 Every table write is logged to the audit workspace: its cost and daily cap follow the site's traffic, and a busy day can stop the audit logs (code review, 2026-11-23) — fixed 2026-11-24, stack #464, #462, applied to test and prod; still to check on 2026-11-25, after a day of traffic: that the audit workspace's `StorageTableLogs` hold only the content and settings writes, no visitor tables' batched writes (maintainer, 2026-11-24); checked 2026-11-24 evening: the visitor tables' own writes are gone, but every batch still came through (about 300 a day on prod, 190 of its 220 KB of table logs), as a batch's log names only the account, not its table; the transformation now drops every batch (the reset's audit entry follows the one an editor causes) — 2026-11-24, [#486](https://github.com/gergodrazsdik/weesite/pull/486), applied to test and prod (prod logged no batch from ten minutes after); to check again on 2026-11-25, after a day of traffic; checked 2026-11-26, two days after: both audit workspaces' `StorageTableLogs` hold only the `audit`, `oauthgrants` and `editors` tables' writes and `CreateTable`, no batches and no visitor tables
- [x] F-0257 An aligned Markdown table column (`|:---:|`) was rendered with an inline style the CSP blocks: not aligned, and a console error per cell, which Search Console reported (maintainer, 2026-11-26) — 2026-11-26, [#499](https://github.com/gergodrazsdik/weesite/pull/499)
- [x] F-0258 The OAuth clean-up crashed at its registrations step on Table Storage: the client listing's select left out the row key, so every client's ID was null; registrations were never deleted (mcp-test run on test, 2026-11-26: 240 failures logged since 2026-11-12) — 2026-11-26, [#500](https://github.com/gergodrazsdik/weesite/pull/500)
- [x] F-0259 Every browser test failed before it started on a machine whose `BROWSER` names its link opener (TUnit.Playwright takes the browser from it): the test process uses Chromium then, and leaves the variable alone outside the run — 2026-11-26, [#503](https://github.com/gergodrazsdik/weesite/pull/503)
- [x] F-0260 Search Console reported every feed as "crawled, not indexed" and listed a text attachment from /media as a search result of its own: feeds and media files that download now say `X-Robots-Tag: noindex`, images stay indexable (maintainer, 2026-11-26) — 2026-11-26, [#504](https://github.com/gergodrazsdik/weesite/pull/504)
- [ ] F-0261 ONNX Runtime's native library tries to reach Microsoft's telemetry endpoint (`mobile.events.data.microsoft.com`) when the embedding model loads: the sandbox blocked it in a local run, Azure doesn't. Find out what it sends and turn it off (`OrtEnv`'s telemetry switch, or the build)

## Open questions

<!-- questions for the maintainer; ID Q-NNNN -->

None open.
