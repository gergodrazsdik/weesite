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
| [gitleaks](https://github.com/gitleaks/gitleaks) | Secret scan | [installing](https://github.com/gitleaks/gitleaks#installing) |
| [Conftest](https://www.conftest.dev/) | Rego policy baseline: tests and checks | [install](https://www.conftest.dev/install/) |
| [gh](https://cli.github.com/) | PRs from the terminal | recommended |
| [pinact](https://github.com/suzuki-shunsuke/pinact) | Pin workflow actions to SHAs | recommended |

### Application (from Phase 1)

| Tool | Used for | Install |
|---|---|---|
| .NET 11 SDK | Build, test, run | <https://dotnet.microsoft.com/download> |
| [Aspire CLI](https://aspire.dev/) | `aspire run`: the app with Azurite and a telemetry dashboard | [install](https://aspire.dev/get-started/install-cli/) |
| Docker or Podman | The Azurite container Aspire starts | [Docker](https://docs.docker.com/get-started/get-docker/), [Podman](https://podman.io/docs/installation) |
| Embedding model | Search by meaning, and its tests | `just model` (downloads `bge-small-en-v1.5`, 34 MB, pinned and checked by SHA-256, into `models/`; without it, search uses keywords only) |
| Playwright browsers | End-to-end tests and `just screens` | `just browsers` (Chromium for the pinned Playwright version; needs PowerShell; add `--with-deps` on Debian or Ubuntu for the system libraries) |
| [hadolint](https://github.com/hadolint/hadolint) | Containerfile lint | [install](https://github.com/hadolint/hadolint#install) |
| [grype](https://github.com/anchore/grype) and [syft](https://github.com/anchore/syft) | Image scan and SBOM | [grype](https://github.com/anchore/grype#installation), [syft](https://github.com/anchore/syft#installation) |

When the solution exists, `global.json` pins the SDK (11.0, `rollForward: latestFeature`). With several SDKs installed, that pin decides which one builds.

### Infrastructure (from Phase 1)

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
just lint     # runs every linter, exactly as CI does
```

Set up commit signing and the DCO sign-off as described in [CONTRIBUTING.md](../CONTRIBUTING.md#signed-commits-and-tags). In short:

```sh
git config commit.gpgSign true   # after telling git and GitHub about your signing key
git commit -s                    # every commit, for the DCO sign-off
```

## Containers: Docker or Podman

Aspire starts Azurite (Blob, Table and Queue emulation) in a container. Docker works out of the box. For rootless Podman, tell Aspire to use it:

```sh
export ASPIRE_CONTAINER_RUNTIME=podman
```

## WSL notes

- Keep the clone in the Linux file system (`~/…`), not under `/mnt/c`: builds and file watching are much slower across the boundary, and file modes get lost.
- Line endings: `.gitattributes` forces LF, so a Windows-side git or editor doesn't introduce CRLF.
- Docker Desktop's WSL integration or Podman inside the distro both work for Azurite.
- Commit signing from Windows tools (an IDE on the Windows side) needs the same key and git config as WSL; it's simplest to commit from WSL.

## Local secrets

Nothing secret is ever committed. Locally:

- **App settings and secrets:** `dotnet user-secrets` for the web project (sign-in client IDs and secrets for localhost), or environment variables. Aspire passes the Azurite connection strings itself.
- **Environment variables:** a gitignored `.env.local` if a tool needs one.
- **Sign-in locally** (`just aspire`, `http://localhost:5080`): the test environment's app registrations allow localhost. Copy their client IDs and secrets from the test Key Vault (`az keyvault secret show --vault-name <vault> --name Authentication--Microsoft--ClientSecret --query value --output tsv`, and the same for the client IDs and Google) into user secrets, with your address as admin:

  ```sh
  cd src/Weesite.Web
  dotnet user-secrets set "Authentication:Microsoft:ClientId" "<id>"
  dotnet user-secrets set "Authentication:Microsoft:ClientSecret" "<secret>"
  dotnet user-secrets set "Authentication:Google:ClientId" "<id>"
  dotnet user-secrets set "Authentication:Google:ClientSecret" "<secret>"
  dotnet user-secrets set "Site:Admins" "<your email>"
  ```

  Without them, `/admin/sign-in` says no provider is configured, and the rest of the site works as usual.
- **OpenTofu bootstrap inputs:** a gitignored `infra/bootstrap/bootstrap.auto.tfvars`, copied from the committed `.example`.
- **The GitHub token for the bootstrap** comes from an environment variable (`GITHUB_TOKEN`), never a file. Fetch it from your own secret manager in a recipe in `local.just` (see `local.just.example`).

## Working with Claude Code

`just claude` starts Claude Code in the repo. The committed `.claude/settings.json` turns on the Bash sandbox (macOS, Linux and WSL2; Linux needs `bubblewrap` and `socat`) with a network allowlist for GitHub, NuGet, .NET, OpenTofu and the Microsoft container registry. Put personal tweaks in `.claude/settings.local.json` and `CLAUDE.local.md`; both are gitignored.

Claude Code reads `CLAUDE.md`, which imports `AGENTS.md`. Other agents read `AGENTS.md` directly.
