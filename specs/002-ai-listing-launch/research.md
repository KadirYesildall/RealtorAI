# Phase 0 Research: AI Listing Launch — Slice A

**Feature**: [002-ai-listing-launch](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-10-12

Scope: Stories 1–5 plus FR-020, FR-049–FR-063. Slice B/C research is recorded only where it
shapes a Slice A seam.

Each item below is stated as **Decision / Rationale / Alternatives considered**. Where a fact
could not be verified in this session, it is marked **⚠ UNVERIFIED** with the exact check
required — per Constitution Principle III, unverified externals are recorded explicitly rather
than guessed, and are gated so they cannot silently reach production.

---

## R0. Codebase reconnaissance (findings that override plan-input defaults)

The plan input described the existing system as "React + TypeScript" with a `profiles.brokerage_id`
column. Both are inaccurate. Actual state:

| Plan input said | Repository actually has | Consequence |
|---|---|---|
| "existing React + TypeScript app" | **Next.js 16.2.10** (App Router) + **React 19.2.4**, Tailwind v4 CSS-first, Zod v4 | Frontend work follows App Router + Server Component conventions, not plain SPA patterns. See R8. |
| "`profiles.brokerage_id` is replaced by…" + "migration moves existing values" | **No `brokerage_id` column exists**; `profiles` has `id, updated_at, full_name, company_name, preferred_language, subscription_tier, api_usage_count, accepted_terms_at`. Zero `brokerage` references repo-wide. | **Research item 6 is moot** — there is nothing to migrate. See R6. |
| Docker Compose Postgres + MinIO | Supabase CLI stack already provides Postgres (54322), Auth (54321), and Storage | Avoid a second Postgres and a second object store. See R9, R10. |
| `.NET 8/9` (constitution) | No .NET project exists at all; `RealtorAI_BackEnd/` holds only `supabase/` | The API solution is created from scratch in this feature; .NET 10 choice needs a constitution PATCH. See R11. |

Also confirmed present: `RealtorAI_FrontEnd/AGENTS.md` instructs that this Next.js version has
breaking changes versus model training data and that `node_modules/next/dist/docs/` must be read
before writing code. That instruction was followed for this plan (see R8) and must be followed
again during implementation.

---

## R1. AI model verification for Slice A

**Decision**: Seed the model registry with the candidate entries below, every one of them
`enabled = false`, and treat Hub verification plus an evaluation-harness pass as the gate that
flips `enabled = true`. Architecture does not depend on any specific model resolving.

| Registry task key | Candidate model | Serving | Fallback |
|---|---|---|---|
| `photo.analysis` | `Qwen/Qwen3-VL-8B-Instruct` | HF Inference Providers (OpenAI-compatible router) | `Qwen/Qwen2.5-VL-7B-Instruct` |
| `photo.embedding` | `google/siglip2-base-patch16-224` | Vision sidecar (local weights) | `openai/clip-vit-base-patch32` |
| `photo.quality` | none — deterministic CV | Vision sidecar | — |
| `listing.content` | `Qwen/Qwen3-30B-A3B-Instruct-2507` | HF Inference Providers | `mistralai/Mistral-Small-3.2-24B-Instruct-2506` |
| `compliance.judge` | same as `listing.content`, separate prompt, temperature 0 | HF Inference Providers | same fallback |
| image/video generation | **none — prohibited** (Principle VIII.6) | — | — |

**⚠ UNVERIFIED — must be checked before any entry is enabled:**

1. Model exists at that exact Hub id and is not deprecated/renamed.
2. License permits commercial use, and the actual license string is recorded in the registry row
   (Principle VIII.4.a). Apache-2.0 is expected for the Qwen family and MIT for BGE, but the
   license on the specific repo must be read, not assumed.
3. The model is actually served by HF Inference Providers — and specifically that the VLM accepts
   **image input** and that the chat endpoint supports a **JSON-schema / structured-output**
   response format. Provider routing coverage changes frequently and is the single most likely
   item to be wrong.
4. Measured p90 latency and cost for a 30-photo listing against SC-005 (< 2 min) and the
   per-listing budget.

**Rationale**: Principle VIII.4 already forbids enabling an unevaluated model, so a disabled-seed
registry is the correct resting state for a plan rather than a deficiency. Principle III forbids
guessing dependency facts. Writing plausible-looking verification results would be exactly the
failure mode both principles exist to prevent.

**Alternatives considered**: (a) Pick only models I can state with high confidence — rejected,
confidence about a Hub repo's current serving status is not verification either. (b) Block the
whole plan until verification completes — rejected, nothing in the architecture depends on the
answer; the `AiService` + registry seam makes model identity a configuration detail, which is the
entire point of Principle VIII.

**Follow-up**: this verification is a Phase-0-completion task that needs network access. It is
listed as a blocking prerequisite in [plan.md](./plan.md) under "Gated before AI enablement".

---

## R2. CRMLS rule profile for Slice A

**Decision**: Model MLS rules as a versioned **data** record (`mls_rule_profiles`), not code, and
ship the CRMLS profile with its numeric limits unset-but-required so the compliance engine fails
closed: a rule whose value is absent raises a blocking "rule profile incomplete" finding rather
than silently passing.

Fields the profile must carry for Slice A:

- `public_remarks_max_chars`, `private_remarks_max_chars`
- `forbidden_in_public_remarks`: phone numbers, email addresses, URLs, agent names, brokerage
  names (detector patterns are code; the on/off and severity are data)
- `required_fields[]` (RESO field keys that must be present and confirmed before approval)
- `photo_min_width`, `photo_min_height`, `photo_max_count`, `photo_min_count`
- `allowed_alteration_types[]` and `disclosure_labels[]` (see R4)
- `enumerated_field_lookups` → RESO lookup mapping (see R3)

**⚠ UNVERIFIED — the actual CRMLS values.** The character limits, required-field list, photo
specs, the current altered-image rule text, and the exact approved disclosure label strings must
be taken from CRMLS's published rules and data dictionary. I did not have reliable current values
for these and deliberately did not invent them — a fabricated character limit would produce
content that silently fails MLS validation, and a fabricated disclosure label would produce a
compliance control that does not actually comply. The plan input referenced "Rule 11.5.x"; that
citation is also unverified and must be confirmed against the current rulebook rather than
repeated.

**Rationale**: Rules-as-data was already required by the spec's Assumptions ("MLS rule
configuration is data, not code"). Fail-closed on a missing value means the unverified state is
safe: Story 3 cannot generate to an invented limit and Story 4 cannot pass a listing it has not
actually checked.

**Alternatives considered**: Hard-code conservative defaults (e.g. assume 1,000 chars) — rejected:
a wrong-but-plausible limit is worse than an obviously missing one, because it produces output
that looks shippable and fails only at MLS submission in Slice B.

---

## R3. RESO Data Dictionary field and enumeration source

**Decision**: `Libs.Reso` carries the field keys and enumerations as generated/seeded reference
data with a recorded Data Dictionary version, plus a CRMLS lookup-mapping layer on top (MLSs
commonly restrict or extend standard lookups).

**⚠ UNVERIFIED**: the exact DD version to target and whether CRMLS publishes a machine-readable
metadata/lookup endpoint usable without vendor credentials. If it does not, the enumerations are
seeded by hand from the published data dictionary for only the fields Slice A actually drafts
(`PublicRemarks`, `PrivateRemarks`, `InteriorFeatures`, `Appliances`, `Flooring`,
`ParkingFeatures`, `PoolFeatures`, `View`) rather than the whole dictionary.

**Rationale**: Story 3's acceptance scenario 1 requires enumerated fields to contain only
permitted values, which requires a trusted value list. Scoping to the eight fields Slice A drafts
keeps the seed small and reviewable (YAGNI — Principle V).

---

## R4. AB 723 and CRMLS photo-integrity requirements (FR-020)

**Decision**: Implement the altered-photo workflow as specified — `altered` flag, a disclosure
label chosen from the rule profile's allowed list, the unaltered original stored and placed
adjacent in display order, and a **blocking** finding when a declared alteration type is outside
the allowed list. The system itself never alters pixels (Principle VIII.6), so this path exists
solely to handle photos the agent altered elsewhere.

**⚠ UNVERIFIED and requiring non-engineering review**: the precise statutory requirements of
California AB 723 — exact label wording, where the label must appear (on the image, in the photo
description field, or in remarks), how long the unaltered original must remain available, and
whether "adjacent placement" satisfies the statute or merely CRMLS rules. AB 723 carries
**criminal** liability per the spec's own framing, which puts its interpretation outside what an
implementation plan should settle.

**Recommendation**: have counsel or CRMLS compliance staff confirm the label text and placement
before Story 4's photo-integrity check is considered done. The engineering shape (flag + label
from a configurable list + paired original + blocking rule) is stable regardless of how that
review resolves; only the label strings and placement target are affected, and both are data.

