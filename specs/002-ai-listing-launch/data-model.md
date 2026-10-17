# Data Model: AI Listing Launch — Slice A

**Feature**: [002-ai-listing-launch](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-10-12

DDL lives in `RealtorAI_BackEnd/supabase/migrations/` and is the single source of truth. EF Core
maps these tables for querying only, with its own migrations disabled and an integration test that
fails on model-vs-schema drift ([research R14](./research.md#r14-data-access--ef-core-alongside-supabase-migrations)).

**Two-layer access control.** Every table below has RLS enabled, but RLS is **not** the
enforcement boundary for API traffic — the API connects with a privileged service role that
bypasses it. Per FR-063, each endpoint authorizes independently via `IResourceAuthorizer`; RLS is
defense in depth for any direct client access. Both layers must agree, and the authorization
matrix tests assert the endpoint layer while the RLS suite asserts the database layer.

---

## Enumerations

| Enum | Values | Notes |
|---|---|---|
| `provenance` | `agent_entered`, `public_record`, `ai_detected_from_photo`, `ai_suggested` | FR-005. The last two require confirmation before publish (FR-006). |
| `membership_status` | `pending`, `approved`, `rejected`, `revoked` | Only `approved` confers brokerage-scoped access (FR-060). |
| `membership_role` | `agent`, `broker_admin` | First registrant of a brokerage becomes `broker_admin` (FR-062). |
| `listing_status` | `draft`, `analyzing`, `ready_for_review`, `approved`, `archived` | Slice A terminal state is `approved`; `archived` is set by soft delete (FR-067). Slice B adds submission states. |
| `finding_severity` | `blocking`, `warning` | `blocking` can never be dismissed (FR-022); `warning` needs a logged reason (FR-023). |
| `finding_status` | `open`, `resolved`, `acknowledged`, `stale` | |
| `check_type` | `fair_housing`, `risky_claim`, `mls_rule`, `photo_integrity`, `advertising_disclosure` | |
| `finding_source` | `lexicon`, `judge`, `deterministic` | FR-017 requires recording which layer produced a finding. |
| `content_kind` | `public_remarks`, `private_remarks`, `headline` | Slice A only. Slice B adds marketing channels. |
| `content_status` | `draft`, `approved`, `stale` | Editing an approved item returns it to `draft` (FR-025). |
| `tone` | `professional`, `warm`, `luxury`, `concise` | FR-013. |
| `room_type` | `kitchen`, `living_room`, `primary_bedroom`, `bedroom`, `bathroom`, `exterior_front`, `backyard`, `pool`, `garage`, `view`, `floor_plan`, `other` | FR-009. Land/lot listings use only exterior/view/other. |
| `quality_flag` | `blurry`, `too_dark`, `overexposed`, `tilted_horizon`, `below_min_resolution`, `near_duplicate` | Deterministic except `near_duplicate` (embedding-based). |
| `content_flag` | `people_visible`, `personal_documents`, `house_number`, `license_plate`, `watermark_or_branding`, `possible_prior_alteration` | FR-009. Blocks external publishing until resolved. |
| `alteration_type` | `virtual_staging`, `other` | `other` raises a blocking finding under the CRMLS profile (FR-020). |
| `audit_action` | `ai_call`, `field_edit`, `field_confirm`, `photo_override`, `photo_reorder`, `photo_delete`, `finding_raised`, `finding_acknowledged`, `content_generated`, `content_edited`, `approval_granted`, `approval_revoked`, `approval_rejected_hash_mismatch`, `membership_requested`, `membership_decided`, `registry_changed`, `listing_soft_deleted`, `originals_purged` | FR-049, FR-062, FR-065, FR-066, FR-067. |
| `model_eval_status` | `unverified`, `passed`, `failed` | Only `passed` may be `enabled` (Principle VIII.4). |

---

## Identity & brokerage

### `profiles` (EXISTING — extended)

Feature 001 owns this table. **No `brokerage_id` column exists or ever existed** — see
[research R6](./research.md#r6-profilesbrokerage_id--brokerage_memberships-migration). This change
is purely additive; no data migration is required.

| Column | Type | Notes |
|---|---|---|
| *(existing)* | | `id`, `updated_at`, `full_name`, `company_name`, `preferred_language`, `subscription_tier`, `api_usage_count`, `accepted_terms_at` |
| `license_number` | `text` | NEW. Required before listing creation (FR-058). |
| `license_state` | `char(2)` | NEW. `CA` for the CRMLS launch. |
| `profile_completed_at` | `timestamptz` | NEW. Null until license fields are supplied. |
| `platform_admin` | `boolean` NOT NULL default `false` | NEW (FR-064). Gates AI registry and prompt writes. **No application role may write this column** — grant UPDATE per-column rather than row-wide, plus a trigger rejecting any change. Set only by direct database access. |

**RLS**: `profiles_select_own`, `profiles_update_own` (`auth.uid() = id`), **narrowed** so
`platform_admin` is not self-writable — grant UPDATE on the specific columns rather than the whole
row, and add a trigger rejecting any `platform_admin` change from a non-superuser. A
self-grantable admin flag would be a privilege-escalation path straight to the model registry.
Broker admins do **not** read agent profiles directly; brokerage-scoped reads go through
`brokerage_memberships`.

### `brokerages`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `name` | `text` NOT NULL | Appears in advertising disclosures (FR-021). |
| `license_number` | `text` NOT NULL | |
| `state` | `char(2)` NOT NULL | Drives state-specific disclosure rules. |
| `logo_storage_key` | `text` | `brokerage-assets` bucket. |
| `required_disclaimers` | `jsonb` | State-required license text (FR-057). |
| `business_calendar` | `jsonb` | Weekends/holidays — Slice B Clear Cooperation clock. |
| `created_at` | `timestamptz` | |

**RLS**: SELECT where the caller has an `approved` membership; UPDATE where the caller is an
`approved` `broker_admin` of that row.

### `brokerage_memberships`

Replaces the column that never existed. The authorization keystone for FR-050/FR-060/FR-061.

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `agent_id` | `uuid` → `profiles.id` | |
| `brokerage_id` | `uuid` → `brokerages.id` | |
| `status` | `membership_status` NOT NULL default `pending` | FR-060: created `pending`. |
| `role` | `membership_role` NOT NULL default `agent` | |
| `requested_at` | `timestamptz` NOT NULL | |
| `decided_by` | `uuid` → `profiles.id` | Null while pending. |
| `decided_at` | `timestamptz` | |

**Constraints**: unique `(agent_id)` where `status in ('pending','approved')` — an agent belongs to
at most one live brokerage. `decided_by`/`decided_at` must both be null or both set.

**RLS**: agent SELECTs own row; `approved` `broker_admin` SELECTs and UPDATEs rows of their own
brokerage. A `pending`, `rejected`, or `revoked` row confers **no** access in either direction.

**Capability predicates** (consumed by the API, not stored):
`CanApproveMarketing` and `CanSubmitToMls` both require `status = 'approved'`. Slice A actions
require only an authenticated agent with `profile_completed_at` set (FR-061).

> **Authorization state is never read from the JWT.** `jwt_expiry` is 3600s, so a token can
> outlive a membership decision by an hour. Status and role are loaded from this table per
> request, cached briefly, and invalidated on any membership decision
> ([research R7](./research.md#r7-validating-existing-supabase-auth-tokens-in-the-net-api)).

### `brokerage_settings`

Split from `brokerages` so settings churn doesn't touch the identity row.

| Column | Type | Notes |
|---|---|---|
| `brokerage_id` | `uuid` PK → `brokerages.id` | |
| `target_mls_id` | `text` | `CRMLS` for launch. |
| `default_tone` | `tone` | |
| `ai_budget_tokens` | `int` | Per-listing budget (FR-055). |
| `ai_budget_images` | `int` | |

**RLS**: as `brokerages`.

---

## Listings

### `listings`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `agent_id` | `uuid` → `profiles.id` NOT NULL | Owner. |
| `brokerage_id` | `uuid` → `brokerages.id` | Nullable: a pending agent can still create listings (FR-061). |
| `status` | `listing_status` NOT NULL default `draft` | |
| `address_line1`, `city`, `state`, `postal_code` | `text` | `agent_entered`. |
| `property_type` | `text` NOT NULL | |
| `list_price` | `numeric(12,2)` | |
| `bedrooms` | `int`, `bathrooms` `numeric(3,1)` | Halves are real. |
| `agent_notes` | `text` | Never copied into public remarks (FR-019 blocking check). |
| `target_mls_id` | `text` | Slice B seam; resolves the rule profile in Slice A. |
| `cover_photo_id` | `uuid` → `photos.id` | |
| `created_at`, `updated_at` | `timestamptz` | Autosave touches `updated_at` (FR Story 1.6). |
| `row_version` | `integer` NOT NULL default 1 | FR-066. Incremented on every write; a conditional update on a stale value affects 0 rows and surfaces as `409`. |
| `deleted_at` | `timestamptz` | FR-067. Soft delete only; starts the FR-065 retention clock. Working views filter `deleted_at is null`. |

**State machine**: `draft → analyzing → ready_for_review → approved`. `analyzing → draft` on total
pipeline failure (SC-010: manual completion always possible). `approved → ready_for_review`
whenever any approved item is edited (FR-025). **Any state → `archived`** via soft delete, which
sets `deleted_at`; there is no transition out of `archived` and no hard-delete path (FR-067).
Enforced in `Modules.Listings`, unit-tested.

**Indexes**: `(agent_id)`, `(brokerage_id)`, `(status)`.

**RLS**: owner full access; `approved` `broker_admin` of `brokerage_id` SELECT only.

### `listing_fields`

One row per RESO field. Provenance is per-field, which is what makes FR-006 enforceable.

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `listing_id` | `uuid` → `listings.id` ON DELETE CASCADE | |
| `reso_field_key` | `text` NOT NULL | e.g. `InteriorFeatures`. Validated against `Libs.Reso`. |
| `value` | `jsonb` NOT NULL | Scalar or array for multi-value enumerations. |
| `provenance` | `provenance` NOT NULL | FR-005. |
| `confirmed` | `boolean` NOT NULL default `false` | |
| `confirmed_by` | `uuid` → `profiles.id` | |
| `confirmed_at` | `timestamptz` | |
| `source_note` | `text` | Public-record source + retrieval date (FR-008). |
| `row_version` | `integer` NOT NULL default 1 | FR-066 optimistic concurrency. |

**Constraints**: unique `(listing_id, reso_field_key)`. **Check**: measurement/legal fields
(`LivingArea`, `LotSizeSquareFeet`, `YearBuilt`, `TaxAnnualAmount`, `AssociationFee`,
`HighSchoolDistrict`, `Zoning`) must have provenance in (`agent_entered`, `public_record`) —
FR-007 enforced in the database, not only in code. **Check**: `confirmed = true` requires
`confirmed_by` and `confirmed_at`.

**Index**: `(listing_id)`.

**RLS**: via parent listing.

---

## Media

### `photos`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `listing_id` | `uuid` → `listings.id` ON DELETE CASCADE | |
| `original_storage_key` | `text` NOT NULL | `listing-photos-original` — write-once. |
| `normalized_storage_key` | `text` | Long edge ~1280px, EXIF/GPS stripped (VIII.4.c). |
| `checksum_sha256` | `text` NOT NULL | Proves originals unchanged (FR-001); dedupe at upload. |
| `width`, `height`, `bytes` | `int` | Against `photo_min_*` in the rule profile. |
| `mime_type` | `text` NOT NULL | JPEG/PNG/HEIC/WebP (FR-003). |
| `order_index` | `int` NOT NULL | Agent-controlled (FR-011). |
| `caption` | `text` | AI-drafted alt text, agent-editable (FR-010). |
| `is_cover` | `boolean` NOT NULL default `false` | |
| `altered` | `boolean` NOT NULL default `false` | Agent declaration — the system never alters pixels. |
| `alteration_type` | `alteration_type` | |
| `disclosure_label` | `text` | From the rule profile's allowed list (FR-020). |
| `paired_original_id` | `uuid` → `photos.id` | The unaltered original, placed adjacent. |
| `uploaded_at` | `timestamptz` | |

**Constraints**: unique `(listing_id, checksum_sha256)` — exact duplicates rejected at upload with
a reference to the existing photo. Unique `(listing_id, order_index)` deferrable, so reordering is
one transaction. **Check**: `altered = true` requires `alteration_type`, `disclosure_label`, and
`paired_original_id` all non-null — FR-020's three-part requirement enforced at the database
level. At most one `is_cover` per listing (partial unique index).

**Index**: `(listing_id, order_index)`.

**RLS**: via parent listing.

**Storage policy (FR-001 / VIII.6)**: `listing-photos-original` grants INSERT and SELECT only —
**no UPDATE, no DELETE policy for any application role**. Immutability is a database/storage
guarantee, not an application convention.

**Retention (FR-065)**: originals are purged **3 years after the parent listing's `deleted_at`**,
by a privileged out-of-band operations job holding its own credential — deliberately *not* the
application identity and *not* a Hangfire job, because the application must never hold delete
rights on this bucket. Each purge is audited as `originals_purged`. Detaching a photo via
`DELETE /photos/{id}` removes the row association but never the object, so the only reclamation
path is this retention job. The 3-year value is configurable and is confirmed as part of the
AB 723 legal review.

**Agent overrides (FR-011, added in 0015)**: `room_type_override` (`room_type`) and
`feature_tags_override` (`jsonb` array) hold the agent's values; when set they win over
`photo_analyses`, which keeps the model's output untouched. Each change is audited as
`photo_override` with before and after.

### `photo_analyses`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `photo_id` | `uuid` → `photos.id` ON DELETE CASCADE | |
| `room_type` | `room_type` | AI result; agent override recorded in audit, not overwritten here. |
| `room_type_confidence` | `numeric(4,3)` | |
| `feature_tags` | `jsonb` | Candidate tags, provenance `ai_detected_from_photo`. |
| `quality_flags` | `jsonb` | Array of `quality_flag`. |
| `content_flags` | `jsonb` | Array of `content_flag`. |
| `alt_text` | `text` | |
| `cover_recommended` | `boolean`, `order_recommendation` `int`, `recommendation_reason` `text` | FR-010 requires a stated reason. |
| `model_id`, `prompt_version`, `prompt_hash` | `text` | VIII.5 provenance. |
| `raw_output` | `jsonb` | Schema-validated model output as stored. |
| `failed`, `failure_reason` | `boolean`, `text` | Per-photo isolation (Story 2.8). |
| `created_at` | `timestamptz` | |

**Index**: `(photo_id)`.

**RLS**: SELECT via parent listing; INSERT by service role only.

### `photo_embeddings`

| Column | Type | Notes |
|---|---|---|
| `photo_id` | `uuid` PK → `photos.id` ON DELETE CASCADE | |
| `embedding` | `vector(768)` | SigLIP2 base. **⚠** dimension must match the verified model. |
| `model_id` | `text` | |

**Index**: HNSW or IVFFlat on `embedding`. Requires `create extension vector`.

**RLS**: service role only — embeddings are never client-readable.

---

## Content, compliance, approval

### `content_drafts`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `listing_id` | `uuid` → `listings.id` ON DELETE CASCADE | |
| `kind` | `content_kind` NOT NULL | |
| `variant_index` | `int` NOT NULL default 0 | 2–3 description variants (FR-013). |
| `tone` | `tone` | |
| `body` | `text` NOT NULL | |
| `supporting_field_ids` | `jsonb` NOT NULL | One `{spanStart, spanEnd, fieldIds}` per sentence citing a fact (research R35) — powers FR-014 highlighting; the contract's flat list is derived from it. |
| `version` | `int` NOT NULL | Generation set: each regeneration of a kind stores the next and marks the previous `stale`. Edits are counted by `row_version`. |
| `status` | `content_status` NOT NULL default `draft` | |
| `selected` | `boolean` NOT NULL default `false` | The chosen variant. |
| `model_id`, `prompt_version`, `prompt_hash` | `text` | |
| `row_version` | `integer` NOT NULL default 1 | FR-066 optimistic concurrency. |
| `created_at` | `timestamptz` | |

**Constraints**: unique `(listing_id, kind, variant_index, version)`. At most one `selected` per
`(listing_id, kind)`.

**Grounding rule (FR-014)**: a draft cannot be approved while any id in `supporting_field_ids`
points to a `listing_fields` row with provenance `ai_detected_from_photo`/`ai_suggested` and
`confirmed = false`. Enforced in `Modules.Content` and covered by a contract test. The spans are
computed when the draft is read, so confirming a fact clears them without regenerating.

**Index**: `(listing_id, kind)`.

**RLS**: via parent listing.

### `compliance_findings`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `listing_id` | `uuid` → `listings.id` ON DELETE CASCADE | |
| `target_type` | `text` NOT NULL | `content_draft`, `photo`, or `listing`. |
| `target_id` | `uuid` NOT NULL | Polymorphic by design — findings attach to heterogeneous targets. |
| `check_type` | `check_type` NOT NULL | |
| `severity` | `finding_severity` NOT NULL | |
| `source` | `finding_source` NOT NULL | FR-017: which layer found it. |
| `span_start`, `span_end` | `int` | Null for photo/listing-level findings. |
| `phrase` | `text` | The offending text. |
| `reason` | `text` NOT NULL | English by design — see [research R15](./research.md#r15-i18n-and-dark-mode-gaps-in-the-plan-input). |
| `suggested_fix` | `text` | Compliant rewrite. |
| `status` | `finding_status` NOT NULL default `open` | |
| `acknowledged_reason` | `text` | Required for `warning` acknowledgement (FR-023). |
| `acknowledged_by`, `acknowledged_at` | `uuid`, `timestamptz` | |
| `lexicon_version`, `model_id`, `prompt_version` | `text` | Reproducibility (FR-024). |
| `created_at` | `timestamptz` | |

**Check (FR-022/FR-023)**: `status = 'acknowledged'` requires `severity = 'warning'` **and**
`acknowledged_reason`/`by`/`at` non-null. A `blocking` finding can therefore never be acknowledged
— the prohibition is a database constraint, not a UI affordance.

**Index**: `(listing_id, severity, status)` — the approve-gate query.

**RLS**: SELECT via parent listing; INSERT/resolve by service role; acknowledge by owning agent.

### `approvals`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `listing_id` | `uuid` → `listings.id` ON DELETE CASCADE | |
| `target_type`, `target_id` | `text`, `uuid` | What was approved. |
| `channel` | `text` NOT NULL | `mls` in Slice A; marketing channels in Slice B (FR-026). |
| `content_hash` | `text` NOT NULL | SHA-256 of exact approved content — answers SC-008. |
| `approved_by` | `uuid` → `profiles.id` NOT NULL | |
| `approved_at` | `timestamptz` NOT NULL | |
| `revoked_at` | `timestamptz` | Set on post-approval edit (FR-025). |
| `revoked_reason` | `text` | |

**Index**: `(listing_id, target_id)`.

**RLS**: SELECT via parent listing; INSERT by owning agent only — never by service role, so an
approval can only ever originate from a human action (VIII.7).

**Approval-time hash check (FR-066)**: the client submits the `contentHash` it reviewed. The API
recomputes the hash of current content and rejects the approval when they differ, auditing it as
`approval_rejected_hash_mismatch`. This is what makes SC-008's "what exactly did I approve" and
SC-011 true in the presence of concurrent writes — without it the stored hash would faithfully
record content the agent never saw.

---

## AI platform

### `ai_model_registry`

The Principle VIII.2 registry. Changing a model is a row update here, never a code change.

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `task_key` | `text` NOT NULL | `photo.analysis`, `photo.embedding`, `listing.content`, `compliance.judge`. |
| `provider` | `text` NOT NULL | |
| `model_id` | `text` NOT NULL | |
| `endpoint` | `text` | |
| `parameters` | `jsonb` | Temperature 0 for `compliance.judge` (FR-024). |
| `fallback_registry_id` | `uuid` → self | FR-054. |
| `license` | `text` | **Required to enable** (VIII.4.a). |
| `eval_status` | `model_eval_status` NOT NULL default `unverified` | |
| `eval_result` | `jsonb` | Harness metrics (FR-059). |
| `enabled` | `boolean` NOT NULL default `false` | |
| `verified_at` | `timestamptz` | |

**Check (VIII.4)**: `enabled = true` requires `license` non-null **and** `eval_status = 'passed'`.
The gate is a database constraint — no code path can enable an unlicensed or unevaluated model.
All Slice A rows seed `enabled = false` per [research R1](./research.md#r1-ai-model-verification-for-slice-a).

**Write authority (FR-064)**: writes require `profiles.platform_admin`. Agents and broker admins
can neither read nor write this table. Every change is audited as `registry_changed` with the
acting administrator.

**Constraint**: at most one `enabled` row per `task_key` (partial unique index).

**RLS**: SELECT by service role; writes restricted to platform administration.

### `prompt_versions`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `task_key` | `text` NOT NULL | |
| `version` | `int` NOT NULL | |
| `prompt_hash` | `text` NOT NULL | Matches the repo file (VIII.5). |
| `output_schema` | `jsonb` NOT NULL | Validated before storage. |
| `active` | `boolean` NOT NULL default `false` | |
| `created_at` | `timestamptz` | |

Prompts live as versioned repo files at `RealtorAI.Api/prompts/{task}/v{n}.md` with
`v{n}.schema.json`; this table records hash + active pointer. A startup check fails if a hash
drifts from its file.

**Constraints**: unique `(task_key, version)`; at most one `active` per `task_key`.

### `ai_usage`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `listing_id` | `uuid` → `listings.id` ON DELETE CASCADE | |
| `task_key`, `model_id` | `text` | |
| `tokens_in`, `tokens_out`, `image_count` | `int` | |
| `cost_usd` | `numeric(10,4)` | Null until provider pricing is verified ([R16](./research.md#r16-cost-caching-and-budget-enforcement-fr-055)); budget enforced in token/image units meanwhile. |
| `cache_hit` | `boolean` NOT NULL default `false` | |
| `created_at` | `timestamptz` | |

**Index**: `(listing_id)`. Budget checked **before** dispatch (FR-055).

**RLS**: SELECT via parent listing; INSERT by service role.

---

## Audit

### `audit_events`

Append-only. The spine of FR-049, FR-050, SC-004, and SC-008.

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `listing_id` | `uuid` → `listings.id` | Nullable for membership/registry events. |
| `brokerage_id` | `uuid` → `brokerages.id` | Scopes broker-admin reads. |
| `actor_id` | `uuid` → `profiles.id` | Null for system/service actions. |
| `action` | `audit_action` NOT NULL | |
| `target_type`, `target_id` | `text`, `uuid` | |
| `before_value`, `after_value` | `jsonb` | FR Story 5.2 requires before/after on edits. |
| `model_id`, `prompt_version`, `prompt_hash` | `text` | FR-049 for `ai_call`. |
| `input_ref`, `output_ref` | `text` | Storage/row references, not inlined payloads. |
| `payload_hash` | `text` | |
| `created_at` | `timestamptz` NOT NULL default `now()` | |

**Append-only enforcement**: INSERT policy only — **no UPDATE and no DELETE policy for any
application role**, plus a `BEFORE UPDATE OR DELETE` trigger that raises an exception, so even the
service role cannot rewrite history.

**Index**: `(listing_id, created_at)`, `(brokerage_id, created_at)`.

**RLS**: owning agent SELECTs their listings' events; `approved` `broker_admin` SELECTs events for
`brokerage_id` **only where the owner's membership is `approved`** (FR-050).

---

## MLS rule profile (Slice B seam, consumed in Slice A)

### `mls_rule_profiles`

Rules as data, so adding an MLS or reacting to a rule change needs no deployment.

| Column | Type | Notes |
|---|---|---|
| `id` | `text` PK | `CRMLS`. |
| `public_remarks_max_chars` | `int` | **⚠ UNVERIFIED** — must come from CRMLS's published rules. |
| `private_remarks_max_chars` | `int` | **⚠ UNVERIFIED** |
| `photo_min_width`, `photo_min_height`, `photo_min_count`, `photo_max_count` | `int` | **⚠ UNVERIFIED** |
| `forbidden_in_public_remarks` | `jsonb` | Phone, email, URL, agent name, brokerage name toggles + severities. |
| `required_fields` | `jsonb` | RESO keys that must be present and confirmed. |
| `allowed_alteration_types` | `jsonb` | `["virtual_staging"]` for CRMLS. |
| `disclosure_labels` | `jsonb` | **⚠ UNVERIFIED** — exact approved strings; AB 723 review pending. |
| `enumerated_field_lookups` | `jsonb` | CRMLS restrictions on RESO lookups. |
| `rulebook_version`, `verified_at` | `text`, `timestamptz` | Provenance of the values. |

**Fails closed**: a null numeric or empty required list raises a blocking
`mls_rule`/"rule profile incomplete" finding rather than passing silently
([research R2](./research.md#r2-crmls-rule-profile-for-slice-a)). Story 3 will not generate against
an invented limit and Story 4 will not pass a listing it has not actually checked.

**RLS**: SELECT by any authenticated user; writes platform-administration only.

---

## Entity relationships

```text
profiles ──1:0..1── brokerage_memberships ──N:1── brokerages ──1:1── brokerage_settings
   │                                                   │
   │ owns                                              │ scopes broker-admin reads
   ▼                                                   ▼
listings ──1:N── listing_fields                   audit_events
   ├──1:N── photos ──1:1── photo_analyses
   │           └──1:1── photo_embeddings
   │           └──0:1── photos (paired_original_id, self-ref)
   ├──1:N── content_drafts ──references──> listing_fields (supporting_field_ids)
   ├──1:N── compliance_findings ──targets──> content_drafts | photos | listings
   ├──1:N── approvals
   └──1:N── ai_usage

ai_model_registry ──self-ref── fallback_registry_id
prompt_versions   (task_key pairs with ai_model_registry.task_key)
mls_rule_profiles ──referenced by──> listings.target_mls_id
```

## Validation summary — requirement to enforcement point

| Requirement | Enforced by |
|---|---|
| FR-001 originals unchanged | `listing-photos-original` bucket has no UPDATE/DELETE policy; `checksum_sha256` verified on read |
| FR-003 1–100 photos, 4 formats | API validation + `mime_type` check + rule-profile photo counts |
| FR-005 provenance on every field | `listing_fields.provenance` NOT NULL |
| FR-006 confirm AI facts before publish | `content_drafts.supporting_field_ids` join against `confirmed` |
| FR-007 no AI-inferred measurements | DB check constraint on measurement/legal field keys |
| FR-013 remarks within MLS limit | `mls_rule_profiles.public_remarks_max_chars`, fails closed |
| FR-020 AB 723 three-part rule | `photos` check: `altered` ⇒ type + label + paired original |
| FR-022 blocking never dismissible | `compliance_findings` check: acknowledge requires `warning` |
| FR-024 deterministic compliance | `ai_model_registry.parameters` temperature 0 for `compliance.judge` |
| FR-026 explicit human approval | `approvals` INSERT by owning agent only, never service role |
| FR-049 append-only audit | INSERT-only policy + update/delete-blocking trigger |
| FR-050 broker reads scoped | RLS predicate requires owner membership `approved` |
| FR-060 pending confers nothing | RLS predicates test `status = 'approved'` |
| FR-063 endpoint authorization | `IResourceAuthorizer` per endpoint + authorization matrix tests |
| FR-064 platform admin gates registry | `profiles.platform_admin`, not writable by any application role (column grant + trigger) |
| FR-065 3-year original retention | Out-of-band privileged purge job keyed on `listings.deleted_at`; bucket still has no app-role DELETE |
| FR-066 no lost updates, no blind approvals | `row_version` conditional updates; approval requires matching reviewed `content_hash` |
| FR-067 soft delete only | `listings.deleted_at` + `archived` status; no hard-delete path, so audit FKs stay intact |
| VIII.4 model enablement gates | `ai_model_registry` check: `enabled` ⇒ license + `eval_status = 'passed'` |
| VIII.5 prompt provenance | `prompt_versions.prompt_hash` + startup drift check |


---

## Pipeline (0015)

Persisted run state: `GET /api/listings/{id}/pipeline` reads it, and SignalR notifications are
only hints to re-read it (FR-053).

### `pipeline_runs`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `listing_id` | `uuid` → `listings.id` (NO ACTION) | A listing is never hard-deleted (FR-067). |
| `started_by` | `uuid` → `profiles.id` | Every AI call of the run is audited as theirs. |
| `tone`, `variant_count` | `tone`, `int` (2–3) | Carried for content generation (US3). |
| `content_kind` | `content_kind` | 0016. The one kind a regeneration run is for; NULL for an analysis run, which generates every kind. |
| `started_at`, `finished_at` | `timestamptz` | |

**Constraint**: at most one unfinished run per listing (partial unique index) — starting again
returns that run.

### `pipeline_step_states` / `pipeline_photo_states`

Per-run state of each step (`normalize`, `technical_quality`, `embeddings`, `photo_analysis`,
`cover_and_order`, `content_generation`, and later `compliance`) and each photo, in the
contract's spellings, with a failure reason required when failed (or skipped, for a step).

**RLS**: enabled with no policy and no privilege — API only, like `photo_embeddings`.
