import axios, { AxiosError, type InternalAxiosRequestConfig } from "axios";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

let sessionEpoch = 0;
const requestEpochs = new WeakMap<object, number>();
let csrfToken = "";
let csrfPromise: Promise<string> | null = null;
let csrfGeneration = 0;
let csrfController: AbortController | null = null;

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

async function fetchCsrfToken(): Promise<string> {
  if (csrfToken) {
    return csrfToken;
  }

  if (csrfPromise) {
    return csrfPromise;
  }

  const generation = csrfGeneration;
  const controller = new AbortController();
  csrfController = controller;
  csrfPromise = (async () => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      const headers: Record<string, string> = {};
      const secureEntryPath = resolveSecureEntryPath();

      if (secureEntryPath) {
        headers["X-AdaPanel-Entry-Path"] = secureEntryPath;
      }

      const request = async () => {
        const response = await fetch("/api/v1/auth/csrf", {
          credentials: "include",
          signal: controller.signal,
          headers
        });
        const payload = await response.json().catch(() => null) as ApiEnvelope<{ requestToken: string }> | null;
        if (!response.ok) {
          const config: InternalAxiosRequestConfig = { url: "/auth/csrf", method: "get", headers: new axios.AxiosHeaders(headers) };
          throw new AxiosError("CSRF request failed", "ERR_BAD_RESPONSE", config, null,
            { data: payload, status: response.status, statusText: response.statusText, headers: {}, config });
        }
        const token = payload?.data?.requestToken;
        if (typeof token !== "string" || !token.trim()) throw new AxiosError("Invalid CSRF response", "ERR_BAD_RESPONSE");
        return token;
      };
      // Axios'un istek zaman aşımı interceptor öncesindeki bu beklemeyi kapsamaz.
      // Süre sınırı gövdenin okunmasını da kapsar; takılmış bir yanıt bütün yazmaları kilitlemesin.
      const timeout = new Promise<never>((_, reject) => {
        timer = setTimeout(() => {
          reject(new AxiosError("CSRF request timeout", "ETIMEDOUT"));
          controller.abort();
        }, 15_000);
      });
      const token = await Promise.race([request(), timeout]);
      if (generation !== csrfGeneration) throw new axios.CanceledError("CSRF context changed");
      csrfToken = token;
      return csrfToken;
    } catch (error) {
      if (generation !== csrfGeneration || axios.isCancel(error)) throw new axios.CanceledError("CSRF context changed");
      csrfToken = "";
      if (axios.isAxiosError(error)) throw error;
      throw new AxiosError("CSRF network request failed", "ERR_NETWORK");
    } finally {
      if (timer !== undefined) clearTimeout(timer);
      if (generation === csrfGeneration) { csrfPromise = null; csrfController = null; }
    }
  })();

  return csrfPromise;
}

export function clearCsrfToken(): void {
  csrfGeneration++;
  csrfController?.abort();
  csrfController = null;
  csrfPromise = null;
  csrfToken = "";
}

export function getSessionEpoch(): number { return sessionEpoch; }

export function invalidateSessionRequests(): void {
  sessionEpoch++;
  clearCsrfToken();
  setReadOnlyMode("");
}

