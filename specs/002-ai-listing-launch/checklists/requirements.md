# Specification Quality Checklist: AI Listing Launch

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-10-12
**Last validated**: 2026-10-12 (pass 3 — all items pass)
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] No implementation details (languages, frameworks, APIs) — *see note 1*
- [x] Focused on user value and business needs
- [x] Written for non-technical stakeholders
- [x] All mandatory sections completed

## Requirement Completeness

- [x] No [NEEDS CLARIFICATION] markers remain — all 3 resolved in the 2026-10-12 session
- [x] Requirements are testable and unambiguous
- [x] Success criteria are measurable
- [x] Success criteria are technology-agnostic (no implementation details)
- [x] All acceptance scenarios are defined
- [x] Edge cases are identified
- [x] Scope is clearly bounded
- [x] Dependencies and assumptions identified

## Feature Readiness

- [x] All functional requirements have clear acceptance criteria
- [x] User scenarios cover primary flows
- [x] Feature meets measurable outcomes defined in Success Criteria
- [x] No implementation details leak into specification — *see note 1*

## Notes

### Note 1 — "No implementation details" is intentionally relaxed for this project

Per the RealtorAI constitution's Principle I (Specification-First Development, NON-NEGOTIABLE),
every spec MUST include Database Schema Changes, API Contracts, and Frontend Component Layout.
The "User Scenarios & Testing", "Requirements", and "Success Criteria" sections are kept
business-facing and technology-agnostic as this checklist requires; the technical detail required
by the constitution lives in the dedicated sections appended after Assumptions (Architecture &
Platform Decisions, AI Capability & Model Mapping, Database Schema Changes, Local Development &
Verification, API Contracts, Design Guidelines, Frontend Component Layout, Backend Project
Layout). This matches the precedent set by feature 001.

Two sections name specific technology by necessity rather than by leakage:

- **AI Capability & Model Mapping** records named Hugging Face model ids. The feature description
  designates these a *binding input* to planning, so dropping them would lose required
  information. They are quarantined in their own labeled section, and the corresponding
  requirements (FR-009 to FR-016, FR-051 to FR-059) are written model-agnostically, so the
  requirement set stays valid under any model swap.
- **Architecture & Platform Decisions** names CRMLS and the .NET Web API because the constitution
  requires the backend choice be stated in the spec, and because the clarification session
  resolved both. The requirements they constrain remain behaviour-level.

### Note 2 — Clarifications resolved (validation passes 1–3)

**Pass 1**: 14 of 15 items passed; the open-clarifications item failed. Two spec gaps were fixed
before pass 2 rather than deferred:

- The feature description's Section 5 (AI model mapping and serving constraints) had been omitted;
  since the description designates it a binding planning input, it was added as its own section.
- UI language versus generated-content language looked contradictory — the constitution mandates
  EN + TR for all user-facing strings, while this feature excludes "non-English listing content".
  Resolved in Assumptions: the exclusion covers only *generated listing and marketing output*
  (U.S. English, targeting U.S. MLSs and U.S. advertising law), while application chrome ships
  both locales. Not a conflict.

**Pass 2**: three clarifications held open deliberately (first target MLS; backend execution path;
brokerage membership and broker-admin access), each lacking a safe default.

**Pass 3**: all three answered by the project owner and encoded into the spec. All 15 items pass.

