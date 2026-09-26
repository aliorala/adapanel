import { createApp, h } from "vue";
import { createPinia } from "pinia";
import { createI18n } from "vue-i18n";
import { safeMessageCompiler } from "./i18n";
import { setFormattingLocale } from "./utils/locale";
import en from "./i18n/en.json";
import tr from "./i18n/tr.json";
import "@fontsource-variable/open-sans";
import "./styles/base.css";
import MainLayout from "./layouts/MainLayout.vue";
import { apiClient } from "./api/client";
apiClient.defaults.adapter = async config => ({data:{success:true,data:[],message:"",code:0},status:200,statusText:"OK",headers:{},config});
import DashboardOverviewView from "./views/DashboardOverviewView.vue";

document.documentElement.dataset.theme =
  new URLSearchParams(location.search).get("theme") === "dark" ? "dark" : "light";

const language = new URLSearchParams(location.search).get("lang") === "en" ? "en" : "tr";
const translations = language === "en" ? en : tr;
setFormattingLocale(language);
document.documentElement.lang = language;
const i18n = createI18n({ legacy: false, locale: language, fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: { tr, en } });

const overview = {
  machineName: "ADA-SRV-01",
  operatingSystem: "Microsoft Windows Server 2022 Standard",
  osArchitecture: "X64",
  frameworkDescription: ".NET 8.0.20",
  panelVersion: "0.8.66",
  currentServerTimeUtc: "2026-09-26T13:02:00Z",
  bootTimeUtc: "2026-08-29T04:11:00Z",
  uptime: "28.08:51:00",
  cpuUsagePercent: 12.4,
  logicalProcessorCount: 8,
  totalMemoryBytes: 17179869184,
  usedMemoryBytes: 14500000000,
  memoryUsagePercent: 84.4,
  disks: [
    { name: "C:\\", usagePercent: 99.9, freeBytes: 558891008, usedBytes: 128000000000, totalBytes: 128558891008 },
    { name: "D:\\", usagePercent: 41.2, freeBytes: 300000000000, usedBytes: 210000000000, totalBytes: 510000000000 },
    { name: "E:\\", usagePercent: 82.5, freeBytes: 40000000000, usedBytes: 188000000000, totalBytes: 228000000000 }
  ],
  networkInterfaces: [
    { name: "Ethernet", bytesReceivedPerSecond: 1258291, bytesSentPerSecond: 409600 },
    { name: "Loopback", bytesReceivedPerSecond: 0, bytesSentPerSecond: 0 }
  ]
};

createApp({
  render: () =>
    h(MainLayout as never, {sections: ["home","sites","sitebuilder","mail","database","accounts","billing","monitor","advisor","seo","audit","tasks","software","security","tools","migration","cluster","ai","account","settings"].map(id=>({id,label:(translations as Record<string,string>)[({sitebuilder:"panel.navigation.siteBuilder",software:"panel.navigation.store",tools:"panel.navigation.serverTools",ai:"panel.navigation.aiConnection",account:"panel.account.title"} as Record<string,string>)[id] || `panel.navigation.${id}`] || id,description:"",glyph:""})), activeSection:"home", title:translations["panel.navigation.home"], description:"", subtitle:"", panelName:"AdaPanel", machineName:overview.machineName,secureEntryPath:"panel",userName:"admin",userEmail:"demo@example.com",role:"Administrator",panelVersion:"0.8.66",isTwoFactorEnabled:false,resolvedTheme:document.documentElement.dataset.theme}, () => h(DashboardOverviewView as never, {
      overview,
      loading: false,
      sitesCount: 12,
      totalBindingCount: 19,
      ftpUserCount: 4,
      databaseCount: 7,
      securityStatusLabel: translations["panel.account.enabled"],
      securePath: "panel",
      auditItems: [
        { id: 1, action: "sites.create.completed", details: "example.com oluşturuldu", userName: "admin", ipAddress: "10.0.0.4", createdAtUtc: "2026-09-26T12:40:00Z" },
        { id: 2, action: "terminal.command.executed", details: "Sunucu servislerinin durumu okundu. ".repeat(100), userName: "admin", ipAddress: "10.0.0.4", createdAtUtc: "2026-09-26T11:02:00Z" }
      ],
      allowedSections: ["sites", "mail", "database", "files", "tools", "security", "audit"],
      panelUpdate: null,
      attention: { criticalCount: 1, warningCount: 2, totalCount: 3, items: [], failedSources: new URLSearchParams(location.search).has("failed") ? ["components", "ssl"] : [] }
    }))
})
  .use(createPinia())
  .use(i18n)
  .mount("#app");
