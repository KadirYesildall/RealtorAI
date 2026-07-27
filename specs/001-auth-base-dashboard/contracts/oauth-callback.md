# Contract: OAuth Callback Route Handler

Location: `RealtorAI_FrontEnd/app/auth/callback/route.ts` — `GET /auth/callback`

This is the one Route Handler in this feature, per the constitution's rule that Route Handlers
are reserved for webhooks and external API access points (here: the Google OAuth redirect
target, which Supabase/Google call directly — it cannot be a Server Action).

**Query parameters** (set by Supabase's OAuth redirect): `code: string` (authorization code) or
`error: string` (if the user denied consent — US2 AC/Edge Cases).

**Behavior**:
1. If `error` is present (user denied/cancelled consent) → redirect to
   `/login?error=oauth_cancelled`; the login page renders "Sign-in was cancelled." (Edge Cases).
2. If `code` is present → call `supabase.auth.exchangeCodeForSession(code)`.
   - On success: a session cookie is set (via `@supabase/ssr`); the corresponding `profiles`
     row already exists or was just created by the `handle_new_user` trigger. Redirect to
     `/dashboard`.
   - On failure (invalid/expired code) → redirect to `/login?error=oauth_failed`; the login
     page renders a generic "Something went wrong signing in with Google. Please try again."
     (never the raw exchange error — Modern Principle B).
3. If neither `code` nor `error` is present (malformed request) → redirect to `/login`.

**Preconditions**: The user reached this URL only via Supabase's own OAuth redirect after
`signInWithGoogleAction` — the consent checkbox was already enforced client-side before that
action could be invoked (US2 AC4).

**Postconditions on success**: Session cookie set; exactly one `profiles` row exists for this
identity (new or reused — US2 AC1/AC2, enforced by the trigger + Supabase's identity linking,
not by this route).