**Alternatives considered**: Infer requirements from the statute's summary — rejected, the
spec and constitution both treat this as a legal control, and VIII.6/VIII.7 are explicitly not
eligible for exception.

---

## R5. Public-record prefill provider (FR-008)

**Decision**: Defer provider selection. Define `IPropertyDataProvider` with a single method
returning a provenance-tagged field set, and ship exactly one implementation:
`NullPropertyDataProvider`, which returns nothing. With no provider configured, square footage,
lot size, year built, and tax fields stay empty, are marked required before approval, and are
never AI-inferred (FR-007).

**Rationale**: FR-008 already specifies the degraded behaviour, so Slice A is fully functional
without a vendor. Provider choice is a commercial decision (coverage, licensing, per-call price)
with no architectural impact behind the interface.

**Alternatives considered**: Select a California data vendor now — rejected as premature; it adds
contract and cost commitments to a slice that does not need them. This is the one place where a
single-implementation interface is justified against Principle V's YAGNI rule, because FR-008
explicitly requires both the configured and unconfigured behaviours to exist.

---

## R6. `profiles.brokerage_id` → `brokerage_memberships` migration

**Decision**: **No data migration is required.** `profiles.brokerage_id` does not exist and never
has; `0001_profiles.sql` is the only migration in the repository and defines no brokerage column.
There are zero `brokerage` references anywhere in `RealtorAI_BackEnd/` or `RealtorAI_FrontEnd/`.

