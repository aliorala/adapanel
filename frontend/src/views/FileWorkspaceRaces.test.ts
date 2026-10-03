import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import FilesView from "./FilesView.vue";
import BackupView from "./BackupView.vue";
import { useSitesStore } from "../stores/sites";
import { compressPath, createFile, createDirectory, extractPath, getFileWorkspace, searchFiles, uploadFile, type FileWorkspace } from "../api/files";
import { getBackupEntries, restoreBackupItems, type BackupRecord } from "../api/backup";

vi.mock("../router", () => ({ default: { currentRoute: { value: { query: {} } }, push: vi.fn() } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
const promptMock = vi.hoisted(() => vi.fn());
const confirmMock = vi.hoisted(() => vi.fn());
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: promptMock }) }));
vi.mock("../api/files", async original => ({ ...await original<object>(), createFile: vi.fn(), createDirectory: vi.fn(), compressPath: vi.fn(), extractPath: vi.fn(), getFileWorkspace: vi.fn(), searchFiles: vi.fn(), uploadFile: vi.fn() }));
vi.mock("../api/backup", async original => ({ ...await original<object>(), restoreBackupItems: vi.fn(), getBackupEntries: vi.fn(), getBackups: vi.fn().mockResolvedValue([]), getBackupRunningJobs: vi.fn().mockResolvedValue({ running: [], interrupted: [] }) }));
vi.mock("../api/subscriptions", async original => ({ ...await original<object>(), getMySubscription: vi.fn().mockResolvedValue(null) }));

let app: App | undefined;
beforeEach(() => {
  confirmMock.mockImplementation(async options => { await options.action?.(); return true; });
  vi.stubGlobal("window", { addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.mocked(getFileWorkspace).mockImplementation(async path => workspace(path!));
});
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); vi.unstubAllGlobals(); });
function workspace(path: string): FileWorkspace {
  return { path, parentPath: "", displayName: path, physicalPath: "", isDirectory: true, content: "", entries: [] };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(yes => { resolve = yes; });
  return { promise, resolve };
}
async function mount<T>(view: Component): Promise<T> {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  useSitesStore().sites = [{ name: "a.test" }, { name: "b.test" }] as never;
  useSitesStore().selectedSiteName = "a.test";
  app = renderer.createApp({ ...view, render: () => h("section") } as Component).use(pinia);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: T }).setupState;
  for (let i = 0; i < 12; i++) await nextTick();
  return state;
}
type FilesState = {
  workspace: FileWorkspace; archiveBusy: boolean; searchResults: { name: string }[]; searchQuery: string;
  compress(path: string): Promise<void>; extract(path: string): Promise<void>; runSearch(): Promise<void>;
  uploadMany(files: File[], folders: string[]): Promise<void>;
  createNewFile(): Promise<void>; createFolder(): Promise<void>;
};

it.each(["createNewFile", "createFolder"] as const)("does not redirect %s into a different folder while the name prompt is open", async operation => {
  const answer = deferred<string>(); promptMock.mockReturnValueOnce(answer.promise);
  const state = await mount<FilesState>(FilesView);
  const pending = state[operation]();
  state.workspace = workspace("sites/a.test/other");
  answer.resolve("new.txt"); await pending;
  expect(createFile).not.toHaveBeenCalled();
  expect(createDirectory).not.toHaveBeenCalled();
});

it("does not open a newly created file over a folder selected during the request", async () => {
  promptMock.mockResolvedValueOnce("new.txt");
  const created = deferred<FileWorkspace>(); vi.mocked(createFile).mockReturnValueOnce(created.promise);
  const state = await mount<FilesState>(FilesView);
  const pending = state.createNewFile(); await nextTick();
  expect(createFile).toHaveBeenCalledWith("sites/a.test/new.txt");
  state.workspace = workspace("sites/a.test/other");
  created.resolve(workspace("sites/a.test/new.txt")); await pending;
  expect(state.workspace.path).toBe("sites/a.test/other");
});

it("opens a newly created file when the originating folder is still selected", async () => {
  promptMock.mockResolvedValueOnce("new.txt");
  vi.mocked(createFile).mockResolvedValueOnce(workspace("sites/a.test/new.txt"));
  const state = await mount<FilesState>(FilesView);
  await state.createNewFile();
  expect(state.workspace.path).toBe("sites/a.test/new.txt");
});

