# Quickstart: Authentication & Base Dashboard

This is the runnable validation guide for `001-auth-base-dashboard`. It proves the feature
works end-to-end on `localhost`, per the constitution's Local-first development phase and the
spec's "Local Development & Verification" section. It does not duplicate schema/contract
detail already written down — see [data-model.md](./data-model.md) and
[contracts/](./contracts/) for that.

## Prerequisites

- Node.js (LTS) and a package manager (npm/pnpm) — exact version pinned in
  `RealtorAI_FrontEnd/package.json` once scaffolded.
- **Docker Desktop** running — required by `supabase start` (research.md §3).
- Supabase CLI installed (`npm install -g supabase` or per Supabase's current install docs —
  confirm the current recommended install method at implementation time, Principle III).
- A Google Cloud OAuth 2.0 Client ID/Secret (for User Story 2), with the redirect URI set to
  the local Supabase Auth callback (`http://127.0.0.1:54321/auth/v1/callback` — confirm the
  exact port against your local `supabase start` output) — this is a one-time manual setup step
  in the Google Cloud Console and the local Supabase Studio's Auth Providers settings, not
  something a migration file can automate. Without it, User Story 1, 3, and 4 are still fully
  testable; only User Story 2 needs it.

## 1. Stand up the local Supabase stack

```bash
cd RealtorAI_BackEnd
supabase init          # first time only
supabase start
```

Note the printed `API URL`, `DB URL`, `anon key`, and `service_role key` — copy the `API URL`
and `anon key` into `RealtorAI_FrontEnd/.env.local` (never commit this file). The migration in
`supabase/migrations/0001_profiles.sql` is applied automatically on `supabase start`.

## 2. Run the app locally

```bash
cd RealtorAI_FrontEnd
npm install
npm run dev
```

Visit `http://localhost:3000/register`.

## 3. Validate each user story

| Story | Steps | Expected result |
|---|---|---|
| US1 — Email/password sign-up & login | Register with a real-looking email, check the consent box, submit. Then open the local email testing inbox (`http://127.0.0.1:54324`, port per research.md §3) for that address and click the confirmation link. Log in with the same credentials. | Redirected to `/dashboard`; welcome message shows the name entered at sign-up (spec US1 AC1–AC2, FR-005). |
| US1 edge — wrong password / duplicate email / no consent | Retry login with a wrong password; retry registration with the same email; try submitting registration with the consent box unchecked. | Each shows the specific inline error from `contracts/auth-actions.md` (US1 AC3–AC5). |
| US2 — Google OAuth | With Google OAuth configured (see Prerequisites), click "Continue with Google" from `/login`, complete consent. | Redirected to `/dashboard`; a `profiles` row exists (verify in DBeaver, step 4) without ever setting a password (spec US2 AC1). |
| US3 — Route guard | While logged out, visit `http://localhost:3000/dashboard` directly. | Redirected to `/login` (spec US3 AC1, SC-002). |
| US3 — Dashboard content | While logged in, view `/dashboard`. | Name, company (if set), and `api_usage_count` are visible; sidebar links to Property/Settings/Billing render (spec US3 AC2–AC3). |
| US4 — Language preference | From the dashboard, switch language to Turkish. Reload the page; then log out and log back in. | All dashboard labels immediately switch to Turkish and stay Turkish after re-login (spec US4 AC2–AC3, SC-004). |

## 4. Inspect the database in DBeaver

This is the Definition-of-Done gate from the spec's "Local Development & Verification"
section — restated here as the concrete runnable step:

1. New connection → **PostgreSQL**.
2. Host `127.0.0.1`, Port `54322`, Database `postgres`, User `postgres`, Password `postgres`
   — **confirm these four values against your own `supabase start` output /
   `RealtorAI_BackEnd/supabase/config.toml` before trusting them**; they are the CLI's
   documented defaults but are configurable per project (spec + research.md §3).
3. Test connection → browse `public.profiles`. After completing the US1/US2 validations above,
   confirm:
   - One row per test account created, with `accepted_terms_at` populated (never null).
   - `preferred_language` reflects the last value set in the US4 validation.
4. Run in a SQL editor:
   ```sql
   SELECT * FROM pg_policies WHERE tablename = 'profiles';
   ```
   Confirm both `profiles_select_own` and `profiles_update_own` are listed (data-model.md).

## 5. Run automated tests

```bash
cd RealtorAI_FrontEnd
npm run test           # Vitest — contract tests
npm run test:e2e       # Playwright — integration tests
```

Both suites run against the same local Supabase stack from step 1 — no separate test database
is stood up for this feature (Principle V: one local stack is sufficient at this scope).

## Teardown

```bash
cd RealtorAI_BackEnd
supabase stop
```
