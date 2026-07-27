# Contract: Profile Server Actions

Location: `RealtorAI_FrontEnd/app/actions/profile.ts`

## `updateLanguagePreferenceAction`

**Input** (`LanguageSchema`, Zod):

```ts
{ language: 'en' | 'tr' }
```

**Behavior**:
1. Parse input. On failure (value outside `'en' | 'tr'`) → `{ success: false, error }`.
2. Require an authenticated session (`supabase.auth.getUser()`); if none, →
   `{ success: false, error: "You must be signed in." }` (defense in depth — this action is
   only ever called from an authenticated dashboard context, but per Modern Principle B every
   action validates its own preconditions rather than trusting the caller).
3. `UPDATE public.profiles SET preferred_language = :language WHERE id = auth.uid()` — RLS's
   `profiles_update_own` policy (data-model.md) is the actual enforcement; the `auth.uid()`
   filter here is defense in depth, not the sole guard.
4. Set the `NEXT_LOCALE` cookie to the same value so subsequent server-rendered pages in the
   same request cycle reflect the change immediately (FR-012, SC-004's "within one page load").
5. On success → `{ success: true }`. On any DB error → `{ success: false, error: "Could not update your language preference." }`.

**Preconditions**: Caller is authenticated (checked in step 2, enforced by RLS regardless).
**Postconditions on success**: `profiles.preferred_language` and the `NEXT_LOCALE` cookie agree.
