import { z } from "zod";

/**
 * T063 — brokerage form validation (constitution Principle B: Zod on every form).
 *
 * Messages here are dictionary keys under `brokerage.errors`, not prose: the action runs on
 * the server with no locale to hand, and the form resolves each key in the agent's language.
 * The API re-validates everything — this exists so an obvious mistake is caught before a
 * round trip, and so a field error can point at the field.
 */

export const TONES = ["professional", "warm", "luxury", "concise"] as const;

export const BrokerageSettingsFormSchema = z.object({
  name: z.string().trim().min(1, "nameRequired").max(200, "nameTooLong"),
  licenseNumber: z.string().trim().min(1, "licenseRequired").max(100, "licenseTooLong"),
  state: z
    .string()
    .trim()
    .toUpperCase()
    .regex(/^[A-Z]{2}$/, "stateFormat"),

  // One disclaimer per line in the textarea; blank lines are dropped rather than rejected,
  // because a trailing newline is not a mistake worth stopping the agent for.
  requiredDisclaimers: z
    .string()
    .transform((value) =>
      value
        .split(/\r?\n/)
        .map((line) => line.trim())
        .filter((line) => line.length > 0)
    )
    .pipe(z.array(z.string().max(1000, "disclaimerTooLong")).max(20, "tooManyDisclaimers")),

  // Empty means "no target MLS". The API checks a non-empty value names a rule profile.
  targetMlsId: z
    .string()
    .trim()
    .transform((value) => (value.length === 0 ? null : value)),

  defaultTone: z.enum(TONES, "toneInvalid"),

  aiBudgetTokens: z.coerce.number("budgetInteger").int("budgetInteger").min(1, "budgetPositive"),
  aiBudgetImages: z.coerce.number("budgetInteger").int("budgetInteger").min(1, "budgetPositive"),
});

export type BrokerageSettingsFormInput = z.input<typeof BrokerageSettingsFormSchema>;
export type BrokerageSettingsFormValues = z.output<typeof BrokerageSettingsFormSchema>;

export const MembershipDecisionSchema = z.enum(["approved", "rejected"]);
export type MembershipDecision = z.infer<typeof MembershipDecisionSchema>;
