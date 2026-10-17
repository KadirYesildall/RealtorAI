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

## R20. Module/persistence boundary: one enum definition, not two (2026-10-13)

Hit while implementing T035-T037. The plan puts the EF model in the host (`Api/Data`) and the
domain logic in modules, and the host references the modules — so a module cannot reference
`AppDbContext` without a project cycle. But `MembershipStatus`, `MembershipRole` and the
18-value `AuditAction` list are needed on **both** sides.

First attempt duplicated the enums per side and translated between them in the adapter. That
worked and built clean, then immediately produced `CS0104: 'MembershipRole' is an ambiguous
reference` — which was the warning sign, not the problem. The real problem was that two copies
of an authorization-relevant enum drift, and the only defence would have been a totality test
per enum.

**Resolution:** the enum is declared once, in the module that owns the concept
(`Modules.Brokerage`, `Modules.Audit`), and the *persistence* layer references it. Those modules
take no database dependency — the PostgreSQL labels live in `Api/Data/PostgresEnumMappings.cs`,
applied through a dictionary-backed `INpgsqlNameTranslator`, because `[PgName]` would require
pulling Npgsql into a domain module.

Enums whose values are purely a storage concern (`tone`, `provenance`, `room_type`, the finding
and content enums) stay in `Api/Data/DbEnums.cs` with `[PgName]`. Both kinds are listed in one
mapping table, which `SchemaDriftTests` reads — so the test compares the database against the
labels the application will actually send, rather than against a second list that could be wrong
in the same way. `EveryMappedEnumMemberHasALabel` additionally fails if a member has no label,
which would otherwise throw at runtime on whichever write first carried that value.

Net effect: 24 duplicated enum members and their translation code removed, and the drift class
closed by construction rather than by vigilance.

## R21. EF silently overrides database column defaults (2026-10-13)

**Found by a failing test, and it was a real bug in shipped code, not in the test.**

`OptimisticConcurrencyTests` asserted that a freshly created listing is at `row_version = 1`,
matching the SQL default in `0005_listings.sql`. It came back `Conflict`: the row was at 0.

Cause: EF Core includes every mapped property in an INSERT rather than omitting those left at
their CLR default, so `int RowVersion` (CLR default 0) was written as 0 and the database default
of 1 never applied. Every listing, listing field and content draft created through the context
therefore started one version behind what the schema declared, and the first conditional update
against the schema's value would fail with a version conflict against a version that had never
existed (FR-066).

A quieter instance of the same fault sat on `brokerage_settings`: `ai_budget_tokens` defaults to
200000 and `ai_budget_images` to 120 in SQL, both 0 in CLR. A brokerage created through the
context would have had a **zero AI budget**, so the FR-055 pre-dispatch check would have refused
every AI call — the feature appearing broken for exactly the brokerages nobody had configured by
hand, with no error pointing at the cause.

Fixed by giving the six affected properties initializers matching their SQL defaults. More
importantly, `tests/integration/ColumnDefaultsTests.cs` now reads **every** defaulted column
from the live schema and compares it against a freshly constructed entity, so a future migration
that adds or changes a default fails unless the entity is updated with it. Unparseable default
expressions are reported rather than skipped, since silently ignoring a shape the test does not
understand is how the next instance would slip through.

Related, same session: constructing fresh `DbContextOptions` per context made EF build a new
internal service provider each time, and past twenty of them EF escalates
`ManyServiceProvidersCreatedWarning` to an exception. The integration tests now share one options
instance. That is the correct fix rather than suppressing the warning — the same pattern in
production would leak a provider per request, which is what the warning exists to catch.

## R22. The endpoint-authorization gate was verified by breaking it (2026-10-13)

T038 required "a test that fails the build if any endpoint lacks an explicit policy". The
failure mode it guards is specific: adding a route without `RequireAuthorization()` produces a
working anonymous endpoint, with no compiler error, no runtime error, no failing test, and
nothing visible in review except a line that is not there.

`EveryEndpointHasPolicyTests` enumerates `EndpointDataSource` — the routing table the server
will actually serve, not a maintained list — and requires each endpoint to be covered by a
policy or named in `PolicyRegistry.DeliberatelyAnonymousEndpoints` with a written reason.

**A gate that has never been observed to fail is not a verified gate**, so it was tested against
a deliberate violation: a temporary unauthenticated `/leaky` endpoint was added, the suite went
red on exactly that assertion, and removing it restored green. The exemption list is itself
guarded — a stale entry fails (it would be a standing permission for a future endpoint reusing
the name), and a reason shorter than 40 characters fails.

No `FallbackPolicy` is configured, deliberately. A fallback would make every endpoint implicitly
authenticated, which sounds safer but is weaker: it hides missing declarations behind a default,
and the coverage test could no longer distinguish a decision from an omission.

