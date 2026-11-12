---
name: mcp-test
description: Run the full test suite of Weesite's MCP endpoints (the public /mcp and the editor /mcp/editor with its OAuth sign-in) against test, prod or a URL, through a timed test runner and through Claude Code's own MCP connection, and write a report with every test case, its result and its response time. Use when asked to test, check or smoke-test the MCP endpoints, the OAuth sign-in or the editor tools on a deployed site.
argument-hint: "[test|prod|<https://site>] [drafts|publish|both]"
---

# MCP test suite

Tests both MCP endpoints of a deployed Weesite (ADR 0007, `docs/mcp.md`) and reports each test case with its result and response time. Arguments: the environment (`test` by default, `prod`, or a site URL), and which consent to test (`both` by default: drafts only, then publishing).

There are two parts, because Claude Code can't time its own tool calls:

- **The runner**, `McpTest.cs` in this folder, a .NET file-based app. It calls both endpoints over HTTP and times every call with a stopwatch. It signs in with its own OAuth client ("Weesite MCP test"), keeps that client and its grant in `~/.cache/weesite-mcp-test/<host>.json` (user-only), and writes the results as JSON.
- **Claude Code's own connection** to `/mcp/editor`: the real client check, read-only.

Run the runner and every network check **outside the sandbox**. It needs the site's host, which isn't on the allowlist, and a loopback listener for the sign-in. Never print, paste or store tokens anywhere else.

## 1. The site

Find the site's address, and use it in every command you give the user. Don't write it into committed files: the repository stays generic.

- `test`: `az containerapp show --resource-group rg-weesite-test --name ca-weesite-web --query properties.configuration.ingress.fqdn -o tsv`, then `https://<that host>`.
- `prod`: `gh variable get PROD_DOMAIN`, then `https://<domain>`. Without a domain, use the same `az` command with `rg-weesite-prod`.
- A URL: use it as given, without a trailing slash.

If `az` isn't signed in, ask the user to run `! az login`, or for the address.

**Prod:** the publishing cycle publishes a test post for a few seconds and changes the feed. On prod, run the drafts part only, unless the user explicitly asks for publishing there. Say so before you start.

## 2. Wake the site

Test scales to zero. After a few idle minutes the first request waits about 40 seconds for a new replica (F-0063), longer than MCP clients wait. Before anything else, poll until the site answers, and tell the user while you wait:

```sh
for i in $(seq 1 36); do s=$(curl -s -o /dev/null -w '%{http_code} %{time_total}' --max-time 10 https://<site>/health); echo "$s"; case "$s" in 200*) break;; esac; sleep 5; done
```

The runner waits too (`--wait`, 180 s by default) and reports the warm-up time as its first test case.

## 3. Claude Code's connection

Run `claude mcp list` and look for servers with the URLs `https://<site>/mcp/editor` and `https://<site>/mcp`. Note their names: tools are named `mcp__<name>__<tool>`.

If the editor endpoint isn't registered, give the user the commands with the site filled in, to run with `!`:

```sh
! claude mcp add --transport http weesite-<env> https://<site>/mcp/editor
! claude mcp add --transport http weesite-<env>-public https://<site>/mcp
```

Then tell them to run `/mcp`, pick `weesite-<env>` and choose **Authenticate**. The browser opens the site's sign-in, then the consent page. The tools appear in this session after that. If they don't, ask the user to reconnect the server from `/mcp`.

**Changing the consent** (drafts only ↔ drafts and publishing): a reconnect reuses the existing grant and doesn't ask again. Tell the user to:

1. open `https://<site>/admin/profile`, find **Connected apps**, and **Revoke** the Claude Code entry;
2. run `/mcp`, pick the server, and choose **Authenticate**;
3. on the consent page, pick the option you need, then **Allow**.

If a call answers "This app may write drafts only", the grant is drafts only. If `/mcp` says the server needs authentication, the grant was revoked or ended. Follow the same steps.

