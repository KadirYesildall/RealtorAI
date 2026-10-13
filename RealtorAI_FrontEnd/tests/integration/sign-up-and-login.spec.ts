import { test, expect } from "@playwright/test";
import { uniqueEmail, getConfirmationLink } from "./helpers";

test.describe("User Story 1 — Email/password sign-up and login", () => {
  test("register, confirm via email, then log in and reach the dashboard", async ({
    page,
  }) => {
    const email = uniqueEmail("signup");

    await page.goto("/register");
    await page.getByLabel("Full name").fill("Jane Agent");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password", { exact: true }).fill("password123");
    await page.getByLabel(/Terms of Service/).check();
    await page.getByRole("button", { name: "Create account" }).click();

    await expect(page).toHaveURL(/\/register\/check-email/);
    await expect(page.getByRole("heading", { name: "Check your email" })).toBeVisible();

    const confirmationLink = await getConfirmationLink(email);
    await page.goto(confirmationLink);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password", { exact: true }).fill("password123");
    await page.getByRole("button", { name: "Sign in" }).click();

    await expect(page).toHaveURL(/\/dashboard/);
    await expect(page.getByRole("heading", { name: /Jane Agent/ })).toBeVisible();
  });

  test("shows an inline error for a wrong password without a page reload", async ({
    page,
  }) => {
    const email = uniqueEmail("wrongpw");
    await page.goto("/register");
    await page.getByLabel("Full name").fill("Wrong Pw");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password", { exact: true }).fill("password123");
    await page.getByLabel(/Terms of Service/).check();
    await page.getByRole("button", { name: "Create account" }).click();
    await expect(page).toHaveURL(/\/register\/check-email/);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password", { exact: true }).fill("wrongpassword");
    await page.getByRole("button", { name: "Sign in" }).click();

    await expect(page.getByText("Incorrect email or password.")).toBeVisible();
  });

  test("keeps the submit button disabled until the consent checkbox is checked", async ({
    page,
  }) => {
    await page.goto("/register");
    await page.getByLabel("Full name").fill("No Consent");
    await page.getByLabel("Email").fill(uniqueEmail("noconsent"));
    await page.getByLabel("Password", { exact: true }).fill("password123");

    await expect(page.getByRole("button", { name: "Create account" })).toBeDisabled();
    await expect(
      page.getByRole("button", { name: "Continue with Google" })
    ).toBeDisabled();

    await page.getByLabel(/Terms of Service/).check();
    await expect(page.getByRole("button", { name: "Create account" })).toBeEnabled();
  });
});
