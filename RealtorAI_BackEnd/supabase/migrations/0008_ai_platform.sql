-- Feature 002-ai-listing-launch — T021
-- The Principle VIII model registry, versioned prompts, and AI usage accounting.
--
-- VIII.2: models are selected per task through this registry. Changing a model or provider is
--         a configuration change recorded in the audit log, never a code change.
-- VIII.4 (NON-NEGOTIABLE): a model may be enabled ONLY if its commercial-use license is
--         recorded AND it passes the evaluation harness. Enforced by a check constraint, so
--         no code path — API, admin endpoint, or otherwise — can enable an unlicensed or
--         unevaluated model.
-- VIII.5: every AI output is validated against a versioned JSON Schema; prompts are versioned
--         repo files and their version/hash is logged per call.
-- FR-064: writes require profiles.platform_admin. Agents and broker admins get nothing here.
-- FR-055: per-listing budget is enforced before dispatch; ai_usage records consumption.

create type public.model_eval_status as enum ('unverified', 'passed', 'failed');

-- Helper mirroring 0003's pattern: SECURITY DEFINER so the policy can read profiles without
-- being subject to profiles' own RLS.
create or replace function public.is_platform_admin()
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    select coalesce(
        (select p.platform_admin from public.profiles p where p.id = auth.uid()),
        false
    );
$$;

comment on function public.is_platform_admin() is
    'FR-064. True only for a platform administrator. The flag itself is not settable through '
    'any application path (see 0004).';

-- ───────────────────────────── ai_model_registry ─────────────────────────────

create table public.ai_model_registry (
    id uuid primary key default gen_random_uuid(),
    task_key text not null,
    provider text not null,
    model_id text not null,
    endpoint text,
    parameters jsonb not null default '{}'::jsonb,
    fallback_registry_id uuid references public.ai_model_registry (id) on delete set null,
    -- Required to enable (VIII.4.a). The actual license string, not a boolean.
    license text,
    eval_status public.model_eval_status not null default 'unverified',
    eval_result jsonb,
    enabled boolean not null default false,
    verified_at timestamptz,

    constraint registry_task_key_known check (
        task_key in ('photo.analysis', 'photo.embedding', 'listing.content', 'compliance.judge')
    ),

    -- VIII.4: the enablement gate, as a database guarantee.
    constraint registry_enable_requires_license_and_eval check (
        enabled = false
        or (license is not null and eval_status = 'passed')
    ),

    constraint registry_no_self_fallback check (fallback_registry_id is distinct from id)
);

comment on table public.ai_model_registry is
    'Principle VIII model registry. No vendor, model id, or endpoint may appear in business '
    'code — everything resolves through here.';
comment on constraint registry_enable_requires_license_and_eval on public.ai_model_registry is
    'VIII.4: enabling requires a recorded commercial license and a passing evaluation.';

-- Exactly one enabled model per task.
create unique index registry_one_enabled_per_task
    on public.ai_model_registry (task_key)
    where enabled;

create index registry_task_idx on public.ai_model_registry (task_key);

-- ───────────────────────────── prompt_versions ─────────────────────────────

create table public.prompt_versions (
    id uuid primary key default gen_random_uuid(),
    task_key text not null,
    version int not null,
    -- Must match the hash of the repo file at RealtorAI.Api/prompts/{task}/v{n}.md.
    -- A startup check fails if they drift (VIII.5).
    prompt_hash text not null,
    output_schema jsonb not null,
    active boolean not null default false,
    created_at timestamptz not null default timezone('utc'::text, now()),

    constraint prompt_versions_unique unique (task_key, version),
    constraint prompt_versions_version_positive check (version >= 1)
);

comment on table public.prompt_versions is
    'Versioned prompt assets. The prompt text lives in the repository; this table records its '
    'hash, its output schema, and which version is active.';

create unique index prompt_versions_one_active_per_task
    on public.prompt_versions (task_key)
    where active;

-- ───────────────────────────── ai_usage ─────────────────────────────

