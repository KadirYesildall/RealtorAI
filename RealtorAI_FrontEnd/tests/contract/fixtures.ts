import { createClient } from "@supabase/supabase-js";

// Service-role client for TEST FIXTURE SETUP ONLY (e.g. force-confirming a user's email to
// reach states Supabase's own flows would otherwise require a real inbox for). Application
// code must never use the service-role key — see the constitution's anon-key-only constraint.
const adminClient = createClient(
  process.env.NEXT_PUBLIC_SUPABASE_URL!,
  process.env.SUPABASE_SERVICE_ROLE_KEY!,
  { auth: { autoRefreshToken: false, persistSession: false } }
);

export async function confirmEmail(email: string) {
  const { data, error } = await adminClient.auth.admin.listUsers();
  if (error) throw error;
  const user = data.users.find((u) => u.email === email);
  if (!user) throw new Error(`Fixture setup: no user found for ${email}`);

  const { error: updateError } = await adminClient.auth.admin.updateUserById(user.id, {
    email_confirm: true,
  });
  if (updateError) throw updateError;
}
