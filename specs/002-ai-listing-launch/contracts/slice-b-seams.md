# Slice B Seams — design intent, deliberately not implemented in Slice A

**Purpose**: record the shape Slice B (Stories 6–8: CRMLS submission, marketing pack, publishing
scheduler) will need, so the next plan inherits the design without Slice A carrying unused code.

**Why a document and not C# interfaces**: Constitution Principle V (YAGNI — "do not build
abstractions, configuration layers, or extensibility points for requirements that are not in the
current spec") and Principle IV (no stubs or unimplemented functions). An interface with zero
implementors in Slice A violates both. A contract document costs nothing, ages gracefully, and
gives Slice B the same head start. See plan.md Complexity Tracking.

## What Slice A actually builds (not deferred)

| Seam | Form | Why it exists in Slice A |
|---|---|---|
| `MlsRuleProfile` + `mls_rule_profiles` table | **Built** | Story 3 generates within CRMLS remarks limits; Story 4 checks CRMLS rules, photo specs, and AB 723 disclosure. Genuinely consumed now. |
| `CanApproveMarketing` predicate | **Built** | FR-061 requires blocking marketing approval for a non-approved membership. The approvals endpoint returns `membership_not_approved` and the review screen renders a disabled reason. |
| `CanSubmitToMls` predicate | **Built** | Same predicate family; surfaced in `Membership.capabilities` so the UI can explain what a pending agent cannot yet do. |
| `listings.target_mls_id` | **Built** | Resolves the active rule profile in Slice A. |
| `photos.altered` / `alteration_type` / `disclosure_label` / `paired_original_id` | **Built** | FR-020 is active in this release because CRMLS is a California MLS. |

## `IMlsAdapter` — deferred, shape recorded

To be created in Slice B, in `Libs.Mls`, with `CrmlsAdapter` as its first implementor. Intended
operations, derived from FR-030–FR-032:

| Operation | Purpose | Notes |
|---|---|---|
| `CreateListingAsync` | Create as draft / Coming Soon where supported | Must be idempotent on an API-side correlation key so a retry after a partial failure cannot create a duplicate MLS listing (FR-031, spec Edge Cases). |
| `UploadPhotosAsync` | Push photos with order and captions | Order and captions already exist in Slice A's `photos` table. |
| `UpdateFieldsAsync` | Push field changes | |
| `ChangeStatusAsync` | Status transitions | |
| `GetStatusAsync` | Pull current MLS state | Feeds the FR-032 status sync. |

Cross-cutting requirements the adapter must satisfy:

- **Delegated authorization only** (FR-028/FR-029): OAuth 2.0 or CRMLS's approved vendor
  mechanism. The adapter must have no code path that accepts an MLS username or password, and no
  browser automation, scraping, or RPA of an MLS web interface.
- **Field-mapped errors** (FR-031): MLS validation failures must be translated to plain language
  bound to the specific listing field, not surfaced as raw provider payloads.
- **Token expiry mid-submission** (spec Edge Cases): partial submission state recorded, agent
  prompted to reconnect, retry must not duplicate.
- **Adapter-per-MLS** (FR-030): adding an MLS must not change listing business logic.

**Open and unresolved**: whether CRMLS exposes RESO Web API Add/Edit to vendors or only a
proprietary listing-input API; the OAuth model; sandbox availability; vendor approval
requirements, timeline, and fees. This is Slice B's first research item and gates Slice B going
live. It does **not** gate Slice A — Story 6's MLS-ready export (FR-033) is also Slice B, and
Slice A ends at an approved listing.

## Other Slice B surfaces (no Slice A footprint)

Recorded for completeness; none has a Slice A data field or interface:

- **MLS-ready export** (FR-033) — field-formatted copy-paste output plus a photo archive. Reads
  only Slice A data; needs no new seam.
- **Marketing pack** (FR-039–FR-041) — `content_drafts.kind` and `approvals.channel` are already
  extensible enumerations; Slice B adds values rather than columns. Slideshow video must be
  assembled only from real photos with deterministic effects (VIII.6).
- **Publishing scheduler + Clear Cooperation** (FR-034–FR-038) — new tables
  (`publish_jobs`, `seller_marketing_choices`), plus `brokerages.business_calendar`, which Slice A
  already defines because it belongs to the brokerage identity record.
- **Social connections** (FR-037) — new table; no Slice A footprint.

## Slice C (Stories 9–11)

Lifecycle content, lead inbox, and insights. No Slice A seams required beyond the extensible
`content_kind` and `approvals.channel` enumerations already in place.
