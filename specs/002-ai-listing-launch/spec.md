# Feature Specification: AI Listing Launch

**Feature Branch**: `002-ai-listing-launch`

**Created**: 2026-10-12

**Status**: Draft — clarifications resolved; constitutionally clear (constitution v3.0.0) and
ready for `/speckit-plan`

**Input**: User description: "AI Listing Launch — from photos and basic details to an approved,
compliant, multi-channel listing. An agent uploads the photos of a home plus the basic listing
details. The system uses AI to analyze the photos, fill in and draft everything a listing needs
(MLS-ready data, property description, marketing content), checks it all for legal and MLS-rule
compliance, shows it to the agent on a single review screen, and — after the agent approves —
publishes it to the MLS and then to marketing channels in the correct order. After launch, the
system keeps producing content as the listing status changes (open house, price change, pending,
sold) and helps the agent handle incoming buyer inquiries." Guiding position: "One upload, a
launch-ready and compliant listing — the agent stays in control." Includes 11 prioritized user
stories, a no-generative-imagery prohibition, a Hugging Face open-weight model mapping, and a
full audit trail.

## Clarifications

### Session 2026-10-12

- Q: Which MLS should receive the first integration adapter? → A: **CRMLS** (California, the
  largest US MLS). Chosen for maximum addressable market. Two consequences are now binding rather
  than conditional: California's AB 723 photo-disclosure rules apply to the first release (not
  just to agents who happen to be in a disclosure-requiring state), and CRMLS's stricter rule set
  and vendor-approval process are the baseline the MLS adapter and rule configuration are built
  against.
- Q: Should the AI/background-job/integration layer run as a C# .NET Web API or as Next.js Server
  Actions plus a managed job runner? → A: **C# .NET 8/9 Web API**, satisfying the constitution's
  requirement that the backend choice be stated in the spec. It owns the AI pipeline, durable
  background workers, compliance engine, and MLS/social adapters. No .NET project exists yet
  (`RealtorAI_BackEnd/` currently holds only the Supabase project), so standing one up is in
  scope for this feature.
- Q: How does an agent become a verified member of a brokerage, given that broker admins can read
  their agents' audit logs? → A: **Self-declared at profile completion, with mandatory broker-admin
  approval.** Membership starts `pending` and confers no brokerage-scoped access in either
  direction; only an `approved` membership exposes the agent's listings and audit trail to that
  brokerage's admins.

### Session 2026-10-13

- Q: What exactly may an agent with a `pending` brokerage membership do? → A: **Confirmed the
  existing FR-061 scope** — draft, analyze, generate, compliance-check, and self-approve *listing*
  content; marketing approval and MLS submission blocked. Rationale: brokerage identity appears
  only in marketing outputs and MLS submission, never in listing content (public remarks forbid
  brokerage names under FR-019), so this blocks the actual harm — advertising under an unverified
  brokerage — without stalling an agent awaiting their broker admin.
- Q: Who is the "platform administration" that may write the AI model registry and prompt
  versions? → A: A new **platform administrator** role, carried as a flag on the agent profile,
  with authenticated admin endpoints. **Hardening: no API path may ever set that flag** — it is
  settable only by direct database access, so it cannot become a privilege-escalation target.
  See FR-064.
- Q: How long are original photos retained, and what reclaims them after deletion? → A: Retain
  **3 years** after listing deletion, then purge via an out-of-band privileged operations job.
  Application roles still cannot delete, so FR-001's write-once guarantee is unchanged. The exact
  duration is folded into the AB 723 legal review already pending. See FR-065.
- Q: How are concurrent edits resolved between autosave, inline review editing, and the background
  pipeline? → A: **Optimistic concurrency** with a row version; a stale write is rejected with the
  current server state. Approval must additionally present the content hash the agent reviewed, and
  a mismatch rejects the approval — so a race becomes a visible rejection rather than an approval
  of content the agent never saw. See FR-066.
- Q: Can a listing be deleted, and what happens to its audit trail? → A: **Soft delete only** —
  `deleted_at` plus an `archived` status. Hard deletion is impossible without either destroying
  immutable audit records or having the audit foreign key block it, both of which contradict
  FR-049. See FR-067.

## Scope & Phasing Note

This specification covers 11 user stories spanning photo analysis, content generation,
compliance, MLS integration, multi-channel publishing, lifecycle automation, and a lead inbox.
That is substantially larger than a single deliverable increment, and two of the stories (MLS
submission, social publishing) depend on third-party authorization approvals whose timelines are
outside the team's control.

The specification is written in full as requested. To keep it buildable, the stories are grouped
into three delivery slices, each independently shippable and independently valuable:

| Slice | Stories | Delivers |
|-------|---------|----------|
| **Slice A (P1)** | 1–5 | Upload → AI analysis → drafted content → compliance review → approved listing, plus MLS-ready manual export. A complete, sellable product with no third-party integration dependency. |
| **Slice B (P2)** | 6–8 | MLS submission via delegated authorization, marketing pack, and the rule-aware publishing scheduler. |
| **Slice C (P3)** | 9–11 | Lifecycle content automation, AI-assisted lead inbox, and listing insights. |

**Recommendation**: treat Slice A as the scope of the first `/speckit-plan` and `/speckit-tasks`
run, and re-enter planning for Slices B and C. Slice A alone satisfies the product position
("one upload, a launch-ready and compliant listing") because Story 6 includes the MLS-ready
export fallback — an agent can launch a compliant listing end-to-end without any MLS or social
integration. This grouping is a planning recommendation; the decision to split or build whole
belongs to the project owner.

## Non-Negotiable Product Principles

These constrain every requirement below and are restated here because they are the feature's
acceptance backbone, not background context.

1. **Human-in-the-loop.** Nothing reaches the MLS, social media, email, or any public surface
   without explicit agent approval. Every AI output is a draft until approved.
2. **No generative imagery of any kind.** The system MUST NOT generate, synthesize, or
   generatively alter images or video frames. Prohibited: virtual staging, object
   removal/inpainting, sky or landscape replacement, digital renovation, generative
   upscaling/super-resolution, AI "enhancement" that invents pixels, AI-generated video or
   avatars. Allowed: non-generative operations only — resize, compress, format conversion, crop,
   rotate, and ordering/selection of the agent's real photos. Original uploaded files are always
   preserved unchanged.
3. **Never invent facts.** AI may only state facts sourced from (a) agent input, (b) public
   records, or (c) what is visibly present in the photos. Every listing field carries a
   provenance tag. Measurements (square footage, lot size), year built, taxes, HOA fees, school
   district, and legal/zoning facts are never inferred by AI.
4. **Compliance by design.** Fair Housing, MLS rules, Clear Cooperation timing, and state
   advertising rules are enforced by the system, not left to the agent's memory.
5. **Full auditability.** Every AI output, edit, compliance finding, approval, and publish action
   is logged with timestamp, user, model name/version, and input/output references.
6. **Model-agnostic AI layer.** All AI calls go through one internal AI service abstraction with
   a model registry, so any model can be swapped by configuration without changing business
   logic.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create a Listing Draft from Photos and Basic Details (Priority: P1)

A listing agent starts a new listing, uploads between 1 and 100 photos of the home, and enters
the property address, property type, list price, bedroom count, bathroom count, and free-text
notes. A listing draft is created that they can leave and resume later.

**Why this priority**: Every other story in this feature consumes the draft this story produces.
Without it there is nothing to analyze, draft, check, or publish.

**Independent Test**: Upload 30 mixed-format photos and enter the six basic detail fields,
navigate away, return, and confirm the draft is intact with all photos present, originals
byte-identical to what was uploaded, and public-record fields either pre-filled and tagged
`public_record` or empty and visibly marked as required.

**Acceptance Scenarios**:

1. **Given** an agent with a complete profile, **When** they upload photos in JPEG, PNG, HEIC, or
   WebP format, **Then** each original is stored unchanged (verifiable by checksum) and a
   normalized copy is created for processing.
2. **Given** an upload containing an unsupported file type or a file exceeding the per-file size
   limit, **When** the upload is attempted, **Then** that file is rejected with a specific,
   human-readable reason naming the file, and the remaining valid files still upload.
3. **Given** an agent uploads fewer than 1 or attempts more than 100 photos, **When** they
   proceed, **Then** the system states the 1–100 bound and blocks the out-of-bound action
   without discarding already-uploaded valid photos.
4. **Given** a property address is entered, **When** the draft is created, **Then** the system
   attempts to pre-fill public-record fields (square footage, lot size, year built, tax
   information) and tags each `public_record` with its source and retrieval date.
5. **Given** no public-record source is configured or the lookup returns no match, **When** the
   draft is created, **Then** those fields are left empty, visibly marked as required before
   publishing, and are never populated by AI inference.
6. **Given** an agent is mid-draft, **When** they stop interacting for the autosave interval or
   navigate away, **Then** all entered values and photo state persist without an explicit save
   action, and resuming restores the draft to its last state.
7. **Given** an agent whose profile lacks license number, state, or brokerage, **When** they
   attempt to create a listing, **Then** they are first routed through profile completion and
   returned to listing creation afterward.
8. **Given** an agent completing their profile, **When** they declare their brokerage, **Then** a
   brokerage membership is created with status `pending`, the agent is told approval by a broker
   admin is required, and the pending membership grants no brokerage-scoped access in either
   direction.
9. **Given** an agent whose brokerage membership is still `pending`, **When** they create a
   listing, **Then** drafting, analysis, generation, compliance, and self-approval all proceed
   normally, while marketing-output approval and MLS submission are blocked with a message naming
   the pending membership as the reason.

---

### User Story 2 - AI Photo Analysis (Priority: P1)

