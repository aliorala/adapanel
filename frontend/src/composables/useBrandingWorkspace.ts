import { ref, watch } from 'vue';
import { emptyBranding, getBranding, getBrandingSettings, updateBrandingSettings, uploadBrandingImage, removeBrandingImage, type Branding, type BrandingImageKind } from '../api/panelSettings';

type Options = {
  active: () => boolean;
  actor: () => string;
  allowed: () => boolean;
  apply: (brand: Branding) => void;
  notify: (message: string, error: boolean) => void;
  error: (error: unknown) => string;
  saved: () => string;
  refreshFailed: () => string;
};

/** Own-scope editing and effective branding reads share one mutation boundary. */
export function useBrandingWorkspace(options: Options) {
  const form = ref<Branding>(emptyBranding()), loaded = ref(false), busy = ref(false);
  let generation = 0, effectiveSequence = 0, settingsSequence = 0, draftRevision = 0;
  watch(form, () => { draftRevision++; }, { deep: true, flush: 'sync' });
  function capture() {
    const actor = options.actor(), version = generation;
    return () => options.active() && actor === options.actor() && version === generation;
  }
  function invalidate() { generation++; effectiveSequence++; settingsSequence++; busy.value = false; }
  async function loadEffective(fromMutation = false): Promise<boolean> {
    if (!options.active() || (busy.value && !fromMutation)) return false;
    const current = capture(), sequence = ++effectiveSequence;
    try {
      const brand = await getBranding();
      if (!current() || sequence !== effectiveSequence) return false;
      options.apply(brand); return true;
    } catch { return false; } // Public login branding remains optional; writes report refresh failure below.
  }
  async function loadSettings() {
    if (!options.active() || !options.allowed() || busy.value) return;
    const current = capture(), sequence = ++settingsSequence, revision = draftRevision;
    try {
      const settings = await getBrandingSettings();
      if (!current() || sequence !== settingsSequence) return;
      if (revision === draftRevision) form.value = settings;
      loaded.value = true;
    } catch (error) { if (current() && sequence === settingsSequence) options.notify(options.error(error), true); }
  }
  async function mutate(action: () => Promise<Branding>, fields: (keyof Branding)[]) {
    if (!options.active() || !options.allowed() || busy.value) return;
    invalidate(); busy.value = true;
    const current = capture(), submitted = { ...form.value };
    try {
      const saved = await action();
      if (!current()) return;
      // Only fields this operation wrote can replace the draft. Asset writes never save text inputs.
      const next = { ...form.value };
      for (const field of fields) if (next[field] === submitted[field]) next[field] = saved[field];
      form.value = next; loaded.value = true;
      const refreshed = await loadEffective(true);
      if (current()) options.notify(refreshed ? options.saved() : options.refreshFailed(), !refreshed);
    } catch (error) { if (current()) options.notify(options.error(error), true); }
    finally { if (current()) busy.value = false; }
  }
  async function save() {
    const payload = { panelName: form.value.panelName, accentColor: form.value.accentColor, logoLinkUrl: form.value.logoLinkUrl };
    await mutate(() => updateBrandingSettings(payload), ['panelName', 'accentColor', 'logoLinkUrl']);
  }
  async function upload(kind: BrandingImageKind, event: Event) {
    const input = event.target as HTMLInputElement, file = input.files?.[0];
    if (!file || busy.value || !options.active() || !options.allowed()) return;
    const pending = mutate(() => uploadBrandingImage(kind, file), [kind === 'logo' ? 'logoDataUrl' : 'faviconDataUrl']);
    const current = capture();
    try { await pending; }
    finally { if (current() && input.files?.[0] === file) input.value = ''; }
  }
  async function remove(kind: BrandingImageKind) {
    await mutate(() => removeBrandingImage(kind), [kind === 'logo' ? 'logoDataUrl' : 'faviconDataUrl']);
  }
  return { form, loaded, busy, invalidate, loadEffective, loadSettings, save, upload, remove };
}
