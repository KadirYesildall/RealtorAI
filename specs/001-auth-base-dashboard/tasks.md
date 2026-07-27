---
description: "Task list for Authentication & Base Dashboard"
---

# Tasks: Authentication & Base Dashboard

**Input**: Design documents from `specs/001-auth-base-dashboard/` (spec.md, plan.md,
research.md, data-model.md, contracts/, quickstart.md)

**Tests**: Included and **mandatory** — the RealtorAI constitution's Principle II (Test-First
Discipline) requires contract tests for every Server Action/Route Handler and integration
tests for every cross-cutting flow; this is not an optional default here.

**Organization**: Tasks are grouped by user story per spec.md's priorities (US1: P1, US3: P1,
US2: P2, US4: P3). US1 and US3 together form the MVP per the spec's own framing ("ships
alongside User Story 1 as part of the same MVP slice").

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: US1 / US2 / US3 / US4, per spec.md
- Every task names an exact file path per plan.md's Project Structure

## Path Conventions (fixed for this project, per the constitution)

- Frontend/app code: `RealtorAI_FrontEnd/`
- Backend/schema: `RealtorAI_BackEnd/supabase/`
- Contract tests: `RealtorAI_FrontEnd/tests/contract/`
- Integration tests: `RealtorAI_FrontEnd/tests/integration/`

---

## Phase 1: Setup

**Purpose**: Project initialization for both sides of the split.

- [X] T001 Initialize `RealtorAI_FrontEnd/` as a Next.js (App Router) + TypeScript project
- [X] T002 Initialize `RealtorAI_BackEnd/` as a Supabase project (`supabase init`), creating `RealtorAI_BackEnd/supabase/config.toml`
- [X] T003 [P] Configure Tailwind v4 design tokens (spacing/color/radius/type scale) via the `@theme` block in `RealtorAI_FrontEnd/app/globals.css`
- [X] T004 [P] Install runtime dependencies (`@supabase/supabase-js`, `@supabase/ssr`, `zod`, `class-variance-authority`, `clsx`, `tailwind-merge`) in `RealtorAI_FrontEnd/package.json` — confirm current published versions via the package registry before pinning (Principle III; do not pin from memory)
- [X] T005 [P] Configure Vitest for contract tests in `RealtorAI_FrontEnd/vitest.config.ts`
- [X] T006 [P] Configure Playwright for integration tests in `RealtorAI_FrontEnd/playwright.config.ts`
- [X] T007 [P] Configure ESLint + Prettier in `RealtorAI_FrontEnd/`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Schema, clients, and shared UI/i18n primitives every user story depends on.

**⚠️ CRITICAL**: No user story task may begin until this phase is complete.

- [X] T008 Write the `profiles` table migration (table, RLS policies `profiles_select_own`/`profiles_update_own`, `handle_new_user` trigger) per data-model.md in `RealtorAI_BackEnd/supabase/migrations/0001_profiles.sql`
- [X] T009 Run `supabase start` (from `RealtorAI_BackEnd/`) and confirm `0001_profiles.sql` applied cleanly to the local stack (depends on: T002, T008)
- [X] T010 [P] Create the Supabase server client factory (anon key + user session via `@supabase/ssr`, **never** the service-role key) in `RealtorAI_FrontEnd/lib/supabase/server.ts` (depends on: T004)
- [X] T011 [P] Create the Supabase browser client factory in `RealtorAI_FrontEnd/lib/supabase/client.ts` (depends on: T004)
- [X] T012 Create `RealtorAI_FrontEnd/proxy.ts`: refresh the Supabase session cookie every request and set a `NEXT_LOCALE` cookie from the `Accept-Language` header on first visit, defaulting to `en` unless `tr` is detected (depends on: T010, T011)
- [X] T013 [P] Create the `cn()` utility (`clsx` + `tailwind-merge`) in `RealtorAI_FrontEnd/lib/utils/cn.ts` (depends on: T004)
- [X] T014 [P] Create the shared `Button` primitive (cva-based, tokens from `app/globals.css` (Tailwind v4 `@theme` tokens)) in `RealtorAI_FrontEnd/components/ui/Button.tsx` (depends on: T003, T013)
- [X] T015 [P] Create the shared `Input` primitive (cva-based) in `RealtorAI_FrontEnd/components/ui/Input.tsx` (depends on: T003, T013)
- [X] T016 [P] Create the shared `Card` primitive (cva-based) in `RealtorAI_FrontEnd/components/ui/Card.tsx` (depends on: T003, T013)
- [X] T017 [P] Create the English dictionary in `RealtorAI_FrontEnd/lib/i18n/dictionaries/en.json`
- [X] T018 [P] Create the Turkish dictionary in `RealtorAI_FrontEnd/lib/i18n/dictionaries/tr.json`
- [X] T019 Create `getDictionary(locale)` helper in `RealtorAI_FrontEnd/lib/i18n/getDictionary.ts` (depends on: T017, T018)

