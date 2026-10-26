#!/usr/bin/env bash
# ------------------------------------------------------------------
# ERPSystem - production deployment orchestration (idempotent).
#
#   sudo bash deploy.sh --check-env
#       Validate .env only; no container is created or started.
#
#   sudo bash deploy.sh [path-to-database-dump.dump]
#       Validate .env, start PostgreSQL, optionally restore the dump into
#       an EMPTY database, then build and start web/api/caddy.
#
# The database name is fixed by the approved template: ERPCommercialDb.
# A different POSTGRES_DB is rejected before anything is started.
# ------------------------------------------------------------------
set -euo pipefail

STACK_DIR="${STACK_DIR:-/opt/erp}"
COMPOSE_DIR="${COMPOSE_DIR:-$STACK_DIR/deploy}"
DATA_DIR="${DATA_DIR:-$STACK_DIR/data}"
EXPECTED_DB="ERPCommercialDb"

CHECK_ONLY=0
if [ "${1:-}" = "--check-env" ]; then CHECK_ONLY=1; shift; fi

DUMP_ABS=""
if [ "${1:-}" != "" ]; then
  DUMP_ABS="$(realpath "$1")"
fi

if [ ! -d "$COMPOSE_DIR" ]; then
  echo "!! compose directory not found: $COMPOSE_DIR"
  exit 1
fi
cd "$COMPOSE_DIR"

if [ ! -f docker-compose.yml ] || [ ! -f .env.example ]; then
  echo "!! docker-compose.yml or .env.example missing in $COMPOSE_DIR"
  exit 1
fi

# ---- .env: create from example (random secrets) if missing -------------
if [ ! -f .env ]; then
  if [ "$CHECK_ONLY" = "1" ]; then
    echo "!! .env is missing: create it from .env.example first"
    exit 1
  fi
  echo "==> .env missing -> generating from .env.example with random secrets"
  cp .env.example .env
  chmod 600 .env
  NEW_PG="$(openssl rand -hex 24)"
  NEW_JWT="$(openssl rand -hex 32)"
  NEW_PROJECT="erp_$(openssl rand -hex 4)"
  sed -i "s/^POSTGRES_PASSWORD=.*/POSTGRES_PASSWORD=${NEW_PG}/" .env
  sed -i "s/^JWT_SECRET=.*/JWT_SECRET=${NEW_JWT}/" .env
  sed -i "s/^COMPOSE_PROJECT_NAME=.*/COMPOSE_PROJECT_NAME=${NEW_PROJECT}/" .env
fi
chmod 600 .env

envval() { grep -E "^$1=" .env | head -1 | cut -d= -f2-; }
fail() { echo "!! $1"; exit 1; }

POSTGRES_USER="$(envval POSTGRES_USER)"; POSTGRES_USER="${POSTGRES_USER:-erp_app}"
POSTGRES_DB="$(envval POSTGRES_DB)"
POSTGRES_PASSWORD="$(envval POSTGRES_PASSWORD)"
JWT_SECRET="$(envval JWT_SECRET)"
COMPOSE_PROJECT_NAME="$(envval COMPOSE_PROJECT_NAME)"
HTTP_PORT="$(envval HTTP_PORT)"; HTTP_PORT="${HTTP_PORT:-80}"
HTTPS_PORT="$(envval HTTPS_PORT)"; HTTPS_PORT="${HTTPS_PORT:-443}"
SETUP_TOKEN="$(envval SETUP_TOKEN)"
OWNER_PASSWORD_HASH="$(envval OWNER_PASSWORD_HASH)"

# ---- Mandatory, non-placeholder configuration -------------------------
[ -n "$POSTGRES_USER" ] || fail "POSTGRES_USER is empty in .env"
[ -n "$POSTGRES_DB" ] || fail "POSTGRES_DB is empty in .env"
[ "$POSTGRES_DB" = "$EXPECTED_DB" ] || fail "POSTGRES_DB must be '$EXPECTED_DB' (the approved template database); got '$POSTGRES_DB'. Refusing to start with a different/empty database."

case "$POSTGRES_PASSWORD" in
  ""|*CHANGE_ME*) fail "POSTGRES_PASSWORD is empty or still contains CHANGE_ME; generate it with: openssl rand -hex 24" ;;
esac
[ "${#POSTGRES_PASSWORD}" -ge 16 ] || fail "POSTGRES_PASSWORD is too short (minimum 16 characters)"

case "$JWT_SECRET" in
  ""|*CHANGE_ME*) fail "JWT_SECRET is empty or still contains CHANGE_ME; generate it with: openssl rand -hex 32" ;;
esac
[[ "$JWT_SECRET" =~ ^[0-9a-fA-F]{64}$ ]] || fail "JWT_SECRET must be exactly 64 hex characters (32 bytes)"

case "$COMPOSE_PROJECT_NAME" in
  ""|*CHANGE_ME*) fail "COMPOSE_PROJECT_NAME is empty or still contains CHANGE_ME; use a unique name per installation" ;;
