import { createI18n, type MessageFunction } from "vue-i18n";

export function safeMessageCompiler(message: unknown): MessageFunction<string> {
  const source = typeof message === "string" ? message : String(message ?? "");
  const fn = ((ctx: { named?: (k: string) => unknown; list?: (i: number) => unknown }) =>
    source.replace(/\{([^{}]*)\}/g, (whole, rawToken: string) => {
      const token = rawToken.trim();
      const literal = token.match(/^'([\s\S]*)'$/) ?? token.match(/^"([\s\S]*)"$/);
      if (literal) return literal[1];
      const named = ctx.named?.(token);
      if (named !== undefined && named !== null) return String(named);
      if (/^\d+$/.test(token)) {
        const item = ctx.list?.(Number(token));
        if (item !== undefined && item !== null) return String(item);
      }
      return whole; // bilinmeyen yer tutucu: olduğu gibi bırak (varsayılan davranış)
    })) as MessageFunction<string>;
  (fn as unknown as { source: string }).source = source;
  return fn;
}

export const i18n = createI18n({ legacy: false, locale: "tr", fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: {} });
const loading = new Map<string, Promise<void>>();
let languageRequest = 0;
export async function setPanelLocale(value: string) {
  const locale = value.startsWith("en") ? "en" : "tr";
  const request = ++languageRequest;
  if (!loading.has(locale)) {
    const load = locale === "en" ? import("./en.json") : import("./tr.json");
    loading.set(locale, load.then((messages) => { i18n.global.setLocaleMessage(locale, messages.default); }).catch((error) => { loading.delete(locale); throw error; }));
  }
  await loading.get(locale);
  if (request !== languageRequest) return;
  i18n.global.locale.value = locale;
  document.documentElement.lang = locale;
  try { localStorage.setItem("adapanel-language", locale); } catch { /* private browsing */ }
}
