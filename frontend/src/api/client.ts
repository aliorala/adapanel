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
