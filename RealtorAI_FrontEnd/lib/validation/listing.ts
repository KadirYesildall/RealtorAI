import { z } from "zod";

/**
 * T082 — listing and profile-completion validation (constitution Principle B: Zod on every form).
 *
 * The same rules the API applies (`Modules.Listings`, `ProfileEndpoints`), checked here first so
 * an obvious mistake is pointed at before a round trip. The API re-validates everything and is
 * the authority; this is a convenience, never the guard.
 *
 * Messages are dictionary keys under `listings.errors` (or `profileCompletion.errors`), not
 * prose: actions run on the server with no locale to hand, and the form resolves each key in the
 * agent's language.
 */

/** Residential RESO PropertySubType values the API accepts (research R33). */
export const PROPERTY_TYPES = [
  "SingleFamilyResidence",
  "Condominium",
  "Townhouse",
  "Duplex",
  "Triplex",
  "Quadruplex",
  "ManufacturedHome",
  "ManufacturedOnLand",
  "StockCooperative",
  "Cabin",
] as const;

export type PropertyType = (typeof PROPERTY_TYPES)[number];

/** FR-003. */
export const MAX_PHOTOS_PER_LISTING = 100;

/** The API's Storage:MaxUploadBytes — the originals bucket's 50 MiB (research R33). */
export const MAX_PHOTO_BYTES = 50 * 1024 * 1024;

/**
 * What the file picker offers. The API decides from the bytes, not from this list or the file
 * name, so this only saves the agent from picking something that is certain to be refused.
 */
export const PHOTO_ACCEPT = "image/jpeg,image/png,image/heic,image/heif,image/webp,.heic,.heif";

/** The FR-008 public-record fields, required before approval while empty. */
export const PUBLIC_RECORD_FIELDS = ["LivingArea", "LotSizeSquareFeet", "YearBuilt", "TaxAnnualAmount"] as const;

export type PublicRecordField = (typeof PUBLIC_RECORD_FIELDS)[number];

const STATE = /^[A-Z]{2}$/;

/** An empty input means "not given", not zero. */
const optionalNumber = (schema: z.ZodNumber) =>
  z.preprocess(
    (value) => (value === "" || value === null || value === undefined ? null : Number(value)),
    schema.nullable()
  );

export const ListingBasicsSchema = z.object({
  addressLine1: z.string().trim().min(1, "addressRequired").max(200, "tooLong"),
  city: z.string().trim().min(1, "cityRequired").max(200, "tooLong"),
  state: z.string().trim().toUpperCase().regex(STATE, "stateFormat"),
  postalCode: z.string().trim().regex(/^[0-9]{5}(-[0-9]{4})?$/, "postalCodeFormat"),
  propertyType: z.enum(PROPERTY_TYPES, "propertyTypeInvalid"),
  listPrice: optionalNumber(
    z
      .number("priceFormat")
      .min(0, "priceFormat")
      .max(9_999_999_999.99, "priceFormat")
      .refine((value) => Math.round(value * 100) === value * 100, "priceFormat")
  ),
  bedrooms: optionalNumber(z.number("bedroomsFormat").int("bedroomsFormat").min(0, "bedroomsFormat").max(99, "bedroomsFormat")),
  bathrooms: optionalNumber(
    z
      .number("bathroomsFormat")
      .min(0, "bathroomsFormat")
      .max(99.5, "bathroomsFormat")
      .refine((value) => Number.isInteger(value * 2), "bathroomsFormat")
  ),
  agentNotes: z
    .string()
    .trim()
    .max(5000, "notesTooLong")
    .optional()
    .transform((value) => (value ? value : null)),
});

export type ListingBasicsInput = z.input<typeof ListingBasicsSchema>;
export type ListingBasics = z.output<typeof ListingBasicsSchema>;

/** Field names of the basics form, for mapping errors back to inputs. */
export const LISTING_BASICS_FIELDS = Object.keys(ListingBasicsSchema.shape) as (keyof ListingBasics)[];

/**
 * A public-record field the agent enters by hand (FR-008's fallback). Always `agent_entered`:
 * these are FR-007 fields, which nothing but the agent or a public record may supply.
 */
export function publicRecordValueSchema(field: PublicRecordField, currentYear: number) {
  if (field === "YearBuilt") {
    return z.coerce
      .number("yearFormat")
      .int("yearFormat")
      .min(1600, "yearFormat")
      .max(currentYear + 3, "yearFormat");
  }

  return z.coerce.number("measurementFormat").min(0, "measurementFormat");
}

