import { deflateSync } from "node:zlib";

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


/** A solid-colour PNG, encoded here so every run uploads bytes no earlier run has. */
export function png(width: number, height: number, rgb: [number, number, number]): Buffer {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc32 = (bytes: Buffer) => {
    let c = 0xffffffff;
    for (const byte of bytes) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type: string, data: Buffer) => {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([length, body, crc]);
  };

  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8; // bit depth
  header[9] = 2; // truecolour RGB

  const row = Buffer.concat([Buffer.from([0]), Buffer.from(Array.from({ length: width }, () => rgb).flat())]);
  const pixels = Buffer.concat(Array.from({ length: height }, () => row));

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", header),
    chunk("IDAT", deflateSync(pixels)),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

export function randomColour(): [number, number, number] {
  return [0, 0, 0].map(() => Math.floor(Math.random() * 256)) as [number, number, number];
}
