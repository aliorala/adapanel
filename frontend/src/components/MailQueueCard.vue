<template>
  <!--
    GİDEN POSTA KUYRUĞU — YALNIZ YÖNETİCİ (uç `Administrator` ile sınırlı).

    Kuyruk sunucudaki BÜTÜN kiracıların zarf gönderici/alıcı adreslerini içerir; bu yüzden
    ekranı çağıran taraf da yönetici koşulunu uygular (bkz. MailView).
  -->
  <section class="form-surface mail-queue-card">
    <div class="section-head">
      <div>
        <h3>{{ t("panel.mail.queueTitle") }}</h3>
        <p class="meta">{{ t("panel.mail.queueHint") }}</p>
      </div>
      <button type="button" class="secondary compact" :disabled="loading" @click="load">
        {{ loading ? t("panel.common.loading") : t("panel.common.refresh") }}
      </button>
    </div>

    <SectionLoadError v-if="loadError.message.value" :message="loadError.message.value" :busy="loading" @retry="load" />

    <p v-if="notice" :class="['notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <template v-if="queue">
      <!-- "Kurulu değil" ≠ "bağlanılamadı" ≠ "kuyruk boş". Üçünü tek boş listeye indirmek,
           sunucu erişilemezken yöneticiye "her şey yolunda" demek olurdu. -->
      <p v-if="!queue.installed" class="empty-state">{{ t("panel.mail.notInstalled") }}</p>
      <p v-else-if="!queue.configured" class="notice error" role="alert">{{ t("panel.mail.queueUnavailable") }}</p>

      <template v-else>
        <p class="meta queue-count" role="status">
          {{ t("panel.mail.queueShowing", { shown: queue.messages.length, total: queue.totalCount }) }}
        </p>

        <div class="data-table">
          <div class="data-row table-head queue-row">
            <span>{{ t("panel.mail.queueFrom") }}</span>
            <span>{{ t("panel.mail.queueTo") }}</span>
            <span>{{ t("panel.mail.queueCreated") }}</span>
            <span>{{ t("panel.mail.queueNextRetry") }}</span>
            <span>{{ t("panel.mail.queueSize") }}</span>
            <span>{{ t("panel.software.action") }}</span>
          </div>
          <div v-for="message in pager.items.value" :key="message.id" class="data-row queue-row">
            <strong class="queue-cell">{{ message.returnPath || t("panel.mail.queueBounce") }}</strong>
            <span class="queue-cell">{{ recipientSummary(message) }}</span>
            <span>{{ formatTime(message.createdAtUtc) }}</span>
            <span>{{ formatTime(message.nextRetryUtc) }}</span>
            <span>{{ formatSize(message.sizeBytes) }}</span>
            <div class="row-actions">
              <button type="button" class="secondary compact row-button" :disabled="busy" @click="openMessage(message.id)">
                {{ t("panel.mail.queueDetail") }}
              </button>
              <button type="button" class="secondary compact row-button danger" :disabled="busy" @click="removeMessage(message)">
                {{ t("panel.mail.queueDelete") }}
              </button>
            </div>
          </div>
          <p v-if="queue.messages.length === 0" class="empty-state">{{ t("panel.mail.queueEmpty") }}</p>
        </div>
        <TablePager :state="pager" :disabled="busy" />
      </template>
    </template>

    <section v-if="selected" class="form-surface queue-detail">
      <div class="section-head">
        <div>
          <h4>{{ t("panel.mail.queueMessageTitle") }}</h4>
          <p class="meta queue-id">{{ selected.id }}</p>
        </div>
        <button type="button" class="secondary compact" @click="selected = null">{{ t("panel.common.cancel") }}</button>
      </div>

      <dl class="queue-meta">
        <div><dt>{{ t("panel.mail.queueFrom") }}</dt><dd>{{ selected.returnPath || t("panel.mail.queueBounce") }}</dd></div>
        <div><dt>{{ t("panel.mail.queueCreated") }}</dt><dd>{{ formatTime(selected.createdAtUtc) }}</dd></div>
        <div><dt>{{ t("panel.mail.queueNextRetry") }}</dt><dd>{{ formatTime(selected.nextRetryUtc) }}</dd></div>
        <div><dt>{{ t("panel.mail.queueSize") }}</dt><dd>{{ formatSize(selected.sizeBytes) }}</dd></div>
        <div><dt>{{ t("panel.mail.queueSourceIp") }}</dt><dd>{{ selected.receivedFromIp || "—" }}</dd></div>
      </dl>

      <!-- ALICI BAŞINA DURUM: bir ileti bir alıcıya teslim edilip diğerine takılabilir. Durumu
           ileti düzeyine indirmek "kime gidemedi" sorusunu cevapsız bırakırdı. -->
      <div class="data-table">
        <div class="data-row table-head recipient-row">
          <span>{{ t("panel.mail.address") }}</span>
          <span>{{ t("panel.mail.queueStatus") }}</span>
          <span>{{ t("panel.mail.queueRetries") }}</span>
          <span>{{ t("panel.mail.queueNextRetry") }}</span>
          <span>{{ t("panel.mail.queueName") }}</span>
        </div>
        <template v-for="(recipient, index) in selected.recipients" :key="`${recipient.address}-${index}`">
          <div class="data-row recipient-row">
            <strong class="queue-cell">{{ recipient.address }}</strong>
            <span class="status-pill">{{ recipient.status || "—" }}</span>
            <span>{{ recipient.retryCount }}</span>
            <span>{{ formatTime(recipient.retryDueUtc) }}</span>
            <code class="queue-cell">{{ recipient.queueName || "—" }}</code>
          </div>
          <!--
            HAM DURUM METNİ: sunucunun durum nesnesinin alan adları doğrulanmadı, bu yüzden
            AYRIŞTIRILMAZ. Uydurma bir alandan okumak, hata sebebini sessizce boş gösterirdi —
            yani "sebebi oku" özelliği hiç çalışmadan var görünürdü.
          -->
          <details v-if="recipient.statusDetail" class="recipient-detail">
            <summary>{{ t("panel.mail.queueStatusRaw") }}</summary>
            <pre>{{ recipient.statusDetail }}</pre>
          </details>
        </template>
        <p v-if="selected.recipients.length === 0" class="empty-state">{{ t("panel.mail.queueNoRecipients") }}</p>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";

