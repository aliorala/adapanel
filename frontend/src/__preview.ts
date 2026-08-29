import { createApp, h } from "vue";
import { createPinia } from "pinia";
import { createI18n } from "vue-i18n";
import en from "./i18n/en.json";
import tr from "./i18n/tr.json";
import "@fontsource-variable/open-sans";
import "./styles/base.css";
import DashboardOverviewView from "./views/DashboardOverviewView.vue";

document.documentElement.dataset.theme =
  new URLSearchParams(location.search).get("theme") === "dark" ? "dark" : "light";

const i18n = createI18n({ legacy: false, locale: "tr", fallbackLocale: "en", messages: { tr, en } });

const overview = {
  machineName: "ADA-SRV-01",
  operatingSystem: "Microsoft Windows Server 2022 Standard",
  osArchitecture: "X64",
  frameworkDescription: ".NET 8.0.11",
  panelVersion: "0.7.10",
  currentServerTimeUtc: "2026-08-29T13:02:00Z",
  bootTimeUtc: "2026-08-01T04:11:00Z",
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
    h(DashboardOverviewView as never, {
      overview,
      loading: false,
      sitesCount: 12,
      totalBindingCount: 19,
      ftpUserCount: 4,
      databaseCount: 7,
      securityStatusLabel: "Etkin",
      securePath: "panel",
      auditItems: [
        { id: 1, action: "site.create", details: "example.com olusturuldu", userName: "admin", ipAddress: "10.0.0.4", createdAtUtc: "2026-08-29T12:40:00Z" },
        { id: 2, action: "ssl.renew", details: "sertifika yenilendi", userName: "admin", ipAddress: "10.0.0.4", createdAtUtc: "2026-08-29T11:02:00Z" }
      ],
      allowedSections: ["sites", "mail", "database", "files", "tools", "security"],
      panelUpdate: null,
      attention: { criticalCount: 1, warningCount: 2, totalCount: 3, items: [] }
    })
})
  .use(createPinia())
  .use(i18n)
  .mount("#app");