## R23. A prohibited image-generation dependency was already in the solution (2026-10-13)

**Constitution VIII.6 violation, found while implementing T053 — the test the principle itself
mandates.**

`Libs.Ai` referenced `Microsoft.Extensions.AI.OpenAI` 10.10.1, committed in the T001-T034
increment. That package depends on `OpenAI` 2.14.0, and inspecting its assembly documentation
confirmed it ships working clients for image generation, **image editing (inpainting)**, image
variations, and video. VIII.6 names virtual staging, object removal and inpainting, and
AI-generated video explicitly, and states that *any dependency capable of image or video
generation is prohibited in the codebase* — with no exception for "nothing calls it". Nothing
did call it; the capability was one `using` away.

**Removed, and replaced rather than worked around.** `Libs.Ai/OpenAiCompatibleChatClient.cs`
now speaks the OpenAI-compatible `/chat/completions` protocol directly over `HttpClient`. That
is a better fit for Principle VIII.1 as well as VIII.6: the base URL, model id and parameters
all arrive from the registry row, nothing vendor-specific appears anywhere, and the same client
reaches every provider serving that widely-implemented wire format — the Hugging Face router,
vLLM, Ollama, llama.cpp. `dotnet nuget why` confirms `OpenAI` is gone from all 15 projects.

**Where the line is drawn, and why.** `Microsoft.Extensions.AI.Abstractions` declares
`IImageGenerator`, and `Microsoft.Extensions.AI` adds middleware around it. Those packages
stay: an interface with no implementation cannot generate an image, and removing them would mean
abandoning the vendor-neutral `IChatClient` abstraction Principle VIII.2 requires. So the test
bans *capability* (the SDK) and, separately, any reference to those type names in our own source
— the capability only becomes real when something implements or invokes it.

**The gate is verified, not merely written.** The prohibited package was re-added, the suite went
red on exactly `NoProjectReferencesAGenerativeImageryPackage(packageId: "Microsoft.Extensions.AI.OpenAI")`,
and removing it restored green. It also immediately caught a self-inflicted case: the doc comment
in the replacement client originally spelled out the banned method names while explaining the
removal, and the scan flagged the comment. The prose was reworded rather than the scan weakened.

The test covers project files, `packages.lock.json` (the transitive route, which is how this
arrived), our own `.cs`/`.ts`/`.tsx`/`.py` sources, the sidecar's requirement files, and the
sidecar's exposed routes — which must stay within `/health`, `/quality`, `/embed`: measure and
embed, never produce. A guard test asserts the file walk actually reaches the codebase, so the
47 assertions cannot pass vacuously.

## R24. The model fallback chain was dead code (2026-10-13)

**Found by T052's unit tests, which is the only way it would have been found.**

`ModelRegistry.ResolveChainAsync` required each fallback entry to have `Enabled = true` before
following it. But `0008_ai_platform.sql` creates:

```sql
create unique index registry_one_enabled_per_task on public.ai_model_registry (task_key) where enabled;
```

so **at most one row per task can ever be enabled**. No second entry for the same task could
satisfy the check, which meant the entire `fallback_registry_id` chain was unreachable: provider
failover silently did nothing. Nothing would have surfaced this in production — the fallback
would simply never engage, and an outage would look like an outage.

The bug was asking the wrong question. `enabled` answers "is this the current choice for this
task"; what a fallback must satisfy is Principle VIII.4 — **license recorded and evaluation
passed**. Those are different properties, and conflating them is what produced the contradiction.

The fix required `eval_status` on the dispatch-time entry, which it did not carry. `ModelEvalStatus`
therefore moved from `Api/Data/DbEnums.cs` into `Libs.Ai`, following the R20 pattern: the enum is
declared once in the library that owns the concept, and its PostgreSQL labels are declared in
`Api.Data.PostgresEnumMappings`. Fallback resolution now stops at an entry with no license, at one
that failed evaluation, at one serving a different task, and on a cycle (the schema forbids
self-reference but not a longer loop).

## R25. Prompt-integrity startup gate, verified by breaking it (2026-10-13)

T046 requires a startup check that fails on prompt hash drift (VIII.5). Verified by running it
rather than by reading it:

| Scenario | Result |
|---|---|
| Prompt on disk, no version recorded | Host starts. Unregistered is not drift — a new version exists before anyone records it. |
| Recorded hash matches the file | Host starts, `/health` returns 200 |
| Recorded hash altered by one character | Host **refuses to start**, naming both the recorded and the computed hash |
| Version recorded but the file absent | Host refuses to start — audit records citing it can no longer be reproduced |

The fourth row was not a planned test. It fired on the first real boot because the prompts
directory resolved to `{ContentRoot}/prompts`, while `plan.md` places `prompts/` as a *sibling*
of `Api/` (shared with the evaluation harness) and `ContentRootPath` is the host project
directory under `dotnet run`. The check was correct: from where it looked, the file was genuinely
missing. Path resolution now probes the published location, then the development sibling, with an
`Ai:PromptsPath` override, and the csproj links the directory into published output.

