# Weesite task runner. `just` lists the recipes.
# On Windows, run recipes from Git Bash or WSL (they use sh).

import? 'local.just'

# List the recipes
default:
    @just --list --unsorted

# Check that the required tools are installed and print install hints
[group('meta')]
doctor:
    #!/bin/sh
    missing=0
    check() {
      if command -v "$1" >/dev/null 2>&1; then
        printf '  ok       %s\n' "$1"
      else
        printf '  MISSING  %-22s %s\n' "$1" "$2"
        missing=1
      fi
    }
    optional() {
      if command -v "$1" >/dev/null 2>&1; then
        printf '  ok       %s\n' "$1"
      else
        printf '  -        %-22s %s\n' "$1" "$2"
      fi
    }
    echo "Required:"
    check git                  "https://git-scm.com/downloads"
    check just                 "https://just.systems/man/en/packages.html"
    check lefthook             "https://lefthook.dev/installation/"
    check markdownlint-cli2    "npm install -g markdownlint-cli2"
    check yamllint             "pipx install yamllint (or uv tool install yamllint)"
    check actionlint           "https://github.com/rhysd/actionlint/blob/main/docs/install.md"
    check zizmor               "pipx install zizmor (or uv tool install zizmor), https://docs.zizmor.sh/installation/"
    check typos                "cargo install typos-cli, or https://github.com/crate-ci/typos#install"
    check editorconfig-checker "https://github.com/editorconfig-checker/editorconfig-checker#installation"
    check shellcheck           "https://github.com/koalaman/shellcheck#installing"
    check gitleaks             "https://github.com/gitleaks/gitleaks#installing"
    echo "Recommended:"
    optional gh                "https://cli.github.com/"
    optional pinact            "https://github.com/suzuki-shunsuke/pinact#install"
    optional jq                "https://jqlang.org/download/ (needed by 'just github-settings')"
    optional claude            "https://code.claude.com/docs/en/setup"
    echo "Application (Phase 1):"
    optional dotnet            ".NET 11 SDK: https://dotnet.microsoft.com/download"
    optional aspire            "https://aspire.dev/get-started/install-cli/"
    optional podman            "Podman or Docker for Azurite: https://podman.io/docs/installation"
    optional hadolint          "https://github.com/hadolint/hadolint#install"
    optional grype             "https://github.com/anchore/grype#installation"
    optional syft              "https://github.com/anchore/syft#installation"
    echo "Infrastructure (Phase 1):"
    optional tofu              "https://opentofu.org/docs/intro/install/"
    optional tflint            "https://github.com/terraform-linters/tflint#installation"
    optional conftest          "https://www.conftest.dev/install/"
    optional checkov           "pipx install checkov (or uv tool install checkov)"
    echo "Git settings:"
    if [ "$(git config --bool commit.gpgSign || echo false)" = true ]; then
      echo "  ok       commit.gpgSign"
    else
      echo "  MISSING  commit.gpgSign         git config commit.gpgSign true (see CONTRIBUTING.md)"
      missing=1
    fi
    if [ "$(git config --bool tag.gpgSign || echo false)" = true ]; then
      echo "  ok       tag.gpgSign"
    else
      echo "  -        tag.gpgSign            git config tag.gpgSign true (maintainers)"
    fi
    if [ -f "$(git rev-parse --git-path hooks)/commit-msg" ]; then
      echo "  ok       git hooks installed"
    else
      echo "  MISSING  git hooks              just hooks"
      missing=1
    fi
    exit "$missing"

# Install the git hooks (lefthook)
[group('dev')]
hooks:
    lefthook install

# Run all linters
[group('lint')]
lint: lint-md lint-yaml lint-actions zizmor pinact-check lint-sh typos editorconfig gitleaks lint-tofu lint-containerfile

# Lint Markdown (markdownlint-cli2)
[group('lint')]
lint-md:
    git ls-files -z '*.md' | xargs -0 scripts/with-tool.sh markdownlint-cli2

# Lint YAML (yamllint)
[group('lint')]
lint-yaml:
    scripts/with-tool.sh yamllint --strict .

# Lint GitHub Actions workflows (actionlint)
[group('lint')]
lint-actions:
    scripts/with-tool.sh actionlint

# Audit GitHub Actions workflows for security problems (zizmor, offline audits)
[group('lint')]
zizmor:
    scripts/with-tool.sh zizmor --offline --no-progress .github/workflows

# Check that every action is pinned to a full commit SHA (pinact)
[group('lint')]
pinact-check:
    scripts/with-tool.sh pinact run --check

# Lint shell scripts (shellcheck)
[group('lint')]
lint-sh:
    git ls-files -z '*.sh' | xargs -0 scripts/with-tool.sh shellcheck

# Check spelling (typos)
[group('lint')]
typos:
    scripts/with-tool.sh typos

# Check files against .editorconfig (editorconfig-checker)
[group('lint')]
editorconfig:
    git ls-files -z | xargs -0 scripts/with-tool.sh editorconfig-checker

# Scan the git history for secrets (gitleaks)
[group('lint')]
gitleaks:
    scripts/with-tool.sh gitleaks git --redact --no-banner

# Check OpenTofu formatting (only when there are .tf files)
[group('lint')]
lint-tofu:
    #!/bin/sh
    if git ls-files '*.tf' '*.tofu' | grep -q .; then
      exec scripts/with-tool.sh tofu fmt -check -recursive infra
    fi
    echo "lint-tofu: no OpenTofu files yet"

# Lint Containerfiles and Dockerfiles (only when there are any)
[group('lint')]
lint-containerfile:
    #!/bin/sh
    files=$(git ls-files '*Containerfile*' '*Dockerfile*')
    if [ -n "$files" ]; then
      # shellcheck disable=SC2086  # one path per word; tracked paths have no spaces
      exec scripts/with-tool.sh hadolint $files
    fi
    echo "lint-containerfile: no Containerfile yet"

# Pin GitHub Actions to full commit SHAs (pinact)
[group('meta')]
pin:
    pinact run

# Merge a PR by fast-forwarding main to its head (maintainers; keeps every commit's signature)
[group('maintainer')]
merge pr:
    scripts/ff-merge.sh {{ pr }}

# Apply the repository settings and rulesets to <owner/repo> (maintainers; see scripts/github-settings.sh)
[group('maintainer')]
github-settings repo *flags:
    scripts/github-settings.sh {{ flags }} {{ repo }}

# Read the repository settings of <owner/repo> back and report differences
[group('maintainer')]
github-settings-check repo:
    scripts/github-settings.sh --check {{ repo }}

# Start Claude Code in this repo (extra arguments are passed through)
[group('dev')]
claude *args:
    claude {{ args }}
