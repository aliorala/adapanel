import { expect, it, vi } from "vitest";
import { updatePanelPreferences, type PanelPreferences } from "./panelSettings";
import { apiClient } from "./client";

vi.mock("./client", () => ({ apiClient: { put: vi.fn() } }));

it("requests same-address reconciliation only for an explicit general settings save", async () => {
  const preferences = { serverAddress: "176.43.253.97", themeMode: "dark" } as PanelPreferences;
  vi.mocked(apiClient.put).mockResolvedValue({ data: { data: preferences } });
  await updatePanelPreferences(preferences, true);
  expect(apiClient.put).toHaveBeenLastCalledWith("/panel-settings", { ...preferences, reconcileServerAddress: true });
  await updatePanelPreferences(preferences);
  expect(apiClient.put).toHaveBeenLastCalledWith("/panel-settings", { ...preferences, reconcileServerAddress: false });
});
