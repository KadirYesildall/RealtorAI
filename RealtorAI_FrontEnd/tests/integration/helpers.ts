const MAILPIT_URL = "http://127.0.0.1:54324";

export function uniqueEmail(label: string) {
  return `pw-${label}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`;
}

type MailpitMessage = { ID: string };

/**
 * Polls the local email testing inbox (Mailpit's REST API, confirmed live at :54324 —
 * see research.md §3) for the confirmation email sent to `email`, and returns the
 * confirmation link it contains.
 */
export async function getConfirmationLink(email: string, timeoutMs = 10_000): Promise<string> {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    const searchRes = await fetch(
      `${MAILPIT_URL}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`
    );
    const searchData = (await searchRes.json()) as { messages: MailpitMessage[] };

    if (searchData.messages.length > 0) {
      const messageRes = await fetch(`${MAILPIT_URL}/api/v1/message/${searchData.messages[0].ID}`);
      const message = (await messageRes.json()) as { Text: string };
      const match = message.Text.match(/\(\s*(https?:\/\/[^\s)]+)\s*\)/);
      if (match) return match[1];
    }

    await new Promise((resolve) => setTimeout(resolve, 300));
  }

  throw new Error(`No confirmation email arrived for ${email} within ${timeoutMs}ms`);
}