The schema work is therefore purely additive:

- `ALTER TABLE profiles ADD COLUMN license_number`, `license_state`, `profile_completed_at`
- `CREATE TABLE brokerages`, `brokerage_memberships`, `brokerage_settings`

Existing users (if any) get no membership row until they self-declare a brokerage, which is the
correct resting state: no membership means no brokerage-scoped access in either direction
(FR-060), so there is no backfill ambiguity to resolve.

**Rationale**: Verified directly against the repository rather than inherited from the plan input.

---

## R7. Validating existing Supabase auth tokens in the .NET API

**Decision**: Validate Supabase-issued JWTs in ASP.NET Core using **asymmetric signing keys via
JWKS**, not the legacy shared HS256 secret.

- Enable asymmetric signing keys in the Supabase project (locally: the `signing_keys_path` key
  under `[auth]` in `config.toml`, currently commented out).
- Configure JWT bearer authentication against the Supabase JWKS endpoint
  (`{SUPABASE_URL}/auth/v1/.well-known/jwks.json`), validating issuer, audience, lifetime and
  signature.
- Map the `sub` claim to `profiles.id`. Nothing beyond identity is taken from the token.

**Critical security consequence — authorization state must not come from the JWT.** `jwt_expiry`
is 3600s, so a token can outlive a membership change by up to an hour. Brokerage membership
status and role are therefore loaded **from the database per request** (with a short in-memory
cache, invalidated on membership decisions), never read from token claims. A revoked or
rejected membership must take effect immediately, not on next token refresh.

**⚠ UNVERIFIED**: the exact `config.toml` key name/shape for enabling asymmetric keys on this CLI
version, and the precise JWKS path, must be confirmed against the installed Supabase CLI's own
output and docs before implementation (Principle III). The commented line in `config.toml` shows
`signing_keys_path` exists as a concept here; the generation command and key format need
checking.

**Alternatives considered**: (a) Legacy shared HS256 secret — simpler to wire, rejected because it
copies a signing-capable secret into a second service, and a leak there lets the API mint tokens
for any user. (b) Call Supabase `/auth/v1/user` per request to validate — rejected: a network
round trip on every API call, and it still would not carry membership state.

---

## R8. Next.js 16 / React 19 conventions (binding on all frontend work)

**Decision**: Follow the conventions below, read from the installed
`node_modules/next/dist/docs/01-app/02-guides/upgrading/version-16.md` as `AGENTS.md` requires.
These are breaking changes against older App Router patterns and several would silently produce
wrong code:

- **`proxy.ts`, not `middleware.ts`** — already correct in this repo; the exported function should
  be named `proxy`. The `edge` runtime is not supported in `proxy`; its runtime is `nodejs` and is
  not configurable.
- **Async request APIs are mandatory.** `cookies()`, `headers()`, `draftMode()`, and `params` /
  `searchParams` in pages, layouts, and route handlers are Promise-based; synchronous access was
  removed in 16. The existing `lib/supabase/server.ts` already awaits `cookies()`.
- **`revalidateTag(tag)` now requires a second `cacheLife` argument**; the single-argument form is
  a TypeScript error. For read-your-writes after a mutation (autosave, field confirmation,
  approval) prefer **`updateTag(tag)`**, which expires and refreshes within the same request, and
  **`refresh()`** to refresh the client router from a Server Action.
- **`cacheLife` / `cacheTag` are stable** — drop any `unstable_` prefix.
- **Turbopack is the default** for `next dev` and `next build`; `next dev` now writes to
  `.next/dev`, and dev/build can run concurrently.
