import { expect, it } from "vitest";
import type { UpdateStatus } from "../api/update";
import { describeUpdateState } from "./updatePresentation";

const current = { currentVersion: "0.8.75", latestVersion: "0.8.75", isCheckFailed: false, isUpdateAvailable: false, isPackageReady: false, isInstallScheduled: false } as UpdateStatus;
const ready = { ...current, isPackageReady: true, preparedVersion: "0.8.76", preparedPackageSha256: "a".repeat(64) };

it("shows an updated panel as current after its temporary installer was removed", () => {
  expect(describeUpdateState(current, true, false)).toMatchObject({ state: "current", tone: "ok" });
});
it.each([true, undefined, null])("does not call the panel current when release checking is %s", isCheckFailed => {
  expect(describeUpdateState({ ...current, isCheckFailed } as UpdateStatus, true, false).state).toBe("unconfirmed");
});
it("does not reuse a green state after a failed refresh or during a new check", () => {
  expect(describeUpdateState(current, false, false).state).toBe("unconfirmed");
  expect(describeUpdateState(current, true, true).state).toBe("checking");
});
it("distinguishes an available release from a prepared installer", () => {
  expect(describeUpdateState({ ...current, isUpdateAvailable: true }, true, false).state).toBe("available");
  expect(describeUpdateState({ ...ready, isUpdateAvailable: true }, true, false).state).toBe("ready");
});
it("does not show current or prepared while an installation is scheduled", () => {
  expect(describeUpdateState({ ...current, isInstallScheduled: true, isPackageReady: true }, true, false).state).toBe("installing");
});
it("keeps a locally verified installer ready when only checking the latest release failed", () => {
  expect(describeUpdateState({ ...ready, isCheckFailed: true }, true, false)).toMatchObject({ state: "ready", tone: "ok" });
});
it("does not advertise an installer with missing prepared identity as ready", () => {
  expect(describeUpdateState({ ...current, isPackageReady: true }, true, false).state).toBe("unconfirmed");
});
it("does not advertise missing latest version information as current", () => {
  expect(describeUpdateState({ ...current, latestVersion: "" }, true, false).state).toBe("unconfirmed");
});
it.each(["scheduled", "restarting", "verifying", "failed", "timeout"])("keeps the observed %s outcome ahead of old version metadata", phase => {
  expect(describeUpdateState(current, true, false, phase).state).toBe(phase === "failed" ? "failed" : phase === "timeout" ? "unconfirmed" : "installing");
});
