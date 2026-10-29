import { ref } from "vue";
import type { PanelPreferences, ServerAddressDnsSync } from "../api/panelSettings";

export function dnsSyncHasFailure(sync?: ServerAddressDnsSync | null) {
  return !!sync && (sync.pending || !!sync.error || sync.zones.some(zone => !zone.succeeded) || !!(sync.ftp && !sync.ftp.succeeded && !sync.ftp.skipped));
}

/** One settings write at a time; ignore late replies after logout/actor change/unmount. */
export function usePanelPreferencesMutation(options: {
  actor: () => string;
  allowed: () => boolean;
  before: () => void;
  apply: (preferences: PanelPreferences, current: () => boolean) => Promise<void>;
  failure: (error: unknown) => void;
}) {
  const busy = ref(false);
  async function run(write: () => Promise<PanelPreferences>, apply = options.apply) {
    if (busy.value || !options.allowed()) return;
    const actor = options.actor();
    const current = () => options.allowed() && options.actor() === actor;
    busy.value = true;
    options.before();
    try {
      const preferences = await write();
      if (current()) await apply(preferences, current);
    } catch (error) {
      if (current()) options.failure(error);
    } finally {
      busy.value = false;
    }
  }
  return { busy, run };
}
