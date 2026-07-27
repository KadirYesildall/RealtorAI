import { createServerClient } from "@supabase/ssr";
import { NextResponse, type NextRequest } from "next/server";
import { defaultLocale, isLocale, type Locale } from "@/lib/i18n/getDictionary";

const LOCALE_COOKIE = "NEXT_LOCALE";

function detectLocaleFromAcceptLanguage(header: string | null): Locale {
  if (!header) return defaultLocale;
  const preferred = header.split(",")[0]?.trim().toLowerCase().slice(0, 2);
  return isLocale(preferred) ? preferred : defaultLocale;
}

export default async function proxy(request: NextRequest) {
  let response = NextResponse.next({ request });

  const supabase = createServerClient(
    process.env.NEXT_PUBLIC_SUPABASE_URL!,
    process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY!,
    {
      cookies: {
        getAll() {
          return request.cookies.getAll();
        },
        setAll(cookiesToSet) {
          cookiesToSet.forEach(({ name, value }) => request.cookies.set(name, value));
          response = NextResponse.next({ request });
          cookiesToSet.forEach(({ name, value, options }) => {
            response.cookies.set(name, value, options);
          });
        },
      },
    }
  );

  // Refresh the session (if needed) before any Server Component/Action reads it.
  await supabase.auth.getUser();

  // First-visit locale detection (FR-013): only set the cookie if it isn't there yet —
  // a saved profile preference (set via updateLanguagePreferenceAction) always wins after that.
  if (!request.cookies.has(LOCALE_COOKIE)) {
    const locale = detectLocaleFromAcceptLanguage(request.headers.get("accept-language"));
    response.cookies.set(LOCALE_COOKIE, locale, { path: "/", sameSite: "lax" });
  }

  return response;
}

export const config = {
  matcher: [
    /*
     * Match all paths except static assets and image optimization files, so auth
     * cookie refresh and locale detection run on every real page/action request.
     */
    "/((?!_next/static|_next/image|favicon.ico).*)",
  ],
};