The agent's uploaded photos are analyzed automatically so they do not have to sort, label,
caption, and quality-check them by hand. Results stream in per photo as analysis completes.

**Why this priority**: Photo handling is the single largest manual time cost in listing prep and
the core of the product's time-saving claim (SC-001). It also supplies the visual evidence that
grounds Story 3's content generation.

**Independent Test**: Submit a 30-photo set containing known room types, a deliberately blurry
photo, a near-duplicate pair, and a photo with a visible face; confirm every photo receives a
room classification, feature tags, the expected quality and content flags, an alt-text caption, a
suggested cover photo with a stated reason, and a recommended order — and confirm by checksum
that no photo's pixels changed.

**Acceptance Scenarios**:

1. **Given** an uploaded photo, **When** analysis completes, **Then** it is classified as one of
   kitchen, living room, primary bedroom, bedroom, bathroom, exterior front, backyard, pool,
   garage, view, floor plan, or other.
2. **Given** an analyzed photo, **When** visible features are detected (e.g., hardwood floors,
   kitchen island, stainless appliances, fireplace, vaulted ceiling, pool), **Then** each is
   recorded as a candidate tag with provenance `ai_detected_from_photo` and remains unconfirmed
   until the agent confirms it.
3. **Given** a photo that is blurry, too dark, overexposed, has a tilted horizon, falls below the
   MLS minimum resolution, or is a near-duplicate of another photo, **When** analysis completes,
   **Then** the corresponding quality flag is raised and shown against that photo.
4. **Given** a photo containing visible people or faces, personal photographs or documents, house
   numbers, license plates, watermarks, or another brokerage's branding, **When** analysis
   completes, **Then** the corresponding content flag is raised and the photo is marked as
   requiring resolution before external publishing.
5. **Given** a photo that shows signs of prior digital alteration, **When** analysis completes,
   **Then** it is flagged for the agent to either confirm it is unaltered or declare it altered
   per Story 4's photo-integrity rules.
6. **Given** a completed photo set, **When** the agent opens the review screen, **Then** the
   system presents a suggested cover photo and a recommended display order (exterior front
   first, then a logical walk-through), each with a short stated reason.
7. **Given** any analysis result, **When** the agent disagrees, **Then** they can override the
   room classification, edit or remove feature tags, edit the caption, reorder photos by drag and
   drop, and delete photos — with each override recorded in the audit trail.
8. **Given** the AI provider is unavailable or analysis fails for a photo, **When** the agent
   views the review screen, **Then** the failure is shown per photo with a retry action and the
   agent can still classify, caption, order, and publish entirely by hand.

---

### User Story 3 - AI Listing Content Generation (Priority: P1)

MLS-ready listing content is drafted for the agent from the verified facts: structured listing
data, public remarks in a chosen tone, private remarks, a headline, and alternative versions.

**Why this priority**: Writing the listing is the second-largest manual time cost and the output
most agents judge the product by (SC-007). It depends on Stories 1 and 2 for its facts.

**Independent Test**: From a draft with confirmed photo analysis and complete basic details,
generate content and confirm the structured fields use RESO Data Dictionary field names with
valid enumerated values, the public remarks fall within the configured character limit, 2–3
description alternatives are offered, the chosen tone is reflected, and any sentence depending on
an unconfirmed `ai_suggested` fact is visibly highlighted.

**Acceptance Scenarios**:

1. **Given** a draft with analyzed photos, **When** content is generated, **Then** structured
   listing data is produced against RESO Data Dictionary field names (including
   `PublicRemarks`, `PrivateRemarks`, `InteriorFeatures`, `Appliances`, `Flooring`,
   `ParkingFeatures`, `PoolFeatures`, `View`), and every enumerated field contains only values
   permitted for that field.
2. **Given** a target MLS with a configured public-remarks character limit, **When** remarks are
   generated, **Then** the output is within that limit, and the limit is configurable per MLS
   without code changes.
3. **Given** the agent selects a tone of Professional, Warm, Luxury, or Concise, **When** content
   is generated, **Then** the output reflects the selected tone and the tone choice is recorded.
4. **Given** generated content, **When** the agent reviews it, **Then** 2–3 alternative versions
   of the description are available to choose from, and selecting one records the choice.
5. **Given** a private/agent remarks draft, **When** it is generated, **Then** showing
   instructions appear as explicit placeholders for the agent to complete rather than invented
   details.
6. **Given** any generated sentence, **When** it relies on a fact whose provenance is
   `ai_detected_from_photo` or `ai_suggested` and is not yet confirmed, **Then** that sentence is
   highlighted and the listing cannot be published until the underlying fact is confirmed or the
   sentence is removed.
7. **Given** generated content, **When** it is inspected for facts, **Then** it contains no
   measurement, year built, tax, HOA, school-district, or zoning claim that is not sourced from
   `agent_entered` or `public_record` provenance.
8. **Given** any generation request, **When** the model returns output that fails schema
   validation, **Then** it is retried once with the validation error, and on a second failure the
   agent sees an explicit failure state rather than partial or silently accepted content.

---

### User Story 4 - Compliance Review (Priority: P1)

Every draft — all text and all photos — is checked before the agent can approve it, with each
finding explaining the problem and offering a compliant fix.

**Why this priority**: This is the feature's primary risk control and a core differentiator. Fair
Housing and AB 723 exposure is the reason an agent would trust AI-drafted advertising at all;
shipping generation without it would be actively harmful.

