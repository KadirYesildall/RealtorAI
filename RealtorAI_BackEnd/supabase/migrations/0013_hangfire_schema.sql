-- Feature 002-ai-listing-launch — T055
-- A dedicated, closed schema for Hangfire's job storage (research R12).
--
-- WHY A SEPARATE SCHEMA. Hangfire installs and upgrades its own tables from versioned scripts
-- it ships, tracked in hangfire.schema. Those are third-party internals that change with the
-- package, so this migration does not try to reproduce them; it owns the one piece of DDL that
-- matters to this project — where they live and who can reach them — and Hangfire fills the
-- schema on first start. Keeping them out of `public` means they can never collide with a table
-- a migration defines, and never appear in the Data API, which exposes `public` only.
--
-- WHY IT IS CLOSED. Job arguments and state carry listing ids and pipeline progress. Only the
-- API's own connection (postgres / service role) needs them. Research R18 found that Supabase's
-- default privileges silently granted ALL — including TRUNCATE — on every new `public` table to
-- `anon` and `authenticated`; this schema is closed explicitly so the same thing cannot happen
-- here, and HangfireStorageTests asserts it after Hangfire has created its tables.

create schema if not exists hangfire;

comment on schema hangfire is
    'Hangfire job storage (pipeline background jobs). Tables are created and upgraded by '
    'Hangfire.PostgreSql itself. No client role may reach this schema.';

revoke all on schema hangfire from public, anon, authenticated;

-- Tables, sequences and functions Hangfire creates later, as postgres, get no default grant.
alter default privileges in schema hangfire revoke all on tables from public, anon, authenticated;
alter default privileges in schema hangfire revoke all on sequences from public, anon, authenticated;
alter default privileges in schema hangfire revoke all on functions from public, anon, authenticated;
