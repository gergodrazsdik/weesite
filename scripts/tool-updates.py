#!/usr/bin/env python3
"""Which pinned tool versions have an update past the cooldown, and which wait (T-0151).

Dependabot keeps the actions, base images, NuGet packages, the highlighter's crates and OpenTofu
providers current. This covers what it can't read: tool versions written as plain strings in
workflows, the infra tools' composite action, the Checkov lock, the Containerfile and the tests'
container images. Like Dependabot, an update is due once it's 7 days old, a new major version too; the newest
such one is proposed, and newer ones wait, with the date the first of them is due.

Read-only, standard library only. Run from the repository root:

    scripts/tool-updates.py              # a table for people
    scripts/tool-updates.py --markdown   # Markdown, for a job summary or an issue
    scripts/tool-updates.py --local      # also this machine's versions, beside the pins

GitHub's API: with GH_TOKEN (the workflow sets it), or the GitHub CLI's sign-in on a maintainer's
machine; without either, 60 requests an hour, which one run fits in. With --github-output, the number of updates due goes to
$GITHUB_OUTPUT as `due=<n>`.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

COOLDOWN = dt.timedelta(days=7)
STABLE = re.compile(r"^v?\d+(\.\d+)*$")


@dataclass(frozen=True)
class Pin:
    """A version written in a file, and where its releases are."""

    name: str
    file: str
    pattern: str  # a regex whose group "v" is the version
    source: str  # github:owner/repo, pypi:package, npm:package or go
    command: tuple[str, ...] = ()  # prints the installed version, for --local
    note: str = ""  # what else changes with it


LINT = ".github/workflows/lint.yml"
INFRA = ".github/actions/setup-infra-tools/action.yml"
CHECKOV_LOCK = ".github/actions/setup-infra-tools/requirements.txt"

PINS = [
    Pin(
        "just",
        LINT,
        r'JUST_VERSION: "(?P<v>[^"]+)"',
        "github:casey/just",
        ("just", "--version"),
        "also rust-just in the Checkov lock (its header has the commands)",
    ),
    Pin(
        "yamllint",
        LINT,
        r'YAMLLINT_VERSION: "(?P<v>[^"]+)"',
        "pypi:yamllint",
        ("yamllint", "--version"),
    ),
    Pin(
        "typos",
        LINT,
        r'TYPOS_VERSION: "(?P<v>[^"]+)"',
        "github:crate-ci/typos",
        ("typos", "--version"),
    ),
    Pin(
        "zizmor",
        LINT,
        r'ZIZMOR_VERSION: "(?P<v>[^"]+)"',
        "pypi:zizmor",
        ("zizmor", "--version"),
        "also zizmor-action's `version:` in lint.yml, and an action release that knows it (F-0178)",
    ),
    Pin(
        "markdownlint-cli2",
        LINT,
        r'MARKDOWNLINT_CLI2_VERSION: "(?P<v>[^"]+)"',
        "npm:markdownlint-cli2",
        ("markdownlint-cli2", "--version"),
    ),
    Pin(
        "actionlint",
        LINT,
        r'ACTIONLINT_VERSION: "(?P<v>[^"]+)"',
        "github:rhysd/actionlint",
        ("actionlint", "--version"),
    ),
    Pin(
        "editorconfig-checker",
        LINT,
        r'EDITORCONFIG_CHECKER_VERSION: "(?P<v>[^"]+)"',
        "github:editorconfig-checker/editorconfig-checker",
        ("editorconfig-checker", "--version"),
    ),
    Pin(
        "gitleaks",
        LINT,
        r'GITLEAKS_VERSION: "(?P<v>[^"]+)"',
        "github:gitleaks/gitleaks",
        ("gitleaks", "version"),
    ),
    Pin(
        "pinact",
        LINT,
        r'PINACT_VERSION: "(?P<v>[^"]+)"',
        "github:suzuki-shunsuke/pinact",
        ("pinact", "--version"),
    ),
    Pin(
        "hadolint",
        LINT,
        r'HADOLINT_VERSION: "(?P<v>[^"]+)"',
        "github:hadolint/hadolint",
        ("hadolint", "--version"),
    ),
    Pin(
        "OpenTofu",
        INFRA,
        r'TOFU_VERSION: "(?P<v>[^"]+)"',
        "github:opentofu/opentofu",
        ("tofu", "version"),
        "and TOFU_SHA256 beside it, from the release's SHA256SUMS once its signature checks out",
    ),
    Pin(
        "TFLint",
        INFRA,
        r'tflint_version: "(?P<v>[^"]+)"',
        "github:terraform-linters/tflint",
        ("tflint", "--version"),
    ),
    Pin(
        "conftest",
        INFRA,
        r'CONFTEST_VERSION: "(?P<v>[^"]+)"',
        "github:open-policy-agent/conftest",
        ("conftest", "--version"),
    ),
    Pin(
        "Checkov",
        CHECKOV_LOCK,
        r"^checkov==(?P<v>\S+)",
        "pypi:checkov",
        ("checkov", "--version"),
        "regenerate the lock with the commands in its header; check its asteval pin (T-0132)",
    ),
    Pin(
        "k6",
        ".github/workflows/load-test.yml",
        r'k6-version: "(?P<v>[^"]+)"',
        "github:grafana/k6",
        ("k6", "version"),
    ),
    Pin(
        "esbuild",
        "Containerfile",
        r"@esbuild/linux-x64/-/linux-x64-(?P<v>[\d.]+)\.tgz",
        "npm:esbuild",
        ("esbuild", "--version"),
        "the Containerfile's ADD --checksum too; install-esbuild.sh reads both from there",
    ),
    Pin(
        "Go",
        LINT,
        r'go-version: "(?P<v>[^"]+)"',
        "go",
        ("go", "version"),
        "the minor version the lint job builds its Go linters with",
    ),
    Pin(
        "Azurite",
        "tests/Shared/Containers/TestAzurite.cs",
        r"azurite:(?P<v>[\d.]+)@sha256:",
        "github:Azure/Azurite",
        note="and its digest (crane digest); also the AppHost's emulator tag and digest (src/Weesite.AppHost/AppHost.cs; a test checks they agree)",
    ),
    Pin(
        "Mailpit",
        "tests/Weesite.Web.Tests/EmailTests.cs",
        r"mailpit:(?P<v>v[\d.]+)@sha256:",
        "github:axllent/mailpit",
        note="and its digest (crane digest)",
    ),
]


@dataclass(frozen=True)
class Release:
    """A stable release and when it came out."""

    version: str
    published: dt.datetime


def key(version: str) -> tuple[int, ...]:
    """Sorts versions: 1.10.0 after 1.9.0."""
    return tuple(int(part) for part in version.lstrip("v").split("."))


def fetch_github(path: str) -> object:
    """
    Fetches from GitHub's API: with GH_TOKEN when it's set (the workflow), else through the GitHub
    CLI when it's signed in (a maintainer's machine), else without a token (60 requests an hour).
    Each way is tried twice: one timeout mustn't make the weekly run fail.
    """
    problem = ""
    if not os.environ.get("GH_TOKEN") and shutil.which("gh"):
        for _ in range(2):
            try:
                result = subprocess.run(
                    ["gh", "api", path],
                    capture_output=True,
                    text=True,
                    timeout=60,
                    check=False,
                )
            except subprocess.TimeoutExpired:
                problem = "gh api timed out"
                continue
            if result.returncode == 0:
                return json.loads(result.stdout)
            problem = (
                result.stderr.strip().splitlines()[-1]
                if result.stderr.strip()
                else "gh api failed"
            )
    try:
        return fetch(f"https://api.github.com/{path}")
    except Exception as error:
        raise RuntimeError(
            f"{problem}; then {error}" if problem else str(error)
        ) from error


def fetch(url: str) -> object:
    """Fetches JSON, with GH_TOKEN for GitHub when it's set; tried twice."""
    request = urllib.request.Request(
        url,
        headers={"Accept": "application/json", "User-Agent": "weesite-tool-updates"},
    )
    token = os.environ.get("GH_TOKEN")
    if token and url.startswith("https://api.github.com/"):
        request.add_header("Authorization", f"Bearer {token}")
    for attempt in range(2):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.load(response)
        except (TimeoutError, urllib.error.URLError) as error:
            # A refusal (403, 404) won't change on a second try.
            if attempt or isinstance(error, urllib.error.HTTPError):
                raise
    raise AssertionError("unreachable")


