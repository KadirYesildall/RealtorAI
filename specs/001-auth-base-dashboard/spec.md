# Feature Specification: Authentication & Base Dashboard

**Feature Branch**: `001-auth-base-dashboard`

**Created**: 2026-07-20

**Status**: Draft

**Input**: User description: "As a real estate agent (Global/Turkish), I want to securely sign
up and log in to RealtorAI using my email, select my preferred language (EN/TR), and be
redirected to a secure, type-safe dashboard where I can view my workspace, so that I can
safely manage my property listings and AI generations." Includes Google OAuth (per
constitution's Authentication requirement), Supabase-backed profiles with RLS, and a
localized, route-guarded dashboard shell.

## Clarifications

### Session 2026-07-20

- Q: Should this feature add its own rate-limiting/lockout behavior for repeated failed
  logins, or rely on Supabase Auth's built-in protection? → A: Rely on Supabase Auth's
  built-in rate limiting only — no custom lockout logic in this feature.
- Q: Should this feature build a custom audit log of auth events (sign-up, login
  success/failure, sign-out), or rely on Supabase's built-in Auth logs? → A: Rely on
  Supabase's built-in Auth logs only — no custom audit table in this feature.
- Q: Should registration capture explicit, timestamped consent to Terms of Service /
  Privacy Policy? → A: Require a consent checkbox at registration, storing an
  `accepted_terms_at` timestamp on the profile.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Email/Password Sign-Up and Login (Priority: P1)

A new real estate agent creates an account with their name, email, and password, and a
returning agent logs back in with those same credentials, landing on their personal
dashboard.

**Why this priority**: This is the entry point for every other feature in RealtorAI — no
listing upload, AI generation, or billing feature is reachable without it. It is the smallest
slice that delivers a demonstrable, secure MVP.

**Independent Test**: Register a new account with a valid name, email, and 8+ character
password, confirm the workspace profile is created, then log out and log back in with the
same credentials to confirm access to the dashboard.

**Acceptance Scenarios**:

1. **Given** a visitor on the registration page, **When** they submit a valid name, email,
   and password, **Then** their account and workspace profile are created and they see a
   confirmation state directing them to verify their email (see Assumptions).
2. **Given** a registered, verified user on the login page, **When** they submit correct
   credentials, **Then** they are redirected to their dashboard.
3. **Given** a registered user on the login page, **When** they submit an incorrect password,
   **Then** they see a clear inline error and remain on the login page with their email
   pre-filled.
