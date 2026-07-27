import { redirect } from "next/navigation";

/**
 * No standalone marketing page exists yet for this feature — root simply routes visitors
 * into the auth flow. `/dashboard`'s own guard sends unauthenticated users to `/login`
 * anyway, so this stays a single redirect rather than duplicating that check.
 */
export default function Home() {
  redirect("/login");
}