Hashing normalises line endings to LF. Without that, the same untouched prompt hashes differently
depending on how git checked it out — `core.autocrlf` is active here — and startup would fail on
a developer machine while passing in CI.

The hash is now seeded (`supabase/seed.sql`), so the guarantee holds from a fresh `db reset`. The
maintenance consequence is stated there plainly: **editing `v1.md` stops the API booting** until
a v2 is added or the hash is updated. That is the intended cost — editing a released prompt
silently invalidates every audit record citing it.

## R26. The host failed every database request after the twentieth (2026-10-14)

**Severity: high — the API would have gone down about twenty requests after every start.**
Found by T051, the first suite to send many database-backed requests through one host.

`PostgresEnumMapping.Translator()` built a new `ExplicitEnumLabelTranslator` on every call.
`AddDbContext` rebuilds `DbContextOptions` per scope, meaning per request, and EF keys its
internal service provider on those options, comparing each enum mapping's translator **by
reference**. So every request looked like a new configuration. EF built a fresh internal service
provider each time, and past twenty it raised `ManyServiceProvidersCreatedWarning`, which this
project's configuration treats as an error. Request 20 onward returned 500.

Nothing caught it earlier because nothing had made more than a few database calls through one
host: the integration suites build their own contexts, and before T050 the only endpoints were
`/health` and `/whoami`, neither of which touches the database.

**Fix**: each mapping creates its translator once, eagerly, and returns that instance.
**Regression**: `tests/contract/SustainedDatabaseRequestsTests.cs` sends 30 requests and asserts
that the translator instance is stable. Both tests were confirmed failing with the fix reverted
("Request 20 of 30 returned 500") and passing with it.

**Lesson recorded**: an option object that EF compares should never be allocated per call. Any
future `MapEnum`, `UseVector`-style plugin, or interceptor added to `ConfigureNpgsql` must be a
singleton for the same reason.

## R27. Evaluation harness: two acceptance criteria the specification has not set (2026-10-14)

T054 builds the harness from quickstart §6. Its rule is that a pass must be earned on every
specified metric: an unmeasured metric, an unset threshold, or a task with no metrics yields
**Incomplete**, which is never written to the registry. Building it exposed two gaps that the
harness deliberately does **not** fill with invented numbers, because a threshold chosen by the
harness author would turn VIII.4's "passes the evaluation harness" into "passes whatever bar was
convenient":

1. **JSON-schema validity rate (FR-015)** — quickstart §6 says "report + threshold set here" and
   none has been set. It is required for `photo.analysis`, `listing.content` and
   `compliance.judge`, so **no chat model can pass until a product owner records a threshold**
   (in quickstart §6 and `MetricCatalog`).
2. **`photo.embedding` has no acceptance metric at all.** The specification defines none for the
   embedding model, so it cannot pass and near-duplicate detection (Story 2) cannot go live on a
   registry-enabled embedding model until one is defined — e.g. near-duplicate recall on the
   seeded living-room pair set.

Both are gates on *enablement*, not on the build, and both surface in `aieval --task all` output.

Design decisions recorded with it:

- The harness is a separate console tool (`tools/AiEval`), not part of the API host, and is the
  **only** writer of `eval_status`; the admin endpoint refuses to set it (T050).
- A recorded verdict requires `--actor`, a platform administrator, and is audited as
  `registry_changed` in the same transaction, through the same `AuditWriter` the API uses.
- The write matches on the evaluated provider/model/endpoint, so a model swapped mid-run is not
  stamped with another model's verdict.
- A pass does not enable; a failure of an enabled model disables it in the same change.

## R28. Hangfire on Supabase Postgres — R12's open items, closed by running a job (2026-10-14)

R12 left two things unverified: Hangfire.PostgreSql's compatibility with the stack, and its
coexistence with Supabase migrations. R17 closed only "it restores on .NET 10".

- **Driver gap, verified.** Hangfire.PostgreSql 1.21.1 targets `netstandard2.0` and is built
  against **Npgsql 6.0.11**; the host resolves **Npgsql 10.0.3**. A restore proves nothing about a
  binary dependency four majors apart, so `HangfireStorageTests` enqueues a real job and waits for
  the host's worker to execute it. It does.
- **Schema.** `0013_hangfire_schema.sql` creates `hangfire`, revokes it from
  `public`/`anon`/`authenticated`, and revokes default privileges on objects created in it later.
  Hangfire installs its own tables inside on first start (`PrepareSchemaIfNecessary`). Tested:
  its tables exist in `hangfire`, none in `public`, and neither client role holds USAGE on the
  schema or any privilege on its tables.