## 4. The runner: drafts only

Run it in the background, because it waits for the browser:

```sh
mkdir -p artifacts/mcp-test
dotnet run .claude/skills/mcp-test/McpTest.cs -- --site https://<site> --consent drafts --report artifacts/mcp-test/<env>-<yyyyMMdd-HHmm>-drafts.json --open
```

Watch its output. When it prints `CONSENT NEEDED`, give the user the address it printed, word for word. Tell them to sign in as an editor, choose **"Read, write drafts and upload media"**, and click **Allow**.

On WSL the Windows browser reaches the runner's `127.0.0.1` listener through localhost forwarding. If it doesn't, ask the user to open the address in a browser inside WSL.

A cached grant that allows the right thing is used without asking. One that allows the other thing is revoked by the runner, which then asks for a new consent.

## 5. The runner: publishing, then the token lifecycle

Skip this on prod unless the user asked for it (step 1).

```sh
dotnet run .claude/skills/mcp-test/McpTest.cs -- --site https://<site> --consent publish --lifecycle --report artifacts/mcp-test/<env>-<yyyyMMdd-HHmm>-publish.json --open
```

The runner revokes the drafts-only grant and asks for a new consent. Tell the user to choose **"Also edit, publish, schedule and archive posts and pages"** this time. Then it runs:

- the publishing cycle on the post `mcp-test-cycle`: scheduled, published, renamed, back, draft, archived;
- the token tests, which end its grant on purpose: refresh rotation, reuse of an old refresh token, and the access token stopping. The next run asks for consent again.

With `drafts` only, run step 4 alone, adding `--lifecycle` to it.

## 6. Claude Code's tools

With the tools from step 3, and reading only: call `list_items` (limit 5), then `get_item` on the first path, then `list_media` (limit 5). Note the time before and after each call (`date +%s.%N`), but call this a wall time with Claude Code's overhead, not a response time.

For the server's own time, read Application Insights for the run's window, if `az` is signed in:

```sh
az monitor app-insights query --app "$(az resource list -g rg-weesite-<env> --resource-type Microsoft.Insights/components --query '[0].name' -o tsv)" -g rg-weesite-<env> --analytics-query "requests | where timestamp > ago(30m) and url has '/mcp' | project timestamp, name, resultCode, duration=round(duration) | order by timestamp asc" --query "tables[0].rows" -o tsv
```

Don't write with Claude Code's connection unless the user asks; the runner covers the writes.

## 7. The report

Read the runner's JSON files and write `artifacts/mcp-test/<env>-<yyyyMMdd-HHmm>.md` (`artifacts/` isn't committed):

- **Summary:** the site (its name from the report, and the address), the date, the consents tested, and passed, failed and total counts. Give the warm-up time separately.
- **One table per group**, in the runner's order, with the columns Test, Result (✅/❌), Time (ms), HTTP and Detail. Groups: Warm-up, Discovery and sign-in, Public endpoint, Editor reading, Editor drafts, Drafts only or Publishing, Tokens.
- **Claude Code's connection:** the calls from step 6, with their wall times and, where found, the server's times.
- **Slow calls:** every call over 1,000 ms other than the warm-up, the slowest first. On a warm site every call should take well under a second. Treat any slow call as a finding to look into: check Application Insights for the same minute (step 6) to see whether the app or the network took the time.
- **Failures:** each with its detail, and what to check next.
- **Left on the site:** the drafts-only test post `mcp-test-draft` (a draft), `mcp-test-cycle` (archived), and the image `mcp-test.png`, which keeps the same name on every run. Runs reuse them rather than adding more. The user can delete them in `/admin`.

Then show the user the summary, the failures and the slow calls, and give the report's path.

If a failure looks like a bug in Weesite, say so plainly, and offer to add a finding to `docs/plan/TRACKER.md`. Don't change the code from within this skill.
