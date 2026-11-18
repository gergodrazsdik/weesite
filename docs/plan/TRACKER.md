# Weesite work tracker

What's open and next, from release 1.0 on. Agents keep it current; humans skim it. Rules are in [AGENTS.md](../../AGENTS.md#work-tracker); decisions are in the [ADRs](../adr/README.md). Everything from the first commit to 1.0, including the IDs that code comments cite, is in the [tracker archive](../history/tracker-1.0.md); the lessons from it are in the [engineering notes](../engineering-notes.md).

Legend: [ ] todo · [~] in progress · [x] done · [-] dropped · [!] blocked/needs a decision

Next free IDs: T-0144, F-0176, Q-0010.

## Now

<!-- the next 3–5 items, in order; agents pick work from here -->

1. Tool updates as they turn two weeks old (T-0135)
1. Waiting: the runtime base image's rebuild (T-0133), Checkov's dependencies by 2026-12-14 (T-0132), and dynamic registration off in July 2027 (T-0134)

## Tasks

- [x] T-0065 Release 1.0: start `CHANGELOG.md` (Keep a Changelog) and keep it from then on; put the changelog item back into the PR template
  - [x] The changelog with the 1.0 entry, its rules in AGENTS.md and CONTRIBUTING, the PR template's item, `<Version>` in `Directory.Build.props`, and `just version` and `just release` (`scripts/release.sh`) — 2026-11-16, branch `release/1.0`
  - [x] Promoted and tagged `v1.0.0` with `just release` (maintainer), on the commit prod runs; the GitHub release has the changelog's 1.0 section — 2026-11-16. Its notes' relative link to the install guide doesn't resolve on the release page: `just release` points such links at the tag from now on — branch `fix/release-notes-links`
