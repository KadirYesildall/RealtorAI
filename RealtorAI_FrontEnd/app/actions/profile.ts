"use server";

import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { ApiError } from "@/lib/api/client";
import { completeProfile, declareMembership } from "@/lib/api/listings";
import { createClient } from "@/lib/supabase/server";
import { LanguageSchema } from "@/lib/validation/auth";
import { ProfileCompletionSchema, safeNextPath } from "@/lib/validation/listing";

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

// ═════════════════════════════ Profile completion (FR-058, FR-060) ═════════════════════════════

/** Keys under `profileCompletion.errors`. */
export type ProfileCompletionErrorCode = "validation" | "unauthenticated" | "brokerageConflict" | "unexpected";

export type ProfileCompletionField =
  | "licenseNumber"
  | "licenseState"
  | "brokerageId"
  | "brokerageName"
  | "brokerageLicenseNumber"
  | "brokerageState";

export type ProfileCompletionState =
  | { status: "idle" }
  | {
      status: "error";
      code: ProfileCompletionErrorCode;
      fieldErrors?: Partial<Record<ProfileCompletionField, string>>;
    };

const PROFILE_COMPLETION_FIELDS: readonly ProfileCompletionField[] = [
  "licenseNumber",
  "licenseState",
  "brokerageId",
  "brokerageName",
  "brokerageLicenseNumber",
  "brokerageState",
];

function isProfileCompletionField(value: PropertyKey): value is ProfileCompletionField {
  return (PROFILE_COMPLETION_FIELDS as readonly PropertyKey[]).includes(value);
}

/**
 * Story 1 scenario 7 and 8: records the license, declares the brokerage when the agent has no
 * live membership yet — which creates it `pending` (FR-060) — and returns the agent to where
 * they were going. The API authorizes and re-validates both writes; it writes only the caller's
 * own profile.
 */
export async function completeProfileAction(
  next: string,
  _previous: ProfileCompletionState,
  formData: FormData
): Promise<ProfileCompletionState> {
  const parsed = ProfileCompletionSchema.safeParse({
    licenseNumber: String(formData.get("licenseNumber") ?? ""),
    licenseState: String(formData.get("licenseState") ?? ""),
    declaresBrokerage: formData.get("declaresBrokerage") === "true",
    brokerageId: String(formData.get("brokerageId") ?? ""),
    brokerageName: String(formData.get("brokerageName") ?? ""),
    brokerageLicenseNumber: String(formData.get("brokerageLicenseNumber") ?? ""),
    brokerageState: String(formData.get("brokerageState") ?? ""),
  });

  if (!parsed.success) {
    const fieldErrors: Partial<Record<ProfileCompletionField, string>> = {};

    for (const issue of parsed.error.issues) {
      const field = issue.path[0];

      if (field !== undefined && isProfileCompletionField(field) && fieldErrors[field] === undefined) {
        fieldErrors[field] = issue.message;
      }
    }

    return { status: "error", code: "validation", fieldErrors };
  }

  const values = parsed.data;

  try {
    await completeProfile({ licenseNumber: values.licenseNumber, licenseState: values.licenseState });

    if (values.declaresBrokerage) {
      await declareMembership({
        brokerageId: values.brokerageId,
        brokerageName: values.brokerageName,
        brokerageLicenseNumber: values.brokerageId === null ? values.brokerageLicenseNumber : null,
        brokerageState: values.brokerageId === null ? values.brokerageState : null,
      });
    }
  } catch (error) {
    if (error instanceof ApiError) {
      if (error.status === 401) {
        return { status: "error", code: "unauthenticated" };
      }

      // A membership declared meanwhile, from another tab: the agent has one, which is what
      // this form was for — but they should see it rather than be redirected past it.
      if (error.status === 409) {
        return { status: "error", code: "brokerageConflict" };
      }

      if (error.status === 422) {
        const fieldErrors: Partial<Record<ProfileCompletionField, string>> = {};

        for (const field of Object.keys(error.problem?.errors ?? {})) {
          if (isProfileCompletionField(field)) {
            fieldErrors[field] = "serverRejected";
          }
        }

        return { status: "error", code: "validation", fieldErrors };
      }
    }

    console.error("Profile completion failed", error);
    return { status: "error", code: "unexpected" };
  }

  redirect(safeNextPath(next));
}
