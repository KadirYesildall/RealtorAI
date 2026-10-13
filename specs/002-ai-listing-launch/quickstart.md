# Quickstart & Validation Guide: AI Listing Launch — Slice A

**Feature**: [002-ai-listing-launch](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-10-12

This is a run-and-validate guide. Schema details live in [data-model.md](./data-model.md), the API
surface in [contracts/api.openapi.yaml](./contracts/api.openapi.yaml), and the sidecar boundary in
[contracts/vision-sidecar.md](./contracts/vision-sidecar.md).

---

## Prerequisites

| Requirement | Notes for **this machine** |
|---|---|
| Docker Desktop | Installed at `C:\Users\ASUS\AppData\Local\Programs\DockerDesktop\Docker Desktop.exe` — **not** the default `C:\Program Files\Docker\`. Must be running with the daemon ready before anything else. |
| Supabase CLI | **Not installed globally and not on PATH.** Always invoke via `npx --yes supabase@latest <cmd>` from `RealtorAI_BackEnd/`. Feature 001's quickstart says `npm install -g supabase`; that was never run and should not be relied on. |
| .NET SDK | **10.0.302 verified installed** (8.0.423 also present). Note the constitution still says "8/9" — see plan.md Complexity Tracking; a PATCH amendment is pending. |
| Node.js | 20.9+ (Next.js 16 minimum). |
| Python | **3.13.14 verified installed** (pip 26.1.2). Note: the plan assumed 3.12; torch wheel availability for 3.13 is the open sidecar risk. |
| HF token | `HF_TOKEN` for Inference Providers. **Optional** — without it the pipeline degrades to the manual path (SC-010) and every registry entry stays disabled anyway until verification completes. |

> **Never start the Supabase containers from the Docker Desktop UI.** It fails with
> `no container found for project "realtorai_backend"` because the compose project entry outlives
> the containers. Always use `npx supabase start`, which recreates them.

---

## 1. Bring up the stack

Order matters. Each step must succeed before the next.

```powershell
# 1. Launch Docker Desktop, then confirm the daemon is actually ready
docker ps

# 2. Supabase (Postgres + Auth + Storage) — from RealtorAI_BackEnd/
cd RealtorAI_BackEnd
npx --yes supabase@latest start

# 3. Apply this feature's migrations (0002–0011)
npx --yes supabase@latest db reset   # destructive: rebuilds from all migrations + seed

# 4. Vision sidecar (separate terminal)
docker compose -f RealtorAI_BackEnd/vision-sidecar/compose.yaml up

# 5. API (separate terminal)
dotnet run --project RealtorAI_BackEnd/RealtorAI.Api/Api

# 6. Frontend (separate terminal)
cd RealtorAI_FrontEnd
npm run dev
```

**There is deliberately no Compose-owned Postgres and no MinIO.** The Supabase CLI stack is the
single source of Postgres, Auth, and Storage; Compose covers only the sidecar. Reasons in
[research R9](./research.md#r9-local-development-topology--supabase-cli-vs-docker-compose) and
[R10](./research.md#r10-object-storage--supabase-storage-vs-minio).

### Confirmed local endpoints

**Verified against the running stack on 2026-10-13**, not merely read from config: the Supabase
containers were up, the database was queried directly, and the API and sidecar were started and
probed. Re-confirm after any CLI upgrade.

| Service | Address |
|---|---|
| Supabase API gateway | `http://127.0.0.1:54321` |
| **Postgres** | `127.0.0.1:54322` — Postgres **17** |
| Shadow DB (db diff) | `54320` |
| Connection pooler | `54329` (disabled by default in config) |
| Supabase Studio | `http://127.0.0.1:54323` |
| Inbucket (mail capture) | `http://127.0.0.1:54324` |
| .NET API | `http://localhost:5080` |
| Next.js | `http://localhost:3000` |
| Vision sidecar | `http://127.0.0.1:8099` (internal-only in deployment) — **verified**: `/health` → 200 `{"status":"ok","model_loaded":false}` |
| Supabase OIDC issuer | `http://127.0.0.1:54321/auth/v1` — **verified**; JWKS at `/.well-known/jwks.json` serves an **ES256** key, so asymmetric signing is already the default and `signing_keys_path` does **not** need enabling |

Storage note: `file_size_limit = "50MiB"` per bucket, and `storage.s3_protocol` is enabled.

### SQL client inspection (DBeaver)

| Setting | Value |
|---|---|
| Host / Port | `127.0.0.1` / `54322` |
| Database | `postgres` |
| User / Password | `postgres` / `postgres` |

Credentials are the Supabase CLI local defaults; **verify against `supabase start` output** before
relying on them.

---

## 1b. What is already verified working (2026-10-13)

Confirmed by running it, so a fresh clone should reproduce these:

| Check | Result |
|---|---|
| `supabase db reset` applying all 11 migrations + seed | Clean |
| 17 public tables, **all** with RLS enabled | Verified |
| pgvector | **0.8.2** installed |
| Seeded sign-in (`agent.a@example.com` / `password123`) | Returns an access token |
| .NET solution build (Debug **and** Release) | 0 warnings, 0 errors |
| API `GET /health` | 200 `{"status":"ok"}` |
| API `GET /whoami` with a real Supabase ES256 token | 200, correct `agentId` |
| API `GET /whoami` with a tampered token | 401 (signature actually validated) |
| Sidecar `POST /quality` | Correct `blurry` + `below_min_resolution` flags on a real generated image |
| Sidecar `POST /embed` with no ML stack | 503 with reason — the SC-010 manual path, not a crash |
| Sidecar tests | 7/7 passing |
| Imaging format tests (incl. **HEIC**) | 8/8 passing |
| NuGet vulnerability scan | Clean across all 15 projects |
| EF Core model vs live schema (`SchemaDriftTests`) | Agrees on every table, column, type, nullability and all 15 enums |
| `Database.Migrate()` | Throws — EF owns no DDL (research R14) |
| Schema-guarantee integration tests | **182/182 passing** |
| TRUNCATE of `listings` / `audit_events` | Refused for client roles **and** for a superuser (research R18) |
| Frontend production dependencies | 0 vulnerabilities (`npm run audit:prod`) |
| Frontend regression (build, lint, tsc, vitest) | Clean on Next 16.3.8 — 16/16 tests |

## 2. Verify the schema (constitutional requirement)

The local-first rule requires inspecting the created tables with a SQL client. Run these checks —
each maps to a requirement that must hold at the database level, not just in application code.

```sql
-- pgvector present (migration 0002)
select extname from pg_extension where extname = 'vector';

-- RLS enabled on every new table (expect rowsecurity = true for all)
select tablename, rowsecurity from pg_tables
where schemaname = 'public'
  and tablename in ('brokerages','brokerage_memberships','brokerage_settings','listings',
                    'listing_fields','photos','photo_analyses','photo_embeddings',
                    'content_drafts','compliance_findings','approvals','audit_events',
                    'ai_model_registry','prompt_versions','ai_usage','mls_rule_profiles');

-- Enumerations match data-model.md
select t.typname, string_agg(e.enumlabel, ', ' order by e.enumsortorder)
from pg_type t join pg_enum e on e.enumtypid = t.oid
where t.typname in ('provenance','membership_status','listing_status','finding_severity')
group by t.typname;

-- audit_events is append-only: expect NO update/delete policies
select cmd, count(*) from pg_policies
where tablename = 'audit_events' group by cmd;
```

Then confirm the four hard guarantees **fail** as designed:

| Check | Expected result |
|---|---|
| `update audit_events set action = 'ai_call' where id = (select id from audit_events limit 1);` | **Error** — append-only trigger raises (FR-049) |
| `delete from audit_events where id = (select id from audit_events limit 1);` | **Error** — no delete policy, trigger raises |
| `insert into ai_model_registry (task_key, provider, model_id, enabled) values ('photo.analysis','hf','x',true);` | **Error** — check constraint: `enabled` needs `license` + `eval_status='passed'` (VIII.4) |
| `update photos set altered = true where id = <any>;` | **Error** — check constraint: requires `alteration_type` + `disclosure_label` + `paired_original_id` (FR-020) |
| `insert into listing_fields (..., reso_field_key, provenance) values (...,'LivingArea','ai_suggested');` | **Error** — check constraint: measurement fields reject AI provenance (FR-007) |
| As an authenticated agent: `update profiles set platform_admin = true where id = auth.uid();` | **Error / 0 rows** — the flag is not self-writable (FR-064). A success here is a privilege-escalation path to the model registry. |
| `delete from listings where id = <any>;` | **Error** — no hard-delete path; audit foreign key blocks it (FR-067) |

A passing `INSERT` on any of the last three rows means the corresponding requirement is **not**
enforced and the migration is wrong.

---

## 3. Seed data

`npx supabase db reset` applies `supabase/seed.sql`, which provides:

- **Brokerage** "Pacific Crest Realty" (CA) with `target_mls_id = 'CRMLS'` and sample disclaimers.
- **Agent A** — approved `broker_admin` of that brokerage, completed profile (CA license).
- **Agent B** — `pending` membership, completed profile. Used to prove FR-061.
- **Agent C** — approved `agent` of a *different* brokerage. Used to prove cross-brokerage denial.
- **CRMLS rule profile** row with numeric limits **deliberately left NULL** — the engine must fail
  closed until real published values are entered
  ([research R2](./research.md#r2-crmls-rule-profile-for-slice-a)).
- **Model registry** rows for all four task keys, every one `enabled = false`,
  `eval_status = 'unverified'` ([research R1](./research.md#r1-ai-model-verification-for-slice-a)).
- **Sample photo set** at `RealtorAI_BackEnd/supabase/seed-assets/sample-listing/` — 12 images
  deliberately including: an exterior front, a kitchen, two near-identical living-room shots, one
  blurry shot, one very dark shot, one tilted shot, one below minimum resolution, and one with a
  visible face. These exercise every Story 2 flag path.

---

## 4. End-to-end validation walkthrough (Stories 1–5)

Run as **Agent A** at `http://localhost:3000` unless stated otherwise.

### Story 1 — Draft from photos and basics

1. Sign in; you are routed to profile completion if license/state are missing. Complete it.
2. Create a listing: enter address, property type, list price, beds, baths, notes.
3. Upload the 12 sample photos.
4. Navigate away mid-entry, return.

**Expect**: draft persists with no explicit save; the below-minimum-resolution file still uploads
but is flagged (rejection is for *unsupported types/oversize*, not low resolution); public-record
fields are empty and marked required, never AI-filled; originals stored with checksums.

**Verify immutability** — compute a SHA-256 of a source file and compare:

```sql
select original_storage_key, checksum_sha256, width, height from photos
where listing_id = '<id>' order by order_index;
```

### Story 2 — AI photo analysis

5. Start the pipeline from the wizard's live view.

**With no model enabled** (the default state): expect `503` with a manual-completion path, the
pipeline marked failed per photo with a reason, and every control for classifying, captioning, and
ordering photos by hand still available. **This is the SC-010 test and it must pass before any
model is enabled.**

**With a verified, enabled model**: expect per-photo results streaming in over SignalR (not one
batch at the end); each photo classified; feature tags tagged `ai_detected_from_photo` and
unconfirmed; `blurry` / `too_dark` / `tilted_horizon` / `below_min_resolution` flags on the
corresponding seeds; `near_duplicate` on the two living-room shots; `people_visible` on the face
photo; a cover suggestion and recommended order each with a stated reason.

6. Override a room type, edit a caption, drag to reorder, delete a photo.

**Expect**: overrides persist, every one appears in the audit trail, and photo checksums are
**unchanged** — confirming no pixels were touched.

### Story 3 — Listing content generation

7. Choose a tone; generate content.

**Expect with the CRMLS profile incomplete (seeded state)**: `409 rule profile incomplete`. Content
is *not* generated against a guessed character limit. This is the fail-closed test.

**Expect with real CRMLS limits entered**: structured RESO fields using only permitted enumerated
values; public remarks within the limit; 2–3 variants; private remarks containing explicit
placeholders rather than invented showing instructions; any sentence resting on an unconfirmed AI
fact visibly highlighted.

8. Inspect that no measurement, year-built, tax, HOA, school, or zoning claim appears unless
   sourced `agent_entered`/`public_record`.

### Story 4 — Compliance review

9. Edit the public remarks to include: `"perfect for young families"`, `"walking distance to
   church"`, `"best schools in the district"`, and a phone number. Re-run compliance.

**Expect**: a Fair Housing finding per protected-class phrase with its exact span, reason, and a
compliant rewrite; `source` recording `lexicon` or `judge`; a risky-claim finding for the
superlative and the school claim; an `mls_rule` blocking finding for the phone number in public
remarks; AB 723 photo-integrity and advertising-disclosure checks run.

10. Try to acknowledge a **blocking** finding.

**Expect**: `422`. Confirm it is impossible at the database level too:

```sql
update compliance_findings set status = 'acknowledged', acknowledged_reason = 'x'
where severity = 'blocking' limit 1;   -- expect: check constraint violation
```

11. Acknowledge a **warning** finding without a reason → rejected; with a reason → accepted and
    audited.
12. Re-run compliance twice on identical text → identical findings (FR-024, temperature 0).

### Story 5 — Review and approve

13. Open the review screen. Everything — fields with provenance badges, photos with analysis,
    variants, findings — is reachable without leaving the page.
14. Attempt approval while a blocking finding is open.

**Expect**: disabled, with machine-readable blockers (`blocking_finding_open`,
`unconfirmed_ai_fact`, `required_field_missing`) rendered as human-readable reasons.

15. Resolve blockers, confirm every AI-derived fact, approve.

**Expect**: `approvals` row with approver, timestamp, and content hash; listing `approved`.

16. Edit the approved description.

**Expect**: returns to `draft`, approval revoked, compliance re-run **on that item only**.

### FR-020 — AB 723 altered-photo flow

17. Mark a photo altered with type `virtual_staging`, a disclosure label from the allowed list, and
    a paired original → accepted, original placed adjacent in order.
18. Mark a photo altered with type `other` → **blocking** finding (CRMLS permits only virtual
    staging).
19. Mark altered while omitting any of the three required attributes → rejected by the database
    check, not merely the UI.

### FR-060/061/063 — membership and authorization

20. As **Agent B** (pending): create a listing, run analysis, generate content, approve listing
    content → **all succeed**. Approving a *marketing* channel or MLS submission → denied with
    `membership_not_approved`.
21. As **Agent C** (other brokerage): `GET /api/listings/{A's listing}` → `403`.
22. As **Agent A** (approved broker_admin): read Agent B's listing → **403**, because B's
    membership is still pending (FR-050). Approve B's membership, retry → succeeds.
23. Revoke B's membership and immediately retry → denied **without waiting for token refresh**,
    proving authorization comes from the database, not JWT claims
    ([research R7](./research.md#r7-validating-existing-supabase-auth-tokens-in-the-net-api)).

### FR-064/065/066/067 — admin role, retention, concurrency, soft delete

27. **Platform admin**: as Agent A (not a platform admin), call `GET /api/admin/ai/registry` →
    `403`. Set `platform_admin = true` by direct SQL, retry → succeeds. Confirm the change to a
    registry row is audited as `registry_changed` with the acting administrator.
28. **Concurrency**: open the same listing in two browser sessions. Edit a field in session 1, then
    edit the same field in session 2 using its stale row version → `409` carrying the current
    server state, and session 2 shows what changed instead of losing the edit (SC-012).
29. **Blind-approval guard**: load the review screen, change the description via the API in another
    session, then approve from the stale screen → rejected with
    `content_changed_since_review`, audited as `approval_rejected_hash_mismatch`. Re-review and
    approve → succeeds (SC-011).
30. **Pipeline vs agent**: start the pipeline, then edit a field it is about to write. Whichever
    write lands second is rejected and retried against fresh state; the agent's in-progress edit is
    never overwritten mid-edit.
31. **Soft delete**: delete a listing → `204`; it leaves the index (returns with
    `?includeArchived=true`), its status is `archived`, `deleted_at` is set, and
    `GET /api/listings/{id}/audit` **still returns its full trail** (FR-049 preserved). Deleting
    again → `204` (idempotent).
32. **Retention**: confirm the originals are still present in storage after deletion and that no
    application role can remove them; the 3-year purge is an out-of-band job, not reachable from
    the API (FR-065).

### Constitution checks

24. **Dark mode**: toggle the theme and walk every new screen *including* empty, queued,
    analyzing, per-photo-failure, blocking-finding, and warning states. All correct in both
    themes; WCAG AA contrast verified in both; severity distinguishable without relying on colour.
25. **i18n**: switch EN ⇄ TR. No hardcoded strings. Finding `reason`/`suggested_rewrite` stay
    English by design ([research R15](./research.md#r15-i18n-and-dark-mode-gaps-in-the-plan-input)).
26. **Mobile**: review screen usable at mobile width; all controls keyboard reachable.

---

## 5. Test suites

```powershell
# API: unit, contract (incl. authorization matrix), integration (Testcontainers), prohibitions
dotnet test RealtorAI_BackEnd/RealtorAI.Api

# The VIII.6 gate on its own — must fail the build if any image/video generation
# dependency or endpoint is referenced anywhere
dotnet test RealtorAI_BackEnd/RealtorAI.Api --filter Category=Prohibitions

# Frontend
cd RealtorAI_FrontEnd
npm test            # Vitest contract tests
npm run test:e2e    # Playwright Stories 1–5, AI stubbed with deterministic fixtures

# Sidecar
pytest RealtorAI_BackEnd/vision-sidecar
```

**Expected gates**: every endpoint in
[api.openapi.yaml](./contracts/api.openapi.yaml) has an authorization-matrix test covering owner /
other agent / pending member / approved admin same brokerage / admin other brokerage; a test fails
if any endpoint lacks an explicit policy; the RLS suite proves direct client access is confined;
an EF-vs-SQL drift test fails if the model and schema diverge.

---

## 6. AI evaluation harness

Required before any registry entry may be enabled (Principle VIII.4, FR-059):

```powershell
dotnet run --project RealtorAI_BackEnd/RealtorAI.Api/tools/AiEval -- --task all
```

| Metric | Threshold | Source |
|---|---|---|
| Room classification accepted unchanged | ≥ 90% | SC-002 |
| Fair Housing recall | ≥ 95% | SC-003 |
| Fair Housing false positives | ≤ 10% | SC-003 |
| JSON-schema validity rate | report + threshold set here | FR-015 |
| 30-photo analysis latency | < 2 min p90 | SC-005 |
| Cost per listing | within budget | SC-006 |

The harness writes results to `ai_model_registry.eval_result` and sets `eval_status`. Because
`enabled = true` requires `license` **and** `eval_status = 'passed'` via a check constraint, a
failing harness run cannot be bypassed from the API or the UI.

---

## 7. Known gates before this feature can go live

Build and local validation work today; these block **production enablement**, not development:

1. **Model verification** — Hub existence, commercial license recorded, HF Inference Providers
   availability with image input and JSON-schema response format, measured latency/cost.
   ([R1](./research.md#r1-ai-model-verification-for-slice-a))
2. **Evaluation harness passing** for every task key.
3. **CRMLS rule profile populated** with actual published values; the engine fails closed until
   then. ([R2](./research.md#r2-crmls-rule-profile-for-slice-a))
4. **AB 723 disclosure label text and placement confirmed by counsel or CRMLS compliance staff** —
   criminal-liability statute; not an engineering decision.
   ([R4](./research.md#r4-ab-723-and-crmls-photo-integrity-requirements-fr-020))
5. **Supabase asymmetric JWT signing keys enabled** and the exact `config.toml` key shape and JWKS
   path confirmed against the installed CLI. ([R7](./research.md#r7-validating-existing-supabase-auth-tokens-in-the-net-api))
6. **Embedding dimension confirmed** to match `photo_embeddings.embedding vector(N)` before the
   migration lands. ([vision-sidecar contract](./contracts/vision-sidecar.md))
7. **Package compatibility verified at install time**: Hangfire Postgres storage on .NET 10 with
   Supabase pooling (and a dedicated `hangfire` schema), plus TanStack Query / React Hook Form /
   dnd-kit against React 19.2 and Next 16.
8. **Retention period confirmed by counsel** — the 3-year original-photo retention (FR-065) is a
   sensible default tied to California broker record-keeping, but its legally required value is
   confirmed as part of the AB 723 review, not settled here.
9. **Privileged purge job credential provisioned** — the FR-065 retention job needs its own
   identity with delete rights on the originals bucket, which the application identity must never
   hold. Until it exists, originals simply accumulate (safe, but unbounded).
