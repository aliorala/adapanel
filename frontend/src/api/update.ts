import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type UpdateAttempt = {
  phase: string;
  installerExitCode: number | null;
  error: string;
  /** Hükmü panel verdiyse çeviri anahtarı; serbest metin `error` yerine bu gösterilir. */
  errorKey: string;
  /** Hüküm çalıştırıcıdan DEĞİL, panelin kendi sürüm ölçümünden geldi. */
  isDecidedByPanel: boolean;
  updatedAtUtc: string | null;
  /** Deneme sonuçlandı mı? true ise arayüz beklemeyi bırakmalı. */
  isFinished: boolean;
  isFailed: boolean;
};

export type UpdateStatus = {
  currentVersion: string;
  isUpdateAvailable: boolean;
  /** Sürüm bilgisi GitHub'dan alınamadı; aşağıdaki sürüm önbellekten. "Güncelsiniz" DENMEZ. */
  isCheckFailed: boolean;
  /** Son kurulum denemesinin durumu; çalıştırıcının diske yazdığı gerçek sonuç. */
  lastAttempt: UpdateAttempt | null;
  latestVersion: string;
  downloadUrl: string;
  packageSha256: string;
  packagePath: string;
  /** Prepared package identity is independent of the current release manifest. */
  preparedVersion?: string;
  preparedPackageSha256?: string;
  isPackageReady: boolean;
  isInstallScheduled: boolean;
  installScheduledAtUtc: string | null;
  notes: string;
  githubRepo: string;
  checkedAtUtc: string;
};

function measuredStatus(envelope: ApiEnvelope<UpdateStatus>): UpdateStatus {
  const value = envelope?.data;
  if (envelope?.success !== true || !value || typeof value.currentVersion !== "string" || !value.currentVersion.trim() ||
    [value.latestVersion, value.downloadUrl, value.packageSha256, value.packagePath].some(item => typeof item !== "string") ||
    [value.isUpdateAvailable, value.isCheckFailed, value.isPackageReady, value.isInstallScheduled].some(item => typeof item !== "boolean") ||
    [value.preparedVersion, value.preparedPackageSha256].some(item => item !== undefined && typeof item !== "string") ||
    !isTimestamp(value.checkedAtUtc) ||
    (value.installScheduledAtUtc !== null && (!isTimestamp(value.installScheduledAtUtc) || Date.parse(value.installScheduledAtUtc) > Date.parse(value.checkedAtUtc))) ||
    (value.isInstallScheduled && value.installScheduledAtUtc === null) ||
    (value.lastAttempt !== null && !measuredAttempt(value.lastAttempt, Date.parse(value.checkedAtUtc))))
    throw new UserFacingError("panel.update.resultUnconfirmed");
  return value;
}

function isTimestamp(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0 && Number.isFinite(Date.parse(value));
}

function measuredAttempt(value: unknown, checkedAt: number): value is UpdateAttempt {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const attempt = value as UpdateAttempt;
  if ([attempt.phase, attempt.error, attempt.errorKey].some(item => typeof item !== "string") ||
    typeof attempt.isDecidedByPanel !== "boolean" ||
    (attempt.installerExitCode !== null && (typeof attempt.installerExitCode !== "number" || !Number.isSafeInteger(attempt.installerExitCode)))) return false;
  const phase = attempt.phase.toLowerCase(), finished = phase === "succeeded" || phase === "failed";
  if (attempt.isFinished !== finished || attempt.isFailed !== (phase === "failed")) return false;
  return attempt.updatedAtUtc === null ? !finished : isTimestamp(attempt.updatedAtUtc) && Date.parse(attempt.updatedAtUtc) <= checkedAt;
}

/**
 * force=true yalnız kullanıcı AÇIKÇA "kontrol et" dediğinde gönderilir: sunucu o zaman önbelleği
 * atlayıp GitHub'a gider. Otomatik çağrılarda (panel açılışı, rozet yenilemesi) önbellek kullanılır,
 * aksi hâlde GitHub'ın kimliksiz saatlik sınırı dolar ve yeni sürüm yayınlansa bile görünmez olur.
 */
export async function checkForUpdate(force = false) {
  const response = await apiClient.get<ApiEnvelope<UpdateStatus>>(
    force ? "/update/check?force=true" : "/update/check",
  );
  return measuredStatus(response.data);
}

export async function applyUpdate(downloadUrl: string, packageSha256 = "") {
  const response = await apiClient.post<ApiEnvelope<UpdateStatus>>("/update/apply", {
    downloadUrl,
    packageSha256
  });
  return measuredStatus(response.data);
}

export async function installPreparedUpdate(expectedVersion: string, expectedPackageSha256: string) {
  const response = await apiClient.post<ApiEnvelope<UpdateStatus>>("/update/install", { expectedVersion, expectedPackageSha256 });
  return measuredStatus(response.data);
}

// Güncellemelerin çekileceği GitHub deposunu (owner/repo) ayarla.
export async function setUpdateGithubRepo(repo: string) {
  const response = await apiClient.post<ApiEnvelope<UpdateStatus>>("/update/github", { repo });
  return measuredStatus(response.data);
}
