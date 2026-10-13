# Implementation Plan: AI Listing Launch — Slice A (Stories 1–5)

**Branch**: `002-ai-listing-launch` | **Date**: 2026-10-12 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/002-ai-listing-launch/spec.md`

**Scope**: Slice A only — Story 1 (draft from photos + basics), Story 2 (AI photo analysis),
Story 3 (AI listing content), Story 4 (compliance review), Story 5 (single review-and-approve
screen), plus FR-020 (AB 723 photo disclosure), FR-049–FR-059 (audit, model registry, prompts,
background pipeline, resilience, budget, privacy, brokerage config, profile completion, model
evaluation), FR-060–FR-063 (brokerage membership, endpoint authorization), and FR-064–FR-067
(platform administrator role, photo retention, optimistic concurrency, soft delete — added by the
2026-10-13 clarification session).

**Explicitly out of scope**: Slice B (FR-028–FR-043: CRMLS submission, MLS-ready export,
marketing pack, publishing scheduler, Clear Cooperation guard, social integrations) and Slice C
(FR-044–FR-048: lifecycle content, lead inbox, insights). Only the seams Slice B needs are
created — see [Slice B seams](#slice-b-seams-created-in-this-plan).

## Summary

An agent uploads 1–100 real photos plus six basic property details; a background pipeline
normalizes the images, measures technical quality deterministically, embeds them for
near-duplicate detection, runs a vision-language model per photo for room type / features /
content flags / alt text, suggests a cover photo and display order, drafts RESO-aligned structured
data and public/private remarks in a chosen tone with 2–3 variants, then runs a two-layer
compliance pass (deterministic Fair Housing lexicon + context-sensitive judge, risky claims, CRMLS
rule profile, AB 723 photo integrity, advertising disclosure). Everything lands on one review
screen where the agent edits inline, confirms every AI-derived fact, and approves per output.
Nothing is published; Slice A ends at an approved, launch-ready listing.

Technical approach: a new **ASP.NET Core (.NET 10) modular-monolith Web API** owns the AI layer,
the compliance engine, RESO mapping, and the pipeline, because Principle VIII.1 requires all AI
calls to go through an `AiService` inside the .NET API and the pipeline needs durable background
workers. The existing **Next.js 16 / React 19** app keeps all UI and session handling and calls the
API for anything involving AI, compliance, or RESO. The existing **Supabase** stack remains the
single source of Postgres, Auth, and Storage; the API authenticates callers by validating
Supabase-issued JWTs via JWKS and authorizes every endpoint independently against resource
ownership or approved brokerage membership, because it connects to Postgres with a privileged
service role that bypasses RLS.

## Technical Context

**Language/Version**: C# / .NET 10 (API); TypeScript 5+ on Next.js 16.2.10 / React 19.2.4
(frontend); Python 3.12 (vision sidecar). SQL for all DDL.

**Primary Dependencies**:
- API: ASP.NET Core 10, `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`),
  `Microsoft.Extensions.Http.Resilience`, EF Core (query/mapping only — DDL stays in SQL, see
  [research R14](./research.md#r14-data-access--ef-core-alongside-supabase-migrations)), Hangfire
  (Postgres storage, dedicated `hangfire` schema), SignalR, Serilog + OpenTelemetry, a JSON Schema
  validator, `Npgsql` + `pgvector` bindings.
- Frontend: existing `@supabase/ssr` + `@supabase/supabase-js`, Zod v4 (present), Tailwind v4
  (present); **new**: TanStack Query, React Hook Form, dnd-kit.
- Sidecar: FastAPI, SigLIP2 (local weights), OpenCV.
- AI serving: Hugging Face Inference Providers (OpenAI-compatible router) for MVP; HF Inference
  Endpoints or self-hosted TGI/vLLM for production — configuration, not code (Principle VIII.3).

**Storage**: Supabase Postgres (local port 54322) with `pgvector` for photo embeddings; Supabase
Storage for originals (write-once bucket), normalized variants, and brokerage assets. **No MinIO
and no second Postgres** — see [research R9](./research.md#r9-local-development-topology--supabase-cli-vs-docker-compose)
and [R10](./research.md#r10-object-storage--supabase-storage-vs-minio).

**Testing**: xUnit + Testcontainers (Postgres+pgvector) for the API, including a per-endpoint
authorization matrix and an RLS suite for direct client access; Vitest (existing) for frontend
contract tests; Playwright (existing) for Stories 1–5 end-to-end with the AI layer stubbed by
deterministic fixtures; a standalone AI evaluation harness gating model enablement; a
build-failing test asserting no image/video-generation dependency or endpoint exists.

**Target Platform**: Linux containers for API and sidecar; Node server for Next.js; modern
browsers per Next.js 16 baseline (Chrome/Edge/Firefox 111+, Safari 16.4+). Windows 11 developer
workstations (this repo's environment) via Supabase CLI + `dotnet run` + Docker for the sidecar.

**Project Type**: Multi-service web application — Next.js frontend + .NET Web API + Python sidecar
over a shared Supabase Postgres, in the repo's mandated two-directory layout.

**Performance Goals**: Photo analysis for 30 photos under 2 minutes at p90 (SC-005); upload →
approved listing under 15 minutes for a 30-photo listing (SC-001); per-photo results stream to the
UI as each completes rather than batching (FR-053).

**Constraints**: Approvals must be impossible against unreviewed content (SC-011) and no edit may
be lost to a concurrent write (SC-012), both enforced by optimistic concurrency plus an
approval-time content-hash check (FR-066). Room-type classification accepted unchanged for ≥90% of photos on the labeled set
(SC-002); Fair Housing recall ≥95% with ≤10% false positives (SC-003); zero publishes without
recorded approval and zero generatively altered images, continuously asserted from the audit log
(SC-004); zero listings approved with an open blocking finding (SC-009); a total AI outage must
still allow a listing to be completed and approved entirely by hand (SC-010); AI cost per listing
within a configurable budget (SC-006), enforced in token/image units until provider pricing is
verified.

**Scale/Scope**: Slice A covers 5 user stories, ~35 functional requirements, ~19 new tables, ~25
API operations, and ~8 new frontend routes. Concurrency targets are not specified in the spec and
are **not** invented here; the pipeline is sized per-listing (≤100 photos) and horizontal API
scaling is a deployment concern deferred to the first load test.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Evaluated against RealtorAI Constitution **v3.0.0**.

| Principle | Status | How this plan satisfies it |
|---|---|---|
| **I. Specification-First** (NON-NEG) | ✅ PASS | `spec.md` exists, 15/15 checklist, clarifications resolved before this plan. |
| **II. Test-First Discipline** | ✅ PASS | Contract tests mandatory per endpoint; integration tests for the pipeline; `tasks.md` must order tests before implementation per story. Pure-presentation components may skip tests only with an explicit note. |
| **III. Verified Dependencies** (NON-NEG) | ⚠️ **PASS WITH RECORDED GATES** | Model ids, CRMLS rule values, AB 723 label specifics, Hangfire/Supabase package compatibility, and provider pricing are **not** guessed. Each is recorded as ⚠ UNVERIFIED in `research.md` with the exact check required, and each is gated so it cannot silently reach production: the model registry seeds every entry `enabled = false`, and the CRMLS rule profile fails closed on a missing value. See [Gated before AI enablement](#gated-before-ai-enablement). |
| **IV. Production-Ready Code Only** (NON-NEG) | ✅ PASS | No TODOs or stubs. The one risk — an `IMlsAdapter` with no implementor — is resolved by **not** writing it in Slice A (see Complexity Tracking row 3). `NullPropertyDataProvider` is a complete, intentional implementation of FR-008's unconfigured branch, not a stub. |
| **V. Clean, Modular & Simple + YAGNI** | ⚠️ **PASS WITH JUSTIFICATION** | Modular monolith, one purpose per module. Three complexity items are justified in Complexity Tracking: the Python sidecar, Hangfire+SignalR, and TanStack Query. `IMlsAdapter` is deferred rather than justified. |
| **VI. Global-First i18n** | ✅ PASS | Every new user-facing string ships as an EN **and** TR key. Gap in the plan input, added here — see [research R15](./research.md#r15-i18n-and-dark-mode-gaps-in-the-plan-input). Finding `reason`/`suggested_rewrite` stay English by design (analysis of U.S.-English copy); all UI chrome is localized. |
| **VII. Security & Data Privacy** | ✅ PASS | RLS enabled on every new table; FR-063 per-endpoint authorization because the service role bypasses RLS; originals bucket has no UPDATE/DELETE policy; `audit_events` append-only with no update/delete policy for any application role; EXIF/GPS stripped before any image leaves the boundary; no secrets with `NEXT_PUBLIC_` prefix. **FR-064**: `platform_admin` gates the model registry and is writable only by direct DB access — no API path may set it, so it cannot become a privilege-escalation target. |
| **VIII. AI Layer: Model-Agnostic by Registry** (NON-NEG) | ✅ PASS | VIII.1 all AI through `AiService` in the .NET API, no vendor/model id in business code. VIII.2 DB-backed registry; model change is configuration, audited. VIII.3 HF open-weight default. VIII.4 enablement requires recorded commercial license + eval-harness pass + data-boundary check. VIII.5 versioned JSON Schema per task, prompts as versioned repo files, version+hash logged per call. VIII.6 no generative imagery, enforced by a build-failing dependency/endpoint test; originals immutable. VIII.7 every AI output is a draft until explicit, logged human approval. |
| **A. Next.js/React Standards** | ✅ PASS | Server Components default; client leaves justified and enumerated (photo grid/reorder, inline editors, tone switcher, compliance panel, pipeline subscriber). `loading.tsx` + `error.tsx` on every new data-fetching segment. Route Handlers reserved for webhooks/callbacks — none needed in Slice A. Next.js 16 conventions read from bundled docs per `AGENTS.md`; see [research R8](./research.md#r8-nextjs-16--react-19-conventions-binding-on-all-frontend-work). |
| **B. State & API Resilience** | ✅ PASS | All AI/DB/HTTP calls in structured try/catch with user-facing messages; no `any`/unchecked `unknown`; Zod on every client form and API boundary input; 400 vs 422 distinction; timeouts, backoff, circuit breaker, model fallback, and an explicit "continue manually" path. |
| **C. Database & Security Hardening** | ✅ PASS | RLS policy designed per table in `data-model.md` before implementation; indexes on `listing_id`, `agent_id`, `brokerage_id`, `(scheduled_at, status)`, plus a vector index; Supabase pooled connections (pooler on 54329); service-role key server-only. |
| **D. AI Engine Operations** | ✅ PASS | Structured outputs schema-validated server-side with one retry on validation error then visible failure; photos downscaled (long edge ~1280px) and EXIF-stripped before any vision call; originals never sent. |
| **E. UI/UX Design Standards** | ✅ PASS | Semantic `--color-*` tokens only — no raw hex, no `dark:` variants; reuses existing `Button`/`Card`/`Input`/`TextField`/`Alert`/`Checkbox` primitives; photos as first-class large visuals; **dark mode correct in every state including queued/analyzing/failed/blocking**; WCAG AA verified in both themes; severity distinguishable without relying on colour alone. Dark mode was a gap in the plan input, added here. |
| **Workflow: Local-first development** | ✅ PASS | Schema developed against the local Supabase stack (`supabase start`); migrations in `RealtorAI_BackEnd/supabase/migrations/`; connection values taken from actual CLI output and documented in `quickstart.md`. This is why the plan input's Compose-owned Postgres was **not** adopted. |
| **Workflow: Sequential numbering** | ✅ PASS | Feature `002-ai-listing-launch`; task IDs `T0xx` assigned by `/speckit-tasks`. |
| **Governance: AI-surface plan disclosure** | ✅ PASS | This plan states the per-task registry entries with license + eval status, the versioned output schema and prompt layout, the data-boundary analysis, and the automated no-generative-imagery test, as v3.0.0's Compliance Review requires. |

**Post-Phase-1 re-check**: re-evaluated after `data-model.md` and `contracts/` were written. No new
violations. The two ⚠ items are unchanged in nature: Principle III's unverified externals remain
gated rather than resolved, and Principle V's three justified complexity items remain in
Complexity Tracking. No gate is failed; no unjustified violation exists.

### Gated before AI enablement

These must complete before any registry entry flips to `enabled = true`. They block Story 2 and 3
*going live*, not the build:

1. Hub verification per model: existence, non-deprecation, commercial license string recorded in
   the registry row, confirmed availability on HF Inference Providers **with image input** for the
   VLM and **JSON-schema response format** for all chat tasks.
2. AI evaluation harness built and passing: room classification ≥90% accepted (SC-002), Fair
   Housing recall ≥95% / FP ≤10% (SC-003), JSON-schema validity rate, p90 latency for 30 photos
   <2 min (SC-005), and measured cost per listing (SC-006).
3. Data-boundary check per VIII.4.c: no seller PII, no agent credentials, downscaled EXIF-stripped
   images only.

### Gated before compliance go-live

4. CRMLS rule profile populated with **actual published values** (remarks limits, required fields,
   photo specs, enumerations). The engine fails closed until then.
5. AB 723 disclosure label text and placement confirmed by counsel or CRMLS compliance staff —
   this is a criminal-liability statute and its interpretation is outside engineering's remit. See
   [research R4](./research.md#r4-ab-723-and-crmls-photo-integrity-requirements-fr-020).

## Project Structure

### Documentation (this feature)

```text
specs/002-ai-listing-launch/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/           # Phase 1 output (/speckit-plan command)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
RealtorAI_BackEnd/
├── supabase/
│   ├── config.toml                      # EXISTING — ports 54321/54322/54323/54324
│   └── migrations/
│       ├── 0001_profiles.sql            # EXISTING (feature 001)
│       ├── 0002_pgvector.sql            # NEW — create extension vector
│       ├── 0003_brokerages.sql          # NEW — brokerages, memberships, settings
│       ├── 0004_profile_license.sql     # NEW — license_number, license_state, completed_at
│       ├── 0005_listings.sql            # NEW — listings, listing_fields
│       ├── 0006_media.sql               # NEW — photos, photo_analyses, embeddings
│       ├── 0007_content_compliance.sql  # NEW — content_drafts, compliance_findings, approvals
│       ├── 0008_ai_platform.sql         # NEW — registry, prompt_versions, ai_usage
│       ├── 0009_audit.sql               # NEW — append-only audit_events
│       ├── 0010_mls_rule_profiles.sql   # NEW — MlsRuleProfile data (Slice B seam)
│       └── 0011_storage_buckets.sql     # NEW — buckets + write-once originals policy
└── RealtorAI.Api/                       # NEW — .NET 10 solution
    ├── Api/                             # Host: auth (JWKS), authz policies, SignalR hubs, DI
    ├── Modules.Brokerage/               # Memberships, approval workflow, settings
    ├── Modules.Listings/                # Listing aggregate, fields + provenance, state machine
    ├── Modules.Media/                   # Upload, immutable originals, variants, order, altered flow
    ├── Modules.Content/                 # Content drafts, versions, approvals
    ├── Modules.Audit/                   # Append-only audit writer
    ├── Libs.Ai/                         # AiService, model registry, prompt registry,
    │                                    # schema validation, cost tracking, provider fallback
    ├── Libs.Compliance/                 # FH lexicon, judge, risky claims, CRMLS rule engine,
    │                                    # AB 723 photo integrity, advertising disclosure
    ├── Libs.Reso/                       # RESO field keys, enumerations, CRMLS lookup mapping
    ├── Libs.Mls/                        # Slice B seam: MlsRuleProfile model ONLY (no IMlsAdapter)
    ├── Pipeline/                        # IPipelineStep orchestration, Hangfire jobs
    ├── prompts/                         # Versioned prompt assets: {task}/v{n}.md + v{n}.schema.json
    └── tests/
        ├── contract/                    # One per endpoint, incl. authorization matrix
        ├── integration/                 # Testcontainers: pipeline, RLS suite, EF-vs-SQL drift
        ├── unit/                        # Provenance, state machine, capabilities, lexicon goldens
        └── prohibitions/                # Build-failing no-generative-imagery test (VIII.6)

