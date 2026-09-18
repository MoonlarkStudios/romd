#!/usr/bin/env bash
set -euo pipefail

psql --set ON_ERROR_STOP=1 --username "${POSTGRES_USER}" --dbname "${POSTGRES_DB}" <<'SQL'
\getenv provisioner_password ROMD_POSTGRES_PROVISIONER_PASSWORD
\getenv worker_password ROMD_POSTGRES_WORKER_PASSWORD
\getenv admin_password ROMD_POSTGRES_ADMIN_PASSWORD
\getenv consumer_password ROMD_POSTGRES_CONSUMER_PASSWORD

REVOKE CREATE ON DATABASE romd FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE ROLE romd_owner
    NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
CREATE ROLE romd_provisioner
    LOGIN PASSWORD :'provisioner_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
CREATE ROLE romd_worker
    LOGIN PASSWORD :'worker_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
CREATE ROLE romd_admin
    LOGIN PASSWORD :'admin_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
CREATE ROLE romd_consumer
    LOGIN PASSWORD :'consumer_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;

GRANT romd_owner TO romd_provisioner;
ALTER ROLE romd_provisioner IN DATABASE romd SET role TO 'romd_owner';

CREATE SCHEMA romd AUTHORIZATION romd_owner;
CREATE SCHEMA hangfire AUTHORIZATION romd_owner;

GRANT CONNECT ON DATABASE romd TO romd_provisioner, romd_worker, romd_admin, romd_consumer;
GRANT USAGE ON SCHEMA hangfire TO romd_worker, romd_admin, romd_consumer;
GRANT USAGE ON SCHEMA romd TO romd_worker, romd_admin, romd_consumer;

ALTER DEFAULT PRIVILEGES FOR ROLE romd_owner IN SCHEMA hangfire
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO romd_worker, romd_admin;
ALTER DEFAULT PRIVILEGES FOR ROLE romd_owner IN SCHEMA hangfire
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO romd_worker, romd_admin;

-- These defaults keep the runtime/owner split for anything created in the schema. The
-- worker's PostgreSqlSchemaProvisioner (#128) then narrows the consumer to its exact
-- per-table and per-column allow-list once the application baseline has been applied.
ALTER DEFAULT PRIVILEGES FOR ROLE romd_owner IN SCHEMA romd
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO romd_worker, romd_admin;
ALTER DEFAULT PRIVILEGES FOR ROLE romd_owner IN SCHEMA romd
    GRANT SELECT ON TABLES TO romd_consumer;
ALTER DEFAULT PRIVILEGES FOR ROLE romd_owner IN SCHEMA romd
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO romd_worker, romd_admin;
SQL