**Independent Test**: Submit a draft containing known Fair Housing violations ("perfect for young
families", "walking distance to church", "exclusive neighborhood"), an unverifiable claim ("best
schools in the district"), a phone number inside public remarks, and an under-resolution photo;
confirm each produces a finding with the offending span, a reason, and a compliant rewrite, and
that blocking findings prevent publishing.

**Acceptance Scenarios**:

1. **Given** text containing a word or phrase that states or implies a preference or limitation
   based on race, color, religion, sex, handicap/disability, familial status, or national origin
   (plus configured state-level protected classes), **When** the compliance check runs, **Then** a
   Fair Housing finding is raised identifying the exact phrase span, the reason, and a compliant
   rewrite suggestion.
2. **Given** the Fair Housing check, **When** it evaluates text, **Then** it applies both a
   deterministic, maintainable phrase lexicon and a context-sensitive AI classifier, and the
   finding records which layer produced it.
3. **Given** text containing a superlative, guarantee, school-assignment claim, or a square
   footage figure not from a verified source (e.g., "best schools", "will appreciate",
   "flood-free"), **When** the check runs, **Then** a risky-claim finding is raised with a
   suggested substitute.
4. **Given** a target MLS configuration, **When** the check runs, **Then** it verifies remarks
   length, absence of contact information, URLs, and agent or brokerage names inside public
   remarks, presence of all required fields, minimum photo count, and minimum photo resolution —
   and each rule is configurable per MLS without code changes.
5. **Given** attached photos, **When** the integrity check runs, **Then** it confirms no
   system-generated or generatively altered image is attached.
6. **Given** an agent attaches a photo they altered elsewhere, **When** the listing's state
   configuration requires disclosure, **Then** the system requires an "altered" flag, a
   disclosure label (e.g., "Virtually Staged"), and the unaltered original placed adjacent in the
   photo order, and blocks publishing until all three are present.
7. **Given** any marketing output, **When** the check runs, **Then** the brokerage name and the
   license information required by the broker's state configuration are verified present.
8. **Given** a finding of blocking severity, **When** the agent attempts to publish, **Then**
   publishing is prevented and the agent must edit the content — a blocking finding can never be
   dismissed or overridden.
9. **Given** a finding of warning severity, **When** the agent acknowledges it, **Then** they
   must supply a reason, which is recorded in the audit trail with their identity and timestamp.
10. **Given** compliance checks, **When** they are executed, **Then** classification and judging
    calls run with deterministic settings so the same input yields the same findings.

---

### User Story 5 - Single Review-and-Approve Screen (Priority: P1)

The agent reviews everything — structured data, photos, description, marketing drafts, and
compliance findings — on one screen, edits inline, and approves per output and per channel.

**Why this priority**: This is where the agent exercises the control the product promises. The
first four stories produce drafts; without a single place to review and approve them, the agent
cannot discharge their legal responsibility and nothing can be published.

**Independent Test**: Open a fully drafted listing, confirm data, photos, content, marketing
drafts, and findings are all reachable without leaving the screen, edit an approved description,
and confirm it returns to draft status and re-runs compliance on that item only.

**Acceptance Scenarios**:

1. **Given** a drafted listing, **When** the agent opens the review screen, **Then** structured
   data, photos with their analysis, description variants, marketing drafts, and all compliance
   findings are presented in one place with each field's provenance visible.
2. **Given** any editable item, **When** the agent edits it inline, **Then** the change saves
   without a full page navigation and is recorded in the audit trail with before and after
   values.
3. **Given** a reviewed output, **When** the agent approves it, **Then** the approval is recorded
   against that specific output and channel with the approver's identity, timestamp, and a hash
   of the exact approved content.
4. **Given** an already-approved item, **When** the agent edits it, **Then** it returns to draft
   status, its approval is revoked, compliance re-runs on that item, and any dependent scheduled
   publish is blocked until re-approval.
5. **Given** unconfirmed AI-derived facts, **When** the agent attempts to approve content that
   depends on them, **Then** approval is prevented until each such fact is confirmed or removed.
6. **Given** the review screen, **When** it is used on a mobile-width viewport, in both light and
   dark themes, and by keyboard alone, **Then** every control is reachable, labeled, and legible.

---

### User Story 6 - MLS Submission on the Agent's Behalf (Priority: P2)

The agent connects their CRMLS account once, and the system submits the approved listing to the
MLS for them, surfaces validation errors in plain language, and syncs status changes back.
**CRMLS is the first and only target MLS in this feature** (see Architecture & Platform
Decisions); further MLSs are added later behind the same adapter interface.

**Why this priority**: High value but gated on CRMLS vendor approval and delegated-authorization
onboarding, timelines outside the team's control. The MLS-ready export in this story's fallback
keeps the product fully usable in the meantime, which is why this is P2 rather than P1.

**Independent Test**: Connect a CRMLS test account via delegated authorization, submit an
approved listing, confirm the external listing identifier and photo order/captions are recorded,
trigger a deliberate validation error and confirm it is shown in plain language mapped to the
field to fix, then change status in the MLS and confirm it syncs back.

**Acceptance Scenarios**:

1. **Given** an agent connecting their MLS account, **When** authorization is performed, **Then**
   it uses delegated authorization (OAuth 2.0 or the MLS's approved vendor mechanism), and the
   system never requests, stores, or transmits the agent's MLS username or password.
2. **Given** any MLS interaction, **When** it is performed, **Then** it uses the RESO Web API
   Add/Edit standard or the MLS's official listing-input API — never browser automation,
   scraping, or robotic process automation of an MLS web interface.
3. **Given** an approved listing, **When** it is submitted, **Then** the system can create the
   listing (as draft or Coming Soon where the MLS supports it), upload photos with their order
   and captions, update fields, and change status.
4. **Given** the MLS rejects a field, **When** the error returns, **Then** it is shown in plain
   language mapped to the specific field to fix, and the listing returns to an editable state
   without losing approved content for unaffected items.
5. **Given** a listing submitted to the MLS, **When** its MLS status changes (Active, Pending,
   Closed, or a price change), **Then** the change is synced back into the system and recorded.
6. **Given** an MLS authorization token that expires or is revoked mid-submission, **When** the
   submission is attempted, **Then** the agent is prompted to reconnect, the partial submission
   state is recorded, and no duplicate listing is created on retry.
7. **Given** an agent whose MLS has no available integration, **When** they finish approval,
   **Then** the system provides an MLS-ready export: every field formatted for copy-paste plus a
   downloadable archive of correctly sized, correctly ordered photos.
8. **Given** additional MLSs are added later, **When** a new MLS is integrated, **Then** it is
   added behind the same MLS-adapter interface without changing listing business logic.

---

### User Story 7 - Marketing Pack Generation (Priority: P2)

Once the listing is ready, the agent receives a complete marketing pack drafted from the same
verified facts, across social, email, print, web, and slideshow video.

**Why this priority**: Strong value multiplier on the core listing, but it depends on Stories 3
and 4 being solid first — marketing text carries the same legal exposure as the listing itself.

**Independent Test**: From an approved listing, generate the full pack and confirm each channel
output exists, draws only on approved facts, passes the same compliance checks as Story 4,
includes the required brokerage disclosure, and that the slideshow video contains only the
agent's real photos with deterministic transitions.

**Acceptance Scenarios**:

1. **Given** an approved listing, **When** the marketing pack is generated, **Then** it includes
   an Instagram "Just Listed" carousel (photo selection and order drawn from the real photos,
   plus caption and hashtags), a Reel/slideshow plan with caption, Story text, a Facebook post, a
   LinkedIn post, an email announcement with subject-line variants, printable flyer content, and
   single-property web page content with a shareable URL.
2. **Given** any marketing text, **When** it is generated, **Then** it passes the same Fair
   Housing, risky-claim, and advertising-disclosure checks as Story 4, and findings block
   approval of that specific output.
3. **Given** any marketing output, **When** it is produced, **Then** it includes the brokerage
   name and the license disclosures required by the broker's state configuration.
4. **Given** a slideshow video, **When** it is assembled, **Then** it uses only the agent's real
   photos with deterministic transitions, pans, and text overlays, and contains no generated,
   synthesized, or generatively altered frame.
5. **Given** the printable flyer, **When** it is produced, **Then** the layout comes from a fixed
   template populated with real photos and AI-drafted text, with no generated imagery.
6. **Given** marketing content referencing a fact, **When** that fact is not an approved listing
   fact, **Then** the content cannot be generated with it or it is highlighted as unconfirmed.

---

### User Story 8 - Publishing Scheduler That Respects Listing Rules (Priority: P2)

The agent schedules or one-click publishes approved content, and the system actively prevents
rule violations around public marketing timing.

**Why this priority**: This is the guard that makes Story 7 safe to use. It is P2 because it is
only reachable once there is approved marketing content to publish.

**Independent Test**: Record a seller marketing choice of delayed/office-exclusive and confirm
public marketing is blocked entirely until the allowed date; then switch to immediate marketing,
publish a public post, and confirm the one-business-day Clear Cooperation clock starts and
further public posts are blocked until MLS submission is completed or scheduled within the
window.

**Acceptance Scenarios**:

1. **Given** a new listing, **When** it is set up, **Then** the seller's marketing choice is
   captured as either immediate public marketing or delayed-marketing/office-exclusive, and a
   delayed/exempt choice requires the seller's signed consent to be recorded before any
   marketing action is permitted.
2. **Given** any public marketing action (social post, public web page, or email blast reaching
   multiple brokerages), **When** it is published, **Then** a one-business-day clock to MLS
   submission starts and is visible to the agent with its deadline.
3. **Given** the Clear Cooperation clock is running, **When** the agent attempts a further public
   post and MLS submission is neither completed nor scheduled within the allowed window, **Then**
   the post is blocked by default and the reason is stated.
4. **Given** a listing marked delayed-marketing or office-exclusive, **When** any public
   marketing is attempted, **Then** it is blocked entirely until the allowed date, regardless of
   approval status.
5. **Given** social publishing, **When** it occurs, **Then** it uses the platform's official API
   with the agent's own connected business account, and a revoked permission or rate limit
   surfaces as an actionable error with a retry path rather than a silent failure.
6. **Given** scheduled publish jobs, **When** the listing is withdrawn or its price changes,
   **Then** affected scheduled jobs are cancelled or held and the agent is told which outputs
   must be regenerated and re-approved.
7. **Given** a scheduled publish job, **When** its content's approval has been revoked by an
   edit, **Then** the job does not execute.

---

### User Story 9 - Lifecycle Content Automation (Priority: P3)

When the listing's status or details change, matching content is drafted automatically for the
agent's approval, with a suggested publish time.

**Why this priority**: Retention and ongoing value rather than first-launch value. Requires
Stories 7 and 8 to exist first.

**Independent Test**: Change a listing to each of open-house-scheduled, price-reduced,
back-on-market, pending, and sold, and confirm the matching content is drafted in draft status
with a suggested publish time and requires explicit approval before publishing.

**Acceptance Scenarios**:

1. **Given** an open house is scheduled, a price is reduced, the listing goes back on market,
   goes pending/under contract, or is sold, **When** the change is recorded, **Then** matching
   content ("Open House this Sunday", "Price Improvement", "Under Contract", "Just Sold") is
   drafted for that event.
2. **Given** lifecycle content is drafted, **When** it is created, **Then** it is in draft status,
   carries a suggested publish time, passes the same compliance checks, and is never published
   without explicit agent approval.
3. **Given** a price change, **When** it is recorded, **Then** previously approved marketing that
   states the old price is flagged as stale and must be regenerated and re-approved.

---

### User Story 10 - AI-Assisted Lead Inbox (Priority: P3)

Inquiries from the single-property page arrive in one inbox, classified and with grounded reply
drafts that answer only from approved listing facts.

**Why this priority**: Valuable but only meaningful once listings are live and generating
inquiries, which requires Slices A and B.

**Independent Test**: Submit inquiries covering a factual question, a showing request, a
negotiation question ("Is the seller flexible on price?"), a neighborhood-safety question, and
spam; confirm classification, contact and preferred-time extraction, a grounded draft for the
factual question only, and a polite agent handoff with no steering language for the rest.

**Acceptance Scenarios**:

1. **Given** an inquiry from the property page, **When** it arrives, **Then** it appears in a
   single inbox classified as showing request, question, financing, or spam, with contact details
   and any preferred times extracted into structured fields.
2. **Given** a factual question answerable from the listing's approved facts, **When** a reply is
   drafted, **Then** the draft answers only from those approved facts.
3. **Given** a question that is not answerable from approved facts — including seller motivation
   or price flexibility, school quality, or neighborhood safety — **When** a reply is drafted,
   **Then** the draft is a polite handoff to the agent containing no steering language, no
   Fair-Housing-risky characterization of an area or its residents, and no speculation.
4. **Given** auto-reply mode, **When** the agent has not changed settings, **Then** it is off by
   default, and when enabled it can send only replies to factual questions, never a handoff or a
   non-factual answer.
5. **Given** any reply, **When** it is sent, **Then** the send is recorded in the audit trail with
   whether it was agent-approved or auto-sent.

---

### User Story 11 - AI Insights and Recommendations (Priority: P3)

The agent sees a listing completeness and quality score with concrete, actionable suggestions.

**Why this priority**: Polish that improves outcomes on top of a working pipeline; nothing
depends on it.

**Independent Test**: Open a listing missing a primary-bathroom photo with a dark exterior shot
and a description omitting a kitchen renovation visible in the photos; confirm the score and all
three concrete suggestions appear, and confirm no pricing or valuation recommendation is offered
anywhere.

**Acceptance Scenarios**:

1. **Given** a listing draft, **When** insights are generated, **Then** a completeness/quality
   score is shown with concrete suggestions naming the specific gap (e.g., "Add a photo of the
   primary bathroom", "Exterior front photo is dark — consider reshooting", "Public remarks don't
   mention the renovated kitchen visible in photos 4–6").
2. **Given** features visible in photos but absent from the listing data, **When** insights are
   generated, **Then** they are offered as suggestions for the agent to confirm, never added
   automatically.
3. **Given** any insight output, **When** it is produced, **Then** it contains no pricing,
   valuation, or comparative-market-analysis recommendation.

---

### Edge Cases

- **Duplicate or near-identical photos**: flagged as near-duplicates; the agent chooses which to
  keep. Exact duplicates (matching checksum) are rejected at upload with a reference to the
  existing photo.
- **Photos of a different property uploaded by mistake**: when a photo's detected exterior or
  setting is inconsistent with the rest of the set, it is flagged as a possible mismatch for the
  agent to remove or confirm; the system does not delete it automatically.
- **Photos containing people, mail, prescription labels, or documents**: content-flagged and
  blocked from external publishing until the agent removes or replaces the photo. The system
  never edits the pixels to obscure them.
- **Interior-only uploads with no exterior photo**: cover-photo suggestion falls back to the best
  available interior photo and an insight states the exterior front photo is missing; MLS photo
  rules still apply.
- **Land or lot listings with no rooms**: room classification yields exterior, view, and lot
  categories only; room-dependent content and interior RESO fields are omitted rather than
  invented.
- **Extremely long agent notes containing contact information**: notes are accepted, but contact
  details are never carried into public remarks, and the MLS-rule check raises a blocking finding
  if any appear there.
- **Agent attempts to override a blocking compliance finding**: not permitted under any
  circumstance; the only path forward is editing the content. Warnings, by contrast, can be
  acknowledged with a logged reason.
- **MLS rejects a field after approval**: the listing returns to an editable state with the error
  mapped to the field; approvals for unaffected outputs survive, and the rejected submission is
  recorded.
- **MLS token expires mid-submission**: the submission halts, its partial state is recorded, the
  agent is prompted to reconnect, and retry does not create a duplicate MLS listing.
- **Social API rate limits or revoked permissions**: the publish job retries with backoff within
  limits, and on exhaustion or revocation surfaces an actionable error naming the account to
  reconnect.
- **Listing withdrawn while posts are scheduled**: all pending scheduled jobs for that listing are
  cancelled and the agent is shown what was cancelled.
- **Price changed after marketing was approved**: affected outputs are marked stale, their
  approvals are revoked, scheduled jobs are held, and regeneration plus re-approval is required.
- **AI provider outage**: analysis and generation degrade to an explicit failure state with retry;
  the agent can always complete, approve, and publish a listing entirely by hand.
- **Partial photo-analysis failure**: per-photo failures are isolated; the rest of the set
  completes and the listing remains workable.
- **Agent abandons a draft mid-analysis**: background jobs complete or time out and their results
  attach to the draft; no orphaned job blocks a later resume.
- **Same photo set uploaded to two listings**: permitted, but each listing holds its own analysis,
  order, and captions; cached analysis may be reused only for byte-identical images.
- **Two sessions editing one listing** (e.g. phone and desktop): the second write against a stale
  row version is rejected with the current server state and the agent is shown what changed; no
  edit is silently lost (FR-066).
- **Pipeline writes while the agent edits the same field**: the background job's write is versioned
  like any other, so whichever lands second is rejected and retried against fresh state; the
  agent's view refreshes rather than being overwritten mid-edit.
- **Content changes between review and approval**: the approval is rejected because the submitted
  content hash no longer matches, and the agent must re-review — an approval can never record
  content the agent did not see (FR-066, SC-008).
- **Agent deletes a listing**: soft-deleted only. It leaves the working views, the audit trail
  stays readable, and originals are purged 3 years later by a privileged job (FR-065, FR-067).
- **Agent deletes a photo**: the photo is detached from the listing but the original object is
  never removed, so FR-001 holds; reclamation happens only through the FR-065 retention purge.

## Requirements *(mandatory)*

### Functional Requirements

**Photo handling and integrity**

- **FR-001**: System MUST store every original uploaded photo unchanged, verifiable by checksum,
  and MUST NOT alter photo pixels with AI under any circumstance.
- **FR-002**: System MUST create normalized processing copies (resize, compress, format
  conversion, crop, rotate only) without modifying the stored original.
- **FR-003**: System MUST accept 1–100 photos per listing in JPEG, PNG, HEIC, and WebP formats,
  rejecting other types with a per-file, human-readable reason.
- **FR-004**: System MUST NOT generate, synthesize, or generatively alter any image or video
  frame, and MUST NOT integrate any image- or video-generation capability.

**Provenance and fact integrity**

- **FR-005**: System MUST tag every listing field with a provenance value of `agent_entered`,
  `public_record`, `ai_detected_from_photo`, or `ai_suggested`.
- **FR-006**: System MUST require explicit agent confirmation of every field tagged
  `ai_detected_from_photo` or `ai_suggested` before that field, or any content depending on it,
  can be published.
- **FR-007**: System MUST NOT infer square footage, lot size, year built, tax information, HOA
  fees, school district, or legal/zoning facts by AI; these accept only `agent_entered` or
  `public_record` provenance.
- **FR-008**: System MUST attempt public-record pre-fill from the property address when a source
  is configured, recording the source and retrieval date, and MUST leave those fields empty and
  marked required when no source is configured or no match is found.

**AI analysis and generation**

- **FR-009**: System MUST analyze each photo for room/area type, candidate visible features,
  quality issues (blur, exposure, tilt, sub-minimum resolution, near-duplicate), and content
  issues (people/faces, personal documents, house numbers, license plates, watermarks or foreign
  branding, signs of prior alteration).
- **FR-010**: System MUST suggest a cover photo and a recommended photo order with a stated
  reason for each suggestion, and MUST generate an alt-text caption per photo.
- **FR-011**: Users MUST be able to override every AI photo result — classification, tags,
  caption, order, cover selection — and delete photos, with each override audited.
- **FR-012**: System MUST generate structured listing data mapped to RESO Data Dictionary field
  names, using only permitted values for enumerated fields.
- **FR-013**: System MUST generate public remarks within the target MLS's configured character
  limit, in an agent-selected tone (Professional, Warm, Luxury, Concise), plus a private-remarks
  draft with explicit placeholders, a headline/tagline, and 2–3 description alternatives.
- **FR-014**: System MUST highlight any generated sentence that depends on an unconfirmed
  AI-derived fact and MUST block publishing of that content until the fact is confirmed or the
  sentence removed.
- **FR-015**: System MUST validate every AI structured output against an explicit schema, retry
  once with the validation error on failure, and surface an explicit failure rather than
  accepting invalid output.
- **FR-016**: System MUST produce all listing and marketing content in U.S. English.

**Compliance**

- **FR-017**: System MUST run Fair Housing checks on all text using both a deterministic phrase
  lexicon and a context-sensitive AI classifier, covering race, color, religion, sex,
  handicap/disability, familial status, national origin, and configured state-level protected
  classes, and MUST report each finding with its phrase span, reason, offending layer, and a
  compliant rewrite suggestion.
- **FR-018**: System MUST detect unverifiable or risky claims — superlatives, guarantees, school
  assignment claims, and square footage without a verified source — with a suggested substitute.
- **FR-019**: System MUST run per-MLS configurable rule checks covering remarks length, absence of
  contact information/URLs/agent or brokerage names in public remarks, required-field presence,
  minimum photo count, and minimum photo resolution.
- **FR-020**: System MUST verify no generatively altered image is attached, and MUST require an
  altered flag, a disclosure label, and the unaltered original placed adjacent for any
  agent-altered photo where the listing's state configuration requires disclosure. Because CRMLS
  (California) is the first target MLS, these disclosure requirements MUST be active in the
  initial release under California AB 723 rather than deferred behind configuration.
- **FR-021**: System MUST verify the brokerage name and state-required license information are
  present on every marketing output.
- **FR-022**: System MUST block publishing while any blocking-severity finding exists, and MUST
  NOT provide any mechanism to dismiss or override a blocking finding.
- **FR-023**: System MUST allow warning-severity findings to be acknowledged only with a recorded
  reason, actor, and timestamp.
- **FR-024**: System MUST run compliance and classification AI calls deterministically so
  identical input produces identical findings.
- **FR-025**: System MUST re-run compliance on any item that is edited after approval, and MUST
  revoke that item's approval.

**Approval and publishing**

- **FR-026**: System MUST require explicit per-output, per-channel agent approval before any
  publish action, recording approver identity, timestamp, and a hash of the approved content.
- **FR-027**: System MUST present structured data, photos and their analysis, content variants,
  marketing drafts, and compliance findings on a single review screen with inline editing and
  visible per-field provenance.
- **FR-028**: System MUST connect to MLSs only through delegated authorization (OAuth 2.0 or the
  MLS's approved vendor mechanism) and official APIs (RESO Web API Add/Edit or the MLS's official
  listing-input API).
- **FR-029**: System MUST NOT request, store, or transmit an agent's MLS username or password,
  and MUST NOT automate, script, or scrape any MLS web interface.
- **FR-030**: System MUST support create (including draft/Coming Soon where available), photo
  upload with order and captions, field update, and status change against the MLS, behind an
  MLS-adapter interface that permits adding further MLSs without changing listing logic. CRMLS
  MUST be the first implemented adapter.
- **FR-031**: System MUST present MLS validation errors in plain language mapped to the field to
  fix, and MUST NOT create a duplicate MLS listing on retry after a partial failure.
- **FR-032**: System MUST sync MLS-side status changes (Active, Pending, Closed, price change)
  back into the system.
- **FR-033**: System MUST provide an MLS-ready manual export — all fields formatted for
  copy-paste plus an archive of correctly sized and ordered photos — when no integration exists
  for the agent's MLS.
- **FR-034**: System MUST capture the seller's marketing choice (immediate public marketing, or
  delayed-marketing/office-exclusive) and MUST require a recorded signed seller consent before
  permitting marketing under a delayed/exempt choice.
- **FR-035**: System MUST start a one-business-day clock to MLS submission upon any public
  marketing action, display its deadline, and by default block further public posts until MLS
  submission is completed or scheduled within the allowed window.
- **FR-036**: System MUST block all public marketing for a listing marked delayed-marketing or
  office-exclusive until its allowed date, regardless of approval status.
- **FR-037**: System MUST publish to social platforms only via official platform APIs using the
  agent's own connected business accounts.
- **FR-038**: System MUST cancel or hold scheduled publish jobs when a listing is withdrawn, its
  price changes, or the underlying content's approval is revoked, and MUST tell the agent which
  outputs require regeneration and re-approval.

**Marketing and lifecycle**

- **FR-039**: System MUST generate a marketing pack from approved facts only, comprising
  Instagram carousel/Reel plan/Story, Facebook post, LinkedIn post, email announcement with
  subject-line variants, printable flyer content, and single-property page content with a
  shareable URL.
- **FR-040**: System MUST subject all marketing text to the same compliance checks as listing
  content, with findings blocking approval of that output.
- **FR-041**: System MUST assemble slideshow video only from the agent's real photos using
  deterministic transitions, pans, and text overlays.
- **FR-042**: System MUST draft lifecycle content on status or detail changes (open house, price
  reduction, back on market, pending/under contract, sold) in draft status with a suggested
  publish time, requiring approval before publishing.
- **FR-043**: System MUST mark previously approved marketing stale and revoke its approval when it
  states a price or detail that has since changed.

**Leads and insights**

- **FR-044**: System MUST answer buyer inquiries only from the listing's approved facts, and MUST
  hand off every other question to the agent without steering language, area or resident
  characterization, or speculation.
- **FR-045**: System MUST classify each inquiry (showing request, question, financing, spam) and
  extract contact details and preferred times into structured fields.
- **FR-046**: System MUST default auto-reply mode to off, and when enabled MUST restrict it to
  factual-question replies only.
- **FR-047**: System MUST provide a listing completeness/quality score with concrete, gap-naming
  suggestions, and MUST offer photo-derived feature suggestions for confirmation rather than
  adding them automatically.
- **FR-048**: System MUST NOT produce any pricing, valuation, or comparative-market-analysis
  recommendation.

**Audit, AI platform, and administration**

- **FR-049**: System MUST log every AI call (task, model identifier, prompt version, input and
  output references), every edit with before/after values, every compliance finding, every
  approval and revocation, and every publish action, with timestamp and actor, in an append-only
  audit log that cannot be updated or deleted by any application role.
- **FR-050**: Listing agents MUST be able to view the audit trail for their own listings, and
  broker admins MUST be able to view the audit trail for agents whose brokerage membership is
  `approved` — never for agents whose membership is `pending` or `rejected`.
- **FR-051**: System MUST route every AI call through a single internal AI service abstraction
  backed by a configuration-driven model registry (task → model → provider → parameters), so
  models can be swapped without code changes.
- **FR-052**: System MUST store AI system prompts as versioned assets and reference the exact
  prompt version in every audit entry.
- **FR-053**: System MUST run photo analysis and content generation as background jobs with
  visible progress, streaming per-photo results as they complete.
- **FR-054**: System MUST apply timeouts, retries with backoff, and fallback to the configured
  alternative model, and MUST always leave a path to complete the listing manually when AI is
  unavailable.
- **FR-055**: System MUST enforce a configurable per-listing AI token/image budget, cache
  identical requests, use embedding-based pre-filtering before vision-model calls, and record the
  AI cost per listing.
- **FR-056**: System MUST exclude agent credentials and seller personal data from AI prompts
  beyond what the task requires, and MUST flag photos with detected faces or documents before any
  external publishing.
- **FR-057**: Broker admins MUST be able to configure brokerage name, license information, logo,
  state, required disclaimers, and target MLS(s), and MUST NOT be able to configure a brokerage
  they are not an approved admin of.
- **FR-058**: System MUST require agent profile completion (license number, state, brokerage)
  before a listing can be created, reusing the existing authentication from feature 001.
- **FR-059**: System MUST evaluate any model against a labeled internal test set before it becomes
  active in the registry, recording the evaluation result against that registry entry.
- **FR-060**: System MUST create an agent's brokerage membership in `pending` status on
  self-declaration, and MUST require an approved broker admin of that brokerage to move it to
  `approved` before any brokerage-scoped data is readable in either direction.
- **FR-061**: System MUST permit an agent with a `pending` membership to draft, analyze, generate,
  compliance-check, and self-approve listing content, while blocking marketing-output approval and
  MLS submission until the membership is `approved`, since both depend on the brokerage's verified
  name and license disclosures.
- **FR-062**: System MUST audit brokerage membership approval and rejection with actor, timestamp,
  and outcome, and MUST grant the first registrant of a brokerage its initial broker-admin role,
  with subsequent admins promoted only by an existing admin.
- **FR-063**: Every .NET API endpoint MUST independently authorize the caller against listing
  ownership or approved brokerage membership, and MUST NOT rely on database row-level security as
  its only access control, because the API connects with a privileged service role. Row-level
  security MUST remain enabled on all tables as defense in depth for direct client reads.
- **FR-064**: System MUST define a **platform administrator** role that alone may write the AI
  model registry and prompt versions. The role MUST NOT be grantable or modifiable through any
  API path, Server Action, or user-facing surface — it is set only by direct database access — and
  every registry or prompt change MUST be recorded in the audit log with the acting administrator.
  Listing agents and broker admins MUST NOT be able to read or write the registry.
- **FR-065**: System MUST retain original photos for **3 years** after a listing is deleted, then
  purge them via a privileged out-of-band operations job. No application role may delete an
  original at any point (preserving FR-001), and the purge MUST be logged. The retention period
  MUST be configurable, and its legally required value is confirmed as part of the AB 723 review.
- **FR-066**: System MUST resolve concurrent writes with optimistic concurrency: listings, listing
  fields, and content drafts each carry a row version, and a write against a stale version MUST be
  rejected with the current server state rather than silently overwriting. Approval MUST require
  the content hash the agent reviewed, and MUST reject the approval when it does not match the
  current content — an agent can never approve content they did not see.
- **FR-067**: System MUST support soft deletion of listings only, via a `deleted_at` timestamp and
  an `archived` status: a deleted listing is hidden from the agent's working views, its audit
  trail remains intact and queryable, and its photo retention clock (FR-065) starts at
  `deleted_at`. System MUST NOT provide any hard-delete path, because deleting a listing row would
  either destroy immutable audit records or be blocked by their foreign key, both contradicting
  FR-049.

### Key Entities

- **Agent**: a licensed listing agent; extends the existing profile from feature 001 with license
  number and license state. Owns listings.
- **Brokerage**: the brokerage or team; holds name, license information, logo, state, required
  disclaimers, business calendar, and target MLS configuration. Scopes broker-admin visibility.
- **BrokerageMembership**: the link between an agent and a brokerage — role (agent or broker
  admin), status (`pending`, `approved`, `rejected`), who decided it and when. Only an `approved`
  membership confers brokerage-scoped access in either direction.
- **Listing**: the property being marketed; holds address, property type, list price, bed/bath
  counts, agent notes, lifecycle status, and target MLS. Parent of all drafts, photos, findings,
  and approvals.
- **ListingField**: one listing data point — RESO field name, value, provenance tag, confirmed
  flag, and who confirmed it when.
- **Photo**: an uploaded image — unchanged original reference, normalized variant references,
  checksum, display order, caption/alt text, cover flag, altered flag with disclosure label, and
  adjacent-original reference when altered.
- **PhotoAnalysis**: the AI result for one photo — room type, feature tags, quality flags, content
  flags, alt text, cover/order recommendation with reason, plus the model and prompt version that
  produced it.
- **ComplianceFinding**: one detected issue — check type, severity (blocking or warning), the
  target output and text span, the phrase, the reason, a suggested fix, resolution status, and the
  acknowledgement reason where applicable.
- **ContentDraft**: one generated output for one channel — channel, variant index, tone, body,
  version, and status (draft, approved, stale).
- **Approval**: an explicit agent approval of one output for one channel — approver, timestamp,
  content hash, and revocation state.
- **MlsConnection**: an agent's delegated authorization to one MLS — MLS key, token reference,
  granted scopes, expiry, and status. Never holds a username or password.
- **MlsSubmission**: one submission attempt — external listing identifier, status, field-mapped
  errors, and timestamps.
- **PublishJob**: a scheduled or immediate publish of one approved output to one channel —
  schedule, status, external post reference, error, and cancellation state.
- **SellerMarketingChoice**: the seller's instruction — immediate or delayed/office-exclusive,
  signed-consent reference, and the earliest allowed public-marketing date.
- **Lead**: an inbound inquiry — contact details, message, intent classification, extracted
  preferred times, status, and its reply drafts with send records.
- **AuditEvent**: one immutable record of an AI call, edit, finding, approval, or publish — actor,
  timestamp, event type, model identifier, prompt version, and input/output references.
- **AiModelRegistryEntry**: the active mapping of a task to a model — task key, model identifier,
  provider, parameters, license, verification date, and evaluation result.
- **PromptVersion**: a versioned system prompt for one AI task, referenced by audit entries.
- **AiUsageRecord**: per-listing AI consumption — task, model, tokens in/out, image count, and
  cost.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An agent goes from photo upload to an approved, launch-ready listing in under 15
  minutes for a 30-photo listing, against a typical manual baseline of 1–2 hours.
- **SC-002**: Room-type classification is accepted without change for at least 90% of photos on
  the internal labeled test set.
- **SC-003**: The Fair Housing check catches at least 95% of violations on the internal labeled
  test set, with a false-positive rate of 10% or less.
- **SC-004**: Zero publishes occur without a recorded explicit approval, and zero generatively
  altered images are ever produced by the system — both verified continuously by audit-log
  assertion.
- **SC-005**: Photo analysis for 30 photos completes in under 2 minutes at the 90th percentile.
- **SC-006**: AI cost per listing stays within the configured budget (target value to be set
  during planning) for at least 95% of listings.
- **SC-007**: At least 80% of beta agents rate the generated description "usable with minor edits
  or less".
- **SC-008**: Every published listing's audit trail can answer "what exactly did I approve and
  when?" — reconstructing the exact approved content for 100% of publishes via content hash.
- **SC-009**: Zero listings are published while a blocking compliance finding is open.
- **SC-010**: An agent can complete and publish a listing entirely by hand during a total AI
  outage, with no step requiring an AI result.
- **SC-011**: Zero approvals are recorded against content the agent did not actually review,
  verified continuously by audit-log assertion.
- **SC-012**: Zero edits are lost when two sessions, or a background job and an agent, write the
  same listing at once — the losing write is always surfaced to the agent rather than silently
  discarded.

## Assumptions

- **Existing authentication is reused.** Feature 001's sign-up/login and Supabase-backed profile
  remain the entry point; this feature adds profile completion (license number, state, brokerage)
  as a gate before listing creation rather than a new auth system.
- **UI language vs. content language.** The constitution (Principle VI) requires all user-facing
  UI strings to ship in English and Turkish. This feature's "non-English listing content" scope
  exclusion applies only to *generated listing and marketing output*, which is U.S. English
  because it targets U.S. MLSs and U.S. advertising law. The application chrome of every screen
  in this feature still ships EN + TR translation keys. These are not in conflict.
- **Dark mode and accessibility are baseline.** Per the constitution, every screen and state
  introduced here (including empty, loading, per-photo failure, and error states) is correct in
  both light and dark themes using the semantic `--color-*` tokens, meets WCAG AA contrast in
  both, and is keyboard reachable.
- **Public-record data source is not yet selected.** No provider is assumed. Until one is
  configured, public-record fields are agent-entered and marked required — the system degrades to
  manual entry rather than blocking listing creation. Selecting a provider is a planning decision.
- **Seller consent capture is a document record, not e-signature.** E-signature is out of scope,
  so a delayed/exempt marketing choice is satisfied by the agent recording an already-signed
  consent document plus an attestation (who, when, which document). The system stores and audits
  that record; it does not collect the signature.
- **"One business day" follows the brokerage's configured business calendar.** The Clear
  Cooperation clock excludes weekends and configured holidays for the brokerage's state.
- **Photo count and size bounds.** 1–100 photos per listing as specified; the per-file size limit
  and normalization target dimensions are configurable, set during planning to satisfy MLS
  minimum resolution while controlling vision-model token cost.
- **Near-duplicate detection is embedding-based with a configurable threshold**, surfaced as a
  flag for the agent rather than an automatic deletion.
- **MLS rule configuration is data, not code.** Remarks limits, required fields, photo minimums,
  and disclosure requirements are per-MLS and per-state configuration records, so adding an MLS or
  reacting to a rule change does not require a deployment. CRMLS's published rules are seeded as
  the first configuration record.
- **CRMLS's current API surface is unverified.** Whether CRMLS exposes RESO Web API Add/Edit to
  vendors, or only a proprietary listing-input API, MUST be confirmed during planning rather than
  assumed (Principle III). The adapter interface is designed so either answer is accommodated,
  and the MLS-ready export (FR-033) covers the case where vendor access is slower than expected.
- **Session identity propagation to the .NET API is a planning decision.** The mechanism for
  passing the authenticated agent's identity from the Next.js session to the Web API (e.g.
  forwarding the Supabase-issued JWT and validating it server-side) is deliberately left to
  `/speckit-plan`; what this spec fixes is the requirement that the API authorize every call
  independently (FR-063).
- **A single AI provider account serves all agents in the MVP.** Per-brokerage bring-your-own AI
  credentials are not assumed; the per-listing budget (FR-055) is the cost control.
- **Social channels are limited to Instagram, Facebook, and LinkedIn** as named in the stories; no
  other platform is assumed.
- **Photo-integrity detection is advisory, not forensic.** Detecting prior digital alteration is a
  best-effort signal that prompts an agent declaration (FR-020); the system does not claim
  forensic certainty, and the agent's declaration plus disclosure is the compliance control.
- **Beta agent cohort exists for SC-007.** A recruited cohort of beta agents is available to
  produce the satisfaction measurement; absent a cohort, SC-007 is measured at first beta rather
  than at feature completion.

## Constitutional Impact — Cleared

Both previously blocking conflicts with the RealtorAI Constitution are now resolved. This feature
is constitutionally clear for `/speckit-plan`.

1. **AI vendor change — RESOLVED by constitution v3.0.0 (2026-10-12).** The constitution
   previously named the Core AI Engine as "OpenAI API (GPT-4o / Vision API) and Claude 3.5 Sonnet
   API" and made any vendor change an amendment, which blocked this feature's Hugging Face
   open-weight mapping. That rule was replaced with **Principle VIII — AI Layer: Model-Agnostic by
   Registry (NON-NEGOTIABLE)**, which names Hugging Face open-weight models as the default
   provider and makes model/provider selection a gated configuration change rather than an
   amendment. This feature's `AiService` + registry requirement (FR-051) is now the
   constitutionally mandated shape, not an exception to it.
2. **Backend/API path — RESOLVED, no amendment needed.** The constitution requires the choice
   between "C# .NET 8/9 Web API" and "Next.js Server Actions/Route Handlers" be "stated in the
   feature spec, not left implicit". This spec selects the **.NET 8/9 Web API** (see Architecture
   & Platform Decisions), an option the constitution already permits. Principle VIII.1 now
   additionally *requires* the AI layer to live in the .NET Web API, which this spec already
   satisfies.

