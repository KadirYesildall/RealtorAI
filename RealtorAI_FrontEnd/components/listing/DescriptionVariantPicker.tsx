"use client";

import { useRouter } from "next/navigation";
import { useCallback, useEffect, useId, useState, useTransition } from "react";
import { createContentAction, editContentAction, regenerateContentAction, type RegenerateResult } from "@/app/actions/content";
import { pipelineStatusAction } from "@/app/actions/photos";
import type { ContentDraft, ContentKind, ListingField, PipelineStatus, Tone } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { CONTENT_KINDS, ContentBodySchema, TONES } from "@/lib/validation/listing";
import { cn } from "@/lib/utils/cn";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { FormField } from "@/components/ui/FormField";
import { Select } from "@/components/ui/Select";
import { Textarea } from "@/components/ui/Textarea";
import { ClaimHighlighter } from "@/components/listing/ClaimHighlighter";

type ContentDict = Dictionary["listings"]["content"];

/** How often the content run's status is re-read while it is drafting. */
const POLL_MS = 2000;

function contentRunning(status: PipelineStatus | null): boolean {
  return (status?.steps ?? []).some(
    (step) => step.name === "content_generation" && (step.state === "queued" || step.state === "running")
  );
}

/**
 * T113 — the listing's content: a tone, the description's alternatives to choose from, the private
 * remarks and the headline, each editable inline (FR-013, FR-014; Story 3 scenarios 3–6).
 *
 * <b>Generated or written.</b> Each kind can be generated in the chosen tone, or written by hand —
 * the manual path SC-010 promises, which is the only path while no model is enabled. Generating
 * when it is unavailable says why and leaves writing open.
 *
 * <b>Choosing.</b> The description's alternatives are a radio group: choosing one records the
 * choice (scenario 4). The private remarks and the headline are one draft each.
 *
 * <b>Highlights.</b> Sentences resting on an unconfirmed fact are highlighted with the facts named
 * (scenario 6); confirming the fact in the listing's details clears them.
 */
