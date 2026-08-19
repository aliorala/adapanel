import axios, { AxiosError, type InternalAxiosRequestConfig } from "axios";

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

apiClient.interceptors.request.use(async (config: InternalAxiosRequestConfig) => {
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

    const token = await fetchCsrfToken();
    headers["X-CSRF-TOKEN"] = token;
  }

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
  (response) => response,
  (error: AxiosError) => {
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
