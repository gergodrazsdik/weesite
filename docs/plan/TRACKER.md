# Weesite work tracker

What's open and next, from release 1.0 on. Agents keep it current; humans skim it. Rules are in [AGENTS.md](../../AGENTS.md#work-tracker); decisions are in the [ADRs](../adr/README.md). Everything from the first commit to 1.0, including the IDs that code comments cite, is in the [tracker archive](../history/tracker-1.0.md); the lessons from it are in the [engineering notes](../engineering-notes.md).

Legend: [ ] todo · [~] in progress · [x] done · [-] dropped · [!] blocked/needs a decision

Next free IDs: T-0137, F-0170, Q-0010.

## Now

<!-- the next 3–5 items, in order; agents pick work from here -->

1. Tool updates as they turn two weeks old (T-0135)
1. Waiting: the runtime base image's rebuild (T-0133), Checkov's dependencies by 2026-12-14 (T-0132), editorconfig-checker 4 with Go 1.27 (T-0136), and dynamic registration off in July 2027 (T-0134)

## Tasks

- [x] T-0065 Release 1.0: start `CHANGELOG.md` (Keep a Changelog) and keep it from then on; put the changelog item back into the PR template
  - [x] The changelog with the 1.0 entry, its rules in AGENTS.md and CONTRIBUTING, the PR template's item, `<Version>` in `Directory.Build.props`, and `just version` and `just release` (`scripts/release.sh`) — 2026-11-16, branch `release/1.0`
  - [x] Promoted and tagged `v1.0.0` with `just release` (maintainer), on the commit prod runs; the GitHub release has the changelog's 1.0 section — 2026-11-16. Its notes' relative link to the install guide doesn't resolve on the release page: `just release` points such links at the tag from now on — branch `fix/release-notes-links`
- [ ] T-0135 Take the tool updates Dependabot doesn't watch once they're two weeks old, as Dependabot's cooldown does (checked 2026-11-16): zizmor 1.32.0 from 2026-11-17, tofu 1.13.9 from 2026-11-19, TFLint 0.66.0 from 2026-11-22, typos 1.52.2 from 2026-11-25, Checkov 3.4.5, k6 2.4.0 and markdownlint-cli2 0.24.1 from 2026-11-26. The pins: `lint.yml` (`*_VERSION`), `.github/actions/setup-infra-tools/` (`action.yml`, and the lock `requirements.txt`, whose header says how), `load-test.yml` (k6). Check again for newer ones then
- [ ] T-0136 editorconfig-checker 4 (v4.0.2 is out) once the maintainer's machine has Go 1.27 with Fedora 45 (maintainer, 2026-11-16): the machine and CI (`EDITORCONFIG_CHECKER_VERSION` in `lint.yml`, which already has Go 1.27) move together, so the hooks and CI agree; check its changes for a major version first
- [ ] T-0132 Check Checkov's dependencies again, with each Checkov update and by 2026-12-14 at the latest (maintainer, 2026-11-14): whether Checkov still pins asteval 1.0.6 (the lock in `.github/actions/setup-infra-tools/requirements.txt` overrides it to 1.0.9; drop the override once Checkov allows a fixed version), and whether ecdsa has a fixed version for GHSA-wj6h-64fc-37mp (allowed in `dependency-review.yml`; remove the exception then). Regenerate the lock with the commands at its top, run `grype dir:` on an environment installed from it, and check that Checkov still scans `infra/app`
  - Checked 2026-11-16: Checkov's newest, 3.4.5, still pins asteval 1.0.6, so the override stays; no advisory affects asteval 1.0.9. ecdsa still has no fixed version (the lock has the newest, 0.19.2), so the exception stays. Checkov updated to 3.4.3, the newest release two weeks old, with only Checkov moving in the lock — 2026-11-16, [#338](https://github.com/gergodrazsdik/weesite/pull/338)
- [ ] T-0133 Take Microsoft's rebuild of the runtime base image (`aspnet:11.0.0-resolute-chiseled-extra`) once it fixes the Medium findings in libc and OpenSSL that grype reports: Dependabot opens the PR when the digest changes; check it with `grype` before merging
  - Checked 2026-11-16: no rebuild yet (the pinned digest is still the current one, built 2026-11-10). grype finds 8 Medium in libc6 2.43-2ubuntu2.4, 6 of them fixed by Ubuntu in 2.43-2ubuntu2.7, and 5 Medium in OpenSSL 3.5.5-1ubuntu3.7, none fixed yet. The chiseled image has no package manager, so Weesite's build can't patch them. Next look: after Patch Tuesday, 2026-12-08
- [ ] T-0134 Turn off dynamic client registration (`/oauth/register`) in July 2027 (maintainer, 2026-11-15): the MCP specification of 2026-07-28 deprecated it in favour of Client ID Metadata Documents, which Weesite supports; check first which connected apps still register themselves (Connected apps, "registered itself"), and drop `registration_endpoint` from the metadata ([ADR 0007](../adr/0007-mcp-for-editors.md#amendment-2026-11-15-openiddict-looked-at-again-dynamic-registration-deprecated))

## Findings

<!-- problems found along the way; ID F-NNNN -->

None open.

## Open questions

<!-- questions for the maintainer; ID Q-NNNN -->

None open.
