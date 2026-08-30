<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.tasks.title") }}</h2>
        <p>{{ t("panel.tasks.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ t("panel.tasks.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div class="module-toolbar">
      <div class="toolbar-group">
        <!-- EKRANDA TEK BİRİNCİL EYLEM. Form kapalıyken bu düğme sayfanın birincil eylemidir
             ("Ekle"). Form AÇILINCA aynı düğme "Formu kapat"a dönüşüyor ama görsel olarak
             birincil kalıyordu: aşağıdaki "Kaydet" de birincil olduğu için ekranda iki mavi
             düğme yarışıyor ve gözün gideceği yer belirsizleşiyordu. Üstelik ikisinden görsel
             olarak baskın olan, işi GERİ ALAN düğmeydi. Kapatma bir vazgeçmedir: sakinleşir. -->
        <button :class="{ secondary: formOpen }" v-bind="writeGate()" @click="toggleForm">
          {{ formOpen ? t("panel.tasks.closeForm") : t("panel.tasks.add") }}
        </button>
        <!-- ROZET YÜKLENİRKEN YALAN SÖYLÜYORDU. Liste "Yükleniyor…" derken bu rozet zaten
             YEŞİL "0 görev" yazıyordu: iki bileşen aynı anda iki farklı şey iddia ediyordu.
             İki düzeltme: (1) sayı ancak liste BİR KEZ okunduktan sonra yazılır, öncesinde
             rozet ölçümün sürdüğünü söyler; (2) ton `ok` (yeşil) DEĞİL nötr — bir SAYI bir
             sağlık durumu değildir, "0 görev" başarı değil sadece bir ölçümdür. -->
        <span v-if="tasksLoaded" class="status-pill">{{ tasks.length }} {{ t("panel.tasks.taskCount") }}</span>
        <span v-else class="status-pill" role="status" aria-live="polite">{{ t("panel.common.loading") }}</span>
      </div>
      <input v-model.trim="query" class="search-control" :placeholder="t('panel.tasks.search')" />
    </div>

    <section v-if="formOpen" class="form-surface">
      <!-- Hangi gorevi duzenledigimiz yazili olsun: form sayfanin tepesinde, gorev satiri asagida
           kaliyor; baslik olmadan kullanici hangi gorevi degistirdigini kaybediyor. -->
      <p v-if="editingName" class="editing-banner">{{ t("panel.tasks.editing", { name: editingName }) }}</p>
      <div class="form-grid">
        <label><span class="field-label">{{ t("panel.tasks.name") }}</span><input v-model.trim="form.name" /></label>
        <label v-if="isAdmin">
          <span class="field-label">{{ t("panel.tasks.taskType") }}</span>
          <select v-model="form.taskType">
            <option value="command">{{ t("panel.tasks.typeCommand") }}</option>
            <option value="php">{{ t("panel.tasks.typePhp") }}</option>
            <option value="script">{{ t("panel.tasks.typeScript") }}</option>
            <option value="url">{{ t("panel.tasks.typeUrl") }}</option>
          </select>
        </label>
        <label v-if="form.taskType === 'url'" class="span-2">
          <span class="field-label">{{ t("panel.tasks.url") }}</span>
          <input v-model.trim="form.url" :placeholder="t('panel.tasks.urlPlaceholder')" spellcheck="false" />
          <small class="field-hint">{{ t("panel.tasks.urlHint") }}</small>
        </label>

        <!-- Betik görevi yalnız yöneticide: sunucu betiği (yol serbest) mi, site betiği (yol o sitenin kökünde) mi. -->
        <label v-if="isScriptForm">
          <span class="field-label">{{ t("panel.tasks.scriptScope") }}</span>
          <select v-model="scriptScope">
            <option value="server">{{ t("panel.tasks.scriptScopeServer") }}</option>
            <option value="site">{{ t("panel.tasks.scriptScopeSite") }}</option>
          </select>
        </label>
        <label v-if="isScriptForm && scriptScope === 'site'">
          <span class="field-label">{{ t("panel.tasks.scriptSite") }}</span>
          <select v-model="form.siteName">
            <option value="">{{ t("panel.tasks.scriptSitePlaceholder") }}</option>
            <option v-for="site in sites" :key="site.name" :value="site.name">{{ site.name }}</option>
          </select>
          <small v-if="selectedSiteRoot" class="field-hint">{{ t("panel.tasks.scriptRootHint", { path: selectedSiteRoot }) }}</small>
        </label>
        <!-- Kimlik seçimi yalnız site modunda anlamlı: havuz hesabı bir SİTEYE aittir. -->
        <label v-if="isScriptForm && scriptScope === 'site'">
          <span class="field-label">{{ t("panel.tasks.runAs") }}</span>
          <select v-model="form.runAs">
            <option value="system">{{ t("panel.tasks.runAsSystem") }}</option>
            <option value="limited">{{ t("panel.tasks.runAsLimited") }}</option>
          </select>
          <small class="field-hint">{{ t("panel.tasks.runAsHint") }}</small>
        </label>

        <label v-if="form.taskType !== 'url'">
          <span class="field-label">{{ commandFieldLabel }}</span>
          <input v-model.trim="form.command" :placeholder="commandFieldPlaceholder" spellcheck="false" />
          <small v-if="form.taskType === 'php'" class="field-hint">{{ t("panel.tasks.phpHint") }}</small>
          <small v-else-if="form.taskType === 'script'" class="field-hint">{{ t("panel.tasks.scriptHint") }}</small>
        </label>
        <label v-if="form.taskType !== 'url'">
          <span class="field-label">{{ t("panel.tasks.arguments") }}</span>
          <input v-model="form.arguments" />
          <small class="field-hint">{{ t("panel.tasks.argumentsHint") }}</small>
        </label>

        <!-- SYSTEM ortamı uyarısı. Kullanıcının en pahalı yanılgısı bu: komut kendi terminalinde
             çalıştığı için görevde de çalışacağını sanıyor. Oysa SYSTEM'in PATH'i ayrıdır ve
             "yalnızca benim için" kurulan araçlar (Python, node, git) ona hiç görünmez. Uyarı
             hatadan SONRA değil, komut yazılırken burada duruyor. -->
        <p v-if="form.taskType === 'command' || form.taskType === 'script'" class="span-2 field-hint env-hint">
          {{ t("panel.tasks.systemEnvHint") }}

          <!-- Ölçülmüş durum. Genel uyarının altında somut cevap: bu sunucuda Python var mı,
               SYSTEM görüyor mu, görmüyorsa tek tıkla düzeltiliyor. -->
          <template v-if="environment">
            <span v-if="environment.pythonOnPath" class="env-ok">
              {{ t("panel.tasks.pythonOnPath", { path: environment.pythonOnPath }) }}
            </span>
            <span v-else-if="environment.canRepairPython" class="env-fix">
              <strong>{{ t("panel.tasks.pythonFoundTitle") }}</strong>
              {{ t("panel.tasks.pythonFoundBody", { path: environment.pythonInterpreters[0] }) }}
              <button v-if="isAdmin" class="secondary compact" v-bind="writeGate(fixingPython)" @click="fixPython">
                {{ t("panel.tasks.pythonFix") }}
              </button>
            </span>
          </template>
        </p>
        <SectionLoadError
          v-if="(form.taskType === 'command' || form.taskType === 'script') && environmentLoad.message.value"
          class="span-2"
          :message="environmentLoad.message.value"
          @retry="loadEnvironment" />
        <!-- Çalışma klasörü ipucu HER ZAMAN görünür. Önceden yalnızca yer tutucu metindi ve alana
             bir şey yazılınca kayboluyordu; kullanıcı "buraya ne yazacağım" diye takılıp kaldı.
             Doğru cevap "boş bırakın" olduğu için bunun okunması gerekiyor. -->
        <label v-if="form.taskType === 'command' || form.taskType === 'script'">
          <span class="field-label">{{ t("panel.tasks.workingDirectory") }}</span>
          <input v-model.trim="form.workingDirectory" />
          <small class="field-hint">{{ t("panel.tasks.workingDirectoryHint") }}</small>
        </label>
        <label>
          <span class="field-label">{{ t("panel.tasks.scheduleType") }}</span>
          <select v-model="form.scheduleType">
            <option value="hourly">{{ t("panel.tasks.hourly") }}</option>
            <option value="daily">{{ t("panel.tasks.daily") }}</option>
            <option value="minute">{{ t("panel.tasks.minutely") }}</option>
          </select>
        </label>
        <label>
          <span class="field-label">{{ t("panel.tasks.interval") }}</span>
          <input v-model.number="form.interval" type="number" min="1" />
          <small class="field-hint">{{ t("panel.tasks.intervalHint") }}</small>
        </label>
        <label v-if="form.scheduleType === 'daily'">
          <span class="field-label">{{ t("panel.tasks.timeOfDay") }}</span>
          <input v-model="form.timeOfDay" type="time" />
        </label>
        <!-- Süre sınırı ARTIK kullanıcının kararı. Sabit 4 saat, uzun süren meşru görevleri
             her seferinde ortasında öldürüyordu. -->
        <label class="span-2">
          <span class="field-label">{{ t("panel.tasks.timeLimit") }}</span>
          <select v-model.number="form.timeLimitHours">
            <option :value="0">{{ t("panel.tasks.timeLimitNone") }}</option>
            <option v-for="saat in [1, 4, 12, 24, 48]" :key="saat" :value="saat">
              {{ t("panel.tasks.timeLimitHours", { hours: saat }) }}
            </option>
          </select>
          <small class="field-hint">{{ t("panel.tasks.timeLimitHint") }}</small>
        </label>
      </div>
      <div class="row-actions">
        <button v-bind="writeGate(saving || !form.name || !formComplete)" @click="saveTask">
          {{ saving ? t("panel.tasks.saving") : t(editingName ? "panel.tasks.update" : "panel.tasks.save") }}
        </button>
        <button class="secondary compact" @click="editingName ? cancelEdit() : resetForm()">
          {{ t(editingName ? "panel.tasks.cancelEdit" : "panel.tasks.reset") }}
        </button>
      </div>
    </section>

    <!-- SÜREN çalışmalar tablonun üstünde: uyarı olarak değil, DURUM olarak.
         Uzun sürmek kendi başına arıza değildir — kullanıcının botu günlerce sürebilir ve bu
         normaldir. Panelin borcu "bir şeyler çalışıyor mu, ne kadardır" sorusuna cevap vermek;
         "takıldı" demek ise ancak ÖLÇÜLEBİLİR bir sınır (kullanıcının kendi verdiği süre sınırı)
         aşıldığında dürüsttür. Sınır yoksa panel teşhis koymaz, yalnızca bilgi verir. -->
    <section v-if="runningTasks.length" :class="['running-notice', { alarm: overLimitTasks.length > 0 }]">
      <strong>{{ t(overLimitTasks.length ? "panel.tasks.overLimitTitle" : "panel.tasks.runningTitle") }}</strong>
      <p>{{ t(overLimitTasks.length ? "panel.tasks.overLimitBody" : "panel.tasks.runningBody") }}</p>
      <ul>
        <li v-for="item in runningTasks" :key="item.name">
          <span>
            <strong>{{ item.name }}</strong>
            <small>{{ runningForLabel(item) }}</small>
          </span>
          <button class="secondary compact" v-bind="writeGate(stoppingName === item.name)" @click="stopRun(item)">
            {{ stoppingName === item.name ? t("panel.tasks.stopping") : t("panel.tasks.stopRunning") }}
          </button>
        </li>
      </ul>
    </section>

    <div class="data-table">
      <!-- BEŞ sütun. Önceden sekiz vardı ve her biri ekranda ~100 piksele düşüyordu: komut yolu,
           tarihler ve dört düğme aynı satırda birbirinin üstüne biniyordu. Birbiriyle ilgili
           alanlar tek hücrede toplandı — "sonraki çalışma" zamanlamanın, "son çalışma" ve "son
           sonuç" durumun parçasıdır; ayrı sütun olmaları bilgiyi çoğaltmıyor, sadece daraltıyordu. -->
      <div class="data-row table-head cron-row">
        <span>{{ t("panel.tasks.name") }}</span>
        <span>{{ t("panel.tasks.command") }}</span>
        <span>{{ t("panel.tasks.scheduleType") }}</span>
        <span>{{ t("panel.sites.state") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>
      <template v-for="item in filteredTasks" :key="item.name">
      <!-- BİR GÖREV = BİR GRUP. Ayırıcı çizgi eskiden satırın kendisine çiziliyordu; görevin
           açıklaması ("Şu anda çalışıyor — ... önce başladı") o çizginin ALTINDA kalıyor ve bir
           alttaki göreve aitmiş gibi okunuyordu. Artık çizgi grubun sonunda: açıklama, ait
           olduğu satırla aynı bandın içinde. -->
      <div class="cron-item">
      <div :class="['data-row', 'cron-row', { 'row-disabled': isDisabled(item), 'row-running': isRunning(item) }]">
        <span class="cell-main">
          <strong>{{ item.name }}</strong>
          <small>{{ typeLabel(item.taskType) }}</small>
        </span>
        <!-- Komut tek parça uzun bir yol olur; okunması için tek aralıklı yazı tipi, taşmaması
             için hücre içinde kırılıyor (.data-row > * kuralı). -->
        <span class="cell-main cell-command">
          <strong>{{ item.taskType === "url" ? item.url || item.arguments : item.command }}</strong>
          <small>{{ item.taskType === "url" ? "-" : item.arguments || "-" }}</small>
        </span>

        <!-- Zamanlama + SONRAKİ çalışma birlikte: ikisi aynı sorunun parçası ("bu ne zaman
             çalışacak"), ayrı sütunlarda dururken kullanıcı gözünü ileri geri gezdiriyordu. -->
        <span class="cell-main">
          <strong>{{ scheduleLabel(item.scheduleType) }}</strong>
          <!-- DEVRE DIŞI görevde tarih GÖSTERİLMEZ. Windows devre dışı görevler için de bir
               sonraki zamanı hesaplar; onu olduğu gibi yazmak, hiç çalışmayacak bir görev için
               kullanıcıya randevu vermektir. Boş bırakmak da "sorun yok" demek değildir: sebebi
               yazılır. -->
          <small v-if="isDisabled(item)" class="cell-flag" :title="t('panel.tasks.disabledNextRunHint')">
            {{ t("panel.tasks.disabledNextRun") }}
          </small>
          <small v-else-if="item.nextRunTimeUtc">
            {{ t("panel.tasks.nextRun") }}: {{ formatUtc(item.nextRunTimeUtc) }}
          </small>
          <small v-else class="cell-flag" :title="t('panel.tasks.noNextRunHint')">
            {{ t("panel.tasks.noNextRun") }}
          </small>
        </span>

        <!-- Durum + son sonuç + son çalışma birlikte: "en son ne oldu" sorusunun tamamı. -->
        <span class="cell-status">
          <!-- DURUM HÜCRESİ YALAN RENK VERİYORDU.
               ÖLÇÜLDÜ: "Çalışma klasörü geçersiz" hatasıyla düşmüş bir görevde hücrenin İLK
               rozeti YEŞİL "Hazır"dı; kehribar hata rozeti onun altındaydı. Bakışta görev
               sağlıklı okunuyordu — oysa Windows'un "Ready"si "bir sonraki tur için sıraya
               alındı" demektir, "son tur iyi gitti" demek DEĞİL.
               İki kural kondu (bkz. `statusPills`): (1) son sonuç bir arıza ise ARIZA ÖNCE
               gelir; (2) aynı durumda "Hazır" rozeti yeşil olmaktan çıkar, nötre döner.
               Rozetler hâlâ SÖZCÜK taşıyor, yani renk tek kanal değil. -->
          <span class="pill-row">
            <!-- Kodun ne demek olduğu rozette yazılı; "neden ve ne yapmalı" ipucu üzerine gelince. -->
            <span
              v-for="(rozet, sira) in statusPills(item)"
              :key="sira"
              :class="['status-pill', rozet.tone]"
              :title="rozet.title || undefined">{{ rozet.text }}</span>
          </span>
          <small>{{ t("panel.tasks.lastRun") }}: {{ formatUtc(item.lastRunTimeUtc) }}</small>
        </span>

        <span class="row-actions">
          <!-- ÇALIŞAN görevde birincil eylem "durdur"dur. Önceden en görünür düğme "Devre dışı
               bırak"tı ve kullanıcı süren işi durdurmak için ona basıyordu; oysa o düğme çalışan
               turu DURDURMAZ, yalnızca gelecek turları engeller. -->
          <button v-if="isRunning(item)" class="secondary compact" v-bind="writeGate(stoppingName === item.name)" @click="stopRun(item)">
            {{ stoppingName === item.name ? t("panel.tasks.stopping") : t("panel.tasks.stopRunning") }}
          </button>
          <button v-else class="secondary compact" v-bind="writeGate(runningName === item.name)" @click="runTask(item.name)">
            {{ runningName === item.name ? t("panel.tasks.running") : t("panel.tasks.runNow") }}
          </button>
          <button class="secondary compact" @click="editTask(item)">{{ t("panel.tasks.edit") }}</button>
          <button
            class="secondary compact"
            v-bind="writeGate(togglingName === item.name, isRunning(item) ? t('panel.tasks.disableHintWhileRunning') : undefined)"
            @click="toggleTask(item)"
          >
            {{ isDisabled(item) ? t("panel.tasks.enableTask") : t("panel.tasks.disableTask") }}
          </button>
          <button class="danger compact" v-bind="writeGate(deletingName === item.name)" @click="removeTask(item.name)">
            {{ deletingName === item.name ? t("panel.database.removing") : t("panel.sites.delete") }}
          </button>
        </span>
      </div>

      <!-- SEBEP TEK CÜMLEDE. "Son sonuç" ve "Son çalışma" ayrı sütunlardı; kullanıcı ikisini
           birleştirip "neden çalışmadı"yı kendi çıkarmak zorundaydı. Panelin işi veriyi
           göstermek değil, soruyu cevaplamak. -->
      <div v-if="explain(item)" :class="['row-explain', explainClass(item)]">
        <span>{{ explain(item) }}</span>
        <!-- Hata koduyla biten görevde tek anlamlı sonraki adım çıktıya bakmaktır; kullanıcı onu
             aramak zorunda kalmasın. -->
        <button
          v-if="canShowOutput(item)"
          class="ghost compact"
          @click="showOutput(item.name)"
        >
          {{ t("panel.tasks.outputShow") }}
        </button>
      </div>

      <!-- Canlı bilgi, görevin KENDİ satırının hemen altında: kullanıcı gözünü tablonun başka bir
           yerine kaydırmadan ne olduğunu görür. -->
      <div v-if="runStates[item.name]" :class="['run-live', runClass(item.name)]">
        <span class="run-dot" :class="{ spinning: runStates[item.name].phase === 'running' || runStates[item.name].phase === 'starting' }" aria-hidden="true"></span>
        <span class="run-text">
          {{ runMessage(item.name) }}

          <!-- Çıktı, sonucun HEMEN ALTINDA: "başarısız" demek yetmez, kullanıcının görmesi
               gereken şey komutun ne yazdığıdır. -->
          <template v-if="runStates[item.name].phase === 'done'">
            <small v-if="outputLoading[item.name]">{{ t("panel.tasks.outputLoading") }}</small>
            <template v-else-if="outputs[item.name]">
              <small v-if="!outputs[item.name].available">{{ t("panel.tasks.outputNone") }}</small>
              <small v-else-if="!outputs[item.name].content">{{ t("panel.tasks.outputEmpty") }}</small>
              <template v-else>
                <small>{{ t("panel.tasks.outputTitle") }}</small>
                <small v-if="outputs[item.name].truncated">{{ t("panel.tasks.outputTruncated") }}</small>
                <pre class="run-output">{{ outputs[item.name].content }}</pre>
              </template>
            </template>
          </template>
        </span>
        <button class="ghost compact" @click="closeRunPanel(item.name)">{{ t("panel.tasks.runClose") }}</button>
      </div>

      <!-- Geçmiş çalışmanın çıktısı: "hata koduyla bitti" dedikten sonra kullanıcının bakacağı
           tek yer burasıdır ve aynı satırda açılır. -->
      <div v-else-if="openOutputs[item.name]" class="run-live">
        <span class="run-dot" aria-hidden="true"></span>
        <span class="run-text">
          {{ t("panel.tasks.outputTitle") }}
          <small v-if="outputLoading[item.name]">{{ t("panel.tasks.outputLoading") }}</small>
          <template v-else-if="outputs[item.name]">
            <small v-if="!outputs[item.name].available">{{ t("panel.tasks.outputNone") }}</small>
            <small v-else-if="!outputs[item.name].content">{{ t("panel.tasks.outputEmpty") }}</small>
            <template v-else>
              <small v-if="outputs[item.name].truncated">{{ t("panel.tasks.outputTruncated") }}</small>
              <pre class="run-output">{{ outputs[item.name].content }}</pre>
            </template>
          </template>
          <!-- Okunamadı ile "çıktı yok" AYRI şeylerdir; boş bir kutu göstermek ikincisini ima ederdi. -->
          <small v-else>{{ t("panel.tasks.outputUnreadable") }}</small>
        </span>
        <button class="ghost compact" @click="closeOutput(item.name)">{{ t("panel.tasks.runClose") }}</button>
      </div>
      </div>
      </template>
      <p v-if="!tasksLoaded" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      <p v-else-if="filteredTasks.length === 0" class="empty-state">{{ t("panel.tasks.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch, onUnmounted } from "vue";
import { useI18n } from "vue-i18n";
import { createScheduledTask, deleteScheduledTask, getScheduledTasks, addPythonToMachinePath, getScheduledTaskOutput, getScheduledTaskStatus, getTaskEnvironment, runScheduledTask, setScheduledTaskEnabled, stopScheduledTask, type ScheduledTask, type ScheduledTaskOutput, type TaskEnvironment } from "../api/cron";
import { getSites, type SiteRecord } from "../api/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useSectionError } from "../composables/useNotice";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { formatDateTime } from "../utils/datetime";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const { confirm } = useConfirm();
const authStore = useAuthStore();
// Müşteri/bayi yalnızca URL görevi kurar (backend de zorlar); tip seçici yalnız admin'de görünür.
// "script" tipi de bilinçli olarak yalnız yöneticide: planlı betik = sunucuda kod çalıştırmak.
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
// Betik görevinin site/sunucu ayrımı yalnız formda yaşar; sunucuya "siteName dolu mu" olarak gider.
const scriptScope = ref<"server" | "site">("server");
const sites = ref<SiteRecord[]>([]);
const tasks = ref<ScheduledTask[]>([]);
// Görev listesi BİR KEZ başarıyla okundu mu. `loading` hata durumunda da false olur; boş dizi
// "zamanlanmış görev yok" gibi KESİN bir cümleye dönüşüyordu (bkz. SiteBackupView `listLoaded`).
const tasksLoaded = ref(false);
const query = ref("");
const formOpen = ref(false);
const loading = ref(false);
const saving = ref(false);
const deletingName = ref("");
const runningName = ref("");
const togglingName = ref("");
const notice = ref("");
const noticeError = ref(false);
const form = reactive(defaultForm());
const isScriptForm = computed(() => isAdmin.value && form.taskType === "script");

/**
 * Tip değişince o tipte KULLANILMAYAN alanları temizle.
 *
 * Çalışma klasörü alanı yalnızca "komut" ve "betik" tiplerinde görünür. Kullanıcı bir .bat
 * görevini düzenlerken tipi PHP'ye çevirdiğinde alan gizleniyor ama formdaki eski değeri
 * taşımaya devam ediyordu; kayıt, kullanıcının EKRANDA GÖREMEDİĞİ bir alan yüzünden
 * reddediliyordu. Görünmeyen bir değere göre verilen hata, çözülemeyen bir hatadır.
 */
watch(() => form.taskType, (yeniTip) => {
  if (yeniTip === "url" || yeniTip === "php") {
    form.workingDirectory = "";
  }
  if (yeniTip !== "url") {
    form.url = "";
  }
});
const selectedSiteRoot = computed(() => {
  const site = sites.value.find((item) => item.name === form.siteName);
  return site ? site.applicationRootPath || site.physicalPath : "";
});
const commandFieldLabel = computed(() => {
  if (form.taskType === "php") return t("panel.tasks.scriptPath");
  if (form.taskType === "script") return t("panel.tasks.scriptFile");
  return t("panel.tasks.command");
});
const commandFieldPlaceholder = computed(() => {
  if (form.taskType === "php") return "C:\\inetpub\\wwwroot\\site\\httpdocs\\cron.php";
  // Site seçiliyse yolun o sitenin kökünden başlaması gerektiğini ipucu olarak göster.
  if (form.taskType === "script") return (selectedSiteRoot.value || "C:\\inetpub\\wwwroot\\site") + "\\cron.bat";
  return "powershell.exe";
});
const formComplete = computed(() => {
  if (form.taskType === "url") return Boolean(form.url);
  if (form.taskType === "script" && scriptScope.value === "site" && !form.siteName) return false;
  return Boolean(form.command);
});

// Tip değişince betiğe uymayan varsayılanları temizle: "powershell.exe" bir betik yolu değildir ve
// betik dışı tiplerde site/kimlik alanları gönderilmemeli (sunucu da yok sayar, ama istek temiz kalsın).
watch(() => form.taskType, (taskType) => {
  if (taskType === "script") {
    if (form.command === "powershell.exe") {
      form.command = "";
    }

    if (sites.value.length === 0 && isAdmin.value) {
      void loadSites();
    }

    return;
  }

  scriptScope.value = "server";
  form.siteName = "";
  form.runAs = "system";
});

// Sunucu betiğine dönülünce site ve havuz kimliği seçimi geçersizdir; sunucuya sızmasın.
watch(scriptScope, (scope) => {
  if (scope === "server") {
    form.siteName = "";
    form.runAs = "system";
  }
});

const filteredTasks = computed(() => {
  const needle = query.value.toLocaleLowerCase();
  return needle
    ? tasks.value.filter((item) => [item.name, item.command, item.arguments, item.state]
        .some((value) => value?.toLocaleLowerCase().includes(needle)))
    : tasks.value;
});

/**
 * Görev ortamı: sunucuda Python var mı, SYSTEM onu görüyor mu.
 *
 * Ölçüm sunucudan gelir; tahmin yok. Okunamazsa null kalır ve hiçbir şey iddia edilmez —
 * "Python yok" demek ile "ölçemedik" demek aynı şey değildir.
 */
const environment = ref<TaskEnvironment | null>(null);
// Ölçüm okunamadıysa bunu SÖYLERİZ: sessizce hiçbir şey göstermek, "ortam sorunsuz" izlenimi verirdi.
const environmentLoad = useSectionError();
const fixingPython = ref(false);

async function loadEnvironment() {
  try {
    environment.value = await getTaskEnvironment();
    environmentLoad.clear();
  } catch (error) {
    environment.value = null;
    environmentLoad.capture(error);
  }
}

async function fixPython() {
  fixingPython.value = true;
  try {
    const eklenen = await addPythonToMachinePath();
    showNotice(t(eklenen > 0 ? "panel.tasks.pythonFixed" : "panel.tasks.pythonAlready"), false);
    await loadEnvironment();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    fixingPython.value = false;
  }
}

// Arama kutusu süren çalışmayı gizlemesin: kutu TÜM görevlere bakar, süzülmüş listeye değil.
const runningTasks = computed(() => tasks.value.filter(isRunning));
// "Takıldı" DEMEK için ölçülebilir bir sınır gerekir; tek meşru sınır kullanıcının o görev için
// kendi verdiği süre sınırıdır. Sınır yoksa liste boş kalır ve panel teşhis koymaz.
const overLimitTasks = computed(() => runningTasks.value.filter(isOverTimeLimit));

/**
 * Süre hesapları için tek saat kaynağı.
 *
 * Yeniden çizim olmadan "2 gün 4 saattir sürüyor" satırı donup kalırdı; dakikada bir tazelenir.
 */
const now = ref(Date.now());
let clockTimer = 0;

onMounted(() => {
  if (!isAdmin.value) {
    form.taskType = "url";
  }
  void refresh();
  // Ortam ölçümü YALNIZ yöneticiye açık (uç de öyle): kutusu yalnız komut/betik formunda çizilir ve
  // o form kapsamlı role hiç açılmaz. Koşulsuz çağırmak, kapsamlı kullanıcıya her açılışta sessiz
  // bir 403 ürettirmek olurdu.
  if (isAdmin.value) {
    void loadEnvironment();
  }
  clockTimer = window.setInterval(() => {
    now.value = Date.now();
  }, 60_000);
});

async function refresh() {
  loading.value = true;
  try {
    tasks.value = await getScheduledTasks();
    tasksLoaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

/**
 * Düzenlenen görevin ADI. Boşsa yeni görev ekleniyordur.
 *
 * Ad ayrıca "hangi görevi değiştiriyoruz"un tek kimliğidir: Görev Zamanlayıcı'da görevler adla
 * anılır, ayrı bir kimlik yoktur.
 */
const editingName = ref("");

/**
 * Görevi düzenlemeye açar: formu görevin GERÇEK değerleriyle doldurur.
 *
 * Sunucudan okunan alanlar kullanılır, ekranda görünenler değil. Aralık, çalışma saati, PHP sürümü
 * ve kimlik tablodaki sütunlarda hiç görünmüyor; formu yalnız görünenlerle doldurmak, dokunulmayan
 * her ayarı sessizce varsayılana döndürürdü.
 */
function editTask(item: ScheduledTask) {
  editingName.value = item.name;
  Object.assign(form, {
    name: item.name,
    taskType: item.taskType,
    command: item.command,
    arguments: item.arguments,
    workingDirectory: item.workingDirectory,
    url: item.url,
    phpVersion: item.phpVersion,
    siteName: "",
    runAs: item.runAs || "system",
    scheduleType: item.scheduleType || "hourly",
    interval: item.interval || 1,
    timeOfDay: item.timeOfDay || "00:00",
    timeLimitHours: item.timeLimitHours || 0
  });

  // Betik görevinde kapsam: sınırlı kimlik yalnızca SİTE betiğinde seçilebilir, dolayısıyla
  // "limited" görmek kapsamın site olduğunu söyler.
  scriptScope.value = item.taskType === "script" && item.runAs === "limited" ? "site" : "server";

  formOpen.value = true;
  window.scrollTo({ top: 0, behavior: "smooth" });
}

/** Formu acar/kapatir. Acarken duzenleme modu BIRAKILIR: dugmenin adi "Ekle". */
function toggleForm() {
  if (formOpen.value) {
    formOpen.value = false;
    return;
  }

  if (editingName.value) {
    editingName.value = "";
    resetForm();
  }

  formOpen.value = true;
}

function cancelEdit() {
  editingName.value = "";
  resetForm();
  formOpen.value = false;
}

async function saveTask() {
  saving.value = true;
  const oncekiAd = editingName.value;
  try {
    await createScheduledTask({ ...form });

    // AD DEĞİŞTİYSE eskisi silinir. Kayıt, aynı adlı görevi yerine yazar; ad değiştiğinde ise yeni
    // bir görev oluşur ve eskisi ORTADA KALIR. Kullanıcı "adını düzelttim" der, sunucuda iki görev
    // birden çalışmaya devam ederdi.
    //
    // Sıra bilerek böyle: önce yeni kayıt BAŞARILI olur, sonra eski silinir. Tersi olsaydı kayıt
    // başarısız olduğunda kullanıcı hiç görevsiz kalırdı.
    if (oncekiAd && oncekiAd !== form.name.trim()) {
      await deleteScheduledTask(oncekiAd);
    }

    await refresh();
    editingName.value = "";
    resetForm();
    formOpen.value = false;
    showNotice(t(oncekiAd ? "panel.tasks.updated" : "panel.tasks.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    saving.value = false;
  }
}

async function removeTask(name: string) {
  const task = tasks.value.find((item) => item.name === name);
  const impact = [`${t("panel.tasks.name")}: ${name}`];
  if (task?.command) {
    impact.push(`${t("panel.tasks.command")}: ${task.command}`);
  }

  // Normal onay: tek bir zamanlanmış görev silinir, veri/dosya kaybı yok → ad yazdırmaya gerek yok.
  await confirm({
    title: t("panel.confirm.deleteTaskTitle", { name }),
    message: t("panel.tasks.confirmDelete"),
    impact,
    action: async () => {
      deletingName.value = name;
      try {
        await deleteScheduledTask(name);
        await refresh();
        showNotice(t("panel.tasks.deleted"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        deletingName.value = "";
      }
    },
  });
}

function defaultForm() {
  return {
    name: "",
    taskType: "command",
    command: "powershell.exe",
    arguments: "",
    workingDirectory: "",
    url: "",
    phpVersion: "",
    siteName: "",
    runAs: "system",
    scheduleType: "hourly",
    interval: 1,
    timeOfDay: "00:00",
    // 0 = sınırsız. Uzun süren meşru bir görevi ortasında kesmemek, takılan bir görevi
    // beklemekten daha önemli: takılanı panel görüyor ve tek düğmeyle durdurulabiliyor.
    timeLimitHours: 0
  };
}

function resetForm() {
  Object.assign(form, defaultForm());
  scriptScope.value = "server";
}

// Site listesi yalnız betik görevinin site modu için gerekir; form açılana kadar istek atma.
async function loadSites() {
  try {
    sites.value = await getSites();
  } catch {
    // Site listesi alınamazsa betik formu yine kullanılabilir (yol elle yazılır); sessiz geç.
  }
}

// Görevi hemen tetikle; birkaç saniye sonra listeyi yenile ki "son çalışma" sütunu sonucu göstersin.
/**
 * "Şimdi çalıştır" sonrası CANLI bilgi.
 *
 * Öncesinde düğmeye basmak sessizdi: görev tetikleniyor, sayfa 4 saniye sonra bir kez tazeleniyordu.
 * 10 saniye süren bir görevde kullanıcı hiçbir şey görmüyor, bittiğini de nereden anlayacağını
 * bilmiyordu. Artık görevin kendi satırının altında "çalışıyor → bitti: <sonuç>" yazıyor.
 */
type RunState = {
  phase: "starting" | "running" | "done" | "unknown";
  seconds: number;
  resultCode: number | null;
};

const runStates = reactive<Record<string, RunState>>({});
const outputs = reactive<Record<string, ScheduledTaskOutput>>({});
const outputLoading = reactive<Record<string, boolean>>({});

/**
 * Görev bittiğinde çıktısını çeker.
 *
 * Neden bitince ve tek sefer: dosyaya yalnızca çalışma boyunca yazılır ve son hâli asıl aranan
 * şeydir. Çalışırken saniyede bir okumak, yarım yazılmış satırlar gösterirdi.
 */
async function loadOutput(name: string) {
  outputLoading[name] = true;
  try {
    outputs[name] = await getScheduledTaskOutput(name);
  } catch {
    // Çıktı okunamadıysa BOŞ göstermeyiz: "çıktı yok" ile "okuyamadık" farklı şeylerdir.
    delete outputs[name];
  } finally {
    outputLoading[name] = false;
  }
}

// Aynı görev iki kez başlatılırsa ESKİ takip durmalı: yoksa iki döngü aynı satıra yazar.
const runTimers: Record<string, number> = {};

function stopWatching(name: string) {
  if (runTimers[name]) {
    window.clearInterval(runTimers[name]);
    delete runTimers[name];
  }
}

function closeRunPanel(name: string) {
  stopWatching(name);
  delete runStates[name];
  delete outputs[name];
  delete outputLoading[name];
}

async function runTask(name: string) {
  runningName.value = name;
  stopWatching(name);
  // Onceki calismanin ciktisi yeni turun ciktisi sanilmasin.
  delete outputs[name];
  runStates[name] = { phase: "starting", seconds: 0, resultCode: null };

  try {
    await runScheduledTask(name);
    watchRun(name);
  } catch (error) {
    delete runStates[name];
    showNotice(resolveApiMessage(error), true);
  } finally {
    runningName.value = "";
  }
}

/**
 * Görev bitene kadar durumunu izler.
 *
 * Sonsuza kadar sormaz: bir üst sınır vardır ve dolduğunda "hâlâ çalışıyor" der. Süresiz bir
 * yoklama, sekme açık kaldığı sürece sunucuda saniyede bir PowerShell çalıştırmak demekti.
 */
function watchRun(name: string) {
  const POLL_MS = 2500;
  const MAX_SECONDS = 300;
  const basladi = Date.now();
  let sorguSuruyor = false;

  runTimers[name] = window.setInterval(() => {
    const gecen = Math.round((Date.now() - basladi) / 1000);
    const durum = runStates[name];
    if (!durum) {
      stopWatching(name);
      return;
    }

    durum.seconds = gecen;

    if (gecen >= MAX_SECONDS) {
      durum.phase = "running";
      stopWatching(name);
      void refresh();
      return;
    }

    // Bir önceki sorgu hâlâ sürüyorsa yenisini açma: yavaş bir sunucuda sorgular üst üste binerdi.
    if (sorguSuruyor) return;
    sorguSuruyor = true;

    void getScheduledTaskStatus(name)
      .then((status) => {
        if (!runStates[name]) return;
        if (status.isRunning) {
          runStates[name].phase = "running";
          return;
        }

        runStates[name].phase = "done";
        runStates[name].resultCode = status.lastTaskResult;
        stopWatching(name);
        void loadOutput(name);
        void refresh();
      })
      .catch(() => {
        // Durum okunamadı: "bitti, başarılı" demek YANLIŞ olurdu — bilmediğimizi söyleriz.
        if (!runStates[name]) return;
        runStates[name].phase = "unknown";
        stopWatching(name);
      })
      .finally(() => {
        sorguSuruyor = false;
      });
  }, POLL_MS);
}

/** Canlı satırda gösterilecek metin. */
function runMessage(name: string) {
  const durum = runStates[name];
  if (!durum) return "";
  if (durum.phase === "starting") return t("panel.tasks.runStarting");
  if (durum.phase === "unknown") return t("panel.tasks.runUnknown");
  if (durum.phase === "running") {
    return durum.seconds >= 300
      ? t("panel.tasks.runStillGoing", { seconds: durum.seconds })
      : t("panel.tasks.runLive", { seconds: durum.seconds });
  }

  // Bitiş: kodu ham göstermek yerine, tablodakiyle AYNI sözlükten geçir.
  const sonuc = resultLabelForCode(durum.resultCode, tasks.value.find((item) => item.name === name)?.taskType ?? "command");
  return t("panel.tasks.runFinished", { seconds: durum.seconds, result: sonuc });
}

function runClass(name: string) {
  const durum = runStates[name];
  if (!durum) return "";
  if (durum.phase === "done") return durum.resultCode === 0 ? "ok" : "warn";
  if (durum.phase === "unknown") return "warn";
  return "";
}

onUnmounted(() => {
  // Sayfadan çıkılınca yoklama sürmemeli.
  Object.keys(runTimers).forEach(stopWatching);
  if (clockTimer) {
    window.clearInterval(clockTimer);
    clockTimer = 0;
  }
});

function isDisabled(item: ScheduledTask) {
  return item.state.toLocaleLowerCase().includes("disabled");
}

function isRunning(item: ScheduledTask) {
  return item.state?.toLocaleLowerCase() === "running";
}

/** Windows'un durum adı kullanıcının dilinde. "Ready" bir cevap değil, bir sızıntıdır. */
function stateLabel(value: string) {
  const state = (value || "").toLocaleLowerCase();
  if (state === "running") return t("panel.tasks.stateRunning");
  if (state === "disabled") return t("panel.tasks.stateDisabled");
  if (state === "ready") return t("panel.tasks.stateReady");
  if (state === "queued") return t("panel.tasks.stateQueued");
  return value || "-";
}

async function toggleTask(item: ScheduledTask) {
  togglingName.value = item.name;
  try {
    await setScheduledTaskEnabled(item.name, isDisabled(item));
    await refresh();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    togglingName.value = "";
  }
}

// Kullanıcının GERÇEKTEN gördüğü kodlar. Ham onaltılık bir sayı göstermek, sorunu kullanıcının
// arama motoruna havale etmektir: "Hata (0x800710E0)" satırı hiçbir şey anlatmıyordu.
const SKIPPED = 2148007648; // 0x800710E0 — önceki tur hâlâ sürerken yeni tur reddedildi
const TERMINATED = 267014; // 0x41306 — süre sınırı ya da elle durdurma
// 0x8007010B — Windows görevi HİÇ başlatamadı: çalışma klasörü yok. Süreç hiç oluşmadığı için
// çıktı da yoktur; kod çevrilmezse kullanıcı olmayan bir çıktıyı arar.
const BAD_DIRECTORY = 2147942667;

// curl çıkış kodları: SADECE "url" tipinde geçerlidir. Aynı küçük sayılar bir .bat ya da .exe
// görevinde o programın kendi çıkış kodudur ve curl'ün anlamıyla okumak yanlış olur.
const CURL_CODES: Record<number, string> = {
  6: "panel.tasks.resultUrlDns",
  7: "panel.tasks.resultUrlConnect",
  22: "panel.tasks.resultUrlHttp",
  28: "panel.tasks.resultUrlTimeout",
  35: "panel.tasks.resultUrlTls",
  60: "panel.tasks.resultUrlTls"
};

// Son çalışma sonucu: 0 = başarılı; 267009/267011/267045 = koşuyor/hiç çalışmadı/kuyrukta (nötr).
//
// TEK sözlük: tablodaki rozet de "şimdi çalıştır" sonrası canlı satır da buradan geçer. İki ayrı
// yerde çevirmek, birini güncelleyip diğerini unutmanın kestirme yoludur.
function resultLabelForCode(code: number | null | undefined, taskType: string) {
  if (code === null || code === undefined || code === 267011) return "-";
  if (code === 0) return t("panel.tasks.resultOk");
  if (code === 267009 || code === 267045) return t("panel.tasks.running");
  if (code === SKIPPED) return t("panel.tasks.resultSkipped");
  if (code === TERMINATED) return t("panel.tasks.resultTerminated");
  if (code === BAD_DIRECTORY) return t("panel.tasks.resultBadDirectory");
  if (code === 2 || code === 3) return t("panel.tasks.resultNotFound");
  if (taskType === "url") {
    return CURL_CODES[code] ? t(CURL_CODES[code]) : t("panel.tasks.resultUrlOther", { code });
  }

  // PROGRAMIN KENDİ ÇIKIŞ KODU. En sık görülen sonuç "0x1"di ve panel onu ham hâliyle yazıyordu:
  // kullanıcı bir Windows hata kodu arıyor, oysa bu, çalışan programın "hata ile bittim" demesidir.
  // Küçük pozitif sayılar (1-255) kabuk/uygulama çıkış kodlarıdır; Windows HRESULT'ları çok daha
  // büyük değerlerdir, o yüzden ikisi karışmaz.
  if (code > 0 && code < 256) {
    return t("panel.tasks.resultExitCode", { code });
  }

  return t("panel.tasks.resultFail", { code: "0x" + code.toString(16).toUpperCase() });
}

function resultLabel(item: ScheduledTask) {
  return resultLabelForCode(item.lastTaskResult, item.taskType);
}

/** Satırın altında gösterilen "peki ne yapmalıyım" cümlesi. Yoksa boş döner. */
function resultHint(item: ScheduledTask) {
  const code = item.lastTaskResult;
  if (code === SKIPPED) return t("panel.tasks.resultSkippedHint");
  if (code === 2 || code === 3) return t("panel.tasks.resultNotFoundHint");
  if (code === BAD_DIRECTORY) return t("panel.tasks.resultBadDirectoryHint");
  if (typeof code === "number" && code > 0 && code < 256 && item.taskType !== "url") {
    return t("panel.tasks.resultExitCodeHint");
  }
  return "";
}

/**
 * Görev kullanıcının KENDİ verdiği süre sınırını aştı mı.
 *
 * "Takıldı" kelimesi yalnızca buradan geçerse kullanılır. Uzun sürmek kendi başına arıza değildir:
 * kullanıcının botu günlerce sürebiliyor ve bu normal. Sınır koymamış bir görevi panelin "takıldı"
 * diye etiketlemesi, kullanıcının işini arıza sanmaktır — güvenilirliği bitiren şey budur.
 */
function isOverTimeLimit(item: ScheduledTask) {
  if (!isRunning(item) || !item.timeLimitHours) return false;
  const gecen = elapsedMs(item);
  return gecen !== null && gecen > item.timeLimitHours * 3600_000;
}

/** Süren çalışmanın başlangıcından bu yana geçen süre (ms). Ölçülemiyorsa null. */
function elapsedMs(item: ScheduledTask) {
  if (!item.lastRunTimeUtc) return null;
  const baslangic = new Date(item.lastRunTimeUtc).getTime();
  if (Number.isNaN(baslangic)) return null;
  // Sunucu ile tarayıcı saati arasındaki küçük farklar negatif süre üretebilir; 0'a kırpılır.
  return Math.max(0, now.value - baslangic);
}

/**
 * Süreyi kullanıcının okuduğu gibi yazar: "2 gün 4 saat", "35 dakika".
 *
 * Saniye cinsinden bir sayı ("187200 sn") teknik olarak doğru ama sorulan soruya cevap değildir.
 */
function formatDuration(ms: number) {
  const toplamDakika = Math.floor(ms / 60_000);
  if (toplamDakika < 1) return t("panel.tasks.durationSeconds", { seconds: Math.floor(ms / 1000) });

  const gun = Math.floor(toplamDakika / 1440);
  const saat = Math.floor((toplamDakika % 1440) / 60);
  const dakika = toplamDakika % 60;

  if (gun > 0) return t("panel.tasks.durationDays", { days: gun, hours: saat });
  if (saat > 0) return t("panel.tasks.durationHours", { hours: saat, minutes: dakika });
  return t("panel.tasks.durationMinutes", { minutes: dakika });
}

/** Üst kutudaki "ne kadardır sürüyor" satırı. */
function runningForLabel(item: ScheduledTask) {
  const gecen = elapsedMs(item);
  if (gecen === null) return t("panel.tasks.runningUnknownStart");
  const sure = formatDuration(gecen);
  return item.timeLimitHours && isOverTimeLimit(item)
    ? t("panel.tasks.runningForOverLimit", { duration: sure, limit: item.timeLimitHours })
    : t("panel.tasks.runningFor", { duration: sure });
}

/**
 * "Neden çalışmadı" sorusunun TEK CÜMLELİK cevabı.
 *
 * Durum, son sonuç ve son çalışma zamanı ayrı ayrı ekranda duruyordu; bunları birleştirip anlam
 * çıkarmak kullanıcıya kalıyordu ve kullanıcı PowerShell'e gitmek zorunda kaldı. Cümle, ölçülmüş
 * verilerden kurulur — tahmin yok, bilinmeyen durumda satır hiç yazılmaz.
 */
function explain(item: ScheduledTask) {
  if (isDisabled(item)) {
    return t("panel.tasks.explainDisabled");
  }

  if (isRunning(item)) {
    const gecen = elapsedMs(item);
    if (gecen === null) return t("panel.tasks.explainRunningUnknown");
    const sure = formatDuration(gecen);
    const baslangic = formatUtc(item.lastRunTimeUtc);

    if (isOverTimeLimit(item)) {
      return t("panel.tasks.explainRunningOverLimit", { duration: sure, started: baslangic, limit: item.timeLimitHours });
    }

    // ÇALIŞIYOR + son sonuç "atlandı": bu ikisi birlikte, kullanıcının asıl merak ettiği
    // durumdur — iş sürerken gelen turların reddedildiği an. Yalnızca "çalışıyor" demek,
    // atlanma bilgisini rozete hapsedip cümleden düşürüyordu.
    if (item.lastTaskResult === SKIPPED) {
      return t("panel.tasks.explainRunningSkipping", { duration: sure, started: baslangic });
    }

    return t("panel.tasks.explainRunning", { duration: sure, started: baslangic });
  }

  const kod = item.lastTaskResult;
  if (!item.lastRunTimeUtc || kod === 267011 || kod === null || kod === undefined) {
    return t("panel.tasks.explainNeverRan");
  }

  const zaman = formatUtc(item.lastRunTimeUtc);
  if (kod === SKIPPED) return t("panel.tasks.explainSkipped", { time: zaman });
  if (kod === 0) return t("panel.tasks.explainOk", { time: zaman });
  if (kod === TERMINATED) return t("panel.tasks.explainTerminated", { time: zaman });
  return t("panel.tasks.explainFailed", { time: zaman, result: resultLabel(item) });
}

function explainClass(item: ScheduledTask) {
  if (isDisabled(item)) return "warn";
  if (isOverTimeLimit(item)) return "warn";
  if (isRunning(item)) return "live";
  const kod = item.lastTaskResult;
  if (kod === 0) return "ok";
  if (kod === null || kod === undefined || kod === 267011) return "";
  return "warn";
}

/**
 * Çıktı bağlantısı yalnızca gerçekten çıktı OLABİLECEK görevlerde çıkar.
 *
 * Çıktı yakalama 0.2.68'de eklendi; ondan önce kaydedilmiş görevlerde dosya yoktur. Var olmayan
 * bir çıktıya bağlantı vermek, kullanıcıyı boş bir kutuya göndermek olurdu.
 */
function canShowOutput(item: ScheduledTask) {
  const kod = item.lastTaskResult;
  return Boolean(item.capturesOutput)
    && typeof kod === "number"
    && kod !== 0
    && kod !== SKIPPED
    && kod !== 267011
    && !isRunning(item);
}

const openOutputs = reactive<Record<string, boolean>>({});

async function showOutput(name: string) {
  openOutputs[name] = true;
  await loadOutput(name);
}

function closeOutput(name: string) {
  delete openOutputs[name];
  delete outputs[name];
  delete outputLoading[name];
}

function resultClass(item: ScheduledTask) {
  const code = item.lastTaskResult;
  if (code === 0) return "ok";
  if (code === null || code === undefined || code === 267011 || code === 267009 || code === 267045) return "";
  return "warn";
}

/**
 * Süren çalışmayı durdurur; görev bir sonraki zamanında normal çalışmaya döner.
 *
 * Sonuç ÖLÇÜLMÜŞ olarak gelir ve olduğu gibi söylenir: kaç süreç sonlandırıldı, görev hâlâ
 * çalışıyor mu. "Durduruldu" demek, durduğunu görmeden verilebilecek bir söz değil.
 */
const stoppingName = ref("");
async function stopRun(item: ScheduledTask) {
  const uzun = elapsedMs(item);
  await confirm({
    title: t("panel.confirm.stopTaskTitle", { name: item.name }),
    message: t("panel.tasks.confirmStop"),
    impact: [
      `${t("panel.tasks.name")}: ${item.name}`,
      uzun === null ? t("panel.tasks.runningUnknownStart") : t("panel.tasks.runningFor", { duration: formatDuration(uzun) })
    ],
    confirmLabel: t("panel.tasks.stopRunning"),
    busyLabel: t("panel.tasks.stopping"),
    action: async () => {
      stoppingName.value = item.name;
      try {
        const sonuc = await stopScheduledTask(item.name);
        if (sonuc.stillRunning) {
          showNotice(t("panel.tasks.stopStillRunning"), true);
        } else if (sonuc.killedProcesses > 0) {
          showNotice(t("panel.tasks.stopDone", { count: sonuc.killedProcesses }), false);
        } else {
          showNotice(t("panel.tasks.stopNothingFound"), false);
        }
        await refresh();
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        stoppingName.value = "";
      }
    }
  });
}

function typeLabel(value: string) {
  const key = value === "url"
    ? "panel.tasks.typeUrl"
    : value === "php"
      ? "panel.tasks.typePhp"
      : value === "script"
        ? "panel.tasks.typeScript"
        : "panel.tasks.typeCommand";
  return t(key);
}

function scheduleLabel(value: string) {
  const key = value === "minute" ? "panel.tasks.minutely" : value === "daily" ? "panel.tasks.daily" : "panel.tasks.hourly";
  return t(key);
}

function taskStateClass(value: string) {
  const state = value.toLocaleLowerCase();
  return state.includes("ready") || state.includes("running") ? "ok" : state.includes("disabled") ? "warn" : "";
}

/**
 * Durum hücresinin rozetleri — SIRASI ve TONU ölçüme göre.
 *
 * Tek başına `taskStateClass` yeterli değildi: Windows'un durumu ("Ready") ile görevin son
 * turunun sonucu BAĞIMSIZ iki ölçüm ve panel ikisini yan yana, en iyisini önce koyarak
 * gösteriyordu. Arıza varsa önce arıza konuşur; "Hazır" o satırda bir başarı iddiası değildir,
 * o yüzden yeşilini bırakır (nötr rozet). Sözcükler değişmez — hiçbir bilgi gizlenmiyor.
 */
function statusPills(item: ScheduledTask): { text: string; tone: string; title?: string }[] {
  const durum = { text: stateLabel(item.state), tone: taskStateClass(item.state) };
  const sonuc = { text: resultLabel(item), tone: resultClass(item), title: resultHint(item) };
  const arizaVar = sonuc.tone === "warn";

  if (arizaVar && durum.tone === "ok") {
    durum.tone = "";
  }

  return arizaVar ? [sonuc, durum] : [durum, sonuc];
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatUtc(value: string | null) {
  return formatDateTime(value, "-");
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  // Gerçek teknik nedeni (backend Details) da göster: "kaydedilemedi" tek başına teşhis edilemiyor.
  const details = extractApiDetails(error);
  return details ? `${message} — ${details}` : message;
}
</script>

<style scoped>
/* Eylem sütunu SABİT: dört düğme 2x2 oturur ve satırdan satıra yeri değişmez. Oransal (fr)
   verildiğinde uzun görev adları düğme sütununu daraltıp düğmeleri alt alta diziyordu — bir
   satırda iki, diğerinde dört sıra hâlinde; tablo tırtıklı görünüyordu. */
.cron-row {
  grid-template-columns: 1.15fr 2fr 1fr 1.05fr 232px;
  align-items: start;
}

/* DÖRT DÜĞME ARTIK IZGARADA, ESNEK SARMADA DEĞİL.
   ÖLÇÜLDÜ: `.row-actions` base.css'te `flex-wrap: wrap` ve düğmeler kendi metin genişliğinde.
   "Durdur" (7 harf) ile "Şimdi çalıştır" (14 harf) farklı genişlikte olduğu için sarma noktası
   SATIRDAN SATIRA değişiyordu: bir görevde 2+2, diğerinde 3+1 diziliyor, `margin-left: auto`
   ile sağa itilen kırmızı "Sil" de her satırda başka bir noktada duruyordu — 25 satırlık
   listede hiçbir düğme dikeyde hizalı değildi.
   İki eşit sütunluk ızgara bunu tümüyle kaldırır: düğmeler her satırda AYNI iki x konumunda ve
   AYNI genişlikte. "Sil" ise 3. sıraya, sağ sütuna sabitlendi — böylece hem hizalı hem de
   "Devre dışı bırak"tan bir satır uzakta kalır (yanlışlıkla tıklamayı zorlaştıran ayrım
   korunuyor; eskiden bunu `margin-left: auto` yapıyordu ve hizayı o bozuyordu). */
.cron-row .row-actions {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  align-content: start;
  gap: var(--sp-6);
}

.cron-row .row-actions .danger {
  grid-column: 2;
  grid-row: 3;
}

/* SESSİZ DÜĞME — `.ghost` panelde 8 yerde kullanılıyor ama HİÇBİR YERDE TANIMLI DEĞİL (ölçüldü:
   AttentionSectionNotice, RunningTaskBanner, ServerHealthView, SiteDetailView ve bu ekran).
   Tanımsız sınıf bir hata üretmez: düğme global `button` kuralına düşer ve BİRİNCİL mavi gradyanı
   alır. Sonuç, bu ekranda ölçülebilir bir hiyerarşi hatasıydı — gri bir açıklama şeridinin içindeki
   "Çıktıyı göster" ve "Kapat", tablodaki gerçek eylemlerden daha baskın çiziliyordu.
   Tanım BURADA kapsamlı (scoped) olarak veriliyor: `styles/base.css` bu turun kapsamı dışında ve
   sınıfın asıl yeri orasıdır — diğer beş bileşen hâlâ tanımsız sınıfa atıf yapıyor. */
.ghost {
  background: transparent;
  border: 1px solid color-mix(in srgb, var(--accent-1) 30%, transparent);
  color: var(--accent-text);
  box-shadow: none;
}

.ghost:not(:disabled):hover {
  background: color-mix(in srgb, var(--accent-1) 10%, transparent);
  filter: none;
  transform: none;
  box-shadow: none;
}

/* Komut = kod. Değişken genişlikli yazı tipinde "l1I" ve "O0" ayırt edilemiyor, oysa buradaki
   metin kullanıcının birebir doğrulaması gereken bir yol.
   Yazı tipi listesi ELDE YAZILMIYOR: `--font-mono` tam da bu yüzden tanımlandı (bkz. base.css),
   yoksa aynı panelin konsol yüzeyleri birbirinden farklı yazı tipiyle çiziliyordu. */
.cell-command strong,
.cell-command small {
  font-family: var(--font-mono);
  font-size: var(--fs-2xs);
  font-weight: 500;
  line-height: 1.4;
}

.cell-status {
  display: grid;
  gap: var(--sp-4);
  min-width: 0;
}

.pill-row {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-4);
}

.cell-status small {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

/* Uyarı niteliğindeki alt satır (çalışma zamanı yok / devre dışı): rozete gerek yok, ama düz
   gri de olmamalı — kullanıcı bunun bir bilgi değil bir eksiklik olduğunu görmeli. */
.cell-flag {
  color: var(--warn-text);
  font-weight: 600;
}

.span-2 {
  grid-column: span 2;
}

/* SATIR UZUNLUĞU SINIRI. En uzun görünür metin bu ekranda (ölçüldü: `panel.tasks.systemEnvHint`
   305 karakter) ve üç sütunluk form ızgarasında iki sütuna yayılıyor: 1440px'lik bir panelde satır
   başına ~105 karaktere çıkıyordu; okunabilir aralık 45-90'dır. `ch` ile sınırlamak metni KISALTMAZ,
   yalnız daha erken sardırır — tek kelime bile gizlenmiyor. */
.field-hint {
  max-width: 78ch;
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.search-control {
  width: min(300px, 50vw);
}

/* Süren çalışma kutusu NÖTR görünür: bilgi verir, alarm çalmaz. Yalnızca kullanıcının kendi
   verdiği süre sınırı aşıldığında sarıya döner (.alarm).
   `margin-bottom` KALDIRILDI: kutu `.module-shell` ızgarasının bir çocuğu ve ızgaranın kendi
   `gap`i (12px) zaten aralığı veriyordu. İkisi toplanınca kutunun ÜSTÜNDE 12px, ALTINDA 26px
   boşluk oluşuyordu; sayfadaki tek asimetrik aralık buydu ve kutuyu tabloya değil forma ait
   gösteriyordu. */
.running-notice {
  border: 1px solid color-mix(in srgb, var(--accent-1) 40%, transparent);
  border-radius: var(--radius-md);
  padding: var(--sp-10) var(--sp-12);
  background: color-mix(in srgb, var(--accent-1) 7%, transparent);
}

.running-notice.alarm {
  border-color: var(--warn-1);
  background: var(--warn-bg);
}

.running-notice p {
  margin: var(--sp-4) 0 0;
  font-size: var(--fs-sm);
}

.running-notice ul {
  margin: var(--sp-8) 0 0;
  padding: 0;
  list-style: none;
  display: grid;
  gap: var(--sp-4);
}

.running-notice li small {
  display: block;
  opacity: 0.8;
}

/* Devre dışı satır: soluk bir rozet fark edilmiyordu, satırın kendisi ayrışmalı. */
.row-disabled {
  opacity: 0.68;
  border-left: 3px solid var(--warn-1);
}

.row-running {
  border-left: 3px solid var(--accent-1);
}

/* BİR GÖREV = BİR GRUP. Ayırıcı çizgi artık satırın değil GRUBUN sonunda; böylece görevin
   açıklaması çizginin altında kalıp bir alttaki göreve aitmiş gibi görünmüyor.
   min-width: satır 720px'e kadar genişleyebiliyor (.data-row); grup da aynı genişlikte olmazsa
   tablo yatay kaydırıldığında açıklama satırla hizasını kaybederdi. */
.cron-item {
  min-width: 720px;
  border-bottom: 1px solid var(--panel-border);
}

.cron-item:last-child {
  border-bottom: 0;
}

.cron-item .data-row {
  border-bottom: 0;
}

/* SATIR ALTI ŞERİDİ — TEK DESEN, İKİ KULLANIM.
   "Sebep" satırı (.row-explain) ve "canlı çalışma" satırı (.run-live) aynı görsel fikri anlatıyor:
   üstteki görev satırına ait, sol şeritle ona bağlanmış bir açıklama bandı. Buna rağmen iki ayrı
   kural olarak yazılmışlardı ve BİRBİRİNİ TUTMUYORLARDI (ölçüldü: dolgu 0.4/0.5rem, yazı
   0.84/0.86rem, kenar yarıçapı aynı ama boşlukları ayrı). Aynı yerde alt alta çizildikleri için
   fark ekranda tırtık olarak görünüyordu.
   Ortak kalıp burada tek yerde tanımlanır; ayrıştıkları tek şey RENKTİR ve o zaten anlam taşır.
   Şablon DEĞİŞMEDİ: sınıf adları ve mantıkları aynı kaldı, yalnız tanım paylaşıldı. */
.row-explain,
.run-live {
  display: flex;
  gap: var(--sp-8);
  margin: 0 var(--sp-12) var(--sp-8);
  padding: var(--sp-6) var(--sp-10);
  border-left: 3px solid var(--accent-1);
  border-radius: 0 var(--radius-sm) var(--radius-sm) 0;
  background: color-mix(in srgb, var(--accent-1) 7%, transparent);
  font-size: var(--fs-sm);
}

/* Sebep satırı tek satırlık bir cümle + düğme: dikeyde ORTALANIR.
   Canlı satır çok satırlı çıktı taşıyabilir, orada nokta üstte kalmalı (aşağıda). */
.row-explain {
  flex-wrap: wrap;
  align-items: center;
  border-left-color: color-mix(in srgb, var(--text-muted) 35%, transparent);
  background: color-mix(in srgb, var(--text-muted) 7%, transparent);
  color: var(--text-muted);
}

.row-explain.warn {
  color: var(--warn-text);
  border-left-color: var(--warn-1);
  background: var(--warn-bg);
}

.row-explain.live {
  color: var(--accent-1);
  border-left-color: var(--accent-1);
  background: color-mix(in srgb, var(--accent-1) 8%, transparent);
}

.run-live {
  align-items: flex-start;
}

.run-live.ok {
  border-left-color: var(--ok-1);
  background: color-mix(in srgb, var(--ok-1) 8%, transparent);
}

.run-live.warn {
  border-left-color: var(--warn-1);
  background: var(--warn-bg);
}

.run-text {
  flex: 1;
}

.run-text small {
  display: block;
  opacity: 0.75;
  margin-top: var(--sp-2);
}

.env-ok,
.env-fix {
  display: block;
  margin-top: var(--sp-6);
  padding-top: var(--sp-6);
  border-top: 1px solid color-mix(in srgb, currentColor 18%, transparent);
}

.env-ok {
  opacity: 0.85;
}

.env-fix strong {
  display: block;
  margin-bottom: var(--sp-2);
}

.env-fix button {
  margin-top: var(--sp-6);
}

/* SYSTEM ortamı uyarısı: sarı zeminli tek kutu. `--warn-bg` TANIMLI bir belirteçtir; buradaki
   yedek değer (`rgba(224, 180, 0, 0.07)`) hiçbir zaman devreye girmiyordu ama okuyana "bu belirteç
   olmayabilir" diye yanlış bilgi veriyordu — bu depoda tam da tanımsız belirteç yüzünden renkler
   sessizce yedeğe düştü (bkz. CssTokenReferenceTests). Yedek kaldırıldı. */
.env-hint {
  margin: 0;
  padding: var(--sp-6) var(--sp-10);
  border-radius: var(--radius-sm);
  border-left: 3px solid var(--warn-1);
  background: var(--warn-bg);
  line-height: 1.5;
}

.editing-banner {
  margin: 0 0 var(--sp-10);
  padding: var(--sp-6) var(--sp-10);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--accent-1) 10%, transparent);
  font-size: var(--fs-sm);
}

.run-output {
  margin: var(--sp-4) 0 0;
  padding: var(--sp-6) var(--sp-10);
  max-height: 18rem;
  overflow: auto;
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  font-family: var(--font-mono);
  font-size: var(--fs-2xs);
  line-height: 1.45;
  /* Uzun satirlar sayfayi YATAY kaydirmasin; kutunun kendi icinde kalsin. */
  white-space: pre-wrap;
  word-break: break-word;
}

.run-dot {
  width: 0.62rem;
  height: 0.62rem;
  margin-top: var(--sp-4);
  border-radius: 50%;
  background: currentColor;
  opacity: 0.55;
  flex: none;
}

/* Dönen halka yalnızca çalışırken; bittiğinde hareket durur ki "bitti" gözle anlaşılsın. */
.run-dot.spinning {
  background: none;
  border: 2px solid currentColor;
  border-top-color: transparent;
  opacity: 0.8;
  animation: run-spin 0.9s linear infinite;
}

@keyframes run-spin {
  to { transform: rotate(360deg); }
}

/* Hareketi azalt ayarı açıksa dönme yerine yanıp sönme bile yok: sabit halka. */
@media (prefers-reduced-motion: reduce) {
  .run-dot.spinning { animation: none; }
}

.running-notice li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-16);
  flex-wrap: wrap;
}
</style>