def when(value: str) -> dt.datetime:
    """Parses the registries' timestamps, with or without a zone (PyPI's are UTC)."""
    parsed = dt.datetime.fromisoformat(value)
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=dt.UTC)


def releases(source: str) -> list[Release]:
    """The stable releases of a source, newest first."""
    kind, _, name = source.partition(":")
    found: list[Release] = []
    if kind == "github":
        data = fetch_github(f"repos/{name}/releases?per_page=40")
        assert isinstance(data, list)
        found = [
            Release(r["tag_name"], when(r["published_at"]))
            for r in data
            if not r["prerelease"]
            and not r["draft"]
            and r["published_at"]
            and STABLE.match(r["tag_name"])
        ]
    elif kind == "pypi":
        data = fetch(f"https://pypi.org/pypi/{name}/json")
        assert isinstance(data, dict)
        found = [
            Release(version, when(files[0]["upload_time_iso_8601"]))
            for version, files in data["releases"].items()
            if files
            and STABLE.match(version)
            and not any(f.get("yanked") for f in files)
        ]
    elif kind == "npm":
        data = fetch(f"https://registry.npmjs.org/{name}")
        assert isinstance(data, dict)
        found = [
            Release(version, when(data["time"][version]))
            for version in data["versions"]
            if STABLE.match(version)
        ]
    elif kind == "go":
        # go.dev lists the supported releases; a minor version came out with its .0 tag.
        data = fetch("https://go.dev/dl/?mode=json")
        assert isinstance(data, list)
        lines = {
            ".".join(r["version"].removeprefix("go").split(".")[:2])
            for r in data
            if r["stable"]
        }
        for line in lines:
            commit = fetch_github(f"repos/golang/go/commits/go{line}.0")
            assert isinstance(commit, dict)
            found.append(Release(line, when(commit["commit"]["committer"]["date"])))
    else:
        raise ValueError(f"unknown source {source}")
    return sorted(found, key=lambda r: key(r.version), reverse=True)