export function DescriptionVariantPicker({
  listingId,
  drafts,
  fields,
  dict,
}: {
  listingId: string;
  drafts: ContentDraft[];
  fields: ListingField[];
  dict: Dictionary["listings"];
}) {
  const t = dict.content;
  const router = useRouter();
  const [, startTransition] = useTransition();
  const [tone, setTone] = useState<Tone>(() => drafts.find((draft) => draft.tone)?.tone ?? "professional");
  const [notice, setNotice] = useState<{ kind: ContentKind; text: string } | null>(null);
  const [generating, setGenerating] = useState<ContentKind | null>(null);

  // Drafts held locally, so an edit shows at once; new props (after a refresh) replace them.
  const [local, setLocal] = useState(drafts);
  const [source, setSource] = useState(drafts);

  if (source !== drafts) {
    setSource(drafts);
    setLocal(drafts);
  }

  const factLabels = new Map(
    fields.map((field) => [field.id ?? "", (t.facts as Record<string, string>)[field.resoFieldKey ?? ""] ?? field.resoFieldKey ?? ""])
  );
  const factLabel = (fieldId: string) => factLabels.get(fieldId) ?? t.unknownFact;

  const replace = (draft: ContentDraft) => setLocal((current) => current.map((candidate) => (candidate.id === draft.id ? draft : candidate)));

  // ── Drafting: re-read the run until it settles, then the page, for the new drafts ──
  const settle = useCallback(async () => {
    const status = await pipelineStatusAction(listingId);

    if (status && !contentRunning(status)) {
      const step = (status.steps ?? []).find((candidate) => candidate.name === "content_generation");

      if (generating && step && step.state !== "done") {
        setNotice({ kind: generating, text: t.generateFailed });
      }

      setGenerating(null);
      startTransition(() => router.refresh());
    }
  }, [generating, listingId, router, t.generateFailed]);

  useEffect(() => {
    if (!generating) return;

    const timer = setInterval(() => void settle(), POLL_MS);
    return () => clearInterval(timer);
  }, [generating, settle]);

  const generate = async (kind: ContentKind) => {
    setNotice(null);

    const result: RegenerateResult = await regenerateContentAction(listingId, kind, tone);

    if (result.status === "started") {
      setGenerating(kind);
    } else if (result.status === "manual") {
      setNotice({ kind, text: t.manual[result.reason] });
    } else {
      setNotice({ kind, text: (t.errors as Record<string, string>)[result.code] ?? t.errors.unexpected });
    }
  };

  return (
    <Card id="content">
      <section aria-labelledby="content-title" className="space-y-6">
        <div>
          <h2 id="content-title" className="text-lg font-semibold text-[var(--color-text)]">
            {t.title}
          </h2>
          <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.body}</p>
        </div>

        <FormField id="content-tone" label={t.tone} hint={t.toneHint}>
          {(describedBy) => (
            <Select
              id="content-tone"
              value={tone}
              aria-describedby={describedBy}
              onChange={(event) => setTone(event.currentTarget.value as Tone)}
              className="sm:max-w-xs"
            >
              {TONES.map((value) => (
                <option key={value} value={value}>
                  {t.tones[value]}
                </option>
              ))}
            </Select>
          )}
        </FormField>

        {CONTENT_KINDS.map((kind) => (
          <KindSection
            key={kind}
            kind={kind}
            listingId={listingId}
            tone={tone}
            drafts={local.filter((draft) => draft.kind === kind)}
            generating={generating === kind}
            busy={generating !== null}
            notice={notice?.kind === kind ? notice.text : null}
            factLabel={factLabel}
            dict={t}
            onGenerate={() => void generate(kind)}
            onSaved={(draft, created) => {
              if (created) {
                setLocal((current) => [...current, draft]);
              } else {
                replace(draft);
              }

              startTransition(() => router.refresh());
            }}
            onSelected={(draft) =>
              setLocal((current) =>
                current.map((candidate) =>
                  candidate.id === draft.id ? draft : candidate.kind === draft.kind ? { ...candidate, selected: false } : candidate
                )
              )
            }
          />
        ))}
      </section>
    </Card>
  );
}

