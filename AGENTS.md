# RealtorAI — Agent Context

Authoritative rules live in `.specify/memory/constitution.md` (currently **v3.0.0**). This file is
the fast orientation; the constitution wins on any conflict.

## Repository layout

| Path | What it is |
|---|---|
| `RealtorAI_FrontEnd/` | Next.js app — **read its own `AGENTS.md` first** |
| `RealtorAI_BackEnd/supabase/` | Supabase project: **owns all DDL** via `migrations/` |
| `RealtorAI_BackEnd/RealtorAI.Api/` | ASP.NET Core Web API (planned in feature 002) |
| `RealtorAI_BackEnd/vision-sidecar/` | Python FastAPI vision sidecar (planned in feature 002) |
| `specs/` | Feature specs, plans, data models, contracts |

Nothing application-specific belongs at the repo root outside these directories.

## Stack facts (verified, not assumed)

- **Frontend**: Next.js **16.2.10**, React **19.2.4**, Tailwind **v4** (CSS-first — there is no
  `tailwind.config.ts`; tokens live in `app/globals.css`), Zod **v4**, Vitest + Playwright.
- **Backend**: C# **.NET 10**. The constitution text still says "8/9" — a PATCH amendment is
  pending; see `specs/002-ai-listing-launch/plan.md` Complexity Tracking.
- **Database**: Supabase Postgres **17** on `127.0.0.1:54322` locally; API gateway `54321`,
  Studio `54323`, Inbucket `54324`. RLS enabled on every table.
- **Auth**: Supabase Auth (email/password + Google OAuth, the latter parked pending credentials).

## This is NOT the Next.js you know

`RealtorAI_FrontEnd/AGENTS.md` requires reading `node_modules/next/dist/docs/` before writing
frontend code. Breaking changes that silently produce wrong code:

- **`proxy.ts`, not `middleware.ts`** (already correct in this repo); export is named `proxy`; the
  `edge` runtime is unsupported there.
- **Async request APIs are mandatory** — `await cookies()`, `await headers()`, `await params`,
  `await searchParams`. Synchronous access was removed in 16.
- **`revalidateTag(tag, cacheLife)` requires the second argument.** For read-your-writes after a
  mutation use `updateTag(tag)`; use `refresh()` to refresh the client router from a Server Action.
- `cacheLife` / `cacheTag` are stable (no `unstable_` prefix). PPR is opt-in via top-level
  `cacheComponents`. Turbopack is the default; `next dev` writes to `.next/dev`. `next lint` is
  gone — call `eslint` directly.
- `npx next typegen` generates `PageProps<'/route'>`, `LayoutProps`, `RouteContext`.

## Non-negotiables

**AI layer (Constitution VIII)**
- No vendor, model id, or endpoint in business code. All AI goes through `AiService` in the .NET
  API, selected per task from the **model registry**. Changing a model is configuration, audited.
- A model may be enabled only with a recorded commercial license **and** a passing evaluation
  **and** a clean data-boundary check. Enforced by a DB check constraint, not convention.
- Every AI output is schema-validated against a versioned JSON Schema; prompts are versioned repo
  files and their version/hash is logged per call.
- **No generative imagery, ever** — no virtual staging, inpainting, object removal, sky
  replacement, digital renovation, generative upscaling, or AI video. Only resize, compress,
  convert, crop, rotate, order, select. Original uploads are immutable. A build-failing test
  prohibits any image/video-generation dependency or endpoint.
- Every AI output is a **draft** until explicit, logged human approval. Nothing reaches an MLS or
  any public channel without it.

**Other standing rules**
- **Spec before code.** No implementation without a reviewed `spec.md` (Principle I).
- **Test-first.** Contract tests per endpoint; integration tests for cross-service flows.
- **No placeholders.** No `TODO: implement later`, no stubs, no partial logic — split the task
  instead.
- **Verify dependencies.** If an SDK/API/config detail is uncertain, halt and ask. Do not guess
  package names, method signatures, or config keys. Record assumptions explicitly.
- **i18n**: every user-facing string is a key in both `en.json` and `tr.json`. No hardcoded text.
- **Dark mode is baseline**, not per-feature. Use the semantic `--color-*` tokens; never `dark:`
  variants, never raw hex. WCAG AA contrast verified in **both** themes.
- **Security**: RLS on every table. Where the .NET API uses a privileged service role, RLS is
  **not** the enforcement boundary — every endpoint authorizes independently (FR-063), with RLS as
  defense in depth. Never read authorization state from a JWT claim; load it per request.
- Secrets are server-only; never `NEXT_PUBLIC_`-prefixed.

## Local stack startup (this machine)

Order matters:

1. Launch Docker Desktop from `C:\Users\ASUS\AppData\Local\Programs\DockerDesktop\Docker Desktop.exe`
   (**not** `C:\Program Files\Docker\`), then confirm `docker ps` succeeds.
2. `cd RealtorAI_BackEnd` → `npx --yes supabase@latest start`. The Supabase CLI is **not** global
   and **not** on PATH; always use `npx`.
3. `cd RealtorAI_FrontEnd` → `npm run dev`.

**Never start the Supabase containers from the Docker Desktop UI** — it fails with
`no container found for project "realtorai_backend"`. Use `npx supabase start`, which recreates
them.

## Current state

- **001-auth-base-dashboard** — implemented: auth, profiles with RLS, localized route-guarded
  dashboard shell, UI primitives (`Button`, `Card`, `Input`, `TextField`, `Alert`, `Checkbox`).
- **002-ai-listing-launch** — spec + plan complete for **Slice A** (Stories 1–5). Slice B (CRMLS
  submission, marketing, publishing) and Slice C (lifecycle, leads, insights) are specced but not
  planned. Next step is `/speckit-tasks` for Slice A.
