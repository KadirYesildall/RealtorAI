---

description: "Task list for AI Listing Launch — Slice A (Stories 1–5)"
---

# Tasks: AI Listing Launch — Slice A

**Input**: Design documents from `/specs/002-ai-listing-launch/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/)

**Tests**: **REQUIRED, not optional.** The template defaults tests to optional, but Constitution
Principle II (Test-First Discipline) makes contract tests mandatory for every API endpoint and
integration tests mandatory for cross-service flows, written first and **confirmed failing** before
implementation. Only pure-presentation components with no logic may skip tests, and each such skip
is noted inline.

**Organization**: Grouped by user story so each is independently implementable and testable.

**Scope**: Slice A = Stories 1–5 (all P1) plus FR-020, FR-049–FR-067. Slice B (CRMLS submission,
marketing pack, publishing) and Slice C (lifecycle, leads, insights) are **out of scope** — see
[contracts/slice-b-seams.md](./contracts/slice-b-seams.md).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US5)
- Include exact file paths in descriptions

## Path Conventions

Per [plan.md](./plan.md) "Project Structure" — the repo's two mandated top-level directories:

- **API**: `RealtorAI_BackEnd/RealtorAI.Api/` (ASP.NET Core, .NET 10, modular monolith)
- **Migrations**: `RealtorAI_BackEnd/supabase/migrations/` — **owns all DDL**
- **Sidecar**: `RealtorAI_BackEnd/vision-sidecar/`
- **Frontend**: `RealtorAI_FrontEnd/` (existing Next.js 16 app, extended in place)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Stand up the new .NET solution and sidecar alongside the existing Supabase + Next.js stack

- [X] T001 Create solution `RealtorAI_BackEnd/RealtorAI.Api/RealtorAI.Api.slnx` targeting `net10.0` (the `.slnx` XML format, not the legacy `.sln`)
- [X] T002 [P] Create host project `RealtorAI_BackEnd/RealtorAI.Api/Api/Api.csproj` (ASP.NET Core Web API)
- [X] T003 [P] Create module projects `Modules.Brokerage`, `Modules.Listings`, `Modules.Media`, `Modules.Content`, `Modules.Audit` under `RealtorAI_BackEnd/RealtorAI.Api/`
- [X] T004 [P] Create library projects `Libs.Ai`, `Libs.Compliance`, `Libs.Reso`, `Libs.Mls` under `RealtorAI_BackEnd/RealtorAI.Api/`
- [X] T005 [P] Create `RealtorAI_BackEnd/RealtorAI.Api/Pipeline/Pipeline.csproj`
- [X] T006 [P] Create test projects `tests/unit`, `tests/contract`, `tests/integration`, `tests/prohibitions` under `RealtorAI_BackEnd/RealtorAI.Api/`
- [X] T007 Wire project references and the DI composition root in `RealtorAI_BackEnd/RealtorAI.Api/Api/Program.cs`
- [X] T008 [P] Add Serilog structured logging and OpenTelemetry in `RealtorAI_BackEnd/RealtorAI.Api/Api/Observability/`
- [X] T009 [P] Add EF Core + Npgsql + pgvector packages with **EF migrations disabled** in `RealtorAI_BackEnd/RealtorAI.Api/Api/Data/AppDbContext.cs` (DDL stays in SQL per research R14)
- [X] T010 [P] Create FastAPI skeleton `RealtorAI_BackEnd/vision-sidecar/app/main.py` plus `RealtorAI_BackEnd/vision-sidecar/compose.yaml`
- [X] T011 [P] Add TanStack Query, React Hook Form, and dnd-kit to `RealtorAI_FrontEnd/package.json`, **verifying React 19.2 / Next 16 peer compatibility at install time** (research R8)
- [X] T012 [P] Create typed API client scaffold `RealtorAI_FrontEnd/lib/api/client.ts` with session-token propagation
- [X] T013 [P] Configure `RealtorAI_BackEnd/RealtorAI.Api/Api/appsettings.Development.json` with server-only secrets (no `NEXT_PUBLIC_` exposure)
- [X] T014 Verify the full local stack per [quickstart.md](./quickstart.md) (`npx --yes supabase@latest start` → `dotnet run` → sidecar → `npm run dev`) and record the **actual** CLI-reported connection values back into `quickstart.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Schema, authorization, AI platform, and audit spine that every user story depends on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Migrations (sequential — ordered DDL in one directory)

