"use client";

import { useRouter } from "next/navigation";
import { useId, useRef, useState, useTransition } from "react";
import {
  DndContext,
  KeyboardSensor,
  PointerSensor,
  closestCenter,
  useSensor,
  useSensors,
  type DragEndEvent,
} from "@dnd-kit/core";
import {
  SortableContext,
  arrayMove,
  rectSortingStrategy,
  sortableKeyboardCoordinates,
  useSortable,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { deletePhotoAction, patchPhotoAction, reorderPhotosAction } from "@/app/actions/photos";
import type { PatchPhotoRequest, Photo } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { MAX_CAPTION_LENGTH, ROOM_TYPES, parseTags } from "@/lib/validation/listing";
import { cn } from "@/lib/utils/cn";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Select } from "@/components/ui/Select";
import { TextField } from "@/components/ui/TextField";
import { AlteredPhotoDialog } from "@/components/listing/AlteredPhotoDialog";
import type { SaveActivity } from "@/components/listing/ListingDraftEditor";

type Dict = Dictionary["listings"];

/**
 * T099 — the photo grid: room labels, quality and content flags, drag to reorder, cover
 * selection, and every override Story 2 scenario 7 asks for (FR-010, FR-011).
 *
 * Everything the analysis concluded is shown as a suggestion with its source; the agent's choice
 * replaces it and says so, and "Use the AI's suggestion" returns to it. Reordering works with the
 * pointer and with the keyboard (dnd-kit's keyboard sensor: Space to lift, arrows to move, Space to
 * drop), and lands as one request — the API applies the whole order in one transaction.
 */
export function PhotoGrid({
  listingId,
  photos,
  approvedLabels,
  dict,
  onActivity,
}: {
  listingId: string;
  photos: Photo[];
  approvedLabels: string[];
  dict: Dict;
  onActivity: (activity: SaveActivity) => void;
}) {
  const router = useRouter();
  // dnd-kit numbers its accessibility ids with a module counter, which differs between the server
  // render and the client's; a React id is the same on both, so the description each handle
  // points at survives hydration.
  const dndId = useId();
  const [, startTransition] = useTransition();
  const [items, setItems] = useState<Photo[]>(photos);
  const [error, setError] = useState<string | null>(null);
  const [declaring, setDeclaring] = useState<Photo | null>(null);
  const errors = dict.analysis.errors as Record<string, string>;

  // The server's view after an upload, removal or analysis replaces the local one — adjusted
  // during render, as React recommends for state derived from a changed prop.
  // Not while a photo is being dragged: replacing the items under an active drag would drop it.
  // The update is applied when the drag ends, on the next render.
  const [dragging, setDragging] = useState(false);
  const [seenPhotos, setSeenPhotos] = useState(photos);
  if (photos !== seenPhotos && !dragging) {
    setSeenPhotos(photos);
    setItems(photos);
  }

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 6 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates })
  );

  // Saves in flight. "Saved" is shown only when none remain: a cover change finishing while a
  // reorder is still queued behind it must not tell the agent everything is saved — leaving then
  // would drop the reorder.
  const inFlight = useRef(0);

  const run = async (work: () => Promise<{ status: string; code?: string }>) => {
    setError(null);
    inFlight.current += 1;
    onActivity("saving");

    const result = await work();
    inFlight.current -= 1;

    if (result.status === "saved") {
      if (inFlight.current === 0) {
        onActivity("saved");

        // Re-read the listing once everything is saved. A refresh started earlier — after an
        // upload — can land after a local change and carry the order from before it; the newest
        // refresh supersedes it, so the grid settles on what is actually stored.
        startTransition(() => router.refresh());
      }

      return true;
    }

    setError(errors[result.code ?? "unexpected"] ?? errors.unexpected);
    onActivity("failed");
    return false;
  };

  const patch = async (photo: Photo, change: PatchPhotoRequest) => {
    await run(async () => {
      const result = await patchPhotoAction(listingId, photo.id ?? "", change);

      if (result.status === "saved") {
        setItems((current) => current.map((item) => (item.id === photo.id ? result.photo : item)));
      }

      return result;
    });
  };

  const saveOrder = async (ordered: Photo[], coverId?: string) => {
    const previous = items;
    setItems(
      ordered.map((item) => ({ ...item, isCover: coverId ? item.id === coverId : item.isCover }))
    );

    const saved = await run(() =>
      reorderPhotosAction(
        listingId,
        ordered.map((item) => item.id ?? ""),
        coverId
      )
    );

    if (!saved) {
      setItems(previous);
    }
  };

  const onDragEnd = ({ active, over }: DragEndEvent) => {
    setDragging(false);
    if (!over || active.id === over.id) return;

    const from = items.findIndex((item) => item.id === active.id);
    const to = items.findIndex((item) => item.id === over.id);
    void saveOrder(arrayMove(items, from, to));
  };

  const remove = async (photo: Photo, position: number) => {
    if (!window.confirm(format(dict.grid.removeConfirm, { n: String(position) }))) return;

    if (await run(() => deletePhotoAction(listingId, photo.id ?? ""))) {
      router.refresh();
    }
  };

  const suggested = items.some((item) => item.analysis?.orderRecommendation !== undefined && item.analysis?.orderRecommendation !== null);

  const applySuggestions = () => {
    const rank = (item: Photo) => item.analysis?.orderRecommendation ?? Number.MAX_SAFE_INTEGER;
    const ordered = [...items].sort((a, b) => rank(a) - rank(b));
    const cover = items.find((item) => item.analysis?.coverRecommended)?.id;
    void saveOrder(ordered, cover ?? undefined);
  };

  if (items.length === 0) {
    return null;
  }

  return (
    <div className="space-y-4">
      {error && <Alert>{error}</Alert>}

      {suggested && (
        <Button type="button" variant="secondary" size="sm" onClick={applySuggestions}>
          {dict.grid.applySuggestions}
        </Button>
      )}

      <DndContext
        id={dndId}
        sensors={sensors}
        collisionDetection={closestCenter}
        onDragStart={() => setDragging(true)}
        onDragCancel={() => setDragging(false)}
        onDragEnd={onDragEnd}
      >
        <SortableContext items={items.map((item) => item.id ?? "")} strategy={rectSortingStrategy}>
          <ul aria-label={dict.grid.label} className="grid gap-4 sm:grid-cols-2">
            {items.map((photo, index) => (
              <PhotoCard
                key={photo.id}
                photo={photo}
                position={index + 1}
                dict={dict}
                onPatch={(change) => patch(photo, change)}
                onCover={() => saveOrder(items, photo.id)}
                onRemove={() => remove(photo, index + 1)}
                onDeclare={() => setDeclaring(photo)}
              />
            ))}
          </ul>
        </SortableContext>
      </DndContext>

      {declaring && (
        <AlteredPhotoDialog
          photo={declaring}
          candidates={items.filter((item) => item.id !== declaring.id && !item.altered)}
          approvedLabels={approvedLabels}
          dict={dict}
          onClose={() => setDeclaring(null)}
          onSubmit={async (change) => {
            await patch(declaring, change);
            setDeclaring(null);
          }}
        />
      )}
    </div>
  );
}