import {
  deleteMailQueueMessage,
  getMailQueue,
  getMailQueueMessage,
  MAIL_QUEUE_MAX_LIMIT,
  type MailQueue,
  type MailQueueMessage
} from "../api/mail";
import { useConfirm } from "./ConfirmDialog.vue";
import SectionLoadError from "./SectionLoadError.vue";
import TablePager from "./TablePager.vue";
import { useNotice, useSectionError } from "../composables/useNotice";
import { usePagination } from "../composables/usePagination";

const { t } = useI18n();
const { confirm } = useConfirm();
const { notice, noticeError, showNotice, showError } = useNotice();
const loadError = useSectionError();

const queue = ref<MailQueue | null>(null);
const selected = ref<MailQueueMessage | null>(null);
const loading = ref(false);
const busy = ref(false);

const messages = computed(() => queue.value?.messages ?? []);
const pager = usePagination(messages);

onMounted(load);

async function load() {
  loading.value = true;
  try {
    queue.value = await getMailQueue(MAIL_QUEUE_MAX_LIMIT);
    loadError.clear();
  } catch (error) {
    // Okunamadıysa BOŞ kuyruk göstermeyiz: "bilinmiyor" ile "bekleyen yok" aynı şey değil.
    queue.value = null;
    loadError.capture(error);
  } finally {
    loading.value = false;
  }
}

async function openMessage(id: string) {
  busy.value = true;
  try {
    selected.value = await getMailQueueMessage(id);
  } catch (error) {
    // 404 = ileti bu arada teslim edildi ya da silindi; sunucu anahtarı çevrilir.
    selected.value = null;
    showError(error);
  } finally {
    busy.value = false;
  }
}

/**
 * KUYRUKTAN SİLME YIKICIDIR: gönderilmemiş bir posta geri dönülemez biçimde yok olur.
 * Bu yüzden onay, neyin kaybolacağını (gönderen, alıcılar, boyut) TEK TEK yazar.
 */
async function removeMessage(message: MailQueueMessage) {
  const impact = [
    `${t("panel.mail.queueFrom")}: ${message.returnPath || t("panel.mail.queueBounce")}`,
    `${t("panel.mail.queueTo")}: ${recipientSummary(message)}`,
    `${t("panel.mail.queueSize")}: ${formatSize(message.sizeBytes)}`
  ];

  await confirm({
    title: t("panel.mail.queueDeleteTitle"),
    message: t("panel.mail.queueDeleteConfirm"),
    impact,
    action: async () => {
      busy.value = true;
      try {
        await deleteMailQueueMessage(message.id);
        if (selected.value?.id === message.id) {
          selected.value = null;
        }
        showNotice(t("mail.queue_message_deleted"));
        await load();
      } catch (error) {
        showError(error);
      } finally {
        busy.value = false;
      }
    }
  });
}

function recipientSummary(message: MailQueueMessage): string {
  const addresses = message.recipients.map((recipient) => recipient.address).filter(Boolean);
  return addresses.length > 0 ? addresses.join(", ") : "—";
}

function formatTime(value: string | null): string {
  if (!value) {
    return "—";
  }
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

function formatSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return "—";
  }
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const kb = bytes / 1024;
  return kb < 1024 ? `${kb.toFixed(1)} KB` : `${(kb / 1024).toFixed(1)} MB`;
}
</script>

<style scoped>
.mail-queue-card {
  display: grid;
  gap: 10px;
}

.queue-row {
  grid-template-columns: minmax(0, 1.4fr) minmax(0, 1.6fr) minmax(0, 1fr) minmax(0, 1fr) 80px auto;
}

.recipient-row {
  grid-template-columns: minmax(0, 1.6fr) minmax(0, 0.9fr) 70px minmax(0, 1fr) minmax(0, 0.9fr);
}

.queue-cell {
  min-width: 0;
  word-break: break-word;
}

.queue-count {
  font-variant-numeric: tabular-nums;
}

.queue-detail {
  margin-top: 6px;
  display: grid;
  gap: 10px;
}

.queue-id {
  word-break: break-all;
}

.queue-meta {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 8px;
  margin: 0;
}

.queue-meta dt {
  font-size: 0.78rem;
  color: var(--text-muted);
}

.queue-meta dd {
  margin: 2px 0 0;
  word-break: break-word;
}

.recipient-detail {
  padding: 4px 10px 8px;
  font-size: 0.78rem;
  color: var(--text-muted);
}

.recipient-detail pre {
  margin: 6px 0 0;
  padding: 8px 10px;
  max-height: 16rem;
  overflow: auto;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