4. **Given** a visitor on the registration page, **When** they submit an email that already
   belongs to a **confirmed** account, **Then** they see a clear "an account with this email
   already exists" message instead of a generic or raw error. **When** the email instead
   belongs to an **unconfirmed** account (a previous incomplete sign-up), **Then** they are
   sent to the same "check your email" state as a fresh sign-up, without being told whether
   an account already existed (see Edge Cases: this is intentional anti-enumeration behavior,
   not a bug — confirmed via testing against Supabase Auth's actual local behavior).
5. **Given** a visitor on the registration page, **When** they have not checked "I agree to
   the Terms of Service and Privacy Policy", **Then** the submit button stays disabled and no
   account is created.

---

### User Story 2 - Google OAuth Sign-In (Priority: P2)

An agent signs up or logs in using their Google account instead of creating a password.

**Why this priority**: Reduces signup friction for a global audience and is explicitly
required by the project's Authentication stack, but the product is fully usable without it,
so it follows the email/password path.

**Independent Test**: From the login or registration page, choose "Continue with Google",
complete Google's consent screen, and confirm a workspace profile is created (on first use)
or reused (on repeat use) and the dashboard loads.

**Acceptance Scenarios**:

1. **Given** a visitor on the login or registration page, **When** they choose "Continue with
   Google" and complete Google's consent flow for the first time, **Then** a workspace profile
   is created for them and they land on their dashboard without a password ever being set.
2. **Given** a user who previously signed in with Google, **When** they choose "Continue with
   Google" again, **Then** they are returned to their existing workspace and no duplicate
   profile is created.
3. **Given** a user who already registered with email/password, **When** they attempt to sign
   in with Google using the same email, **Then** the system links or reuses the existing
   workspace rather than creating a second, conflicting profile.
4. **Given** a visitor on the login or registration page, **When** they have not checked "I
   agree to the Terms of Service and Privacy Policy", **Then** "Continue with Google" stays
   disabled just like the email/password submit button.

---

### User Story 3 - Protected, Personalized Dashboard (Priority: P1)

A logged-in agent sees a dashboard scoped to their own workspace; a logged-out visitor cannot
reach it.

**Why this priority**: This is the payoff moment of sign-up/login and the security boundary
protecting all future features (listings, AI usage, billing). It ships alongside User Story 1
as part of the same MVP slice.

**Independent Test**: While logged out, request the dashboard URL directly and confirm
redirection to login; then log in and confirm the dashboard shows the current user's own name
and usage data only.

**Acceptance Scenarios**:

1. **Given** an unauthenticated visitor, **When** they navigate directly to the dashboard,
   **Then** they are redirected to the login page.
2. **Given** an authenticated user, **When** they open the dashboard, **Then** they see a
   welcome message with their name, their company (if provided), and their current AI
   generation usage count.
3. **Given** an authenticated user, **When** they view the dashboard, **Then** they can
   navigate to Property, Settings, and Billing areas of the workspace from a persistent
   sidebar.
4. **Given** two different authenticated users, **When** each views their own dashboard,
   **Then** neither can see or affect the other's profile or usage data.

---

### User Story 4 - Language Preference (Priority: P3)

An agent uses RealtorAI in their preferred language (English or Turkish), and that choice
persists across future visits.

**Why this priority**: Important for the product's stated global/Turkish audience, but the
product is functional in a single default language without it, so it's the last slice of this
feature.

**Independent Test**: As a logged-in user, switch the language setting and confirm dashboard
text updates immediately and remains in that language on the next login from a different
session.

**Acceptance Scenarios**:

1. **Given** a first-time visitor whose browser is set to Turkish, **When** they first load
   the app, **Then** the interface defaults to Turkish; **Given** any other browser language,
   **When** they first load the app, **Then** it defaults to English.
2. **Given** a logged-in user, **When** they change their language preference in Settings,
   **Then** all dashboard labels immediately reflect the new language and the choice is saved
   to their profile.
3. **Given** a user who previously set a language preference, **When** they log in from a new
   device or browser, **Then** the dashboard loads in their saved preferred language rather
   than the new browser's default.

---

### Edge Cases

- What happens when Supabase or the network is unavailable during sign-up or login? The user
  sees a friendly "something went wrong, please try again" message — never a raw
  system/database error.
- What happens if the automatic workspace-profile creation (triggered on account creation)
  fails or lags? The dashboard must handle a temporarily missing profile gracefully (e.g., a
  short loading/retry state) rather than crashing or leaking a stack trace.
- What happens when an unverified user tries to log in before confirming their email? They see
  a clear message telling them to check their inbox, with an option to resend the
  confirmation email.
- What happens when a user's session expires while they are on the dashboard? Their next
  action (navigation or data mutation) redirects them to login rather than failing silently.
- What happens when a user removes/denies permission mid-way through the Google consent
  screen? They are returned to the login page with a clear "sign-in was cancelled" message,
  and no partial account is created.
- What happens when a visitor tries to view another user's dashboard URL/profile directly?
  The request is denied at the data layer (not just hidden in the UI) and the visitor is
  redirected to their own dashboard or to login.
- What happens when a user's detected browser language is neither English nor Turkish? The
  interface defaults to English.
- What happens when a visitor repeatedly submits the wrong password for the same account? The
  system relies on Supabase Auth's built-in rate limiting (no custom app-level lockout in this
  feature); each attempt still returns the standard inline error from FR-003's scenario.
- What happens if a visitor bypasses the disabled-button UI and attempts to submit sign-up
  without accepting terms? The server-side validation rejects the request with a clear
  "you must accept the Terms of Service and Privacy Policy" error; no account is created.