### New obligations this feature inherits from Principle VIII

Carried into planning as hard gates rather than recommendations:

- **Model enablement gates (VIII.4)**: every model in the AI Capability & Model Mapping section
  may be enabled only with its commercial-use license recorded in its registry entry, a passing
  run against the AI evaluation harness for its task (accuracy, JSON-schema validity, latency,
  cost), and a data-boundary check. This supersedes FR-059's weaker "evaluate before activation"
  wording by adding the license and data-boundary conditions.
- **AI evaluation harness is a foundational deliverable**, not a later refinement — no model may
  be marked enabled until it exists, so it blocks the AI pipeline work. Its thresholds are
  defined in this feature's `plan.md`.
- **Automated prohibition test (VIII.6)**: a test that fails if any image/video-generation
  dependency or endpoint is present in the codebase is mandatory, and is explicitly not covered
  by the "pure UI may skip tests" allowance in Principle II.
- **EXIF stripping (VIII.4.c)**: images sent outside the approved data boundary must be
  downscaled and EXIF-stripped. This sharpens FR-056 and FR-002 — the normalized copy used for
  vision calls strips EXIF, while the immutable original retains it.

## Architecture & Platform Decisions

Resolved in the clarification session above; recorded here because each one constrains multiple
requirements.

### First target MLS: CRMLS

