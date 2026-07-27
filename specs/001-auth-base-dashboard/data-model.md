# Phase 1 Data Model: Authentication & Base Dashboard

Source of truth for the schema is `spec.md`'s "Database Schema Changes" section
(`RealtorAI_BackEnd/supabase/migrations/0001_profiles.sql`); this document restates it as a
data model for planning/task-generation purposes and adds the relationships/validation view
the migration file doesn't spell out on its own.

## Entities

### Workspace Profile (`public.profiles`)

One row per authenticated user; created automatically by the `handle_new_user` trigger the
instant a corresponding `auth.users` row is created (email/password or Google — see spec's
Database Schema Changes for the trigger definition).

| Field | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `uuid` | PK, FK → `auth.users.id`, `ON DELETE CASCADE` | Same value as the Supabase Auth identity; deleting the auth user deletes the profile. |
| `updated_at` | `timestamptz` | `NOT NULL`, default now (UTC) | Bumped on any profile update (e.g., language change). |
| `full_name` | `text` | nullable | From `raw_user_meta_data.full_name` at sign-up; Google sign-ins may populate this from the Google profile. |
| `company_name` | `text` | nullable | Not collected at sign-up in this feature; editable later via Settings (out of scope here). |
| `preferred_language` | `varchar(5)` | `NOT NULL`, default `'en'` | Values constrained by application logic to `'en'` \| `'tr'` (FR-012/013); no DB check constraint added — enforced by `LanguageSchema` (Zod) to keep the migration simple (YAGNI) since the app is the only writer. |
| `subscription_tier` | `varchar(20)` | `NOT NULL`, default `'free'` | Read-only in this feature; owned by the future Subscription & Paywall feature. |
| `api_usage_count` | `int` | `NOT NULL`, default `0` | Read-only in this feature; owned by the future AI Generation feature. |
| `accepted_terms_at` | `timestamptz` | `NOT NULL`, default now (UTC) | Set implicitly at profile-creation time; UI (FR-016) guarantees creation never happens without consent — see spec's "Consent note". |

**Validation rules** (enforced in `lib/validation/auth.ts` and `lib/validation/profile.ts`,
not the DB, per the "app is sole writer" reasoning above):
- `full_name`: 2–100 characters when supplied at sign-up.
- `preferred_language`: literal union `'en' | 'tr'`.
- `accepted_terms_at`/consent: not directly writable by any Server Action input — it is a
  DB-side default, never a client-supplied value (prevents a client from spoofing an earlier
  consent timestamp).

**Access control** (RLS, from spec): `profiles_select_own` and `profiles_update_own`, both
`USING (auth.uid() = id)`. No `INSERT`/`DELETE` policy — rows are created only by the
`SECURITY DEFINER` trigger and deleted only via the `auth.users` cascade. This is a
default-deny table: any access pattern not covered by these two policies is rejected by
Postgres RLS, not by application code.

**State transitions**: None beyond creation (on first auth) → updates (language preference,
future company_name edits) → deletion (cascades from `auth.users` deletion, out of scope for
this feature's own UI).

### Authentication Session (Supabase Auth, not a custom table)

Not a table this feature creates — it's Supabase Auth's own session, referenced here only to
document what the app relies on: `supabase.auth.getUser()` server-side to check for a valid
session (used by the dashboard route guard), and the `@supabase/ssr` cookie mechanism to
persist it across requests. No custom `sessions` table is introduced (would duplicate what
Supabase Auth already owns — rejected in `research.md` §1).

## Relationships

```text
auth.users (Supabase-managed)
   │ 1:1, ON DELETE CASCADE
   ▼
public.profiles
```

No other entities are introduced by this feature. `profiles` will later be referenced by
future features (Property listings → `profiles.id` as owner; Subscription & Paywall →
`profiles.subscription_tier`/`api_usage_count`), but those relationships are out of scope here
and MUST NOT be pre-built speculatively (Principle V, YAGNI) — this feature adds only the
`profiles` table itself.
