"use server";

import { cookies } from "next/headers";
import { createClient } from "@/lib/supabase/server";
import { LanguageSchema } from "@/lib/validation/auth";

export type ProfileActionResult = { success: true } | { success: false; error: string };

export async function updateLanguagePreferenceAction(
  language: string
): Promise<ProfileActionResult> {
  const parsed = LanguageSchema.shape.language.safeParse(language);
  if (!parsed.success) {
    return { success: false, error: "Unsupported language." };
  }

  const supabase = await createClient();
  const { data: userData } = await supabase.auth.getUser();

  if (!userData.user) {
    return { success: false, error: "You must be signed in." };
  }

  const { error } = await supabase
    .from("profiles")
    .update({ preferred_language: parsed.data })
    .eq("id", userData.user.id);

  if (error) {
    return { success: false, error: "Could not update your language preference." };
  }

  (await cookies()).set("NEXT_LOCALE", parsed.data, { path: "/", sameSite: "lax" });

  return { success: true };
}
