import axios, { type InternalAxiosRequestConfig } from "axios";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

let csrfToken = "";
let csrfPromise: Promise<string> | null = null;

function resolveSecureEntryPath(): string {
  const segments = window.location.pathname.split("/").filter(Boolean);
  if (segments.length === 0) {
    return "";
  }

  const first = segments[0];
  if (first === "api" || first === "assets") {
    return "";
  }

  return first;
}

async function fetchCsrfToken(force = false): Promise<string> {
  if (!force && csrfToken) {
    return csrfToken;
  }

  if (!force && csrfPromise) {
    return csrfPromise;
  }

  csrfPromise = (async () => {
    try {
      const headers: Record<string, string> = {};
      const secureEntryPath = resolveSecureEntryPath();

      if (secureEntryPath) {
        headers["X-AdaPanel-Entry-Path"] = secureEntryPath;
      }

      const response = await fetch("/api/v1/auth/csrf", {
        credentials: "include",
        headers
      });

      const payload = (await response.json()) as ApiEnvelope<{ requestToken: string }>;
      csrfToken = payload?.data?.requestToken ?? "";
      return csrfToken;
    } catch {
      // Ağ hatası ("Failed to fetch") / boş yanıt → ÇÖKME yerine boş token (çağıran tarafta temiz hata).
      csrfToken = "";
      return csrfToken;
    } finally {
      csrfPromise = null;
    }
  })();

  return csrfPromise;
}

export function clearCsrfToken(): void {
  csrfToken = "";
}

// Manuel fetch (ör. SSE göç akışı) için mutasyon başlıklarını üretir — axios interceptor'ı ile AYNI kuralları
// izler: Content-Type + (varsa) güvenli-giriş yolu + CSRF jetonu. Böylece akış uçları da CSRF korumasından geçer.
export async function buildStreamRequestHeaders(): Promise<Record<string, string>> {
  const headers: Record<string, string> = { "Content-Type": "application/json" };

  const secureEntryPath = resolveSecureEntryPath();
  if (secureEntryPath) {
    headers["X-AdaPanel-Entry-Path"] = secureEntryPath;
  }

  headers["X-CSRF-TOKEN"] = await fetchCsrfToken();
  return headers;
}

export function readSecureEntryPath(): string {
  return resolveSecureEntryPath();
}

export const apiClient = axios.create({
  baseURL: "/api/v1",
  withCredentials: true,
  headers: {
    "Content-Type": "application/json"
  }
});

apiClient.interceptors.request.use(async (config: InternalAxiosRequestConfig) => {
  const headers = config.headers as Record<string, string>;
  const secureEntryPath = resolveSecureEntryPath();

  if (secureEntryPath) {
    headers["X-AdaPanel-Entry-Path"] = secureEntryPath;
  }

  const method = (config.method ?? "get").toUpperCase();
  if (["POST", "PUT", "PATCH", "DELETE"].includes(method)) {
    const token = await fetchCsrfToken();
    headers["X-CSRF-TOKEN"] = token;
  }

  return config;
});