| Q | Answer | Requirements updated |
|---|--------|----------------------|
| Q1 | **CRMLS** as first and only target MLS | Story 6 retitled to CRMLS; FR-020 (AB 723 disclosure now active in the first release, not deferred behind state config, because CRMLS is a California MLS); FR-030 (CRMLS as first adapter); new Architecture section recording the vendor-approval schedule dependency and an explicit Principle III requirement to verify CRMLS's actual API surface. |
| Q2 | **C# .NET 8/9 Web API** | Constitutional Impact item 2 closed (the constitution already permits this option, so no amendment needed); API Contracts re-attributed per owner (.NET vs Route Handler vs Server Action); new Backend Project Layout section; new **FR-063** requiring per-endpoint authorization because the API holds a privileged service role, with RLS retained as defense in depth; Local Development extended to three local processes and service-role key isolation. |
| Q3 | **Self-declared + mandatory broker-admin approval** | FR-050 and FR-057 scoped to `approved` memberships; new **FR-060 to FR-062** (pending-by-default creation, what a pending agent may and may not do, approval/rejection auditing and first-admin bootstrap); new `brokerage_memberships` table replacing the `profiles.brokerage_id` column; new BrokerageMembership key entity; Story 1 scenarios 8–9 added; brokerage admin screens added to the layout. |

One consequence of Q3 is worth flagging because it was a judgment call rather than a direct
answer: a `pending` agent is **not** blocked from working. They can draft, analyze, generate,
compliance-check, and self-approve, since those need only their own license and state. What is
blocked is marketing-output approval and MLS submission, because both publish the brokerage's
name and license disclosures (FR-021), and advertising under an unverified brokerage is itself a
compliance violation. If the intent was instead to block listing creation outright until
approval, say so and FR-061 should be tightened.

Lower-impact unknowns were resolved as documented assumptions rather than consuming clarification
slots: public-record data provider (degrade to manual entry), seller consent capture (recorded
signed document plus attestation, since e-signature is out of scope), business-day calendar for
the Clear Cooperation clock, photo size and normalization bounds, near-duplicate threshold,
per-brokerage AI credentials, CRMLS API surface verification, session identity propagation to the
API, and the limitation of social channels to Instagram, Facebook, and LinkedIn.

### Note 3 — Constitutional conflicts CLEARED (was the last blocker)

Both conflicts are now resolved and `/speckit-plan` is unblocked:

- **AI vendor change** — cleared by **constitution v3.0.0** (amended 2026-10-12). The
  vendor-specific "Core AI Engine: OpenAI/Claude" rule was replaced with *Principle VIII — AI
  Layer: Model-Agnostic by Registry (NON-NEGOTIABLE)*, which names Hugging Face open-weight
  models as the default provider and makes model/provider changes a gated configuration change
  rather than an amendment. This feature's FR-051 registry requirement is now the
  constitutionally mandated shape rather than an exception to it.
- **Backend path** — cleared by the clarification session selecting the .NET Web API, an option
  the constitution already permitted. Principle VIII.1 now also *requires* the AI layer to live
  in the .NET Web API, which this spec already satisfies.

The amendment added four hard obligations that planning must honor, recorded in the spec's
"Constitutional Impact — Cleared" section: the VIII.4 model-enablement gates (commercial license
recorded, evaluation-harness pass, data-boundary check) which supersede FR-059's weaker wording;
the AI evaluation harness as a *foundational* deliverable that blocks the AI pipeline; the
mandatory automated test prohibiting any image/video-generation dependency (VIII.6), explicitly
outside Principle II's "pure UI may skip tests" allowance; and EXIF stripping for images leaving
the data boundary (VIII.4.c), which sharpens FR-056 and FR-002.

### Note 4 — Scope size

The spec covers 11 user stories across photo analysis, generation, compliance, MLS integration,
publishing, lifecycle automation, and a lead inbox. It is written in full as requested, with a
"Scope & Phasing Note" grouping the stories into three independently shippable slices (A: P1
stories 1–5; B: P2 stories 6–8; C: P3 stories 9–11). The recommendation is to plan Slice A first:
it is a complete sellable product with no third-party integration dependency, which matters more
now that CRMLS is the target — its vendor-approval timeline gates Slice B but not Slice A. The
decision to split or build whole belongs to the project owner.

### Status

All 15 checklist items pass and all constitutional conflicts are cleared (see Note 3). The spec
is **ready for `/speckit-plan`** against constitution v3.0.0.
