#!/usr/bin/env bash
# ------------------------------------------------------------------
# ERPSystem - daily PostgreSQL backup (run from cron on the server)
#
# The backup is created with pg_dump INSIDE the db service, so no
# host-side PostgreSQL tooling is required. Path: /opt/erp/data/backups/
# Keep the newest KEEP_DAYS copies, then verify the dump integrity.
#
# Suggested crontab line (as root or a user with docker rights):
#   17 3 * * * /bin/bash /opt/erp/deployment/backup.sh >> /opt/erp/data/backups/backup.log 2>&1
# ------------------------------------------------------------------
set -euo pipefail

STACK_DIR="${STACK_DIR:-/opt/erp}"
COMPOSE_DIR="${COMPOSE_DIR:-$STACK_DIR/deploy}"
DATA_DIR="${DATA_DIR:-$STACK_DIR/data}"
BACKUP_DIR="${BACKUP_DIR:-/opt/erp/data/backups}"
KEEP_DAYS="${KEEP_DAYS:-14}"
EXPECTED_DB="ERPCommercialDb"
POSTGRES_DB="${POSTGRES_DB:-$EXPECTED_DB}"
POSTGRES_USER="${POSTGRES_USER:-erp_app}"
ENV_FILE="${ENV_FILE:-$COMPOSE_DIR/.env}"
COMPOSE_FILE="$COMPOSE_DIR/docker-compose.yml"

# Load POSTGRES_PASSWORD from the stack .env (do not print it)
if [ -f "$ENV_FILE" ]; then
  set -a; . "$ENV_FILE"; set +a
fi
: "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD not found in $ENV_FILE}"
case "$POSTGRES_PASSWORD" in
  *CHANGE_ME*) echo "!! POSTGRES_PASSWORD still contains CHANGE_ME; refusing to run"; exit 1 ;;
esac
[ "$POSTGRES_DB" = "$EXPECTED_DB" ] || {
  echo "!! POSTGRES_DB must be '$EXPECTED_DB' (got '$POSTGRES_DB'); refusing to back up the wrong database"
  exit 1
}

DC() { docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"; }

mkdir -p "$BACKUP_DIR"
STAMP="$(date -u +%Y%m%d_%H%M%S)"
DUMP="$BACKUP_DIR/erp_${STAMP}.dump"

echo "[$(date -u +%FT%TZ)] starting pg_dump -> $DUMP"
# -Fc custom format; the password travels over the internal docker network only.
DC exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db \
  pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -f "/tmp/erp_tmp.dump"

# Verify the dump inside the container (the host path is not mounted there).
DC exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db \
  pg_restore -l /tmp/erp_tmp.dump >/dev/null

# Copy the verified dump out of the container into the host backup dir.
DC cp db:/tmp/erp_tmp.dump "$DUMP"
DC exec -T db rm -f /tmp/erp_tmp.dump
SIZE="$(du -h "$DUMP" | cut -f1)"
echo "[$(date -u +%FT%TZ)] dump verified: $SIZE"

# Record the marker used by the app's BackupHealthCheck (26h threshold).
echo "$(date -u +%FT%TZ)" > "$BACKUP_DIR/last_backup.txt"

# Retention: remove dumps older than KEEP_DAYS.
find "$BACKUP_DIR" -maxdepth 1 -type f -name 'erp_*.dump' -mtime "+${KEEP_DAYS}" \
  -delete 2>/dev/null || true
echo "[$(date -u +%FT%TZ)] done"
