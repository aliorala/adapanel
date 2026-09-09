import { createApp } from "vue";
import { createPinia } from "pinia";
import { i18n, setPanelLocale } from "./i18n";
import router from "./router";
import App from "./App.vue";
import "@fontsource-variable/open-sans";
import "./styles/base.css";

// Kayıtlı temayı uygulama monte edilmeden önce uygula (login ekranı dahil, açılışta tema sıçraması olmaz).
// Varsayılan AÇIK tema (profesyonel panel benzeri hosting paneli görünümü); kullanıcı koyuya geçebilir.
try {
  const savedTheme = localStorage.getItem("adapanel-theme");
  document.documentElement.dataset.theme = savedTheme === "dark" ? "dark" : "light";
} catch {
  document.documentElement.dataset.theme = "light";
}

const app = createApp(App);

app.config.errorHandler = (error, _instance, info) => {
  console.error("Vue render error", info, error);
  document.body.dataset.vueError = `${info}: ${String(error)}`;
};

async function mount() {
  let language = "tr";
  try { language = localStorage.getItem("adapanel-language") || "tr"; } catch { /* private browsing */ }
  await setPanelLocale(language);
  app.use(createPinia()).use(i18n).use(router);
  await router.isReady();
  app.mount("#app");
}
void mount().catch((error) => {
  console.error("Panel startup failed", error);
  const root = document.getElementById("app");
  if (root) {
    root.textContent = "Panel yüklenemedi / Unable to load panel. ";
    const retry = document.createElement("button");
    retry.textContent = "Yeniden dene / Retry";
    retry.onclick = () => window.location.reload();
    root.append(retry);
  }
});