- [X] T015 Create `RealtorAI_BackEnd/supabase/migrations/0002_pgvector.sql` enabling the `vector` extension
- [X] T016 Create `RealtorAI_BackEnd/supabase/migrations/0003_brokerages.sql` — `brokerages`, `brokerage_memberships`, `brokerage_settings` with RLS, status/role enums, and indexes
- [X] T017 Create `RealtorAI_BackEnd/supabase/migrations/0004_profile_license.sql` — add `license_number`, `license_state`, `profile_completed_at`, `platform_admin` to `profiles`; **narrow `profiles_update_own` to per-column grants and add a trigger rejecting any `platform_admin` change** (FR-064)
- [X] T018 Create `RealtorAI_BackEnd/supabase/migrations/0005_listings.sql` — `listings` (with `row_version`, `deleted_at`, `archived` status) and `listing_fields` (with `row_version` and the FR-007 measurement-provenance check constraint)
- [X] T019 Create `RealtorAI_BackEnd/supabase/migrations/0006_media.sql` — `photos` (with the FR-020 three-part altered check), `photo_analyses`, `photo_embeddings` + vector index
- [X] T020 Create `RealtorAI_BackEnd/supabase/migrations/0007_content_compliance.sql` — `content_drafts` (with `row_version`), `compliance_findings` (with the FR-022 acknowledge-only-if-warning check), `approvals`
- [X] T021 Create `RealtorAI_BackEnd/supabase/migrations/0008_ai_platform.sql` — `ai_model_registry` (with the VIII.4 `enabled ⇒ license + eval_status='passed'` check), `prompt_versions`, `ai_usage`
- [X] T022 Create `RealtorAI_BackEnd/supabase/migrations/0009_audit.sql` — `audit_events` with INSERT-only policy **and** a `BEFORE UPDATE OR DELETE` trigger that raises (FR-049)
- [X] T023 Create `RealtorAI_BackEnd/supabase/migrations/0010_mls_rule_profiles.sql` — table plus a CRMLS row with **NULL numeric limits** so the engine fails closed (research R2)
- [X] T024 Create `RealtorAI_BackEnd/supabase/migrations/0011_storage_buckets.sql` — buckets with **no UPDATE/DELETE policy on `listing-photos-original`** (FR-001)
- [X] T025 Create `RealtorAI_BackEnd/supabase/seed.sql` and `RealtorAI_BackEnd/supabase/seed-assets/sample-listing/` — Agents A/B/C, brokerage, disabled registry rows, incomplete CRMLS profile, and the 12-image flag-exercising photo set per [quickstart.md](./quickstart.md)

- [X] T025a Create `RealtorAI_BackEnd/supabase/migrations/0012_privilege_hardening.sql` — revoke the privileges Supabase's schema default grants to `anon`/`authenticated` (incl. **TRUNCATE, which bypasses RLS**), re-grant per table, and add `before truncate` guards on `audit_events` and `listings` (research R18)

### Schema guarantee tests (write first — these must fail before the migrations land)

- [X] T026 [P] Integration test: RLS enabled on all 16 new tables in `tests/integration/RlsEnabledTests.cs`
- [X] T027 [P] Integration test: `audit_events` UPDATE and DELETE both raise in `tests/integration/AuditAppendOnlyTests.cs`
- [X] T028 [P] Integration test: enabling a registry row without license or passing eval is rejected in `tests/integration/RegistryGateTests.cs`
- [X] T029 [P] Integration test: `photos.altered` without all three attributes is rejected in `tests/integration/PhotoAlteredCheckTests.cs`
- [X] T030 [P] Integration test: measurement fields reject AI provenance in `tests/integration/FieldProvenanceCheckTests.cs`
- [X] T031 [P] Integration test: an authenticated agent cannot set `platform_admin` on themselves in `tests/integration/PlatformAdminEscalationTests.cs`
- [X] T032 [P] Integration test: hard `DELETE FROM listings` is blocked by the audit FK in `tests/integration/NoHardDeleteTests.cs`
- [X] T033 [P] Integration test: EF Core model vs actual SQL schema drift in `tests/integration/SchemaDriftTests.cs`
- [X] T033a [P] Integration test: no client role holds TRUNCATE/REFERENCES/TRIGGER on any feature table, `anon` holds nothing, and the schema default no longer grants — in `tests/integration/PrivilegeHardeningTests.cs` (research R18)
- [X] T033b [P] Integration test: every database column default matches the CLR property default in `tests/integration/ColumnDefaultsTests.cs` (found a real bug: EF sends the CLR value on insert, so `row_version` started at 0 against a SQL default of 1)

### Authentication & authorization (FR-063, FR-064)