- **DDL ownership.** The constitution's "migrations own all DDL" is kept at the level that
  matters to this project — where Hangfire lives and who can reach it. Hangfire's internal tables
  are versioned by the package (`hangfire.schema`) and are not reproduced in a migration, where
  they would drift from the package version.
- **No dashboard.** It exposes job arguments (listing ids) under an authorization model separate
  from the API's token validation. Not mapped.
- **Still open:** behaviour through the Supabase **pooler** (port 54329, transaction mode).
  Hangfire uses session-level advisory locks, which transaction-mode pooling does not preserve;
  the local configuration connects directly on 54322. A production deployment must point
  `ConnectionStrings:Hangfire` at a direct or session-mode connection, not the transaction pooler.

## R29. CVE-2026-70159 in SignalR — mitigated in code; the runtime still needs patching (2026-10-14)

**Severity: high (CVSS 7.5, denial of service). Found by the NuGet audit gate when adding the
SignalR client for T056's tests.** GHSA-334f-vvp4-7g6p, published 2026-10-13: a client using
*stateful reconnect* can block broadcasts indefinitely through `MessageBuffer.ResendAsync`
backpressure and pin server resources. Affected: `Microsoft.AspNetCore.SignalR.Common` 10.0.0 –
10.0.12; patched in **10.0.13**.

The package gate caught the **client** package, which was then added at 10.0.13. The more
important exposure is the **server**: the hub runs on the ASP.NET Core shared framework, which
NuGet audit does not scan, and this machine has **Microsoft.AspNetCore.App 10.0.10** — affected.

**Mitigation in code.** Stateful reconnect is opt-in on the server. The hub sets
`AllowStatefulReconnects = false` explicitly, with the CVE cited, and
`PipelineHubTests.StatefulReconnect_IsDisabledOnTheHub` reads it back from the routing table the
server serves, so re-enabling it by any route fails the build. The feature was never needed: a
reconnecting client re-subscribes and re-reads status from the polling endpoint.

**Still required — outside the codebase:** install the **.NET 10.0.13** runtime/SDK on developer
machines and use a ≥ 10.0.13 base image for deployment. The code mitigation closes the known
vulnerable path; the patched runtime closes it in depth. Recorded here rather than done
automatically because it is a machine-level install.

**Lesson recorded:** NuGet audit covers packages, not the shared framework. A framework advisory
reaches a deployed ASP.NET Core app only through the runtime version, so runtime currency needs
its own check.

## R30. Write-once originals were only write-once for `authenticated` (2026-10-14)

**Severity: high — FR-001 and FR-065 held only as an application convention.** Found while
designing T057, before any code used storage.

0011 made `listing-photos-original` write-once with storage RLS: INSERT and SELECT policies, no
UPDATE or DELETE policy. That binds `authenticated` and nothing else — the service role bypasses
RLS, and a server-side storage wrapper uses the service key. Verified against the running stack
with that key before changing anything:

| Request (secret key) | Before 0014 | After 0014 |
|---|---|---|
| upload, `x-upsert: false` | 200 | 200 |
| upload over it, `x-upsert: true` | **200, replaced** | refused (`AccessDenied`) |
| `POST /object/move` | — | refused |
| `DELETE /object/...` | **200, deleted** | refused, bytes still served |
| bulk delete by prefix | — | refused |
| download / sign | 200 | 200 |

**Fix: `0014_originals_write_once.sql`** — `BEFORE UPDATE` and `BEFORE DELETE` row triggers on
`storage.objects`, scoped to the originals bucket, that raise for **every** role; plus a
statement-level `BEFORE TRUNCATE` guard (R18: row triggers do not fire on TRUNCATE, and
`service_role` holds TRUNCATE on `storage.objects`). The bytes were checked after a refused
delete rather than assuming Storage removes the row before the file; it does.
`StorageServiceTests` pins all of this, including SQL-level update/delete/truncate as superuser
with Supabase's own `protect_delete` guard switched off, so only 0014 can be what refuses.

**Consequences:**

- FR-065's retention purge (T154) is now the only path that could ever delete an original, and
  it does not exist yet; it must amend this guard for its own credential. Until then nothing can
  delete an original — the safe direction.
- Tests that write originals leave them behind in the local stack. `supabase db reset` clears
  them.

**Also in T057:** the unused `Supabase` 8.3.1 SDK package was removed from `Modules.Media`.
Storage is called over its REST API, every call of which was exercised against the running stack
first; the SDK would have added unverified signatures and a large transitive tree (realtime,
websockets) for five operations.

## R31. Three persistence defects surfaced by the brokerage endpoints (2026-10-14)

T061/T062 were the first endpoints to create several related rows and audit them in one request.
Three defects, all latent since earlier tasks, came out of it.

### 1. Every API-written timestamp defaulting to `now()` was stored as `-infinity` (high)

