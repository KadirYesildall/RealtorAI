import { describe, it, expect, vi, beforeEach } from "vitest";
import { confirmEmail } from "./fixtures";

// Server Actions call next/headers (cookies/headers) and next/navigation (redirect), both of
// which require Next's request-scoped AsyncLocalStorage context — unavailable when a Server
// Action is invoked directly from a plain Vitest process. Mocking both lets these contract
// tests exercise real validation + real Supabase calls against the local stack (research.md
// §6) without needing a full Next dev server (that's what the Playwright integration tests
// in tests/integration/ are for).
const { cookieStore } = vi.hoisted(() => ({
  cookieStore: new Map<string, string>(),
}));

class RedirectSignal extends Error {
  constructor(public target: string) {
    super(`REDIRECT:${target}`);
  }
}

vi.mock("next/headers", () => ({
  cookies: async () => ({
    getAll: () => Array.from(cookieStore.entries()).map(([name, value]) => ({ name, value })),
    set: (name: string, value: string) => cookieStore.set(name, value),
  }),
  headers: async () => new Headers({ origin: "http://localhost:3000" }),
}));

vi.mock("next/navigation", () => ({
  redirect: (target: string) => {
    throw new RedirectSignal(target);
  },
}));

const {
  signUpAction,
  signInAction,
  signOutAction,
  resendConfirmationAction,
  signInWithGoogleAction,
} = await import("@/app/actions/auth");
const { GET: oauthCallback } = await import("@/app/auth/callback/route");

function formDataOf(fields: Record<string, string>) {
  const fd = new FormData();
  for (const [key, value] of Object.entries(fields)) fd.set(key, value);
  return fd;
}

function uniqueEmail() {
  return `contract-test-${Date.now()}-${Math.random().toString(36).slice(2)}@example.com`;
}

beforeEach(() => {
  cookieStore.clear();
});

describe("signUpAction", () => {
  it("returns field errors for invalid input without calling Supabase", async () => {
    const result = await signUpAction(
      null,
      formDataOf({ fullName: "A", email: "not-an-email", password: "short", acceptedTerms: "" })
    );
    expect(result.success).toBe(false);
    expect(result.fieldErrors?.fullName).toBeTruthy();
    expect(result.fieldErrors?.email).toBeTruthy();
    expect(result.fieldErrors?.password).toBeTruthy();
    expect(result.fieldErrors?.acceptedTerms).toBeTruthy();
  });

  it("rejects submission when consent is not accepted, even with otherwise valid input", async () => {
    const result = await signUpAction(
      null,
      formDataOf({
        fullName: "Jane Agent",
        email: uniqueEmail(),
        password: "password123",
        acceptedTerms: "",
      })
    );
    expect(result.success).toBe(false);
    expect(result.fieldErrors?.acceptedTerms).toBeTruthy();
  });

  it("redirects to /register/check-email on successful registration", async () => {
    const email = uniqueEmail();
    await expect(
      signUpAction(
        null,
        formDataOf({
          fullName: "Jane Agent",
          email,
          password: "password123",
          acceptedTerms: "on",
        })
      )
    ).rejects.toMatchObject({ target: "/register/check-email" });
  });

  it("silently resends confirmation for a duplicate *unconfirmed* email (Supabase's built-in anti-enumeration behavior — verified, not assumed)", async () => {
    const email = uniqueEmail();
    const fields = {
      fullName: "Jane Agent",
      email,
      password: "password123",
      acceptedTerms: "on",
    };

    await expect(signUpAction(null, formDataOf(fields))).rejects.toMatchObject({
      target: "/register/check-email",
    });
    // Local config rate-limits confirmation emails to 1/second (config.toml auth.email.
    // max_frequency); wait it out so this assertion is about the *identity* behavior
    // (not "already exists"), not about hitting that unrelated rate limiter.
    await new Promise((resolve) => setTimeout(resolve, 1100));
    // Re-submitting the same unconfirmed email behaves exactly like a fresh sign-up —
    // it is NOT reported as "already exists" (that only fires for confirmed accounts,
    // see the next test).
    await expect(signUpAction(null, formDataOf(fields))).rejects.toMatchObject({
      target: "/register/check-email",
    });
  });

  it("returns an account-exists error when the existing account is already confirmed", async () => {
    const email = uniqueEmail();
    const fields = {
      fullName: "Jane Agent",
      email,
      password: "password123",
      acceptedTerms: "on",
    };

    await expect(signUpAction(null, formDataOf(fields))).rejects.toBeInstanceOf(RedirectSignal);
    await confirmEmail(email);

    const second = await signUpAction(null, formDataOf(fields));
    expect(second.success).toBe(false);
    expect(second.error).toBe("An account with this email already exists.");
  });
});