create table public.ai_usage (
    id uuid primary key default gen_random_uuid(),
    listing_id uuid not null references public.listings (id) on delete cascade,
    task_key text not null,
    model_id text not null,
    tokens_in int not null default 0,
    tokens_out int not null default 0,
    image_count int not null default 0,
    -- Null until provider pricing is verified; the budget is enforced in token/image units
    -- meanwhile (research.md R16).
    cost_usd numeric(10, 4),
    cache_hit boolean not null default false,
    created_at timestamptz not null default timezone('utc'::text, now()),

    constraint ai_usage_nonneg check (
        tokens_in >= 0 and tokens_out >= 0 and image_count >= 0
    )
);

comment on table public.ai_usage is
    'Per-call AI consumption, aggregated per listing to enforce the FR-055 budget before '
    'dispatch.';

create index ai_usage_listing_idx on public.ai_usage (listing_id);

-- ───────────────────────────── Row Level Security ─────────────────────────────

alter table public.ai_model_registry enable row level security;
alter table public.prompt_versions enable row level security;
alter table public.ai_usage enable row level security;

-- Registry and prompts: platform administrators only, for both read and write (FR-064).
-- The service role bypasses RLS, which is how the AI layer reads them at runtime.
create policy "registry_platform_admin_all"
    on public.ai_model_registry for all
    using (public.is_platform_admin())
    with check (public.is_platform_admin());

create policy "prompt_versions_platform_admin_all"
    on public.prompt_versions for all
    using (public.is_platform_admin())
    with check (public.is_platform_admin());

-- Usage is readable through the parent listing; written by the service role only.
create policy "ai_usage_select_via_listing"
    on public.ai_usage for select
    using (
        exists (select 1 from public.listings l
                where l.id = listing_id
                  and (l.agent_id = auth.uid()
                       or public.can_read_agent_as_broker_admin(l.agent_id)))
    );

-- ───────────────────────────── Grants ─────────────────────────────

grant select, insert, update on public.ai_model_registry to authenticated;
grant select, insert, update on public.prompt_versions to authenticated;
grant select on public.ai_usage to authenticated;
grant execute on function public.is_platform_admin() to authenticated;

-- ───────────────────────────── Seed: registry candidates ─────────────────────────────
-- Every row is enabled = false with eval_status = 'unverified', because none of these model
-- ids has been verified on the Hugging Face Hub in this environment: availability, the actual
-- license string, and whether HF Inference Providers serves them with image input and
-- JSON-schema response format all remain unconfirmed (research.md R1, Principle III).
-- The check constraint above makes it impossible to enable them until that is done.

insert into public.ai_model_registry (task_key, provider, model_id, parameters, enabled, eval_status)
values
    ('photo.analysis',   'huggingface-inference-providers', 'Qwen/Qwen3-VL-8B-Instruct',
     '{"temperature": 0.2, "response_format": "json_schema"}', false, 'unverified'),
    ('photo.analysis',   'huggingface-inference-providers', 'Qwen/Qwen2.5-VL-7B-Instruct',
     '{"temperature": 0.2, "response_format": "json_schema", "role": "fallback"}', false, 'unverified'),
    ('photo.embedding',  'vision-sidecar', 'google/siglip2-base-patch16-224',
     '{"dimension": 768}', false, 'unverified'),
    ('photo.embedding',  'vision-sidecar', 'openai/clip-vit-base-patch32',
     '{"dimension": 512, "role": "fallback"}', false, 'unverified'),
    ('listing.content',  'huggingface-inference-providers', 'Qwen/Qwen3-30B-A3B-Instruct-2507',
     '{"temperature": 0.7, "response_format": "json_schema"}', false, 'unverified'),
    ('listing.content',  'huggingface-inference-providers', 'mistralai/Mistral-Small-3.2-24B-Instruct-2506',
     '{"temperature": 0.7, "response_format": "json_schema", "role": "fallback"}', false, 'unverified'),
    -- FR-024: compliance judging is deterministic.
    ('compliance.judge', 'huggingface-inference-providers', 'Qwen/Qwen3-30B-A3B-Instruct-2507',
     '{"temperature": 0, "response_format": "json_schema"}', false, 'unverified');