- [X] T034 Implement Supabase JWT bearer validation via JWKS in `RealtorAI.Api/Api/Authentication/SupabaseJwtSetup.cs` (validate issuer, audience, lifetime, signature; trust only `sub`)
- [X] T035 Implement `IResourceAuthorizer` in `RealtorAI.Api/Api/Authorization/ResourceAuthorizer.cs` (listing owner, or approved broker_admin of the listing's brokerage)
- [X] T036 [P] Implement capability predicates `CanApproveMarketing` and `CanSubmitToMls` in `Modules.Brokerage/Capabilities.cs` (both require `approved`)
- [X] T037 Implement per-request membership resolution from the database with a short invalidating cache in `Modules.Brokerage/MembershipResolver.cs` — **never read status or role from JWT claims** (research R7)
- [X] T038 Implement the endpoint policy registry plus a test that **fails the build if any endpoint lacks an explicit policy** in `RealtorAI.Api/Api/Authorization/PolicyRegistry.cs` and `tests/contract/EveryEndpointHasPolicyTests.cs`
- [X] T039 Build the authorization matrix test harness in `tests/contract/AuthorizationMatrix.cs` covering owner / other agent / pending member / approved admin same brokerage / admin other brokerage / platform admin / non-admin

### Optimistic concurrency (FR-066)

- [X] T040 Implement `If-Match` row-version handling and the `VersionConflict` 409 problem-details response in `RealtorAI.Api/Api/Concurrency/`
- [X] T041 [P] Unit tests for the conditional-update helper in `tests/unit/OptimisticConcurrencyTests.cs`

### Audit spine (FR-049)

- [X] T042 Implement the append-only audit writer in `Modules.Audit/AuditWriter.cs` (actor, action, before/after, model/prompt refs, payload hash)
- [X] T043 [P] Contract test for the audit writer in `tests/contract/AuditWriterTests.cs`

### AI platform (Principle VIII)

- [X] T044 Implement the `AiService` abstraction over `IChatClient` / `IEmbeddingGenerator` in `Libs.Ai/AiService.cs` — **no vendor, model id, or endpoint may appear outside the registry** (VIII.1)
- [X] T045 Implement the model registry reader in `Libs.Ai/ModelRegistry.cs` (task → provider → model → endpoint → params → fallback → enabled)
- [X] T046 Implement the prompt registry in `Libs.Ai/PromptRegistry.cs` reading versioned files from `RealtorAI.Api/prompts/{task}/v{n}.md` + `v{n}.schema.json`, with a **startup check that fails on hash drift** (VIII.5)
- [X] T047 Implement JSON Schema validation with exactly one retry carrying the validation error, then an explicit failure, in `Libs.Ai/StructuredOutputValidator.cs` (FR-015)
- [X] T048 [P] Implement resilience (timeouts, backoff, per-provider circuit breaker, automatic fallback) in `Libs.Ai/Resilience.cs` using `Microsoft.Extensions.Http.Resilience`
- [X] T049 Implement cost tracking, the **pre-dispatch** per-listing budget check, and request caching by input hash in `Libs.Ai/CostTracker.cs` (FR-055)
- [X] T050 Implement the registry admin endpoints in `RealtorAI.Api/Api/Endpoints/AiRegistryEndpoints.cs` gated on `profiles.platform_admin`, auditing every change as `registry_changed` (FR-064)
- [X] T051 [P] Contract tests for the registry admin endpoints including the authorization matrix in `tests/contract/AiRegistryEndpointTests.cs`
- [X] T052 [P] Unit tests for schema-retry and fallback behaviour in `tests/unit/AiServiceTests.cs`
- [X] T053 **Prohibition test (VIII.6)**: fail the build if any image/video-generation dependency or endpoint is referenced anywhere in the solution or sidecar, in `tests/prohibitions/NoGenerativeImageryTests.cs`
- [X] T054 Create the AI evaluation harness skeleton in `RealtorAI.Api/tools/AiEval/` with the task/metric scaffolding from [quickstart.md](./quickstart.md) §6

### Shared services

- [X] T055 Configure Hangfire on Postgres using a **dedicated `hangfire` schema** so it never collides with Supabase migrations, in `Pipeline/HangfireSetup.cs` (research R12)
- [X] T056 [P] Implement the SignalR pipeline-progress hub in `RealtorAI.Api/Api/Hubs/PipelineHub.cs`
- [X] T057 Implement the Supabase Storage wrapper in `Modules.Media/StorageService.cs` — write-once originals, normalized variants, short-lived signed URLs
- [X] T058 Implement image normalization in `Modules.Media/ImageNormalizer.cs` — **non-generative operations only** (resize long edge ~1280px, compress, convert, crop, rotate) plus EXIF/GPS stripping (VIII.4.c, VIII.6)
- [X] T059 [P] Unit tests asserting normalization strips EXIF and never mutates the original, in `tests/unit/ImageNormalizerTests.cs`
- [X] T060 [P] Define `IPropertyDataProvider` and ship `NullPropertyDataProvider` in `Modules.Listings/PropertyData/` (research R5)
- [X] T061 [P] Contract tests for every brokerage and membership endpoint — declare (201/409), list pending, approve/reject decision, and settings read/write — including the authorization matrix and the FR-062 audit assertion, in `tests/contract/BrokerageMembershipTests.cs`
- [X] T062 Implement brokerage and membership endpoints (declare, list pending, approve/reject, settings) in `Modules.Brokerage/Endpoints/` — *built in `Api/Endpoints/BrokerageEndpoints.cs` with the lifecycle rules in `Modules.Brokerage/MembershipRules.cs`, because the EF model lives in the host (research R20, R32)* per [api.openapi.yaml](./contracts/api.openapi.yaml)
- [X] T063 Build the brokerage frontend in `RealtorAI_FrontEnd/app/dashboard/brokerage/` (settings, members approve/reject) plus the pending-membership banner component, with EN/TR keys in `lib/i18n/dictionaries/` and both themes

**Checkpoint**: Foundation ready — user story implementation can now begin

---

## Phase 3: User Story 1 — Create a Listing Draft from Photos and Basic Details (Priority: P1)

**Goal**: An agent uploads 1–100 real photos plus six basic details and gets a resumable draft, with originals stored byte-identical and public-record fields left honest.

**Independent Test**: Upload 30 mixed-format photos, enter the six detail fields, navigate away and return — the draft is intact, originals verify by checksum, and public-record fields are empty and marked required rather than AI-filled.

### Tests for User Story 1 (write first, confirm failing)

- [X] T064 [P] [US1] Contract test `POST /api/profile/complete` in `tests/contract/ProfileCompleteTests.cs`
- [X] T065 [P] [US1] Contract test `POST /api/listings` including the pending-membership allowance in `tests/contract/CreateListingTests.cs`
- [X] T066 [P] [US1] Contract test `PATCH /api/listings/{id}` autosave plus stale-`If-Match` 409 in `tests/contract/ListingAutosaveTests.cs`
- [X] T067 [P] [US1] Contract test `DELETE /api/listings/{id}` soft delete and idempotency in `tests/contract/ListingSoftDeleteTests.cs`
- [X] T068 [P] [US1] Contract test `POST /api/listings/{id}/photos` per-file 207 accept/reject in `tests/contract/PhotoUploadTests.cs`
- [X] T069 [P] [US1] Contract test `PUT /api/listings/{id}/fields` rejecting AI provenance on measurement fields in `tests/contract/FieldUpsertTests.cs`
- [X] T070 [P] [US1] Integration test: uploaded originals remain byte-identical by checksum in `tests/integration/OriginalImmutabilityTests.cs`
- [X] T071 [P] [US1] Integration test: an agent with a `pending` membership can create a listing and run AI work (FR-061) in `tests/integration/PendingMembershipScopeTests.cs`
- [X] T072 [P] [US1] E2E test: wizard entry, autosave, navigate away, resume in `RealtorAI_FrontEnd/tests/integration/listing-wizard.spec.ts`

### Implementation for User Story 1

- [X] T073 [US1] Implement the listing aggregate and state machine (`draft → analyzing → ready_for_review → approved`, any → `archived`) in `Modules.Listings/Listing.cs`
- [X] T074 [US1] Implement the provenance-aware field service in `Modules.Listings/ListingFieldService.cs` (FR-005, FR-007)
- [X] T075 [US1] Implement listings endpoints (create, get, list with `includeArchived`, autosave, soft delete) in `Modules.Listings/Endpoints/ListingEndpoints.cs` — *built in `Api/Endpoints/ListingEndpoints.cs` with the rules in `Modules.Listings`, because the EF model lives in the host (research R20, R33); `POST /api/profile/complete` is `Api/Endpoints/ProfileEndpoints.cs`*
- [X] T076 [US1] Implement field upsert and confirm endpoints in `Modules.Listings/Endpoints/ListingFieldEndpoints.cs` (FR-006) — *built in `Api/Endpoints/ListingFieldEndpoints.cs` (research R20)*
- [X] T077 [US1] Implement the photo upload endpoint in `Modules.Media/Endpoints/PhotoEndpoints.cs` — *built in `Api/Endpoints/PhotoEndpoints.cs` (research R20)* — originals + normalized variants, checksum dedupe, per-file accept/reject, 1–100 bound
- [X] T078 [US1] Wire public-record prefill through `IPropertyDataProvider` in `Modules.Listings/PrefillService.cs` — the null path marks fields required (FR-008)
- [X] T079 [P] [US1] Build the profile completion page at `RealtorAI_FrontEnd/app/dashboard/profile/complete/page.tsx`
- [X] T080 [P] [US1] Build the new-listing wizard at `RealtorAI_FrontEnd/app/dashboard/listings/new/page.tsx` (basics → upload → progress) — *photos go through the Route Handler `app/api/listings/[listingId]/photos/route.ts`, which proxy.ts skips (its body buffering truncates past 10 MB)*
- [X] T081 [P] [US1] Build the listing index at `RealtorAI_FrontEnd/app/dashboard/listings/page.tsx` with the archived filter
- [X] T082 [P] [US1] Add Zod schemas in `RealtorAI_FrontEnd/lib/validation/listing.ts`
- [X] T083 [P] [US1] Add EN/TR keys for all US1 strings in `RealtorAI_FrontEnd/lib/i18n/dictionaries/{en,tr}.json` and verify both themes on every US1 screen — *verified by `tests/integration/us1-themes.spec.ts`: computed WCAG AA contrast on every rendered text node, plus screenshots, light and dark*

**Checkpoint**: US1 independently deliverable — an agent can create and resume a listing draft

---

## Phase 4: User Story 2 — AI Photo Analysis (Priority: P1)

**Goal**: Each photo is classified, tagged, quality- and content-flagged, captioned, and ordered, with results streaming in per photo — and never a pixel altered.

**Independent Test**: Submit the 12-image seed set and confirm every photo gets a room type, feature tags, the expected quality/content flags, a caption, and a cover/order suggestion with a reason — then confirm all checksums are unchanged.

### Tests for User Story 2 (write first, confirm failing)

- [X] T084 [P] [US2] Contract test `POST /api/listings/{id}/pipeline` covering 202 / 409 budget / 503 no-model in `tests/contract/PipelineStartTests.cs`
- [X] T085 [P] [US2] Contract test `GET /api/listings/{id}/pipeline` status shape in `tests/contract/PipelineStatusTests.cs`
- [X] T086 [P] [US2] Contract test `PATCH /photos/{photoId}` overrides and the altered declaration in `tests/contract/PhotoPatchTests.cs`
- [X] T087 [P] [US2] Contract test `PUT /photos/order` reorder and cover selection in `tests/contract/PhotoOrderTests.cs`
- [X] T088 [P] [US2] Sidecar contract tests for `/quality` and `/embed` in `RealtorAI_BackEnd/vision-sidecar/tests/test_contract.py`
- [X] T089 [P] [US2] Integration test: with no enabled model the pipeline returns 503 and the listing stays fully completable by hand (SC-010) in `tests/integration/ManualFallbackTests.cs`
- [X] T090 [P] [US2] Integration test: near-duplicate detection flags the seeded living-room pair in `tests/integration/NearDuplicateTests.cs`
- [X] T091 [P] [US2] Integration test: checksums unchanged after a full analysis run in `tests/integration/AnalysisDoesNotMutateTests.cs`
- [X] T092 [P] [US2] E2E test: per-photo streaming, override, drag-reorder in `RealtorAI_FrontEnd/tests/integration/photo-analysis.spec.ts`

### Implementation for User Story 2

- [ ] T093 [US2] Implement sidecar `/quality` deterministic metrics (Laplacian variance, histogram, tilt, dimensions) in `RealtorAI_BackEnd/vision-sidecar/app/quality.py`
- [ ] T094 [US2] Implement sidecar `/embed` SigLIP2 embeddings in `RealtorAI_BackEnd/vision-sidecar/app/embed.py`, **asserting the returned dimension matches the `photo_embeddings` column width** (contracts/vision-sidecar.md)
- [ ] T095 [US2] Implement idempotent, resumable pipeline steps (normalize → quality → embeddings) in `Pipeline/Steps/`
- [ ] T096 [US2] Implement the per-photo VLM analysis step plus prompt `prompts/photo.analysis/v1.md` and `v1.schema.json`
- [ ] T097 [US2] Implement the cover-and-order suggestion step with a stated reason per suggestion in `Pipeline/Steps/CoverAndOrderStep.cs` (FR-010)
- [ ] T098 [US2] Implement pgvector near-duplicate detection with a configurable threshold in `Modules.Media/NearDuplicateDetector.cs` (flag only, never auto-delete)
- [ ] T099 [P] [US2] Build the photo grid with room labels, quality/content flags, dnd-kit reorder, and cover selection in `RealtorAI_FrontEnd/components/listing/PhotoGrid.tsx`
- [ ] T100 [P] [US2] Build the altered-photo declaration flow (type, disclosure label, paired original) in `RealtorAI_FrontEnd/components/listing/AlteredPhotoDialog.tsx` (FR-020)
- [ ] T101 [P] [US2] Build the live pipeline view consuming SignalR in `RealtorAI_FrontEnd/components/listing/PipelineProgress.tsx`, with per-photo queued/analyzing/done/failed states styled in **both themes** plus EN/TR keys

**Checkpoint**: US2 independently deliverable — photos are analyzed and agent-correctable

---

## Phase 5: User Story 3 — AI Listing Content Generation (Priority: P1)

**Goal**: RESO-aligned structured data plus public/private remarks, a headline, and 2–3 variants in a chosen tone — grounded only in facts with valid provenance.

**Independent Test**: From a draft with analyzed photos, generate content and confirm enumerated fields hold only permitted values, remarks fall within the configured limit, 2–3 variants exist, and any sentence resting on an unconfirmed AI fact is highlighted.

### Tests for User Story 3 (write first, confirm failing)

- [ ] T102 [P] [US3] Contract test `GET /api/listings/{id}/content` in `tests/contract/ContentListTests.cs`
- [ ] T103 [P] [US3] Contract test `POST /content/regenerate` including **409 when the CRMLS rule profile is incomplete** in `tests/contract/ContentRegenerateTests.cs`
- [ ] T104 [P] [US3] Contract test `PATCH /content/{draftId}` with `If-Match` and approval revocation in `tests/contract/ContentEditTests.cs`
- [ ] T105 [P] [US3] Integration test: remarks respect the configured limit and **fail closed** when it is unset in `tests/integration/RemarksLimitTests.cs`
- [ ] T106 [P] [US3] Integration test: generated content contains no measurement/tax/school/zoning claim lacking valid provenance in `tests/integration/GroundingTests.cs`
- [ ] T107 [P] [US3] Unit tests for RESO enumerated-value validation in `tests/unit/ResoEnumerationTests.cs`

### Implementation for User Story 3

- [ ] T108 [US3] Implement RESO field keys, enumerations, and the CRMLS lookup mapping for the eight Slice A fields in `Libs.Reso/` (research R3)
- [ ] T109 [US3] Author prompt `prompts/listing.content/v1.md` and `v1.schema.json` requiring per-claim supporting field ids
- [ ] T110 [US3] Implement `GenerateListingContentAsync` with tone and variant count in `Libs.Ai/Tasks/ListingContentTask.cs`
- [ ] T111 [US3] Implement grounding: persist `supporting_field_ids` and compute unconfirmed-claim spans in `Modules.Content/GroundingService.cs` (FR-014)
- [ ] T112 [US3] Implement content endpoints (list, regenerate, edit/select) in `Modules.Content/Endpoints/ContentEndpoints.cs`
- [ ] T113 [P] [US3] Build the variant picker and tone switcher in `RealtorAI_FrontEnd/components/listing/DescriptionVariantPicker.tsx`
- [ ] T114 [P] [US3] Build unconfirmed-claim highlighting in `RealtorAI_FrontEnd/components/listing/ClaimHighlighter.tsx`
- [ ] T115 [P] [US3] Add EN/TR keys for US3 and verify both themes

**Checkpoint**: US3 independently deliverable — MLS-ready content drafted and grounded

---

## Phase 6: User Story 4 — Compliance Review (Priority: P1)

**Goal**: Every draft and photo checked for Fair Housing, risky claims, CRMLS rules, AB 723 photo integrity, and advertising disclosure — with blocking findings that cannot be dismissed.

**Independent Test**: Submit text with known violations ("perfect for young families", "walking distance to church", "best schools", a phone number) plus an under-resolution photo; confirm each produces a finding with span, reason, source layer, and a compliant rewrite, and that blocking findings prevent approval.

### Tests for User Story 4 (write first, confirm failing)

- [ ] T116 [P] [US4] Contract test `POST /api/listings/{id}/compliance` in `tests/contract/ComplianceRunTests.cs`
- [ ] T117 [P] [US4] Contract test `GET /compliance` with severity/status filters in `tests/contract/ComplianceListTests.cs`
- [ ] T118 [P] [US4] Contract test `POST /compliance/{findingId}/apply-fix` in `tests/contract/ApplyFixTests.cs`
- [ ] T119 [P] [US4] Contract test acknowledge returning **422 for a blocking finding** in `tests/contract/AcknowledgeTests.cs`
- [ ] T120 [P] [US4] Golden unit tests for the Fair Housing lexicon (federal + California classes) in `tests/unit/FairHousingLexiconTests.cs`
- [ ] T121 [P] [US4] Unit tests for risky-claim detection (superlatives, guarantees, school claims, unverified measurements) in `tests/unit/RiskyClaimTests.cs`
- [ ] T122 [P] [US4] Unit tests for the CRMLS rule profile checks including fail-closed behaviour in `tests/unit/MlsRuleProfileTests.cs`
- [ ] T123 [P] [US4] Unit tests for AB 723 photo integrity, including that a non-staging alteration is **blocking** in `tests/unit/PhotoIntegrityTests.cs`
- [ ] T124 [P] [US4] Integration test: determinism — identical input yields identical findings (FR-024) in `tests/integration/ComplianceDeterminismTests.cs`
- [ ] T125 [P] [US4] Integration test: a blocking finding cannot be acknowledged at either the API or the database layer in `tests/integration/BlockingFindingTests.cs`
- [ ] T126 [P] [US4] E2E test: seeded violations surface with spans and rewrites in `RealtorAI_FrontEnd/tests/integration/compliance-review.spec.ts`

### Implementation for User Story 4

- [ ] T127 [US4] Author the versioned Fair Housing lexicon rule files in `Libs.Compliance/Lexicon/fair-housing.v1.json` (phrase/regex, protected class, severity, explanation, rewrite)
- [ ] T128 [US4] Implement the deterministic lexicon matcher in `Libs.Compliance/LexiconMatcher.cs`
- [ ] T129 [US4] Author prompt `prompts/compliance.judge/v1.md` + `v1.schema.json` and wire the judge at temperature 0 in `Libs.Compliance/JudgeChecker.cs`
- [ ] T130 [US4] Implement finding merge/dedupe across lexicon and judge with `source` attribution in `Libs.Compliance/FindingMerger.cs` (FR-017)
- [ ] T131 [P] [US4] Implement the risky-claim checker in `Libs.Compliance/RiskyClaimChecker.cs`
- [ ] T132 [US4] Implement the CRMLS rule engine in `Libs.Compliance/MlsRuleEngine.cs` — **raises a blocking "rule profile incomplete" finding on any missing value** (research R2)
- [ ] T133 [US4] Implement the AB 723 photo-integrity checker in `Libs.Compliance/PhotoIntegrityChecker.cs` (FR-020)
- [ ] T134 [P] [US4] Implement the advertising-disclosure checker in `Libs.Compliance/DisclosureChecker.cs` (FR-021)
- [ ] T135 [US4] Implement compliance endpoints with edit-scoped re-runs in `Libs.Compliance/Endpoints/ComplianceEndpoints.cs` (FR-025)
- [ ] T136 [P] [US4] Build the compliance panel with jump-to-span and one-click rewrite in `RealtorAI_FrontEnd/components/compliance/FindingList.tsx`
- [ ] T137 [P] [US4] Build the acknowledge-warning dialog in `RealtorAI_FrontEnd/components/compliance/AcknowledgeWarningDialog.tsx`, with severity distinguishable **without relying on colour alone**, WCAG AA in both themes, and EN/TR keys

**Checkpoint**: US4 independently deliverable — nothing unsafe can be approved

---

## Phase 7: User Story 5 — Single Review-and-Approve Screen (Priority: P1)

**Goal**: One screen holding data, photos, content, and findings, with inline editing, visible provenance, per-output approval, and an approval that can never record unreviewed content.

**Independent Test**: Open a fully drafted listing, confirm everything is reachable without leaving the page, edit an approved description and confirm it returns to draft and re-runs compliance on that item only.

### Tests for User Story 5 (write first, confirm failing)

- [ ] T138 [P] [US5] Contract test `GET /api/listings/{id}` detail aggregate with `approvalBlockers` in `tests/contract/ListingDetailTests.cs`
- [ ] T139 [P] [US5] Contract test `POST /approvals` success path recording approver, timestamp, and hash in `tests/contract/ApprovalTests.cs`
- [ ] T140 [P] [US5] Contract test `POST /approvals` 409 blockers including `content_changed_since_review` in `tests/contract/ApprovalBlockedTests.cs`
- [ ] T141 [P] [US5] Contract test audit-trail authorization (owner yes; broker admin only when owner membership is approved) in `tests/contract/AuditAccessTests.cs`
- [ ] T142 [P] [US5] Integration test: editing an approved item revokes approval and re-runs compliance **on that item only** in `tests/integration/ReapprovalTests.cs`
- [ ] T143 [P] [US5] Integration test: blind-approval guard — a stale review screen cannot approve (SC-011) in `tests/integration/BlindApprovalTests.cs`
- [ ] T144 [P] [US5] Integration test: concurrent edit returns 409 with current state and loses no edit (SC-012) in `tests/integration/ConcurrentEditTests.cs`
- [ ] T145 [P] [US5] E2E test: full Stories 1–5 walkthrough with the AI layer stubbed by deterministic fixtures in `RealtorAI_FrontEnd/tests/integration/review-and-approve.spec.ts`

### Implementation for User Story 5

- [ ] T146 [US5] Implement the `ListingDetail` aggregate endpoint in `Modules.Listings/Endpoints/ListingDetailEndpoint.cs`
- [ ] T147 [US5] Implement the approval service in `Modules.Content/ApprovalService.cs` — content hashing, blocker computation, revocation on edit
- [ ] T148 [US5] Implement the approval endpoint enforcing `reviewedContentHash` in `Modules.Content/Endpoints/ApprovalEndpoints.cs`, auditing mismatches as `approval_rejected_hash_mismatch` (FR-066)
- [ ] T149 [US5] Implement audit query endpoints (listing and brokerage, membership-scoped) in `Modules.Audit/Endpoints/AuditEndpoints.cs` (FR-050)
- [ ] T150 [P] [US5] Build the review screen shell with provenance badges and confirm actions at `RealtorAI_FrontEnd/app/dashboard/listings/[id]/page.tsx` (plus `loading.tsx` and `error.tsx`)
- [ ] T151 [P] [US5] Build the approve control rendering machine-readable blockers as human-readable disabled reasons in `RealtorAI_FrontEnd/components/approval/ApprovalBar.tsx`
- [ ] T152 [P] [US5] Build the audit trail view at `RealtorAI_FrontEnd/app/dashboard/listings/[id]/audit/page.tsx`
- [ ] T153 [P] [US5] Add EN/TR keys for US5 and verify WCAG AA in both themes, mobile width, and full keyboard reachability on the review screen

**Checkpoint**: US5 complete — Slice A delivers an approved, launch-ready listing

---

## Phase 8: Polish & Cross-Cutting Concerns

- [ ] T154 [P] Implement the FR-065 retention purge job in `RealtorAI_BackEnd/ops/retention-purge/` — **a separate ops task with its own credential, deliberately not a Hangfire job**, since the application identity must never hold delete rights on the originals bucket
- [ ] T155 [P] Integration test: the purge removes originals only past the retention window and no application role can delete them, in `tests/integration/RetentionPurgeTests.cs`
- [ ] T156 [P] Build the labeled photo evaluation set and assert room classification ≥90% accepted (SC-002) in `RealtorAI.Api/tools/AiEval/PhotoEval.cs`
- [ ] T157 [P] Build the labeled text evaluation set and assert Fair Housing recall ≥95% with ≤10% false positives (SC-003) in `RealtorAI.Api/tools/AiEval/ComplianceEval.cs`
- [ ] T158 [P] Add schema-validity rate, p90 latency (<2 min for 30 photos, SC-005), and cost-per-listing metrics to `RealtorAI.Api/tools/AiEval/`
- [ ] T159 Write the model-enablement runbook in `specs/002-ai-listing-launch/quickstart.md` §6 and flip registry entries to `enabled` **only after** Hub verification and a passing eval (research R1, VIII.4)
- [ ] T160 [P] Populate the CRMLS rule profile with **verified published values** via a new migration in `RealtorAI_BackEnd/supabase/migrations/` (research R2)
- [ ] T161 [P] Record counsel's or CRMLS compliance staff's confirmation of AB 723 disclosure label text and placement in `specs/002-ai-listing-launch/research.md` R4, then seed the approved labels (research R4)
- [ ] T162 [P] Add OpenTelemetry spans across every pipeline step in `Pipeline/Steps/`
- [ ] T163 [P] Add `loading.tsx` and `error.tsx` to every new route segment under `RealtorAI_FrontEnd/app/dashboard/`
- [ ] T164 [P] Add an automated accessibility suite (keyboard reach, accessible labels, WCAG AA contrast in both themes) covering every new screen, in `RealtorAI_FrontEnd/tests/integration/accessibility.spec.ts` — **reuse the existing accessible names the current e2e suite pins** so those tests keep passing
- [ ] T165 [P] Add an i18n completeness test asserting en/tr key parity and no hardcoded user-facing strings, in `RealtorAI_FrontEnd/tests/contract/i18n-parity.test.ts`
- [ ] T166 [P] Add the end-to-end performance validation for SC-001 (upload → approved under 15 min for a 30-photo listing) and SC-005 (analysis under 2 min p90) in `RealtorAI_FrontEnd/tests/integration/performance-slice-a.spec.ts`
- [ ] T167 [P] Refresh `specs/002-ai-listing-launch/quickstart.md` with the **actual** values reported by `supabase start`
- [ ] T168 [P] Dark/light screenshot pass over every new screen including empty, queued, analyzing, failed, blocking, and warning states
- [ ] T169 Record the final constitution compliance review (Principles I–VIII, Sections A–E, workflow gates) against v3.0.0 in the post-implementation Constitution Check of `specs/002-ai-listing-launch/plan.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)** — no dependencies; blocks everything
- **Foundational (Phase 2)** — depends on Setup; **blocks all user stories**
- **US1 (Phase 3)** — depends on Foundational
- **US2 (Phase 4)** — depends on Foundational + US1 (needs uploaded photos to analyze)
- **US3 (Phase 5)** — depends on Foundational + US1; **soft** dependency on US2 (richer grounding with photo analysis, but generates from agent-entered facts alone)
- **US4 (Phase 6)** — depends on Foundational + US3 (needs text to check); photo checks also need US2
- **US5 (Phase 7)** — depends on US1–US4 (aggregates all of their outputs)
- **Polish (Phase 8)** — depends on all user stories

### Critical path

```text
Setup → Foundational → US1 → US2 → US3 → US4 → US5 → Polish
```

Unlike a typical feature where P1 stories are parallel, Slice A's five stories form a **pipeline**:
each consumes the previous one's output. They remain independently *testable* (each has its own
checkpoint and acceptance criteria) but are not independently *deliverable* out of order.

### Within each phase

- Tests before implementation (Constitution Principle II — confirm failing first)
- Migrations are sequential (ordered DDL in one directory)
- Models → services → endpoints → frontend
- Frontend tasks marked [P] touch separate component files

### Gated — blocks go-live, not the build

- T156–T159 (evaluation harness) gate flipping any registry entry to `enabled`
- T160 (CRMLS values) gates compliance go-live; the engine fails closed until then
- T161 (AB 723 legal confirmation) gates the photo-disclosure control

---

## Parallel Execution Examples

**Phase 1 — project scaffolding:**

```text
T002, T003, T004, T005, T006 together (separate .csproj files)
then T008, T009, T010, T011, T012, T013 together
```

**Phase 2 — schema guarantee tests (all separate test files):**

```text
T026, T027, T028, T029, T030, T031, T032, T033 together
```

**Phase 2 — AI platform:**

```text
T048, T051, T052 together (separate files, after T044–T047)
```

**US1 — all contract and integration tests first:**

```text
T064, T065, T066, T067, T068, T069, T070, T071, T072 together
then T073 → T078 sequentially (shared modules)
then T079, T080, T081, T082, T083 together (separate frontend files)
```

**US4 — the largest parallel block:**

```text
T116, T117, T118, T119, T120, T121, T122, T123, T124, T125, T126 together
then T127 → T130, T132, T133, T135 sequentially; T131 and T134 in parallel
then T136, T137 together
```

---

## Implementation Strategy

### MVP scope

**Phases 1–3 (Setup + Foundational + US1) is the smallest shippable increment**: an agent can
complete their profile, declare a brokerage, create a listing, upload photos with originals
provably preserved, and resume the draft — with full authorization, audit, and concurrency
guarantees already in place. No AI is required for this to be useful or demonstrable.

Note that Foundational is unusually large (48 tasks) because Slice A's non-negotiables —
append-only audit, per-endpoint authorization, the model registry with its enablement gates, the
no-generative-imagery prohibition test, and optimistic concurrency — are all cross-cutting
guarantees that cannot be retrofitted per story.

### Incremental delivery

1. **Setup + Foundational** → stack runs, every schema guarantee provably enforced
2. **+ US1** → MVP: listings with immutable originals *(demo-able)*
3. **+ US2** → photos analyzed and agent-correctable
4. **+ US3** → MLS-ready content drafted and grounded
5. **+ US4** → nothing unsafe can be approved
6. **+ US5** → **Slice A complete**: approved, launch-ready, compliant listing
7. **+ Polish** → evaluation harness passing, retention job, verified CRMLS values

### AI-outage path is a first-class requirement

SC-010 means every story above must work with the AI layer unavailable. T089 tests exactly this,
and because the registry seeds every model `enabled = false`, **the manual path is the default
state during development** — the AI path is the one that needs enabling, not the fallback.

---

## Notes

- **Tests are mandatory here**, not optional: Constitution Principle II requires contract tests per
  endpoint and integration tests for cross-service flows, written first and confirmed failing.
- **Read `node_modules/next/dist/docs/` before any frontend task** — `RealtorAI_FrontEnd/AGENTS.md`
  requires it, and Next.js 16 breaks patterns from training data (`proxy` not `middleware`, async
  request APIs, `revalidateTag` arity). See research R8.
- **No task may introduce a raw hex colour or a `dark:` variant** — semantic `--color-*` tokens
  only, correct in both themes (Constitution Section E; standing project requirement).
- **No task may add an image/video-generation dependency.** T053 fails the build if one appears.
- **Every new table needs its RLS policy in the same migration**, and every endpoint needs an
  explicit policy — T038 fails the build otherwise.
- Commit per task or per logical group, referencing the feature and task ID (e.g.
  `[002-ai-listing-launch] T018: add listings and listing_fields migration`).
- Avoid: vague tasks, same-file conflicts within a [P] group, and skipping the failing-test step.