CRMLS is the first and only MLS adapter in scope for this feature. Consequences:

- **AB 723 is in scope from the first release.** Because CRMLS is a California MLS, the
  altered-photo disclosure requirements of FR-020 are active for every listing in the initial
  release rather than gated on a per-state configuration flag. The state-configuration mechanism
  still exists (other states will differ), but California is the implemented baseline and its
  rules are the stricter path the UI and compliance engine are designed against.
- **CRMLS rule configuration is the reference data set.** Remarks character limit, prohibited
  content in public remarks, required fields, minimum photo count, and minimum photo resolution
  are populated from CRMLS's published rules as the first `brokerage_mls_configs` record.
- **Vendor approval is a schedule dependency, not a technical one.** CRMLS's vendor-approval and
  delegated-authorization onboarding process gates Story 6 going live. Slice A is unaffected and
  Story 6's MLS-ready export (FR-033) remains the fallback path, so the product is fully usable
  before approval completes. Planning MUST confirm CRMLS's current listing-input API surface and
  whether RESO Web API Add/Edit is available to vendors, per Principle III — it is not assumed.

### Backend/API execution path: C# .NET 8/9 Web API

Satisfies the constitution's requirement that this choice be explicit in the spec.

- **The .NET Web API owns**: the `AiService` abstraction and model registry, the photo-analysis
  and generation background workers, the compliance engine (lexicon + judge), the MLS adapter
  interface and CRMLS adapter, social publishing adapters, the publish scheduler, and the
  append-only audit writer.