- **`next lint` is removed** — the repo already calls `eslint` directly.
- **Typed route helpers**: `npx next typegen` generates `PageProps<'/route'>`, `LayoutProps`, and
  `RouteContext` helpers; use them for the new dynamic listing routes.
- PPR is opt-in via top-level `cacheComponents`; `experimental_ppr` and `experimental.dynamicIO` /
  `experimental.useCache` are gone.

**Client-vs-Server split for Slice A**: the constitution makes Server Components the default and
requires Client Components to be justified and pushed to leaves. The review screen genuinely
qualifies — drag-and-drop ordering, inline editing, and live per-photo pipeline updates are all
browser-interactive. The justified client leaves are: photo grid/reorder, inline field editors,
tone switcher, compliance panel interactions, and the pipeline progress subscriber. Route shells,
data loading, and the audit view stay server-rendered.

**New frontend dependencies** (none currently installed): TanStack Query, React Hook Form, and
dnd-kit are all additions. TanStack Query is justified by the streaming pipeline and the
review screen's many independently-refreshing panels talking to the .NET API; React Hook Form +
the already-present Zod v4 covers the wizard forms; dnd-kit covers FR-011 reordering.
**⚠ UNVERIFIED**: React 19.2 / Next 16 peer-dependency compatibility for each of the three must be
confirmed at install time rather than assumed.

---

## R9. Local development topology — Supabase CLI vs Docker Compose

**Decision**: Keep the **Supabase CLI stack as the single source of Postgres, Auth, and Storage**,
and use Docker Compose only for the genuinely new services: the .NET API and the vision sidecar.
Compose services connect to the CLI-managed Postgres on the host.

The plan input's Compose topology (its own Postgres + pgvector, plus MinIO) is **not** adopted
as-is, for two reasons:

1. **Constitutional.** The local-first development rule requires schema work to be developed and
   verified against the local Supabase stack (`supabase start`), with the connection values taken
   from actual CLI output. A second, Compose-owned Postgres would make the Supabase-managed
   database and the API's database two different places, and would break the DBeaver inspection
   workflow the constitution mandates.
2. **Duplication.** Two Postgres instances on one machine invites the classic failure where
   migrations land in one and the API reads the other.

Confirmed local ports from `config.toml`: API gateway **54321**, Postgres **54322**, shadow 54320,
pooler 54329, Studio **54323**, Inbucket (mail) **54324**, analytics 54327.

**pgvector**: enabled via `create extension if not exists vector` in a migration — Supabase ships
the extension, so no image change is needed. **⚠ UNVERIFIED**: confirm the extension is available
in the CLI's bundled Postgres image version before relying on it.

**Alternatives considered**: Full Compose topology including Postgres — rejected per above. Running
the API outside Compose entirely (`dotnet run`) — viable and simpler for day-to-day work; the plan
supports both and `quickstart.md` documents the `dotnet run` path as primary with Compose for the
sidecar.

---

## R10. Object storage — Supabase Storage vs MinIO

**Decision**: Use **Supabase Storage** for originals, normalized variants, and brokerage assets.
Do not introduce MinIO.

**Rationale**: The spec already specifies Supabase storage buckets with signed-URL access
(`listing-photos-original` write-once, `listing-photos-normalized`, `brokerage-assets`,
`seller-consents`). Supabase Storage is already in the stack, already part of the local CLI
environment, already integrated with the auth model the frontend uses, and already provides signed
URLs — which is exactly what the VLM call needs for short-lived image access. Adding MinIO means a
second object store, a second credential set, a second bucket-policy model, and an S3 client in a
project that otherwise has none; the constitution treats replacing Supabase as an amendment-level
change, and adding a parallel store is a de facto partial replacement.

**Immutability of originals (FR-001 / Principle VIII.6)** is enforced by granting no UPDATE or
DELETE policy on the originals bucket for any application role, plus a stored checksum verified on
read. The plan input's "versioning + immutable prefix" intent is preserved; only the backing
service differs.

**Alternatives considered**: MinIO for S3 API parity and bucket versioning — rejected for Slice A
as unjustified complexity (Principle V); revisit only if a concrete Supabase Storage limitation
appears (e.g. object-size or throughput ceilings measured during R1 latency work).

---

## R11. .NET version versus the constitution

**Finding**: The constitution's Technology Stack says "**C# .NET 8/9 Web API**". The plan input
specifies **.NET 10**. These do not match.

**Decision**: Proceed with .NET 10 (current LTS, and the sensible choice for a greenfield API) and
record this as a **constitutional deviation requiring a PATCH amendment** to the stack line — e.g.
"C# .NET (current LTS)" so the constitution stops pinning minor versions that age out. Logged in
[plan.md](./plan.md) Complexity Tracking.

