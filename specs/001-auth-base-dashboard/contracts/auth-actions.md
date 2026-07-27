# Contract: Auth Server Actions

Location: `RealtorAI_FrontEnd/app/actions/auth.ts`

All four actions are `"use server"` functions, callable only from the app itself (no public
HTTP surface). On success, each action calls Next's `redirect()` directly (the framework's
documented pattern — `redirect()` throws a control-flow exception, so the function never
returns normally on that path). On failure, each action returns the error shape below
(constitution Modern Principle B) instead of throwing:

```ts
type ActionError = {
  success: false;
  error: string;
  fieldErrors?: Partial<Record<string, string>>;
};
```

No action throws an unhandled/raw error to its caller — every Supabase/network error is caught
and mapped into `{ success: false, error }` with a user-safe message (never the raw
Supabase/Postgres error). Callers (Client Components using `useActionState`) only ever see a
defined `state` on the error path; the success path is a navigation, not a state update.

---

## `signUpAction`

**Input** (`SignUpSchema`, Zod):

```ts
{
  fullName: string;       // min 2, max 100 chars
  email: string;          // valid email format
  password: string;       // min 8 chars
  acceptedTerms: true;    // Zod literal(true) — must be exactly true
}
```

**Behavior**:
1. Parse input with `SignUpSchema`. On failure → `{ success: false, error, fieldErrors }`
   (covers FR-002 and FR-016's server-side re-validation of consent).
2. Call `supabase.auth.signUp({ email, password, options: { data: { full_name: fullName } } })`.
3. If the error's `code` is `user_already_exists` (fires only when the existing account is
   already **confirmed** — Supabase Auth's anti-enumeration design makes an unconfirmed
   duplicate indistinguishable from a fresh sign-up, verified by direct testing, not assumed)
   → return `{ success: false, error: "An account with this email already exists." }`
   (US1 AC4).
4. On any other Supabase/network error → return a generic
   `{ success: false, error: "Something went wrong. Please try again." }` (never the raw
   Supabase error — Modern Principle B, FR-010).
5. On success → `redirect("/register/check-email")` (US1 AC1; the `handle_new_user` trigger
   creates the `profiles` row server-side, no extra call needed).

**Preconditions**: None (public entry point).
**Postconditions on success**: Exactly one `auth.users` row and one `profiles` row exist for
the new email; `profiles.accepted_terms_at` is set (DB default, per data-model.md).

---

## `signInAction`

**Input** (`SignInSchema`, Zod): `{ email: string; password: string }`.

**Behavior**:
1. Parse input. On failure → `{ success: false, error, fieldErrors }`.
2. Call `supabase.auth.signInWithPassword({ email, password })`.
3. If Supabase reports "email not confirmed" → `{ success: false, error: "Please verify your email before logging in.", fieldErrors: { _resend: "true" } }`
   (FR-011; UI uses this to show the resend-confirmation option).
4. If Supabase reports invalid credentials → `{ success: false, error: "Incorrect email or password." }`
   (US1 AC3) — deliberately does not reveal whether the email exists (standard practice; not
   an explicit FR but consistent with FR-010's "no raw/leaky error" intent).
5. On success → `redirect("/dashboard")` (US1 AC2).

**Preconditions**: None.
**Postconditions on success**: A valid Supabase session cookie is set via `@supabase/ssr`.

---

## `signInWithGoogleAction`

**Input**: none.

**Behavior**:
1. Call `supabase.auth.signInWithOAuth({ provider: 'google', options: { redirectTo: '<origin>/auth/callback' } })`.
   Running server-side, this makes no browser navigation itself — it just returns the
   provider's authorization URL for the action to redirect to.
2. On success, Supabase returns `{ data: { url } }` → `redirect(url)` (Next's `redirect()`
   accepts external absolute URLs). **Note (verified by testing, not assumed)**: this call
   succeeds and returns a URL even if the Google provider is disabled/unconfigured in
   `config.toml` — Supabase only validates provider configuration when the browser actually
   follows that URL to `/auth/v1/authorize`, not when this action constructs it. A disabled
   provider therefore surfaces as an error on Supabase's authorize page, not from this action.
3. On failure to initiate (a genuine client/network failure calling Supabase — not a
   provider-configuration issue, see above) → `{ success: false, error: "Google sign-in is unavailable right now." }`.

**Preconditions**: The consent checkbox MUST be checked before this action is invoked — enforced
client-side by disabling the "Continue with Google" button (US2 AC4); this action does not
re-validate consent server-side since it never creates a profile itself (the callback route
and trigger do that; see `oauth-callback.md`).

---

## `signOutAction`

**Input**: none.

**Behavior**: Calls `supabase.auth.signOut()`, clearing the session cookie, then
`redirect("/login")` (FR-015). Errors are caught and mapped to
`{ success: false, error: "Could not sign out. Please try again." }` — extremely unlikely in
practice but still never left unhandled (Modern Principle B).
