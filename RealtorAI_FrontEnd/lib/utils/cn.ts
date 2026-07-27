import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

/** Merges conditional class names and resolves conflicting Tailwind utility classes. */
export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