- What happens when a visitor re-submits registration for an email that already has an
  **unconfirmed** account? Supabase Auth's local behavior (verified by direct testing, not
  assumed) is indistinguishable from a fresh sign-up — no error, and the confirmation email
  is silently re-sent — as a deliberate anti-account-enumeration measure. The system does not
  attempt to detect or special-case this; the visitor lands on "check your email" exactly as
  they would for a brand-new address. Only re-submitting for an already-**confirmed** email
  produces the explicit "account already exists" error (US1 AC4).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST allow a new visitor to create an account using full name, email, and
  a password of at least 8 characters.
- **FR-002**: System MUST validate email format and password length on both the entry form and
  the server before creating an account, and reject invalid submissions with field-specific
  errors.
- **FR-003**: System MUST allow a registered user to log in with their email and password.
- **FR-004**: System MUST allow a visitor to sign up or log in using their Google account as an
  alternative to email/password.
- **FR-005**: System MUST automatically create exactly one workspace profile per user the
  first time they successfully authenticate, regardless of whether they used email/password or
  Google.
- **FR-006**: System MUST redirect unauthenticated visitors who request the dashboard (or any
  page under it) to the login page.
- **FR-007**: System MUST redirect a user to their dashboard immediately after a successful
  login or completed sign-up (subject to FR-011's email verification gate).
- **FR-008**: System MUST display the authenticated user's name, workspace/company name (if
  provided), and current AI generation usage count on the dashboard.
- **FR-009**: System MUST prevent any user from viewing or modifying another user's profile,
  usage data, or workspace information, enforced at the data layer, not only in the UI.
- **FR-010**: System MUST present clear, field-specific validation and authentication errors to
  the user and MUST NOT surface raw system, network, or database error details.
- **FR-011**: System MUST require email verification before granting dashboard access to
  users who registered via email/password, and MUST allow them to request the confirmation
  email again.
- **FR-012**: System MUST let a user select their interface language (English or Turkish) and
  persist that choice to their profile so it applies on future sessions and devices.
- **FR-013**: System MUST default a first-time visitor's interface language to their browser's
  detected language when it is English or Turkish, and to English otherwise.
- **FR-014**: System MUST provide persistent dashboard navigation to the Property, Settings,
  and Billing areas of the workspace; areas not yet delivered by their own feature MUST show an
  explicit "coming soon" state rather than a broken link or empty page.
- **FR-015**: System MUST allow an authenticated user to sign out, ending their session and
  requiring re-authentication to access the dashboard again.
- **FR-016**: System MUST require explicit consent to the Terms of Service and Privacy Policy
  before a visitor can create an account via either sign-up method, and MUST record the
  timestamp at which consent was given.

### Key Entities

- **Workspace Profile**: An agent's identity and account state within RealtorAI. Holds display
  name, company name, preferred interface language, subscription tier, current AI generation
  usage count, and the timestamp consent to Terms of Service/Privacy Policy was given. Exists
  in a strict one-to-one relationship with an authentication identity (email/password or
  Google) and is created automatically on first successful authentication.
- **Authentication Session**: Represents a signed-in visit. Determines whether dashboard
  access is granted and is invalidated on sign-out or expiry.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A new visitor can complete registration (excluding email verification wait time)
  in under 90 seconds.
- **SC-002**: 100% of unauthenticated requests to dashboard pages are redirected to login in
  testing.
- **SC-003**: A returning, verified user can log in and see their personalized dashboard in
  under 5 seconds on a standard broadband connection.
- **SC-004**: 100% of dashboard labels reflect the user's selected language within one page
  load of changing it.