- **Next.js owns**: all UI, session handling, and reads that can be served directly from Supabase
  under RLS. Route Handlers remain the surface for third-party webhooks and OAuth callbacks per
  the constitution.
- **No .NET project exists yet.** `RealtorAI_BackEnd/` contains only the Supabase project, so
  creating the Web API solution is a foundational task in this feature.
- **Authorization boundary (important).** Where the .NET API connects to Postgres with a
  privileged service role, RLS is no longer the effective enforcement boundary for those calls.
  Therefore: every .NET endpoint MUST independently authorize the caller against listing
  ownership or approved brokerage membership, and RLS MUST remain enabled on every table as
  defense in depth for direct client reads. Neither layer alone is sufficient, and planning MUST
  record how the agent's identity is propagated from the Next.js session to the .NET API.

### Brokerage membership: self-declared, broker-admin approval required

- An agent selects or enters their brokerage during profile completion; the membership is created
  with status `pending`.
- A `pending` membership grants **no** brokerage-scoped access in either direction: the agent
  gains no brokerage configuration access, and the brokerage's admins can see neither the agent's
  listings nor their audit trail.
- A broker admin of that brokerage must approve the membership to move it to `approved`, which is
  the only state that exposes the agent's listings and audit trail to brokerage admins (FR-050).
  Approval and rejection are both audited with actor and timestamp.
