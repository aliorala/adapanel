<template>
  <section class="panel module-shell firewall-panel">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.firewall.title") }}</h3>
        <p class="meta">{{ t("panel.firewall.subtitle") }}</p>
      </div>
      <div class="row-actions">
        <button class="compact" @click="showRuleForm = !showRuleForm">
          {{ showRuleForm ? t("panel.common.cancel") : t("panel.firewall.newRule") }}
        </button>
        <button class="secondary compact" :disabled="loading || loadingPorts" @click="refreshAll">{{ t("panel.firewall.refresh") }}</button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div class="ports-block">
      <div class="ports-head">
        <div>
          <h4>{{ t("panel.firewall.ports.title") }}</h4>
          <p class="meta">{{ t("panel.firewall.ports.hint") }}</p>
        </div>
        <div class="row-actions">
          <span class="ports-summary">{{ t("panel.firewall.ports.summary", { open: requiredOpenCount, total: requiredTotalCount }) }}</span>
          <button v-if="missingRequired.length > 0" class="compact" :disabled="busy" @click="openAllMissing">
            {{ t("panel.firewall.ports.openAllMissing") }}
          </button>
        </div>
      </div>

      <p class="cloud-note">{{ t("panel.firewall.ports.cloudNote") }}</p>

      <div class="data-table ports-list">
        <div class="data-row table-head port-row">
          <span>{{ t("panel.firewall.ports.service") }}</span>
          <span>{{ t("panel.sites.port") }}</span>
          <span>{{ t("panel.firewall.ports.listening") }}</span>
          <span>{{ t("panel.firewall.ports.firewall") }}</span>
          <span>{{ t("panel.firewall.ports.statusColumn") }}</span>
        </div>
        <div v-for="p in portStatus" :key="`${p.serviceKey}:${p.port}`" class="data-row port-row">
          <div class="svc">
            <strong>{{ serviceName(p.serviceKey) }}</strong>
            <span class="tags">
              <span class="chip">{{ t(`panel.firewall.ports.category.${p.category}`) }}</span>
              <span :class="['chip', p.required ? 'req' : 'opt']">
                {{ p.required ? t("panel.firewall.ports.required") : t("panel.firewall.ports.optional") }}
              </span>
            </span>
          </div>
          <span class="mono">{{ portLabel(p) }}</span>
          <span :class="['cell', listenClass(p)]">{{ listenLabel(p) }}</span>
          <span :class="['cell', p.firewallAllowed ? 'ok' : 'bad']">
            {{ p.firewallAllowed ? t("panel.firewall.ports.allowed") : t("panel.firewall.ports.missing") }}
          </span>
          <span class="cell action-cell">
            <span :class="['dot', rowStatus(p)]"></span>
            <span class="status-text">{{ t(`panel.firewall.ports.status.${rowStatus(p)}`) }}</span>
            <button v-if="!p.firewallAllowed" class="micro" :disabled="busy" @click="openPort(p)">
              {{ t("panel.firewall.ports.open") }}
            </button>
          </span>
        </div>
        <p v-if="!loadingPorts && portStatus.length === 0" class="meta">{{ t("panel.firewall.ports.empty") }}</p>
      </div>
    </div>

    <form v-if="showRuleForm" class="form-surface rule-form" @submit.prevent="saveRule('allow')">
      <label>
        <span>{{ t("panel.firewall.ruleName") }}</span>
        <input v-model="form.ruleName" maxlength="128" :placeholder="t('panel.firewall.ruleNameHint')" />
      </label>
      <label>
        <span>{{ t("panel.sites.port") }}</span>
        <input v-model.number="form.port" type="number" min="1" max="65535" required />
      </label>
      <label>
        <span>{{ t("panel.firewall.protocol") }}</span>
        <select v-model="form.protocol">
          <option value="TCP">TCP</option>
          <option value="UDP">UDP</option>
        </select>
      </label>
      <div class="form-actions">
        <button type="submit" :disabled="busy">{{ t("panel.firewall.allow") }}</button>
        <button type="button" class="danger" :disabled="busy" @click="saveRule('block')">
          {{ t("panel.firewall.block") }}
        </button>
      </div>
    </form>

    <div class="data-table rule-list">
      <div class="data-row table-head rule-row">
        <span>{{ t("panel.firewall.ruleName") }}</span>
        <span>{{ t("panel.firewall.protocol") }}</span>
        <span>{{ t("panel.sites.port") }}</span>
        <span>{{ t("panel.firewall.action") }}</span>
        <span>{{ t("panel.sites.state") }}</span>
      </div>
      <div v-for="rule in rules" :key="`${rule.displayName}:${rule.protocol}:${rule.localPort}`" class="data-row rule-row">
        <strong>{{ rule.displayName }}</strong>
        <span>{{ rule.protocol }}</span>
        <span>{{ rule.localPort || "-" }}</span>
        <span :class="['status', rule.action.toLowerCase()]">{{ rule.action }}</span>
        <span>{{ rule.enabled ? t("panel.status.enabled") : t("panel.status.disabled") }}</span>
      </div>
      <p v-if="!loading && rules.length === 0" class="meta">{{ t("panel.firewall.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { allowPort, blockPort, getFirewallRules, getPortStatus, type FirewallRule, type PortStatus } from "../api/firewall";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const { confirm } = useConfirm();
const rules = ref<FirewallRule[]>([]);
const portStatus = ref<PortStatus[]>([]);
const loading = ref(false);
const loadingPorts = ref(false);
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const showRuleForm = ref(false);
const form = reactive({ ruleName: "", port: 80, protocol: "TCP" });

const requiredTotalCount = computed(() => portStatus.value.filter((p) => p.required).length);
const requiredOpenCount = computed(
  () => portStatus.value.filter((p) => p.required && p.listening && p.firewallAllowed).length
);
const missingRequired = computed(() => portStatus.value.filter((p) => p.required && !p.firewallAllowed));

/**
 * PANELİN KENDİ PORTU — TAHMİN DEĞİL, ÖLÇÜM.
 *
 * `/firewall/ports` kataloğunda "panel" anahtarlı satırın portu sunucuda HttpContext'ten
 * doldurulur (FirewallService.BuildCatalog), yani panelin GERÇEKTEN dinlediği porttur. Ayrı bir
 * ayar okumak (panel-settings.preferredHttpsPort) yanıltıcı olurdu: orası "tercih edilen" portu
 * tutar, yeniden başlatılmamış bir panelde çalışan porttan farklı olabilir.
 *
 * Satır okunamadıysa 0 döner: o durumda panel portunu BİLMEDİĞİMİZİ kabul eder, uydurmayız —
 * sunucudaki koruma (EnsurePortIsNotSelfLocking) son sözü zaten söyler.
 */
const panelPort = computed(() => portStatus.value.find((p) => p.serviceKey === "panel")?.port ?? 0);

// Sunucunun reddettiği portlarla AYNI liste: uzak masaüstü + 80/443 + panelin kendi portu.
const REMOTE_DESKTOP_PORT = 3389;

function protectedPortReasonKey(port: number): string {
  if (panelPort.value > 0 && port === panelPort.value) {
    return "panel.firewall.protectedReason.panel";
  }
  if (port === REMOTE_DESKTOP_PORT) {
    return "panel.firewall.protectedReason.rdp";
  }
  if (port === 80 || port === 443) {
    return "panel.firewall.protectedReason.web";
  }
  return "";
}

onMounted(refreshAll);

async function refreshAll() {
  await Promise.all([refresh(), refreshPorts()]);
}

async function refresh() {
  loading.value = true;
  try {
    rules.value = await getFirewallRules();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function refreshPorts() {
  loadingPorts.value = true;
  try {
    portStatus.value = await getPortStatus();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loadingPorts.value = false;
  }
}

function serviceName(key: string) {
  const fullKey = `panel.firewall.ports.services.${key}`;
  const translated = t(fullKey);
  return translated === fullKey ? key : translated;
}

function portLabel(p: PortStatus) {
  return p.rangeEnd > 0 ? `${p.port}-${p.rangeEnd}` : `${p.port}/${p.protocol}`;
}

function listenLabel(p: PortStatus) {
  if (p.rangeEnd > 0) {
    return t("panel.firewall.ports.listen.onDemand");
  }
  if (!p.listening) {
    return t("panel.firewall.ports.listen.no");
  }
  return p.listenScope === "loopback"
    ? t("panel.firewall.ports.listen.loopback")
    : t("panel.firewall.ports.listen.all");
}

function listenClass(p: PortStatus) {
  if (p.rangeEnd > 0) {
    return "muted";
  }
  if (!p.listening) {
    return p.required ? "bad" : "muted";
  }
  return p.listenScope === "loopback" ? "warn" : "ok";
}

function rowStatus(p: PortStatus): "ok" | "warn" | "problem" | "unused" {
  // Aralık (FTP pasif): dinleme istek anında olur; durum yalnızca firewall'a bakar.
  if (p.rangeEnd > 0) {
    return p.firewallAllowed ? "ok" : "warn";
  }
  if (p.required) {
    if (!p.listening || !p.firewallAllowed) {
      return "problem";
    }
    return p.listenScope === "loopback" ? "warn" : "ok";
  }
  if (!p.listening) {
    return "unused";
  }
  return p.firewallAllowed ? "ok" : "warn";
}

async function openPort(p: PortStatus) {
  busy.value = true;
  try {
    await allowPort(p.port, p.protocol || "TCP", `AdaPanel ${serviceName(p.serviceKey)} ${p.port}`);
    await refreshAll();
    showNotice(t("panel.firewall.ports.opened"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function openAllMissing() {
  busy.value = true;
  try {
    for (const p of missingRequired.value) {
      await allowPort(p.port, p.protocol || "TCP", `AdaPanel ${serviceName(p.serviceKey)} ${p.port}`);
    }
    await refreshAll();
    showNotice(t("panel.firewall.ports.opened"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

/**
 * ENGELLEME ONAYSIZ YAPILMAZ.
 *
 * "Engelle" düğmesi formun hemen yanındaydı ve tek tıkla uygulanıyordu. Windows'ta engelleme
 * kuralı izin kuralını EZER: yanlış porta basmak yönetim panelini ya da uzak masaüstünü kendine
 * kapatmak demektir ve panelden geri alınamaz. Onay metni HANGİ PORT ve HANGİ PROTOKOL için
 * kural kurulacağını açıkça yazar; korumalı bir portta ayrıca uyarır ve port numarasının
 * birebir yazılmasını ister (refleksle onaylanamasın).
 *
 * "İzin ver" onaysız kalır: kapıyı açmak geri alınabilir bir işlemdir.
 */
async function saveRule(action: "allow" | "block") {
  if (action === "block") {
    const port = form.port;
    const protocol = form.protocol;
    const reasonKey = protectedPortReasonKey(port);
    const impact = [
      t("panel.firewall.confirmBlockTarget", { port, protocol }),
      t("panel.firewall.confirmBlockRuleName", { name: form.ruleName.trim() || t("panel.firewall.ruleNameAuto") })
    ];
    if (reasonKey) {
      impact.push(t(reasonKey, { port }));
      impact.push(t("panel.firewall.confirmBlockServerRefuses"));
    }

    const onay = await confirm({
      title: t("panel.firewall.confirmBlockTitle", { port, protocol }),
      message: t("panel.firewall.confirmBlockMessage", { port, protocol }),
      impact,
      // Korumalı port: sunucu zaten reddeder, ama kullanıcı NE yaptığını bilerek ilerlesin.
      requireText: reasonKey ? String(port) : undefined,
      confirmLabel: t("panel.firewall.block")
    });
    if (!onay) {
      return;
    }
  }

  busy.value = true;
  try {
    const operation = action === "allow" ? allowPort : blockPort;
    await operation(form.port, form.protocol, form.ruleName);
    form.ruleName = "";
    showRuleForm.value = false;
    await refreshAll();
    showNotice(t("panel.firewall.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.firewall-panel,
.rule-list {
  display: grid;
  gap: 12px;
}

.form-actions {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: flex-start;
}

.module-head button,
.form-actions button {
  width: auto;
}

.rule-form {
  grid-template-columns: minmax(220px, 1fr) 130px 120px auto;
  align-items: end;
}

/* Onay kutusu satirlari DISARIDA: bunlar iki satirlik bir izgara degil, yan yana duran
   kutu+etikettir. Kapsamli `label` kurali element duzeyinde oldugu icin global
   `.check-field` kuralini eziyor, onay kutusu hucreyi kaplayip etiketi alt satira dusuruyordu.
   `:where()` KULLANILIYOR, duz `:not()` DEGIL: `:not(.x)` argumaninin ozgullugunu kurala EKLER
   ((0,1,1) -> (0,2,1)) ve boylece bilesenlerin kendi `.checkbox-field[data-v]` (0,2,0) kurallarini
   ezer -- SiteAliases'in onay kutulari tam olarak boyle bozulmustu. `:where()` her zaman SIFIR
   ozgullukte oldugu icin dislama yapar ama kurali guclendirmez. */
label:not(:where(.check-field, .checkbox-field, .checkbox-row)) {
  display: grid;
  gap: 6px;
}

.rule-row {
  grid-template-columns: minmax(220px, 1.5fr) 0.6fr 0.6fr 0.7fr 0.7fr;
}

.status {
  color: var(--accent-1);
  font-weight: 700;
}

.status.block {
  color: var(--danger-1);
}

.ports-block {
  display: grid;
  gap: 10px;
  padding-bottom: 16px;
  margin-bottom: 4px;
  border-bottom: 1px solid var(--panel-border);
}

.ports-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
  flex-wrap: wrap;
}

.ports-head h4 {
  margin: 0;
}

.ports-head .row-actions {
  display: flex;
  align-items: center;
  gap: 10px;
}

.ports-summary {
  font-weight: 600;
}

.cloud-note {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  padding: 8px 10px;
  border-radius: 6px;
  background: var(--warn-bg);
  border-left: 3px solid var(--warn-1);
}

.port-row {
  grid-template-columns: minmax(200px, 1.6fr) 0.8fr 1fr 0.9fr 1.2fr;
  align-items: center;
}

.svc {
  display: grid;
  gap: 4px;
}

.tags {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.chip {
  font-size: 11px;
  padding: 1px 8px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
}

.chip.req {
  color: var(--accent-1);
}

.chip.opt {
  color: var(--text-muted);
}

.mono {
  font-variant-numeric: tabular-nums;
}

.cell.ok {
  color: var(--accent-1);
  font-weight: 600;
}

.cell.bad {
  color: var(--danger-1);
  font-weight: 600;
}

.cell.warn {
  color: var(--warn-text);
  font-weight: 600;
}

.cell.muted {
  color: var(--text-muted);
}

.action-cell {
  display: flex;
  align-items: center;
  gap: 8px;
}

.dot {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  flex: none;
}

.dot.ok {
  background: var(--accent-1);
}

.dot.warn {
  background: var(--warn-1);
}

.dot.problem {
  background: var(--danger-1);
}

.dot.unused {
  background: var(--text-muted);
  opacity: 0.55;
}

.micro {
  width: auto;
  padding: 2px 12px;
  font-size: 12px;
}

@media (max-width: 900px) {
  .rule-form,
  .rule-row,
  .port-row {
    grid-template-columns: 1fr;
  }

}
</style>
