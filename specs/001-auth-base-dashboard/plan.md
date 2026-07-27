# Implementation Plan: Authentication & Base Dashboard

**Branch**: `001-auth-base-dashboard` | **Date**: 2026-07-20 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-auth-base-dashboard/spec.md`

## Summary

Ship email/password + Google OAuth authentication against Supabase, with an auto-provisioned
`profiles` row (RLS-protected, includes language preference, subscription tier, usage count,
and ToS/Privacy consent timestamp), and a route-guarded, localized (EN/TR) dashboard shell.
Built entirely with Next.js Server Actions against a **local** Supabase stack first
(`supabase start`), independently verifiable in DBeaver, before any hosted project is touched.
No `.NET` backend involvement — this feature is Supabase-native per the spec's Assumptions.

## Technical Context

**Language/Version**: TypeScript 5.x on Next.js (App Router) with React — exact Next.js/React
minor/patch versions to be confirmed via `npm view next version` / `npm view react version`
immediately before scaffolding (Principle III: no version numbers asserted from memory).

**Primary Dependencies**: `next`, `react`, `react-dom`, `tailwindcss`, `@supabase/supabase-js`,
`@supabase/ssr` (Supabase's current SSR-safe cookie-based auth helper for Next.js App Router —
supersedes the deprecated `@supabase/auth-helpers-nextjs`), `zod`, `class-variance-authority`
+ `clsx`/`tailwind-merge` (for the shared `Button`/`Input`/`Card` primitives required by the
constitution's UI/UX Design Standards). No form-state library (e.g., react-hook-form) — React's
built-in `useActionState` with Server Actions is sufficient and avoids an unjustified
dependency (Principle V, YAGNI).

**Storage**: Supabase / PostgreSQL, run locally via the Supabase CLI (`supabase start`, which
requires Docker Desktop running) per the constitution's Local-first development phase. No
hosted/staging Supabase project is touched by this feature.

**Testing**: Vitest for unit and contract tests (Server Action input validation, redirect
behavior, RLS-policy SQL assertions via a test Postgres connection); Playwright for
integration/e2e flows (sign-up → verify → login, Google OAuth happy path, dashboard route
guard, language switch persistence) — both run against the local Supabase stack.

**Target Platform**: Web, responsive mobile → desktop, evergreen browsers; developed and
verified on `localhost` in this phase per the constitution's Local-first rule.

**Project Type**: Web application — split per the constitution's Project Structure rule into
`RealtorAI_FrontEnd/` (all application code for this feature) and `RealtorAI_BackEnd/`
(Supabase schema/migrations only; no .NET code is needed for this feature).

**Performance Goals**: Registration completes in <90s excluding email-verification wait
(SC-001); login-to-dashboard render in <5s on standard broadband (SC-003) — both from spec
Success Criteria, not independently invented here.

**Constraints**: Local-first (Docker + Supabase CLI required for development); Server Actions
MUST use the Supabase **anon key** bound to the user's session — the **service-role key MUST
NOT** be used anywhere in this feature, since every mutation is user-scoped and already covered
by RLS (Principle VII); light-mode UI only; EN/TR only; no custom rate-limiting or audit-log
infrastructure (per the Clarifications session — Supabase's built-ins are relied on).

**Scale/Scope**: Single feature, 4 user stories, ~7 Server Actions/routes, 1 table. Not
load-tested at this stage — deferred per spec Assumptions (Supabase-managed infra).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / Section | Status | Notes |
|---|---|---|
| I. Specification-First | PASS | `spec.md` exists, was clarified (3 questions resolved), and includes DB schema, API contracts, and frontend layout as required. |
| II. Test-First | PASS (by design) | This plan specifies Vitest contract tests and Playwright integration tests per user story; `/speckit-tasks` MUST order them before implementation tasks. |
| III. Verified Dependencies | PASS (with follow-up) | No package versions are hardcoded from memory; research.md flags exact versions to be confirmed at scaffold time via the package registry, not assumed. |
| IV. Production-Ready Code | PASS (by design) | No placeholders planned; "coming soon" states for Property/Settings/Billing are an explicitly scoped, declared behavior (spec Assumptions), not a silent stub. |
| V. Clean, Modular & Simple (SOLID/YAGNI) | PASS | No react-hook-form, no next-intl, no custom audit/rate-limit systems — each rejected as unjustified complexity for this scope. |
| VI. Global-First i18n | PASS | EN/TR dictionaries + locale cookie + profile-persisted preference (FR-012/013); no hardcoded UI strings planned. |
| VII. Security & Data Privacy | PASS | RLS-only data access, no service-role key in app code, secrets server-only, explicit ToS consent capture (FR-016). |
| Modern Principles A (Next.js/React) | PASS | Server Components default; Client Components confined to `LoginForm`, `RegisterForm`, `Sidebar`, `LanguageSwitcher`; `loading.tsx`/`error.tsx` planned for `dashboard/`. |
| Modern Principles B (Resilience) | PASS | `AuthActionResult` typed union, Zod validation, try/catch mandated in every Server Action per spec's API Contracts. |
| Modern Principles C (DB/Security) | PASS | RLS policies specified; no long-lived/unpooled connections (Supabase pooling default); secrets via server-only env vars. |
| Modern Principles E (UI/UX) | PASS | Design Guidelines section in spec maps directly to shared Tailwind-token-driven primitives (`Button`/`Input`/`Card`). |
| Local-first development phase | PASS | Entire "Local Development & Verification" section of the spec is the operational basis for this plan's quickstart.md. |
| Sequential numbering | PASS | Feature is `001-auth-base-dashboard`; tasks will carry `T0xx` IDs. |

**Result**: No violations. Complexity Tracking table is empty by design — nothing here needs
justification.

## Project Structure

### Documentation (this feature)

```text
specs/001-auth-base-dashboard/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md         # Phase 1 output
├── quickstart.md         # Phase 1 output
├── contracts/            # Phase 1 output
│   ├── auth-actions.md
│   ├── profile-actions.md
│   └── oauth-callback.md
└── tasks.md              # Phase 2 output (/speckit-tasks — not created here)
```

### Source Code (repository root)

```text
RealtorAI_BackEnd/
└── supabase/
    ├── config.toml                     # Local Supabase CLI config (ports, etc.)
    └── migrations/
        └── 0001_profiles.sql           # profiles table, RLS policies, handle_new_user trigger

