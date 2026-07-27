# Phase 0 Research: Authentication & Base Dashboard

No `[NEEDS CLARIFICATION]` markers remained in `spec.md` after the `/speckit-clarify` session,
so this research focuses on technical unknowns implied by the Technical Context: which
libraries/patterns to use for Supabase SSR auth, local dev stand-up, i18n, and UI primitives.

## 1. Supabase auth integration with Next.js App Router

**Decision**: Use `@supabase/ssr` for both the server-side client (`lib/supabase/server.ts`,
built from `cookies()`) and the browser client (`lib/supabase/client.ts`), plus a
`proxy.ts` that refreshes the Supabase session cookie on every request.

**Rationale**: `@supabase/ssr` is Supabase's current, actively maintained package purpose-built
for cookie-based SSR session handling in Next.js App Router; it replaces the now-deprecated
`@supabase/auth-helpers-nextjs`. Using it correctly (server client for Server
Components/Actions, browser client for Client Components, proxy for cookie refresh) is
the documented pattern for exactly this feature's shape (Server Actions + route guarding).

**Alternatives considered**:
- `@supabase/auth-helpers-nextjs` — rejected: deprecated in favor of `@supabase/ssr`.
- Rolling a custom cookie/JWT session handler — rejected: reinvents what Supabase already
  provides correctly, violates Principle V (YAGNI) and Principle III (higher hallucination
  risk in hand-rolled auth/session code).

**Follow-up (Principle III)**: Confirm the exact current API surface (`createServerClient`,
`createBrowserClient` signatures) against the installed package version's own type
definitions/docs at implementation time — do not hand-write calls from memory without that
check, since Supabase has changed this API shape across major versions before.

## 2. Route protection strategy

**Decision**: Enforce the auth guard in `app/dashboard/layout.tsx` (Server Component) via
`supabase.auth.getUser()`, redirecting to `/login` on a null user — not via `proxy.ts`
matcher-based blocking.

**Rationale**: A Server Component guard keeps the redirect logic co-located with the protected
route tree (matches Modern Principle A's Server-Component-first guidance) and avoids
duplicating auth logic between proxy and layout. Proxy in this feature is scoped
narrowly to session-cookie refresh and locale-cookie detection, not authorization — keeping
its responsibility single and simple (Principle V).

**Alternatives considered**: Proxy-based route blocking for `/dashboard/*` — viable and
common, but would split "is this route protected" logic across two files (proxy matcher +
layout) for one route tree; rejected as unnecessary duplication for this feature's scope.

## 3. Local Supabase stack & DBeaver verification

**Decision**: `supabase init` + `supabase start` from `RealtorAI_BackEnd/`, which requires
Docker Desktop running locally. Confirmed public defaults (Supabase CLI, current as of
project's knowledge): API `http://127.0.0.1:54321`, DB `postgresql://postgres:postgres@127.0.0.1:54322/postgres`,
Studio `http://127.0.0.1:54323`, the local email testing inbox (used to "verify" email
confirmation in FR-011 without a real mail provider) `http://127.0.0.1:54324`.

**Rationale**: Matches the constitution's Local-first development phase rule exactly, and
The local email testing inbox gives a way to satisfy FR-011 (email verification) entirely locally without any real
SMTP provider — closing a gap the spec didn't explicitly resolve (this is the plan's job, not
the spec's).

**Follow-up (Principle III)**: These ports are the Supabase CLI's long-standing documented
defaults, but MUST be confirmed against the actual `supabase start` output (and
`RealtorAI_BackEnd/supabase/config.toml`) during implementation before being relied on, exactly
as the spec's "Local Development & Verification" section already requires.

**Alternatives considered**: Pointing directly at a hosted Supabase free-tier project instead
of running locally — rejected outright; contradicts the constitution's Local-first rule
explicitly added for this project.

## 4. Internationalization approach

**Decision**: No URL-based locale routing (no `app/[lang]/` segment). A `NEXT_LOCALE` cookie
(set by `proxy.ts` from the `Accept-Language` header on first visit, or from the user's
saved `profiles.preferred_language` after login) drives a server-side `getDictionary(locale)`
lookup returning a plain JSON object of translation keys.

**Rationale**: FR-012/013 require persisted-preference-driven language, not shareable localized
URLs — the spec never asked for `/tr/dashboard`-style routing. A cookie + dictionary approach
satisfies every functional requirement with far less complexity than a full i18n routing
library (Principle V, YAGNI).

**Alternatives considered**: `next-intl` or `next-i18next` — capable, well-supported libraries,
but bring URL-segment routing and provider/config overhead this feature's requirements don't
call for; rejected as premature for a 2-locale, non-URL-localized MVP. Can be introduced later
if URL-based localization becomes an actual requirement.

## 5. Shared UI primitives & design tokens

**Decision**: Hand-built `Button`, `Input`, `Card` primitives in `components/ui/`, styled with
Tailwind and variant-managed via `class-variance-authority` (`cva`), composed with a `cn()`
helper (`clsx` + `tailwind-merge`). All spacing/color/radius/type values sourced from
`app/globals.css` (Tailwind v4 `@theme` tokens) tokens, per the constitution's UI/UX Design Standards (Section E).

**Rationale**: `cva` + `cn()` is the standard, lightweight pattern for maintainable
Tailwind component variants (used by shadcn/ui and most modern Tailwind design systems)
without pulling in a full component library — keeps the "real-estate-appropriate, premium,
consistent" bar from Section E achievable without heavy dependencies.

**Alternatives considered**: A pre-built component library (e.g., shadcn/ui's CLI-generated
components, Radix + custom styling, MUI) — shadcn/ui specifically is a reasonable future
option since it's compatible with this exact `cva`/Tailwind foundation, but is deferred for
this first feature to keep the dependency surface minimal (Principle V); nothing here blocks
adopting it later since the token/primitive strategy is compatible.

## 6. Testing strategy

**Decision**: Vitest for contract tests (Zod schema validation, Server Action success/error
shape, RLS policy assertions run via a direct Postgres connection to the local Supabase DB) and
Playwright for integration/e2e tests (full browser flows per user story, including the
the local email testing inbox-based email-confirmation flow and Google OAuth — the latter tested against a Google
test account per Playwright's browser automation, or stubbed at the Supabase Auth layer if a
real Google test account isn't available in CI).

**Rationale**: Matches Principle II (contract tests per API surface, integration tests per
cross-cutting flow) using widely-adopted, actively maintained tools with first-class Next.js
App Router support.

**Follow-up**: Whether Google OAuth e2e testing uses a real test Google account or a
Supabase Auth-level stub is an implementation-time decision for `/speckit-tasks`, not a spec-
level one — either satisfies User Story 2's Independent Test.
