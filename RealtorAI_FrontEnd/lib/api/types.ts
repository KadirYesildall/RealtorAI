/**
 * T012 — shared transport-level types for the .NET API client.
 *
 * Endpoint request/response shapes are NOT declared here. They are generated from
 * `specs/002-ai-listing-launch/contracts/api.openapi.yaml`, which is the single source of
 * truth for the contract; hand-maintaining a second copy guarantees the two drift apart and
 * the compiler would keep agreeing with whichever one was wrong.
 */

/**
 * RFC 9457 problem details, as ASP.NET Core emits them.
 *
 * `type` is the machine-readable discriminator. Callers branch on it rather than on `title`
 * or `detail`, which are human-facing and may be localised or reworded.
 */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;

  /** Per-field validation messages, keyed by property name. */
  errors?: Record<string, string[]>;

  /**
   * Present on a 409 version conflict (FR-066): the row version the server actually holds, so
   * the UI can refetch and show what changed instead of guessing.
   */
  currentRowVersion?: number;

  /** ASP.NET Core allows arbitrary extension members. */
  [key: string]: unknown;
}
