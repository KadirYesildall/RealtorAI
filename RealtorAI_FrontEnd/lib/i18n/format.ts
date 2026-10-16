/**
 * Fills `{name}` placeholders in a dictionary string. Placeholders with no value are left as
 * they are, so a missing argument shows up visibly in review instead of rendering as nothing.
 */
export function format(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (placeholder, key: string) => values[key] ?? placeholder);
}