- **SC-005**: Zero cross-user data exposure incidents across security testing (no user can
  view or alter another user's profile or usage data).
- **SC-006**: At least 95% of sign-up/login attempts in testing complete without the user
  encountering an unhandled or raw error message.
- **SC-007**: The developer can stand up the `profiles` schema entirely on `localhost` (no
  hosted Supabase project required) and independently verify, via a standard SQL client
  connected to the local database, that the table and its RLS policies exist exactly as
  specified.
- **SC-008**: On first look, the login, registration, and dashboard screens are recognized by
  a reviewer as a modern, professional real-estate SaaS product rather than a generic
  admin-template or unstyled form — validated by design review against the constitution's
  UI/UX Design Standards, not by an automated metric.

## Assumptions

- Google OAuth is in scope for this feature because the project constitution's Authentication
  requirement names it explicitly, even though the originating feature narrative emphasized
  the email/password path.
- Password reset ("forgot password") is explicitly **out of scope** for this feature and will
  be specified as its own feature; this feature's Definition of Done does not include it.
- Email confirmation is required before dashboard access for email/password sign-ups
  (industry-standard default); Google sign-ins are treated as pre-verified since Google already
  confirms the email.
- New workspace profiles default to the `free` subscription tier with a usage count of zero;
  the exact tier names, numeric usage limits, and enforcement behavior belong to the future
  Subscription & Paywall feature — this feature only displays the raw usage count.
- Browser-locale detection supports English and Turkish only at this stage; any other detected
  locale falls back to English. Additional locales are a future amendment, per the
  constitution's Internationalization stack constraint.
- The "Property", "Settings", and "Billing" sidebar destinations are visible from day one for
  navigational completeness, but their full functionality ships with their own dedicated
  feature specs; until then they intentionally render a "coming soon" state, which is scoped
  and declared here rather than left as a silent stub.
- Per the constitution's Technology Stack rule requiring an explicit choice per feature: this
  feature uses **Next.js Server Actions** (not the .NET backend) for all mutations, since it is
  a lightweight, Supabase-native auth flow with no need for a separate API layer.
- This feature spans both sides of the project's mandated directory split: application code
  lives in `RealtorAI_FrontEnd/`, while the Supabase SQL schema is tracked as the canonical
  schema-of-record under `RealtorAI_BackEnd/supabase/migrations/`, per the constitution's
  Project Structure rule for cross-cutting features.
- Account linking when the same email is used for both Google and email/password sign-in
  follows Supabase Auth's default identity-linking behavior (same `auth.users` row, multiple
  linked identities) rather than a custom merge flow.
- Per the constitution's "Local-first development phase" workflow rule, this feature is built
  and verified entirely against the local Supabase stack (`supabase start` on `localhost`)
  first; connecting to a hosted/staging Supabase project is out of scope for this feature.
- The UI for this feature (login, registration, dashboard shell) follows the constitution's
  UI/UX Design Standards (Section E): light-mode-first, Tailwind design-token-driven, real
  estate-appropriate visual language. Dark mode is not in scope.
- No custom audit log of authentication events is built in this feature; Supabase's built-in
  Auth logs are the system of record for sign-up/login/logout activity. A dedicated
  `auth_events` table is out of scope unless a future compliance or analytics need requires it.

## Database Schema Changes

Canonical migration file: `RealtorAI_BackEnd/supabase/migrations/0001_profiles.sql`.

```sql
-- 1. Profiles table (extends Supabase Auth's auth.users)
CREATE TABLE public.profiles (
    id UUID REFERENCES auth.users ON DELETE CASCADE PRIMARY KEY,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT TIMEZONE('utc'::text, NOW()) NOT NULL,
    full_name TEXT,
    company_name TEXT,
    preferred_language VARCHAR(5) DEFAULT 'en' NOT NULL,
    subscription_tier VARCHAR(20) DEFAULT 'free' NOT NULL,
    api_usage_count INT DEFAULT 0 NOT NULL,
    accepted_terms_at TIMESTAMP WITH TIME ZONE DEFAULT TIMEZONE('utc'::text, NOW()) NOT NULL
);

-- Enable Row Level Security (default-deny until policies are added)
ALTER TABLE public.profiles ENABLE ROW LEVEL SECURITY;

-- A user may read only their own profile
CREATE POLICY "profiles_select_own"
    ON public.profiles FOR SELECT
    USING (auth.uid() = id);

-- A user may update only their own profile
CREATE POLICY "profiles_update_own"
    ON public.profiles FOR UPDATE
    USING (auth.uid() = id);

-- No INSERT/DELETE policies are defined: profile rows are created only by the
-- SECURITY DEFINER trigger below and are never client-inserted or client-deleted.

-- Auto-create a profile row whenever a new auth.users row is created,
-- regardless of signup method (email/password or Google OAuth).
CREATE OR REPLACE FUNCTION public.handle_new_user()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO public.profiles (id, full_name, preferred_language)
    VALUES (
        new.id,
        new.raw_user_meta_data ->> 'full_name',
        'en'
    );
    RETURN NEW;
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

CREATE TRIGGER on_auth_user_created
    AFTER INSERT ON auth.users
    FOR EACH ROW EXECUTE PROCEDURE public.handle_new_user();
```

