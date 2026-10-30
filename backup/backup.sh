#!/usr/bin/env bash
# Dumps the Sequfloh database, checks the dump, uploads it to the file bucket under backups/db/
# and removes backups there older than KEEP_DAYS (30). Secrets are read from the environment and never printed.
set -euo pipefail

# Settings pasted or linked in a dashboard can carry a trailing newline or spaces: drop CR/LF and trim.
clean() { local v="${1//$'\r'/}"; v="${v//$'\n'/}"; v="${v#"${v%%[![:space:]]*}"}"; v="${v%"${v##*[![:space:]]}"}"; printf '%s' "$v"; }
for v in DATABASE_URL STORAGE_SERVICE_URL STORAGE_BUCKET STORAGE_ACCESS_KEY STORAGE_SECRET_KEY; do
  if [ -n "${!v:-}" ]; then printf -v "$v" '%s' "$(clean "${!v}")"; fi
done

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

# libpq keyword value, quoted so spaces, quotes and backslashes survive
kv() { local x="${2//\\/\\\\}"; x="${x//\'/\\\'}"; printf "%s='%s' " "$1" "$x"; }

# The app also accepts an Npgsql string (Host=...;Username=...); turn that into a libpq string
to_libpq() {
  local out="" part key val IFS=';'
  for part in $1; do
    [ -n "$(clean "$part")" ] || continue
    key=$(clean "${part%%=*}"); val=$(clean "${part#*=}")
    case "${key,,}" in
      host|server) out+=$(kv host "$val") ;;
      port) out+=$(kv port "$val") ;;
      database|db) out+=$(kv dbname "$val") ;;
      username|user\ id|userid|user) out+=$(kv user "$val") ;;
      password|pwd) out+=$(kv password "$val") ;;
      ssl\ mode|sslmode)
        case "${val,,}" in verifyfull|verify-full) val=verify-full ;; verifyca|verify-ca) val=verify-ca ;; *) val="${val,,}" ;; esac
        out+=$(kv sslmode "$val") ;;
      channel\ binding|channel_binding) out+=$(kv channel_binding "${val,,}") ;;
    esac
  done
  printf '%s' "$out"
}

case "$DATABASE_URL" in
  postgres://*|postgresql://*)
    url="$DATABASE_URL"
    case "$url" in
      *sslmode=*) ;;
      *localhost*|*127.0.0.1*) ;;
      *\?*) url="${url}&sslmode=require" ;;
      *) url="${url}?sslmode=require" ;;
    esac ;;
  *)
    url=$(to_libpq "$DATABASE_URL")
    if [ -z "$url" ]; then echo "Backup not started: DATABASE_URL is not a postgres:// URL or a Host=...;Database=... string." >&2; exit 1; fi
    case "$url" in
      *"sslmode="*) ;;
      *"host='localhost'"*|*"host='127.0.0.1'"*) ;;
      *) url+="sslmode='require'" ;;
    esac ;;
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
