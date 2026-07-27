import { NextResponse } from "next/server";
import { createClient } from "@/lib/supabase/server";

/**
 * External OAuth redirect target (Google). Reserved for webhooks/external API access
 * points per the constitution's Route Handler rule — see contracts/oauth-callback.md.
 */
export async function GET(request: Request) {
  const { searchParams, origin } = new URL(request.url);
  const code = searchParams.get("code");
  const oauthError = searchParams.get("error");

  if (oauthError) {
    return NextResponse.redirect(`${origin}/login?error=oauth_cancelled`);
  }

  if (code) {
    const supabase = await createClient();
    const { error } = await supabase.auth.exchangeCodeForSession(code);

    if (!error) {
      return NextResponse.redirect(`${origin}/dashboard`);
    }
    return NextResponse.redirect(`${origin}/login?error=oauth_failed`);
  }

  // Neither `code` nor `error` present — malformed request, not a failed exchange.
  return NextResponse.redirect(`${origin}/login`);
}