**Rationale**: The constitution's intent is clearly "a .NET Web API, not something else", and the
amendment procedure exists precisely so stack text can be corrected. Silently shipping .NET 10
against a document that says 8/9 would leave the next reviewer unable to tell whether it was a
decision or a mistake.

---

## R12. Background jobs and real-time progress

**Decision**: Hangfire with Postgres storage for the pipeline, with every job body behind an
`IPipelineStep` interface so the orchestrator is transport-agnostic; SignalR for per-photo progress
push.

**Rationale**: Hangfire gives retries, idempotency keys, scheduling, and an inspection dashboard
without adding a broker — Postgres is already there. FR-053 requires visible progress with results
streaming per photo as they finish, which is a push problem; SignalR is the native ASP.NET Core
answer and avoids a polling loop over 30 photos.

**⚠ UNVERIFIED**: Hangfire's Postgres storage package and its compatibility with .NET 10 and with
Supabase's pooled connections must be confirmed at install time. Hangfire creates its own schema —
it must be pointed at a dedicated schema (e.g. `hangfire`) so it never collides with Supabase
migrations or the Data API's exposed `public` schema.

**Alternatives considered**: (a) .NET `BackgroundService` + a hand-rolled queue table — fewer
dependencies, but retries/scheduling/observability get rebuilt by hand. (b) Server-Sent Events
instead of SignalR — simpler, but SignalR's reconnection and group semantics are worth the
dependency for a 30-photo fan-out. Both recorded; neither preferred.

---

## R13. Vision sidecar versus in-process .NET

**Decision**: Adopt the Python FastAPI sidecar as specified (SigLIP embeddings + OpenCV technical
metrics), called only by the API, never by the browser.