export const ProfileCompletionSchema = z
  .object({
    licenseNumber: z.string().trim().min(1, "licenseRequired").max(50, "licenseTooLong"),
    licenseState: z.string().trim().toUpperCase().regex(STATE, "stateFormat"),

    /** False when the agent already has a live membership, which this form does not change. */
    declaresBrokerage: z.boolean(),
    brokerageId: z
      .string()
      .trim()
      .transform((value) => (value.length === 0 ? null : value))
      .pipe(z.uuid("brokerageIdFormat").nullable()),
    brokerageName: z.string().trim().max(200, "tooLong"),
    brokerageLicenseNumber: z.string().trim().max(100, "licenseTooLong"),
    brokerageState: z.string().trim().toUpperCase(),
  })
  .superRefine((value, context) => {
    if (!value.declaresBrokerage) {
      return;
    }

    if (value.brokerageName.length === 0) {
      context.addIssue({ code: "custom", path: ["brokerageName"], message: "brokerageNameRequired" });
    }

    // Joining an existing brokerage needs only its id and name; creating one needs its license
    // and state too, because they appear in advertising disclosures.
    if (value.brokerageId === null) {
      if (value.brokerageLicenseNumber.length === 0) {
        context.addIssue({ code: "custom", path: ["brokerageLicenseNumber"], message: "licenseRequired" });
      }

      if (!STATE.test(value.brokerageState)) {
        context.addIssue({ code: "custom", path: ["brokerageState"], message: "stateFormat" });
      }
    }
  });

export type ProfileCompletionValues = z.output<typeof ProfileCompletionSchema>;

/**
 * Where profile completion may send the agent afterwards. Only paths inside the dashboard, so
 * a crafted `?next=` cannot turn the form into an open redirect.
 */
export function safeNextPath(value: string | string[] | undefined): string {
  const candidate = Array.isArray(value) ? value[0] : value;

  return candidate && /^\/dashboard(\/[A-Za-z0-9/_-]*)?(\?[A-Za-z0-9=&_-]*)?$/.test(candidate)
    ? candidate
    : "/dashboard/listings/new";
}

// ───────────────────────────── US2: photos ─────────────────────────────

/** The contract's RoomType, in the walk-through order the grid offers them. */
export const ROOM_TYPES = [
  "exterior_front",
  "living_room",
  "kitchen",
  "primary_bedroom",
  "bedroom",
  "bathroom",
  "backyard",
  "pool",
  "view",
  "garage",
  "floor_plan",
  "other",
] as const;

export type RoomType = (typeof ROOM_TYPES)[number];

/** The API's bounds on an agent's photo edits (PhotoEditEndpoints). */
export const MAX_PHOTO_TAGS = 20;
export const MAX_TAG_LENGTH = 60;
export const MAX_CAPTION_LENGTH = 300;

export const PhotoPatchSchema = z
  .object({
    roomType: z.enum(ROOM_TYPES).nullable().optional(),
    featureTags: z.array(z.string().trim().min(1).max(MAX_TAG_LENGTH)).max(MAX_PHOTO_TAGS).nullable().optional(),
    caption: z.string().trim().max(MAX_CAPTION_LENGTH).nullable().optional(),
    altered: z.boolean().optional(),
    alterationType: z.enum(["virtual_staging", "other"]).optional(),
    disclosureLabel: z.string().min(1).optional(),
    pairedOriginalId: z.uuid().optional(),
  })
  .strict();

/** "Island, Pendant lights" → ["Island", "Pendant lights"]; blanks and repeats dropped. */
export function parseTags(text: string): string[] {
  const seen = new Set<string>();

  return text
    .split(",")
    .map((tag) => tag.trim())
    .filter((tag) => {
      const key = tag.toLowerCase();
      if (tag.length === 0 || seen.has(key)) return false;
      seen.add(key);
      return true;
    });
}

// ───────────────────────────── US3: listing content ─────────────────────────────

/** The contract's `Tone`, in the order the switcher offers them (FR-013). */
export const TONES = ["professional", "warm", "luxury", "concise"] as const;

export type ToneValue = (typeof TONES)[number];

/** The contract's `ContentKind`, in the order the review shows them. */
export const CONTENT_KINDS = ["public_remarks", "private_remarks", "headline"] as const;

export type ContentKindValue = (typeof CONTENT_KINDS)[number];

/**
 * Bounds what is sent; the MLS's own limit is the API's to enforce, from the rule profile. This
 * ceiling is above any remarks limit and only stops an accidental paste of a document.
 */
export const MAX_CONTENT_LENGTH = 10_000;

export const ContentBodySchema = z
  .string()
  .refine((value) => value.trim().length > 0, "contentEmpty")
  .refine((value) => value.length <= MAX_CONTENT_LENGTH, "tooLong");
