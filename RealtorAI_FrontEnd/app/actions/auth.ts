"use server";

import { redirect } from "next/navigation";
import { headers } from "next/headers";
import { createClient } from "@/lib/supabase/server";
import { SignUpSchema, SignInSchema } from "@/lib/validation/auth";

export type ActionError = {
  success: false;
  error: string;
  fieldErrors?: Partial<Record<string, string>>;
};

const GENERIC_ERROR: ActionError = {
  success: false,
  error: "Something went wrong. Please try again.",
};

export async function signUpAction(
  _prevState: ActionError | null,
  formData: FormData
): Promise<ActionError> {
  const parsed = SignUpSchema.safeParse({
    fullName: formData.get("fullName"),
    email: formData.get("email"),
    password: formData.get("password"),
    acceptedTerms: formData.get("acceptedTerms") === "on",
  });

  if (!parsed.success) {
    const fieldErrors = parsed.error.flatten().fieldErrors;
    return {
      success: false,
      error: "Please fix the highlighted fields.",
      fieldErrors: {
        fullName: fieldErrors.fullName?.[0],
        email: fieldErrors.email?.[0],
        password: fieldErrors.password?.[0],
        acceptedTerms: fieldErrors.acceptedTerms?.[0],
      },
    };
  }

  const { fullName, email, password } = parsed.data;
  const supabase = await createClient();

  const { error } = await supabase.auth.signUp({
    email,
    password,
    options: { data: { full_name: fullName } },
  });

  if (error) {
    if (error.code === "user_already_exists") {
      return {
        success: false,
        error: "An account with this email already exists.",
        fieldErrors: { email: "An account with this email already exists." },
      };
    }
    return GENERIC_ERROR;
  }

  redirect("/register/check-email");
}

export async function signInAction(
  _prevState: ActionError | null,
  formData: FormData
): Promise<ActionError> {
  const parsed = SignInSchema.safeParse({
    email: formData.get("email"),
    password: formData.get("password"),
  });

  if (!parsed.success) {
    const fieldErrors = parsed.error.flatten().fieldErrors;
    return {
      success: false,
      error: "Please fix the highlighted fields.",
      fieldErrors: {
        email: fieldErrors.email?.[0],
        password: fieldErrors.password?.[0],
      },
    };
  }

  const supabase = await createClient();
  const { error } = await supabase.auth.signInWithPassword(parsed.data);

  if (error) {
    if (error.code === "email_not_confirmed") {
      return {
        success: false,
        error: "Please verify your email before logging in.",
        fieldErrors: { _resend: "true" },
      };
    }
    if (error.code === "invalid_credentials") {
      return { success: false, error: "Incorrect email or password." };
    }
    return GENERIC_ERROR;
  }

  redirect("/dashboard");
}

export type ResendResult = { success: true } | ActionError;

export async function resendConfirmationAction(email: string): Promise<ResendResult> {
  const parsed = SignInSchema.shape.email.safeParse(email);
  if (!parsed.success) {
    return { success: false, error: "Enter your email above first." };
  }

  const supabase = await createClient();
  const { error } = await supabase.auth.resend({ type: "signup", email: parsed.data });

  if (error) {
    return GENERIC_ERROR;
  }

  return { success: true };
}

export async function signInWithGoogleAction(): Promise<ActionError> {
  const supabase = await createClient();
  const origin = (await headers()).get("origin");

  const { data, error } = await supabase.auth.signInWithOAuth({
    provider: "google",
    options: { redirectTo: `${origin}/auth/callback` },
  });

  if (error || !data.url) {
    return { success: false, error: "Google sign-in is unavailable right now." };
  }

  redirect(data.url);
}

export async function signOutAction(): Promise<ActionError> {
  const supabase = await createClient();
  const { error } = await supabase.auth.signOut();

  if (error) {
    return { success: false, error: "Could not sign out. Please try again." };
  }

  redirect("/login");
}
