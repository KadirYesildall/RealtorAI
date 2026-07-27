// Vitest isn't Next's build pipeline, so the real "server-only" package (which throws
// unconditionally to catch client-bundle leaks) would break every import chain that touches
// it. Aliased in vitest.config.ts — contract tests are themselves a server-side context, so
// a no-op stub is correct here, not a workaround for a real client/server boundary violation.
export {};