- [ ] T-0135 Take the tool updates Dependabot doesn't watch once they're two weeks old, as Dependabot's cooldown does (checked 2026-11-16): zizmor 1.32.0 from 2026-11-17, tofu 1.13.9 from 2026-11-19, TFLint 0.66.0 from 2026-11-22, typos 1.52.2 from 2026-11-25, Checkov 3.4.5, k6 2.4.0 and markdownlint-cli2 0.24.1 from 2026-11-26. The pins: `lint.yml` (`*_VERSION`), `.github/actions/setup-infra-tools/` (`action.yml`, and the lock `requirements.txt`, whose header says how), `load-test.yml` (k6). Check again for newer ones then
- [x] T-0136 editorconfig-checker 4 (v4.0.2) on the maintainer's machine (Go 1.27 with Fedora 45) and in CI (`EDITORCONFIG_CHECKER_VERSION` in `lint.yml`), together so the hooks and CI agree. None of v4's breaking changes apply here (no `.ecrc`, no `SpacesAftertabs`, no latin1 files) — 2026-11-17, branch `ci/editorconfig-checker-4`
- [x] T-0137 Upload addresses for the editor MCP endpoint: `create_media_upload` and `PUT /mcp/editor/upload`, so files don't pass through the model as base64 (Claude couldn't send a 195 KB image) ([ADR 0007 amendment](../adr/0007-mcp-for-editors.md#amendment-2026-11-17-upload-addresses)) — 2026-11-17, branch `feat/mcp-upload-url`
- [x] T-0138 Replacing a file through an upload address too (`create_media_upload` with `replaces`), under `replace_media`'s rules, checked when the address is made and at upload (maintainer, 2026-11-17) — 2026-11-17, branch `feat/mcp-upload-url`
- [x] T-0139 Compile with .NET 11's runtime async (`runtime-async=on` in `Directory.Build.props`): every async method of ours but the async iterators drops its state machine (Web 138 to 4); the build and all tests pass, and a micro-benchmark of suspending awaits ran 2.6 times faster with 37 % less allocated — 2026-11-18, branch `build/runtime-async`
- [x] T-0140 A read-only `list_tags` tool on the editor MCP endpoint, so apps reuse the site's tags and add one only when none fits (maintainer, 2026-11-18): published and the editor's own unpublished counts, colour and description, most used first — 2026-11-18, branch `feat/mcp-list-tags`
- [x] T-0141 The edit page says when someone else saved the item meanwhile, in another tab, in the editor or through an app (maintainer, 2026-11-18): it asks `/admin/edit/state` every 30 s while visible and on focus, offers the saved version, and with unsaved changes says a save will meet the conflict; also when the item was deleted — 2026-11-18, branch `feat/editor-changed-elsewhere`
- [!] T-0142 Suggest tag colours and fill in tag descriptions (maintainer, 2026-11-18), looked at 2026-11-18, waiting on the maintainer's answers: group tags by the mean vector of their posts (no model calls; co-occurrence where there are no vectors), map groups onto the palette keeping colours already used, and apply only the rows the editor ticks on a preview page; descriptions show only in the tag page's lede today, so first use them for the tag page's meta description, the tag feeds, the tags index and `llms.txt`, and let apps write them through MCP (needs an ADR 0007 amendment). About three PRs
- [!] T-0143 Visitor countries in the analytics (maintainer, 2026-11-18), looked at 2026-11-18, waiting on the maintainer's answers: DB-IP's IP to Country Lite (CC BY 4.0, monthly MMDB of about 8 MB, no key) read with MaxMind.Db; the app keeps its copy in Blob, loads it in the background after start and refreshes it monthly; the lookup happens where a view is recorded, after the response, and only the country code is stored. Names, not flags, on the analytics page; privacy page, NOTICE and deploy guide updated. About two PRs
- [ ] T-0132 Check Checkov's dependencies again, with each Checkov update and by 2026-12-14 at the latest (maintainer, 2026-11-14): whether Checkov still pins asteval 1.0.6 (the lock in `.github/actions/setup-infra-tools/requirements.txt` overrides it to 1.0.9; drop the override once Checkov allows a fixed version), and whether ecdsa has a fixed version for GHSA-wj6h-64fc-37mp (allowed in `dependency-review.yml`; remove the exception then). Regenerate the lock with the commands at its top, run `grype dir:` on an environment installed from it, and check that Checkov still scans `infra/app`
  - Checked 2026-11-16: Checkov's newest, 3.4.5, still pins asteval 1.0.6, so the override stays; no advisory affects asteval 1.0.9. ecdsa still has no fixed version (the lock has the newest, 0.19.2), so the exception stays. Checkov updated to 3.4.3, the newest release two weeks old, with only Checkov moving in the lock — 2026-11-16, [#338](https://github.com/gergodrazsdik/weesite/pull/338)
- [ ] T-0133 Take Microsoft's rebuild of the runtime base image (`aspnet:11.0.0-resolute-chiseled-extra`) once it fixes the Medium findings in libc and OpenSSL that grype reports: Dependabot opens the PR when the digest changes; check it with `grype` before merging
  - Checked 2026-11-16: no rebuild yet (the pinned digest is still the current one, built 2026-11-10). grype finds 8 Medium in libc6 2.43-2ubuntu2.4, 6 of them fixed by Ubuntu in 2.43-2ubuntu2.7, and 5 Medium in OpenSSL 3.5.5-1ubuntu3.7, none fixed yet. The chiseled image has no package manager, so Weesite's build can't patch them. Next look: after Patch Tuesday, 2026-12-08
- [ ] T-0134 Turn off dynamic client registration (`/oauth/register`) in July 2027 (maintainer, 2026-11-15): the MCP specification of 2026-07-28 deprecated it in favour of Client ID Metadata Documents, which Weesite supports; check first which connected apps still register themselves (Connected apps, "registered itself"), and drop `registration_endpoint` from the metadata ([ADR 0007](../adr/0007-mcp-for-editors.md#amendment-2026-11-15-openiddict-looked-at-again-dynamic-registration-deprecated))

## Findings

<!-- problems found along the way; ID F-NNNN -->

- [x] F-0170 The `mcp-test` runner failed two checks on test that the site passed: it looked for the old wording "needs publish_at" (changed in `55e804d`), and expected the old refresh token to be refused at once, though T-0122's retry window answers it once within a minute. The checks now follow the site; the run on test passed — 2026-11-18, [#345](https://github.com/gergodrazsdik/weesite/pull/345)
- [x] F-0171 HEAD requests found no endpoint on what the app maps itself: 405 on `/media`, the not-found page (404) on the feeds, the sitemap, `robots.txt`, `llms.txt`, the `.md` pages and the Open Graph cards, so link checkers and crawlers saw them as missing (found in the maintainer's upload test from Claude Desktop). They answer HEAD like GET now (`MapGetAndHead`) — 2026-11-18, branch `fix/head-requests`
- [x] F-0172 A stored JPEG can be larger than the upload: every original is re-encoded at quality 85 to strip its metadata (`ImageProcessor`), so a file compressed harder grows (about 4 % and 17 % in the maintainer's tests) and loses a little quality. A JPEG or WebP is now stored as uploaded when its structure holds nothing but the picture (an allowlist of segments and chunks, `ImageMetadata`) and re-encoding wouldn't make it smaller — 2026-11-18, branch `fix/keep-clean-images`
- [x] F-0173 A test emptied the sample site when run outside a git working tree (a copy made with `git archive`): it ran the factory reset on `seed/` and relied on the reset refusing folders inside a working tree. It now builds a working tree of its own in a temporary folder, with a `.git` folder and with a `.git` file — 2026-11-18, branch `fix/reset-guard-test`
- [x] F-0174 Pressing Save in the post and page editor with nothing changed made a new version and an audit entry (maintainer, 2026-11-18). A save whose text is what the version opened already holds writes nothing; the editor says so, and `update_item` answers `unchanged` — 2026-11-18, branch `fix/unchanged-save`
- [x] F-0175 The settings pages (site settings, menu, tags, colour theme, profile) also made a version and an audit entry when saved with nothing changed (maintainer, 2026-11-18). A save that leaves `site.json` (compared as JSON, however it was written) or the profile as it was writes nothing, and the page says so — 2026-11-18, branch `fix/unchanged-settings-save`

## Open questions

<!-- questions for the maintainer; ID Q-NNNN -->

None open.