def pinned(root: Path, pin: Pin) -> str:
    """The version a file pins."""
    text = (root / pin.file).read_text(encoding="utf-8")
    match = re.search(pin.pattern, text, re.MULTILINE)
    if not match:
        raise ValueError(f"{pin.file}: no {pin.name} version (pattern {pin.pattern})")
    return match["v"]


def installed(pin: Pin) -> str:
    """The version on this machine, or why there's none."""
    if not pin.command or not shutil.which(pin.command[0]):
        return "not installed"
    try:
        output = subprocess.run(
            pin.command, capture_output=True, text=True, timeout=30, check=False
        )
    except (OSError, subprocess.TimeoutExpired):
        return "unknown"
    match = re.search(r"\d+\.\d+(\.\d+)?", output.stdout + output.stderr)
    if match:
        return match[0]
    # A tool built with `go install` may print no version; its build info has the module's.
    if (go := shutil.which("go")) and (binary := shutil.which(pin.command[0])):
        info = subprocess.run(
            [go, "version", "-m", binary],
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        if found := re.search(
            r"^\s*mod\s+\S+\s+v?(\d+\.\d+(\.\d+)?)", info.stdout, re.MULTILINE
        ):
            return found[1]
    return "unknown"


@dataclass(frozen=True)
class Row:
    """One pin's state."""

    pin: Pin
    current: str
    status: str  # current, update, wait or error
    detail: str
    local: str = ""


def check(root: Path, pin: Pin, today: dt.datetime, local: bool) -> Row:
    """Compares a pin with its releases."""
    current = pinned(root, pin)
    here = installed(pin) if local else ""
    try:
        found = releases(pin.source)
    except Exception as error:  # noqa: BLE001 - one source failing mustn't stop the rest
        return Row(pin, current, "error", f"couldn't read {pin.source}: {error}", here)

    prefix = "v" if current.startswith("v") else ""
    newer = [r for r in found if key(r.version) > key(current)]

    ripe = [r for r in newer if today - r.published >= COOLDOWN]
    young = [r for r in newer if today - r.published < COOLDOWN]
    parts = []
    status = "current"
    if ripe:
        target = ripe[0]
        status = "update"
        parts.append(
            f"to {prefix}{target.version.lstrip('v')} ({target.published:%Y-%m-%d})"
        )
    if young:
        status = status if ripe else "wait"
        # The one due first, and the newest when it's another: a newer release doesn't hold back
        # an older one that's due sooner.
        newest = young[0]
        first = min(young, key=lambda r: r.published)
        same_day = first.published.date() == newest.published.date()
        for release in [newest] if same_day else [first, newest]:
            due = (release.published + COOLDOWN).date()
            parts.append(
                f"{prefix}{release.version.lstrip('v')} ({release.published:%Y-%m-%d}) due {due:%Y-%m-%d}"
            )
    if status != "current" and pin.note:
        parts.append(pin.note)
    return Row(pin, current, status, "; ".join(parts), here)


def render_text(rows: list[Row], local: bool) -> str:
    """A plain table."""
    header = ["tool", "pinned", "status", "detail"] + (
        ["this machine"] if local else []
    )
    lines = [
        [r.pin.name, r.current, r.status, r.detail] + ([r.local] if local else [])
        for r in rows
    ]
    widths = [
        max(len(str(line[i])) for line in [header, *lines])
        for i in range(len(header) - 1)
    ]
    out = []
    for line in [header, *lines]:
        cells = [str(cell).ljust(widths[i]) for i, cell in enumerate(line[:-1])]
        out.append("  ".join([*cells, str(line[-1])]).rstrip())
    return "\n".join(out)


def render_markdown(rows: list[Row], today: dt.datetime) -> str:
    """A Markdown summary: what's due first."""
    due = [r for r in rows if r.status == "update"]
    waiting = [r for r in rows if r.status == "wait"]
    errors = [r for r in rows if r.status == "error"]
    out = [f"## Tool updates, {today:%Y-%m-%d}", ""]
    out.append(
        f"{len(due)} due (released 7 days ago or more), {len(waiting)} waiting, "
        f"{len(rows) - len(due) - len(waiting) - len(errors)} current."
        " Dependabot covers the actions, base images, NuGet packages and OpenTofu providers."
    )
    out.append("")
    out.append("| Tool | Pinned | Status | Detail | File |")
    out.append("|---|---|---|---|---|")
    order = {"update": 0, "error": 1, "wait": 2, "current": 3}
    for r in sorted(rows, key=lambda r: (order[r.status], r.pin.name.lower())):
        out.append(
            f"| {r.pin.name} | {r.current} | {r.status} | {r.detail or '—'} | `{r.pin.file}` |"
        )
    return "\n".join(out) + "\n"


def main() -> int:
    """Checks every pin."""
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("--markdown", action="store_true", help="print Markdown")
    parser.add_argument(
        "--local", action="store_true", help="also show this machine's versions"
    )
    parser.add_argument(
        "--github-output", action="store_true", help="write due=<n> to $GITHUB_OUTPUT"
    )
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent
    today = dt.datetime.now(dt.UTC)
    rows = [check(root, pin, today, args.local) for pin in PINS]
    print(
        render_markdown(rows, today) if args.markdown else render_text(rows, args.local)
    )

    if args.github_output and (path := os.environ.get("GITHUB_OUTPUT")):
        with Path(path).open("a", encoding="utf-8") as output:
            output.write(f"due={sum(r.status == 'update' for r in rows)}\n")
    return 1 if any(r.status == "error" for r in rows) else 0


if __name__ == "__main__":
    sys.exit(main())