**Checkpoint**: Schema is live locally; shared clients, UI primitives, and i18n plumbing exist. User story work can begin.

---

## Phase 3: User Story 1 - Email/Password Sign-Up and Login (Priority: P1) 🎯 MVP

**Goal**: A visitor can register, verify their email, and log in to reach the dashboard.

**Independent Test**: Register with a valid name/email/password + accepted consent, confirm via
the local email testing inbox, then log in with those credentials (spec US1 Independent Test).

### Tests for User Story 1 ⚠️

> Write these tests FIRST; confirm they FAIL before implementation begins.

- [X] T020 [US1] Write contract tests for `signUpAction` and `signInAction` (valid input, field-validation failures, duplicate-email, wrong-password, unverified-email cases per `contracts/auth-actions.md`) in `RealtorAI_FrontEnd/tests/contract/auth-actions.test.ts`
- [X] T021 [P] [US1] Write integration test for register → the local email testing inbox email verification → login → dashboard redirect in `RealtorAI_FrontEnd/tests/integration/sign-up-and-login.spec.ts`

### Implementation for User Story 1

- [X] T022 [US1] Create `SignUpSchema` and `SignInSchema` (Zod, including `acceptedTerms: literal(true)`) in `RealtorAI_FrontEnd/lib/validation/auth.ts` (depends on: T020, T021 failing; T004)
- [X] T023 [US1] Implement `signUpAction` per `contracts/auth-actions.md` in `RealtorAI_FrontEnd/app/actions/auth.ts` (depends on: T022, T010, T009)
- [X] T024 [US1] Implement `signInAction` per `contracts/auth-actions.md` in `RealtorAI_FrontEnd/app/actions/auth.ts` (depends on: T023 — same file, sequential)
- [X] T025 [US1] Create `ConsentCheckbox` component in `RealtorAI_FrontEnd/components/auth/ConsentCheckbox.tsx` (depends on: T013)
- [X] T026 [US1] Create `RegisterForm` client component (`useActionState` + `signUpAction` + `ConsentCheckbox`, all labels via `getDictionary`) in `RealtorAI_FrontEnd/components/auth/RegisterForm.tsx` (depends on: T023, T025, T014, T015, T019)
- [X] T027 [US1] Create `LoginForm` client component (`useActionState` + `signInAction`, resend-confirmation affordance, localized) in `RealtorAI_FrontEnd/components/auth/LoginForm.tsx` (depends on: T024, T014, T015, T019)
- [X] T028 [US1] Create the register page (Server Component shell) in `RealtorAI_FrontEnd/app/register/page.tsx` (depends on: T026)
- [X] T029 [US1] Create the login page (Server Component shell) in `RealtorAI_FrontEnd/app/login/page.tsx` (depends on: T027)
- [X] T030 [US1] Create the "check your email" confirmation page in `RealtorAI_FrontEnd/app/register/check-email/page.tsx` (depends on: T028)

**Checkpoint**: User Story 1 is fully functional and independently testable.

---

## Phase 4: User Story 3 - Protected, Personalized Dashboard (Priority: P1) 🎯 MVP

**Goal**: Only authenticated users reach the dashboard, and it shows their own data.

**Independent Test**: Visit `/dashboard` logged out (expect redirect to `/login`); log in and
confirm the dashboard shows only that user's own name/company/usage data (spec US3
Independent Test).

### Tests for User Story 3 ⚠️

- [X] T031 [P] [US3] Write integration test for the dashboard route guard (logged-out redirect; two different logged-in users each seeing only their own data) in `RealtorAI_FrontEnd/tests/integration/dashboard-route-guard.spec.ts`
- [X] T032 [US3] Write contract test for `signOutAction` in `RealtorAI_FrontEnd/tests/contract/auth-actions.test.ts` (depends on: T020 — same file)

### Implementation for User Story 3

