#!/bin/sh
# Download DB-IP's "IP to Country Lite" database (CC BY 4.0, https://db-ip.com) for the visitor
# countries in the analytics (T-0143), and check it before anything uploads it: the gzip file is
# whole, the database is at least MIN_BYTES and at most MAX_BYTES, and its MaxMind DB metadata is
# there and names a country database. DB-IP publishes each month's file early in the month; when
# this month's isn't out yet, last month's is taken.
# Writes <dir>/country.mmdb and <dir>/month (YYYY-MM). The geoip workflow runs it.
# Usage: scripts/geoip-fetch.sh <dir> [YYYY-MM]
set -eu

if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
  echo "usage: $0 <dir> [YYYY-MM]" >&2
  exit 2
fi

dir=$1
base=${GEOIP_BASE_URL:-https://download.db-ip.com/free}
min_bytes=${GEOIP_MIN_BYTES:-1048576}
max_bytes=268435456

fail() {
  echo "geoip-fetch: $1; not using it" >&2
  rm -f "$dir/download.gz" "$dir/country.mmdb.part" "$dir/meta" "$dir/meta.after"
  exit 1
}

fetch() {
  curl -fsSL --retry 3 --max-filesize 104857600 -o "$dir/download.gz" "$base/dbip-country-lite-$1.mmdb.gz"
}

mkdir -p "$dir"
if [ "$#" -eq 2 ]; then
  month=$2
  fetch "$month" || fail "dbip-country-lite-$month.mmdb.gz couldn't be downloaded"
else
  month=$(date -u +%Y-%m)
  if ! fetch "$month"; then
    month=$(date -u -d "$(date -u +%Y-%m-01) -1 day" +%Y-%m)
    fetch "$month" || fail "neither this month's nor last month's file could be downloaded"
  fi
fi
echo "geoip-fetch: downloaded dbip-country-lite-$month.mmdb.gz"

gzip -t "$dir/download.gz" || fail "the gzip file is damaged"
# At most max_bytes + 1 come out, so a file that unpacks to far more can't fill the disk.
gzip -dc "$dir/download.gz" | head -c "$((max_bytes + 1))" > "$dir/country.mmdb.part"
size=$(wc -c < "$dir/country.mmdb.part")
[ "$size" -ge "$min_bytes" ] || fail "the database is only $size bytes"
[ "$size" -le "$max_bytes" ] || fail "the database is larger than $max_bytes bytes"

# The metadata follows the last "\xAB\xCD\xEFMaxMind.com" marker, within the last 128 KiB.
tail -c 131072 "$dir/country.mmdb.part" > "$dir/meta"
marker=$(printf '\253\315\357MaxMind.com')
offset=$(LC_ALL=C grep -a -o -b -F "$marker" "$dir/meta" | tail -n 1 | cut -d: -f1)
[ -n "$offset" ] || fail "there's no MaxMind DB metadata"
tail -c +"$((offset + 1))" "$dir/meta" > "$dir/meta.after"
LC_ALL=C grep -a -q -F database_type "$dir/meta.after" || fail "the metadata has no database type"
LC_ALL=C grep -a -q -i -F country "$dir/meta.after" || fail "it isn't a country database"

mv "$dir/country.mmdb.part" "$dir/country.mmdb"
printf '%s\n' "$month" > "$dir/month"
rm -f "$dir/download.gz" "$dir/meta" "$dir/meta.after"
echo "geoip-fetch: $dir/country.mmdb ($size bytes, $month) passed the checks"
