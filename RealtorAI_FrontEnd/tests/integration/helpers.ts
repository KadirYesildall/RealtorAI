import type { Page } from "@playwright/test";
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

/** Text elements whose contrast against their effective background is below WCAG AA. */
export async function contrastFailures(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const parse = (value: string): [number, number, number, number] | null => {
      const match = value.match(/rgba?\(([^)]+)\)/);
      if (!match) return null;
      const parts = match[1].split(/[\s,/]+/).filter(Boolean).map(Number);
      return [parts[0], parts[1], parts[2], parts[3] ?? 1];
    };

    const luminance = ([r, g, b]: number[]) => {
      const channel = (c: number) => {
        const s = c / 255;
        return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
      };
      return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
    };

    const background = (element: Element | null): [number, number, number] => {
      const layers: [number, number, number, number][] = [];
      for (let node = element; node; node = node.parentElement) {
        const color = parse(getComputedStyle(node).backgroundColor);
        if (color && color[3] > 0) {
          layers.push(color);
          if (color[3] >= 1) break;
        }
      }
      let result: [number, number, number] = [255, 255, 255];
      for (const [r, g, b, a] of layers.reverse()) {
        result = [r * a + result[0] * (1 - a), g * a + result[1] * (1 - a), b * a + result[2] * (1 - a)];
      }
      return result;
    };

    const failures: string[] = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);

    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim();
      const element = node.parentElement;
      if (!text || !element) continue;

      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      if (style.visibility === "hidden" || style.display === "none" || rect.width === 0 || rect.height === 0) continue;
      if (element.closest("[aria-hidden='true'], .sr-only, button:disabled, [aria-disabled='true']")) continue;

      const foreground = parse(style.color);
      if (!foreground) continue;

      const back = background(element);
      const front: [number, number, number] = [
        foreground[0] * foreground[3] + back[0] * (1 - foreground[3]),
        foreground[1] * foreground[3] + back[1] * (1 - foreground[3]),
        foreground[2] * foreground[3] + back[2] * (1 - foreground[3]),
      ];

      const [light, dark] = [luminance(front), luminance(back)].sort((a, b) => b - a);
      const ratio = (light + 0.05) / (dark + 0.05);
      const size = parseFloat(style.fontSize);
      const large = size >= 24 || (size >= 18.66 && Number(style.fontWeight) >= 700);

      if (ratio < (large ? 3 : 4.5)) {
        failures.push(`${ratio.toFixed(2)}:1 "${text.slice(0, 40)}"`);
      }
    }

    return failures;
  });
}
