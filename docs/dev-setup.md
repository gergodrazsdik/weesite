# Development setup

What you need to work on Weesite. Linux, macOS, Windows and WSL2 all work. On Windows, run the `just` recipes from Git Bash or WSL: they use `sh`.

`just doctor` checks everything below and prints install hints.

## Prerequisites

### Every contribution

| Tool | Used for | Install |
|---|---|---|
| git | Version control, signed commits | <https://git-scm.com/downloads> |
| [just](https://just.systems/) | Task runner | [packages](https://just.systems/man/en/packages.html) |
| [lefthook](https://lefthook.dev/) | Git hooks | [installation](https://lefthook.dev/installation/) |
| [markdownlint-cli2](https://github.com/DavidAnson/markdownlint-cli2) | Markdown lint | `npm install -g markdownlint-cli2` |
| [yamllint](https://yamllint.readthedocs.io/) | YAML lint | `pipx install yamllint` or `uv tool install yamllint` |
| [actionlint](https://github.com/rhysd/actionlint) | Workflow lint | [install](https://github.com/rhysd/actionlint/blob/main/docs/install.md) |
| [zizmor](https://docs.zizmor.sh/) | Workflow security audit | `pipx install zizmor` or `uv tool install zizmor` |
| [typos](https://github.com/crate-ci/typos) | Spelling | `cargo install typos-cli`, `brew install typos-cli`, `pipx install typos` |
| [editorconfig-checker](https://github.com/editorconfig-checker/editorconfig-checker) | `.editorconfig` compliance | [installation](https://github.com/editorconfig-checker/editorconfig-checker#installation) |
| [ShellCheck](https://www.shellcheck.net/) | Shell script lint | [installing](https://github.com/koalaman/shellcheck#installing) |
| [Node.js](https://nodejs.org/) | Script syntax check (`node --check`), and markdownlint-cli2 | [download](https://nodejs.org/en/download) |
| [esbuild](https://esbuild.github.io/) | Checks that the browser scripts and stylesheets minify (`just minify`); the image build minifies them | `npm install -g esbuild`, or `scripts/install-esbuild.sh ~/.local/bin` (Linux x64, the version the image uses) |
| [gitleaks](https://github.com/gitleaks/gitleaks) | Secret scan | [installing](https://github.com/gitleaks/gitleaks#installing) |
| [Conftest](https://www.conftest.dev/) | Rego policy baseline: tests and checks | [install](https://www.conftest.dev/install/) |
| [pinact](https://github.com/suzuki-shunsuke/pinact) | Checks that workflow actions are pinned to SHAs (`just lint`), and pins them (`just pin`) | [install](https://github.com/suzuki-shunsuke/pinact#install) |
| [hadolint](https://github.com/hadolint/hadolint) | Containerfile lint | [install](https://github.com/hadolint/hadolint#install) |
| [gh](https://cli.github.com/) | PRs from the terminal | recommended |

CI pins the versions of these tools in `.github/workflows/lint.yml`, and of the infrastructure tools in `.github/actions/setup-infra-tools/`; other versions may report differently.

### Application

| Tool | Used for | Install |
|---|---|---|
| .NET 11 SDK | Build, test, run | <https://dotnet.microsoft.com/download> |
| [Rust](https://www.rust-lang.org/) | The code highlighter (`native/highlight`, ADR 0012), which `dotnet build` builds with cargo, and `just highlighter` | [rustup](https://rustup.rs/) with the stable toolchain and its rustfmt and clippy (`rustup component add rustfmt clippy`); at least the `rust-version` in `native/highlight/Cargo.toml`. A C compiler too (`gcc` or `clang`), for Oniguruma. Without Rust, build with `-p:WeesiteSkipHighlighter=true`: code then renders plain, and the highlighter's tests fail |
| [Aspire CLI](https://aspire.dev/) 17 | `aspire run`: the app with Azurite and a telemetry dashboard. The AppHost (Aspire 17) builds and runs with the dashboard and orchestrator of the CLI's bundle; without a CLI on `PATH`, its build fetches the `Aspire.Cli` package of its own version from NuGet and runs it with `dnx` (see [Aspire](#aspire)) | [install](https://aspire.dev/get-started/install-cli/); `aspire update --self` updates an installed one (installed as a .NET tool: `dotnet tool update -g Aspire.Cli --version <version>`) |
| Docker or Podman | The Azurite container Aspire starts | [Docker](https://docs.docker.com/get-started/get-docker/), [Podman](https://podman.io/docs/installation) |
| Embedding model | Search by meaning, and its tests | `just model` (downloads `bge-small-en-v1.5`, 34 MB, and `multilingual-e5-small`, 123 MB, pinned and checked by SHA-256, into `models/`; without them, search uses keywords only and their tests fail) |
| Playwright browsers | End-to-end tests and `just screens` | `just browsers` (Chromium for the pinned Playwright version; needs PowerShell). For the system libraries on Debian or Ubuntu, then run `pwsh artifacts/bin/Weesite.Web.EndToEnd/debug/playwright.ps1 install --with-deps chromium` |
| [grype](https://github.com/anchore/grype) and [syft](https://github.com/anchore/syft) | Image scan and SBOM | [grype](https://github.com/anchore/grype#installation), [syft](https://github.com/anchore/syft#installation) |

`global.json` pins the SDK (11.0.100, `rollForward: latestFeature`) and makes `dotnet test` use Microsoft.Testing.Platform. With several SDKs installed, that pin decides which one builds.

### Infrastructure

| Tool | Used for | Install |
|---|---|---|
| [OpenTofu](https://opentofu.org/) | Infrastructure as code | [install](https://opentofu.org/docs/intro/install/) |
| [TFLint](https://github.com/terraform-linters/tflint) | OpenTofu lint (azurerm ruleset) | [install](https://github.com/terraform-linters/tflint#installation) |
| [Checkov](https://www.checkov.io/) | Misconfiguration scan | `pipx install checkov` or `uv tool install checkov` |
| Azure CLI | Signing in for the bootstrap | [install](https://learn.microsoft.com/cli/azure/install-azure-cli) |

## First steps

```sh
git clone https://github.com/gergodrazsdik/weesite.git
cd weesite
just doctor   # lists missing tools and git settings, with install hints
just hooks    # installs the pre-commit and commit-msg hooks
just lint     # runs the linters and infrastructure checks CI runs
just model    # downloads the embedding models; the search tests need them
just build    # builds the solution
just browsers # installs Chromium for the browser tests (needs pwsh)
just test     # runs the browser tests, then the other test projects
just aspire   # serves the site on http://localhost:5080; Aspire prints the dashboard link
just uat 123  # the same for pull request 123, from a worktree of its own
```

`just lint` needs every tool under "Every contribution" plus the infrastructure tools (OpenTofu, TFLint, Checkov). A missing tool is skipped locally with a `SKIPPED` message; CI sets `WEESITE_REQUIRE_TOOLS=1`, which makes it a failure.

Set up commit signing and the DCO sign-off as described in [CONTRIBUTING.md](../CONTRIBUTING.md#signed-commits-and-tags). In short:

```sh
git config commit.gpgSign true   # after telling git and GitHub about your signing key
git commit -s                    # every commit, for the DCO sign-off
```

## Aspire

`src/Weesite.AppHost` is the Aspire AppHost for local runs only: Azurite, the web app and the re-index job. It's a plain `Microsoft.NET.Sdk` project with the `Aspire.Hosting.*` packages and registers the web project by its path (`AddDotnetProject`), so building the AppHost doesn't build the web app; `aspire run` builds it before starting it.

Its build looks up the Aspire CLI bundle (the dashboard and DCP, the orchestrator) and records where it is. It uses a compatible `aspire` on `PATH`; failing that, it runs `dnx --yes aspire.cli@<version> -- setup` with the version of `Aspire.Hosting.AppHost` in `Directory.Packages.props`, which restores the CLI from nuget.org (about 180 MB) and unpacks its bundle into the Aspire home (`~/.aspire`, or `$ASPIRE_HOME`). CI builds it that way. The Aspire home has to be writable for that step: in a sandbox that keeps the home directory read-only, set `ASPIRE_HOME` to a writable directory, or install the CLI and run `aspire setup` once outside it.

Keep the CLI on the same major version as the AppHost packages: update both together.

## Containers: Docker or Podman

Aspire starts Azurite (Blob, Table and Queue emulation) in a container. Docker works out of the box. For rootless Podman, tell Aspire to use it:

```sh
export ASPIRE_CONTAINER_RUNTIME=podman
```

The tests in the `containers` category (Testcontainers) need Docker, or Podman with its Docker-compatible socket. For rootless Podman:

```sh
systemctl --user enable --now podman.socket
export DOCKER_HOST=unix://$XDG_RUNTIME_DIR/podman/podman.sock
```

The dashboard also lists `reindex`, the re-index job, which doesn't start on its own. "Re-embed all" in the admin's site settings queues a request; start `reindex` from the dashboard to process it, as Container Apps does when a request arrives.

## Trying a pull request locally

`just uat <pr>` runs a pull request on `http://localhost:5080` in about a minute, before its CI, the merge and the deploy to test (T-0174). It fetches the pull request's head (a fork's too), checks it out in a worktree of its own under `~/.cache/weesite-uat/`, so your checkout stays as it is, and starts it with `aspire run`, with fresh sample data each time. In a stack, run the top pull request: its branch holds everything under it. Stop it with Ctrl+C; the worktree stays, so the next run of the same pull request builds only what changed. `just uat-clean` removes them all.

**A fork's pull request runs as you.** Its build (MSBuild files, analyzers and source generators, packages) and its code run with your user's rights, outside any sandbox: they can read your files and use your Azure and GitHub sign-ins and your containers. So `just uat` refuses a pull request from a fork; read its changes first, then run it with `just uat <pr> --fork`, which lists the changed files that affect the build and runs it without your user secrets (an empty user-secrets folder of its own: no sign-in, and the app doesn't get test's sign-in clients). That keeps the secrets out of the app's configuration; it can't stop the pull request's code from reading your files. To try a fork's change more safely, push its commits to a branch of your own after reading them, or run it in a container or virtual machine without your credentials.

Locally you can try what runs in one app: the public site and the admin, in both themes, at phone widths (the browser's device mode), in any language the site settings choose; comments, reactions and the contact form; the analytics (your own visits); MCP from Claude Code, which signs in on localhost; and mail through Mailpit (below).

- **Sign-in:** `just local-signin` once (below).
- **Visitor countries:** `just local-geoip` downloads DB-IP's database, as the `geoip` workflow does for Azure; `just aspire` and `just uat` use it from then on. Your own visits come from localhost, which has no country: a request with `X-Forwarded-For: <a public address>` and a browser's User-Agent counts from that address's country, as behind Container Apps.

What needs test (or prod): Claude Desktop's connector and IndexNow, which need a public HTTPS address; more than one replica, scaling to zero, Container Apps' forwarded addresses, Key Vault, managed identity and Application Insights; Azure Table Storage's real speed (Azurite behaves the same, but isn't as slow); and a real phone, which can't easily reach a WSL machine.

## WSL notes

- Keep the clone in the Linux file system (`~/…`), not under `/mnt/c`: builds and file watching are much slower across the boundary, and file modes get lost.
- Line endings: `.gitattributes` forces LF, so a Windows-side git or editor doesn't introduce CRLF.
- Docker Desktop's WSL integration or Podman inside the distro both work for Azurite.
- Commit signing from Windows tools (an IDE on the Windows side) needs the same key and git config as WSL; it's simplest to commit from WSL.

## Local secrets

Nothing secret is ever committed. Locally:

- **App settings and secrets:** `dotnet user-secrets` for the web project (sign-in client IDs and secrets for localhost), or environment variables. Aspire passes the Azurite connection strings itself.
- **Environment variables:** a gitignored `.env.local` if a tool needs one.
- **Sign-in locally** (`just aspire`, `just uat`, `http://localhost:5080`): the maintainer runs `just local-signin` once, signed in to `az`. It copies the test environment's Microsoft and Google clients, which allow localhost, with test's admins and trusted tenants, from test's Key Vault and app settings into the web project's user secrets, printing only the keys. Anyone else: create your own OAuth clients with Microsoft and Google, with the redirect URIs `http://localhost:5080/signin-microsoft` and `http://localhost:5080/signin-google`. Put their client IDs and secrets into user secrets, with your address as admin:

  ```sh
  cd src/Weesite.Web
  dotnet user-secrets set "Authentication:Microsoft:ClientId" "<id>"
  dotnet user-secrets set "Authentication:Microsoft:ClientSecret" "<secret>"
  dotnet user-secrets set "Authentication:Google:ClientId" "<id>"
  dotnet user-secrets set "Authentication:Google:ClientSecret" "<secret>"
  dotnet user-secrets set "Site:Admins" "<your email>"
  ```

  Without them, `/admin/sign-in` says no provider is configured, and the rest of the site works as usual.
- **Invitations locally** ([ADR 0010](adr/0010-email-over-smtp.md)): without settings, nothing is sent and the Editors page shows the invitation to copy. To see the mail itself, run a local mail catcher such as [Mailpit](https://mailpit.axllent.org/) (`podman run --rm -p 1025:1025 -p 8025:8025 docker.io/axllent/mailpit`) and point the app at it. `None` (no TLS) is accepted only for a server on this machine:

  ```sh
  dotnet user-secrets set "Email:Smtp:Host" "localhost"
  dotnet user-secrets set "Email:Smtp:Port" "1025"
  dotnet user-secrets set "Email:Smtp:Security" "None"
  dotnet user-secrets set "Email:From" "noreply@example.com"
  ```

  The mail shows up at `http://localhost:8025`.
- **OpenTofu bootstrap inputs:** a gitignored `infra/bootstrap/bootstrap.auto.tfvars`, copied from the committed `.example`.
- **The GitHub token for the bootstrap** comes from an environment variable (`GITHUB_TOKEN`), never a file. Fetch it from your own secret manager in a recipe in `local.just` (see `local.just.example`).

## Working with Claude Code

`just claude` starts Claude Code in the repo. The committed `.claude/settings.json` turns on the Bash sandbox (macOS, Linux and WSL2; Linux needs `bubblewrap` and `socat`) with a network allowlist (the hosts are listed in that file). Put personal tweaks in `.claude/settings.local.json` and `CLAUDE.local.md`; both are gitignored.

Claude Code reads `CLAUDE.md`, which imports `AGENTS.md`. Other agents read `AGENTS.md` directly.

`.mcp.json` adds two MCP servers, which run outside the sandbox: Aspire's (`aspire agent mcp`) and Playwright's, which `npx` fetches from npm as `@playwright/mcp` at an exact version. Its dependencies (`playwright` and `playwright-core`) are pinned to exact versions by the package itself, and npm doesn't let a published version change, so the whole tree is fixed by that one version; `npx` has no lock file, so nothing checks the downloaded files against a hash beyond npm's own registry integrity. Update the version in `.mcp.json` deliberately, as for any tool.