- A `pending` agent is **not** blocked from working: they can create listings, run analysis,
  generate content, run compliance, and approve their own outputs, because those require only
  their own license and state. Actions that depend on brokerage data — the required brokerage
  name and license disclosures on marketing outputs (FR-021), and MLS submission under the
  brokerage's CRMLS configuration — are blocked until the membership is approved, since using an
  unverified brokerage's name in advertising is itself a compliance violation.
- The first user to register a brokerage becomes its first broker admin; subsequent admins are
  promoted by an existing admin.

## AI Capability & Model Mapping — Binding Input for Planning

Recorded verbatim-in-substance from the feature description because it is a **binding input** to
`/speckit-plan`, not a suggestion. It is deliberately kept out of the Requirements section above
so the business-facing requirements stay technology-agnostic: the requirements state *what* the
AI must do and guarantee (FR-009 to FR-016, FR-051 to FR-059); this section records *which
models* planning starts from.

All AI access goes through one internal `AiService` with a configuration-driven model registry
(task → model id → provider → parameters), per FR-051.

| Stage | Task | Recommended default | Why | Fallback |
|---|---|---|---|---|
| Photo analysis | Room classification, feature detection, quality/content flags, cover & order suggestions, alt text — one structured JSON per photo | `Qwen/Qwen3-VL-8B-Instruct` | Strong image understanding, structured JSON output, Apache-2.0, small enough to self-host | `Qwen/Qwen2.5-VL-7B-Instruct` |
| Photo embeddings | Near-duplicate detection, fast zero-shot room-type pre-classification | `google/siglip2-base-patch16-224` | Fast, cheap image–text embeddings; avoids sending every check to the VLM | `openai/clip-vit-base-patch32` |
| Photo technical quality | Blur, exposure, resolution, tilt | **No model** — deterministic image processing (Laplacian variance, histogram, EXIF/size checks) | Cheaper, explainable, deterministic | — |
| Listing text generation | Structured RESO field drafting, public/private remarks, headlines, variants | `Qwen/Qwen3-30B-A3B-Instruct-2507` | MoE: high quality at low active-parameter cost, reliable JSON/instruction following, Apache-2.0 | `mistralai/Mistral-Small-3.2-24B-Instruct-2506` |
| Marketing copy | Social captions, hashtags, email, flyer text, lifecycle posts | Same as listing text generation | One model for consistent voice; lower ops cost | Same fallback |
| Compliance — Fair Housing & risky claims | Phrase lexicon (deterministic) + LLM-as-judge returning findings with span, category, reason, rewrite | Lexicon + `Qwen/Qwen3-30B-A3B-Instruct-2507` (separate system prompt, temperature 0) | Lexicon is auditable; the judge catches context the lexicon misses | Later: fine-tuned classifier on `answerdotai/ModernBERT-base` once labeled data exists |
| Lead inbox | Intent classification, entity extraction, grounded reply drafting | `Qwen/Qwen3-30B-A3B-Instruct-2507` with retrieval over the listing's approved facts only | Grounding prevents invented answers | `Qwen/Qwen3-8B` for classification-only |
| Retrieval | Embeddings for listing facts and templates | `BAAI/bge-m3` | Strong multilingual embeddings, MIT | `Qwen/Qwen3-Embedding-0.6B` |
| Image/video generation | — | **NONE — prohibited** | Product principle 2 | — |

**Verification gate (Principle III).** During `/speckit-plan`, every model id above MUST be
verified on the Hugging Face Hub for (1) current availability, (2) a license permitting
commercial use (prefer Apache-2.0/MIT), and (3) availability on the chosen serving option. A
clearly better model in the same family may replace a default, recorded as a plan decision. No
model id is treated as confirmed on the strength of this table alone.

**Serving path.** MVP uses Hugging Face Inference Providers (OpenAI-compatible
chat-completions via the HF router) for development speed; the production path is Hugging Face
Inference Endpoints (dedicated, region-pinned) or self-hosted TGI/vLLM for data control and
predictable cost. This choice is configuration, not code.

**Operational requirements** are stated as testable requirements above rather than repeated
here: schema-validated structured outputs with one retry (FR-015), temperature 0 for compliance
and classification (FR-024), versioned prompt assets referenced in the audit log (FR-052),
background pipeline with streamed per-photo results (FR-053), timeouts/retries/model fallback
plus a manual path (FR-054), per-listing budget with caching and embedding pre-filtering
(FR-055), prompt privacy and face/document flagging (FR-056), and pre-activation evaluation
against a labeled test set (FR-059).

**Note**: this mapping is constitutionally compliant as of constitution v3.0.0, which names
Hugging Face open-weight models as the default provider under Principle VIII. Each model id here
is a *candidate* — it becomes usable only once its registry entry records a commercial-use
license, it passes the AI evaluation harness for its task, and its data-boundary check succeeds
(Principle VIII.4).

## Database Schema Changes

All tables carry Row Level Security per Principle VII. The access-control model below assumes
verified brokerage membership per the resolved model: **only an `approved` membership confers
brokerage-scoped access** (FR-060). Per FR-063, RLS is defense in depth — the .NET API connects
with a privileged service role and MUST enforce the same rules independently at every endpoint.

**Extended from feature 001**

- `profiles` — add `license_number`, `license_state`, `profile_completed_at`, and
  `platform_admin` (boolean, default false). Agent reads/writes own row only, **except
  `platform_admin`, which no application role may write** — it is set exclusively by direct
  database access (FR-064). Brokerage linkage lives in `brokerage_memberships` rather than a
  `brokerage_id` column, because membership now carries a status and an approver.

**New tables**

| Table | Purpose | Access-control model |
|-------|---------|----------------------|
| `brokerages` | Name, license info, logo reference, state, required disclaimers, business calendar | Approved members read; approved broker admins of that brokerage write |
| `brokerage_memberships` | Agent, brokerage, role (agent / broker_admin), status (`pending` / `approved` / `rejected`), requested_at, decided_by, decided_at | Agent reads own row; approved broker admins of that brokerage read all rows and decide pending ones. A `pending` or `rejected` row confers no access in either direction |
| `brokerage_mls_configs` | Target MLS per brokerage (CRMLS first), remarks limits, required fields, photo minimums, disclosure rules | Approved members read; approved broker admin writes |
| `listings` | Address, property type, list price, beds/baths, notes, lifecycle status, target MLS | Owning agent full access; broker admin read only where the owner's membership is `approved` |
| `listing_fields` | RESO field name, value, provenance, confirmed flag/by/at | Via parent listing |
| `photos` | Original + normalized references, checksum, order, caption/alt text, cover flag, altered flag, disclosure label, adjacent-original reference | Via parent listing |
| `photo_analyses` | Room type, feature tags, quality flags, content flags, alt text, cover/order reason, model id, prompt version | Via parent listing; insert by service role only |
| `photo_embeddings` | Vector embedding for near-duplicate detection and room pre-classification | Service role only |
| `compliance_findings` | Check type, severity, target + span, phrase, reason, suggested fix, status, acknowledgement reason/by/at | Via parent listing; insert/resolve by service role, acknowledge by owning agent |
| `content_drafts` | Channel, variant, tone, body, version, status | Via parent listing |
| `approvals` | Target output, channel, approver, timestamp, content hash, revoked_at | Via parent listing; insert by owning agent only |
| `mls_connections` | Agent, MLS key, delegated-token reference, scopes, expiry, status — never a password | Owning agent only; token material stored outside the row |
| `mls_submissions` | Connection, external listing id, status, field-mapped errors | Via parent listing |
| `publish_jobs` | Channel, content draft, schedule, status, external post reference, error, cancelled_at | Via parent listing |
| `seller_marketing_choices` | Choice, signed-consent document reference, attestation, earliest allowed public date | Via parent listing |
| `leads` | Contact details, message, intent class, extracted fields, status | Via parent listing |
| `lead_replies` | Draft body, status, approver, sent_at, auto_sent flag | Via parent listing |
| `audit_events` | Actor, listing, event type, model id, prompt version, input/output references, payload | Append-only: insert allowed, **no update or delete policy for any application role**; owning agent reads always, broker admin reads only where the owner's membership is `approved` (FR-050) |
| `ai_model_registry` | Task key, model id, provider, parameters, license, verified_at, evaluation result, active flag | Read by service role; write requires `profiles.platform_admin` (FR-064). Never readable by agents or broker admins |
| `prompt_versions` | Task key, version, system prompt, active flag | Read by service role; write requires `profiles.platform_admin` (FR-064) |
| `ai_usage_records` | Listing, task, model, tokens in/out, image count, cost | Via parent listing; insert by service role |

**Indexes**: `listings(agent_id)`, `listings(brokerage_id)`, `listings(status)`,
`brokerage_memberships(agent_id)`, `brokerage_memberships(brokerage_id, status)`,
`listing_fields(listing_id)`, `photos(listing_id, order_index)`,
`photo_analyses(photo_id)`, `compliance_findings(listing_id, severity, status)`,
`content_drafts(listing_id, channel)`, `approvals(listing_id, target_id)`,
`publish_jobs(scheduled_at, status)`, `leads(listing_id, status)`,
`audit_events(listing_id, created_at)`, `ai_usage_records(listing_id)`, plus a vector index on
`photo_embeddings`.

