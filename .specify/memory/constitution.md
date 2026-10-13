<!--
Sync Impact Report
===================
Version change: 2.0.0 → 3.0.0
Modified principles:
  - Technology Stack "Core AI Engine" bullet REMOVED as a vendor naming rule. It previously
    read "OpenAI API (GPT-4o / Vision API) and Claude 3.5 Sonnet API for multi-modal
    generation" and bound the project to two named vendors. It is replaced by a pointer to the
    new Principle VIII, which governs model selection by registry rather than by vendor name.
    This is the MAJOR trigger: a stack constraint is redefined, and prior guidance that
    "changing the AI vendor is a constitution amendment" no longer holds — vendor/model changes
    are now routine configuration changes subject to the Principle VIII gates.
  - Section D renamed: "AI Engine Operations (OpenAI / Claude Multi-Modal)" →
    "AI Engine Operations (Model-Agnostic Multi-Modal)". Its two existing rules (deterministic
    JSON outputs, token & timeout management) are unchanged in substance; vendor-specific
    examples were generalized and a cross-reference to Principle VIII added.
Added sections:
  - Core Principles: "VIII. AI Layer: Model-Agnostic by Registry (NON-NEGOTIABLE)" — seven
    rules covering the AiService abstraction, the model registry, default provider, model
    enablement gates (license / evaluation harness / data boundary), schema-validated outputs
    with versioned prompts, the no-generative-imagery prohibition with an enforcing automated
    test, and AI-output-is-always-a-draft human approval.
Removed sections: None
Rationale: requested by the project owner on 2026-10-12. Feature 002 (AI Listing Launch)
  mandates open-weight Hugging Face models, which the previous vendor-specific rule prohibited
  without an amendment. Rather than swap one vendor name for another, the rule is replaced with
  vendor-neutral governance so future model changes are configuration, not constitutional
  amendments.
Scope note (narrowing to be aware of): Principle VIII.1 requires all AI calls to go through the
  AiService inside the .NET Web API. The Technology Stack still permits Next.js Server
  Actions/Route Handlers as a feature's backend path, but a feature that makes AI calls now
  MUST stand up or use the .NET Web API for that layer. This is intentional per the owner's
  wording and matches feature 002's resolved architecture.
Templates requiring updates:
  - .specify/templates/plan-template.md — ✅ no change needed (Constitution Check is
    "[Gates determined based on constitution file]", resolved dynamically at plan time)
  - .specify/templates/spec-template.md — ✅ no change needed (stack-agnostic; verified to
    contain no AI vendor, model, or engine references)
  - .specify/templates/tasks-template.md — ✅ updated: Phase 2 (Foundational) gains two
    principle-driven foundational task examples (model-registry/AiService wiring and the
    generative-imagery prohibition test) because VIII.6 mandates an automated enforcement test
    that is NOT optional, whereas the template's per-story test sections are marked optional
  - .claude/skills/speckit-*/SKILL.md — ✅ reviewed, no AI vendor references found, no changes
  - README.md / docs/ — ✅ not present in this repository, nothing to update
  - specs/002-ai-listing-launch/spec.md — ✅ its "Constitutional Impact" blocker is cleared by
    this amendment; the spec's AI model mapping and FR-051 registry requirement are now
    compliant. Its model ids remain subject to the VIII.4 enablement gates at plan time.
  - specs/001-auth-base-dashboard/spec.md — ✅ no change needed (no AI surface)
Follow-up TODOs:
  - TODO(AI_EVAL_HARNESS): Principle VIII.4.b requires an AI evaluation harness with accuracy,
    JSON-schema-validity, latency, and cost thresholds. The harness does not exist yet; its
    thresholds are to be defined in feature 002's plan.md and the harness built as a
    foundational task there. Until it exists, no model may be marked enabled in the registry.
  - TODO(GENERATIVE_DEPENDENCY_TEST): VIII.6's automated test prohibiting image/video-generation
    dependencies and endpoints must be implemented in feature 002's foundational phase.
  - TODO(DARK_MODE_REGRESSION_GATE): carried over from 2.0.0, still unresolved — dark/light
    parity is verified by review and manual screenshots, not CI.
  - TODO(GIT_REMOTE_AUTOMATION): carried over from 1.1.0, still unresolved — the automated
    commit & push workflow is policy-only, not yet wired to an executing hook/script.

