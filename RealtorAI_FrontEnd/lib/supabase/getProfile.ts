import "server-only";
import { cache } from "react";
import { redirect } from "next/navigation";
import { createClient } from "@/lib/supabase/server";

export type Profile = {
  id: string;
  fullName: string | null;
  companyName: string | null;
  preferredLanguage: string;
  subscriptionTier: string;
  apiUsageCount: number;
};

/**
 * Auth guard + own-profile fetch, memoized per request (React cache) so dashboard
 * layout.tsx and page.tsx share one DB round trip instead of two.
 */
export const getAuthenticatedProfile = cache(async (): Promise<Profile> => {
  const supabase = await createClient();
  const { data: userData } = await supabase.auth.getUser();

  if (!userData.user) {
    redirect("/login");
  }

  const { data: profile } = await supabase
    .from("profiles")
    .select("id, full_name, company_name, preferred_language, subscription_tier, api_usage_count")
    .eq("id", userData.user.id)
    .single();

  if (!profile) {
    // Auto-provision trigger hasn't landed yet (rare race just after signup) — treat as
    // a temporary loading state rather than crashing (spec Edge Cases).
    redirect("/login");
  }

  return {
    id: profile.id,
    fullName: profile.full_name,
    companyName: profile.company_name,
    preferredLanguage: profile.preferred_language,
    subscriptionTier: profile.subscription_tier,
    apiUsageCount: profile.api_usage_count,
  };
});
