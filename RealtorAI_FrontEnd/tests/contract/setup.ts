// Loads RealtorAI_FrontEnd/.env.local so contract tests can reach the local Supabase stack
// (the same instance `npm run dev` uses) without a separate test database (plan.md, Principle V).
process.loadEnvFile(new URL("../../.env.local", import.meta.url));