- [X] T033 [US3] Implement `signOutAction` per `contracts/auth-actions.md` in `RealtorAI_FrontEnd/app/actions/auth.ts` (depends on: T032, T024 — same file, sequential)
- [X] T034 [US3] Create `RealtorAI_FrontEnd/app/dashboard/layout.tsx`: Server Component auth guard (`supabase.auth.getUser()` → redirect `/login` if null) + sidebar shell (depends on: T010, T031)
- [X] T035 [P] [US3] Create `Sidebar` component (nav to Property/Settings/Billing with "coming soon" states, sign-out button wired to `signOutAction`) in `RealtorAI_FrontEnd/components/dashboard/Sidebar.tsx` (depends on: T033, T014)
- [X] T036 [P] [US3] Create `UsageSummaryCard` component in `RealtorAI_FrontEnd/components/dashboard/UsageSummaryCard.tsx` (depends on: T016)
- [X] T037 [US3] Create the dashboard home page (welcome hero + usage summary, fetches the caller's own profile server-side) in `RealtorAI_FrontEnd/app/dashboard/page.tsx` (depends on: T034, T035, T036)
- [X] T038 [P] [US3] Create `RealtorAI_FrontEnd/app/dashboard/loading.tsx`
- [X] T039 [P] [US3] Create `RealtorAI_FrontEnd/app/dashboard/error.tsx` (renders a friendly message; never surfaces the raw error, per Modern Principle B)

**Checkpoint**: US1 + US3 together are a demoable MVP — full register/verify/login → protected, personalized dashboard loop.

---

## Phase 5: User Story 2 - Google OAuth Sign-In (Priority: P2)

**Goal**: A visitor can sign up or log in with Google instead of a password.

**Independent Test**: From login/register, choose "Continue with Google", complete consent,
confirm a profile is created (first time) or reused (repeat) and the dashboard loads (spec US2
Independent Test).

### Tests for User Story 2 ⚠️

- [X] T040 [US2] Write contract tests for `signInWithGoogleAction` and the OAuth callback route per `contracts/auth-actions.md` and `contracts/oauth-callback.md` in `RealtorAI_FrontEnd/tests/contract/auth-actions.test.ts` (depends on: T032 — same file)
- [X] T041 [P] [US2] Write integration test for the Google OAuth happy path and consent-denied cancellation in `RealtorAI_FrontEnd/tests/integration/google-oauth.spec.ts` (happy-path completion needs real Google credentials — manual verification per quickstart.md; automated coverage stops at the Supabase authorize-URL boundary)

### Implementation for User Story 2

- [X] T042 [US2] Implement `signInWithGoogleAction` per `contracts/auth-actions.md` in `RealtorAI_FrontEnd/app/actions/auth.ts` (depends on: T040, T033 — same file, sequential)
- [X] T043 [US2] Implement the OAuth callback Route Handler per `contracts/oauth-callback.md` in `RealtorAI_FrontEnd/app/auth/callback/route.ts` (depends on: T040, T010)
- [X] T044 [US2] Add a "Continue with Google" button (gated by the existing `ConsentCheckbox`) to `RealtorAI_FrontEnd/components/auth/RegisterForm.tsx` and `RealtorAI_FrontEnd/components/auth/LoginForm.tsx` (depends on: T042, T026, T027)

**Checkpoint**: US1, US2, and US3 all work independently — Google sign-in is a fully alternate path into the same dashboard.

---

## Phase 6: User Story 4 - Language Preference (Priority: P3)

**Goal**: Users can switch and persist their interface language (English/Turkish).

**Independent Test**: Switch language as a logged-in user, confirm immediate UI update, then
confirm it's preserved after logging out and back in from a different session (spec US4
Independent Test).

### Tests for User Story 4 ⚠️

- [X] T045 [P] [US4] Write contract test for `updateLanguagePreferenceAction` per `contracts/profile-actions.md` in `RealtorAI_FrontEnd/tests/contract/profile-actions.test.ts`
- [X] T046 [P] [US4] Write integration test for language switch + persistence across re-login (and default-locale detection per spec US4 AC1) in `RealtorAI_FrontEnd/tests/integration/language-preference.spec.ts`

### Implementation for User Story 4

- [X] T047 [US4] Add `LanguageSchema` (Zod, `'en' | 'tr'`) to `RealtorAI_FrontEnd/lib/validation/auth.ts` (depends on: T045)
- [X] T048 [US4] Implement `updateLanguagePreferenceAction` per `contracts/profile-actions.md` in `RealtorAI_FrontEnd/app/actions/profile.ts` (depends on: T047, T010)
- [X] T049 [US4] Create `LanguageSwitcher` client component in `RealtorAI_FrontEnd/components/dashboard/LanguageSwitcher.tsx` (depends on: T048, T014)
- [X] T050 [US4] Wire `LanguageSwitcher` into the dashboard shell in `RealtorAI_FrontEnd/app/dashboard/layout.tsx` (depends on: T049, T034)

