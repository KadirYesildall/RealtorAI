"use client";

import { useState } from "react";
import { TextField, type TextFieldProps } from "@/components/ui/TextField";
import { EyeIcon, EyeOffIcon, LockIcon } from "@/components/ui/icons";

export interface PasswordFieldProps extends Omit<
  TextFieldProps,
  "type" | "trailing" | "icon"
> {
  showLabel: string;
  hideLabel: string;
}

/**
 * Password input with a reveal toggle — the single biggest friction point on a sign-in
 * form, since a mistyped password is otherwise invisible.
 *
 * The toggle is a real <button type="button"> so it never submits the form, and it is
 * excluded from the tab order's happy path only in the sense that it comes after the
 * field itself. Its accessible name flips with state, which is also why the Playwright
 * specs target the password input with `{ exact: true }`: a non-exact getByLabel(
 * "Password") would otherwise match both the field and this toggle.
 */
export function PasswordField({ showLabel, hideLabel, ...props }: PasswordFieldProps) {
  const [visible, setVisible] = useState(false);
  const ToggleIcon = visible ? EyeOffIcon : EyeIcon;

  return (
    <TextField
      {...props}
      type={visible ? "text" : "password"}
      icon={<LockIcon />}
      trailing={
        <button
          type="button"
          onClick={() => setVisible((current) => !current)}
          aria-label={visible ? hideLabel : showLabel}
          aria-pressed={visible}
          className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-surface-muted)] hover:text-[var(--color-text)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-accent)]"
        >
          <ToggleIcon className="h-[18px] w-[18px]" />
        </button>
      }
    />
  );
}
