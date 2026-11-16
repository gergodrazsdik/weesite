#!/usr/bin/env bash
# Releases: the version in Directory.Build.props, the changelog, the tag and the GitHub release.
#
#   release.sh version <x.y.z>
#       Sets <Version> in Directory.Build.props and turns the changelog's Unreleased section into the version's
#       section, dated today (UTC). Run it on a branch; the change goes through a PR like any other
#       (`just version <x.y.z>`).
#
#   release.sh publish [--dry-run]
#       Maintainers only, from main once the version's PR is merged and prod runs it (`just release`). Checks first:
#       main is clean and level with origin/main, the tag doesn't exist yet, the changelog has a section for the
#       version in Directory.Build.props, and prod runs the image the app workflow built from this commit (compared by
#       digest; when that can't be read, it says so and asks). Then it creates the signed tag v<version>, pushes it,
#       and creates the GitHub release with the version's changelog section as its notes, its links to files in the
#       repository pointing at the tag. --dry-run stops before the tag and prints the notes.
#
# Needs git, gh (signed in), jq and curl; publish also az (signed in) for the prod check.
set -euo pipefail

root=$(git rev-parse --show-toplevel)
props="$root/Directory.Build.props"
changelog="$root/CHANGELOG.md"

die() {
  echo "release: $*" >&2
  exit 1
}

current_version() {
  sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$props" | head -n 1
}

# Whether the changelog has a "## [<name>]" heading.
has_section() {
  awk -v name="$1" 'index($0, "## [" name "]") == 1 { found = 1 } END { exit !found }' "$changelog"
}

# A changelog section's text, without its heading and its leading blank lines: up to the next "## [" heading.
section() {
  awk -v name="$1" '
    index($0, "## [" name "]") == 1 { on = 1; next }
    on && index($0, "## [") == 1 { exit }
    on { print }
  ' "$changelog" | sed '/./,$!d'
}

confirm() {
  local answer
  read -r -p "$1 [y/N] " answer
  [[ $answer == [yY] ]] || die "stopped"
}

# Does prod run the image built from <commit>? The app workflow pushes ghcr.io/<owner>/weesite:<commit>, and prod
# runs an image by digest. A second install names its resource groups with WEESITE_INSTANCE (in the shell, or the
# repository variable the bootstrap set).
check_prod() {
  local commit=$1 instance group running owner token built
  if [ -n "${WEESITE_INSTANCE+set}" ]; then
    instance=$WEESITE_INSTANCE
  else
    instance=$(gh variable list --json name,value --jq '.[] | select(.name == "WEESITE_INSTANCE") | .value' 2>/dev/null || true)
  fi
  group="rg-weesite${instance:+-$instance}-prod"
  running=$(az containerapp show --resource-group "$group" --name ca-weesite-web \
    --query 'properties.template.containers[0].image' --output tsv --only-show-errors 2>/dev/null || true)
  owner=$(gh repo view --json owner --jq '.owner.login | ascii_downcase' 2>/dev/null || true)
  if [ -z "$running" ] || [ -z "$owner" ]; then
    echo "release: couldn't read which image prod runs ($group; is az signed in?)." >&2
    confirm "Tag $commit without that check?"
    return
  fi
  token=$(curl -fsS "https://ghcr.io/token?scope=repository:$owner/weesite:pull" | jq -r '.token // empty') || token=
  built=$(curl -fsI -H "Authorization: Bearer $token" \
    -H 'Accept: application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json' \
    "https://ghcr.io/v2/$owner/weesite/manifests/$commit" | tr -d '\r' | sed -n 's/^docker-content-digest: //Ip') || built=
  if [ -z "$built" ]; then
    die "no image for $commit in ghcr.io/$owner/weesite yet: the app workflow builds one for the last commit of each push to main; wait for it, deploy and promote"
  fi
  if [ "${running##*@}" != "$built" ]; then
    die "prod runs ${running##*@}, not the image of $commit ($built): promote it first, or release from the commit prod runs"
  fi
  echo "release: prod runs the image of $commit."
}

case ${1:-} in
  version)
    version=${2:-}
    [[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "usage: release.sh version <major.minor.patch>"
    [ -n "$(current_version)" ] || die "Directory.Build.props has no <Version>"
    has_section Unreleased || die "CHANGELOG.md has no '## [Unreleased]' section"
    ! has_section "$version" || die "CHANGELOG.md has a section for $version already"
    [ -n "$(section Unreleased)" ] || die "the Unreleased section is empty: write down what changed first"

    tmp=$(mktemp)
    trap 'rm -f "$tmp"' EXIT
    sed "s:<Version>[^<]*</Version>:<Version>$version</Version>:" "$props" >"$tmp"
    cat "$tmp" >"$props"
    # A new, empty Unreleased heading above; what was under it is now the version's.
    awk -v v="$version" -v d="$(date -u +%Y-%m-%d)" '
      !done && $0 == "## [Unreleased]" { print; print ""; print "## [" v "] - " d; done = 1; next }
      { print }
    ' "$changelog" >"$tmp"
    cat "$tmp" >"$changelog"
    echo "release: version $version in Directory.Build.props and CHANGELOG.md. Commit, open a PR, and once it's merged"
    echo "and prod runs it, the maintainer runs: just release"
    ;;

  publish)
    dry_run=false
    case ${2:-} in
      "") ;;
      --dry-run) dry_run=true ;;
      *) die "usage: release.sh publish [--dry-run]" ;;
    esac
    version=$(current_version)
    [ -n "$version" ] || die "Directory.Build.props has no <Version>"
    tag="v$version"

    [ "$(git -C "$root" branch --show-current)" = main ] || die "switch to main first"
    [ -z "$(git -C "$root" status --porcelain)" ] || die "the working tree has changes"
    git -C "$root" fetch --quiet --tags origin main
    commit=$(git -C "$root" rev-parse HEAD)
    [ "$commit" = "$(git -C "$root" rev-parse origin/main)" ] || die "main isn't level with origin/main: pull first"
    ! git -C "$root" rev-parse --quiet --verify "refs/tags/$tag" >/dev/null || die "$tag exists already"
    [ -z "$(git -C "$root" ls-remote --tags origin "refs/tags/$tag")" ] || die "$tag exists on origin already"
    has_section "$version" || die "CHANGELOG.md has no section for $version: run just version $version in a PR first"
    notes=$(section "$version")
    [ -n "$notes" ] || die "CHANGELOG.md's section for $version is empty"
    # A release page doesn't resolve links relative to the repository: point them at the files at this tag.
    repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) || die "gh can't read the repository"
    notes=$(sed -E "s#\]\(([A-Za-z0-9._-][^):]*)\)#](https://github.com/$repo/blob/$tag/\1)#g" <<<"$notes")

    check_prod "$commit"

    if $dry_run; then
      printf 'release: would tag %s as %s, with these notes:\n\n%s\n' "$commit" "$tag" "$notes"
      exit 0
    fi
    tmp=$(mktemp)
    trap 'rm -f "$tmp"' EXIT
    printf '%s\n' "$notes" >"$tmp"
    git -C "$root" tag --sign --message "Weesite $version" "$tag" "$commit"
    git -C "$root" push origin "$tag"
    gh release create "$tag" --verify-tag --title "Weesite $version" --notes-file "$tmp"
    ;;

  *)
    sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