R21 fixed EF overriding database defaults for literals, and `ColumnDefaultsTests` deliberately
excluded `now()` defaults ("the application has nothing to agree with"). But the same mechanism
applies: with no default declared in the model, EF sends `default(DateTimeOffset)`, which Npgsql
writes as **`-infinity`**. Checked against the database: **31 of 43 audit rows had
`created_at = -infinity`** — every one the API wrote via `EfAuditSink` (T042 onward, first
exercised by T050). An audit trail that cannot say when anything happened defeats FR-049.

Fix: `AppDbContext.ConfigureDatabaseClockDefaults` declares `HasDefaultValueSql` on all 14
`now()`-defaulted columns. `ColumnDefaultsTests` gains two tests — every `now()` column in the
live schema must have a model default (failed listing all 14 before the fix), and an audit row
inserted through EF gets the database clock. The `-infinity` rows already in the local database
are immutable by design; `supabase db reset` clears them. No deployed environment existed.

### 2. FR-066's 409 on a genuine write race was a 500 (high)

`ConcurrencyGuard` caught `PostgresException` with SQLSTATE 40001, but that exception never
arrives bare: EF wraps it in `DbUpdateException` and Npgsql's default execution strategy wraps
that in `InvalidOperationException`. So two writers truly racing on one row got a 500, not the
version conflict FR-066 and SC-012 require. `TwoConcurrentWritersAtTheSameVersion_OnlyOneSucceeds`
existed for exactly this and passed only because its writers rarely overlapped; once the suite
grew heavier they did, every run. Fix: the guard walks the exception chain
(`IsSerializationFailure`), with unit tests over the real wrapping shapes — and confirming a
deadlock or constraint violation is *not* reported as a conflict.

### 3. EF cannot order inserts that reference each other (medium, open)

The EF model deliberately has no navigations (see `BrokerageEntities.cs`) — and so no foreign-key
metadata either, which means EF does not know an audit row citing a new brokerage must be
inserted after it. One `SaveChanges` batch ordered them wrongly and failed on the FK. T062 saves
the parent rows first, then the audit rows, inside one explicit transaction, so atomicity is
unchanged. **Open:** every later endpoint that creates a parent and its dependents in one
request (listing + fields + photos + audit, T075/T077) meets the same trap. The general fix is
to declare FK relationships **without navigations** (`HasOne<T>().WithMany().HasForeignKey(...)`,
`DeleteBehavior.NoAction` to mirror the SQL), plus a schema-drift test comparing them to the live
constraints. Recommended before US1's endpoints.

## R32. Brokerage endpoints: contract additions and one gap (2026-10-14)

- **Creating a brokerage needs its license number and state** (`brokerages` columns are NOT NULL
  and FR-021/FR-057 put them in disclosures), which `DeclareMembershipRequest` lacked.
  Added `brokerageLicenseNumber` and `brokerageState`, required only when creating.
- **Joining needs the brokerage id.** No endpoint lists brokerages (RLS lets only approved members
  read one), so the id is shared by its admin; the declared name must match it.
- **`Membership` gains the agent's name and license**, which an admin needs to decide a request.
- **First registrant** = no *live* members. A brokerage whose memberships have all ended can be
  claimed again rather than stranded with nobody able to approve anyone.
- **Gap: logo upload.** FR-057 lists the logo as configurable, but Slice A's contract has no upload
  endpoint. `logoStorageKey` is read-only in `PUT /settings`; accepting a free-form key would let
  a brokerage point its logo at any stored object. Needs a `brokerage-assets` upload endpoint.
- **Membership resolver bug fixed:** a rejected agent re-declaring the same brokerage leaves two
  rows there, and `GetMembershipAsync`'s `SingleOrDefault` threw — every request by that agent
  would have been a 500. It now returns the live row, else a historical one.

## R33. US1 listings: decisions made while building them (2026-10-16)

Each of these was a gap or an ambiguity in the documents; the choice and its reason are recorded so
a later story does not have to rediscover them.

- **`draft → ready_for_review` without analysis.** data-model.md draws review only through
  `analyzing`. With every registry entry disabled until its evaluation passes, there is no analysis
  to run during development or in an AI outage, so SC-010's hand-completion path needs this edge.
  Added to `ListingLifecycle`; every other edge is data-model.md's.
- **Empty required fields have no row, so `ListingDetail` gains `missingRequiredFieldKeys`.** FR-008
  says the public-record fields stay empty and are "visibly marked as required". Writing a
  placeholder row would claim a provenance for a value nobody supplied, and `listing_fields.value`
  is NOT NULL anyway. The required set is FR-008's four keys plus the MLS rule profile's
  `required_fields` (empty for the unverified CRMLS row). `requiredBeforeApproval` still marks a
  present field.