RealtorAI_FrontEnd/
├── proxy.ts                       # Reads/sets NEXT_LOCALE cookie from Accept-Language; refreshes Supabase session cookie
├── app/globals.css (Tailwind v4 @theme tokens)                  # Design tokens: spacing/color/radius/type scale (constitution Section E)
├── app/
│   ├── login/
│   │   └── page.tsx                    # Server Component shell; renders LoginForm
│   ├── register/
│   │   └── page.tsx                    # Server Component shell; renders RegisterForm
│   ├── auth/
│   │   └── callback/
│   │       └── route.ts                # OAuth callback Route Handler
│   ├── actions/
│   │   ├── auth.ts                     # signUpAction, signInAction, signInWithGoogleAction, signOutAction
│   │   └── profile.ts                  # updateLanguagePreferenceAction
│   └── dashboard/
│       ├── layout.tsx                  # Server Component: auth guard + sidebar shell
│       ├── page.tsx                    # Server Component: welcome hero + usage summary
│       ├── loading.tsx
│       └── error.tsx
├── components/
│   ├── ui/
│   │   ├── Button.tsx
│   │   ├── Input.tsx
│   │   └── Card.tsx
│   ├── auth/
│   │   ├── LoginForm.tsx               # Client Component (useActionState + signInAction)
│   │   ├── RegisterForm.tsx            # Client Component (useActionState + signUpAction)
│   │   └── ConsentCheckbox.tsx         # Client Component; gates submit/Google buttons (FR-016)
│   └── dashboard/
│       ├── Sidebar.tsx
│       ├── UsageSummaryCard.tsx
│       └── LanguageSwitcher.tsx
├── lib/
│   ├── supabase/
│   │   ├── server.ts                   # Server-only client (anon key + user session, cookies via @supabase/ssr)
│   │   └── client.ts                   # Browser client (anon key)
│   ├── validation/
│   │   └── auth.ts                     # Zod: SignUpSchema, SignInSchema, LanguageSchema
│   ├── i18n/
│   │   ├── dictionaries/en.json
│   │   ├── dictionaries/tr.json
│   │   └── getDictionary.ts
│   └── utils/
│       └── cn.ts                       # clsx + tailwind-merge helper for cva-based primitives
└── tests/
    ├── contract/
    │   ├── auth-actions.test.ts
    │   └── profile-actions.test.ts
    └── integration/
        ├── sign-up-and-login.spec.ts
        ├── google-oauth.spec.ts
        ├── dashboard-route-guard.spec.ts
        └── language-preference.spec.ts
```

**Structure Decision**: Web application per the constitution's mandatory split. This feature's
application code lives entirely in `RealtorAI_FrontEnd/` (Server Actions replace a separate API
project, per the spec's Assumptions); `RealtorAI_BackEnd/` holds only the Supabase migration —
no .NET project is created or touched by this feature.

## Complexity Tracking

*No entries — Constitution Check reported no violations.*
