import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import path from "node:path";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "server-only": path.resolve(__dirname, "tests/contract/server-only-stub.ts"),
      "@": path.resolve(__dirname, "."),
    },
  },
  test: {
    environment: "node",
    include: ["tests/contract/**/*.test.ts"],
    setupFiles: ["tests/contract/setup.ts"],
    globals: true,
  },
});
