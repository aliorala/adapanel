/** Only for display. The scheduler must receive the original URL on save. */
export function taskUrlDisplay(value: string): string {
  if (!value) return "";
  try {
    const url = new URL(value);
    if (url.protocol !== "http:" && url.protocol !== "https:") return "•••";
    if (!url.username && !url.password && !url.search && !url.hash) return value;
    return url.origin + url.pathname + (url.search ? "?•••" : "") + (url.hash ? "#•••" : "");
  } catch {
    // Older tasks can expose the runner arguments instead of a parsed URL.
    return "•••";
  }
}

export function taskCommandDisplay(value: string): string {
  return value.replace(/https?:\/\/[^\s"'<>]+/gi, taskUrlDisplay);
}
