import { z } from "zod";

export const SignUpSchema = z.object({
  fullName: z.string().trim().min(2).max(100),
  email: z.email().trim(),
  password: z.string().min(8),
  acceptedTerms: z.literal(true, "You must accept the Terms of Service and Privacy Policy."),
});
export type SignUpInput = z.infer<typeof SignUpSchema>;

export const SignInSchema = z.object({
  email: z.email().trim(),
  password: z.string().min(8),
});
export type SignInInput = z.infer<typeof SignInSchema>;

export const LanguageSchema = z.object({
  language: z.enum(["en", "tr"]),
});
export type LanguageInput = z.infer<typeof LanguageSchema>;
