import { defineStore } from "pinia";
import { getPhpVersions, installPhpVersion, setSitePhpVersion, type PhpVersionRecord, type SitePhpAssignment } from "../api/php";

export const usePhpStore = defineStore("php", {
  state: () => ({
    versions: [] as PhpVersionRecord[],
    lastAssignment: null as SitePhpAssignment | null,
    loading: false,
    /*
     * Sürüm listesi BİR KEZ başarıyla okundu mu. `loading` hata durumunda da false döner ve boş
     * `versions` "kurulu PHP sürümü yok" diye okunurdu. Bayrak yalnız BAŞARIDA true olur;
     * mağazada durur çünkü PHP/ASP/Kurulum sihirbazı aynı listeyi okuyor.
     */
    loaded: false,
    installingComponentId: "",
    assigningComponentId: ""
  }),
  actions: {
    async fetchVersions() {
      this.loading = true;

      try {
        this.versions = await getPhpVersions();
        // Yalnız BAŞARIDA: hata fırlarsa bayrak false kalır, ekran "yükleniyor" der.
        this.loaded = true;
        return this.versions;
      } finally {
        this.loading = false;
      }
    },
    async installVersion(componentId: string) {
      this.installingComponentId = componentId;

      try {
        const message = await installPhpVersion(componentId);
        await this.fetchVersions();
        return message;
      } finally {
        this.installingComponentId = "";
      }
    },
    async assignVersion(siteName: string, componentId: string) {
      this.assigningComponentId = componentId;

      try {
        const assignment = await setSitePhpVersion(siteName, componentId);
        this.lastAssignment = assignment;
        return assignment;
      } finally {
        this.assigningComponentId = "";
      }
    }
  }
});