Indexing note: `id` is already the primary key (indexed); no additional index is required for
this feature's query patterns (lookups are always by the authenticated `auth.uid()`).

Consent note: `accepted_terms_at` defaults to the moment the profile row is created for both
sign-up methods. This is accurate because the UI (per FR-016) disables both the email/password
submit button and "Continue with Google" until the consent checkbox is checked, so profile
creation never happens without consent having just been given — no extra metadata plumbing
through the OAuth redirect is required.

## Local Development & Verification

Per the constitution's "Local-first development phase" rule, this feature runs entirely on
`localhost` before touching any hosted Supabase project:

1. **Stand up the local stack**: from `RealtorAI_BackEnd/`, run the Supabase CLI's local
   stack (`supabase start`). This starts a local Postgres instance, Auth, and Storage, and
   applies migrations found under `RealtorAI_BackEnd/supabase/migrations/` (including
   `0001_profiles.sql` from this feature).
2. **Run the app locally**: from `RealtorAI_FrontEnd/`, run the Next.js dev server against
   the local Supabase URL/anon key that `supabase start` prints out (stored in the frontend's
   local `.env.local`, never committed).
3. **Inspect the database in DBeaver** (or any standard Postgres client):
   - Create a new **PostgreSQL** connection in DBeaver.
   - Host: `127.0.0.1` · Port: `54322` · Database: `postgres` · User: `postgres` ·
     Password: `postgres` — this is the Supabase CLI's documented local default; the plan/
     quickstart produced by `/speckit-plan` MUST confirm these against the actual
     `supabase start` output before a developer relies on them, since the port is
     configurable per project via `supabase/config.toml`.
   - Test the connection, then browse to the `public` schema → `profiles` table to confirm
     its columns, and to **Properties → Permissions/Policies** (or run
     `SELECT * FROM pg_policies WHERE tablename = 'profiles';` in a SQL editor) to confirm the
     `profiles_select_own` and `profiles_update_own` RLS policies exist.
   - Sign up a test user through the running app, then refresh the `profiles` table in
     DBeaver to confirm the `handle_new_user` trigger created the corresponding row.

This local verification loop is a Definition-of-Done gate for this feature: the schema is not
considered complete until it has been visually confirmed in a SQL client against the local
database, not just assumed correct from the migration file.

## API Contracts

All Server Actions return a type-safe `AuthActionResult`:

```ts
type AuthActionResult =
  | { success: true; data?: { redirectTo: string } }
  | { success: false; error: string };
```

No action ever throws a raw error to the caller; failures are caught and mapped to a
user-safe `error` message (Principle B / Section B of the constitution).

| Action | Location | Input (Zod-validated) | Behavior |
|---|---|---|---|
| `signUpAction` | `RealtorAI_FrontEnd/app/actions/auth.ts` | `{ fullName: string (min 2), email: string (email), password: string (min 8), acceptedTerms: literal(true) }` | Rejects with a field error if `acceptedTerms` is not `true`; otherwise calls `supabase.auth.signUp()` and returns a redirect to a "check your email" state. |
| `signInAction` | `RealtorAI_FrontEnd/app/actions/auth.ts` | `{ email: string (email), password: string (min 8) }` | Calls `supabase.auth.signInWithPassword()`; on success, returns a redirect to `/dashboard`. |
| `signInWithGoogleAction` | `RealtorAI_FrontEnd/app/actions/auth.ts` | none | Calls `supabase.auth.signInWithOAuth({ provider: 'google' })` and returns the provider redirect URL. |
| `signOutAction` | `RealtorAI_FrontEnd/app/actions/auth.ts` | none | Calls `supabase.auth.signOut()`; returns a redirect to `/login`. |
| `updateLanguagePreferenceAction` | `RealtorAI_FrontEnd/app/actions/profile.ts` | `{ language: 'en' \| 'tr' }` | Updates `profiles.preferred_language` for `auth.uid()`. |

