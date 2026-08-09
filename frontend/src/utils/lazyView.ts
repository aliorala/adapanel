import { defineAsyncComponent, type Component } from "vue";

import ViewLoadFailed from "../components/ViewLoadFailed.vue";
import ViewLoading from "../components/ViewLoading.vue";

/**
 * KOD BÖLME — TEK KAYNAK.
 *
 * NEDEN: Panel kabuğu (SetupWizardView) ve alan adı çalışma alanı (WebsiteWorkspaceView) ~40
 * bölüm görünümünü STATİK import ediyordu; hepsi tek pakete giriyor ve giriş ekranını görmek
 * için bile 1,1 MB indiriliyordu. Bölümler artık yalnız AÇILDIĞINDA indirilir.
 *
 * Ölü ekran YOK: indirme sürerken `ViewLoading`, indirme başarısızsa (güncelleme sonrası eski
 * hash) `ViewLoadFailed` çizilir. "Yükleniyor", "okunamadı" ve "veri yok" birbirinden ayrıdır.
 *
 * Kullanım:  const MailView = lazyView(() => import("../views/MailView.vue"));
 */
export function lazyView(loader: () => Promise<{ default: Component }>) {
  return defineAsyncComponent({
    loader,
    loadingComponent: ViewLoading,
    errorComponent: ViewLoadFailed,
    // Hızlı ağda gereksiz bir "Yükleniyor…" titremesi olmasın.
    delay: 150,
    timeout: 30000
  });
}
