// OmniWin fork: reads the first lines of an OmniWin answer. It never upgrades a result: "unverified" is never shown as done.

/** What an OmniWin answer came to. Never upgraded: "unverified" is never shown as done. */
export type Outcome = "done" | "unverified" | "needsAi" | "refused" | "stopped" | "failed";

export interface Explained {
  outcome: Outcome;
  recipe: string | null;
  detail: string | null;
  suggestion: string | null;
}

const token = (line: string, key: string): string | null =>
  line.split(" ").find((t) => t.startsWith(key))?.slice(key.length) ?? null;

export const explain = (text: string): Explained => {
  const lines = text.replace(/\r/g, "").split("\n");
  const first = lines[0] ?? "";
  const detail =
    lines.find((l) => l.startsWith("detail:"))?.slice(7).trim() ?? null;
  const rule =
    lines.find((l) => l.startsWith("routed rule="))?.slice(12).trim() ?? "";
  const recipe = rule.startsWith("recipe.")
    ? rule.slice(7)
    : rule.startsWith("decision.recipe.")
      ? rule.slice(16)
      : null;
  const suggestion =
    lines
      .find((l) => l.startsWith("recipe: "))
      ?.slice(8)
      .split("(")[0] ?? null;
  if (first.startsWith("needs_brain"))
    return { outcome: "needsAi", recipe: null, detail, suggestion };
  const status = token(first, "status=");
  const sent = token(first, "input_sent=") === "true";
  if (token(first, "stopped_at=") && status !== "confirmed")
    return { outcome: "stopped", recipe, detail, suggestion };
  switch (status) {
    case "confirmed":
      return { outcome: "done", recipe, detail, suggestion };
    case "unverified":
      return { outcome: "unverified", recipe, detail, suggestion };
    case "denied":
    case "blocked":
    case "stale":
      return {
        outcome: sent ? "stopped" : "refused",
        recipe,
        detail,
        suggestion,
      };
    default:
      return { outcome: sent ? "stopped" : "failed", recipe, detail, suggestion };
  }
};