- **`If-Match: "0"` creates a field.** The contract requires If-Match on the field upsert, which has
  no version before the field exists. Versions start at 1, so 0 is free to mean "expect none"; a
  create that loses a race is answered 409 with the winner, never turned into a blind update.
- **Property type is a residential RESO `PropertySubType`.** The contract left it a free string;
  the seed already used `SingleFamilyResidence`. Restricted to ten residential lookup values in
  `Libs.Reso.ResoPropertySubType` because Slice A drafts residential sale listings for CRMLS. A
  listing type outside this set is a scope change, not a missing value.
- **Per-file upload limit = 50 MiB, from configuration.** The spec leaves it to planning; the
  originals bucket already enforces 50 MiB (0011), so `Storage:MaxUploadBytes` equals it — the API
  never accepts a file storage would then refuse. Ten files per request bound the body; past that
  bound the answer is 413, since the files can no longer be read one by one.
- **"Resumable" is per file.** Each upload request is independent and the wizard sends batches, so
  an interrupted upload resumes by sending the files that did not land. No chunked-upload
  protocol: the size bound makes one file per round trip cheap, and the write-once bucket would
  make a half-written original permanent.
- **The 100-photo bound is checked before anything is stored.** Every file is judged first (size,
  format from its bytes, exact duplicate by SHA-256), then the bound, then storage — under a row
  lock on the listing — so a refused batch leaves no orphaned original in a bucket that cannot
  delete one.
- **A listing is filed under the agent's live membership.** Pending or approved, per FR-061; a
  rejected or revoked membership is not live, so later work is not that brokerage's. `targetMlsId`
  comes from the brokerage's settings, never the request.
- **Profile completion requires license and state; the API does not require a brokerage.**
  `Capabilities.CanPrepareListing` (T036) already permits drafting with no brokerage, and the
  contract's authorization for `POST /api/listings` names only `profile_completed_at`. Story 1
  scenario 7's routing through brokerage declaration is the frontend's job: the wizard sends an
  agent with no license or no brokerage to `/dashboard/profile/complete` first.
- **No `listing_created` audit action.** FR-049 audits edits, and creation is not one; the row
  itself records who created it and when. Public-record pre-fill *is* audited, as `field_edit`
  per field. An autosave that changes nothing writes no audit event.
- **`PendingMembershipScopeTests` (T071) covers US1's operations.** Starting analysis under a
  pending membership is asserted in `PipelineStartTests` (T084), where that endpoint is specified.
- **`HangfireStorageTests` flaked once US1 added hosts.** `dotnet test` runs test assemblies in
  parallel processes, and every host runs workers on the shared queues; the probe job could be
  taken by a worker in a process where the probe type does not exist. The probe now uses a queue
  only its own host serves.

## R34. US2 photo analysis: decisions made while building it (2026-10-16)

- **Pipeline state is persisted (0015).** data-model.md had no home for step and per-photo
  progress, yet the contract makes `GET /pipeline` the record and SignalR a hint. Three tables:
  `pipeline_runs`, `pipeline_step_states`, `pipeline_photo_states`; API-only (RLS, no policy, no
  privilege). One unfinished run per listing makes `POST /pipeline` idempotent.
- **Overrides live on the photo.** data-model.md said an override is "recorded in audit, not
  overwritten" — true of the model's output, but the value the agent chose needs a column to be
  read back. `photos.room_type_override` and `feature_tags_override` win when set; the model's
  output stays in `photo_analyses`; the audit records both.
- **`photo.analysis` v2.** v1's content flags (`faces_visible`, `address_visible`, …) did not
  match the contract's `PhotoAnalysis.contentFlags` or FR-009's list, and v1 cannot be edited
  without invalidating its recorded hash. v2 aligns them; v1 stays on disk and recorded. v2's
  hash is recorded in seed.sql (active) and in the local database.
- **Images reach the model inline, normalized.** `AiRequest` gained `Images`; the OpenAI-compatible
  client sends them as `image_url` parts with a `data:` URL. A signed storage URL would be
  unreachable from a remote provider anyway, and inlining sends exactly the normalized copy
  (VIII.4.c). The response cache key includes each image's hash.
- **Resolution is judged on the original's dimensions.** The sidecar measures the normalized copy
  (≤1280px), so `below_min_resolution` is computed by the API from `photos.width/height` against
  the rule profile's minimums — unset for unverified CRMLS, so no flag until T160.
- **Cover and order are deterministic.** A walk-through order from the effective room types,
  exterior front first, cover the best clean exterior; reasons are codes (`rule:…`) the UI
  localizes. Suggestions only; nothing is reordered (FR-011).
- **Run status changes do not advance `listings.row_version`.** The version guards the agent's
  edits; advancing it for a background status change would turn the agent's next autosave into a
  false conflict.
