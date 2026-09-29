import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";

import SitesView from "./SitesView.vue";

/**
 * "Oluştur ve yayınla" akışı — canlıda bildirilen kaza.
 *
 * KULLANICI BİLDİRİMİ: bir alan adı eklendi, site kuruldu, ama düğme hâlâ "Oluştur ve yayınla"
 * yazıyordu ve form açıktı; iş olmamış gibi görünüyordu, tekrar basılabilirdi.
 *
 * ÖLÇÜLEN SEBEP: `sitesStore.creating` akışın YALNIZ ilk çağrısını (POST /sites) kapsıyordu.
 * İkinci ve uzun süren adım (POST /sites/{ad}/defaults) o bayrak düştükten SONRA çalışıyor, form
 * ise ancak akışın en sonunda kapanıyordu. İkinci basışın bedeli ağırdı: sunucu aynı adda site
 * bulunca onu KALDIRIP yeniden oluşturuyordu.
 */

const createSiteMock = vi.fn();
const prepareSiteDefaultsMock = vi.fn();
const confirmMock = vi.fn();
let apiMessageKey = "";

vi.mock("../stores/sites", () => ({
  useSitesStore: () => ({
    sites: [],
    creating: false,
    selectedSiteName: "",
    busySiteName: "",
    createSite: createSiteMock,
    prepareSiteDefaults: prepareSiteDefaultsMock,
    fetchSites: vi.fn().mockResolvedValue([]),
    importExistingSites: vi.fn().mockResolvedValue([])
  })
}));

vi.mock("../stores/auth", () => ({
  extractApiMessage: () => apiMessageKey,
  extractApiDetails: () => "",
  useAuthStore: () => ({ user: { role: "Administrator" }, isAdmin: true })
}));

vi.mock("../components/ConfirmDialog.vue", () => ({
  default: {},
  useConfirm: () => ({ confirm: confirmMock })
}));

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

let app: App | undefined;

beforeEach(() => {
  apiMessageKey = "";
  createSiteMock.mockReset();
  prepareSiteDefaultsMock.mockReset().mockResolvedValue(null);
  confirmMock.mockReset().mockResolvedValue(false);
});

afterEach(() => {
  app?.unmount();
  app = undefined;
  vi.clearAllMocks();
});

type SitesViewState = {
  createForm: Record<string, unknown>;
  creatingSite: boolean;
  showCreateForm: boolean;
  submitCreateSite: (replaceExisting?: boolean) => Promise<void>;
};

async function mount(): Promise<SitesViewState> {
  const renderer = createRenderer<object, object>({
    createElement: () => ({}),
    createText: () => ({}),
    createComment: () => ({}),
    insert() {},
    remove() {},
    setText() {},
    setElementText() {},
    patchProp() {},
    parentNode: () => null,
    nextSibling: () => null
  });
  app = renderer.createApp({ ...(SitesView as Component), render: () => h("section") } as Component);
  // GERCEK pinia: SitesView, abonelik kapisi (useSubscriptionGate) uzerinden baska magazalara
  // dokunuyor. Her birini ayri ayri sahtelemek yerine bos bir pinia vermek hem daha az kod hem
  // daha gercekci -- varsayilan durumda kilit kapali.
  app.use(createPinia());
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: SitesViewState }).setupState;
  await nextTick();
  await nextTick();
  await nextTick();
  return state;
}

function siteSonucu(name: string) {
  return { site: { name, bindings: [{ port: 80 }] }, created: [], warnings: [], generatedCredentials: [] };
}

it("meşgulken ikinci gönderim oluşturmayı TEKRAR çağırmaz", async () => {
  const state = await mount();
  state.createForm.name = "videotools.com.tr";

  // İlk istek sürüyor: çözülmedi.
  let bitir!: (deger: unknown) => void;
  createSiteMock.mockImplementationOnce(() => new Promise(resolve => { bitir = resolve; }));

  const birinci = state.submitCreateSite();
  await nextTick();
  expect(state.creatingSite).toBe(true);

  // Kullanıcı "olmadı" sanıp tekrar basıyor. Kapı burada tutmazsa sunucu az önce kurulan siteyi
  // KALDIRIP yeniden kurardı.
  await state.submitCreateSite();
  expect(createSiteMock).toHaveBeenCalledTimes(1);

  bitir(siteSonucu("videotools.com.tr"));
  await birinci;
  expect(state.creatingSite).toBe(false);
});

it("form, uzun süren varsayılanlar adımını BEKLEMEDEN kapanır", async () => {
  const state = await mount();
  state.showCreateForm = true;
  state.createForm.name = "videotools.com.tr";

  // Varsayılanlar adımı (dns/güvenlik/ssl/ftp/runtime) canlıda uzun sürüyor; form onu beklerse
  // kullanıcı tam o pencerede "olmadı" sanıyor.
  let varsayilanlariBitir!: (deger: unknown) => void;
  createSiteMock.mockResolvedValueOnce(siteSonucu("videotools.com.tr"));
  prepareSiteDefaultsMock.mockImplementationOnce(() => new Promise(resolve => { varsayilanlariBitir = resolve; }));

  const akis = state.submitCreateSite();
  await nextTick();
  await nextTick();

  expect(state.showCreateForm).toBe(false);
  // Form kapandı ama iş sürüyor: düğme hâlâ kilitli olmalı.
  expect(state.creatingSite).toBe(true);

  varsayilanlariBitir(null);
  await akis;
  expect(state.creatingSite).toBe(false);
});

it("aynı ad varsa ONAY sorar ve onaylanınca isteği replaceExisting ile tekrarlar", async () => {
  const state = await mount();
  state.createForm.name = "videotools.com.tr";

  apiMessageKey = "sites.name_exists";
  createSiteMock.mockRejectedValueOnce(new Error("409"));
  createSiteMock.mockResolvedValueOnce(siteSonucu("videotools.com.tr"));
  confirmMock.mockImplementationOnce(async (secenekler: { action: () => Promise<void> }) => {
    await secenekler.action();
    return true;
  });

  await state.submitCreateSite();

  expect(confirmMock).toHaveBeenCalledTimes(1);
  expect(createSiteMock).toHaveBeenCalledTimes(2);
  expect(createSiteMock.mock.calls[0][0]).toMatchObject({ replaceExisting: false });
  expect(createSiteMock.mock.calls[1][0]).toMatchObject({ replaceExisting: true });
});

it("onay verilmezse istek TEKRARLANMAZ — site olduğu gibi kalır", async () => {
  const state = await mount();
  state.createForm.name = "videotools.com.tr";

  apiMessageKey = "sites.name_exists";
  createSiteMock.mockRejectedValueOnce(new Error("409"));
  confirmMock.mockResolvedValueOnce(false);

  await state.submitCreateSite();

  expect(confirmMock).toHaveBeenCalledTimes(1);
  expect(createSiteMock).toHaveBeenCalledTimes(1);
});

it("BAŞKA bir hata onay sormaz — yalnız sites.name_exists bu yola girer", async () => {
  const state = await mount();
  state.createForm.name = "videotools.com.tr";

  apiMessageKey = "sites.quota_exceeded";
  createSiteMock.mockRejectedValueOnce(new Error("403"));

  await state.submitCreateSite();

  expect(confirmMock).not.toHaveBeenCalled();
  expect(createSiteMock).toHaveBeenCalledTimes(1);
});