**Recorded alternative with a recommendation**: an in-process .NET path —
**ONNX Runtime** for SigLIP inference plus **ImageSharp** for blur/exposure/tilt/resolution
metrics — would remove a language, a service, a deployment unit, an HTTP contract, and a failure
mode from Slice A. Principle V (YAGNI, and "complexity MUST be justified against a real, current
requirement") favours it. The sidecar's justification is ecosystem maturity: SigLIP2 preprocessing
and OpenCV's metrics are battle-tested in Python and would be re-derived in .NET.

This is left as the plan-input default rather than overridden, but it is the single largest
complexity item in Slice A and is flagged in plan.md Complexity Tracking. Recommended resolution:
spike the ONNX + ImageSharp path during Phase 0 completion; adopt it if the quality metrics match
the Python reference on the labeled photo set, otherwise keep the sidecar.

**Either way**: technical quality detection is deterministic and model-free (Laplacian variance,
histogram, EXIF/dimension checks), so it is not subject to the VIII.4 model gates.

---

## R14. Data access — EF Core alongside Supabase migrations

**Decision**: **Supabase SQL migrations own all DDL.** EF Core is used for querying and mapping
only, with its own migrations disabled; the model is kept in sync with the hand-written SQL and
verified by an integration test that fails if the EF model and the actual schema diverge.

**Rationale**: Two migration systems writing the same database is a genuine hazard — the
constitution's local-first rule and feature 001 both establish Supabase CLI migrations as the
schema source of truth, and the RLS policies, grants, and `SECURITY DEFINER` triggers in
`0001_profiles.sql` are things EF Core migrations model poorly or not at all.

**Alternatives considered**: (a) EF Core migrations as source of truth — rejected, it would strand
the existing RLS/grant/trigger definitions and break the DBeaver verification workflow. (b) Dapper
instead of EF Core — attractive given DDL lives elsewhere and the query shapes are simple;
recorded as a reasonable substitute if EF Core's model-vs-SQL drift becomes a maintenance cost.

---

## R15. i18n and dark mode (gaps in the plan input)

**Finding**: The plan input's frontend section specifies WCAG 2.1 AA and mobile usability but is
silent on internationalization and dark mode. Both are constitutional requirements, not options,
so they are added to the plan rather than inferred away.

**Decision**:

- **i18n (Principle VI)**: every user-facing string in the new screens ships as a key in both
  `lib/i18n/dictionaries/en.json` and `tr.json`. No hardcoded text. A feature with an
  English-only string in the UI is not done.
- **Dark mode (Section E)**: every new screen, component, and state — including empty, queued,
  analyzing, per-photo failure, blocking-finding, and warning states — is correct in light and
  dark, built from the semantic `--color-*` tokens in `app/globals.css`. No `dark:` variants, no
  raw hex. WCAG AA contrast is verified in **both** themes.

**The one genuine tension, resolved**: compliance findings mix two kinds of text. UI chrome
(severity labels, panel headings, button text, empty states, "why is approve disabled" reasons) is
**localized EN+TR**. The finding's `reason` and `suggested_rewrite` are **English-only by design**,
because they are analysis *of* U.S.-English listing copy destined for a U.S. MLS — translating a
rewrite suggestion for English remarks into Turkish would produce an unusable suggestion. This is
consistent with the spec's existing assumption separating UI language from generated-content
language, and is recorded here so it is not later mistaken for an i18n violation.

---

## R16. Cost, caching, and budget enforcement (FR-055)

**Decision**: Record tokens in/out, image count, and computed cost per call in `ai_usage`;
aggregate per listing; enforce a configurable per-listing budget checked **before** dispatching a
call, failing with an explicit "budget exceeded — continue manually" state rather than a silent
stop. Cache by a hash of (task key, model id, prompt version, normalized input) so re-running
analysis on an unchanged photo set is free.

**⚠ UNVERIFIED**: provider per-token and per-image pricing, needed to turn token counts into
currency. Until R1 supplies it, the budget is enforced in **token/image units** rather than
currency, which is sufficient for the control and avoids hard-coding a wrong price.

---

## R17. VERIFIED during implementation (2026-10-13) — supersedes earlier UNVERIFIED notes

Resolved empirically against this machine and the running local stack, not from memory. Items
here are no longer open; where a finding contradicts an earlier section, this one wins.

### Toolchain (measured)

| Component | Verified value | Note |
|---|---|---|
| .NET SDK | **10.0.302** (also 8.0.423 present) | Plan assumed .NET 10 — confirmed available |
| Node | **24.18.0** | Exceeds Next.js 16's 20.9 minimum |
| Python | **3.13.14**, pip 26.1.2 | Plan said 3.12; actual is **3.13** (Windows Store build). torch/OpenCV wheel availability for 3.13 is the remaining sidecar risk |
| Postgres | **17** (Supabase CLI bundled) | |
| pgvector | **0.8.2 available and installed** | Resolves R9's open item — no image change needed |
| Solution format | **`.slnx`** | .NET 10's `dotnet new sln` emits the XML format, not `.sln`. tasks.md T001 says `.sln`; the actual file is `RealtorAI.Api.slnx` |

### Supabase auth — R7's open item is CLOSED

Probed directly:

- OIDC discovery **is** served at `http://127.0.0.1:54321/auth/v1/.well-known/openid-configuration`
- `issuer`: `http://127.0.0.1:54321/auth/v1`
- `jwks_uri`: `http://127.0.0.1:54321/auth/v1/.well-known/jwks.json`
- The JWKS returns an **ES256 (EC P-256) signing key**

**This corrects R7.** Asymmetric signing keys are already the default in this CLI version —
the commented-out `signing_keys_path` in `config.toml` does **not** need to be enabled. The API
configures `Authority` and lets OIDC discovery find the JWKS, validating issuer, audience
(`authenticated`), lifetime, and signature. Only `sub` is trusted; membership state is still
loaded per request from the database.

### Package versions (resolved by NuGet, recorded per Principle III)

| Package | Version | Project |
|---|---|---|
| Serilog.AspNetCore | 10.0.0 | Api |
| OpenTelemetry.Extensions.Hosting | 1.19.1 | Api |
| OpenTelemetry.Instrumentation.AspNetCore | 1.19.0 | Api |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.19.1 | Api |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.12 | Api |
| Microsoft.EntityFrameworkCore | 10.0.12 | Api |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | Api |
| Pgvector.EntityFrameworkCore | 0.3.0 | Api |
| Microsoft.Extensions.AI | 10.10.0 | Libs.Ai |
| Microsoft.Extensions.AI.OpenAI | 10.10.1 | Libs.Ai |
| Microsoft.Extensions.Http.Resilience | 10.10.0 | Libs.Ai |
| JsonSchema.Net | 9.4.1 | Libs.Ai |
| **Hangfire.AspNetCore** | **1.8.25** | Pipeline |
| **Hangfire.PostgreSql** | **1.21.1** | Pipeline |
| **Magick.NET-Q8-AnyCPU** | **15.0.0** | Modules.Media, tests/unit |
| Supabase | 8.3.1 | Modules.Media |
| Newtonsoft.Json | 13.0.4 | Api, Pipeline (pinned — see below) |
| Microsoft.OpenApi | 2.12.2 | Api (pinned — see below) |
| Testcontainers.PostgreSql | 4.16.0 | tests/integration |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | tests/contract, tests/integration |

Hangfire's Postgres storage installing cleanly on .NET 10 closes that part of R12's open item.
Whether it coexists with Supabase's pooled connections is still unproven — it is pointed at a
dedicated `hangfire` schema, and that remains to be exercised under load.

### Two supply-chain findings, both fixed

The build was configured to escalate NuGet audit warnings (NU1901–NU1904) to **errors**, which
immediately surfaced two vulnerable transitive packages that would otherwise have shipped:

1. **`Microsoft.OpenApi` 2.0.0 — HIGH** (GHSA-v5pm-xwqc-g5wc), pulled in by the `webapi`
   template via `Microsoft.AspNetCore.OpenApi` 10.0.10. Pinned to **2.12.2** (same major, so
   no API break). Verified clean.
2. **`Newtonsoft.Json` 11.0.1 — HIGH** (GHSA-5crp-9r3c-p9vr), pulled in by
   `Hangfire.Core` 1.8.25. Pinned to **13.0.4**. Verified clean.

Final scan: no vulnerable packages in any of the 15 projects. The gate stays in place, so the
next one fails the build rather than warning.

### Imaging library CHANGED: ImageSharp → Magick.NET

**This supersedes the plan's and tasks.md's reference to `SixLabors.ImageSharp`** (T058, T093).
ImageSharp was installed first, then removed for two independent reasons:

1. **Licensing — a hard Release-build blocker.** ImageSharp 4.1.3 is under the Six Labors Split
   License: Apache-2.0 only for a for-profit *direct* dependency with **under $1M USD annual
   gross revenue**, otherwise a purchased commercial license. Worse than a future obligation,
   its MSBuild targets emit: *"No Six Labors license found. This Debug build can continue, but
   **Release builds require a valid license**."* For a paid B2B SaaS that is a production
   blocker, not a licensing footnote.
2. **No HEIC decoder.** FR-003 requires accepting HEIC, which is the **default iPhone format** —
   i.e. the single most likely real-world upload. ImageSharp has no native HEIC support.

**Magick.NET-Q8-AnyCPU 15.0.0** replaces it: license expression **Apache-2.0** (bundling
ImageMagick 7.1.2-33), no license key, no revenue threshold, no Release gate. HEIC/HEIF read
support was then **verified by a passing test** (`tests/unit/ImagingFormatSupportTests.cs`,
8/8 passing) rather than assumed, and that test now pins the capability so a future package
swap cannot silently drop it.

Both Debug and Release builds are clean: 0 warnings, 0 errors.

## R18. Privilege defect found and fixed during implementation (2026-10-13)

**Severity: high. Found by writing T026, not by review.** Migration `0012_privilege_hardening.sql`
corrects it.

### What was wrong

The Supabase CLI configures this on the `public` schema:

```sql
alter default privileges in schema public grant all on tables to anon, authenticated, service_role;
```

So every table created by migrations 0003-0011 received **SELECT, INSERT, UPDATE, DELETE,
TRUNCATE, REFERENCES and TRIGGER** for `anon` and `authenticated` at creation time, regardless
of the narrower `grant` statements those migrations wrote. Writing no `grant` for a table did
not mean the table had no grant. 0006_media.sql's comment "photo_embeddings: intentionally no
grant to authenticated" was false from the moment the table was created; that comment has been
corrected in place rather than deleted.

### Why it mattered

**TRUNCATE is not subject to row-level security.** RLS constrains SELECT, INSERT, UPDATE and
DELETE; it has no effect on TRUNCATE, which requires only the privilege. The append-only trigger
from 0009 fires `before update or delete`, which does not cover TRUNCATE either.

Reproduced before writing the fix, as `authenticated` with `auth.uid()` set to Agent C — an
approved agent at an unrelated brokerage who owns no listings:

```
select count(*) from public.listings;   -- 0  (RLS correctly hid every row)
truncate public.listings, public.audit_events cascade;
-- TRUNCATE TABLE
-- cascaded to: listing_fields, photos, content_drafts, compliance_findings,
--              approvals, ai_usage, photo_analyses, photo_embeddings
```

Any signed-in agent could destroy every listing and the **entire audit trail** for every
brokerage, while RLS behaved exactly as designed. This defeated FR-049 (immutable audit) and
FR-065 (no hard delete) simultaneously.

Not an active breach in the deployed shape: PostgREST exposes no TRUNCATE, so no known HTTP
request reached it. That is a property of the client in front of the database, not of the
database, and Principle VII requires the privilege not to exist. The audit-immutability claim
was simply not true as written.

### The fix

0012 revokes everything from `anon` and `authenticated` on all 16 feature tables, then grants
back only the commands each table's policies are written for. TRUNCATE, REFERENCES and TRIGGER
are granted to **nobody**: TRUNCATE because it ignores RLS, REFERENCES because a foreign key
aimed at a table can probe for rows RLS is hiding, TRIGGER because client-authored trigger code
would run on every write.

Two changes go beyond restoring the intended state:

- **`listings` no longer grants DELETE to `authenticated`.** FR-065's soft-delete-only rule
  previously rested on `audit_events.listing_id` being a NO ACTION foreign key, which only bites
  once an audit row exists — a brand-new listing was hard-deletable. Withholding the privilege
  makes it unconditional.
- **`before truncate` guards on `audit_events` and `listings`**, so the rule also holds for the
  API's own privileged connection and survives a future migration re-granting by accident.

`alter default privileges ... revoke all on tables from anon, authenticated` stops new tables
inheriting the same grant. Only the migration role's defaults are changed; Supabase's internal
defaults (owned by `supabase_admin`) are left alone, since they are not ours to change and
hosted projects do not permit it.

### Verified after the fix

| Check | Result |
|---|---|
| TRUNCATE as unrelated agent | `permission denied for table listings` |
| TRUNCATE `audit_events` as **superuser** | refused by trigger |
| TRUNCATE `listings cascade` as **superuser** | refused by trigger |
| DELETE own listing as owner | `permission denied` — soft delete is now the only path |
| Owner SELECT / UPDATE / INSERT own listing | still work (not over-locked) |
| `anon` privileges on the 16 tables | zero |
| `photo_embeddings` privileges for client roles | zero |

`tests/integration/PrivilegeHardeningTests.cs` pins all of it, including the original
reproduction and the schema-default revoke, so the defect cannot return silently.

### Lesson recorded

Writing no `GRANT` is not the same as granting nothing. On a platform that sets default
privileges, access control has to be asserted by test rather than inferred from the absence of a
statement. Every table-level privilege claim in this feature is now covered by an assertion.

## R19. Frontend supply-chain state (2026-10-13)

Checked while adding the Slice A frontend dependencies (T011). `npm audit` reported **16
vulnerabilities (1 critical, 12 high, 3 moderate)** — all pre-existing from feature 001, none
introduced by the new packages.

The critical one was directly relevant: **`next` 16.2.10, GHSA "Middleware / Proxy bypass in App
Router applications using Turbopack and single locale"**, affecting `9.3.4-canary.0 - 16.3.7`.
This project is App Router on Turbopack and guards the dashboard with `proxy.ts`, so the advisory
describes a bypass of the exact mechanism feature 001's route guard depends on.

Resolved by upgrading to **next 16.3.8** — the minimum patched version, chosen over the latest
16.4.0 deliberately: this is a security patch and bundling a minor-version jump into it would mix
an unreviewed behavioural change into a fix that needed to land. `eslint-config-next` was moved
to 16.3.8 to match, and both are pinned exactly (no caret), as feature 001 pinned Next, because
the App Router API surface is version-sensitive. 16.3.8 also brought patched `postcss` and
`sharp` transitively.

`vitest` went to 4.1.11 (patch) for GHSA path-traversal in `@vitest/mocker`; `npm audit fix`
cleared `brace-expansion`, `browserslist`, `js-yaml`, `nanoid` and `source-map-js`.

**Final state: production dependencies 0 vulnerabilities.** Five high advisories remain, all in
one dev-only chain: `eslint-config-next -> @next/eslint-plugin-next -> fast-glob -> micromatch ->
braces`. They are **not fixable** — the advisory covers `@next/eslint-plugin-next >= 14.3.0`, so
every 16.x release is affected, and npm's only proposed remedy is downgrading
`eslint-config-next` to 14.2.35, which is two majors back and incompatible with Next 16. The
vulnerability is CPU exhaustion from deeply nested glob patterns, and the only glob patterns
involved come from our own ESLint config, not from untrusted input. Accepted and recorded rather
than papered over.

`npm run audit:prod` was added as the gate (fails on any production-dependency advisory at
`low` or above), giving the frontend the same posture as the backend's NU1901-NU1904-as-errors
rule, without the dev-only chain making the gate permanently red.

## Open items carried to Slice B (recorded, not resolved)

- **CRMLS API surface**: whether RESO Web API Add/Edit is exposed to vendors or only a proprietary
  listing-input API; the OAuth/delegated-authorization model; sandbox availability; vendor
  approval requirements, timeline, and fees. Affects Slice B only. Slice A deliberately creates no
  MLS adapter implementation (see plan.md Complexity Tracking for the `IMlsAdapter` decision).
- Clear Cooperation timing, seller marketing choice capture, social platform APIs, and the
  marketing pack — all untouched by Slice A beyond the capability predicates `CanApproveMarketing`
  and `CanSubmitToMls`, which exist in Slice A to enforce FR-061.
