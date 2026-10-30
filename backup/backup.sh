#!/usr/bin/env bash
# Dumps the Sequfloh database, checks the dump, uploads it to the file bucket under backups/db/
# and removes backups there older than KEEP_DAYS (30). Secrets are read from the environment and never printed.
set -euo pipefail

missing=()
for v in DATABASE_URL STORAGE_SERVICE_URL STORAGE_BUCKET STORAGE_ACCESS_KEY STORAGE_SECRET_KEY; do
  [ -n "${!v:-}" ] || missing+=("$v")
done
if [ ${#missing[@]} -gt 0 ]; then
  echo "Backup not started: these settings are missing: ${missing[*]}" >&2
  exit 1
fi

PREFIX="backups/db/"
KEEP_DAYS="${KEEP_DAYS:-30}"
export AWS_ACCESS_KEY_ID="$STORAGE_ACCESS_KEY" AWS_SECRET_ACCESS_KEY="$STORAGE_SECRET_KEY"
export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-auto}"
s3() { aws --endpoint-url "$STORAGE_SERVICE_URL" "$@"; }

url="$DATABASE_URL"
case "$url" in
  *sslmode=*) ;;
  *localhost*|*127.0.0.1*) ;;
  *\?*) url="${url}&sslmode=require" ;;
  *) url="${url}?sslmode=require" ;;
esac

stamp=$(TZ=America/New_York date +%Y-%m-%d-%H%M)
name="sequfloh-db-${stamp}.dump"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

echo "Dumping the database ($(pg_dump --version))"
pg_dump --format=custom --no-owner --no-privileges --file="$work/$name" --dbname="$url"

count=$(pg_restore --list "$work/$name" | grep -vc '^;' || true)
size=$(stat -c %s "$work/$name")
if [ "$count" -lt 1 ]; then
  echo "Backup failed: the dump lists no objects." >&2
  exit 1
fi
echo "Dump checked: $count objects, $size bytes"

s3 s3 cp --only-show-errors "$work/$name" "s3://$STORAGE_BUCKET/$PREFIX$name"
echo "Uploaded $PREFIX$name"

cutoff=$(date -u -d "-${KEEP_DAYS} days" +%s)
s3 s3api list-objects-v2 --bucket "$STORAGE_BUCKET" --prefix "$PREFIX" \
  --query "Contents[].[Key,LastModified]" --output text > "$work/list"
while read -r key modified; do
  case "$key" in "$PREFIX"sequfloh-db-*.dump) ;; *) continue ;; esac
  if [ "$(date -u -d "$modified" +%s)" -lt "$cutoff" ]; then
    s3 s3 rm --only-show-errors "s3://$STORAGE_BUCKET/$key"
    echo "Removed old backup $key"
  fi
done < "$work/list"
echo "Backup finished"
