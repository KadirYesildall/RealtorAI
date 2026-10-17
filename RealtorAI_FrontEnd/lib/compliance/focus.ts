/**
 * T136 — "show in text": the compliance panel asks the content card to mark a finding's span.
 *
 * The two are siblings on the page with no shared state worth lifting for this one gesture, so
 * the request travels as a DOM event. The content card that holds the draft marks the span and
 * scrolls it into view; every other card ignores it.
 */

export const FOCUS_SPAN_EVENT = "compliance:focus-span";

export type FocusSpan = { draftId: string; start: number; end: number };

export function focusSpan(detail: FocusSpan) {
  window.dispatchEvent(new CustomEvent<FocusSpan>(FOCUS_SPAN_EVENT, { detail }));
}