function PhotoCard({
  photo,
  position,
  dict,
  onPatch,
  onCover,
  onRemove,
  onDeclare,
}: {
  photo: Photo;
  position: number;
  dict: Dict;
  onPatch: (change: PatchPhotoRequest) => Promise<void>;
  onCover: () => void;
  onRemove: () => void;
  onDeclare: () => void;
}) {
  const t = dict.grid;
  const n = String(position);
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, transform, transition, isDragging } = useSortable({
    id: photo.id ?? "",
  });

  const savedCaption = photo.caption ?? "";
  const savedFeatures = (photo.featureTags ?? []).join(", ");
  const [caption, setCaption] = useState(savedCaption);
  const [features, setFeatures] = useState(savedFeatures);
  const [seen, setSeen] = useState({ caption: savedCaption, features: savedFeatures });

  // A saved value replaces the field's text when it changes underneath — an analysis landing,
  // or "use the AI's suggestion".
  if (seen.caption !== savedCaption || seen.features !== savedFeatures) {
    setSeen({ caption: savedCaption, features: savedFeatures });
    setCaption(savedCaption);
    setFeatures(savedFeatures);
  }

  const analysis = photo.analysis && !photo.analysis.failed ? photo.analysis : null;
  const flags = [
    ...(analysis?.qualityFlags ?? []).map((flag) => t.quality[flag]),
    ...(analysis?.contentFlags ?? []).map((flag) => t.content[flag]),
  ];
  const reason = analysis?.recommendationReason;
  const reasonText = reason
    ? reason.startsWith("rule:order:")
      ? format(t.reasons.order, { room: t.rooms[reason.slice("rule:order:".length) as keyof typeof t.rooms] ?? "" })
      : ((t.reasons as Record<string, string>)[reason] ?? reason)
    : null;

  return (
    <li
      ref={setNodeRef}
      style={{ transform: CSS.Transform.toString(transform), transition }}
      className={cn(
        "space-y-3 rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-surface)] p-3",
        isDragging && "shadow-[var(--shadow-card)]"
      )}
    >
      <div className="relative aspect-[4/3] overflow-hidden rounded-[var(--radius-md)] bg-[var(--color-surface-muted)]">
        {photo.normalizedUrl && (
          // A short-lived signed URL to the normalized copy (VIII.4.c); next/image would cache it
          // past its expiry.
          // eslint-disable-next-line @next/next/no-img-element
          <img
            src={photo.normalizedUrl}
            alt={photo.caption ?? format(dict.photos.photoAlt, { n })}
            className="h-full w-full object-cover"
            loading="lazy"
          />
        )}
        {photo.isCover && (
          <span className="absolute left-2 top-2 rounded-full bg-[var(--color-accent)] px-2 py-0.5 text-xs font-medium text-[var(--color-accent-foreground)]">
            {t.cover}
          </span>
        )}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          ref={setActivatorNodeRef}
          {...attributes}
          {...listeners}
          aria-label={format(t.move, { n })}
          className="cursor-grab rounded-[var(--radius-md)] border border-[var(--color-border)] px-2 py-1 text-sm text-[var(--color-text)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-accent)]"
        >
          <span aria-hidden="true">⠿</span> {n}
        </button>
        <Button
          type="button"
          size="sm"
          variant={photo.isCover ? "primary" : "secondary"}
          aria-pressed={photo.isCover ?? false}
          aria-label={format(t.makeCover, { n })}
          onClick={onCover}
        >
          {t.cover}
        </Button>
        <Button type="button" size="sm" variant="ghost" aria-label={format(t.declare, { n })} onClick={onDeclare}>
          {dict.altered.title}
        </Button>
        <Button type="button" size="sm" variant="ghost" aria-label={format(t.remove, { n })} onClick={onRemove}>
          ✕
        </Button>
      </div>

      {photo.altered && photo.disclosureLabel && (
        <p className="text-sm font-medium text-[var(--color-text)]">{format(t.altered, { label: photo.disclosureLabel })}</p>
      )}

      <div className="space-y-1.5">
        <label htmlFor={`photo-${photo.id}-room`} className="block text-sm font-medium text-[var(--color-text)]">
          {format(t.roomType, { n })}
        </label>
        <Select
          id={`photo-${photo.id}-room`}
          value={photo.roomType ?? ""}
          onChange={(event) =>
            void onPatch({ roomType: event.currentTarget.value === "" ? null : (event.currentTarget.value as PatchPhotoRequest["roomType"]) })
          }
        >
          <option value="">{t.roomTypeNone}</option>
          {ROOM_TYPES.map((room) => (
            <option key={room} value={room}>
              {t.rooms[room]}
            </option>
          ))}
        </Select>
        {photo.roomTypeOverridden ? (
          <p className="flex flex-wrap items-center gap-2 text-sm text-[var(--color-text-muted)]">
            {t.fromAgent}
            {analysis?.roomType && (
              <button type="button" className="font-medium text-[var(--color-accent)] underline" onClick={() => void onPatch({ roomType: null })}>
                {t.useAi}
              </button>
            )}
          </p>
        ) : (
          analysis?.roomType && (
            <p className="text-sm text-[var(--color-text-muted)]">
              {format(t.fromAi, { confidence: String(Math.round((analysis.roomTypeConfidence ?? 0) * 100)) })}
            </p>
          )
        )}
      </div>

      <TextField
        id={`photo-${photo.id}-features`}
        label={format(t.features, { n })}
        hint={t.featuresHint}
        value={features}
        onChange={(event) => setFeatures(event.currentTarget.value)}
        onBlur={() => {
          const tags = parseTags(features);
          if (tags.join(",") !== (photo.featureTags ?? []).join(",")) void onPatch({ featureTags: tags });
        }}
      />

      <TextField
        id={`photo-${photo.id}-caption`}
        label={format(t.caption, { n })}
        value={caption}
        maxLength={MAX_CAPTION_LENGTH}
        onChange={(event) => setCaption(event.currentTarget.value)}
        onBlur={() => {
          if (caption.trim() !== (photo.caption ?? "")) void onPatch({ caption: caption.trim() === "" ? null : caption.trim() });
        }}
      />

      {flags.length > 0 && (
        <ul aria-label={format(t.flagsLabel, { n })} className="flex flex-wrap gap-1.5">
          {flags.map((flag) => (
            <li
              key={flag}
              className="rounded-full border border-[color-mix(in_srgb,var(--color-danger)_35%,transparent)] bg-[var(--color-danger-soft)] px-2 py-0.5 text-xs font-medium text-[var(--color-danger)]"
            >
              {flag}
            </li>
          ))}
        </ul>
      )}

      {analysis?.orderRecommendation !== undefined && analysis?.orderRecommendation !== null && (
        <p className="text-sm text-[var(--color-text-muted)]">
          {format(t.suggested, { position: String(analysis.orderRecommendation + 1) })}
          {reasonText ? ` — ${reasonText}` : ""}
        </p>
      )}
    </li>
  );
}
