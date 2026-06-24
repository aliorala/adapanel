<template>
  <section class="panel module-shell mail-panel">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.mail.title") }}</h2>
        <p class="meta">{{ t("panel.mail.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">{{ t("panel.mail.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>
    <details v-if="noticeError && noticeDetails" class="notice-details">
      <summary>{{ t("panel.common.technicalDetails") }}</summary>
      <pre>{{ noticeDetails }}</pre>
    </details>
    <p v-if="workspace?.statusMessage" class="notice error">{{ workspace.statusMessage }}</p>

    <div v-if="loading && !workspace" class="empty-state mail-loading">
      <span class="mail-spinner" aria-hidden="true"></span>
      {{ t("panel.mail.loadingWorkspace") }}
    </div>

    <div v-if="workspace && !workspace.installed" class="empty-state">{{ t("panel.mail.notInstalled") }}</div>

    <section v-else-if="workspace && !workspace.configured" class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.mail.configureTitle") }}</h3>
          <p class="meta">{{ t("panel.mail.configureHint") }}</p>
        </div>
      </div>
      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.mail.adminPassword") }}</span>
          <input v-model="adminPassword" type="password" autocomplete="new-password" />
        </label>
      </div>
      <div class="row-actions">
        <button class="compact" :disabled="busy || !adminPassword" @click="configure">{{ t("panel.mail.connect") }}</button>
      </div>
    </section>

    <template v-else-if="workspace">
      <!-- panel: Posta ana ekrani = web sitesi alan adlari listesi. Ayri "mail alan adi
           olustur" adimi yoktur; domaine tiklayinca posta otomatik etkinlesir ve hesap
           yonetimine dusulur. -->
      <template v-if="!selectedDomain">
        <p class="meta mail-list-hint">{{ t("panel.mail.listHint") }}</p>
        <div class="data-table">
          <div class="data-row table-head maildomain-row">
            <span>{{ t("panel.mail.domain") }}</span>
            <span>{{ t("panel.mail.mailboxes") }}</span>
            <span>{{ t("panel.sites.state") }}</span>
            <span>{{ t("panel.software.action") }}</span>
          </div>
          <div v-for="item in mailDomains" :key="item.name" class="data-row maildomain-row">
            <strong>{{ item.name }}</strong>
            <span>{{ item.active ? item.accountCount : "—" }}</span>
            <span :class="['status-pill', item.active ? 'ok' : 'warn']">
              {{ item.active ? t("panel.status.enabled") : t("panel.mail.mailOff") }}
            </span>
            <button class="compact row-button" :disabled="busy" @click="openDomain(item.name)">
              {{ t("panel.mail.manage") }}
            </button>
          </div>
          <p v-if="mailDomains.length === 0" class="empty-state">{{ t("panel.mail.noSiteDomainsList") }}</p>
        </div>
      </template>

      <div v-if="selectedDomain" class="module-toolbar mail-domain-bar">
        <button class="secondary compact" @click="backToDomains">← {{ t("panel.mail.allDomains") }}</button>
        <strong class="current-domain">{{ selectedDomain }}</strong>
        <button class="secondary compact danger" :disabled="busy" @click="removeDomain">
          {{ t("panel.mail.deleteDomain") }}
        </button>
      </div>

      <div v-if="selectedDomain" class="form-surface quota-strip">
        <label class="inline-field">
          <span class="field-label">{{ t("panel.mail.domainQuota") }}</span>
          <input v-model.number="domainQuota" type="number" min="0" />
          <span class="hint">MB ({{ t("panel.mail.zeroUnlimited") }})</span>
        </label>
        <button class="secondary compact" :disabled="busy" @click="saveDomainQuota">{{ t("panel.common.save") }}</button>
      </div>

      <template v-if="selectedDomain">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.mailboxes") }}</h3>
            <p class="meta">{{ selectedDomain }}</p>
          </div>
          <button class="compact" @click="showAccountForm = !showAccountForm">
            {{ showAccountForm ? t("panel.common.cancel") : t("panel.mail.newMailbox") }}
          </button>
        </div>

        <div v-if="showAccountForm" class="form-surface">
          <div class="form-grid">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.address") }}</span>
              <div class="address-field">
                <input v-model.trim="accountForm.localPart" placeholder="info" />
                <span>@{{ selectedDomain }}</span>
              </div>
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.password") }}</span>
              <input v-model="accountForm.password" type="password" minlength="8" autocomplete="new-password" />
              <small class="field-hint">{{ t("panel.mail.passwordHint") }}</small>
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.maxSize") }}</span>
              <input v-model.number="accountForm.maxSizeMB" type="number" min="0" />
            </label>
          </div>
          <div class="row-actions">
            <button class="compact" :disabled="busy || !accountForm.localPart || !accountForm.password" @click="addAccount">
              {{ t("panel.mail.create") }}
            </button>
          </div>
        </div>

        <div class="data-table">
          <div class="data-row table-head mail-row">
            <span>{{ t("panel.mail.address") }}</span>
            <span>{{ t("panel.mail.maxSize") }}</span>
            <span>{{ t("panel.sites.state") }}</span>
            <span>{{ t("panel.software.action") }}</span>
          </div>
          <div v-for="account in workspace.accounts" :key="account.address" class="data-row mail-row">
            <strong>{{ account.address }}</strong>
            <span>{{ account.currentSizeMB }} / {{ account.maxSizeMB ? `${account.maxSizeMB} MB` : "∞" }}</span>
            <span :class="['status-pill', account.active ? 'ok' : 'warn']">
              {{ account.active ? t("panel.status.enabled") : t("panel.status.disabled") }}
            </span>
            <div class="row-actions">
              <button class="secondary compact row-button" :disabled="busy" @click="changeAccountQuota(account.address, account.maxSizeMB)">
                {{ t("panel.mail.quota") }}
              </button>
              <button class="secondary compact row-button" :disabled="busy" @click="changePassword(account.address)">
                {{ t("panel.mail.changePassword") }}
              </button>
              <button class="secondary compact row-button" :disabled="busy" @click="openAutoResponder(account.address)">
                {{ t("panel.mail.autoResponder") }}
              </button>
              <button class="secondary compact row-button danger" :disabled="busy" @click="removeAccount(account.address)">
                {{ t("panel.mail.delete") }}
              </button>
            </div>
          </div>
          <p v-if="workspace.accounts.length === 0" class="empty-state">{{ t("panel.mail.noMailboxes") }}</p>
        </div>

        <!-- Webmail + posta istemci ayarları (profesyonel paneller gibi). -->
        <section class="form-surface">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.mail.webmailTitle") }}</h3>
              <p class="meta">{{ t("panel.mail.webmailHint") }}</p>
            </div>
            <button class="compact" :disabled="busy" @click="openWebmail">
              {{ busy && webmailOpening ? t("panel.mail.webmailOpening") : t("panel.mail.webmailOpen") }}
            </button>
          </div>

          <div class="client-settings">
            <h4>{{ t("panel.mail.clientSettingsTitle") }}</h4>
            <p class="meta">{{ t("panel.mail.clientSettingsHint") }}</p>
            <div class="data-table">
              <div class="data-row table-head client-row">
                <span>{{ t("panel.mail.clientField") }}</span>
                <span>{{ t("panel.mail.clientServer") }}</span>
                <span>{{ t("panel.mail.clientPort") }}</span>
                <span>{{ t("panel.mail.clientSecurity") }}</span>
              </div>
              <div class="data-row client-row">
                <strong>{{ t("panel.mail.clientIncoming") }} (IMAP)</strong>
                <span class="copyable" @click="copyDnsValue(mailHost)">{{ mailHost }}</span>
                <span>143</span>
                <span>STARTTLS</span>
              </div>
              <div class="data-row client-row">
                <strong>{{ t("panel.mail.clientOutgoing") }} (SMTP)</strong>
                <span class="copyable" @click="copyDnsValue(mailHost)">{{ mailHost }}</span>
                <span>587</span>
                <span>STARTTLS</span>
              </div>
            </div>
            <p class="meta client-username">
              {{ t("panel.mail.clientUsername") }}:
              <strong>{{ workspace.accounts[0]?.address || `info@${selectedDomain}` }}</strong>
              — {{ t("panel.mail.clientUsernameHint") }}
            </p>
          </div>
        </section>

        <!-- Mail teşhis: panel MX/A/SPF/DKIM/DMARC/PTR'yi kendisi kontrol eder (profesyonel paneller gibi). -->
        <section class="form-surface diagnostics-card">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.mail.diag.title") }}</h3>
              <p class="meta">{{ t("panel.mail.diag.hint") }}</p>
            </div>
            <button class="compact" :disabled="busy || diagLoading" @click="runDiagnostics">
              {{ diagLoading ? t("panel.mail.diag.checking") : t("panel.mail.diag.check") }}
            </button>
          </div>

          <div v-if="diagnostics" class="diag-grid">
            <div v-for="item in diagnostics.checks" :key="item.key" :class="['diag-item', item.status]">
              <span class="diag-dot"></span>
              <div class="diag-body">
                <div class="diag-row">
                  <strong>{{ t(`panel.mail.diag.label.${item.key}`) }}</strong>
                  <span :class="['status-pill', item.status === 'ok' ? 'ok' : item.status === 'fail' ? 'danger' : 'warn']">
                    {{ t(`panel.mail.diag.state.${item.status}`) }}
                  </span>
                </div>
                <small v-if="item.status !== 'ok'" class="diag-detail">
                  <span>{{ t("panel.mail.diag.expected") }}: <code>{{ item.expected || "—" }}</code></span>
                  <span v-if="item.found"> · {{ t("panel.mail.diag.found") }}: <code>{{ item.found }}</code></span>
                </small>
              </div>
            </div>
          </div>
          <p v-else class="meta">{{ t("panel.mail.diag.empty") }}</p>
        </section>

        <section v-if="autoResponderFor" class="form-surface">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.mail.autoResponderTitle") }}</h3>
              <p class="meta">{{ autoResponderFor }}</p>
            </div>
          </div>
          <label class="checkbox">
            <input v-model="autoResponderForm.enabled" type="checkbox" />
            <span>{{ t("panel.mail.autoResponderEnabled") }}</span>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.autoResponderMessage") }}</span>
            <textarea v-model="autoResponderForm.message" rows="5"></textarea>
          </label>
          <div class="row-actions">
            <button class="compact" :disabled="busy" @click="saveAutoResponder">{{ t("panel.common.save") }}</button>
            <button class="secondary compact" @click="autoResponderFor = ''">{{ t("panel.common.cancel") }}</button>
          </div>
        </section>

        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.aliases") }}</h3>
            <p class="meta">{{ t("panel.mail.aliasesHint") }}</p>
          </div>
          <button class="compact" @click="showAliasForm = !showAliasForm">
            {{ showAliasForm ? t("panel.common.cancel") : t("panel.mail.newAlias") }}
          </button>
        </div>

        <div v-if="showAliasForm" class="form-surface">
          <div class="form-grid">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.aliasAddress") }}</span>
              <div class="address-field">
                <input v-model.trim="aliasForm.localPart" placeholder="sales" />
                <span>@{{ selectedDomain }}</span>
              </div>
              <small class="field-hint">{{ t("panel.mail.catchAllHint") }}</small>
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.aliasTarget") }}</span>
              <input v-model.trim="aliasForm.target" type="email" placeholder="info@example.com" />
            </label>
          </div>
          <div class="row-actions">
            <button class="compact" :disabled="busy || !aliasForm.localPart || !aliasForm.target" @click="addAlias">
              {{ t("panel.mail.create") }}
            </button>
          </div>
        </div>

        <div class="data-table">
          <div class="data-row table-head alias-row">
            <span>{{ t("panel.mail.aliasAddress") }}</span>
            <span>{{ t("panel.mail.aliasTarget") }}</span>
            <span>{{ t("panel.software.action") }}</span>
          </div>
          <div v-for="alias in workspace.aliases" :key="alias.address" class="data-row alias-row">
            <strong>{{ alias.address }}</strong>
            <span>{{ alias.target }}</span>
            <button class="secondary compact row-button danger" :disabled="busy" @click="removeAlias(alias.address)">
              {{ t("panel.mail.delete") }}
            </button>
          </div>
          <p v-if="workspace.aliases.length === 0" class="empty-state">{{ t("panel.mail.noAliases") }}</p>
        </div>

        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.lists") }}</h3>
            <p class="meta">{{ t("panel.mail.listsHint") }}</p>
          </div>
          <button class="compact" @click="showListForm = !showListForm">
            {{ showListForm ? t("panel.common.cancel") : t("panel.mail.newList") }}
          </button>
        </div>

        <div v-if="showListForm" class="form-surface">
          <div class="form-grid">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.listAddress") }}</span>
              <div class="address-field">
                <input v-model.trim="listForm.localPart" placeholder="duyuru" />
                <span>@{{ selectedDomain }}</span>
              </div>
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.listRecipients") }}</span>
              <textarea v-model="listForm.recipients" rows="3" :placeholder="t('panel.mail.listRecipientsPlaceholder')"></textarea>
              <small class="field-hint">{{ t("panel.mail.listRecipientsHint") }}</small>
            </label>
          </div>
          <div class="row-actions">
            <button class="compact" :disabled="busy || !listForm.localPart || !parseRecipients(listForm.recipients).length" @click="addList">
              {{ t("panel.mail.create") }}
            </button>
          </div>
        </div>

        <div class="data-table">
          <div class="data-row table-head list-row">
            <span>{{ t("panel.mail.listAddress") }}</span>
            <span>{{ t("panel.mail.listRecipients") }}</span>
            <span>{{ t("panel.software.action") }}</span>
          </div>
          <div v-for="list in workspace.lists" :key="list.address" class="data-row list-row">
            <strong>{{ list.address }}</strong>
            <span class="list-recipients">{{ list.recipients.join(", ") || "—" }}</span>
            <div class="row-actions">
              <button class="secondary compact row-button" :disabled="busy" @click="editListRecipients(list.address, list.recipients)">
                {{ t("panel.mail.listEdit") }}
              </button>
              <button class="secondary compact row-button danger" :disabled="busy" @click="removeList(list.address)">
                {{ t("panel.mail.delete") }}
              </button>
            </div>
          </div>
          <p v-if="workspace.lists.length === 0" class="empty-state">{{ t("panel.mail.noLists") }}</p>
        </div>

        <div class="row-actions mail-advanced-toggle">
          <button type="button" class="secondary compact" @click="showMailAdvanced = !showMailAdvanced">
            {{ showMailAdvanced ? t("panel.mail.hideSettings") : t("panel.mail.showSettings") }}
          </button>
        </div>

        <template v-if="showMailAdvanced">
        <template v-if="isAdmin">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.listenersTitle") }}</h3>
            <p class="meta">{{ t("panel.mail.listenersHint") }}</p>
          </div>
          <button class="secondary compact" :disabled="busy" @click="loadListeners">{{ t("panel.mail.listenersShow") }}</button>
        </div>
        <div v-if="listeners" class="data-table">
          <div class="data-row table-head listener-row">
            <span>{{ t("panel.mail.listenerName") }}</span>
            <span>{{ t("panel.mail.listenerProtocol") }}</span>
            <span>{{ t("panel.mail.listenerBinds") }}</span>
          </div>
          <div v-for="listener in listeners" :key="listener.name" class="data-row listener-row">
            <strong>{{ listener.name }}</strong>
            <span>{{ listener.protocol || "—" }}</span>
            <code class="listener-binds">{{ listener.binds.join(", ") || "—" }}</code>
          </div>
          <p v-if="listeners.length === 0" class="empty-state">{{ t("panel.mail.noListeners") }}</p>
        </div>
        </template>

        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.dnsTitle") }}</h3>
            <p class="meta">{{ t("panel.mail.dnsHint") }}</p>
          </div>
          <div class="row-actions">
            <button class="secondary compact" :disabled="busy" @click="loadDns">{{ t("panel.mail.dnsShow") }}</button>
            <button v-if="dns" class="compact" :disabled="busy" @click="applyDns">{{ t("panel.mail.dnsApply") }}</button>
          </div>
        </div>

        <div v-if="dns" class="data-table">
          <div class="data-row table-head dns-row">
            <span>{{ t("panel.mail.dnsType") }}</span>
            <span>{{ t("panel.mail.dnsHost") }}</span>
            <span>{{ t("panel.mail.dnsValue") }}</span>
            <span>{{ t("panel.software.action") }}</span>
          </div>
          <div v-for="record in dns.records" :key="record.purpose + record.host" class="data-row dns-row">
            <span class="status-pill">{{ record.type }}{{ record.priority != null ? ` ${record.priority}` : "" }}</span>
            <strong>{{ record.host }}</strong>
            <code class="dns-value">{{ record.value || t("panel.mail.dnsValueMissing") }}</code>
            <button class="secondary compact row-button" :disabled="!record.value" @click="copyDnsValue(record.value)">
              {{ t("panel.mail.dnsCopy") }}
            </button>
          </div>
          <p v-if="!dns.serverAddress" class="empty-state">{{ t("panel.mail.dnsNoServerAddress") }}</p>
        </div>

        <template v-if="isAdmin">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.rdns.title") }}</h3>
            <p class="meta">{{ t("panel.mail.rdns.hint") }}</p>
          </div>
          <div class="row-actions">
            <input v-model.trim="rdnsHost" class="rdns-input" :placeholder="`mail.${selectedDomain}`" />
            <button class="secondary compact" :disabled="busy" @click="runRdns">{{ t("panel.mail.rdns.check") }}</button>
          </div>
        </div>

        <div v-if="rdns" class="rdns-result">
          <div :class="['rdns-status', rdnsClass]">{{ t(rdns.statusMessage) }}</div>
          <dl class="rdns-meta">
            <div><dt>{{ t("panel.mail.rdns.host") }}</dt><dd>{{ rdns.expectedHostName || "-" }}</dd></div>
            <div><dt>{{ t("panel.mail.rdns.ip") }}</dt><dd>{{ rdns.ipAddress || "-" }}</dd></div>
            <div><dt>{{ t("panel.mail.rdns.current") }}</dt><dd>{{ rdns.ptrHostName || t("panel.mail.rdns.none") }}</dd></div>
          </dl>
          <p class="meta rdns-guide">{{ t("panel.mail.rdns.guide") }}</p>
        </div>

        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.certTitle") }}</h3>
            <p class="meta">{{ t("panel.mail.certHint") }}</p>
          </div>
        </div>

        <div class="form-surface">
          <p v-if="certificate?.active" class="meta">
            <span :class="['status-pill', certificate.source === 'letsencrypt' ? 'ok' : 'warn']">
              {{ certificate.source === "letsencrypt" ? "Let's Encrypt" : t("panel.mail.certSelfSigned") }}
            </span>
            {{ certificate.hostName }}
            <template v-if="certificate.notAfterUtc">
              - {{ t("panel.mail.certExpires") }}: {{ new Date(certificate.notAfterUtc).toLocaleDateString() }}
            </template>
          </p>
          <p v-else class="meta">{{ t("panel.mail.certNone") }}</p>
          <div class="form-grid">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.certHost") }}</span>
              <input v-model.trim="certForm.hostName" :placeholder="`mail.${selectedDomain}`" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.certEmail") }}</span>
              <input v-model.trim="certForm.contactEmail" type="email" placeholder="admin@example.com" />
            </label>
          </div>
          <div class="row-actions">
            <button class="compact" :disabled="busy" @click="issueCert">{{ t("panel.mail.certIssue") }}</button>
          </div>
        </div>
        </template>
        </template>
      </template>

      <section v-if="spam && spam.installed && spam.configured" class="form-surface">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.spam") }}</h3>
            <p class="meta">{{ t("panel.mail.spamHint") }}</p>
          </div>
          <label class="toggle-field">
            <input v-model="spam.enabled" type="checkbox" />
            <span>{{ t("panel.mail.spamEnabled") }}</span>
          </label>
        </div>
        <button type="button" class="secondary compact" @click="spamAdvanced = !spamAdvanced">
          {{ spamAdvanced ? t("panel.sites.hideAdvanced") : t("panel.sites.showAdvanced") }}
        </button>
        <div v-if="spamAdvanced" class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.spamScoreSpam") }}</span>
            <input v-model.number="spam.scoreSpam" type="number" min="0" max="1000" step="0.5" />
            <small class="field-hint">{{ t("panel.mail.spamScoreSpamHint") }}</small>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.spamScoreReject") }}</span>
            <input v-model.number="spam.scoreReject" type="number" min="0" max="1000" step="0.5" />
            <small class="field-hint">{{ t("panel.mail.spamZeroOff") }}</small>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.spamScoreDiscard") }}</span>
            <input v-model.number="spam.scoreDiscard" type="number" min="0" max="1000" step="0.5" />
            <small class="field-hint">{{ t("panel.mail.spamZeroOff") }}</small>
          </label>
          <label class="toggle-field">
            <input v-model="spam.greylistEnabled" type="checkbox" />
            <span>{{ t("panel.mail.spamGrey") }}</span>
          </label>
          <label v-if="spam.greylistEnabled" class="stacked-field">
            <span class="field-label">{{ t("panel.mail.spamGreyMinutes") }}</span>
            <input v-model.number="spam.greylistMinutes" type="number" min="1" max="10080" />
          </label>
          <label class="toggle-field">
            <input v-model="spam.trustContacts" type="checkbox" />
            <span>{{ t("panel.mail.spamTrustContacts") }}</span>
          </label>
          <label class="toggle-field">
            <input v-model="spam.trustReplies" type="checkbox" />
            <span>{{ t("panel.mail.spamTrustReplies") }}</span>
          </label>
        </div>
        <div class="row-actions">
          <button class="compact" :disabled="busy" @click="saveSpam">{{ t("panel.common.save") }}</button>
        </div>
      </section>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import {
  applyMailDns,
  checkRdns,
  configureMail,
  createMailAccount,
  createMailAlias,
  createMailDomain,
  createMailList,
  deleteMailList,
  setMailListRecipients,
  deleteMailAccount,
  deleteMailAlias,
  deleteMailDomain,
  getMailAutoResponder,
  getMailCertificate,
  getMailDns,
  getMailDiagnostics,
  getMailListeners,
  getMailSpam,
  getMailWorkspace,
  setMailAutoResponder,
  issueMailCertificate,
  setMailAccountPassword,
  setMailAccountQuota,
  setMailDomainQuota,
  updateMailSpam,
  openWebmail as openWebmailApi,
  type MailCertificate,
  type MailDiagnostics,
  type MailDns,
  type MailListener,
  type MailSpamSettings,
  type MailWorkspace,
  type RdnsCheck
} from "../api/mail";
import { toReachableUrl } from "../utils/url";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

// Site bağlamından (Web Siteleri > site > Mail) gelindiğinde o sitenin alan adını
// önceden seçmek için opsiyonel prop (profesyonel paneller gibi domain-içi Mail sekmesi).
const props = defineProps<{ domain?: string }>();

const { t } = useI18n();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
// Sunucu seviyesi mail araçları (dinleyiciler, rDNS, sertifika, spam) yalnız admin'e gösterilir;
// müşteri/bayide bu uçlar 403 döner.
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const workspace = ref<MailWorkspace | null>(null);
const selectedDomain = ref((props.domain ?? "").toLowerCase());
const adminPassword = ref("");
const accountForm = reactive({ localPart: "", password: "", maxSizeMB: 1024 });
const autoResponderFor = ref("");
const autoResponderForm = reactive({ enabled: false, message: "" });
const aliasForm = reactive({ localPart: "", target: "" });
const domainQuota = ref(0);
const spam = ref<MailSpamSettings | null>(null);
const dns = ref<MailDns | null>(null);
const rdns = ref<RdnsCheck | null>(null);
const rdnsHost = ref("");
const spamAdvanced = ref(false);
const certificate = ref<MailCertificate | null>(null);
const certForm = reactive({ hostName: "", contactEmail: "" });
const certAutoTried = ref(false);
const webmailOpening = ref(false);
const mailHost = computed(() => `mail.${selectedDomain.value}`);
const diagnostics = ref<MailDiagnostics | null>(null);
const diagLoading = ref(false);
const showAccountForm = ref(false);
const showAliasForm = ref(false);
const showListForm = ref(false);
const listForm = reactive({ localPart: "", recipients: "" });
const showMailAdvanced = ref(false);
const listeners = ref<MailListener[] | null>(null);
const loading = ref(false);
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const noticeDetails = ref("");

// panel: posta domain listesi = mevcut mail domainleri (Stalwart) + henuz postasi acilmamis
// web sitesi alan adlari birlesimi. Boylece kullanici ayri bir "olustur" adimi gormeden
// listeden secip yonetir; postasi yoksa secince otomatik etkinlesir (openDomain).
const mailDomains = computed(() => {
  const map = new Map<string, { name: string; accountCount: number; active: boolean }>();
  for (const domain of workspace.value?.domains ?? []) {
    map.set(domain.name.toLowerCase(), { name: domain.name, accountCount: domain.accountCount, active: true });
  }
  for (const site of sitesStore.sites) {
    for (const binding of site.bindings ?? []) {
      const host = (binding.hostName ?? "").trim().toLowerCase();
      if (!host || !host.includes(".")) continue;
      const bare = host.startsWith("www.") ? host.slice(4) : host;
      if (!map.has(bare)) map.set(bare, { name: bare, accountCount: 0, active: false });
    }
  }
  return Array.from(map.values()).sort((a, b) => a.name.localeCompare(b.name));
});

// Listeden bir alan adi secilince: postasi yoksa otomatik olustur (profesyonel paneller gibi), sonra
// o domaini yukle ve DNS kayitlarini hazirla.
async function openDomain(name: string) {
  const lower = name.toLowerCase();
  await run(async () => {
    const exists = (workspace.value?.domains ?? []).some((item) => item.name.toLowerCase() === lower);
    if (!exists) {
      apply(await createMailDomain(lower));
    }
    selectedDomain.value = lower;
    // Domaine özgü durumları sıfırla ki önceki domainin sonucu yanlışlıkla görünmesin.
    diagnostics.value = null;
    certForm.hostName = "";
    apply(await getMailWorkspace(lower));
    dns.value = await getMailDns(lower).catch(() => null);
  });
}

function backToDomains() {
  selectedDomain.value = "";
  dns.value = null;
  rdns.value = null;
  rdnsHost.value = "";
  diagnostics.value = null;
}

onMounted(async () => {
  if (sitesStore.sites.length === 0) {
    await sitesStore.fetchSites().catch(() => undefined);
  }
  await refresh();
  await ensureSiteDomain();
});

// profesyonel paneller gibi: site bağlamından gelince (domain prop) o alan adı için mail otomatik etkin olsun;
// kullanıcı ayrıca "alan adı ekle" adımı görmesin.
async function ensureSiteDomain() {
  const target = (props.domain ?? "").trim().toLowerCase();
  if (!target || !workspace.value?.installed || workspace.value?.statusMessage) {
    return;
  }
  const exists = workspace.value.domains.some((item) => item.name.toLowerCase() === target);
  if (exists) {
    return;
  }
  try {
    apply(await createMailDomain(target));
    selectedDomain.value = target;
    apply(await getMailWorkspace(target));
  } catch {
    // Otomatik oluşturma başarısızsa kullanıcı listeden elle seçebilir.
  }
}

async function refresh() {
  loading.value = true;
  try {
    apply(await getMailWorkspace(selectedDomain.value || undefined));
  } catch (error) {
    showError(error);
  } finally {
    loading.value = false;
  }
}

const rdnsClass = computed(() => {
  if (!rdns.value) {
    return "";
  }
  if (rdns.value.statusMessage === "mail.rdns.ok") {
    return "ok";
  }
  return rdns.value.statusMessage === "mail.rdns.match_no_fcrdns" ? "warn" : "danger";
});

async function runRdns() {
  await run(async () => {
    const host = rdnsHost.value || `mail.${selectedDomain.value}`;
    rdns.value = await checkRdns(host);
  });
}

async function loadDns() {
  await run(async () => {
    dns.value = await getMailDns(selectedDomain.value);
  });
}

async function loadListeners() {
  await run(async () => {
    listeners.value = await getMailListeners();
  });
}

async function applyDns() {
  await run(async () => {
    const result = await applyMailDns(selectedDomain.value);
    showNotice(t("panel.mail.dnsAppliedNotice", { count: result.data.appliedCount, zone: result.data.zoneName }), false);
  });
}

async function loadCertificate() {
  try {
    const result = await getMailCertificate();
    certificate.value = result;
    // Formu otomatik doldur ki kullanıcı mail sunucu adını/e-postayı elle yazmasın.
    if (!certForm.hostName) certForm.hostName = `mail.${selectedDomain.value}`;
    if (!certForm.contactEmail && result.suggestedContactEmail) {
      certForm.contactEmail = result.suggestedContactEmail;
    }
    // profesyonel paneller gibi: panel sertifikası yoksa ve iletişim e-postası zaten biliniyorsa
    // (ör. site SSL'i alınırken kaydedildi) sertifikayı bir kez otomatik al — uğraştırma.
    // Sonuç (LE ya da self-signed) yüklenince active=true olur, bu yüzden döngüye girmez.
    if (!result.active && result.suggestedContactEmail && !certAutoTried.value) {
      certAutoTried.value = true;
      void autoIssueCert();
    }
  } catch {
    certificate.value = null;
  }
}

// Sayfa açılışında sessiz, en iyi çaba issuance: başarısızsa form zaten dolu, kullanıcı
// "Sertifika al ve yükle" ile tek tıkla tekrar dener (hata toast'ı ile rahatsız etme).
async function autoIssueCert() {
  try {
    certificate.value = await issueMailCertificate(
      selectedDomain.value,
      certForm.hostName || `mail.${selectedDomain.value}`,
      certForm.contactEmail);
    if (certificate.value.source === "letsencrypt") {
      showNotice(t("panel.mail.certIssued"), false);
    }
  } catch {
    // sessiz geç
  }
}

async function issueCert() {
  await run(async () => {
    certificate.value = await issueMailCertificate(
      selectedDomain.value,
      certForm.hostName || `mail.${selectedDomain.value}`,
      certForm.contactEmail);
    showNotice(
      certificate.value.source === "letsencrypt"
        ? t("panel.mail.certIssued")
        : t("panel.mail.certIssuedSelfSigned"),
      false);
  });
}

// Tek tık webmail (profesyonel paneller gibi): Roundcube kurulu/yayında değilse panel otomatik kurar,
// sonra yeni sekmede açar. Kullanıcı tam e-posta adresi + şifresiyle giriş yapar.
async function openWebmail() {
  // Pop-up engelleyiciye takılmamak için sekmeyi tıklama anında aç, URL gelince yönlendir.
  const tab = window.open("", "_blank");
  webmailOpening.value = true;
  await run(async () => {
    try {
      const result = await openWebmailApi();
      const url = toReachableUrl(result.url);
      if (tab) {
        tab.location.href = url;
      } else {
        window.location.href = url;
      }
    } catch (error) {
      if (tab) tab.close();
      throw error;
    }
  });
  webmailOpening.value = false;
}

// Panel domainin yayınlanmış mail kayıtlarını canlı kontrol eder; kullanıcı eksiği kendi aramaz.
async function runDiagnostics() {
  if (!selectedDomain.value) return;
  diagLoading.value = true;
  try {
    diagnostics.value = await getMailDiagnostics(selectedDomain.value);
  } catch (error) {
    showError(error);
  } finally {
    diagLoading.value = false;
  }
}

async function copyDnsValue(value: string) {
  try {
    await navigator.clipboard.writeText(value);
    showNotice(t("panel.mail.dnsCopied"), false);
  } catch {
    window.prompt(t("panel.mail.dnsCopy"), value);
  }
}

async function configure() {
  await run(async () => {
    apply(await configureMail(adminPassword.value));
    adminPassword.value = "";
    showNotice(t("panel.mail.configured"), false);
  });
}

async function removeDomain() {
  if (!selectedDomain.value || !window.confirm(t("panel.mail.confirmDeleteDomain"))) return;
  await run(async () => {
    apply(await deleteMailDomain(selectedDomain.value));
    selectedDomain.value = "";
    showNotice(t("panel.mail.domainDeleted"), false);
  });
}

async function addAccount() {
  await run(async () => {
    apply(await createMailAccount(selectedDomain.value, accountForm.localPart, accountForm.password, accountForm.maxSizeMB));
    accountForm.localPart = "";
    accountForm.password = "";
    showAccountForm.value = false;
    showNotice(t("panel.mail.mailboxCreated"), false);
  });
}

async function removeAccount(address: string) {
  if (!window.confirm(t("panel.mail.confirmDelete"))) return;
  await run(async () => {
    apply(await deleteMailAccount(address));
    showNotice(t("panel.mail.mailboxDeleted"), false);
  });
}

async function openAutoResponder(address: string) {
  autoResponderFor.value = address;
  autoResponderForm.enabled = false;
  autoResponderForm.message = "";
  try {
    const data = await getMailAutoResponder(address);
    autoResponderForm.enabled = data.enabled;
    autoResponderForm.message = data.message;
  } catch (error) {
    showError(error);
  }
}

async function saveAutoResponder() {
  if (!autoResponderFor.value) return;
  await run(async () => {
    await setMailAutoResponder(autoResponderFor.value, autoResponderForm.enabled, autoResponderForm.message);
    autoResponderFor.value = "";
    showNotice(t("panel.mail.autoResponderSaved"), false);
  });
}

async function changePassword(address: string) {
  const password = window.prompt(t("panel.mail.enterNewPassword"));
  if (!password) return;
  await run(async () => {
    apply(await setMailAccountPassword(address, password));
    showNotice(t("panel.mail.passwordChanged"), false);
  });
}

async function addAlias() {
  await run(async () => {
    apply(await createMailAlias(selectedDomain.value, aliasForm.localPart, aliasForm.target));
    aliasForm.localPart = "";
    aliasForm.target = "";
    showAliasForm.value = false;
    showNotice(t("panel.mail.aliasCreated"), false);
  });
}

async function removeAlias(address: string) {
  if (!window.confirm(t("panel.mail.confirmDelete"))) return;
  await run(async () => {
    apply(await deleteMailAlias(address));
    showNotice(t("panel.mail.aliasDeleted"), false);
  });
}

// Alıcıları virgül/satır/boşlukla ayrılmış metinden ayrıştırır.
function parseRecipients(text: string): string[] {
  return text
    .split(/[\s,;]+/)
    .map((value) => value.trim())
    .filter((value) => value.length > 0);
}

async function addList() {
  await run(async () => {
    apply(await createMailList(selectedDomain.value, listForm.localPart, parseRecipients(listForm.recipients)));
    listForm.localPart = "";
    listForm.recipients = "";
    showListForm.value = false;
    showNotice(t("panel.mail.listCreated"), false);
  });
}

async function editListRecipients(address: string, current: string[]) {
  const value = window.prompt(t("panel.mail.listEditPrompt"), current.join(", "));
  if (value === null) return;
  await run(async () => {
    apply(await setMailListRecipients(address, parseRecipients(value)));
    showNotice(t("panel.mail.listUpdated"), false);
  });
}

async function removeList(address: string) {
  if (!window.confirm(t("panel.mail.confirmDelete"))) return;
  await run(async () => {
    apply(await deleteMailList(address));
    showNotice(t("panel.mail.listDeleted"), false);
  });
}

async function run(operation: () => Promise<void>) {
  busy.value = true;
  try {
    await operation();
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}

function apply(next: MailWorkspace) {
  workspace.value = next;
  if (next.selectedDomain) {
    selectedDomain.value = next.selectedDomain;
  }
  const domain = next.domains.find((item) => item.name === selectedDomain.value);
  domainQuota.value = domain?.maxSizeMB ?? 0;
  // Spam + sertifika uçları admin-only; müşteri/bayide çağırma (gereksiz 403 olmasın).
  if (isAdmin.value && next.configured && !spam.value) {
    void loadSpam();
  }
  if (isAdmin.value && next.configured && !certificate.value) {
    void loadCertificate();
  }
}

async function loadSpam() {
  try {
    spam.value = await getMailSpam();
  } catch {
    spam.value = null;
  }
}

async function saveSpam() {
  if (!spam.value) return;
  await run(async () => {
    spam.value = await updateMailSpam(spam.value as MailSpamSettings);
    showNotice(t("panel.mail.spamSaved"), false);
  });
}

async function saveDomainQuota() {
  await run(async () => {
    apply(await setMailDomainQuota(selectedDomain.value, domainQuota.value));
    showNotice(t("panel.mail.quotaSaved"), false);
  });
}

async function changeAccountQuota(address: string, currentMax: number) {
  const value = window.prompt(t("panel.mail.enterQuota"), String(currentMax));
  if (value === null) return;
  const mb = Number(value);
  if (Number.isNaN(mb) || mb < 0) return;
  await run(async () => {
    apply(await setMailAccountQuota(address, mb));
    showNotice(t("panel.mail.quotaSaved"), false);
  });
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
  noticeDetails.value = "";
}

// Hata bildirimini gerçek teknik sebebiyle (CLI/Details) birlikte gösterir; böylece "zayıf
// parola" gibi durumlar net mesajla + ayrıntıyla görünür.
function showError(error: unknown) {
  notice.value = resolveApiMessage(error);
  noticeError.value = true;
  noticeDetails.value = extractApiDetails(error);
}
</script>

<style scoped>
.mail-loading {
  display: inline-flex;
  align-items: center;
  gap: 10px;
}

.mail-spinner {
  width: 15px;
  height: 15px;
  border: 2px solid color-mix(in srgb, var(--accent-1, #2e7d32) 35%, transparent);
  border-top-color: var(--accent-1, #2e7d32);
  border-radius: 50%;
  animation: mail-spin 0.7s linear infinite;
}

@keyframes mail-spin {
  to { transform: rotate(360deg); }
}

.mail-panel {
  display: grid;
  gap: 12px;
  align-content: start;
}

.notice-details {
  margin: -4px 0 0;
  font-size: 0.78rem;
  color: var(--text-muted);
}

.notice-details pre {
  margin: 6px 0 0;
  padding: 8px 10px;
  border: 1px solid var(--panel-border);
  border-radius: 8px;
  background: var(--surface-1);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.section-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: flex-start;
  flex-wrap: wrap;
}

.section-head button,
.row-actions button {
  width: auto;
}

.domain-select {
  min-width: min(320px, 60vw);
}

.address-field {
  display: flex;
  align-items: center;
  gap: 6px;
}

.address-field span {
  color: var(--text-muted);
  white-space: nowrap;
}

.mail-row {
  grid-template-columns: minmax(200px, 1.6fr) 0.7fr 0.7fr auto;
}

.client-row {
  grid-template-columns: minmax(140px, 1fr) minmax(180px, 1.6fr) 0.5fr 0.8fr;
  align-items: center;
}

.client-settings {
  margin-top: 14px;
}

.client-settings h4 {
  margin: 0 0 2px;
}

.client-username {
  margin-top: 8px;
}

.copyable {
  cursor: pointer;
  text-decoration: underline dotted;
}

.diag-grid {
  display: grid;
  gap: 8px;
}

.diag-item {
  display: flex;
  align-items: flex-start;
  gap: 11px;
  padding: 11px 13px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.diag-item.ok {
  border-color: color-mix(in srgb, var(--ok-1) 35%, transparent);
  background: var(--ok-bg);
}

.diag-item.fail {
  border-color: color-mix(in srgb, var(--danger-1) 35%, transparent);
  background: var(--danger-bg);
}

.diag-item.unknown {
  border-color: color-mix(in srgb, var(--warn-1) 30%, transparent);
}

.diag-dot {
  width: 9px;
  height: 9px;
  margin-top: 5px;
  border-radius: 50%;
  flex: none;
  background: var(--text-muted);
}

.diag-item.ok .diag-dot {
  background: var(--ok-1);
  box-shadow: 0 0 0 3px var(--ok-bg);
}

.diag-item.fail .diag-dot {
  background: var(--danger-1);
  box-shadow: 0 0 0 3px var(--danger-bg);
}

.diag-item.unknown .diag-dot {
  background: var(--warn-1);
}

.diag-body {
  display: grid;
  gap: 3px;
  min-width: 0;
  flex: 1;
}

.diag-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.diag-detail {
  color: var(--text-muted);
  word-break: break-word;
}

.diag-detail code {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--surface-2);
  font-size: 0.92em;
}

.maildomain-row {
  grid-template-columns: minmax(220px, 2fr) 0.8fr 0.9fr auto;
  align-items: center;
}

.mail-list-hint {
  margin: 0;
}

.mail-domain-bar {
  display: flex;
  align-items: center;
  gap: 12px;
}

.mail-domain-bar .current-domain {
  font-size: 1.02rem;
}

.mail-domain-bar .danger {
  margin-left: auto;
}

.alias-row {
  grid-template-columns: minmax(200px, 1.4fr) minmax(180px, 1.2fr) auto;
}

.list-row {
  grid-template-columns: minmax(180px, 1.2fr) minmax(220px, 2fr) auto;
  align-items: center;
}

.list-recipients {
  overflow-wrap: anywhere;
  font-size: 0.82rem;
  color: var(--text-soft);
}

.dns-row {
  grid-template-columns: auto minmax(140px, 0.8fr) minmax(280px, 2.4fr) auto;
}

.listener-row {
  grid-template-columns: minmax(120px, 1fr) minmax(80px, 0.6fr) minmax(220px, 2fr);
  align-items: center;
}

.listener-binds {
  overflow-wrap: anywhere;
  font-size: 0.78rem;
  color: var(--text-soft);
}

.dns-value {
  overflow-wrap: anywhere;
  font-size: 0.78rem;
  color: var(--text-soft);
}

.danger {
  color: var(--danger-text);
}

.quota-strip {
  display: flex;
  gap: 12px;
  align-items: flex-end;
  flex-wrap: wrap;
}

.inline-field {
  display: flex;
  gap: 8px;
  align-items: center;
}

.inline-field input {
  width: 120px;
}

.inline-field .hint {
  color: var(--text-muted);
  font-size: 0.75rem;
}

.toggle-field {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--text-soft);
}

.toggle-field input {
  width: auto;
}

.rdns-input {
  max-width: 240px;
}

.rdns-result {
  display: grid;
  gap: 10px;
  padding: 12px 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.rdns-status {
  padding: 8px 12px;
  border-radius: 8px;
  font-weight: 650;
  font-size: 0.84rem;
}

.rdns-status.ok {
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-text, var(--accent-1));
}

.rdns-status.warn {
  background: color-mix(in srgb, var(--warn-1) 16%, transparent);
  color: var(--warn-text, var(--warn-1));
}

.rdns-status.danger {
  background: color-mix(in srgb, var(--danger-1) 16%, transparent);
  color: var(--danger-text, var(--danger-1));
}

.rdns-meta {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px;
  margin: 0;
}

.rdns-meta dt {
  color: var(--text-muted);
  font-size: 0.66rem;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.rdns-meta dd {
  margin: 2px 0 0;
  font-size: 0.82rem;
  word-break: break-all;
}

.rdns-guide {
  margin: 0;
}

@media (max-width: 760px) {
  .rdns-meta {
    grid-template-columns: 1fr;
  }

  .section-head {
    flex-direction: column;
  }
}
</style>