describe("signInAction", () => {
  it("returns field errors for invalid input", async () => {
    const result = await signInAction(
      null,
      formDataOf({ email: "not-an-email", password: "short" })
    );
    expect(result.success).toBe(false);
    expect(result.fieldErrors?.email).toBeTruthy();
    expect(result.fieldErrors?.password).toBeTruthy();
  });

  it("returns an unverified-email error for an unconfirmed account", async () => {
    const email = uniqueEmail();
    await expect(
      signUpAction(
        null,
        formDataOf({
          fullName: "Jane Agent",
          email,
          password: "password123",
          acceptedTerms: "on",
        })
      )
    ).rejects.toBeInstanceOf(RedirectSignal);

    const result = await signInAction(null, formDataOf({ email, password: "password123" }));
    expect(result.success).toBe(false);
    expect(result.error).toBe("Please verify your email before logging in.");
    expect(result.fieldErrors?._resend).toBe("true");
  });

  it("returns a generic invalid-credentials error for a wrong password", async () => {
    const result = await signInAction(
      null,
      formDataOf({ email: uniqueEmail(), password: "wrongpassword" })
    );
    expect(result.success).toBe(false);
    expect(result.error).toBe("Incorrect email or password.");
  });
});

describe("resendConfirmationAction", () => {
  it("rejects a malformed email without calling Supabase", async () => {
    const result = await resendConfirmationAction("not-an-email");
    expect(result.success).toBe(false);
  });
});

describe("signOutAction", () => {
  it("redirects to /login", async () => {
    await expect(signOutAction()).rejects.toMatchObject({ target: "/login" });
  });
});

describe("signInWithGoogleAction", () => {
  it("redirects to Supabase's Google authorize URL, even though the provider is disabled locally (verified: Supabase only rejects a disabled provider when the browser follows the URL to /auth/v1/authorize, not when this action constructs it)", async () => {
    let target: string | undefined;
    try {
      await signInWithGoogleAction();
    } catch (e) {
      target = (e as RedirectSignal).target;
    }
    expect(target).toContain("/auth/v1/authorize");
    expect(target).toContain("provider=google");
  });
});

describe("GET /auth/callback", () => {
  it("redirects to /login?error=oauth_cancelled when the provider reports an error", async () => {
    const res = await oauthCallback(
      new Request("http://localhost:3000/auth/callback?error=access_denied")
    );
    expect(res.status).toBe(307);
    expect(res.headers.get("location")).toBe("http://localhost:3000/login?error=oauth_cancelled");
  });

  it("redirects to /login?error=oauth_failed for an invalid/expired code", async () => {
    const res = await oauthCallback(
      new Request("http://localhost:3000/auth/callback?code=not-a-real-code")
    );
    expect(res.status).toBe(307);
    expect(res.headers.get("location")).toBe("http://localhost:3000/login?error=oauth_failed");
  });

  it("redirects to /login for a malformed request with neither code nor error", async () => {
    const res = await oauthCallback(new Request("http://localhost:3000/auth/callback"));
    expect(res.status).toBe(307);
    expect(res.headers.get("location")).toBe("http://localhost:3000/login");
  });
});
