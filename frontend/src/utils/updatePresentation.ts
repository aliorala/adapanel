import type { UpdateStatus } from "../api/update";

type Presentation = { state: string; tone: "ok" | "info" | "warn" | "error"; icon: "check" | "download" | "clock" | "alert" };

export function describeUpdateState(status: UpdateStatus | null, confirmed: boolean, checking: boolean, phase = ""): Presentation {
  if (phase === "failed") return { state: "failed", tone: "error", icon: "alert" };
  if (phase === "timeout") return { state: "unconfirmed", tone: "warn", icon: "alert" };
  if (["scheduled", "restarting", "verifying"].includes(phase) || (confirmed && status?.isInstallScheduled === true))
    return { state: "installing", tone: "info", icon: "clock" };
  if (phase === "downloading") return { state: "downloading", tone: "info", icon: "download" };
  if (checking) return { state: "checking", tone: "info", icon: "clock" };
  if (!status || !confirmed)
    return { state: "unconfirmed", tone: "warn", icon: "alert" };
  if (status.isPackageReady === true) return status.preparedVersion?.trim() && /^[a-f\d]{64}$/i.test(status.preparedPackageSha256 ?? "")
    ? { state: "ready", tone: "ok", icon: "check" }
    : { state: "unconfirmed", tone: "warn", icon: "alert" };
  if (status.isCheckFailed !== false) return { state: "unconfirmed", tone: "warn", icon: "alert" };
  if (!status.currentVersion?.trim() || !status.latestVersion?.trim()) return { state: "unconfirmed", tone: "warn", icon: "alert" };
  if (status.isUpdateAvailable === true) return { state: "available", tone: "info", icon: "download" };
  if (status.isUpdateAvailable === false) return { state: "current", tone: "ok", icon: "check" };
  return { state: "unconfirmed", tone: "warn", icon: "alert" };
}
