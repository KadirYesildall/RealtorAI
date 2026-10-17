import type { ContentDraft } from "@/lib/api/listings";
import { format } from "@/lib/i18n/format";

type Claim = NonNullable<ContentDraft["unconfirmedClaims"]>[number];

/** A run of the body: plain, a claim with the facts it rests on, or the span a finding points at. */
type Segment = { text: string; facts: string[] | null; focused?: true };

/** A span to mark because a compliance finding points at it (T136). */
export type Focus = { start: number; end: number };

/**
 * Splits `body` at the claims' spans. Claims on the same span are one segment naming every fact;
 * a span overlapping one already taken, or falling outside the body, is skipped rather than
 * highlighting the wrong words.
 */
export function segment(body: string, claims: Claim[], factLabel: (fieldId: string) => string, focus?: Focus | null): Segment[] {
  const spans = new Map<string, { start: number; end: number; facts: string[]; focused?: true }>();
  const focused = focus && focus.start >= 0 && focus.end <= body.length && focus.end > focus.start ? focus : null;

  // The finding's span wins over a claim it overlaps: it is what the agent asked to see.
  if (focused) spans.set("focus", { start: focused.start, end: focused.end, facts: [], focused: true });

  for (const claim of claims) {
    const start = claim.spanStart ?? -1;
    const end = claim.spanEnd ?? -1;

    if (start < 0 || end > body.length || end <= start) continue;
    if (focused && start < focused.end && focused.start < end) continue;

    const key = `${start}:${end}`;
    const label = factLabel(claim.fieldId ?? "");
    const span = spans.get(key) ?? { start, end, facts: [] };

    if (!span.facts.includes(label)) span.facts.push(label);
    spans.set(key, span);
  }

  const segments: Segment[] = [];
  let cursor = 0;

  for (const span of [...spans.values()].sort((a, b) => a.start - b.start)) {
    if (span.start < cursor) continue;
    if (span.start > cursor) segments.push({ text: body.slice(cursor, span.start), facts: null });
    segments.push(
      span.focused ? { text: body.slice(span.start, span.end), facts: null, focused: true } : { text: body.slice(span.start, span.end), facts: span.facts }
    );
    cursor = span.end;
  }

  if (cursor < body.length) segments.push({ text: body.slice(cursor), facts: null });

  return segments;
}

/**
 * T114 — highlights the sentences resting on a fact nobody has confirmed (FR-014; Story 3
 * scenario 6).
 *
 * The highlight is not colour alone: the sentence is marked, underlined, and followed by a note
 * naming the facts it rests on — read by screen readers and shown on hover. The spans come from
 * the API, computed against the facts as they are now, so confirming a fact clears the highlight
 * on the next read.
 */
export function ClaimHighlighter({
  body,
  claims,
  factLabel,
  note,
  focus,
  focusNote,
}: {
  body: string;
  claims: Claim[];
  factLabel: (fieldId: string) => string;
  /** `listings.content.claimNote`, with `{facts}`. */
  note: string;
  focus?: Focus | null;
  /** `listings.content.findingNote`, read after the marked span. */
  focusNote?: string;
}) {
  return (
    <p className="whitespace-pre-wrap text-sm leading-relaxed text-[var(--color-text)]">
      {segment(body, claims, factLabel, focus).map((part, index) =>
        part.focused ? (
          <span key={index}>
            <mark
              data-finding-span=""
              title={focusNote}
              className="rounded-sm bg-[var(--color-danger-soft)] px-0.5 text-[var(--color-text)] outline outline-2 outline-offset-1 outline-[var(--color-danger)]"
            >
              {part.text}
            </mark>
            {focusNote && <span className="sr-only"> ({focusNote})</span>}
          </span>
        ) : part.facts === null ? (
          <span key={index}>{part.text}</span>
        ) : (
          <span key={index}>
            <mark
              title={format(note, { facts: part.facts.join(", ") })}
              className="rounded-sm bg-[var(--color-accent-soft)] px-0.5 text-[var(--color-text)] underline decoration-[var(--color-accent)] decoration-dotted decoration-2 underline-offset-4"
            >
              {part.text}
            </mark>
            <span className="sr-only"> ({format(note, { facts: part.facts.join(", ") })})</span>
          </span>
        )
      )}
    </p>
  );
}