esac
[[ "$COMPOSE_PROJECT_NAME" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || fail "COMPOSE_PROJECT_NAME must match [a-z0-9][a-z0-9_-]*"

[[ "$HTTP_PORT" =~ ^[0-9]+$ ]] || fail "HTTP_PORT must be a number"
[[ "$HTTPS_PORT" =~ ^[0-9]+$ ]] || fail "HTTPS_PORT must be a number"

if [ -n "$SETUP_TOKEN" ]; then
  case "$SETUP_TOKEN" in *[[:space:]]*) fail "SETUP_TOKEN must not contain whitespace" ;; esac
  [ "${#SETUP_TOKEN}" -ge 16 ] || fail "SETUP_TOKEN is too short (minimum 16 characters)"
fi

case "$OWNER_PASSWORD_HASH" in *CHANGE_ME*) fail "OWNER_PASSWORD_HASH still contains CHANGE_ME" ;; esac

echo "==> configuration accepted (database: $POSTGRES_DB, project: $COMPOSE_PROJECT_NAME)"
if [ "$CHECK_ONLY" = "1" ]; then
  echo "==> --check-env: no container was started"
  exit 0
fi

if [ -n "$DUMP_ABS" ] && [ ! -f "$DUMP_ABS" ]; then
  echo "!! dump not found: $DUMP_ABS"; exit 1
fi

mkdir -p "$STACK_DIR" "$DATA_DIR/backups"

echo "==> stack dir: $STACK_DIR"
echo "==> compose dir: $COMPOSE_DIR"

COMPOSE() { docker compose -f "$COMPOSE_DIR/docker-compose.yml" --env-file "$COMPOSE_DIR/.env" "$@"; }
DBCMD() { docker compose -f "$COMPOSE_DIR/docker-compose.yml" --env-file "$COMPOSE_DIR/.env" exec -T -i db "$@"; }

# ---- PostgreSQL first (fresh volume => bootstrap) ----------------------
echo "==> starting database container"
COMPOSE up -d db

echo "==> waiting for PostgreSQL to become healthy"
for i in $(seq 1 60); do
  CID="$(COMPOSE ps -q db 2>/dev/null || true)"
  if [ -n "$CID" ] && [ "$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$CID" 2>/dev/null)" = "healthy" ]; then
    echo "    database healthy (${i} tries)"; break
  fi
  if [ "$i" = "60" ]; then
    echo "!! database did not become healthy - see: docker compose logs db"; exit 1
  fi
  sleep 5
done

# ---- Initial data restore (ONLY when a dump is supplied) ---------------
if [ -n "$DUMP_ABS" ]; then
  # Only restore if the database is still empty (never over existing data).
  TABLE_COUNT="$(DBCMD psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "SELECT count(*) FROM pg_tables WHERE schemaname='public';")"
  if [ "$TABLE_COUNT" != "0" ]; then
    echo "!! database already contains $TABLE_COUNT tables - refusing to restore over data."; exit 1
  fi
  echo "==> restoring database dump: $DUMP_ABS"
  DBCMD pg_restore \
    --no-owner --no-privileges --exit-on-error -U "$POSTGRES_USER" \
    -d "$POSTGRES_DB" /dev/stdin < "$DUMP_ABS"
  echo "==> restore finished"
fi

# ---- Full stack -------------------------------------------------------
echo "==> building and starting web / api / caddy"
COMPOSE up -d --build

# ---- Verification -----------------------------------------------------
echo "==> verifying services"
sleep 5
for svc in web api caddy; do
  CID="$(COMPOSE ps -q "$svc" 2>/dev/null || true)"
  STATUS="none"
  if [ -n "$CID" ]; then
    STATUS="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}running{{end}}' "$CID" 2>/dev/null || echo unknown)"
  fi
  echo "    $svc -> $STATUS"
done

echo ""
echo "==> local endpoint checks"
curl -fsS "http://127.0.0.1:${HTTP_PORT}/health"              && echo "  [api /health ok]"
curl -fsS -o /dev/null -w "  [web http status] %{http_code}\n" "http://127.0.0.1:${HTTP_PORT}/login" || true
curl -fsS -o /dev/null -w "  [api /api/items unauth] %{http_code}\n" "http://127.0.0.1:${HTTP_PORT}/api/items" || true

# ---- Firewall (SSH + configured HTTP/HTTPS only) -----------------------
if command -v ufw >/dev/null 2>&1; then
  echo "==> ensuring UFW allows only 22,${HTTP_PORT},${HTTPS_PORT}/tcp"
  ufw allow 22/tcp >/dev/null 2>&1 || true
  ufw allow "${HTTP_PORT}/tcp" >/dev/null 2>&1 || true
  ufw allow "${HTTPS_PORT}/tcp" >/dev/null 2>&1 || true
  echo "    ufw status:"
  ufw status | sed 's/^/    /'
fi

echo ""
echo "==> deployment finished"
echo "    Public web : http://<SERVER-IP>:${HTTP_PORT}/"
echo "    Mobile API : http://<SERVER-IP>:${HTTP_PORT}/health  and  /api/..."
echo "    Owner URL  : disabled unless OWNER_SECRET_PATH is set in .env"
echo "    Logs       : docker compose logs -f web api"
