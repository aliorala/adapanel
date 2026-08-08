import { createApp } from "vue";
import { createPinia } from "pinia";
import { createI18n, type MessageFunction } from "vue-i18n";
import App from "./App.vue";
import en from "./i18n/en.json";
import tr from "./i18n/tr.json";
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

// vue-i18n'in varsayılan mesaj derleyicisi '@' (linked-message), '|' (çoğul) ve
// '{...}' karakterlerini özel söz dizimi olarak yorumlar; bir çeviri DEĞERİ ya da
// (daha tehlikelisi) t()'ye verilen sunucu kaynaklı ham bir hata metni bunlardan
// birini içerirse derleyici SyntaxError fırlatıp tüm bileşeni çökertir → boş sayfa.
// Panel yalnızca isimli/sıralı yer tutucu ({name}, {0}) ve {'@'} literal kaçışını
// kullandığından, bu davranışların hiçbirine ihtiyacımız yok. Aşağıdaki güvenli
// derleyici SADECE bu yer tutucuları işler, geri kalan her şeyi düz metin bırakır;
// böylece hiçbir çeviri ya da hata metni paneli ASLA çökertemez.
function safeMessageCompiler(message: unknown): MessageFunction<string> {
  const source = typeof message === "string" ? message : String(message ?? "");
  const fn = ((ctx: { named?: (k: string) => unknown; list?: (i: number) => unknown }) =>
    source.replace(/\{([^{}]*)\}/g, (whole, rawToken: string) => {
      const token = rawToken.trim();
      const literal = token.match(/^'([\s\S]*)'$/) ?? token.match(/^"([\s\S]*)"$/);
      if (literal) return literal[1];
      const named = ctx.named?.(token);
      if (named !== undefined && named !== null) return String(named);
      if (/^\d+$/.test(token)) {
        const item = ctx.list?.(Number(token));
        if (item !== undefined && item !== null) return String(item);
      }
      return whole; // bilinmeyen yer tutucu: olduğu gibi bırak (varsayılan davranış)
    })) as MessageFunction<string>;
  (fn as unknown as { source: string }).source = source;
  return fn;
}

const i18n = createI18n({
  legacy: false,
  locale: "tr",
  fallbackLocale: "en",
  // flatJson KAPALI — bilinçli.
  //
  // tr.json/en.json ZATEN DÜZ: anahtarlar noktalı dizgeler, iç içe nesne yok. `flatJson: true`
  // bu düz anahtarları iç içe bir ağaca AÇMAYA çalışır; bir anahtar hem yaprak hem önek olduğunda
  // (ör. "attention.update.panel" + "attention.update.panel.desc") ağaç kurulamaz ve vue-i18n
  // her yüklemede "[intlify] Ignore object flatten" uyarısı basar. Bugün 12 uyarı vardı ve yeni
  // bir ".desc" kardeşi eklendiğinde sınıf büyüyordu.
  //
  // ÖLÇÜLDÜ (3469 anahtar × 2 dil, iki mod karşılaştırıldı): flatJson kapalıyken çözümlenen
  // değerler BİREBİR aynı; uyarı sayısı 12 → 0. Anahtarları yeniden adlandırmak gerekmiyor —
  // zaten adlar backend'den (AttentionService TitleKey/DescriptionKey) geliyor ve orayı
  // değiştirmeden yeniden adlandırma çeviriyi kırardı.
  messageCompiler: safeMessageCompiler,
  messages: { tr, en }
});

const app = createApp(App);

app.config.errorHandler = (error, _instance, info) => {
  console.error("Vue render error", info, error);
  document.body.dataset.vueError = `${info}: ${String(error)}`;
};

app
  .use(createPinia())
  .use(i18n)
  .mount("#app");
