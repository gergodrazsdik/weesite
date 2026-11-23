# Engineering notes

What building Weesite up to 1.0 taught: the gotchas, measured numbers and reasons behind the code, by topic, for whoever maintains or deploys it. The IDs in parentheses (T-, F- and Q-) refer to the frozen work log in [history/tracker-1.0.md](history/tracker-1.0.md), where each has its full story.

The guides already cover how to install and run a site ([install.md](install.md), [deploy.md](deploy.md)), develop ([dev-setup.md](dev-setup.md)), test ([tests/README.md](../tests/README.md)), run the load test ([load-test.md](load-test.md)) and connect MCP clients ([mcp.md](mcp.md)). This page doesn't repeat them.

## Azure and Container Apps

### Cold start

- Test scales to zero after five idle minutes, and the next request waits for a new replica. A first measurement took 41 s to the first page: about 22 s of platform work (scheduling, the image pull, Container Apps' own containers) and 17 s of the app's own start (T-0129, F-0063).
- After the startup work below, the first page came after 34.7 to 37.8 s, of which the app's own start was about 14 s. The embedding model (6.8 s) now loads beside the content (6.1 s). What's left is mostly the platform's (F-0107, T-0122).
- MCP clients give up after about 30 s, so the first connection to a sleeping test site fails and a retry works. Test stays at zero replicas because it then costs nothing; prod keeps one replica warm ([mcp.md](mcp.md#troubleshooting)) (F-0063).
- The app logs the time each startup step takes (Key Vault, model, media index, reading, rendering). Read that line before guessing where a slow start comes from (F-0107).

### What made startup faster

- `AZURE_TOKEN_CREDENTIALS=ManagedIdentityCredential`, with one credential shared by Key Vault, storage and the Data Protection key. `DefaultAzureCredential` tries other credentials first. Loading the Key Vault secrets takes about 1 to 1.2 s of the host's start; the app logs it (F-0107).
- The image is published ReadyToRun for linux-x64 only. That also drops other platforms' native libraries: 435 MB unpacked instead of 855 MB (F-0106, F-0107).
- Each image's size is kept with the media index instead of being measured by downloading the original at every start (F-0107).
- The search index builds in the background. Only the very first build is awaited, before the replica reports ready; later builds serve the previous index until they finish (F-0102).
- The seeder skips images already stored. Media names carry the file's hash, and each original goes up after its variants, so an interrupted start leaves nothing that looks done. Before this, test took about 100 s to start (F-0033).

### Scaling

- The default HTTP scale rule (10 concurrent requests) added a second replica after every cold start: requests waiting at the ingress count as concurrent. `infra/app` sets 50, which real load still reaches (F-0106).
- That second replica pulled the image on a fresh node (about 25 s) and stayed for the cooldown. Smaller images shorten this too (F-0106).

### Memory

Measured on the earlier 24-post sample, so expect somewhat more with a larger site.

- With bge, the web replica has 0.5 GiB. A reload after each write used to grow memory from about 200 MB to 470 MB and keep it: original images downloaded again, libvips' operation cache (up to 100 MB, outside the .NET heap) and every vector ever seen. With those fixed, memory stays flat at about 320 to 335 MB through writes and MCP test runs (F-0064).
- libvips' cache is off (`NetVips.Cache.Max = 0` in `ImageProcessor`). Dispose every intermediate `Image`: two undisposed ones per social card were a leak (F-0064).
- With `multilingual-e5-small`, test used about 450 MB of its 1 GiB, against about 300 MB with bge (T-0060).

### Identities, keys and secrets

- Data Protection keys (sign-in cookies, form tokens) live in Blob Storage and are wrapped with a Key Vault key. Kept per container, they were lost at every restart, and one replica couldn't read another's cookies (F-0031, T-0078).
- A new or rotated Entra client secret takes about a minute to work everywhere. The first sign-in right after an apply can fail with a 400 from Microsoft; the next one works (F-0030).
- The Entra app's owner is the apply identity's service principal, looked up by name. `azuread_client_config` would give the plan identity, because the plan computes it, and the apply identity then can't change the app under `Application.ReadWrite.OwnedBy` (F-0029).
- Region limits: a subscription has a per-region quota of Container Apps environments, and some regions refuse new customers for some services (`RequestDisallowedByAzure`). Probe a region before moving to it ([deploy.md](deploy.md#bootstrap-inputs)) (F-0019).

### Logs and telemetry

- At Information, the Azure SDK logs about 15 lines per storage request: some 240,000 lines a day on an idle test site. `appsettings.json` sets `Azure` to Warning and `Azure.Core` to Error; the expected 404 and 409 answers are warnings, and real failures throw (F-0047).
- Traces are sampled (a quarter by default), but every log goes to Application Insights (`EnableTraceBasedLogsSampler = false`). With trace-based log sampling, the warning that explained a refused sign-in was dropped with its unsampled request (F-0159).
- In the first days of the editor MCP endpoint, calls from a client stalled a few times on test for up to 30 s on a warm replica, with no request in Application Insights; retries took under half a second. It hasn't been seen since. The cause was between the ingress and the client, not the app: if it comes back, look there (F-0062).
- The Container Apps ingress doesn't compress responses, so the app does (T-0077).
- The availability test runs from two locations every 15 minutes. Five locations every 5 minutes would cost about $28 a month (T-0054).

## OpenTofu

- azurerm 5.0 registers no resource providers by default, and the apply identities only reach their resource group, so the bootstrap registers what `infra/app` needs. Provider pins use patch ranges (`~> 5.12.0`), so new releases arrive through Dependabot, not through `tofu init` (F-0010).
- Before an environment's first apply there's no `app.tfstate`, and the read-only plan identity can't create one (403 on the lock). `scripts/infra-plan.sh` reports "no state yet", and the apply job plans and applies with the lock (F-0013).
- azurerm 5.11 fails the refresh with a 404 on `azurerm_storage_table` when its storage account is gone, instead of dropping it from state. When every resource in a state is gone, delete the state blob (versioning keeps it) with a temporary data role, and the next apply starts fresh (F-0020).
- azurerm adds the `hidden-link` tag to an Application Insights web test and leaves it out when it reads tags back. Naming that tag in the configuration made every plan show drift (F-0057).
- `infra-apply` compares a push with the commit of the last successful `infra-apply` run (`scripts/last-applied.sh`), not with the previous push. `just merge-stack` pushes once per PR, GitHub cancels all but the newest waiting run of a concurrency group, and comparing with `github.event.before` skipped real changes (F-0048).
- Dependabot's OpenTofu PRs fail the required `infra-plan` check: their runs get Dependabot's secrets, which hold no Azure IDs. The maintainer takes each such update into a branch of their own and plans it there (F-0130, Q-0009).
- The plan identity needs a custom role for two actions, and the sign-in client secret sits in the state: both are explained in [infra/bootstrap/README.md](../infra/bootstrap/README.md#identities-and-roles) and [policy/README.md](../policy/README.md#not-covered-on-purpose) (F-0023, T-0122).

## Policy and Checkov

- Checkov on the plan JSON ignores the inline `#checkov:skip` comments; no `--repo-root-for-plan-enrichment` variant mapped plan resources to their code. `scripts/infra-plan.sh` therefore skips, in the plan tier, every check waived anywhere in the root, and `infra-static` checks each waiver on its own resource (F-0018).
- Checkov installs from a hash-locked `requirements.txt` in `.github/actions/setup-infra-tools/`, which Dependabot doesn't watch: update it by hand with the commands at its top, then run `grype dir:` on an environment installed from it. The lock overrides Checkov's pin of asteval 1.0.6 to 1.0.9, and `dependency-review.yml` allows ecdsa, which has no fixed version for its advisory. Drop both once upstream allows it (T-0132).
- How the Rego baseline judges HCL and plans, and how waivers work: [policy/README.md](../policy/README.md) (F-0009, F-0012).

## GitHub, CI and dependencies

- GitHub's default `run` shell is `bash -e` without `pipefail`, so `cmd | tee` hid failures. The workflows set `defaults.run.shell: bash`, which adds `-o pipefail` (F-0014).
- Dependabot and NuGet both wait before proposing a new release (`cooldown`, and `minPublishAgeHours` in `nuget.config`): 14 days until 2026-11-23, then 7, with 14 for NuGet's major versions, as `scripts/tool-updates.py` does for the tools. Fast-moving tools such as Checkov release about twice a week, and 7 days still outlasts how long a bad release usually stays up. Dependabot's first PR came minutes after the first push, for a four-day-old release (F-0008, F-0016).
- Security releases skip the wait. NuGetAudit fails the build at every severity, transitive packages included, so an advisory stops every PR until the fix is taken (T-0064, F-0058).
- Linter versions are pinned in `.github/workflows/lint.yml`, which Dependabot doesn't update: bump them by hand. pinact v5 needs Go 1.27 (F-0002, F-0003).
- zizmor reads ignore comments only as YAML comments, so the pinned `npm install -g markdownlint-cli2` is its own one-line step with the waiver on it. A `package.json` with a lockfile would remove the waiver (F-0006).
- The Azure Storage SDK moves to new service API versions faster than Azurite: SDK 12.29 needed Azurite 3.37, and 12.30 sent a version no Azurite release accepted. The tests (`tests/Shared/Containers/TestAzurite.cs`) and the AppHost start Azurite with `--skipApiVersionCheck` (F-0021, F-0129).
- The runtime base image is chiseled and has no package manager, so Weesite's build can't patch its OS packages. grype's Medium findings in libc and OpenSSL wait for Microsoft's rebuild; Dependabot opens a PR when the digest changes. Scan it with grype before merging (T-0133).
- Dockerfile `ADD` from a URL writes the file as root with mode 600, and the non-root app couldn't read the model. The Containerfile uses `ADD --chmod=444`; a model that fails to load leaves search on keywords instead of stopping the app (F-0026).
- libvips draws card text through Fontconfig, and the chiseled image has no Fontconfig configuration and a read-only `/tmp`. The image sets `FONTCONFIG_FILE` to the app's own `Fonts/fonts.conf` (F-0024).
- CI installs only Chromium's headless shell (`playwright.ps1 install --only-shell chromium`), without `--with-deps`. The tests run headless, and the runner image has the browser's libraries already; `--with-deps` only added optional fonts through apt. Ubuntu's mirror was once unreachable for hours (F-0059), and on about half the runs it was slow enough to hold the step for four minutes (F-0179).
- `scripts/github-settings.sh` only checks `allow_forking`, which GitHub refuses (422) to set on public personal repositories, and retries calls when GitHub's API times out (F-0007).
- A syntax error in an admin script passed the build. `scripts/check-js.sh` parses every script as an ES module, as the pages load them (F-0038).

## Tests and flaky tests

- CI and `just test` run the browser tests first, alone, then the other projects. Starting Azurite containers changes the runner's network, and Chromium then drops scripts mid-load or hangs. It costs about a minute (F-0043).
- Page loads in the browser tests go through `Navigation.OpenAsync`, which retries once on `net::ERR_NETWORK_CHANGED` and on a load "interrupted by another navigation". Use it for every page load (F-0037, F-0118).
- A clipboard write in headless Chromium never finishes without permission: grant `clipboard-read` and `clipboard-write` to the browser context (F-0041).
- Check an address with `Expect(Page).ToHaveURLAsync`. `WaitForURLAsync` waits for the load state when the address already matches, and timed out on CI (F-0104).
- The MCP SDK's client probes `server/discover` with a 5 s timeout of its own, and cancelling it shows up as a 499. The test client turns that timeout off and relies on the 60 s initialization timeout (F-0034).
- Regex timeouts are wall-clock, so a busy runner fired a 100 ms timeout on a 20-character input. Every pattern is anchored and bounded and uses `RegexOptions.NonBacktracking` instead (F-0025).
- Load TextMate grammars once per process. Each highlighter loading its own (about half a second of CPU) took the web tests from 9 s to 55 s. Grammar loading also sits inside the tokenizer lock: two threads loading at once failed in TextMateSharp's registry (F-0027, F-0022).
- Each test app keeps its Data Protection keys in memory. Sharing the profile's key folder made parallel apps race to create the first keys (F-0044).
- Browser tests that write get a site of their own (`EditorTests`), so the shared site keeps the seed for the structure snapshots and `just screens` (F-0071).
- A page's address changes when it starts loading, before its module scripts run. A browser test that leaves right after `ToHaveURLAsync` can cancel them: wait for what the script does instead (F-0185).
- The seed's scheduled posts are due in 2099: with real dates, tests would start failing when the clock passes them (F-0118).
- The settings test scans the code for every setting read by name and checks `appsettings.example.json` lists it (F-0118).
- Azurite has no blob versioning, so bugs in version history show only in Azure. Tests had a `site.json` too, and missed that the first save on an empty prod site always failed. Test the empty-site case and check version features on test (F-0054, F-0055).
- Suite times measured at 1.0: the web tests 62 to 66 s and the whole suite 73 to 75 s, after read-only tests started sharing one app and highlighted code blocks were cached (T-0122).
- Screenshots aren't compared pixel by pixel: [tests/README.md](../tests/README.md) (F-0017).

## Local development and the Claude Code sandbox

- What runs inside the sandbox and what doesn't is in [CLAUDE.md](../CLAUDE.md). Project settings can't add `excludedCommands`, so the exclusions for `tflint`, `just infra` and the recipes that call `gh` live in each maintainer's user-level settings (F-0004, F-0011).
- The file stores (content, settings, media in tests and local runs) write a temporary file and rename it into place. A plain write empties the file first, and a concurrent reader found nothing to parse (F-0061).
- With `Seed:Path` (Aspire), the seeder writes a blob only when it's missing or its seed file changed (`seedhash` metadata), and keeps one saved in the editor. Writing every file at each start made dozens of versions and would have reverted edits (F-0035).

## Blazor static SSR and the admin

- There's no `<base href>`: with it, `#section` resolved to `/#section`, and the skip link, "On this page" and footnotes went to the home page. Asset URLs are absolute instead (F-0040).
- Save in the `EditForm`'s `OnSubmit` ([AGENTS.md](../AGENTS.md#code-principles)). A plain-text 400 after a save that went through looks like an antiforgery rejection, but came from navigating before the form had rendered (F-0044, F-0046).
- The forms validate on the server (`FieldError`) plus the browser's own checks (`required`, `pattern`, `maxlength`). Blazor's client-side validation would need `DataAnnotationsValidator` and its script, which the site doesn't load (T-0122).
- Keep the explicit `app.UseAntiforgery()`. Since ASP.NET Core 11, Blazor renders tokens only when it runs, and the import and media upload endpoints validate tokens themselves (T-0122).
- Only components can set Blazor's TempData in ASP.NET Core 11 (its `ITempDataProvider` is internal). The form pages show "Saved." through TempData; endpoints (import, reset, deletion, media, editors) keep their query parameters (T-0122).
- ASP.NET Core marks any response carrying an antiforgery token as uncacheable. The header's theme switch carried one, and every public page went out `no-store`. The theme post now has no antiforgery: a forged post only changes a colour preference (F-0032).
- No compression under `/admin` and `/mcp`: admin pages carry antiforgery tokens, and compressing them with reflected input invites BREACH (T-0056).
- 404 and 410 answers get `Cache-Control: no-store`. Without it, Chromium kept a 410 for a post after it was published again (F-0075).
- Redirects from earlier slugs carry `Cache-Control: public, max-age=86400`. A 301 without freshness stays in browsers for good, so after a slug was given to a new item, readers who had followed the redirect still went to the old target (F-0245, as F-0075).
- A native date input's format follows the browser and can't be set, so the editor's dates are text fields in `yyyy-MM-dd` and `yyyy-MM-dd HH:mm` (F-0036); the time is typed in the site's time zone and stored as UTC (T-0157).
- Times are stored in UTC everywhere and shown in the site's time zone (Site settings, T-0157): the admin, the digests and the analytics days. What machines read keeps UTC: the MCP tools, feeds, the sitemap, structured data, the audit CSV and the logs. Analytics stores page views by UTC day, so a zone's day reads the UTC days either side; the visitor hash changes at UTC midnight, so in another zone a visitor active across it counts twice that day.
- Look up other web components when they're needed, not while setting up: `ws-media-picker.js` loads after `ws-md-editor`, and drop and paste silently did nothing (F-0045).
- The split view lines its panes up by source lines (`scroll-sync.js`): the preview endpoint renders `data-line` on each top-level block and list item (`sourceLines`, never on the public site), and the textarea's line positions come from a hidden copy laid out like it. An empty marker `span` reports the top of its text, not of its line: subtract half the leading, or the panes drift by the leading times the preview's scale (F-0180). Don't give the panes `overscroll-behavior: contain`: a pane that can't scroll, or is at its end, then keeps the wheel from the page (F-0183).
- Unsaved text in the browser is keyed per editor (`weesite:unsaved:<author id>:…`) and cleared at sign-out. One shared key showed one editor's text to another (F-0089).
- Once a save is written, its version bump and reload aren't cancelled with the request (`ContentReloader.ReloadAfterWriteAsync`). A cancelled request used to leave the other replicas on old content (F-0114, F-0120).
- An empty ETag field means "no file yet". On a site without `site.json`, it came back as an empty string and every settings save was refused as a conflict (F-0055).
- The editor marker cookie (`weesite.editor`) grants nothing. It keeps an editor's visits out of the analytics, and a script shows the admin link, so every visitor still gets the same cached page (F-0150).
- The output cache holds anonymous GETs without a query string, per theme and content version, at most 16 MB and ten minutes, and empties on every content reload and again when the search index for it is in, since a post rendered before then has the previous index's related posts (F-0245). Static files stay out of it (T-0056).
- Content lookups are case-sensitive, but routing and the output cache's default key aren't: `/Blog` rendered first was served for `/blog`, with its spelling in the canonical link and the menu. Paths with capitals aren't cached, the key keeps the path's case, and canonical addresses of site-wide pages are lowercase (F-0245).

## Content, Markdown and media

- A content load must never leave a replica stuck. The reloader records a version only after its load succeeded; a file deleted between listing and download is skipped; a file that fails to parse is reported as a problem, not thrown; settings that can't be read keep the last good ones (F-0108).
- Loads run one at a time, so a later load always finishes later. Overlapping loads could replace a just-saved catalog with an older one (F-0042).
- Each Azure blob version has its own creation time, so it can't identify a file's history. A new file gets a `life` ID in its metadata, and the version list keeps versions with the current one's ID, which hides a deleted file's versions from a new file at the same path (F-0054).
- A post's file keeps its first name after a slug change: in Blob Storage, versions belong to the blob's name, so renaming would cut its history (F-0091).
- `/media` answers ranges only from a stream that seeks. A blob's `DownloadStreamingAsync` body doesn't, so Azure answered every range with the whole file and no length; the blob store reads the properties and hands out a stream that downloads from its position when read, so HEAD and 304 download nothing (F-0236).
- Media addresses carry the file's hash and are cached for a year as immutable. Replacing a file therefore stores it under a new address and saves every post and page that used the old one (F-0143).
- Image descriptions live in `settings/media.json`: blob metadata refuses accented text, and the settings file travels with backups and versions (F-0139).
- Kestrel refuses a non-ASCII `Location` header with a 500, so redirects percent-encode their target (F-0113).
- Every route's first segment and every folder in `wwwroot` must be a reserved slug, and a test checks it. A mistake doesn't break the app: routes and static files win over `/{slug}`, so the page would only be unreachable (F-0151, F-0120).
- Rendering limits: 100,000 characters of text and 16 KB of front matter per item, checked on save, preview, MCP, import and load, plus a highlighting time budget per document (T-0122).
- Front-matter dates are read as written: a time's day in its own offset, not the server's (F-0116).
- The Markdown served to agents quotes front-matter values YAML would read as a number, boolean or null, such as a tag `2024` (F-0116).
- Feeds resolve every link, image and `srcset` against the post's own address. Readers don't apply a base address, and `//host` links once became `{origin}//host` (F-0116).
- Image placement (`{.left .small}`) is read by `ImagePlacement`, not Markdig's generic attributes, which also took braces out of ordinary text and allowed any attribute (F-0142).
- The social card leaves emoji out of its text: Inter has none, and a colour emoji font would add about 10 MB for a one-colour mask (F-0085).
- Site import never holds the whole zip in memory: the review keeps no bytes, the apply writes each file as it reads it, and it reads the file count (at most 20,000) before the directory. An overwrite names the version the review showed, so a file changed meanwhile is a conflict (F-0109, T-0122).
- Each settings file is checked against the shape the site writes. `authors.json` is a list, and an import that expected an object refused every backup's profiles (F-0078).
- A profile name created from the sign-in is only a name the provider gave, never the email fallback, so an address can't become a public byline (F-0092).

## Search

The models, their sizes and languages, and how keyword search treats languages are in [languages.md](languages.md#search). The thresholds and their meaning are in `src/Weesite.Web/appsettings.example.json`.

- bge rates unrelated text 0.5 to 0.66, so a fixed similarity floor let noise through ("cheap cloud hosting" found the privacy policy). A meaning hit must also lead the median similarity of all items for that query by `Search:MinLead`, from four items up (F-0056).
- An item scores by the mean of its two best sections, and with e5 each section loses a quarter of how much more it resembles the whole site than the average section does. Scoring by the best section favoured long, generic pages. Leaving legal pages out, or comparing each section to its ten nearest, did worse (F-0103).
- Quality at 1.0, on the 100-post sample with 45 labelled queries (`SearchQualityTests`; its explicit `Measure` sweeps the thresholds): bge at a lead of 0.155 finds 33 of 42 labelled items, with something unrelated in 5 of 38 queries; e5 at 0.07 finds 28 of 46, with noise in 3 of 42. Re-measure after changing scoring or the sample (F-0103, T-0128).
- FTS5 stems a prefix query before matching ("lay" becomes "lai") and misses the word it starts. The last word of a query, when it isn't a word on the site, is completed to the site's words that start with it (F-0056).
- ".NET" tokenizes as "net". .NET, ASP.NET, C#, F# and Node.js get a shared token in the index and in queries (`Aliases`), and snippets keep the written form. Add more names there as they turn up (T-0079).
- A model's tokenizer must match the original's. bge's was created without removing accents, so every accented word became `[UNK]`. A model's `Version` is part of its stored ID, so fixing a pipeline re-embeds everything through the re-index job (F-0112).
- A web replica embeds at most 200 sections per index build (`Search:EmbedLimit`) and asks the re-index job for the rest. Vectors are stored per item and model in the `index` container, and a reload downloads only the files whose version changed (T-0069, F-0111).
- Embedding is quick: on the earlier 24-post sample, a re-index run on test did 23 items and 71 sections in 1.8 s, and e5 embedded 99 sections at startup in 6.7 s on half a vCPU (T-0069, T-0060).
- The search cache is keyed by the index's own number and the whole query. A key that ignored case and words past the eighth served wrong results (F-0111, F-0119).

## Performance and load test results

The method is in [load-test.md](load-test.md). Results on test (one address, from a GitHub runner, so client times include 100 to 150 ms of network):

- `pages` (5 requests a second) and `scaleout` at scale 1 (22 a second) used at most 64 of 500 millicores: the output cache does the work (T-0129).
- `search` at up to 10 different searches a second used at most 89 millicores. At scale 5 (up to 50 a second, 7,680 searches), p99 stayed at 164 ms with at most 164 millicores on two replicas. Query embeddings are cheap, and the 30-a-minute limit per address caps real traffic far below that (T-0129).
- `scaleout` with 40 users: 8,833 requests, none failed; p95 137 ms for pages, 144 ms for searches and 272 ms for feeds, on one replica at up to 240 millicores (F-0106).
- `scaleout` at scale 5 (200 users, 99 requests a second, 43,578 in all): none failed; p95 183 ms for pages, 212 ms for searches and 338 ms for feeds. A second replica was added under the load and removed after it (F-0106).
- Largest Contentful Paint on test (mobile, CPU 4×, about 1.6 Mbps): a post 0.69 s. The home page took 1.6 to 2.0 s because its banner was a CSS background found only after `site.css`. The banner is now preloaded from the HTML with `fetchpriority="high"`, and phones get its middle third: Lighthouse performance 100, LCP 1.3 s, FCP 0.8 s, CLS 0 (T-0056).
- Fonts were most of the bytes. IBM's split WOFF2 files with `unicode-range`, and the body and heading faces preloaded, took a post's fonts from 347 KB to 132 KB and the home page's from 280 KB to 118 KB (T-0098).
- `site.css` is the only render-blocking file (8 KB compressed). The scripts are modules and so deferred already, and bundling gains nothing over HTTP/2 (T-0077).

## Sign-in, OAuth and MCP

The authorization server's design and its rules are in [ADR 0007](adr/0007-mcp-for-editors.md); connecting clients, consent levels and limits are in [mcp.md](mcp.md).

- Google and Microsoft are asked for `prompt=select_account`. Without it, every sign-in after the first went straight through with the same account (F-0074).
- A sign-in lasts 8 hours, renewed with use, and seven days at most; **Sign out everywhere** ends the rest. The cookie is `__Host-weesite.auth` outside Development, where plain HTTP refuses `__Host-` cookies (T-0122).
- Client ID Metadata Documents may list grant and response types Weesite doesn't use, such as the device code grant. A client needs `authorization_code` and `code`, and other types are ignored, as RFC 7591 allows. Refusing them locked out VS Code and Claude Desktop (F-0067).
- Fetched metadata documents have a cache of their own (at most 1,000 entries). A failed fetch is cached for a minute, a cancelled one not at all (F-0065, F-0115).
- Rate limits are per address because the limiter runs before sign-in: a limit per token would give every made-up token its own. Requests to `/mcp/editor` with a bearer token have their own, higher limit, as an app working for an editor easily makes 60 calls a minute (F-0068).
- `upload_media` takes at most 10 MB, refused before decoding. Base64 in JSON holds a file several times over before libvips decodes it, which a 0.5 GiB replica can't afford; larger files go through the media page (F-0066).
- Once a save, upload, token exchange or revocation has been committed, a failing audit write or client mark is logged, not turned into an error for what it records (F-0115).
- Tool annotations don't make clients ask before a write: Claude Code ran `set_status` without asking. The consent level, enforced by the server, is the guard (T-0082).
- The browser's cookie check and the MCP token check once cached different types under one key; using both within 30 seconds on one replica ended in a 500. Give each cache entry a key of its own kind (T-0122).
- ASP.NET Core 11's cross-origin request protection refuses none of the OAuth and MCP flows of a browser-based client on another site; a test covers it (T-0122).
- The consent page adds the client's origin to the CSP's `form-action`: Chromium checks `form-action` on the redirect that follows the form post. The sign-in page's form may go to the identity providers for the same reason (T-0082, T-0053).
- The editor endpoint's instructions say whether an admin or an author connected the app, and that `editor-…` IDs name a person. An author's app took its refusals for a role mix-up (F-0161).

## Email

How to set up SMTP and the DNS records is in [deploy.md](deploy.md#invitations-by-email) and [ADR 0010](adr/0010-email-over-smtp.md).

- MailKit builds the Message-ID from the machine's name, which in Container Apps is the replica's. Weesite uses the sender address's domain instead (F-0168).
- Turn off the SMTP provider's open and click tracking. Providers add a tracking pixel and rewrite links on their own, which a site that counts page views without cookies or IP addresses shouldn't do. With tracking off, invitations still pass SPF, DKIM and DMARC, also through a forward (F-0169, T-0126).

## Accessibility

What the tests check and the screen reader checklist are in [accessibility.md](accessibility.md).

- A code block whose long lines scroll sideways is a region a keyboard user can't reach. Code blocks render with `tabindex="0"`, so they take focus and scroll with the arrow keys (F-0028).
- axe found nothing on any page at 1.0, but keyboard and reflow tests did: hidden submit buttons took a keyboard stop with nothing to see, and three admin pages scrolled sideways at 320 px (T-0123).
- Screen readers read text nodes together. A count beside a label read as "Posts101" and a tab as "Published94" until they got a separator; plural forms need their own strings ("1 posts") (T-0123).
- Changing a button's text under an `aria-label` says nothing. Copy buttons report through a status region ("Copied.") (F-0117).
- `lang` goes on an item's title, summary and text, not on the page's menus, dates and buttons. With it wrong, NVDA read a Hungarian post's contents in the English voice and "Link to this section" in the Hungarian one (T-0060, T-0123).
- The Markdown toolbar is one tab stop with a roving tabindex; 15 separate stops were a long way to the text (F-0091).
- The live preview and the word count are deliberately not live regions: they change at every key (F-0091).
- Known and left as they are: NVDA reads a colour picker by its value when arrowing (its name comes with Tab), and the browser tab's title is read in the site's language, since a title can't carry `lang` (T-0123).

## Security hardening themes

The general shape of what's in place at 1.0. Report vulnerabilities as [SECURITY.md](../SECURITY.md) says, not in issues.

- **Headers:** a strict CSP with no inline scripts or styles, HSTS outside Development, `nosniff`, `Referrer-Policy`, `Permissions-Policy`, `frame-ancestors 'none'`, `Cross-Origin-Opener-Policy: same-origin`, and no `Server` header. The browser tests fail on any CSP violation (T-0053, T-0122).
- **Sessions:** `__Host-` cookies, a seven-day absolute lifetime, sign out everywhere, and Data Protection keys wrapped with a Key Vault key (T-0078, T-0122).
- **Input:** raw HTML in Markdown shows as text; links and images only to safe addresses; uploads checked by their content as libvips reads it; limits on text, front matter, zips and highlighting time; regexes that run in linear time (T-0053, T-0122, F-0025).
- **Rate limits:** per address on search, sign-in, MCP, the authorization server and the editor's live preview. A deploy check sends sign-in posts with made-up `X-Forwarded-For` addresses and expects the limit, so a client-sent header can't dodge it (`scripts/check-forwarded-for.sh`) (T-0053, T-0122).
- **Infrastructure:** pull requests plan test only; prod is planned only from `main` with its own identity, so a branch's workflow can read neither prod's state nor its Key Vault. Key Vault and storage writes and deletes, and every state access, go to audit workspaces with caps of their own (T-0122).
- **Supply chain:** pinned actions and tools, hash-locked Python installs in CI, 7-day cooldowns (14 for majors), NuGet source mapping, grype on every image (F-0016, T-0122, T-0132).
- **Exports:** the audit log's CSV writes cells that would start a spreadsheet formula as text (T-0122).
- **Analytics:** page views without cookies; no IP address is stored, only a hash of the address and User-Agent with a salt that changes daily and is deleted after a day; views are kept 13 months (T-0052).