- **Embedding audit.** `ai_call` requires a prompt version and hash (VIII.5); embeddings have no
  prompt, so they are recorded in `ai_usage` and not as `ai_call` events.
- **Near-duplicate threshold 0.97 is provisional** until the embedding model is evaluated (T156).
- **Two defects in Hangfire's host integration, found by the pipeline suites.** (1) Without an
  explicit `BackgroundJobServerOptions.Activator`, jobs are activated through Hangfire's static
  activator, which the last host in a process overwrites — a job could run in another host's
  container. Each server now activates from its own. (2) Test hosts exhausted the local server's
  100 connection slots (53300); tests now run one worker and bounded pools.
- **Photo deletion was in no task.** Story 2 scenario 7 requires it and the contract defines it;
  built with T086. Deleting the paired original of an altered photo is refused (409).
- **The hub is the one API path the browser calls.** CORS is configured for `/hubs` only
  (`Cors:PipelineHubOrigins`), without credentials; the token travels as `access_token`. The
  browser needs `NEXT_PUBLIC_API_BASE_URL` for it — a public URL, not a secret.
- **Three UI races found by the e2e suite, fixed in the grid:** "saved" shown while a later save
  was still queued (now counted); a router refresh started before a local change overwriting it
  (a refresh after every settled save supersedes it); a refresh replacing items mid-drag (held
  until the drag ends). And dnd-kit's counter-based accessibility ids mismatched across
  hydration; the context now takes a React id.
- **Streaming in the browser e2e.** No model can be enabled on the local stack until T159, so
  the Story 2 e2e walks the manual path; per-photo streaming is verified over a real hub
  connection in `PipelineStatusTests`.

## R35. US3 content generation: decisions made while building it (2026-10-17)

- **Regeneration is a pipeline run of one step.** `POST /content/regenerate {kind, tone?}`
  creates a `pipeline_runs` row with `content_kind` set (0016) and a single `content_generation`
  step row; the runner executes only the steps a run was queued with. Progress reads through
  `GET /pipeline` like any run, and the one-unfinished-run-per-listing index applies (409
  `pipeline_running`). A full analysis run ends with the same step for every kind.
- **Refused before queuing, each naming the manual path.** 503 `no_model` when no
  `listing.content` model is enabled; 409 `mls_rule_profile_incomplete` when the listing has no
  target MLS, its profile is unverified, or it has no limit for the kind (research R2 — CRMLS
  today); 409 `budget_exceeded`. Inside an analysis run the same conditions skip the step with
  the reason, and the photo steps are unaffected.
- **Limits.** Public and private remarks use the profile's verified limits. The headline is not
  an MLS field and has a fixed 100-character limit, but still needs a verified profile. The
  model is told the limit; every variant is then measured, and one over it is discarded. When
  none survives, the kind fails and the current drafts stay (FR-015).
- **Variants.** Public remarks: 2–3 (default 3), none selected — choosing is the agent's.
  Private remarks and headline: one each, selected on arrival.
- **Grounding is a check, not an instruction** (`Modules.Content.GroundingService`). The model
  cites `listing_fields` ids per sentence. A sentence is removed if it cites an unknown or
  malformed id, or if it makes a measurement, tax, school, association or zoning claim without
  citing a field of that kind entered by the agent or from a public record. Every figure in such
  a sentence must be the value of a cited field or a basic detail. Recognition is deliberately
  broad. Removals are counted in the `content_generated` audit event.
- **Claims are stored as spans.** `supporting_field_ids` holds `{spanStart, spanEnd, fieldIds}`
  per citing sentence; the contract's flat `supportingFieldIds` and the `unconfirmedClaims` are
  derived when read, against the facts as they are now. After an edit, a claim survives only if
  its sentence is still present word for word; a rewritten sentence is the agent's own words.
- **Versions supersede.** Each generation is the kind's next `version`; the previous drafts go
  `stale` and unselected, an approval of any of them is revoked (`superseded_by_regeneration`),
  and an approved listing returns to review — in one transaction.
- **Edits** require `If-Match` (428/409 with `current`). Editing an approved draft returns it to
  `draft`, revokes its approval (`content_edited`), and returns an approved listing to review;
  so does changing the selection. Over the limit is 422; a stale draft is 409.
- **Structured fields** (analysis runs only): the model proposes values for the six enumerated
  fields; values outside the RESO lookups, narrowed or extended by the profile's
  `enumerated_field_lookups` (`{field: {only?, extra?}}`), are dropped. What remains is stored as
  unconfirmed `ai_detected_from_photo`, never over an agent-entered, public-record or confirmed
  value. The lookups are enforced on AI output only, not on agent entries.
- **⚠ UNVERIFIED RESO seed.** `Libs.Reso/Lookups/dd-2.0-slice-a.json` (DD 2.0, legacy OData
  spellings) was transcribed without the published dictionary to hand and says `verified: false`.
  Verification is folded into T160; no MLS submission (Slice B) may rely on it until then.