--- Previous report (1.3.0 → 2.0.0) ---
Version change: 1.3.0 → 2.0.0
Modified principles: None renamed; one rule in Section E REVERSED, which is why this is a
  MAJOR bump rather than a MINOR one:
  - "Light mode is the default and required baseline; dark mode support is optional per
    feature and MUST NOT be assumed available" → replaced by "Dark mode is a project-wide
    baseline, not a per-feature option", plus a companion rule requiring WCAG AA contrast to
    be verified in BOTH themes. Prior specs written under the old rule (001) remain valid;
    001's UI was brought into compliance in the same pass.
  - "Design system consistency" bullet: corrected the token location from the non-existent
    `tailwind.config.ts` to `RealtorAI_FrontEnd/app/globals.css` (Tailwind v4 is configured
    CSS-first), and added the semantic-token rule that makes dark mode automatic.
Rationale: requested by the project owner on 2026-10-12 as a standing rule for all work
  before and after that date, which directly contradicted the previous bullet.
Added sections: None
Removed sections: None
Templates requiring updates:
  - .specify/templates/plan-template.md — ✅ no change needed (Constitution Check gate pulls
    these rules dynamically)
  - .specify/templates/spec-template.md — ✅ no change needed (stack-agnostic)
  - .specify/templates/tasks-template.md — ✅ no change needed
Follow-up TODOs:
  - TODO(DARK_MODE_REGRESSION_GATE): dark/light parity is currently verified by review and
    manual screenshots. Consider a Playwright pass that drives both themes so a future
    hard-coded colour fails CI rather than review.

--- Previous report (1.2.0 → 1.3.0) ---
Version change: 1.2.0 → 1.3.0
Modified principles: None renamed or redefined
Added sections:
  - Modern Coding Principles & Architecture: new subsection
    E. UI/UX Design Standards (Real Estate SaaS)
  - Development Workflow & Quality Gates: new "Local-first development phase" bullet
    (localhost + local Supabase DB stand-up, DBeaver/SQL-client inspection guidance)
Removed sections: None
Templates requiring updates:
  - .specify/templates/plan-template.md — ✅ no change needed (Technical Context /
    quickstart.md already carry local-run instructions per feature; Constitution Check gate
    pulls the new rules dynamically)
  - .specify/templates/spec-template.md — ✅ no change needed (generic, stack-agnostic)
  - .specify/templates/tasks-template.md — ✅ no change needed (Setup/Foundational phases
    already include "Setup database schema and migrations framework"; this now concretely
    means the local Supabase stack per the new workflow bullet)
  - .claude/skills/speckit-*/SKILL.md — ✅ reviewed, no changes required
  - specs/001-auth-base-dashboard/spec.md — ✅ updated in the same pass to add local-dev/
    DBeaver verification and UI design requirements to the first live feature
Follow-up TODOs:
  - TODO(GIT_REMOTE_AUTOMATION): carried over from 1.1.0, still unresolved — the automated
    commit & push workflow is policy-only, not yet wired to an executing hook/script.
-->

# RealtorAI Constitution

## Core Principles

### I. Specification-First Development (NON-NEGOTIABLE)

No implementation code is written before a formal `spec.md` (and, where required, `plan.md`
and `tasks.md`) exists for the feature, has been reviewed, and reflects an agreed User Story,
Database Schema Changes, API Contracts (endpoints/payloads), Frontend Component Layout, and
Edge Cases. When a request arrives to build a new feature, the first deliverable is always the
`[feature-name]/spec.md`, never source code. Specs are the source of truth; code that
contradicts an approved spec is a bug in the code, not the spec.

