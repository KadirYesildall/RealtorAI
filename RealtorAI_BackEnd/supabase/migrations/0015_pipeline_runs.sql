-- Feature 002-ai-listing-launch — US2 (T095)
-- Pipeline run state, and where an agent's photo overrides live. See data-model.md "Pipeline"
-- and research R34.
--
-- FR-053: progress is visible per step and per photo, and GET /pipeline is the record of it —
--         SignalR notifications are hints to re-read this, never the source. So the state is
--         persisted rather than held in the worker's memory, and survives a restart.
-- FR-011: every AI result about a photo is overridable. The model's output stays untouched in
--         photo_analyses (FR-049 keeps the original visible); the agent's value is stored on the
--         photo and wins when present.

-- ───────────────────────────── pipeline_runs ─────────────────────────────

create table public.pipeline_runs (
    id uuid primary key default gen_random_uuid(),
    -- NO ACTION, like audit_events: a listing is never hard-deleted (FR-067), and a run is part
    -- of the record of what was done to it.
    listing_id uuid not null references public.listings (id),
    -- The agent whose action started the run. Every AI call it makes is audited as theirs.
    started_by uuid not null references public.profiles (id),
    tone public.tone,
    variant_count int,
    started_at timestamptz not null default timezone('utc'::text, now()),
    finished_at timestamptz,

    constraint pipeline_runs_variant_count check (variant_count is null or variant_count between 2 and 3)
);

comment on table public.pipeline_runs is
    'One analysis run of a listing. At most one unfinished run per listing; starting again while '
    'one is unfinished returns that run.';

create unique index pipeline_runs_one_active_per_listing
    on public.pipeline_runs (listing_id)
    where finished_at is null;

create index pipeline_runs_listing_started_idx on public.pipeline_runs (listing_id, started_at desc);

-- ───────────────────────────── pipeline_step_states ─────────────────────────────

create table public.pipeline_step_states (
    run_id uuid not null references public.pipeline_runs (id) on delete cascade,
    step text not null,
    -- Execution order, so the status endpoint lists steps as they run.
    position int not null,
    state text not null default 'queued',
    -- Phrased for the agent; present when a step failed or was skipped.
    failure_reason text,
    updated_at timestamptz not null default timezone('utc'::text, now()),

    primary key (run_id, step),

    constraint pipeline_step_known check (step in (
        'normalize', 'technical_quality', 'embeddings', 'photo_analysis', 'cover_and_order',
        'content_generation', 'compliance'
    )),
    constraint pipeline_step_state_known check (state in ('queued', 'running', 'done', 'failed', 'skipped')),
    constraint pipeline_step_reason_when_not_done check (
        state not in ('failed', 'skipped') or failure_reason is not null
    )
);

-- ───────────────────────────── pipeline_photo_states ─────────────────────────────

create table public.pipeline_photo_states (
    run_id uuid not null references public.pipeline_runs (id) on delete cascade,
    -- A photo removed from the listing mid-run leaves the run with it.
    photo_id uuid not null references public.photos (id) on delete cascade,
    state text not null default 'queued',
    failure_reason text,
    updated_at timestamptz not null default timezone('utc'::text, now()),

    primary key (run_id, photo_id),

    constraint pipeline_photo_state_known check (state in ('queued', 'analyzing', 'done', 'failed')),
    constraint pipeline_photo_reason_when_failed check (state <> 'failed' or failure_reason is not null)
);

create index pipeline_photo_states_photo_idx on public.pipeline_photo_states (photo_id);

-- ───────────────────────────── Agent overrides on photos (FR-011) ─────────────────────────────

alter table public.photos
    add column room_type_override public.room_type,
    add column feature_tags_override jsonb,
    add constraint photos_feature_tags_override_is_array check (
        feature_tags_override is null or jsonb_typeof(feature_tags_override) = 'array'
    );

comment on column public.photos.room_type_override is
    'FR-011: the agent''s room type. Wins over photo_analyses.room_type when set; null means '
    'the model''s value stands. The override itself is audited as photo_override.';
comment on column public.photos.feature_tags_override is
    'FR-011: the agent''s feature tags, replacing the model''s when set.';

-- ───────────────────────────── Row Level Security ─────────────────────────────
--
-- Run state is written by the API under its privileged role and read through GET /pipeline,
-- which authorizes every read (FR-063). No client role has any reason to touch it, so: RLS on,
-- no policy, no privilege — the same posture as photo_embeddings (0012).

alter table public.pipeline_runs enable row level security;
alter table public.pipeline_step_states enable row level security;
alter table public.pipeline_photo_states enable row level security;

revoke all on table
    public.pipeline_runs,
    public.pipeline_step_states,
    public.pipeline_photo_states
from anon, authenticated;