- **Cache.** The run id is part of every `listing.content` input, so a regeneration is never
  answered from the response cache.
- **Private remarks** carry bracketed placeholders (`[Showing instructions]`, `[Access]`) for
  what only the agent knows (Story 3 scenario 5). Blocking approval on an unresolved placeholder
  belongs to the compliance checks (US4).
- **Endpoints live in the host** (`Api/Endpoints/ContentEndpoints.cs`), not `Modules.Content`
  as tasks.md says, for the reason in R20.
- **Content by hand was in no task.** SC-010 promises a listing can be completed by hand in a
  total AI outage, and the contract only had regenerate and edit. `POST /content {kind, body,
  tone?}` stores the agent's words as the kind's selected draft (no model, no claims, audited
  `content_edited`); 409 if the kind already has a current draft. It needs no verified profile,
  because compliance (US4) holds the listing to the MLS's rules, but a known limit is enforced.

## R36. US4 compliance: decisions made while building it (2026-10-17)

- **⚠ UNREVIEWED Fair Housing lexicon.** `Libs.Compliance/Lexicon/fair-housing.v1.json` was
  authored from the federal protected classes and California's additions (FEHA) without
  counsel's review, and says `reviewed: false`. Explicit preferences or exclusions are blocking;
  context-dependent phrases (a place of worship as a landmark, "exclusive", "safe neighborhood",
  "family-friendly neighborhood") are warnings. Like R4, the rules, severities and rewrites need
  counsel's or CRMLS compliance staff's review before Story 4 is relied on.
- **No judge model is a warning, not a block.** Blocking every listing while no
  `compliance.judge` model is enabled would contradict SC-010 (complete and publish by hand in a
  total AI outage). The lexicon and every deterministic check still run, and the listing carries
  a warning `fair_housing.judge_unavailable`, acknowledgeable with a reason. The same applies
  when the budget is spent or the answer is unusable.
- **The registry's temperature was never sent.** `AiService` dispatched with `ResponseFormat`
  only, so the judge's `temperature: 0` was recorded and dropped. Fixed in `AiService`, and
  `ComplianceDeterminismTests` checks the value reaching the provider (FR-024).
- **Runs.** `POST /compliance` creates a pipeline run of the `compliance` step, over the listing
  or one item (`compliance_target_type/id`, 0017). Analysis and regeneration runs end with the
  same step. An edit, a one-click rewrite, a photo override or an order change re-checks the
  edited item in the request (FR-025).
- **Findings are stored as a difference.** Identity is rule, layer, span, phrase and reason
  (`rule_id`, 0017). A matching finding is kept as it is, so an acknowledgement survives a check
  of unchanged words. One no longer found is `resolved`, a new one is raised and audited
  (`finding_raised`), and a superseded draft's or deleted photo's findings go `stale`. Writes
  for a listing are serialised on its row.
- **Merge.** A judge finding overlapping a lexicon finding is dropped; the lexicon's stands. A
  judge phrase not found in the text is dropped.
- **Fail closed.** An absent, unverified or incomplete profile is one blocking listing finding
  `mls.profile_incomplete` naming the missing values, and no per-item MLS rule runs against a
  guessed value. An altered photo with no profile, or with no approved label (CRMLS until T161),
  is blocking.
- **Advertising disclosure (FR-021)** in Slice A checks the listing: the brokerage's name and
  license number and the agent's license number must be on record. California requires a
  licensee's license number on advertising. Whether other states require both numbers is
  unverified.
- **Photo content flags** from analysis (people, documents, house numbers, plates, branding) and
  signs of prior alteration are warnings (Story 2 scenarios 4, 5).
- **Reasons are English** by design (R15). The UI localises the severity, layer and actions.
- **Hand-written content creation** no longer uses a serializable transaction: two creates on
  different listings could fail each other, and the unique (listing, kind, variant, version)
  constraint already decides a real race.
- **Endpoints live in the host** (`Api/Endpoints/ComplianceEndpoints.cs`), not
  `Libs.Compliance/Endpoints` as tasks.md says, for the reason in R20.

## Open items carried to Slice B (recorded, not resolved)

- **CRMLS API surface**: whether RESO Web API Add/Edit is exposed to vendors or only a proprietary
  listing-input API; the OAuth/delegated-authorization model; sandbox availability; vendor
  approval requirements, timeline, and fees. Affects Slice B only. Slice A deliberately creates no
  MLS adapter implementation (see plan.md Complexity Tracking for the `IMlsAdapter` decision).
- Clear Cooperation timing, seller marketing choice capture, social platform APIs, and the
  marketing pack — all untouched by Slice A beyond the capability predicates `CanApproveMarketing`
  and `CanSubmitToMls`, which exist in Slice A to enforce FR-061.