it.each(["compress", "extract"] as const)("ignores %s results after navigating to another folder and prevents duplicate archive jobs", async operation => {
  const delayed = deferred<FileWorkspace>();
  const api = operation === "compress" ? compressPath : extractPath;
  vi.mocked(api).mockReturnValue(delayed.promise);
  const state = await mount<FilesState>(FilesView);
  const pending = state[operation]("sites/a.test/data.zip");
  await state[operation]("sites/a.test/data.zip");
  expect(api).toHaveBeenCalledOnce();
  state.workspace = workspace("sites/a.test/new-folder");
  delayed.resolve(workspace("sites/a.test")); await pending;
  expect(state.workspace.path).toBe("sites/a.test/new-folder");
  expect(state.archiveBusy).toBe(false);
});

it("keeps the newest search when responses arrive in reverse order", async () => {
  const first = deferred<never[]>();
  vi.mocked(searchFiles).mockReturnValueOnce(first.promise).mockResolvedValueOnce([{ name: "new" }] as never);
  const state = await mount<FilesState>(FilesView);
  state.searchQuery = "old"; const pending = state.runSearch();
  state.searchQuery = "new"; await state.runSearch();
  first.resolve([{ name: "old" }] as never); await pending;
  expect(state.searchResults.map(item => item.name)).toEqual(["new"]);
});

it("does not start a second drag-and-drop upload while the first is running", async () => {
  const delayed = deferred<{ path: string }>();
  vi.mocked(uploadFile).mockReturnValue(delayed.promise);
  const state = await mount<FilesState>(FilesView);
  const files = [{ name: "data.txt" }] as File[];
  const pending = state.uploadMany(files, []);
  await state.uploadMany(files, []);
  expect(uploadFile).toHaveBeenCalledOnce();
  delayed.resolve({ path: "sites/a.test/data.txt" }); await pending;
});

type BackupState = {
  browseRecord: BackupRecord | null; browseEntries: { name: string }[]; browseLoading: boolean;
  openBrowser(record: BackupRecord): Promise<void>; closeBrowser(): void;
  selectedItems: string[]; itemsTargetPath: string; restoreSelectedItems(): Promise<void>;
  noticeError: boolean; notice: string;
};

it("does not repeat destructive restore operations after a lost batch response", async () => {
  const state = await mount<BackupState>(BackupView);
  state.browseRecord = { id: "a", name: "backup-a" } as BackupRecord;
  state.selectedItems = ["first.txt", "second.txt"];
  vi.mocked(restoreBackupItems).mockRejectedValue(new Error("connection lost"));
  await state.restoreSelectedItems();
  expect(restoreBackupItems).toHaveBeenCalledOnce();
  expect(state.noticeError).toBe(true);
  expect(state.selectedItems).toEqual(["first.txt", "second.txt"]);
});

it("restores only to the target shown in the confirmation", async () => {
  const state = await mount<BackupState>(BackupView);
  state.browseRecord = { id: "a", name: "backup-a" } as BackupRecord;
  state.selectedItems = ["first.txt"]; state.itemsTargetPath = "original-target";
  confirmMock.mockImplementationOnce(async options => { state.itemsTargetPath = "changed-target"; await options.action(); return true; });
  vi.mocked(restoreBackupItems).mockResolvedValue({ restoredCount: 1, targetRoot: "original-target" } as never);
  await state.restoreSelectedItems();
  expect(restoreBackupItems).toHaveBeenCalledWith("a", ["first.txt"], "original-target", "");
});

it("does not start a restore confirmed after its backup browser was closed", async () => {
  const state = await mount<BackupState>(BackupView);
  state.browseRecord = { id: "a", name: "backup-a" } as BackupRecord;
  state.selectedItems = ["first.txt"];
  confirmMock.mockImplementationOnce(async options => { state.closeBrowser(); await options.action(); return true; });
  await state.restoreSelectedItems();
  expect(restoreBackupItems).not.toHaveBeenCalled();
});
it("does not show entries from a closed backup under a newly opened backup", async () => {
  const first = deferred<Awaited<ReturnType<typeof getBackupEntries>>>();
  const second = deferred<Awaited<ReturnType<typeof getBackupEntries>>>();
  vi.mocked(getBackupEntries).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const state = await mount<BackupState>(BackupView);
  const a = state.openBrowser({ id: "a" } as BackupRecord);
  state.closeBrowser();
  const b = state.openBrowser({ id: "b" } as BackupRecord);
  first.resolve({ prefix: "", entries: [{ name: "wrong-backup" }], truncated: false } as never); await a;
  expect(state.browseLoading).toBe(true);
  expect(state.browseEntries).toEqual([]);
  second.resolve({ prefix: "", entries: [{ name: "correct-backup" }], truncated: false } as never); await b;
  expect(state.browseRecord?.id).toBe("b");
  expect(state.browseEntries.map(entry => entry.name)).toEqual(["correct-backup"]);
});