**Storage buckets** (all private, accessed by signed URL): `listing-photos-original`
(write-once; no update or delete policy for application roles, enforcing FR-001),
`listing-photos-normalized`, `listing-media` (slideshow video output), `brokerage-assets`
(logos), `seller-consents`.

## Local Development & Verification

Per the constitution's local-first rule, all schema work is developed and verified against the
local Supabase stack before any hosted project is touched.

- Migrations live in `RealtorAI_BackEnd/supabase/migrations/` alongside feature 001's
  `0001_profiles.sql`, applied locally via the Supabase CLI (this project invokes it through
  `npx`).
- The local Postgres connection values for SQL-client inspection (e.g., DBeaver) MUST be taken
  from the actual `supabase start` output rather than assumed, and recorded in this feature's
  `quickstart.md` during `/speckit-plan`.
- Verification includes connecting a SQL client and confirming: every new table exists with RLS
  enabled, the `audit_events` and `listing-photos-original` write-once policies reject update and
  delete, the provenance and severity enumerations match this spec, and a `pending`
  brokerage membership exposes no brokerage-scoped rows in either direction.
- The new .NET Web API runs locally alongside the Next.js app and the local Supabase stack.
  `quickstart.md` MUST document all three processes, the API's local base URL, how the Next.js app
  is pointed at it, and how agent identity is propagated from the Next.js session to the API.
- Because the API holds privileged database credentials, local setup MUST keep its service-role
  key in server-only configuration with no `NEXT_PUBLIC_` exposure, per the constitution's
  sensitive-data isolation rule.

## API Contracts

Ownership follows the resolved backend decision: the **.NET 8/9 Web API** owns the AI pipeline,
background workers, compliance engine, and MLS/social adapters; **Next.js Route Handlers** own
third-party webhooks and OAuth callbacks; simple UI-local reads and form posts may remain Server
Actions where no worker or adapter is involved. Every input is validated against an explicit
schema at the boundary that receives it, and every .NET endpoint authorizes the caller
independently per FR-063.

**Listing lifecycle** (.NET, except profile completion): complete agent profile (Server Action) ·
create listing draft · autosave draft · upload photos (returns per-file accept/reject) · reorder
photos · set cover photo · delete photo · override photo analysis · confirm AI-derived field ·
update listing fields · record seller marketing choice.

**AI pipeline** (.NET): start photo analysis (background) · stream/poll per-photo analysis
progress · generate listing content (tone, variant count) · select description variant ·
regenerate single output · generate marketing pack · generate lifecycle content · compute listing
insights.

**Compliance and approval** (.NET): run compliance check (listing or single item) · fetch findings
· acknowledge warning finding with reason · approve output for channel · revoke approval.

**MLS** (.NET, callbacks in Next.js): initiate CRMLS delegated authorization · authorization
callback (Route Handler) · submit listing · fetch submission status and field-mapped errors · sync
status from MLS (scheduled worker) · MLS status webhook (Route Handler) · generate MLS-ready
export archive.

**Publishing** (.NET, callbacks in Next.js): connect social account (delegated authorization +
Route Handler callback) · schedule publish job · cancel publish job · execute due publish jobs
(scheduled worker) · Clear Cooperation clock state.

**Leads**: submit inquiry from public property page (Route Handler, public and rate-limited) ·
list inbox · draft reply (.NET) · approve and send reply (.NET) · toggle factual auto-reply.

**Audit and administration** (.NET): fetch listing audit trail · fetch brokerage audit trail
(broker admin, approved memberships only) · read/update brokerage configuration · read/update MLS
rule configuration.

**Brokerage membership**: declare brokerage at profile completion (creates `pending`) · list
pending membership requests (broker admin) · approve or reject membership (broker admin) · promote
an approved member to broker admin · read own membership status.

## Design Guidelines

- The review-and-approve screen is the feature's centerpiece and follows the constitution's
  UI/UX standards: generous whitespace, clear typographic hierarchy, soft elevation, and photos
  treated as first-class large visual elements with consistent aspect-ratio cropping — not dense
  admin-template tables.
- All spacing, color, radius, and type values come from the semantic `--color-*` and scale tokens
  in `RealtorAI_FrontEnd/app/globals.css`. No raw hex values, no direct `--light-*`/`--dark-*`
  palette references, and no `dark:` variants.
- New components reuse the existing `Button`, `Card`, `Input`, `TextField`, `Alert`, and
  `Checkbox` primitives from feature 001 before any new primitive is introduced.
- Compliance findings use a consistent severity visual language — blocking versus warning must be
  distinguishable without relying on color alone, and both must meet WCAG AA contrast in light
  and dark themes.
- Provenance is always visible on the review screen: an agent can tell at a glance which values
  they entered, which came from public records, and which the AI derived and still need
  confirmation.
- Long-running analysis uses per-photo progressive states (queued, analyzing, done, failed) rather
  than one blocking spinner, with every state styled in both themes.
- All user-facing strings are i18n keys in both `en.json` and `tr.json`; no hardcoded text.

## Frontend Component Layout

```text
RealtorAI_FrontEnd/
├── app/
│   ├── dashboard/
│   │   ├── profile/complete/page.tsx       # License, state, brokerage declaration (FR-058/060)
│   │   ├── brokerage/
│   │   │   ├── page.tsx                    # Brokerage config: name, license, logo,
│   │   │   │                               # disclaimers, CRMLS settings (FR-057)
│   │   │   ├── members/page.tsx            # Pending/approved members, approve & reject (FR-060)
│   │   │   └── audit/page.tsx              # Brokerage audit trail (FR-050)
│   │   └── listings/
│   │       ├── page.tsx                    # Listing index
│   │       ├── new/page.tsx                # Upload + basic details (Story 1)
│   │       └── [id]/
│   │           ├── page.tsx                # Review & approve screen (Story 5)
│   │           ├── loading.tsx             # Required per constitution
│   │           ├── error.tsx               # Required per constitution
│   │           ├── marketing/page.tsx      # Marketing pack (Story 7)
│   │           ├── publish/page.tsx        # Scheduler + Clear Cooperation (Story 8)
│   │           ├── leads/page.tsx          # Lead inbox (Story 10)
│   │           └── audit/page.tsx          # Listing audit trail (FR-050)
│   ├── p/[slug]/page.tsx                   # Public single-property page + inquiry form
│   ├── api/                                # Route Handlers only: CRMLS + social OAuth
│   │                                       # callbacks, MLS status webhook, public inquiry
│   └── actions/                            # Server Actions: profile completion, membership
│                                           # declaration, simple UI-local posts
├── components/
│   ├── listing/                            # PhotoUploader, PhotoGrid (drag-reorder),
│   │                                       # PhotoAnalysisCard, ProvenanceBadge,
│   │                                       # FieldConfirmToggle, ToneSelector,
│   │                                       # DescriptionVariantPicker
│   ├── compliance/                         # FindingList, FindingCard, SeverityBadge,
│   │                                       # RewriteSuggestion, AcknowledgeWarningDialog
│   ├── approval/                           # ApprovalBar, PerChannelApproval, StaleBadge
│   ├── marketing/                          # ChannelPreview, CarouselPlanner, FlyerPreview,
│   │                                       # SlideshowPlanner
│   ├── publish/                            # ScheduleForm, ClearCooperationClock,
│   │                                       # SellerChoiceForm
│   ├── brokerage/                          # MembershipStatusBanner, PendingMemberList,
│   │                                       # ApproveMemberDialog
│   └── leads/                              # InboxList, LeadCard, ReplyDraftEditor
└── lib/
    ├── api/                                # Typed client for the .NET Web API + session
    │                                       # identity propagation
    └── validation/listing.ts               # Client-side schemas for every listing input
```

## Backend Project Layout

New .NET solution, created in this feature alongside the existing Supabase project.

```text
RealtorAI_BackEnd/
├── supabase/
│   └── migrations/                         # 0001_profiles.sql (001) + this feature's migrations
└── RealtorAI.Api/                          # NEW — .NET 8/9 Web API solution
    ├── Endpoints/                          # Listings, Photos, Ai, Compliance, Approvals,
    │                                       # Mls, Publishing, Leads, Audit, Brokerage
    ├── Ai/                                 # AiService abstraction, model registry,
    │                                       # prompt-version store, structured-output validation
    ├── Compliance/                         # Fair Housing lexicon (deterministic) + judge,
    │                                       # risky-claim, MLS-rule, photo-integrity,
    │                                       # disclosure checks
    ├── Reso/                               # RESO field names + enumerated value maps
    ├── Mls/                                # IMlsAdapter + CrmlsAdapter (first implementation)
    ├── Publishing/                         # Social adapters, scheduler, Clear Cooperation clock
    ├── Imaging/                            # Non-generative ops only: resize, compress, convert,
    │                                       # crop, rotate; deterministic quality metrics
    ├── Workers/                            # Photo-analysis fan-out, generation, publish jobs,
    │                                       # MLS status sync
    ├── Audit/                              # Append-only audit writer
    └── Authorization/                      # Per-endpoint ownership + approved-membership checks
```

## Out of Scope

- Any image or video generation or generative editing — virtual staging, inpainting, object
  removal, sky replacement, digital renovation, generative upscaling, AI "enhancement", AI video
  or avatars.
- Automated pricing, valuation, or comparative market analysis.
- Paid advertising campaigns. If added later, they must respect the housing Special Ad Category
  restrictions of the ad platform.
- Transaction management — offers, escrow, e-signature collection.
- Buyer-side agent workflows.
- Storing or using MLS usernames and passwords; scraping or automating MLS web interfaces.
- Scraping consumer portals (Zillow, Realtor.com).
- Non-English generated listing or marketing content (UI chrome remains EN + TR — see
  Assumptions).
- Social DM ingestion into the lead inbox (property-page inquiries only in this feature).
