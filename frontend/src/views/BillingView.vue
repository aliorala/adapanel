<template>
  <section class="panel module-shell billing-workspace">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.billing.title") }}</h2>
        <p class="meta">{{ t("panel.billing.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || saving" @click="refresh">
        {{ t("panel.billing.refreshUsage") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <!-- (a) Açma/kapama: anahtar doğrulamasının YERİNE değil ÜSTÜNE binen ikinci kapı.
         Sızmış geçerli bir anahtar varken yönetici entegrasyonu tek düğmeyle kesebilmeli. -->
    <section class="form-surface">
      <!--
        BAŞLIK DURUM SÖYLEMEZ, DURUMU ROZET SÖYLER.

        ÖLÇÜLDÜ: başlık "Entegrasyon açık" yazıyordu ama içindeki onay kutusu işaretsiz,
        rozet "Reddedildi" olabiliyordu; yani kart başlığı kapalı bir entegrasyona "açık"
        diyordu. Başlık artık KONUYU adlandırır (panel.billing.integrationTitle), durumu
        yalnızca rozet söyler.

        Rozet metni de değişti: eskiden ÇAĞRI SONUCU sözlüğünden ("Reddedildi") alınıyordu
        ve neyin reddedildiğini söylemeden havada duruyordu. O sözlük aşağıdaki "Son çağrılar"
        tablosuna ait; bu rozet artık entegrasyonun kendi durumunu ve sonucunu yazar.

        Rozet SUNUCUDAN OKUNAN değere bakar (`settings.enabled`), formdaki `enabled`
        değişkenine değil: kullanıcı kutuyu işaretleyip henüz kaydetmediyse rozet hâlâ
        gerçeği gösterir.
      -->
      <div class="section-head">
        <div>
          <h3>{{ t("panel.billing.integrationTitle") }}</h3>
          <p class="meta">{{ t("panel.billing.enabledHint") }}</p>
        </div>
        <span v-if="loaded" :class="['status-pill', settings?.enabled ? 'ok' : 'warn']">
          {{ settings?.enabled ? t("panel.billing.stateOn") : t("panel.billing.stateOff") }}
        </span>
      </div>
      <label class="inline-field">
        <input v-model="enabled" type="checkbox" :disabled="saving || !loaded" />
        <span>{{ t("panel.billing.enabled") }}</span>
      </label>
      <div class="row-actions">
        <button :disabled="saving || !loaded || enabled === settings?.enabled" @click="save">
          {{ t("panel.billing.save") }}
        </button>
      </div>
    </section>

    <!-- (b) Bağlantı anahtarı: burada anahtar ÜRETİLMEZ. Kimlik bilgisi üreten uç
         (ApiTokensController) bilerek çerez şemasına sabitli; bu ekran kullanıcıyı oraya
         yönlendirir ve WHMCS'e nasıl yazılacağını söyler. -->
    <section class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.billing.tokenTitle") }}</h3>
          <p class="meta">{{ t("panel.billing.tokenHint") }}</p>
        </div>
      </div>
      <p class="meta">{{ t("panel.billing.tokenReadOnly") }}</p>
      <!-- HAM BİÇİM CÜMLEDEN ÇIKTI. "entry=<gizli-yol>;token=<anahtar>" ipucu cümlesinin
           İÇİNDE gömülü duruyordu; okuyan gözün cümleyle kodu ayırması gerekiyordu ve
           kopyalanamıyordu. Artık kendi kutusunda ve tek tıkla panoya alınıyor. -->
      <div class="stacked-field">
        <span class="field-label">Access Hash</span>
        <CodeSample
          :code="accessHashSample"
          :copy-label="t('panel.dashboard.access.copy')"
          :copied-label="t('panel.common.copied')" />
      </div>
    </section>

    <!-- (c) Durum + son çağrılar: "entegrasyon çalışıyor mu" sorusu panelden yanıtlanabilmeli,
         yoksa arıza teşhisi WHMCS günlüklerine bakabilen tek kişiye bağımlı kalır. -->
    <section class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.billing.statusTitle") }}</h3>
        </div>
      </div>
      <dl class="status-grid">
        <div>
          <dt>{{ t("panel.billing.lastCall") }}</dt>
          <dd>{{ settings?.lastCallAtUtc ? formatDate(settings.lastCallAtUtc) : t("panel.billing.never") }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.billing.lastAccounts") }}</dt>
          <dd>{{ settings?.lastAccountCount ?? 0 }}</dd>
        </div>
      </dl>

      <h4>{{ t("panel.billing.recentTitle") }}</h4>
      <p v-if="loaded && !settings?.recentCalls?.length" class="meta">{{ t("panel.billing.recentEmpty") }}</p>
      <div v-else-if="settings?.recentCalls?.length" class="table-scroll">
        <table class="data-table">
          <thead>
            <tr>
              <th>{{ t("panel.billing.colTime") }}</th>
              <th>{{ t("panel.billing.colAction") }}</th>
              <th>{{ t("panel.billing.colOutcome") }}</th>
              <th>{{ t("panel.billing.colDetail") }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(entry, index) in settings.recentCalls" :key="`${entry.atUtc}-${index}`">
              <td>{{ formatDate(entry.atUtc) }}</td>
              <td>{{ actionLabel(entry.action) }}</td>
              <td>
                <span :class="['status-pill', entry.outcome === 'ok' ? 'ok' : 'warn']">{{ outcomeLabel(entry.outcome) }}</span>
              </td>
              <td>{{ entry.detail }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <!-- (d) WHMCS'e verilen değerlerin AYNISI gösterilir: fatura tartışmasında panel ile
         faturalama sisteminin aynı sayıyı söylediği buradan doğrulanır. -->
    <section class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.billing.usageTitle") }}</h3>
        </div>
      </div>

      <p v-if="usage && usage.unmeasuredTrafficCount > 0" class="notice error">
        {{ t("panel.billing.unmeasuredWarning", { count: usage.unmeasuredTrafficCount }) }}
      </p>

      <p v-if="loaded && !usage?.accounts?.length" class="meta">{{ t("panel.billing.usageEmpty") }}</p>
      <div v-else-if="usage?.accounts?.length" class="table-scroll">
        <table class="data-table">
          <thead>
            <tr>
              <th>{{ t("panel.billing.colAccount") }}</th>
              <th>{{ t("panel.billing.colDomains") }}</th>
              <th>{{ t("panel.billing.colDisk") }}</th>
              <th>{{ t("panel.billing.colBandwidth") }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="account in usage.accounts" :key="account.userId">
              <td>{{ account.userName }}</td>
              <td>{{ account.domains.join(", ") }}</td>
              <!--
                DİSK, BANT GENİŞLİĞİNDEN FARKLI GÖSTERİLİR ve bu bilinçli.
                Bant genişliği ölçülemediğinde sayı HİÇ gösterilmez, çünkü orada ölçülemeyen site
                toplama katılmaz — kalan sayı bir alt sınır bile değildir, yanıltıcıdır.
                Diskte ise kesilen tarama GERÇEK bir alt sınır üretir ("en az bu kadar"), yani
                sayıyı atmak bilgi kaybı olurdu. Bu yüzden sayı durur, yanına eksik olduğunu
                söyleyen rozet konur.
              -->
              <td>
                {{ account.diskUsageMb }} / {{ limitLabel(account.diskLimitMb) }}
                <span
                  v-if="!account.diskMeasured"
                  class="status-pill warn"
                  :title="t('users.disk_usage_partial_hint')">
                  {{ t("users.disk_usage_partial") }}
                </span>
              </td>
              <td>
                <span v-if="account.bandwidthMeasured">
                  {{ account.bandwidthUsageMb }} / {{ limitLabel(account.bandwidthLimitMb) }}
                </span>
                <span v-else class="status-pill warn">{{ t("panel.billing.unmeasured") }}</span>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
// Faturalama (WHMCS) entegrasyonunun ayar ve tanı ekranı. Sağlama işlemleri (hesap açma,
// askı, iptal) WHMCS modülünün kendi işidir; burada yalnızca entegrasyonun AÇIK olup olmadığı,
// son çağrıların ne yaptığı ve faturalama sistemine verilen kullanım değerleri görünür.
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import CodeSample from "../components/CodeSample.vue";
import {
  getBillingSettings,
  getBillingUsage,
  updateBillingSettings,
  type BillingSettings,
  type BillingUsageReport
} from "../api/billing";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { getSessionEpoch } from "../api/client";
import { formatDateTime } from "../utils/datetime";

const { t } = useI18n();

const settings = ref<BillingSettings | null>(null);
const usage = ref<BillingUsageReport | null>(null);
const enabled = ref(false);
// Liste okunmadan "kayıt yok" gösterilmesin diye ayrı bayrak.
const loaded = ref(false);
const loading = ref(false);
const saving = ref(false);
const notice = ref("");
const noticeError = ref(false);

const accessHashSample = computed(() => "entry=panel;token=adap_...");

const auth = useAuthStore();
let disposed = false, generation = 0, request = 0;
onBeforeUnmount(() => { disposed = true; });
function context() {
  const session = auth.session, actor = session?.user?.id, role = session?.user?.role;
  const version = generation, epoch = getSessionEpoch();
  return { current: () => !disposed && version === generation && epoch === getSessionEpoch() &&
    session === auth.session && actor === auth.session?.user?.id && role === auth.session?.user?.role && role === "Administrator" };
}

async function refresh() {
  if (disposed || saving.value || auth.session?.user?.role !== "Administrator") return;
  const target = context(), id = ++request;
  loading.value = true;
  try {
    const result = await getBillingSettings();
    if (!target.current() || id !== request) return;
    settings.value = result;
    enabled.value = result.enabled;
    usage.value = null;
    loaded.value = true;

    // Kullanım YALNIZCA entegrasyon açıkken okunur: kapalıyken uç 403 döner ve ekran her
    // açılışta gereksiz bir hata gösterirdi.
    const report = result.enabled ? await getBillingUsage() : null;
    if (target.current() && id === request) usage.value = report;
  } catch (error) {
    if (target.current() && id === request) showNotice(resolveApiMessage(error), true);
  } finally {
    if (target.current() && id === request) loading.value = false;
  }
}

async function save() {
  if (disposed || saving.value || !loaded.value || auth.session?.user?.role !== "Administrator") return;
  const target = context(), id = ++request;
  saving.value = true;
  loading.value = false;
  let saved = false;
  try {
    const result = await updateBillingSettings(enabled.value);
    if (!target.current() || id !== request) return;
    settings.value = result;
    enabled.value = result.enabled;
    usage.value = null;
    saved = true;
    const report = result.enabled ? await getBillingUsage() : null;
    if (!target.current() || id !== request) return;
    usage.value = report;
    showNotice(t("billing.settings_saved"), false);
  } catch (error) {
    if (target.current() && id === request)
      showNotice((saved ? `${t("billing.settings_saved")}\n\n` : "") + resolveApiMessage(error), true);
  } finally {
    if (target.current() && id === request) saving.value = false;
  }
}

watch(() => [auth.session, auth.session?.user?.id, auth.session?.user?.role], () => {
  generation++;
  request++;
  settings.value = null;
  usage.value = null;
  enabled.value = false;
  loaded.value = loading.value = saving.value = false;
  notice.value = "";
  noticeError.value = false;
  void refresh();
}, { immediate: true, flush: "sync" });

/** 0 = sınırsız (panel ve WHMCS aynı kabulü paylaşır). */
function limitLabel(limitMb: number) {
  return limitMb > 0 ? String(limitMb) : t("panel.billing.unlimited");
}

function actionLabel(action: string) {
  const key = `panel.billing.action.${action}`;
  const translated = t(key);
  return translated === key ? action : translated;
}

function outcomeLabel(outcome: string) {
  const key = `panel.billing.outcome.${outcome}`;
  const translated = t(key);
  return translated === key ? outcome : translated;
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatDate(value: string) {
  return formatDateTime(value);
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.billing-workspace {
  align-content: start;
}

.status-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 0.75rem;
  margin: 0 0 1rem;
}

.status-grid dt {
  font-size: 0.85rem;
  color: var(--text-muted);
}

.status-grid dd {
  margin: 0.15rem 0 0;
  font-weight: 600;
}

/* Geniş içerik KENDİ kabında kaydırılır; sayfa gövdesi yatay kaymamalı. */
.table-scroll {
  overflow-x: auto;
}

/* `.sample` KALDIRILDI: tek kullanıcısı olan `<code class="sample">` yerini ortak
   `CodeSample` bileşenine bıraktı; kural artık hiçbir şeyi boyamıyordu. */

.inline-field {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.75rem;
}
</style>