function KindSection({
  kind,
  listingId,
  tone,
  drafts,
  generating,
  busy,
  notice,
  factLabel,
  dict,
  onGenerate,
  onSaved,
  onSelected,
}: {
  kind: ContentKind;
  listingId: string;
  tone: Tone;
  drafts: ContentDraft[];
  generating: boolean;
  busy: boolean;
  notice: string | null;
  factLabel: (fieldId: string) => string;
  dict: ContentDict;
  onGenerate: () => void;
  onSaved: (draft: ContentDraft, created: boolean) => void;
  onSelected: (draft: ContentDraft) => void;
}) {
  const headingId = useId();
  const [writing, setWriting] = useState(false);
  const [selectError, setSelectError] = useState<string | null>(null);
  const variants = kind === "public_remarks" && drafts.length > 1;

  const select = async (draft: ContentDraft) => {
    setSelectError(null);
    onSelected({ ...draft, selected: true });

    const result = await editContentAction(listingId, draft.id ?? "", draft.rowVersion ?? 1, { selected: true });

    if (result.status === "saved") {
      onSaved(result.draft, false);
    } else {
      setSelectError(result.status === "conflict" ? dict.conflict : ((dict.errors as Record<string, string>)[result.code] ?? dict.errors.unexpected));
      onSaved(result.status === "conflict" && result.current ? result.current : draft, false);
    }
  };

  return (
    <section aria-labelledby={headingId} className="space-y-3 border-t border-[var(--color-border)] pt-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h3 id={headingId} className="font-medium text-[var(--color-text)]">
          {dict.kinds[kind]}
        </h3>
        <div className="flex flex-wrap gap-2">
          <Button type="button" size="sm" variant="secondary" loading={generating} disabled={busy} onClick={onGenerate}>
            {drafts.length > 0 ? format(dict.regenerate, { kind: dict.kinds[kind] }) : format(dict.generate, { kind: dict.kinds[kind] })}
          </Button>
          {drafts.length === 0 && !writing && (
            <Button type="button" size="sm" variant="ghost" onClick={() => setWriting(true)}>
              {format(dict.write, { kind: dict.kinds[kind] })}
            </Button>
          )}
        </div>
      </div>

      {generating && <p role="status" className="text-sm text-[var(--color-text-muted)]">{dict.generating}</p>}
      {notice && <Alert role="status">{notice}</Alert>}
      {selectError && <Alert>{selectError}</Alert>}

      {drafts.length === 0 && !writing && <p className="text-sm text-[var(--color-text-muted)]">{dict.empty}</p>}

      {drafts.length === 0 && writing && (
        <DraftEditor
          id={`${kind}-new`}
          label={format(dict.bodyLabel, { kind: dict.kinds[kind] })}
          initial=""
          limit={null}
          dict={dict}
          onCancel={() => setWriting(false)}
          onSave={async (body) => {
            const result = await createContentAction(listingId, kind, body, tone);

            if (result.status === "saved") {
              setWriting(false);
              onSaved(result.draft, true);
              return null;
            }

            return result.status === "conflict" ? dict.conflict : ((dict.errors as Record<string, string>)[result.code] ?? dict.errors.unexpected);
          }}
        />
      )}

      {variants ? (
        <fieldset className="space-y-3">
          <legend className="text-sm text-[var(--color-text-muted)]">{dict.chooseVersion}</legend>
          {drafts.map((draft, index) => (
            <DraftCard
              key={draft.id}
              draft={draft}
              listingId={listingId}
              title={format(dict.version, { n: String(index + 1) })}
              radioName={`${kind}-choice`}
              factLabel={factLabel}
              dict={dict}
              onSelect={() => void select(draft)}
              onSaved={(saved) => onSaved(saved, false)}
            />
          ))}
        </fieldset>
      ) : (
        drafts.map((draft) => (
          <DraftCard
            key={draft.id}
            draft={draft}
            listingId={listingId}
            title={null}
            radioName={null}
            factLabel={factLabel}
            dict={dict}
            onSelect={() => void select(draft)}
            onSaved={(saved) => onSaved(saved, false)}
          />
        ))
      )}
    </section>
  );
}

