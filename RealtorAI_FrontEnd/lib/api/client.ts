import type { ProblemDetails } from "@/lib/api/types";

/**
 * T012 — typed client for the .NET API (`RealtorAI_BackEnd/RealtorAI.Api`).
 *
 * This is deliberately separate from the Supabase clients in `lib/supabase/`. Those talk to
 * PostgREST with the anon key and rely on RLS; this talks to the .NET API, which owns every
 * operation that needs more than a row-level rule — AI dispatch, compliance evaluation,
 * approval with a content hash, audit writes. Routing a listing mutation through PostgREST
 * would skip all of that, so the two clients are not interchangeable and are not merged.
 *
 * The access token comes from the Supabase session and is sent as a bearer token; the API
 * validates it against Supabase's JWKS and trusts only `sub`
 * (`Api/Authentication/SupabaseJwtSetup.cs`). Nothing here sends membership status or role —
 * the API resolves those from the database per request, because a token can outlive a
 * membership decision by up to an hour.
 */

/** Base URL of the .NET API. Server-side and browser-side builds read different variables. */
function resolveBaseUrl(): string {
  // NEXT_PUBLIC_ is required for the browser bundle. The value is a public URL, not a secret;
  // no key or token is ever exposed this way (constitution: no secrets in NEXT_PUBLIC_).
  const url = process.env.NEXT_PUBLIC_API_BASE_URL ?? process.env.API_BASE_URL;

  if (!url) {
    throw new Error(
      "NEXT_PUBLIC_API_BASE_URL is not set. The local default is http://127.0.0.1:5xxx — " +
        "take the actual port from `dotnet run` output rather than assuming it " +
        "(see specs/002-ai-listing-launch/quickstart.md)."
    );
  }

  return url.replace(/\/+$/, "");
}

/**
 * Thrown for any non-2xx response. Carries the parsed RFC 9457 problem details when the API
 * sent them, so callers can branch on a machine-readable reason rather than on message text.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails | undefined,
    message: string
  ) {
    super(message);
    this.name = "ApiError";
  }

  /**
   * FR-066: the row version sent in `If-Match` no longer matches, so someone else changed the
   * record. Callers must refetch and show the user what changed rather than retrying blindly —
   * a silent retry would overwrite an edit the agent never saw.
   */
  get isVersionConflict(): boolean {
    return this.status === 409;
  }

  /** The token is missing, expired, or invalid. The caller should re-authenticate. */
  get isUnauthenticated(): boolean {
    return this.status === 401;
  }

  /**
   * Authenticated but not permitted. Distinct from 401 on purpose: for a pending brokerage
   * member this is the expected, non-exceptional answer and the UI explains the pending state
   * rather than bouncing the user to the login page.
   */
  get isForbidden(): boolean {
    return this.status === 403;
  }
}

export interface RequestOptions {
  /** HTTP method. Defaults to GET. */
  method?: "GET" | "POST" | "PATCH" | "PUT" | "DELETE";

  /** JSON-serialisable request body. Mutually exclusive with `formData`. */
  body?: unknown;

  /**
   * Multipart body, for photo upload. Sent as-is with no `Content-Type` header, so the browser
   * sets the boundary itself.
   */
  formData?: FormData;

  /**
   * Row version for a conditional update (FR-066). Sent as `If-Match`; the API answers 409
   * with problem details when it does not match, surfaced as {@link ApiError.isVersionConflict}.
   */
  ifMatch?: string | number;

  /** Forwarded to fetch, so a caller can cancel in-flight work on unmount. */
  signal?: AbortSignal;

  /**
   * Next's fetch cache behaviour. Defaults to "no-store": every endpoint here returns
   * agent-scoped data, and a cached listing response served to the wrong agent would be a
   * data leak, so the safe default is explicit rather than inherited.
   */
  cache?: RequestCache;
}

/** Resolves the caller's Supabase access token, or null when not signed in. */
export type TokenResolver = () => Promise<string | null>;

async function request<TResponse>(
  path: string,
  getToken: TokenResolver,
  options: RequestOptions = {}
): Promise<TResponse> {
  const { method = "GET", body, formData, ifMatch, signal, cache = "no-store" } = options;

  if (body !== undefined && formData !== undefined) {
    throw new Error("Pass either `body` or `formData`, not both.");
  }

  const token = await getToken();

  const headers: Record<string, string> = { Accept: "application/json" };

  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  if (ifMatch !== undefined) {
    // Quoted per RFC 9110; the API compares the row_version inside the quotes.
    headers["If-Match"] = `"${ifMatch}"`;
  }

  const response = await fetch(`${resolveBaseUrl()}${path}`, {
    method,
    headers,
    body: formData ?? (body !== undefined ? JSON.stringify(body) : undefined),
    signal,
    cache,
  });

  if (!response.ok) {
    throw new ApiError(response.status, await readProblemDetails(response), buildErrorMessage(response));
  }

  // 204 and 205 carry no body; parsing them as JSON would throw on an otherwise fine response.
  if (response.status === 204 || response.status === 205) {
    return undefined as TResponse;
  }

  return (await response.json()) as TResponse;
}

/**
 * Reads problem details without letting a parse failure mask the original error. An API that
 * returns 500 with an HTML error page should still surface as "500", not as a JSON syntax
 * error from the error handler.
 */
async function readProblemDetails(response: Response): Promise<ProblemDetails | undefined> {
  const contentType = response.headers.get("content-type") ?? "";

  if (!contentType.includes("json")) {
    return undefined;
  }

  try {
    return (await response.json()) as ProblemDetails;
  } catch {
    return undefined;
  }
}

function buildErrorMessage(response: Response): string {
  return `${response.status} ${response.statusText} from ${response.url}`;
}

/**
 * An API client bound to one way of obtaining a token. Created by
 * {@link createServerApiClient} or {@link createBrowserApiClient} rather than directly, so a
 * caller cannot accidentally construct one with no token resolution at all.
 */
export interface ApiClient {
  get<TResponse>(path: string, options?: Omit<RequestOptions, "method" | "body">): Promise<TResponse>;
  post<TResponse>(path: string, body?: unknown, options?: Omit<RequestOptions, "method" | "body">): Promise<TResponse>;
  patch<TResponse>(path: string, body?: unknown, options?: Omit<RequestOptions, "method" | "body">): Promise<TResponse>;
  put<TResponse>(path: string, body?: unknown, options?: Omit<RequestOptions, "method" | "body">): Promise<TResponse>;
  delete<TResponse>(path: string, options?: Omit<RequestOptions, "method" | "body">): Promise<TResponse>;

  /** Multipart upload, for photos (FR-002). */
  upload<TResponse>(path: string, formData: FormData, options?: Omit<RequestOptions, "method" | "body" | "formData">): Promise<TResponse>;
}

export function createApiClient(getToken: TokenResolver): ApiClient {
  return {
    get: (path, options) => request(path, getToken, { ...options, method: "GET" }),
    post: (path, body, options) => request(path, getToken, { ...options, method: "POST", body }),
    patch: (path, body, options) => request(path, getToken, { ...options, method: "PATCH", body }),
    put: (path, body, options) => request(path, getToken, { ...options, method: "PUT", body }),
    delete: (path, options) => request(path, getToken, { ...options, method: "DELETE" }),
    upload: (path, formData, options) => request(path, getToken, { ...options, method: "POST", formData }),
  };
}