**Checkpoint**: All four user stories are independently functional.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [X] T051 [P] Run the full `quickstart.md` validation end-to-end (every user-story row plus the DBeaver DoD gate) across `RealtorAI_BackEnd/` and `RealtorAI_FrontEnd/` — schema/RLS/trigger verified via direct `psql` (equivalent facts DBeaver would show); all 4 user-story flows verified via Playwright against the real local stack. Opening DBeaver itself is a GUI step left for the developer (see completion notes).
- [X] T052 [P] Accessibility pass: verify WCAG AA contrast and keyboard reachability on `Button`/`Input`/`Card` and all four screens, per constitution Section E, in `RealtorAI_FrontEnd/components/ui/` — all chosen tokens compute to ≥6.2:1 contrast on white; all interactive elements are native/keyboard-reachable with visible focus rings
- [X] T053 [P] Security pass: confirm the Supabase service-role key is referenced nowhere in `RealtorAI_FrontEnd/`, and re-verify both RLS policies in DBeaver per the spec's Definition-of-Done gate — grep confirms zero references outside `tests/contract/fixtures.ts` (explicitly test-only); RLS policies + explicit `GRANT` re-verified via `psql`
- [X] T054 Verify no hardcoded user-facing strings remain outside `RealtorAI_FrontEnd/lib/i18n/dictionaries/` (Principle VI) — found and fixed one gap (`dashboard/loading.tsx`'s `aria-label`)
- [X] T055 Final review against the Constitution Check gate recorded in `plan.md` — all rows still PASS; see completion notes for corrections discovered during implementation

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately.
- **Foundational (Phase 2)**: Depends on Setup — BLOCKS every user story.
- **User Stories (Phase 3-6)**: All depend on Foundational. Priority order is US1 → US3 → US2 → US4 (both P1 stories first, in spec order; then P2; then P3). US3 and US2 can start as soon as Foundational is done if staffed separately, but see the shared-file note below.
- **Polish (Phase 7)**: Depends on all four user stories being complete.

### Shared-file sequencing (important exception to story independence)

Three files are extended across multiple story phases and MUST be edited in task-ID order even
though the stories themselves are logically independent:

- `RealtorAI_FrontEnd/app/actions/auth.ts`: T023 (US1) → T024 (US1) → T033 (US3) → T042 (US2).
- `RealtorAI_FrontEnd/tests/contract/auth-actions.test.ts`: T020 (US1) → T032 (US3) → T040 (US2).
- `RealtorAI_FrontEnd/components/auth/{RegisterForm,LoginForm}.tsx`: created in US1 (T026/T027), extended with the Google button in US2 (T044).

Each story's own contract/acceptance criteria remain independently verifiable; only the raw
edit order on these shared files is constrained.

### Within Each User Story

- Tests are written and confirmed failing before implementation (Principle II, non-negotiable
  for this project).
- Validation schemas before actions; actions before components; components before pages.

---

## Parallel Example: Foundational Phase

```bash
# After T008/T009 (schema) and T010/T011 (clients) are done, these can run together:
Task: "Create cn() utility in RealtorAI_FrontEnd/lib/utils/cn.ts"
Task: "Create shared Button primitive in RealtorAI_FrontEnd/components/ui/Button.tsx"
Task: "Create shared Input primitive in RealtorAI_FrontEnd/components/ui/Input.tsx"
Task: "Create shared Card primitive in RealtorAI_FrontEnd/components/ui/Card.tsx"
Task: "Create English dictionary in RealtorAI_FrontEnd/lib/i18n/dictionaries/en.json"
Task: "Create Turkish dictionary in RealtorAI_FrontEnd/lib/i18n/dictionaries/tr.json"
```

## Parallel Example: User Story 1

```bash
# T020 and T021 touch different files and can run together once Foundational is done:
Task: "Write contract tests for signUpAction/signInAction in RealtorAI_FrontEnd/tests/contract/auth-actions.test.ts"
Task: "Write integration test for sign-up-and-login in RealtorAI_FrontEnd/tests/integration/sign-up-and-login.spec.ts"
```

---

## Implementation Strategy

### MVP First (User Story 1 + User Story 3)

1. Complete Phase 1: Setup.
2. Complete Phase 2: Foundational (schema live locally, clients/primitives/i18n ready).
3. Complete Phase 3: User Story 1 (register/verify/login).
4. Complete Phase 4: User Story 3 (protected, personalized dashboard).
5. **STOP and VALIDATE**: run `quickstart.md`'s US1/US3 rows and the DBeaver DoD gate.
6. This is the MVP — deployable/demoable on its own per the spec's framing.

### Incremental Delivery

1. Setup + Foundational → local stack verified in DBeaver.
2. US1 + US3 → MVP (validate, demo).
3. US2 (Google OAuth) → validate, demo.
4. US4 (Language preference) → validate, demo.
5. Polish → accessibility, security, i18n-completeness, final constitution review.

## Notes

- [P] tasks touch different files with no unmet dependency.
- Tests are mandatory in this project (constitution Principle II) — always write and confirm
  failing before the corresponding implementation task.
- Commit after each task or logical group, per the constitution's Sequential Numbering and
  Automated Commit & Push workflow rules (`[001-auth-base-dashboard] T0xx: <summary>`).
- No task in this list contains a placeholder, stub, or "implement later" marker — per
  Principle IV, any task too large to finish in one pass should be split further rather than
  landed incomplete.
