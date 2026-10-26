# ERPSystem — standalone deployment (Docker Compose)

> **⚠️ DO NOT USE THIS COPY FOR DEPLOYMENT (rc3).**
> The canonical deployment package is `deployment/` (sibling of `source/`).
> This `source/deploy/` folder exists only as the Docker build context
> (`context: ../source`, `dockerfile: deploy/web.Dockerfile|api.Dockerfile`).
> Its `docker-compose.yml` and `deploy.sh` differ from the canonical copies
> and must not be executed.

This folder contains the deployment files inside the source tree
(`deploy/` is next to `src/` and `ERPSystem.slnx`).

## Components

| File | Purpose |
|---|---|
| `docker-compose.yml` | Stack: `db` (PostgreSQL 17) + `web` + `api` + `caddy` |
| `web.Dockerfile` | Multi-stage build for the Blazor web app + `pg_dump` client |
| `api.Dockerfile` | Multi-stage build for the REST API (mobile client) |
| `Caddyfile` | Generic reverse proxy: `/api/*` + `/health` → api, everything else → web |
| `.env.example` | Secret/config template (copy to `.env`, chmod 600, never commit) |
| `deploy.sh` | Idempotent orchestrator: validate → db → optional restore → build → verify |
| `backup.sh` | Daily `pg_dump` via the `db` service + retention (cron) |

## Architecture (security by default)

```
Internet → firewall 22/HTTP_PORT/HTTPS_PORT → Caddy
                                   ├── /api/* + /health → api:8080
                                   └── everything else  → web:8080
PostgreSQL (postgres:17) has NO published port; internal `erpnet` only.
```

- `COMPOSE_PROJECT_NAME` is mandatory and unique per installation; it namespaces
  containers, networks and volumes so two installs never collide.
- `POSTGRES_DB` is fixed by the approved template: **`ERPCommercialDb`**.
  `deploy.sh` refuses to start with any other value.
- Caddy has no default domain or certificate. It serves HTTP for any Host.
  To use a customer domain with automatic HTTPS, edit `Caddyfile` (see below).

## Initial deployment

1. Create `.env` from the example and set the values:

   ```bash
   cp .env.example .env
   chmod 600 .env
   # edit .env: COMPOSE_PROJECT_NAME, POSTGRES_PASSWORD, JWT_SECRET, SETUP_TOKEN
   ```

2. Validate before touching Docker:

   ```bash
   bash deploy.sh --check-env
   ```

3. Deploy and restore the approved template:

   ```bash
   bash deploy.sh /path/to/ERPCommercialDb-golden-template-20261018T1621Z.dump
   ```

   `deploy.sh` restores only when the database is empty; it refuses to
   overwrite existing data. Without a dump argument it only (re)starts the stack.

4. First administrator: open `http://<SERVER-IP>:${HTTP_PORT}/setup`, enter the
   `SETUP_TOKEN` from `.env`, then empty `SETUP_TOKEN` and recreate `web`.

## Customer domain + HTTPS (optional)

1. Point the customer's DNS record to this server.
2. In `Caddyfile`, uncomment the `erp.example.com` site block and replace the
   domain and contact e-mail with the customer's values.
3. Keep `HTTP_PORT`/`HTTPS_PORT` (80/443) published; Caddy obtains and renews
   the certificate automatically.

No domain, IP, certificate or upstream belonging to another customer is
included in this package.

## Backups

```cron
17 3 * * * /bin/bash /opt/erp/deploy/backup.sh >> /opt/erp/data/backups/backup.log 2>&1
```

`backup.sh` uses the same `POSTGRES_DB=ERPCommercialDb`, verifies the dump
inside the `db` service, keeps 14 days by default, and writes `last_backup.txt`.

## Recurring deploy (code update)

```bash
cd /opt/erp/deploy
bash deploy.sh --check-env
docker compose --env-file .env up -d --build
```

`docker compose up -d` never restores data; the `pgdata` volume is untouched.