RealtorAI_BackEnd/vision-sidecar/        # NEW — Python 3.12 FastAPI (SigLIP2 + OpenCV)
├── app/                                 # /embed, /quality endpoints; called only by the API
└── tests/

RealtorAI_FrontEnd/                      # EXISTING Next.js 16 app — extended, not replaced
├── proxy.ts                             # EXISTING (Next 16 name; not middleware.ts)
├── app/
│   ├── actions/                         # EXISTING auth.ts, profile.ts + NEW membership action
│   └── dashboard/
│       ├── profile/complete/            # NEW — license, state, brokerage self-declaration
│       ├── brokerage/                   # NEW — settings, members (approve/reject)
│       └── listings/
│           ├── page.tsx                 # NEW — listing index
│           ├── new/                     # NEW — wizard: basics → upload → live pipeline
│           └── [id]/                    # NEW — review & approve (+ loading/error/audit)
├── components/
│   ├── ui/                              # EXISTING primitives — reused, not duplicated
│   ├── listing/ compliance/ approval/ brokerage/   # NEW
├── lib/
│   ├── supabase/                        # EXISTING server/client factories
│   ├── i18n/dictionaries/{en,tr}.json   # EXISTING — extended with all new keys
│   ├── api/                             # NEW — typed .NET API client + session propagation
│   └── validation/listing.ts            # NEW — Zod schemas
└── tests/{contract,integration}/        # EXISTING Vitest + Playwright — extended
```

**Structure Decision**: The repo's mandated two top-level directories are preserved.
`RealtorAI_BackEnd/` gains the new .NET solution (`RealtorAI.Api/`) and the Python sidecar
alongside the existing `supabase/` project, which keeps owning all DDL. `RealtorAI_FrontEnd/` is
extended in place — no restructuring of the existing Next.js app, and the existing auth,
Supabase client factories, i18n dictionaries, and UI primitives are reused rather than replaced.
The template's generic `src/models|services|api` layout was replaced by the modular-monolith
layout the feature actually needs, where each module owns its domain, persistence, and endpoints.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| **.NET 10 vs constitution's ".NET 8/9"** (Technology Stack) | .NET 10 is the current LTS and the sensible target for a greenfield API; pinning to an older minor would mean starting on a shorter support runway. Approved by the project owner on 2026-10-12. | Using .NET 9 to match the text literally was rejected — it solves a documentation problem by degrading the product. **Action**: amend the constitution's stack line to "C# .NET (current LTS)" via a PATCH bump so it stops pinning minors that age out. Until that amendment lands, this row is the documented exception. |
| **Python vision sidecar** adds a second language, service, and deployment unit (Principle V) | SigLIP2 preprocessing and OpenCV's blur/exposure/tilt metrics are mature and battle-tested in Python; re-deriving them in .NET risks subtly wrong quality thresholds feeding Story 2's flags. Plan-input default. | In-process **ONNX Runtime + Magick.NET** in .NET would remove the whole service and its HTTP contract, and Principle V favours it. (Originally written as ImageSharp; that library was later ruled out entirely — its license fails Release builds and it cannot decode HEIC, see [research R17](./research.md). The spike, if run, must use Magick.NET.) Not rejected outright — **recommended as a Phase 0 spike**: adopt it if its metrics match the Python reference on the labeled photo set, otherwise keep the sidecar. This is Slice A's largest complexity item. See [research R13](./research.md#r13-vision-sidecar-versus-in-process-net). |
| **`IMlsAdapter` seam — DEFERRED, not built** | Slice A has no caller for it. Writing an interface with zero implementors is both a Principle V YAGNI violation and arguably the Principle IV stub it forbids. | The plan input asked for it as a Slice B seam. Instead of C# with no implementor, the adapter contract is documented in [contracts/slice-b-seams.md](./contracts/slice-b-seams.md) so Slice B inherits the design intent at zero code cost. `MlsRuleProfile` **is** built — Slice A genuinely consumes it for Story 3 limits and Story 4 checks. |
| **Hangfire + SignalR** (two infrastructure dependencies) | FR-053 requires durable background analysis with visible progress and per-photo result streaming. Hangfire supplies retries/idempotency/scheduling/inspection on the Postgres already present (no broker added); SignalR supplies push for a 30-photo fan-out. | Hand-rolled queue table + polling was rejected: it rebuilds retry, backoff, scheduling, and observability by hand, and polling 30 photos is worse than one push connection. Both alternatives recorded in [research R12](./research.md#r12-background-jobs-and-real-time-progress). |
| **TanStack Query** in a Server-Component-first app (Section A) | The review screen has many independently-refreshing client panels over the .NET API plus SignalR-driven invalidation; hand-rolled fetch state across them would be worse. | Server Components alone can't express live per-photo streaming into an interactive, drag-reorderable grid. Scope is limited to the justified client leaves; route shells, data loading, and the audit view stay server-rendered. |
| **`IPropertyDataProvider` with one no-op implementation** | FR-008 explicitly specifies *both* behaviours — configured prefill and the unconfigured degraded path. Encoding the unconfigured path as a real implementation keeps the rule testable. | Hard-coding "no prefill" inline was rejected because FR-008's configured branch would then have nowhere to live when a provider is chosen. This is a deliberate, narrowly-scoped exception to YAGNI. |

## Clarification follow-ups (2026-10-13 session)

Five answers landed after Phase 1 was written. Their effect on this plan:

| Answer | Plan impact |
|---|---|
| **FR-061 scope confirmed** (pending agent may draft/analyze/generate/check/self-approve listing content) | No design change — this was already the plan's assumption. The plan input's "confirm FR-061 before implementation" caveat is now discharged; the authorization matrix can be written against it as settled. |
| **Platform administrator = `profiles.platform_admin` flag + admin endpoints** | `Modules.Brokerage` (or `Api`) owns the policy; the `/api/admin/ai/registry` endpoints are **retained** and gated on the flag. Migration `0004` adds the column with **no application-role write grant**. Authorization matrix gains platform-admin and non-admin rows for the admin endpoints. |
| **3-year original retention, purged out-of-band** | New privileged retention job — deliberately **not** a Hangfire application job, since Hangfire runs under the application identity which must never hold delete rights on the originals bucket. Runs as a separate ops task with its own credential. Migration `0011` keeps the write-once policy unchanged. |
| **Optimistic concurrency + approval-time hash check** | `listings`, `listing_fields`, `content_drafts` gain a `row_version`; all writes become conditional; API returns `409` with current server state. The approval endpoint now requires the reviewed `contentHash`. The SignalR channel already exists to push refreshes on conflict. |
| **Soft delete only** | `listings.deleted_at` + `archived` status; a `DELETE /api/listings/{id}` endpoint is added that soft-deletes. **The `ON DELETE CASCADE` from `listings` in migrations 0005–0008 must be reconsidered** — with no hard-delete path it is unreachable for listings, and keeping it would be misleading; retain cascade only where a parent row can genuinely be removed (e.g. `photos` → `photo_analyses`). |

## Slice B seams created in this plan

Listed explicitly so the next plan can build on them:

| Seam | Form in Slice A | Consumed by |
|---|---|---|
| `MlsRuleProfile` | **Built** — `mls_rule_profiles` table + `Libs.Mls` model. Already drives Story 3 remarks limits and Story 4 CRMLS checks. | Slice B MLS validation |
| `CanApproveMarketing` | **Built** — capability predicate in `Modules.Brokerage`, requires `approved` membership. Slice A uses it for the review screen's disabled-reason messaging and FR-061 enforcement. | Slice B marketing approval |
| `CanSubmitToMls` | **Built** — same predicate family, requires `approved` membership. | Slice B MLS submission |
| `IMlsAdapter` | **Deliberately not built** — documented in `contracts/slice-b-seams.md`. See Complexity Tracking. | Slice B CRMLS adapter |
| `listings.target_mls_id`, `photos.altered` / `disclosure_label` / `paired_original_id` | **Built** — data fields present and populated in Slice A. | Slice B submission payload |

## Phase 1 outputs

- [data-model.md](./data-model.md) — 19 tables, provenance and severity enumerations, the listing
  state machine, and an RLS policy per table.
- [contracts/](./contracts/) — OpenAPI for the API surface, each operation documenting its
  authorization rule; the sidecar contract; the Slice B seam document.
- [quickstart.md](./quickstart.md) — Supabase CLI + `dotnet run` + sidecar bring-up, DBeaver
  inspection values, seed data, and the Stories 1–5 validation walkthrough.
- [AGENTS.md](../../AGENTS.md) (repo root) — agent context: stack facts, the Next.js 16 reading
  requirement, and the non-negotiable AI/imagery rules.
