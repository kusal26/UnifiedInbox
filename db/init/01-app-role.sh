#!/bin/sh
# Creates the least-privilege runtime role used by the API and worker.
# The migrator keeps owner credentials; table grants are applied by EF migrations.
set -eu
: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_DB:?POSTGRES_DB is required}"
: "${APP_DB_PASSWORD:?APP_DB_PASSWORD is required}"

psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_runtime') THEN
    CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD '$APP_DB_PASSWORD';
  ELSE
    ALTER ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD '$APP_DB_PASSWORD';
  END IF;
END
\$\$;
GRANT CONNECT ON DATABASE "$POSTGRES_DB" TO app_runtime;
GRANT USAGE ON SCHEMA public TO app_runtime;
SQL
