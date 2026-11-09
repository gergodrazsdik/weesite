#!/bin/sh
# Deploy an image to an environment's web Container App and smoke-test it, then point the
# re-index job at the same image.
# Usage: scripts/deploy-app.sh <test|prod> <image@sha256:digest>
#
# Runs in CI after `azure/login` with the environment's apply identity. Points the Container App
# (created by infra/app) at the image, waits until /health answers "Healthy" on the new revision,
# and fails otherwise. Only then does the re-index job (infra/app reindex.tf) get the image, so a
# failed deploy leaves the job on the image that still runs. The app's hostname is masked in the
# log: it's not secret, but the test hostname stays out of public logs (brief, public-repo
# hygiene). If infra-apply hasn't created the app or the job yet, it warns; the next deploy (or a
# re-run) picks the image up.
set -eu

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <test|prod> <image@sha256:digest>" >&2
  exit 2
fi
env=$1
image=$2
case $image in
  *@sha256:*) ;;
  *)
    echo "deploy-app: give the image by digest (…@sha256:…), not by tag" >&2
    exit 2
    ;;
esac

group="rg-weesite-$env"
app="ca-weesite-web"
job="caj-weesite-reindex"

az extension add --name containerapp --upgrade --only-show-errors >/dev/null

if ! az containerapp show --resource-group "$group" --name "$app" --query name --output tsv >/dev/null 2>&1; then
  echo "::warning::$app doesn't exist in $env yet: infra-apply creates it. Re-run this deploy afterwards."
  exit 0
fi

fqdn=$(az containerapp show --resource-group "$group" --name "$app" \
  --query properties.configuration.ingress.fqdn --output tsv)
echo "::add-mask::$fqdn"

echo "deploy-app: $env: updating $app"
az containerapp update --resource-group "$group" --name "$app" --image "$image" --only-show-errors --output none

revision=$(az containerapp show --resource-group "$group" --name "$app" \
  --query properties.latestRevisionName --output tsv)
running=$(az containerapp revision show --resource-group "$group" --name "$app" --revision "$revision" \
  --query properties.template.containers[0].image --output tsv)
if [ "$running" != "$image" ]; then
  echo "deploy-app: $env: the latest revision runs $running, not $image" >&2
  exit 1
fi

# Scale-to-zero environments start a replica on the first request; give it time.
for attempt in $(seq 1 30); do
  if [ "$(curl -fsS --max-time 10 "https://$fqdn/health" 2>/dev/null)" = Healthy ]; then
    echo "deploy-app: $env: healthy (try $attempt)"
    if az containerapp job show --resource-group "$group" --name "$job" --query name --output tsv >/dev/null 2>&1; then
      echo "deploy-app: $env: updating $job"
      az containerapp job update --resource-group "$group" --name "$job" --image "$image" --only-show-errors --output none
    else
      echo "::warning::$job doesn't exist in $env yet: infra-apply creates it. Re-run this deploy afterwards."
    fi
    echo "Deployed \`$image\` to $env; /health is healthy."
    exit 0
  fi
  sleep 10
done

echo "deploy-app: $env: /health didn't answer Healthy within 5 minutes" >&2
exit 1
