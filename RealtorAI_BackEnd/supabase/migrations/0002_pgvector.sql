-- Feature 002-ai-listing-launch — T015
-- Enables pgvector for photo embeddings (near-duplicate detection and zero-shot room
-- pre-classification). See specs/002-ai-listing-launch/data-model.md "photo_embeddings".
--
-- Verified available in this project's bundled Postgres: pg_available_extensions reports
-- vector 0.8.2 (research.md R9 — this resolves that item's UNVERIFIED note).

create extension if not exists vector;

comment on extension vector is
    'pgvector — image embedding storage and similarity search for listing photo analysis.';