| Route Handler | Location | Purpose |
|---|---|---|
| `GET /auth/callback` | `RealtorAI_FrontEnd/app/auth/callback/route.ts` | External OAuth redirect target: exchanges the Google auth code for a Supabase session, then redirects to `/dashboard` (or `/login?error=...` on failure). |

## Design Guidelines

Applying the constitution's UI/UX Design Standards (Section E) to this feature's three
screens:

- **Login / Register**: A centered, uncluttered card on a calm neutral background — not a
  generic centered Bootstrap form. Reserve visual room for brand/trust elements (logo,
  short value proposition, or a property-photo panel on wider viewports) rather than a bare
  form floating in empty space. Primary action (Sign in / Create account) uses the single
  accent color; "Continue with Google" is visually secondary but equally accessible. A single
  "I agree to the Terms of Service and Privacy Policy" checkbox (with inline links to both
  documents) gates both buttons — unchecked, both are visibly disabled rather than hidden.
  Inline validation errors appear directly beneath their field, not in a top-of-form banner.
- **Dashboard shell**: Persistent sidebar (Dashboard Home, New Property, Settings, Billing)
  using clear active-state styling; main content area uses a card-based layout for the
  welcome hero and usage summary, with generous padding rather than dense, bordered table
  rows. The usage count is presented as a simple, legible stat, not a raw database number
  dump.
- **Consistency**: All spacing, color, radius, and type-scale values come from
  `RealtorAI_FrontEnd`'s shared Tailwind config; the Login/Register form fields and Dashboard
  cards reuse the same base `Button`, `Input`, and `Card` primitives so later features inherit
  a consistent look with no additional design work.

## Frontend Component Layout

```text
RealtorAI_FrontEnd/
├── app/
│   ├── login/page.tsx              # Client form; email/password + "Continue with Google"
│   ├── register/page.tsx           # Client form; email/password + "Continue with Google"
│   ├── auth/
│   │   └── callback/route.ts       # OAuth callback Route Handler
│   ├── actions/
│   │   ├── auth.ts                 # signUpAction, signInAction, signInWithGoogleAction, signOutAction
│   │   └── profile.ts              # updateLanguagePreferenceAction
│   └── dashboard/
│       ├── layout.tsx              # Server Component: auth guard + sidebar shell
│       └── page.tsx                # Server Component: welcome hero + usage summary
├── components/
│   ├── ui/
│   │   ├── Button.tsx              # Shared primitive, reused by every feature
│   │   ├── Input.tsx                # Shared primitive
│   │   └── Card.tsx                 # Shared primitive
│   ├── auth/
│   │   ├── LoginForm.tsx           # Client Component
│   │   └── RegisterForm.tsx        # Client Component
│   └── dashboard/
│       ├── Sidebar.tsx             # Client Component (active-link state)
│       ├── UsageSummaryCard.tsx    # Server Component
│       └── LanguageSwitcher.tsx    # Client Component
├── lib/
│   ├── supabase/
│   │   ├── server.ts               # Server-only Supabase client factory
│   │   └── client.ts               # Browser Supabase client factory
│   ├── validation/
│   │   └── auth.ts                 # Zod: SignUpSchema, SignInSchema, LanguageSchema
│   └── i18n/
│       ├── dictionaries/en.json
│       ├── dictionaries/tr.json
│       └── getDictionary.ts
└── tests/
    ├── contract/                   # One contract test per Server Action / Route Handler
    └── integration/                # Sign-up, login, OAuth, route-guard, language-switch flows
```