**Rationale**: RealtorAI ships fast across many small features (upload, generation, export,
billing). Specifying before coding prevents scope drift, keeps AI-assisted generation
grounded in reviewed intent, and gives a durable artifact for future contributors instead of
tribal knowledge.

### II. Test-First Discipline

For every feature that implements business logic, an API contract, or a data mutation: tests
are written first, reviewed, confirmed to fail, and only then is implementation written
(Red-Green-Refactor). Contract tests are mandatory for every API endpoint (Next.js Route
Handlers/Server Actions and any .NET Web API surface). Integration tests are mandatory for
cross-service flows (e.g., upload → AI generation → export, or subscription tier → usage
limit enforcement). Pure UI presentation with no logic may skip tests only when explicitly
noted as such in the feature's `tasks.md`.

**Rationale**: The core product value (AI-generated listings, scripts, and posts) depends on
correctness of multi-step pipelines (Vision AI parsing, tiered usage limits, payments). Tests
written after the fact tend to validate what the code does rather than what it should do.

### III. Verified Dependencies, No Hallucinations (NON-NEGOTIABLE)

If a dependency, SDK/API version (e.g., current OpenAI, Claude, Stripe, or Lemon Squeezy SDK
syntax), or configuration detail is ambiguous or not confidently known, work MUST halt and the
user must be asked to clarify or supply current documentation. Guessing at API shapes,
package names, or config keys is prohibited. Any assumption made in the absence of
confirmation MUST be recorded explicitly in the spec's Assumptions section, not silently
embedded in code.

**Rationale**: This project integrates multiple fast-moving external APIs (OpenAI, Claude,
Stripe/Lemon Squeezy, Supabase). A hallucinated method signature or deprecated endpoint fails
silently in production billing or AI generation — both are unacceptable for a paid B2B
product.

### IV. Production-Ready Code Only (NON-NEGOTIABLE)

No `// TODO: implement later`, no `/* ... logic here ... */`, no structural placeholders, no
stub functions left unimplemented. Every function, endpoint, and component delivered MUST be
fully articulated, handle its stated edge cases, and be robust enough to ship. If a task is too
large to complete fully in one pass, it MUST be split into smaller fully-completed tasks in
`tasks.md` rather than landing as a partial implementation.

**Rationale**: The developer profile for this project is a senior engineer shipping a
commercial product, not a prototype. Half-finished code compounds silently and erodes trust in
AI-assisted output; catching it at generation time is far cheaper than catching it in review.

### V. Clean, Modular & Simple Architecture