function isStale(config?: object): boolean {
  const epoch = config && requestEpochs.get(config);
  return epoch !== undefined && epoch !== sessionEpoch;
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

/*
 * SALT OKUNUR MOD (askıya alınmış abonelik) — TEK KAPI.
 *
 * Sunucu askıdaki abonelik için BÜTÜN POST/PUT/PATCH/DELETE isteklerini
 * `403 subscription.suspended_readonly` ile reddediyor. Bu kapı aynı kuralı istemcide de
 * uygular: istek AĞA HİÇ ÇIKMAZ ve çağıran ekran, sunucudan gelmiş gibi çevrilebilir bir
 * `messageKey` görür (her ekranın `resolveApiMessage`'ı bunu zaten çeviriyor).
 *
 * Kuralın tek yerde olması şart: elli ekranın her düğmesine ayrı ayrı koşul yazmak, bir tanesi
 * unutulduğunda kullanıcıyı yine ham 403'e sürerdi.
 *
 * İSTİSNA — `/auth/*`: sunucu da bu yolları askıya takmıyor. Aksi hâlde askıdaki kullanıcı
 * çıkış yapamaz, parolasını değiştiremez, iki adımlı doğrulamasını yönetemez görünürdü.
 */
const MUTATING_METHODS = ["POST", "PUT", "PATCH", "DELETE"];
let readOnlyMessageKey = "";

/** Salt okunur modu açar (mesaj anahtarıyla) ya da boş dizgiyle kapatır. Tek çağıran: abonelik store'u. */
export function setReadOnlyMode(messageKey: string): void {
  readOnlyMessageKey = messageKey;
}

export function isReadOnlyMode(): boolean {
  return readOnlyMessageKey.length > 0;
}

function isAuthPath(url: string | undefined): boolean {
  // `config.url` baseURL'e göredir: "/auth/logout", "auth/logout" ya da tam yol gelebilir.
  return (url ?? "").replace(/^\/?(api\/v1\/)?/, "").startsWith("auth/");
}

/** Ağa çıkmadan, sunucunun döndüreceğiyle AYNI biçimde reddeder (çağıranlar tek kalıp kullanır). */
function readOnlyRejection(config: InternalAxiosRequestConfig): AxiosError {
  return new AxiosError(
    readOnlyMessageKey,
    "ERR_SUBSCRIPTION_READONLY",
    config,
    null,
    {
      status: 403,
      statusText: "Forbidden",
      data: { success: false, data: null, message: readOnlyMessageKey, code: 403 },
      headers: {},
      config
    }
  );
}

export const apiClient = axios.create({
  baseURL: "/api/v1",
  withCredentials: true,
  headers: {
    "Content-Type": "application/json"
  }
});

// Bir isteğin iptali, aynı CSRF isteğini bekleyen diğer kullanıcı işlemlerini iptal etmez.
function waitForCsrf(pending: Promise<string>, signal: InternalAxiosRequestConfig["signal"]): Promise<string> {
  if (!signal) return pending;
  return new Promise((resolve, reject) => {
    const cleanup = () => signal.removeEventListener?.("abort", abort);
    const abort = () => { cleanup(); reject(new axios.CanceledError("Request cancelled")); };
    pending.then(value => { cleanup(); resolve(value); }, error => { cleanup(); reject(error); });
    if (signal.aborted) abort();
    else signal.addEventListener?.("abort", abort, { once: true });
  });
}

apiClient.interceptors.request.use(async (config: InternalAxiosRequestConfig) => {
  requestEpochs.set(config, sessionEpoch);
  const headers = config.headers as Record<string, string>;
  const secureEntryPath = resolveSecureEntryPath();

  if (secureEntryPath) {
    headers["X-AdaPanel-Entry-Path"] = secureEntryPath;
  }

  const method = (config.method ?? "get").toUpperCase();
  if (MUTATING_METHODS.includes(method)) {
    if (isReadOnlyMode() && !isAuthPath(config.url)) {
      throw readOnlyRejection(config);
    }

    const token = await waitForCsrf(fetchCsrfToken(), config.signal);
    if (isReadOnlyMode() && !isAuthPath(config.url)) throw readOnlyRejection(config);
    headers["X-CSRF-TOKEN"] = token;
  }

  if (isStale(config)) throw new axios.CanceledError("Session changed");
  return config;
});

/*
 * OTURUM DÜŞTÜ KAPISI — TEK YER.
 *
 * NEDEN: yanıt interceptor'ı HİÇ YOKTU. Oturum zaman aşımına uğradığında (ya da sunucu yeniden
 * başladığında) panel ölü ekranda kalıyordu: kullanıcı hâlâ paneli görüyor, bastığı HER düğme
 * `401` ile kırmızı hata veriyor ve tek çıkış yolu sayfayı ELLE yenilemek oluyordu. Kimse
 * "oturumun bitti" demiyordu.
 *
 * Kabuk (SetupWizardView) buraya bir işleyici kaydeder; 401 gelince giriş ekranına döner.
 * Kural burada duruyor çünkü elli ekranın her çağrısına ayrı 401 kontrolü yazmak, biri
 * unutulduğunda kullanıcıyı yine ölü ekranda bırakırdı.
 */
type SessionExpiredHandler = () => void;
let sessionExpiredHandler: SessionExpiredHandler | null = null;

/** Kabuk çağırır. Tek işleyici tutulur: ikinci kayıt öncekinin yerine geçer. */
export function setSessionExpiredHandler(handler: SessionExpiredHandler | null): void {
  sessionExpiredHandler = handler;
}

apiClient.interceptors.response.use(
  (response) => {
    if (isStale(response.config)) throw new axios.CanceledError("Session changed");
    return response;
  },
  (error: AxiosError) => {
    if (isStale(error.config)) return Promise.reject(new axios.CanceledError("Session changed"));
    /*
     * `/auth/*` DIŞARIDA: bu yolların 401'i "oturum düştü" DEĞİLDİR.
     * - `/auth/login` → parola yanlış (kullanıcı zaten giriş ekranında),
     * - `/auth/session` → henüz giriş yapılmamış (açılıştaki normal durum).
     * Bunları oturum düşmesi saymak, kullanıcıyı yanlış parolada "oturumunuz bitti" mesajıyla
     * karşılardı.
     */
    if (error.response?.status === 401 && !isAuthPath(error.config?.url)) {
      // CSRF jetonu oturuma bağlıdır; ölü jetonla yeniden giriş denemesi 400 verirdi.
      clearCsrfToken();
      sessionExpiredHandler?.();
    }

    // Hata AYNEN ilerler: mesajı çevirmek çağıranın işi (bkz. utils/apiError.ts). Burada
    // yutulursa ekranların kendi `catch` blokları hiç çalışmaz.
    return Promise.reject(error);
  }
);