function DraftCard({
  draft,
  listingId,
  title,
  radioName,
  factLabel,
  dict,
  onSelect,
  onSaved,
}: {
  draft: ContentDraft;
  listingId: string;
  title: string | null;
  radioName: string | null;
  factLabel: (fieldId: string) => string;
  dict: ContentDict;
  onSelect: () => void;
  onSaved: (draft: ContentDraft) => void;
}) {
  const radioId = useId();
  const [editing, setEditing] = useState(false);
  const [conflict, setConflict] = useState<ContentDraft | null>(null);
  const claims = draft.unconfirmedClaims ?? [];
  const body = draft.body ?? "";

  return (
    <div
      className={cn(
        "space-y-3 rounded-[var(--radius-md)] border p-4",
        draft.selected ? "border-[var(--color-accent)] bg-[var(--color-surface-elevated)]" : "border-[var(--color-border)]"
      )}
    >
      {radioName && title && (
        <div className="flex items-center gap-2">
          <input
            id={radioId}
            type="radio"
            name={radioName}
            checked={draft.selected ?? false}
            onChange={onSelect}
            className="size-4 accent-[var(--color-accent)]"
          />
          <label htmlFor={radioId} className="text-sm font-medium text-[var(--color-text)]">
            {title}
          </label>
        </div>
      )}

      {conflict && (
        <Alert>
          <p>{dict.conflict}</p>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            className="mt-3"
            onClick={() => {
              onSaved(conflict);
              setConflict(null);
              setEditing(false);
            }}
          >
            {dict.loadLatest}
          </Button>
        </Alert>
      )}

      {editing ? (
        <DraftEditor
          id={`draft-${draft.id}`}
          label={title ? format(dict.editVersion, { version: title }) : format(dict.bodyLabel, { kind: dict.kinds[draft.kind ?? "public_remarks"] })}
          initial={body}
          limit={draft.characterLimit ?? null}
          dict={dict}
          onCancel={() => setEditing(false)}
          onSave={async (next) => {
            const result = await editContentAction(listingId, draft.id ?? "", draft.rowVersion ?? 1, { body: next });

            if (result.status === "saved") {
              setEditing(false);
              onSaved(result.draft);
              return null;
            }

            if (result.status === "conflict") {
              setConflict(result.current);
              return null;
            }

            return (dict.errors as Record<string, string>)[result.code] ?? dict.errors.unexpected;
          }}
        />
      ) : (
        <>
          <ClaimHighlighter body={body} claims={claims} factLabel={factLabel} note={dict.claimNote} />
          <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-[var(--color-text-muted)]">
            <span>
              {draft.characterLimit
                ? format(dict.countOfLimit, { count: String(draft.characterCount ?? body.length), limit: String(draft.characterLimit) })
                : format(dict.count, { count: String(draft.characterCount ?? body.length) })}
            </span>
            <Button type="button" size="sm" variant="ghost" onClick={() => setEditing(true)}>
              {title ? format(dict.editVersion, { version: title }) : format(dict.edit, { kind: dict.kinds[draft.kind ?? "public_remarks"] })}
            </Button>
          </div>
          {claims.length > 0 && (
            <p className="text-sm text-[var(--color-text)]">{format(dict.claimsSummary, { count: String(new Set(claims.map((claim) => claim.spanStart)).size) })}</p>
          )}
          {draft.status === "approved" && <p className="text-xs text-[var(--color-text-muted)]">{dict.approvedNote}</p>}
        </>
      )}
    </div>
  );
}

/** An inline editor with a live count against the MLS's limit, which it will not save past. */
function DraftEditor({
  id,
  label,
  initial,
  limit,
  dict,
  onCancel,
  onSave,
}: {
  id: string;
  label: string;
  initial: string;
  limit: number | null;
  dict: ContentDict;
  onCancel: () => void;
  /** Resolves to an error to show, or null when saved. */
  onSave: (body: string) => Promise<string | null>;
}) {
  const [value, setValue] = useState(initial);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const over = limit !== null && value.length > limit;
  const empty = !ContentBodySchema.safeParse(value).success;

  return (
    <form
      className="space-y-3"
      onSubmit={async (event) => {
        event.preventDefault();
        if (over || empty) return;

        setSaving(true);
        setError(await onSave(value));
        setSaving(false);
      }}
    >
      <FormField
        id={id}
        label={label}
        hint={limit !== null ? format(dict.countOfLimit, { count: String(value.length), limit: String(limit) }) : format(dict.count, { count: String(value.length) })}
        error={over ? format(dict.errors.overLimit, { limit: String(limit) }) : (error ?? undefined)}
      >
        {(describedBy) => (
          <Textarea
            id={id}
            value={value}
            rows={6}
            invalid={over || error !== null}
            aria-describedby={describedBy}
            onChange={(event) => setValue(event.currentTarget.value)}
          />
        )}
      </FormField>
      <div className="flex flex-wrap gap-2">
        <Button type="submit" size="sm" loading={saving} disabled={over || empty}>
          {dict.save}
        </Button>
        <Button type="button" size="sm" variant="ghost" disabled={saving} onClick={onCancel}>
          {dict.cancel}
        </Button>
      </div>
    </form>
  );
}