Code MUST follow SOLID principles, proper TypeScript interfaces (or equivalent typed contracts
in C#), and async best practices. Modules must have a single, clear purpose — no
organizational-only files. At the same time, YAGNI applies: do not build abstractions,
configuration layers, or extensibility points for requirements that are not in the current
spec. Prefer three similar lines over a premature abstraction. Complexity (an extra project,
a new layer of indirection, a design pattern) MUST be justified against a real, current
requirement, not a hypothetical future one.

**Rationale**: Speed-to-market and long-term maintainability both fail if the codebase is
either a tangle of ad hoc scripts or an over-engineered framework nobody can extend quickly.
Balancing SOLID with YAGNI keeps the codebase professional without slowing feature delivery.

### VI. Global-First Internationalization

Hardcoded user-facing strings are strictly banned. All user-facing text MUST flow through the
project's i18n system, with English as the default locale and Turkish as the secondary locale
from day one. New features MUST add translation keys for both locales before the feature is
considered complete; a feature that ships with an English-only string in the UI is not done.

**Rationale**: RealtorAI targets a global B2B market of real estate agents. Retrofitting i18n
after hardcoding strings is expensive and error-prone; enforcing it from the first commit
keeps the cost near zero.

### VII. Security & Data Privacy by Default

User authentication, property data, uploaded photos, and payment information MUST be handled
with least-privilege access, encrypted transport, and provider-recommended security defaults
(Supabase Row Level Security policies for all tables containing user or listing data, signed
URLs for storage access, no secrets committed to the repository or logged in plaintext).
Payment and subscription logic MUST treat tier/usage limits as server-enforced, never
client-trusted. Any new table, storage bucket, or endpoint that touches user or client data
MUST state its access-control model explicitly in the feature's spec.

**Rationale**: RealtorAI is a paid B2B SaaS handling real estate agents' client and property
data alongside billing information. A single RLS misconfiguration or client-trusted usage
check directly threatens both customer trust and revenue integrity.

### VIII. AI Layer: Model-Agnostic by Registry (NON-NEGOTIABLE)

1. No business code may reference a specific AI vendor, model id, or endpoint. All AI calls MUST
   go through a single backend `AiService` abstraction inside the .NET Web API.
2. Models are selected per task through a **model registry** (task → provider → model id →
   endpoint → parameters → fallback → enabled). Changing a model or provider is a configuration
   change, recorded in the audit log, never a code change.
3. Default provider: open-weight models from the Hugging Face Hub, served via Hugging Face
   Inference Providers for development/MVP and via Hugging Face Inference Endpoints or
   self-hosted TGI/vLLM for production. Other providers (including OpenAI or Anthropic) are
   permitted only as registry entries that pass the same gates.
4. A model MAY be enabled in the registry only if:
   a. its license permits commercial use and is recorded in the registry entry;
   b. it passes the project's AI evaluation harness for its task (accuracy, JSON-schema
      validity, latency, and cost thresholds defined in the feature's plan);
   c. it does not send data outside the approved data-handling boundary — no seller PII, no
      agent credentials, and downscaled, EXIF-stripped images only.
5. Every AI output MUST be validated against a versioned JSON Schema before it is stored.
   Prompts are versioned files in the repository, and their version/hash is logged with every
   call.
6. **No generative imagery.** The system MUST never generate or generatively alter images or
   video — virtual staging, inpainting, object removal, sky replacement, digital renovation,
   generative upscaling, or AI video. Only non-generative operations (resize, compress, format
   conversion, crop, rotate, order, select) are allowed on photos. Original uploads are
   immutable. Any dependency or endpoint capable of image/video generation is prohibited in the
   codebase, enforced by an automated test.
7. AI output is always a draft: nothing AI-produced reaches the MLS or any public channel
   without explicit, logged human approval.

**Rationale**: The AI landscape moves faster than this codebase can be refactored, and the
previous rule — naming OpenAI and Claude as the engine — made every model change a
constitutional amendment while still permitting vendor lock-in inside business logic. Routing
all calls through one abstraction with a gated registry inverts that: swapping a model becomes
configuration, while the things that actually carry risk (commercial licensing, measured
quality, the data boundary, schema-valid output, prompt provenance) become hard gates that no
model can bypass. Rules 6 and 7 are legal controls, not architectural preferences: California
AB 723 (effective 2026-01-01) makes undisclosed digitally altered listing images a criminal
offense, MLSs such as CRMLS prohibit most alterations, and the agent and their broker — not the
software — are legally responsible for every piece of advertising they publish.

## Technology Stack & Platform Constraints

- **Frontend**: Next.js (App Router), React, TypeScript, Tailwind CSS.
- **Backend/API**: C# .NET 8/9 Web API, or Next.js Server Actions/Route Handlers where a
  feature's spec explicitly chooses that path — the choice MUST be stated in the feature spec,
  not left implicit. **Exception**: a feature with an AI surface MUST route those calls through
  the `AiService` in the .NET Web API per Principle VIII.1, regardless of which path it chooses
  for the rest of its backend.
- **Database**: Supabase / PostgreSQL, with Row Level Security enabled on all tables holding
  user, listing, or billing data.
- **Authentication**: Supabase Auth (or the project's chosen boilerplate auth handler),
  supporting email/password and Google OAuth.
- **Core AI Engine**: no vendor or model is named at the constitution level. Multi-modal
  generation (photo analysis, SEO listing text, short-form video scripts, LinkedIn B2B posts)
  is served through the `AiService` abstraction and model registry governed by **Principle
  VIII**, whose default provider is open-weight models from the Hugging Face Hub. Selecting,
  swapping, or adding a model or provider is a configuration change subject to Principle
  VIII.4's gates — it is explicitly NOT a constitution amendment.
- **Global Payments**: Stripe or Lemon Squeezy (Merchant of Record) for tiered subscription
  billing (Starter vs. Pro) with server-enforced usage tracking.
- **Internationalization**: Multi-language architecture enabled from day one — English
  (default) and Turkish (secondary) — extensible to further locales without a rewrite.
- **Project Structure**: Backend and frontend code live in one repository, split into two
  top-level directories — `RealtorAI_BackEnd/` and `RealtorAI_FrontEnd/`. Nothing
  application-specific lives at the repository root outside these two directories (and the
  shared `specs/`, `.specify/` tooling). A feature that touches both sides of the stack MUST
  say so explicitly in its spec and land changes in both directories under that feature's
  branch.

Any change to this stack (e.g., swapping the payments provider, dropping .NET, replacing
Supabase) is a constitution amendment, not a routine implementation decision, and MUST follow
the amendment procedure below. **Changing an AI model or provider is the one deliberate
exception**: under Principle VIII it is a gated configuration change, not an amendment.
Removing or weakening the `AiService` abstraction, the registry itself, or any Principle VIII
gate IS an amendment.

## Modern Coding Principles & Architecture

### A. Next.js (App Router) & React Standards

- **Component isolation**: Server Components are the default; Client Components
  (`"use client"`) MUST be justified by an actual need for interactivity/browser APIs and MUST
  be pushed to the leaves of the component tree to maximize SSR and SEO performance.
- **Server Actions vs. Route Handlers**: Server Actions are used for form submissions,
  mutations, and direct database writes via Supabase. Route Handlers (`route.ts`) are used
  only for webhooks (e.g., Stripe/Lemon Squeezy webhooks) and external API access points —
  not as a general-purpose substitute for Server Actions.
- **Data fetching**: Server Components fetch data directly via `async`/`await`. Every route
  segment that fetches data MUST ship a `loading.tsx` and an `error.tsx` so failures and
  latency never surface as a blank screen or an unhandled exception.

### B. Robust State Management & API Resilience

- **Fail-safe async operations**: Every API call, AI generation request, or database
  transaction MUST be wrapped in structured `try/catch`. Raw system errors MUST NOT be
  surfaced to the user or dumped to console in place of a proper user-facing message.
- **Type-safe payloads**: `any` and unchecked `unknown` are forbidden in TypeScript. Every API
  payload, database row, and AI-generated JSON output MUST have a fully declared
  `interface`/`type` (this sharpens Principle V's typed-contract requirement — it is not a new,
  separate obligation).
- **Zod validation**: All server-side inputs (Server Actions, Route Handlers) and all
  client-side form inputs MUST be validated with Zod schemas. Failed validation returns a
  `400 Bad Request` (malformed request) or `422 Unprocessable Entity` (well-formed but
  semantically invalid) response with field-specific error detail — never a bare 500 or a
  silently ignored failure.

### C. Database & Security Hardening

- **Row Level Security**: Every new Supabase table MUST have its RLS policy designed in the
  feature's spec before implementation (this is the concrete mechanism for Principle VII's
  access-control requirement). No row is readable or writable without verifying the
  authenticated `auth.uid()` against ownership/membership.
- **Query performance**: Frequently filtered columns (e.g., `user_id`, `property_id`) MUST be
  indexed. Long-running or unpooled connections are prohibited — use Supabase's pooled
  connection modes.
- **Sensitive data isolation**: API keys, Stripe/Lemon Squeezy secret keys, and AI platform
  tokens MUST NOT be exposed to the client. They are read only in server-only contexts, using
  environment variables without the `NEXT_PUBLIC_` prefix.

### D. AI Engine Operations (Model-Agnostic Multi-Modal)

These are the operational mechanics of Principle VIII; that principle governs which models may
be used at all, while this section governs how calls are made.

- **Deterministic JSON outputs**: Structured generations (the SEO listing text, short-form
  video script, and LinkedIn B2B post) MUST enforce JSON Mode / Structured Outputs against an
  explicit, versioned JSON schema — never parsed out of free-form prose. The mechanism is
  whatever the registry's active provider exposes for structured output; the requirement is the
  schema-validated result, not any one vendor's parameter name.
- **Token & timeout management**: Property photos MUST be compressed/resized client-side
  before upload to Supabase Storage or submission to a vision model, to control bandwidth,
  token cost, and avoid gateway timeouts on large images. Images leaving the approved data
  boundary MUST additionally be EXIF-stripped per Principle VIII.4.c, and the compressed copy
  never replaces the immutable original (Principle VIII.6).

### E. UI/UX Design Standards (Real Estate SaaS)

- **Modern, premium aesthetic**: The UI MUST read as a modern, professional B2B SaaS product —
  generous whitespace, a clear typographic scale, soft elevation (subtle shadows/borders)
  instead of heavy boxed borders, and rounded, consistent corner radii. Dated, generic
  "admin template" styling (dense tables with no hierarchy, default browser form controls,
  clashing colors) is not acceptable.
- **Real-estate-appropriate visual language**: The palette and imagery treatment MUST convey
  trust and quality — property photos are a first-class visual element (large, high-quality,
  properly cropped/aspect-ratio-consistent) rather than small thumbnails; a neutral base
  palette (e.g., whites/greys/near-black text) with a single confident accent color is
  preferred over saturated, generic "tech startup" gradients.
- **Design system consistency**: Spacing, color, radius, and type-scale values MUST come from
  the shared design tokens defined in `RealtorAI_FrontEnd/app/globals.css` (Tailwind v4 is
  configured CSS-first; there is no `tailwind.config.ts`) — not one-off inline hex codes or
  magic-number spacing repeated per component. Components MUST consume the semantic
  `--color-*` tokens, never a raw hex and never a `--light-*`/`--dark-*` palette entry
  directly. New components reuse existing primitives (buttons, cards, inputs, fields)
  before introducing new ones.
- **Responsive & accessible by default**: Every screen MUST be usable from mobile through
  desktop widths and MUST meet WCAG AA color contrast; interactive elements MUST be keyboard
  reachable and carry accessible labels — this is a concrete implementation of Principle V's
  "robust, production-ready" bar applied to UI, not a new exception process.
- **Dark mode is a project-wide baseline, not a per-feature option.** Light and dark are
  both first-class: every screen, component, and state (including empty, loading, and error
  states) MUST be correct in both. In practice this is satisfied by consuming the semantic
  `--color-*` tokens above — a component that uses only those is dark-mode correct for free.
  A feature is NOT done if it introduces a hard-coded colour that breaks in one theme. The
  active palette is resolved by `<html data-theme>` plus a `prefers-color-scheme` fallback;
  the user's choice (light / dark / follow-system) lives in the `theme` cookie, which is a
  per-device display preference and deliberately NOT stored on `profiles`.
- **WCAG AA contrast MUST be verified in both themes**, not just light. Accent colours in
  particular need separate light and dark values — a hue that passes on white usually fails
  on a near-black surface, and its foreground flips with it.

## Development Workflow & Quality Gates

- **Spec before code**: For any new feature, the first response MUST be a drafted
  `[feature-name].spec.md` covering User Story, Database Schema Changes, API Contracts,
  Frontend Component Layout, and Edge Cases — before the codebase is touched.
- **Constitution Check gate**: `/speckit-plan` MUST verify the plan against every principle in
  this document before Phase 0 research begins, and again after Phase 1 design. Unresolved
  violations MUST be recorded in the plan's Complexity Tracking table with a justification, or
  the plan MUST be revised to comply.
- **Tests as a gate, not an afterthought**: Per Principle II, contract/integration tests for a
  user story are written and confirmed failing before that story's implementation tasks begin.
- **No placeholder merges**: Per Principle IV, a task is not "done" if it contains stubs,
  TODOs, or partial logic — split the task instead of merging it incomplete.
- **i18n and security checks**: Before a feature is marked complete, verify no hardcoded
  user-facing strings were introduced (Principle VI) and that any new data surface has an
  explicit access-control model (Principle VII).
- **Tone**: All specs, plans, and code review commentary are concise, professional, and
  engineering-focused.
- **Local-first development phase**: In this project's initial build-out, every feature that
  introduces or changes a database schema MUST be developed and verified against the local
  Supabase stack (`supabase start`) running on `localhost` before any hosted/staging Supabase
  project is touched. The feature's `plan.md`/`quickstart.md` MUST document: (1) how to stand
  up the schema locally (migration files applied via the Supabase CLI), and (2) how to connect
  a standard SQL client (e.g., DBeaver) to the local Postgres instance to inspect the created
  tables — including host, port, database, and credentials as reported by `supabase start`
  (its default local Postgres endpoint is `127.0.0.1:54322`, db `postgres`, user `postgres`;
  MUST be confirmed against the actual CLI output rather than assumed, since it is
  configurable per project).
- **Sequential numbering**: Every feature carries the sequential number assigned by
  `/speckit-specify` (e.g. `001-feature-name`), and every task within it carries its `T0xx`
  ID from `tasks.md`. These numbers are the traceability key across spec → plan → tasks →
  commit → deploy; no work item is referenced only by a free-text description.
- **Automated commit & push**: When a numbered task (or a full feature) completes, the
  resulting changes MUST be committed with a message that references the feature number and
  task ID (e.g. `[001-feature-name] T012: add listing upload endpoint`, or
  `[001-feature-name]: implement feature` for a feature-level commit), and pushed
  automatically to that feature's numbered branch on the configured remote — without waiting
  for a manual `git commit`/`git push` from the developer. This automation MUST NOT push
  directly to `main`/`master`; merging a feature branch to main still goes through normal
  review. If no remote is configured or push fails, the automation halts and surfaces the
  failure rather than silently skipping it (per Principle III's halt-and-ask spirit).

## Governance

This constitution supersedes all other project practices, ad hoc conventions, and prior
verbal agreements. Where a spec, plan, or piece of code conflicts with this document, this
document wins unless the constitution itself is amended.

**Amendment procedure**: Amendments are proposed by editing
`.specify/memory/constitution.md`, describing the change and rationale, and are adopted by
updating the version per the policy below. Amendments MUST include a review of
`.specify/templates/plan-template.md`, `.specify/templates/spec-template.md`, and
`.specify/templates/tasks-template.md` for consistency, and MUST update any runtime guidance
docs that reference changed principles.

**Versioning policy**: Semantic versioning applies to this constitution:
- **MAJOR** — backward-incompatible governance changes or removal/redefinition of a principle.
- **MINOR** — a new principle or materially expanded guidance is added.
- **PATCH** — clarifications, wording, and non-semantic refinements.

**Compliance review**: Every `/speckit-plan` run MUST pass the Constitution Check gate. Every
feature's `tasks.md` MUST reflect the test-first, no-placeholder, i18n, and security
obligations above. Any feature with an AI surface MUST additionally show, in its `plan.md`,
how it satisfies Principle VIII: the registry entry per task with its license and evaluation
result, the versioned output schema and prompt, the data-boundary analysis, and the automated
test enforcing the no-generative-imagery prohibition. Reviewers MUST reject specs, plans, or
PRs that violate a NON-NEGOTIABLE principle (I, III, IV, VIII) without an explicit, documented
exception approved by the project owner — noting that Principle VIII.6 (no generative imagery)
and VIII.7 (human approval before publication) are legal controls and are not eligible for
exception.

**Version**: 3.0.0 | **Ratified**: 2026-07-20 | **Last Amended**: 2026-10-12
