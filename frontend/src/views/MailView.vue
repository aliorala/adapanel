<template>
  <section class="panel module-shell mail-panel">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.mail.title") }}</h2>
        <p class="meta">{{ t("panel.mail.subtitle") }}</p>
        <p v-if="workspace?.provider" class="meta">{{ t("panel.mail.activeProvider") }}: {{ workspace.provider === 'mailenable' ? 'MailEnable' : 'Stalwart' }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">{{ t("panel.mail.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>
    <details v-if="noticeError && noticeDetails" class="notice-details" open>
      <summary>{{ t("panel.common.technicalDetails") }}</summary>
      <pre>{{ noticeDetails }}</pre>
    </details>
    <p v-if="workspace?.statusMessage" class="notice error" role="alert">{{ workspace.statusMessage }}</p>

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
          <PasswordField v-model="adminPassword" autocomplete="new-password" />
        </label>
      </div>
      <div class="row-actions">
        <button class="compact" v-bind="writeGate(busy || !adminPassword)" @click="configure">{{ t("panel.mail.connect") }}</button>
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
          <div v-for="item in domainPager.items.value" :key="item.name" class="data-row maildomain-row">
            <strong>{{ item.name }}</strong>
            <span>{{ item.active ? (item.countMeasured ? item.accountCount : t("panel.mail.mailboxCountUnmeasured")) : "—" }}</span>
            <span :class="['status-pill', item.active ? 'ok' : 'warn']">
              {{ item.active ? t("panel.status.enabled") : t("panel.mail.mailOff") }}
            </span>
            <button class="compact row-button" v-bind="item.active ? { disabled: busy } : writeGate(busy)" @click="openDomain(item.name)">
              {{ t("panel.mail.manage") }}
            </button>
          </div>
          <p v-if="mailDomains.length === 0" class="empty-state">{{ t("panel.mail.noSiteDomainsList") }}</p>
        </div>
        <TablePager :state="domainPager" :disabled="busy" />
      </template>

      <div v-if="selectedDomain" class="module-toolbar mail-domain-bar">
        <button class="secondary compact" @click="backToDomains">← {{ t("panel.mail.allDomains") }}</button>
        <strong class="current-domain">{{ selectedDomain }}</strong>
        <button class="danger compact" v-bind="writeGate(busy)" @click="removeDomain">
          {{ t("panel.mail.deleteDomain") }}
        </button>
      </div>

      <template v-if="selectedDomain">
      <!--
        ALAN ADI ÇALIŞMA ALANININ SIRASI = KULLANICININ SORU SIRASI.

        ÖNCEKİ SIRA ölçüldü ve soruyla uyuşmuyordu: kutular → webmail → otomatik yapılandırma →
        teşhis → otomatik yanıtlayıcı → takma adlar → listeler. Yani "alan adım postaya hazır mı?"
        sorusunun cevabı (teşhis kartı) YEDİNCİ bloktaydı; kullanıcının her gün kullandığı takma ad
        ve liste bölümleri ise yılda bir açılan yayınlama kartının ALTINA düşüyordu.

        YENİ SIRA: alan adı hazır mı (teşhis) → kutular (+ o kutunun otomatik yanıtlayıcısı) →
        takma adlar → listeler → ayarlar (kotalar, webmail/istemci ayarları, otomatik yapılandırma,
        gelişmiş). Hiçbir blok silinmedi, hiçbiri katlanmadı; yalnız sıra değişti.
      -->

        <!-- Mail teşhis: panel MX/A/SPF/DKIM/DMARC/PTR'yi kendisi kontrol eder (profesyonel paneller gibi). -->
        <section class="form-surface diagnostics-card">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.mail.diag.title") }}</h3>
              <p class="meta">{{ t("panel.mail.diag.hint") }}</p>
            </div>
            <!-- ÖLÇME eylemi BİRİNCİL değildir: hiçbir şey yazmaz, geri alınacak bir sonucu
                 yoktur. Panelin aynı işi yapan düğmesi (Yedekler > "Yedekleri doğrula") da
                 `secondary`; vurgu dolgusu, kaydeden/yayınlayan düğmelere ayrılmıştır.
                 SAYILDI: bu ekranda 15 vurgu dolgulu düğme vardı, hiçbiri diğerinden
                 baskın değildi -- yani hiçbiri "birincil" değildi. -->
            <button class="secondary compact" :disabled="busy || diagLoading" @click="runDiagnostics">
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

        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.mailboxes") }}</h3>
            <p class="meta">{{ selectedDomain }}</p>
          </div>
          <!-- "Iptal" BIRINCIL DUGME OLARAK CIZILEMEZ: ayni dugme formu acar (Yeni ...) ve
               kapatir (Iptal); sinif sabit `compact` oldugu icin kapatma eylemi ekranin en
               guclu, vurgu dolgulu dugmesi gibi gorunuyordu. Etiket "Iptal"e donunce dugme de
               sakinlesir; vurgu, formun icindeki gercek islem dugmesinde ("Olustur") kalir. -->
          <button :class="['compact', { secondary: showAccountForm }]" @click="showAccountForm = !showAccountForm">
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
              <PasswordField v-model="accountForm.password" minlength="8" autocomplete="new-password" />
              <small class="field-hint">{{ t("panel.mail.passwordHint") }}</small>
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.maxSize") }}</span>
              <input v-model.number="accountForm.maxSizeMB" type="number" min="0" />
            </label>
          </div>
          <div class="row-actions">
            <button class="compact" v-bind="writeGate(busy || !accountForm.localPart || !accountForm.password)" @click="addAccount">
              {{ t("panel.mail.create") }}
            </button>
          </div>
        </div>

        <div v-if="accountSelection.count.value > 0" class="bulk-bar" role="status">
          <span class="status-pill">{{ t("panel.bulk.selectedCount", { count: accountSelection.count.value }) }}</span>
          <button class="danger compact" v-bind="writeGate(busy || bulk.busy.value)" @click="removeSelectedAccounts">
            {{ t("panel.bulk.deleteSelected", { count: accountSelection.count.value }) }}
          </button>
          <button class="secondary compact" :disabled="bulk.busy.value" @click="accountSelection.clear">
            {{ t("panel.bulk.clearSelection") }}
          </button>
        </div>

        <div class="data-table">
          <div class="data-row table-head mail-row">
            <span class="select-cell">
              <input
                type="checkbox"
                :checked="accountSelection.allSelected(visibleAccountAddresses)"
                :aria-label="t('panel.bulk.selectAll')"
                @change="accountSelection.toggleAll(visibleAccountAddresses)" />
            </span>
            <span>{{ t("panel.mail.address") }}</span>
            <span>{{ t("panel.mail.maxSize") }}</span>
            <span>{{ t("panel.sites.state") }}</span>
            <span>{{ t("panel.software.action") }}</span>
          </div>
          <div v-for="account in accountPager.items.value" :key="account.address" class="data-row mail-row">
            <span class="select-cell">
              <input
                type="checkbox"
                :checked="accountSelection.isSelected(account.address)"
                :aria-label="t('panel.bulk.selectRow', { name: account.address })"
                @change="accountSelection.toggle(account.address)" />
            </span>
            <strong>{{ account.address }}</strong>
            <!-- BİRİM MiB, "MB" DEĞİL: `MailService` bu iki sayıyı da Stalwart'ın bayt değerini
                 1024*1024'e bölerek üretiyor ve kotayı `MaxSizeMB * 1024 * 1024` ile geri yazıyor.
                 Sayı ÖLÇEKLENMEZ (kutu kotası formda MiB olarak girilir, kart onun yankısıdır);
                 düzeltilen tek şey koda gömülü etiketin yanlış tabanı. -->
            <span>{{ account.currentSizeMB }} / {{ account.maxSizeMB ? `${account.maxSizeMB} ${t("panel.units.mib")}` : "∞" }}</span>
            <!-- UC DURUM, IKIYE INDIRILMEZ: `active === null` "sunucuya soruldu ama cevap
                 alinamadi" demek. Eskiden backend sabit `true` donduruyordu ve HER kutu yesil
                 "Etkin" goruluyordu; kapali bir kutu panelde saglikli gorunuyordu. -->
            <span v-if="account.active === null || account.active === undefined" class="status-pill warn">
              {{ t("panel.mail.accountStateUnmeasured") }}
            </span>
            <span v-else :class="['status-pill', account.active ? 'ok' : 'warn']">
              {{ account.active ? t("panel.status.enabled") : t("panel.status.disabled") }}
            </span>
            <div class="row-actions">
              <button class="secondary compact row-button" v-bind="writeGate(busy)" @click="openWebmail()">
                {{ t("panel.mail.webmailOpen") }}
              </button>
              <button class="secondary compact row-button" v-bind="writeGate(busy)" @click="changeAccountQuota(account.address, account.maxSizeMB)">
                {{ t("panel.mail.quota") }}
              </button>
              <button class="secondary compact row-button" v-bind="writeGate(busy)" @click="changePassword(account.address)">
                {{ t("panel.mail.changePassword") }}
              </button>
              <button class="secondary compact row-button" :disabled="busy" @click="openAutoResponder(account.address)">
                {{ t("panel.mail.autoResponder") }}
              </button>
              <button class="danger compact row-button" v-bind="writeGate(busy)" @click="removeAccount(account.address)">
                {{ t("panel.mail.delete") }}
              </button>
            </div>
          </div>
          <p v-if="workspace.accounts.length === 0" class="empty-state">{{ t(workspace.accountsMeasured === false ? "panel.mail.mailboxesUnmeasured" : "panel.mail.noMailboxes") }}</p>
        </div>
        <TablePager :state="accountPager" :disabled="busy || bulk.busy.value" />

        <section v-if="autoResponderFor" class="form-surface">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.mail.autoResponderTitle") }}</h3>
              <p class="meta">{{ autoResponderFor }}</p>
            </div>
            <label class="toggle-field">
              <input v-model="autoResponderForm.enabled" type="checkbox" />
              <span>{{ t("panel.mail.autoResponderEnabled") }}</span>
            </label>
          </div>
          <p class="meta">{{ t("panel.mail.autoResponderHint") }}</p>
          <div v-if="autoResponderForm.enabled" class="form-grid one">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.autoResponderSubject") }}</span>
              <input v-model.trim="autoResponderForm.subject" :placeholder="t('panel.mail.autoResponderSubjectPlaceholder')" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.autoResponderMessage") }}</span>
              <textarea v-model="autoResponderForm.message" rows="5"></textarea>
            </label>
          </div>
          <div class="row-actions">
            <button
              class="compact"
              v-bind="writeGate(busy || (autoResponderForm.enabled && !autoResponderForm.message.trim()))"
              @click="saveAutoResponder">
              {{ busy ? t("panel.database.saving") : t("panel.common.save") }}
            </button>
            <button class="secondary compact" @click="autoResponderFor = ''">{{ t("panel.common.cancel") }}</button>
          </div>
        </section>

        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.aliases") }}</h3>
            <p class="meta">{{ t("panel.mail.aliasesHint") }}</p>
          </div>
          <!-- "Iptal" BIRINCIL DUGME OLARAK CIZILEMEZ: ayni dugme formu acar (Yeni ...) ve
               kapatir (Iptal); sinif sabit `compact` oldugu icin kapatma eylemi ekranin en
               guclu, vurgu dolgulu dugmesi gibi gorunuyordu. Etiket "Iptal"e donunce dugme de
               sakinlesir; vurgu, formun icindeki gercek islem dugmesinde ("Olustur") kalir. -->
          <button :class="['compact', { secondary: showAliasForm }]" @click="showAliasForm = !showAliasForm">
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
            <button class="compact" v-bind="writeGate(busy || !aliasForm.localPart || !aliasForm.target)" @click="addAlias">
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
            <button class="danger compact row-button" v-bind="writeGate(busy)" @click="removeAlias(alias.address)">
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
          <!-- "Iptal" BIRINCIL DUGME OLARAK CIZILEMEZ: ayni dugme formu acar (Yeni ...) ve
               kapatir (Iptal); sinif sabit `compact` oldugu icin kapatma eylemi ekranin en
               guclu, vurgu dolgulu dugmesi gibi gorunuyordu. Etiket "Iptal"e donunce dugme de
               sakinlesir; vurgu, formun icindeki gercek islem dugmesinde ("Olustur") kalir. -->
          <button :class="['compact', { secondary: showListForm }]" @click="showListForm = !showListForm">
            {{ showListForm ? t("panel.common.cancel") : t("panel.mail.newList") }}
          </button>
        </div>

        <div v-if="showListForm" class="form-surface">
          <div class="form-grid">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.listAddress") }}</span>
              <div class="address-field">
                <input v-model.trim="listForm.localPart" :placeholder="t('panel.mail.listAddressPlaceholder')" />
                <span>@{{ selectedDomain }}</span>
              </div>
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.listDescription") }}</span>
              <input v-model.trim="listForm.description" :placeholder="t('panel.mail.listDescriptionPlaceholder')" :maxlength="isMailEnable ? 256 : 512" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.listSubscribers") }}</span>
              <textarea v-model="listForm.recipients" rows="3" :placeholder="t('panel.mail.listRecipientsPlaceholder')"></textarea>
              <small class="field-hint">{{ t("panel.mail.listRecipientsHint") }}</small>
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.listPostingPolicy") }}</span>
              <select v-model="listForm.postingPolicy">
                <option value="anyone">{{ t("panel.mail.listPolicyAnyone") }}</option>
                <option v-if="!isMailEnable" value="subscribers">{{ t("panel.mail.listPolicySubscribers") }}</option>
                <option v-if="!isMailEnable" value="allowed">{{ t("panel.mail.listPolicyAllowed") }}</option>
              </select>
              <small class="field-hint">{{ t(isMailEnable ? "panel.mail.mailEnableListPolicyHint" : "panel.mail.listPostingPolicyHint") }}</small>
            </label>
            <label v-if="listForm.postingPolicy !== 'anyone'" class="stacked-field">
              <span class="field-label">{{ t("panel.mail.listAllowedSenders") }}</span>
              <textarea v-model="listForm.allowedSenders" rows="2" :placeholder="t('panel.mail.listRecipientsPlaceholder')"></textarea>
              <small class="field-hint">{{ t("panel.mail.listAllowedSendersHint") }}</small>
            </label>
          </div>
          <div class="row-actions">
            <button class="compact" v-bind="writeGate(busy || !listForm.localPart || !parseRecipients(listForm.recipients).length)" @click="addList">
              {{ t("panel.mail.create") }}
            </button>
          </div>
        </div>

        <div class="data-table">
          <div class="data-row table-head list-row">
            <span>{{ t("panel.mail.listAddress") }}</span>
            <span>{{ t("panel.mail.listSubscribers") }}</span>
            <span>{{ t("panel.software.action") }}</span>
          </div>
          <div v-for="list in workspace.lists" :key="list.address" class="data-row list-row">
            <div class="list-identity">
              <strong>{{ list.address }}</strong>
              <small v-if="list.description" class="list-description">{{ list.description }}</small>
              <!-- Politika satirda GORUNUR olmali: "herkes gonderebilir" bir guvenlik durumudur ve
                   ancak gorulurse fark edilir. -->
              <small
                class="list-policy"
                :class="{ 'policy-open': list.postingPolicy === 'anyone' }"
              >{{ policyLabel(list.postingPolicy) }}</small>
              <!-- EK ADRESLER: listeye giden IKINCI kapilardir. Adres kimliginin yaninda
                   gorunmeli -- ayri bir sekmede saklansaydi, listeye baska hangi adresten posta
                   gelebildigi ekranda hic gorunmezdi. -->
              <span v-if="list.aliases.length" class="list-aliases">
                <span v-for="alias in list.aliases" :key="alias" class="alias-chip">
                  {{ alias }}
                  <button
                    type="button"
                    class="chip-remove"
                    v-bind="writeGate(busy)"
                    :title="t('panel.mail.listRemoveAlias', { address: alias })"
                    :aria-label="t('panel.mail.listRemoveAlias', { address: alias })"
                    @click="removeListAlias(list.address, alias)"
                  >
                    ×
                  </button>
                </span>
              </span>
            </div>
            <div class="list-subscribers">
              <!-- Her abone tek tek cikarilabilir: tum listeyi elle yeniden yazmak, bir adresi
                   yanlis kopyalayip o kisiyi sessizce dusurmek demekti. -->
              <span v-for="subscriber in list.recipients" :key="subscriber" class="subscriber-chip">
                {{ subscriber }}
                <button
                  type="button"
                  class="chip-remove"
                  v-bind="writeGate(busy)"
                  :title="t('panel.mail.listUnsubscribe', { address: subscriber })"
                  :aria-label="t('panel.mail.listUnsubscribe', { address: subscriber })"
                  @click="unsubscribe(list.address, subscriber)"
                >
                  ×
                </button>
              </span>
              <span v-if="list.recipients.length === 0" class="meta">{{ t("panel.mail.listNoSubscribers") }}</span>
            </div>
            <div class="row-actions">
              <button class="secondary compact row-button" v-bind="writeGate(busy)" @click="subscribe(list.address)">
                {{ t("panel.mail.listAddSubscriber") }}
              </button>
              <button class="secondary compact row-button" v-bind="writeGate(busy)" @click="addListAlias(list.address)">
                {{ t("panel.mail.listAddAlias") }}
              </button>
              <button v-if="!isMailEnable" class="secondary compact row-button" v-bind="writeGate(busy)" @click="editListPolicy(list)">
                {{ t("panel.mail.listPostingPolicy") }}
              </button>
              <button class="secondary compact row-button" v-bind="writeGate(busy)" @click="editListRecipients(list.address, list.recipients)">
                {{ t("panel.mail.listEdit") }}
              </button>
              <button class="danger compact row-button" v-bind="writeGate(busy)" @click="removeList(list.address)">
                {{ t("panel.mail.delete") }}
              </button>
            </div>
          </div>
          <p v-if="workspace.lists.length === 0" class="empty-state">{{ t("panel.mail.noLists") }}</p>
        </div>

        <!-- Panelin genel adresi: List-Unsubscribe baglantisi bundan kurulur. TAHMIN EDILMEZ --
             bos birakilirsa baslik hic eklenmez, cunku calismayan bir baglanti gondermek
             baslik koymamaktan kotudur. Yalniz yonetici gorur/degistirir. -->
        <div v-if="isAdmin" class="form-surface list-baseurl">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.publicBaseUrl") }}</span>
            <div class="row-actions">
              <input v-model.trim="publicBaseUrl" placeholder="https://panel.example.com" />
              <button class="compact" v-bind="writeGate(busy)" @click="savePublicBaseUrl">
                {{ t("panel.common.save") }}
              </button>
            </div>
            <small class="field-hint">{{ t("panel.mail.publicBaseUrlHint") }}</small>
          </label>
        </div>

        <!-- DURUST SINIR: "kimler gonderebilir" ve self-servis abonelikten cikma ARTIK CALISIYOR
             (RCPT sieve kurali + genel onay sayfasi, ikisi de canli dogrulandi). Kalan fark:
             abone cikarma baglantisi liste basinadir ve e-posta onayi ister -- Stalwart fan-out'u
             iletiyi kisisellestirmedigi icin aboneye ozel tek-tik baglanti URETILEMEZ. -->
        <p class="meta list-limitation">{{ t("panel.mail.listPolicyLimitation") }}</p>

        <!--
          KOTALAR VE SINIRLAR TEK KARTTA — DÖRT BLOK YERİNE BİR.

          SAYILDI: aynı soruyu ("bu alan adının sınırları ne?") yanıtlayan dört ayrı blok üst üste
          duruyordu — alan adı kotası şeridi (kart), giden posta limiti şeridi (kart), okunamadı
          kutusu ve çıplak bir ipucu paragrafı. Üçü kenarlıklı kart, biri kenarlıksız paragraftı:
          aynı işi gören iki farklı desen.

          ÜSTELİK İPUCU YERİNDE DEĞİLDİ: hiçbir `v-if` altında olmadığı için ALAN ADI SEÇİLİ
          DEĞİLKEN de çiziliyordu, yani hiçbir posta kutusunun görünmediği alan adı LİSTESİNİN
          altında "saatlik giden posta limiti" anlatan 199 karakterlik bir paragraf duruyordu.

          HİÇBİR DENETİM VE HİÇBİR METİN DÜŞMEDİ, kimin gördüğü de değişmedi: ipucu yine herkese,
          giden posta alanları yine yalnız yöneticiye görünür. Yalnız hepsi tek başlığın altında.
        -->
        <section class="form-surface limits-card">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.mail.limitsTitle") }}</h3>
              <p class="meta">{{ selectedDomain }}</p>
            </div>
          </div>

          <div class="quota-strip">
            <label class="inline-field">
              <span class="field-label">{{ t("panel.mail.domainQuota") }}</span>
              <input v-model.number="domainQuota" type="number" min="0" />
              <span class="hint">MB ({{ t("panel.mail.zeroUnlimited") }})</span>
            </label>
            <span v-if="selectedDomainInfo && selectedDomainInfo.maxSizeMB > 0" class="hint">
              {{ t("panel.mail.domainQuotaUsage", { used: selectedDomainInfo.allocatedMB, limit: selectedDomainInfo.maxSizeMB }) }}
            </span>
            <button class="secondary compact" v-bind="writeGate(busy)" @click="saveDomainQuota">{{ t("panel.common.save") }}</button>
          </div>

          <p v-if="isMailEnable" class="field-hint">{{ t("panel.mail.mailEnableAllocationHint") }}</p>

          <!-- GİDEN POSTA LİMİTİ: sunucunun itibarını koruyan sınır. Yalnız yönetici değiştirebilir —
               kendi limitini müşterinin yükseltebilmesi korumayı anlamsız kılardı. -->
          <div v-if="isAdmin && !isMailEnable" class="quota-strip">
            <label class="inline-field">
              <span class="field-label">{{ t("panel.mail.outboundLimit") }}</span>
              <input v-model.number="outboundLimit" type="number" min="0" />
              <span class="hint">{{ t("panel.mail.outboundLimitPerHour") }} ({{ t("panel.mail.zeroUnlimited") }})</span>
            </label>
            <!-- "Kurulu değil" ile "limitsiz" AYRI şeyler: ilki panelin sınır kuramadığı, yani
                 sunucunun korumasız olduğu anlamına gelir. -->
            <span
              v-if="outboundLimitState && !outboundLimitState.configured"
              class="status-pill warn"
              :title="t('panel.mail.outboundLimitNotConfiguredHint')">
              {{ t("panel.mail.outboundLimitNotConfigured") }}
            </span>
            <button class="secondary compact" :disabled="busy" @click="saveOutboundLimit">
              {{ t("panel.mail.outboundLimitSave") }}
            </button>
          </div>
          <SectionLoadError
            v-if="isAdmin && outboundLimitLoad.message.value"
            :message="outboundLimitLoad.message.value"
            @retry="loadOutboundLimit" />
          <p class="field-hint">{{ t(isMailEnable ? "panel.mail.mailEnableOutboundHint" : "panel.mail.outboundLimitHint") }}</p>

          <!-- SAHTECİLİK KURALI: sunucu, kimliksiz bir bağlantının buradaki alan adları adına
               göndermesini reddeder. Bu KIRDIĞIMIZ bir akış (PHP'nin ayarsız mail() çağrısı) ve
               sessiz ölmemesi gerekiyor: kullanıcı reddi gördüğünde alternatifi (kimlik
               doğrulamalı gönderim) burada yazılı bulur. Gerekçe: Mail/MailSenderPolicy. -->
          <p v-if="!isMailEnable" class="field-hint">
            <strong>{{ t("panel.mail.senderPolicyTitle") }}</strong>
            {{ t("panel.mail.senderPolicyHint") }}
          </p>
        </section>

        <!-- Webmail + posta istemci ayarları (profesyonel paneller gibi). -->
        <section class="form-surface">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.mail.webmailTitle") }}</h3>
              <p class="meta">{{ t("panel.mail.webmailHint") }}</p>
            </div>
            <span class="row-actions">
              <button class="compact" v-bind="writeGate(busy)" @click="openWebmail()">
                {{ busy && webmailOpening ? t("panel.mail.webmailOpening") : t("panel.mail.webmailOpen") }}
              </button>
              <!-- TANILA: uç ve çevirisi vardı ama çağıran yoktu. "Webmail açılmıyor" durumunda
                   hazır olan tek teşhis aracına kullanıcı erişemiyordu. Uç admin-only. -->
              <button v-if="isAdmin" class="secondary compact" :disabled="webmailDiagnosing" @click="runWebmailDiagnose">
                {{ webmailDiagnosing ? t("panel.common.loading") : t("panel.mail.webmailDiagnose") }}
              </button>
            </span>
          </div>

          <pre v-if="webmailReport" class="webmail-report">{{ webmailReport }}</pre>

          <!--
            İSTEMCİ AYARLARI SUNUCUDAN ÖLÇÜLÜR (`GET /mail/client-settings/{domain}`).
            Önce burada sabit "mail.<alan> / 143 / 587 / STARTTLS" yazılıydı: sunucu o portu
            dinlemiyorsa ya da sertifika başka bir adı kapsıyorsa panel kullanıcıya ÇALIŞMAYAN
            bir ayar dağıtıyordu — "panel yanlış söyledi" hatası, elle kurmaktan daha kötüdür.
            Aynı kaynak autodiscover/autoconfig XML'lerini de besler, yani ekran ile istemcinin
            kendiliğinden bulduğu ayar bir daha ayrışamaz.
          -->
          <div class="client-settings">
            <h4>{{ t("panel.mail.clientSettingsTitle") }}</h4>
            <p class="meta">{{ t("panel.mail.clientSettingsHint") }}</p>
            <SectionLoadError
              v-if="clientSettingsLoad.message.value"
              :message="clientSettingsLoad.message.value"
              @retry="loadClientSettings" />
            <div v-else-if="clientSettings" class="data-table">
              <div class="data-row table-head client-row">
                <span>{{ t("panel.mail.clientField") }}</span>
                <span>{{ t("panel.mail.clientServer") }}</span>
                <span>{{ t("panel.mail.clientPort") }}</span>
                <span>{{ t("panel.mail.clientSecurity") }}</span>
              </div>
              <div
                v-for="server in clientSettings.servers"
                :key="`${server.protocol}-${server.port}`"
                class="data-row client-row">
                <strong>{{ clientProtocolLabel(server.protocol) }}</strong>
                <span class="copyable" @click="copyDnsValue(server.host)">{{ server.host }}</span>
                <span>{{ server.port }}</span>
                <span>{{ server.socketType }}</span>
              </div>
              <p v-if="clientSettings.servers.length === 0" class="empty-state">
                {{ t("panel.mail.clientSettingsNone") }}
              </p>
            </div>
            <p class="meta client-username">
              {{ t("panel.mail.clientUsername") }}:
              <strong>{{ workspace.accounts[0]?.address || `info@${selectedDomain}` }}</strong>
              — {{ t("panel.mail.clientUsernameHint") }}
            </p>
          </div>
        </section>

        <!--
          POSTA İSTEMCİSİ OTOMATİK YAPILANDIRMA (autodiscover / autoconfig).

          YALNIZ YÖNETİCİ: uçlar `[Authorize(Roles = "Administrator")]`. Kart müşteriye
          gösterilseydi her düğmesi 403 dönerdi.

          Ölçüm zincirin ADIMLARINI gösterir, tek bir "kurulu/kurulu değil" rozetini değil:
          panelin uçları çalışıyorken istemcilerin oraya HİÇ ulaşamadığı durum tam olarak bu
          şekilde görünmez kalmıştı.
        -->
        <section v-if="isAdmin" class="form-surface autoconfig-card">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.mail.autoconfig.title") }}</h3>
              <p class="meta">{{ t("panel.mail.autoconfig.subtitle") }}</p>
            </div>
            <div class="row-actions">
              <span v-if="autoconfig" :class="['status-pill', autoconfig.isPublished ? 'ok' : 'warn']">
                {{ autoconfig.isPublished ? t("panel.mail.autoconfig.published") : t("panel.mail.autoconfig.notPublished") }}
              </span>
              <button class="secondary compact" :disabled="autoconfigBusy" @click="loadAutoconfig">
                {{ autoconfigBusy && !autoconfigPublishing ? t("panel.common.loading") : t("panel.mail.autoconfig.check") }}
              </button>
            </div>
          </div>

          <SectionLoadError
            v-if="autoconfigLoad.message.value"
            :message="autoconfigLoad.message.value"
            @retry="loadAutoconfig" />

          <template v-else-if="autoconfig">
            <div class="diag-grid">
              <div
                v-for="step in autoconfig.steps"
                :key="step.id"
                :class="['diag-item', autoconfigStateTone(step.status)]">
                <span class="diag-dot"></span>
                <div class="diag-body">
                  <div class="diag-row">
                    <strong>{{ autoconfigStepLabel(step.id) }}</strong>
                    <span
                      :class="['status-pill', step.status === 'ok' ? 'ok' : step.status === 'fail' ? 'danger' : 'warn']">
                      {{ autoconfigStateLabel(step.status) }}
                    </span>
                  </div>
                  <!-- Ham ayrıntı HER ZAMAN gösterilir (yalnız hatada değil): "ok" satırında da
                       hangi site/kural/ana ad ölçüldüğü kullanıcının doğrulayabileceği tek şey. -->
                  <small class="diag-detail">
                    <code v-if="step.value">{{ step.value }}</code>
                    <span v-if="step.detail"> {{ step.detail }}</span>
                  </small>
                </div>
              </div>
            </div>

            <dl class="autoconfig-meta">
              <div>
                <dt>{{ t("panel.mail.autoconfig.site") }}</dt>
                <dd>{{ autoconfig.siteName || "—" }}</dd>
              </div>
              <div>
                <dt>{{ t("panel.mail.autoconfig.backendUrl") }}</dt>
                <dd>{{ autoconfig.backendUrl || "—" }}</dd>
              </div>
              <div>
                <dt>{{ t("panel.mail.autoconfig.checkedAt") }}</dt>
                <dd>{{ new Date(autoconfig.checkedAtUtc).toLocaleString(getFormattingLocale()) }}</dd>
              </div>
            </dl>

            <!-- GEREKEN DNS KAYITLARI: liste sunucudan yalnızca kayıtlar GERÇEKTEN yayında
                 DEĞİLKEN dolu gelir. Boş liste "eksik yok" demektir; kendimiz bir şey uydurmayız. -->
            <div v-if="autoconfig.requiredDnsRecords.length > 0" class="autoconfig-block">
              <h4>{{ t("panel.mail.autoconfig.requiredDns") }}</h4>
              <p class="meta">{{ t("panel.mail.autoconfig.requiredDnsHint", { domain: autoconfig.domain }) }}</p>
              <p v-if="autoconfig.dnsManagedByPanel" class="meta">
                {{ t("panel.mail.autoconfig.dnsManagedByPanel") }}
              </p>
              <div class="data-table">
                <div class="data-row table-head dns-row">
                  <span>{{ t("panel.mail.dnsType") }}</span>
                  <span>{{ t("panel.mail.dnsHost") }}</span>
                  <span>{{ t("panel.mail.dnsValue") }}</span>
                  <span>{{ t("panel.software.action") }}</span>
                </div>
                <div
                  v-for="record in autoconfig.requiredDnsRecords"
                  :key="`${record.type}-${record.host}`"
                  class="data-row dns-row">
                  <span class="status-pill">{{ record.type }}</span>
                  <strong>{{ record.host }}</strong>
                  <code class="dns-value">{{ record.value || t("panel.mail.dnsValueMissing") }}</code>
                  <button class="secondary compact row-button" :disabled="!record.value" @click="copyDnsValue(record.value)">
                    {{ t("panel.mail.dnsCopy") }}
                  </button>
                </div>
              </div>
            </div>

            <!-- DOĞRULAMA ADRESLERİ: kullanıcı zinciri panele güvenmeden kendi tarayıcısından
                 sınayabilsin. Panel "yayında" derken tarayıcı 404 veriyorsa gerçek olan ikincisidir. -->
            <div v-if="autoconfig.probeUrls.length > 0" class="autoconfig-block">
              <h4>{{ t("panel.mail.autoconfig.probeUrls") }}</h4>
              <ul class="autoconfig-probes">
                <li v-for="url in autoconfig.probeUrls" :key="url">
                  <a :href="url" target="_blank" rel="noopener noreferrer">{{ url }}</a>
                  <button class="secondary compact row-button" @click="copyDnsValue(url)">
                    {{ t("panel.mail.dnsCopy") }}
                  </button>
                </li>
              </ul>
            </div>
          </template>

          <p v-else class="meta">{{ t("panel.mail.autoconfig.empty") }}</p>

          <p class="meta">{{ t("panel.mail.autoconfig.publishHint") }}</p>

          <div class="form-grid">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.mail.certEmail") }}</span>
              <input v-model.trim="autoconfigContactEmail" type="email" placeholder="admin@example.com" />
            </label>
          </div>

          <div class="row-actions">
            <button class="compact" v-bind="writeGate(autoconfigBusy)" @click="publishAutoconfig">
              {{ autoconfigPublishing ? t("panel.mail.autoconfig.publishing") : t("panel.mail.autoconfig.publish") }}
            </button>
            <button
              v-if="autoconfig && autoconfig.isPublished"
              class="danger compact"
              v-bind="writeGate(autoconfigBusy)"
              @click="unpublishAutoconfig">
              {{ t("panel.mail.autoconfig.unpublish") }}
            </button>
          </div>
        </section>

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
        <!--
          OLCULMEYEN "YOK" DEGILDIR: liste okunamadiginda eskiden bos dizi geliyordu ve burasi
          "dinleyici yok" yaziyordu. Artik once OLCULDU MU sorusu cevaplaniyor.
        -->
        <p v-if="listeners && !listeners.measured" class="notice error" role="alert">
          {{ t("panel.mail.listenersUnmeasured") }}
        </p>
        <div v-else-if="listeners" class="data-table">
          <div class="data-row table-head listener-row">
            <span>{{ t("panel.mail.listenerName") }}</span>
            <span>{{ t("panel.mail.listenerProtocol") }}</span>
            <span>{{ t("panel.mail.listenerBinds") }}</span>
          </div>
          <div v-for="listener in listeners.listeners" :key="listener.name" class="data-row listener-row">
            <strong>{{ listener.name }}</strong>
            <span>{{ listener.protocol || "—" }}</span>
            <code class="listener-binds">{{ listener.binds.join(", ") || "—" }}</code>
          </div>
          <p v-if="listeners.listeners.length === 0" class="empty-state">{{ t("panel.mail.noListeners") }}</p>
        </div>
        </template>

        <div class="section-head">
          <div>
            <h3>{{ t("panel.mail.dnsTitle") }}</h3>
            <p class="meta">{{ t("panel.mail.dnsHint") }}</p>
          </div>
          <div class="row-actions">
            <button class="secondary compact" :disabled="busy" @click="loadDns">{{ t("panel.mail.dnsShow") }}</button>
            <button v-if="dns" class="compact" v-bind="writeGate(busy)" @click="applyDns">{{ t("panel.mail.dnsApply") }}</button>
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

        <SectionLoadError
          v-if="certificateLoad.message.value"
          :message="certificateLoad.message.value"
          @retry="loadCertificate" />

        <div class="form-surface">
          <p v-if="certificate?.active" class="meta">
            <span :class="['status-pill', certificate.active && certificate.source !== 'self-signed' ? 'ok' : 'warn']">
              {{ certificate.source === "windows-store" ? t("panel.mail.certWindowsStore") : certificate.source === "letsencrypt" ? "Let's Encrypt" : certificate.source === "self-signed" ? t("panel.mail.certSelfSigned") : t("panel.mail.certSourceUnknown") }}
            </span>
            {{ certificate.hostName }}
            <template v-if="certificate.notAfterUtc">
              - {{ t("panel.mail.certExpires") }}: {{ new Date(certificate.notAfterUtc).toLocaleDateString(getFormattingLocale()) }}
            </template>
          </p>
          <p v-else class="meta">{{ t("panel.mail.certNone") }}</p>
          <template v-if="certificate?.hostName">
            <p class="meta">{{ t("panel.mail.certWeb") }}:
              <span :class="['status-pill', certificate.webCertificate?.status === 'ok' ? 'ok' : 'warn']">{{ certificate.webCertificate?.status === 'ok' ? t('panel.mail.certVerified') : t('panel.mail.certUnverified') }}</span>
              <template v-if="certificate.webCertificate">
                {{ certificate.webCertificate.subject }} · {{ certificate.webCertificate.issuer }} · {{ certificate.webCertificate.notAfterUtc }}
                {{ certificate.webCertificate.thumbprint }} {{ certificate.webCertificate.detail }}
              </template>
            </p>
            <p class="meta">{{ t("panel.mail.certMail") }} — {{ certificate.hostName }}</p>
            <p v-for="endpoint in certificate.endpoints || []" :key="endpoint.port" class="meta">
              <span :class="['status-pill', endpoint.status === 'ok' ? 'ok' : 'warn']">{{ endpoint.port }} {{ endpoint.protocol }}: {{ endpoint.status === 'ok' ? t('panel.mail.certVerified') : t('panel.mail.certUnverified') }}</span>
              {{ endpoint.subject }} · {{ endpoint.issuer }} · {{ endpoint.notAfterUtc }}
              {{ endpoint.thumbprint }} {{ endpoint.detail }}
              <span v-if="endpoint.subjectAlternativeNames?.length"> · SAN: {{ endpoint.subjectAlternativeNames.join(", ") }}</span>
              <span v-if="endpoint.notBeforeUtc"> · {{ t("panel.mail.certValidFrom") }}: {{ endpoint.notBeforeUtc }}</span>
              <span v-if="endpoint.chainStatus?.length"> · {{ t("panel.mail.certChain") }}: {{ endpoint.chainStatus.join(", ") }}</span>
            </p>
            <p class="meta">{{ t('panel.mail.certRegistry') }}: {{ certificate.configurationStatus || 'unknown' }} · {{ certificate.certificateId || '-' }} · {{ t('panel.mail.certDefault') }}: {{ certificate.defaultCertificateId || '-' }}</p>
          </template>
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
            <button class="compact" :disabled="busy" @click="issueCert()">{{ t("panel.mail.certIssue") }}</button>
            <button class="compact" :disabled="busy" @click="issueCert(true)">{{ t("panel.mail.certReuse") }}</button>
            <button class="compact" :disabled="busy" @click="loadCertificate">{{ t("panel.mail.certCheck") }}</button>
          </div>
        </div>
        </template>
        </template>
      </template>

      <SectionLoadError v-if="spamLoad.message.value" :message="spamLoad.message.value" @retry="loadSpam" />
      <p v-if="isMailEnable" class="field-hint">{{ t("panel.mail.mailEnableSpamHint") }}</p>

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

      <!--
        SUNUCU DÜZEYİ POSTA ARAÇLARI — YALNIZ YÖNETİCİ.
        Kuyruk bütün kiracıların gönderici/alıcı adreslerini, röle ise sunucunun TÜM giden
        postasının nereye teslim edildiğini içerir; uçlar `Administrator` ile sınırlı.
        Alan adı bağlamında (site çalışma alanı → Posta) GÖSTERİLMEZ: orada ekran o alan adına
        aittir, sunucu geneline değil.
      -->
      <template v-if="isAdmin && !props.domain && workspace.installed && workspace.configured">
        <MailQueueCard />
        <MailRelayCard :provider="workspace?.provider" />
      </template>
    </template>
  </section>
</template>

<script setup lang="ts">
import { getFormattingLocale } from '../utils/locale';
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
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
  setMailListPolicy,
  getMailPublicBaseUrl,
  setMailPublicBaseUrl,
  subscribeToMailList,
  unsubscribeFromMailList,
  addMailListAlias,
  removeMailListAlias,
  deleteMailAccount,
  deleteMailAlias,
  deleteMailDomain,
  getMailCertificate,
  getMailClientSettings,
  getMailDns,
  getMailDiagnostics,
  getMailListeners,
  getMailSpam,
  getMailWorkspace,
  getAutoResponder,
  setAutoResponder,
  issueMailCertificate,
  setMailAccountPassword,
  setMailAccountQuota,
  setMailDomainQuota,
  updateMailSpam,
  diagnoseWebmail as diagnoseWebmailApi,
  getOutboundLimit,
  setOutboundLimit,
  type MailOutboundLimit,
  openWebmail as openWebmailApi,
  type MailCertificate,
  type MailClientSettings,
  type MailDiagnostics,
  type MailDns,
  type MailList,
  type MailListPostingPolicy,
  type MailListenerSnapshot,
  type MailSpamSettings,
  type MailWorkspace,
  type RdnsCheck
} from "../api/mail";
import {
  getMailAutoconfig,
  publishMailAutoconfig,
  unpublishMailAutoconfig,
  type MailAutodiscoverPublication
} from "../api/autodiscover";
import { webmailPage } from "../utils/webmailPage";
import { toReachableUrl } from "../utils/url";
import { UserFacingError } from "../utils/apiError";
import { useConfirm } from "../components/ConfirmDialog.vue";
import MailQueueCard from "../components/MailQueueCard.vue";
import MailRelayCard from "../components/MailRelayCard.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import TablePager from "../components/TablePager.vue";
import { useSectionError } from "../composables/useNotice";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { useBulkAction, useBulkSelection } from "../composables/useBulkSelection";
import { usePagination } from "../composables/usePagination";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { copyToClipboard } from "../utils/clipboard";
import { siteWebDomain } from "../utils/siteDomain";

// Site bağlamından (Web Siteleri > site > Mail) gelindiğinde o sitenin alan adını
// önceden seçmek için opsiyonel prop (profesyonel paneller gibi domain-içi Mail sekmesi).
const props = defineProps<{ domain?: string }>();

const { t, locale } = useI18n();
const { confirm } = useConfirm();
const { prompt } = usePrompt();
// Askıdaki abonelikte YAZMA düğmeleri baştan devre dışı + sebebi title/aria ile söylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma düğmeleri (yenile, tanıla, göster) kapıya girmez.
const { writeGate, locked: subscriptionLocked } = useSubscriptionGate();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
// Sunucu seviyesi mail araçları (dinleyiciler, rDNS, sertifika, spam) yalnız admin'e gösterilir;
// müşteri/bayide bu uçlar 403 döner.
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const workspace = ref<MailWorkspace | null>(null);
const isMailEnable = computed(() => workspace.value?.provider === "mailenable");
const selectedDomain = ref("");
const adminPassword = ref("");
const accountForm = reactive({ localPart: "", password: "", maxSizeMB: 1024 });
const autoResponderFor = ref("");
const autoResponderForm = reactive({ enabled: false, subject: "", message: "" });
const aliasForm = reactive({ localPart: "", target: "" });
const spam = ref<MailSpamSettings | null>(null);
const spamLoad = useSectionError();
const dns = ref<MailDns | null>(null);
const rdns = ref<RdnsCheck | null>(null);
const rdnsHost = ref("");
const spamAdvanced = ref(false);
const certificate = ref<MailCertificate | null>(null);
const certificateLoad = useSectionError();
const certForm = reactive({ hostName: "", contactEmail: "" });
let certificateRequest = 0;
let certificateDisposed = false;
const certificateIssuing = ref(false);
watch(() => [selectedDomain.value, authStore.session?.isAuthenticated, authStore.session?.user?.id, authStore.session?.user?.role], () => {
  ++certificateRequest;
  certificate.value = null;
  certificateLoad.clear();
  certForm.hostName = "";
}, { flush: "sync" });
onBeforeUnmount(() => { certificateDisposed = true; ++certificateRequest; });
const webmailOpening = ref(false);
const webmailDiagnosing = ref(false);
const webmailReport = ref("");

async function runWebmailDiagnose() {
  webmailDiagnosing.value = true;
  webmailReport.value = "";
  try {
    const sonuc = await diagnoseWebmailApi();
    webmailReport.value = sonuc.report;
    showNotice(t(sonuc.healthy ? "mail.webmail.diag_ok" : "mail.webmail.diag_fail"), !sonuc.healthy);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    webmailDiagnosing.value = false;
  }
}
const clientSettings = ref<MailClientSettings | null>(null);
const clientSettingsLoad = useSectionError();

/**
 * İstemci ayarları sunucunun GERÇEK dinleyicilerinden ölçülür; panel sabit port yazmaz.
 * Okunamadıysa tablo GİZLENİR ve "okunamadı" gösterilir — yanlış bir ayarı doğruymuş gibi
 * dağıtmak, hiç göstermemekten kötüdür.
 */
async function loadClientSettings() {
  if (!selectedDomain.value) {
    clientSettings.value = null;
    clientSettingsLoad.clear();
    return;
  }
  try {
    clientSettings.value = await getMailClientSettings(selectedDomain.value);
    clientSettingsLoad.clear();
  } catch (error) {
    clientSettings.value = null;
    clientSettingsLoad.capture(error);
  }
}

/** Protokol adları (IMAP/POP3/SMTP) marka değil sabit protokol adlarıdır; yön bilgisi çevrilir. */
function clientProtocolLabel(protocol: string): string {
  const name = (protocol || "").toLowerCase();
  if (name === "smtp") {
    return `${t("panel.mail.clientOutgoing")} (SMTP)`;
  }
  if (name === "imap" || name === "pop3") {
    return `${t("panel.mail.clientIncoming")} (${name.toUpperCase()})`;
  }
  return protocol.toUpperCase();
}

/*
  OTOMATİK YAPILANDIRMA (autodiscover/autoconfig) — ARAYÜZ BAĞLANTISI.

  Arka uç (`api/v1/mail/autoconfig/{domain}`) ve çevirileri aylardır duruyordu ama HİÇBİR ekran
  onu çağırmıyordu: kullanıcı ne yayınlayabiliyor, ne zincirin hangi halkasının kırık olduğunu
  görebiliyor, ne de kendi DNS sağlayıcısında açması gereken kayıtları okuyabiliyordu.

  Uçlar `[Authorize(Roles = "Administrator")]`: kural müşterinin web.config'ine yazar, IIS
  bağlaması ekler, DNS bölgesine kayıt basar. Bu yüzden kart yalnız yöneticiye gösterilir —
  kiracıya göstermek, her düğmesi 403 dönen bir ekran olurdu.
*/
const autoconfig = ref<MailAutodiscoverPublication | null>(null);
const autoconfigLoad = useSectionError();
const autoconfigBusy = ref(false);
const autoconfigPublishing = ref(false);
// En son ÖLÇÜLEN alan adı — başarısız ölçüm de sayılır. Sadece `autoconfig`'e bakılsaydı,
// ölçüm hata verdiğinde her `apply()` çağrısı ucu yeniden dener ve döngüye girerdi.
const autoconfigDomain = ref("");
const autoconfigContactEmail = ref("");

/**
 * Zinciri ölçer; hiçbir şey yazmaz. Sonuç okunamazsa kart "yayında" DEMEZ: bölüm hatası
 * gösterilir. Ölçemediğimiz bir kurulumu çalışıyor göstermek, kullanıcıyı sessizce ölü bir
 * autodiscover'a güvendirirdi.
 */
async function loadAutoconfig() {
  if (!selectedDomain.value || !isAdmin.value) {
    autoconfig.value = null;
    autoconfigDomain.value = "";
    autoconfigLoad.clear();
    return;
  }

  const domain = selectedDomain.value;
  autoconfigDomain.value = domain;
  autoconfigBusy.value = true;
  try {
    autoconfig.value = await getMailAutoconfig(domain);
    autoconfigLoad.clear();
  } catch (error) {
    autoconfig.value = null;
    autoconfigLoad.capture(error);
  } finally {
    autoconfigBusy.value = false;
  }
}

/**
 * Yayınlar. Sunucu İKİ ayrı başarı anahtarı döner (`..._published` / `..._published_partial`);
 * ikisi de "başarı" zarfıyla gelir, bu yüzden bildirimin rengi mesaja değil ölçüme
 * (`isPublished`) bakar — yarım kalmış bir zinciri yeşil göstermek yalan olurdu.
 */
async function publishAutoconfig() {
  if (!selectedDomain.value) return;
  autoconfigPublishing.value = true;
  autoconfigBusy.value = true;
  try {
    const envelope = await publishMailAutoconfig(selectedDomain.value, autoconfigContactEmail.value);
    autoconfig.value = envelope.data;
    autoconfigDomain.value = selectedDomain.value;
    autoconfigLoad.clear();
    showNotice(translateKey(envelope.message), !envelope.data.isPublished);
  } catch (error) {
    showError(error);
  } finally {
    autoconfigPublishing.value = false;
    autoconfigBusy.value = false;
  }
}

/** Yayını geri alır. Yıkıcı olduğu için ONAY ZORUNLU: istemciler ayarı bir daha bulamaz. */
async function unpublishAutoconfig() {
  const domain = selectedDomain.value;
  if (!domain) return;

  await confirm({
    title: t("panel.mail.autoconfig.unpublishConfirmTitle"),
    message: t("panel.mail.autoconfig.unpublishConfirmMessage"),
    impact: [`${t("panel.mail.domain")}: ${domain}`, `${t("panel.mail.autoconfig.site")}: ${autoconfig.value?.siteName || "—"}`],
    action: async () => {
      autoconfigBusy.value = true;
      try {
        const envelope = await unpublishMailAutoconfig(domain);
        autoconfig.value = envelope.data;
        autoconfigDomain.value = domain;
        autoconfigLoad.clear();
        showNotice(translateKey(envelope.message), false);
      } catch (error) {
        showError(error);
      } finally {
        autoconfigBusy.value = false;
      }
    }
  });
}

/**
 * Adım/durum etiketi. Çeviri YOKSA ham anahtar değil ham DEĞER gösterilir: sunucuya yarın
 * yedinci bir adım eklenirse kullanıcı "panel.mail.autoconfig.step.xyz" değil "xyz" görür.
 */
function autoconfigStepLabel(id: string) {
  return translateKey(`panel.mail.autoconfig.step.${id}`, id);
}

function autoconfigStateLabel(status: string) {
  return translateKey(`panel.mail.autoconfig.state.${status}`, status);
}

/**
 * Durumun görsel tonu. `warn` ile `unknown` AYRI tutulur: biri "eksik ama çalışır", diğeri
 * "ölçemedik". İkisini aynı renge boyamak, ölçülemeyen bir adımı bilinen bir eksik gibi
 * gösterirdi.
 */
function autoconfigStateTone(status: string) {
  if (status === "ok") return "ok";
  if (status === "fail") return "fail";
  return status === "warn" ? "warn" : "unknown";
}

const diagnostics = ref<MailDiagnostics | null>(null);
const diagLoading = ref(false);
const showAccountForm = ref(false);
const showAliasForm = ref(false);
const showListForm = ref(false);
const listForm = reactive({
  localPart: "",
  description: "",
  recipients: "",
  postingPolicy: "anyone" as MailListPostingPolicy,
  allowedSenders: ""
});
const showMailAdvanced = ref(false);
const listeners = ref<MailListenerSnapshot | null>(null);
const loading = ref(false);
const busy = ref(false);
const domainQuota = ref(0);
const outboundLimit = ref(0);
const outboundLimitState = ref<MailOutboundLimit | null>(null);
// "Okunamadı" BOŞ durumdan ayrı tutulur; şablonda SectionLoadError ile görünür + tekrar denenebilir.
const outboundLimitLoad = useSectionError();

async function loadOutboundLimit() {
  if (isMailEnable.value) {
    outboundLimitState.value = null;
    outboundLimitLoad.clear();
    return;
  }
  if (!selectedDomain.value || !isAdmin.value) {
    outboundLimitState.value = null;
    outboundLimitLoad.clear();
    return;
  }
  try {
    outboundLimitState.value = await getOutboundLimit(selectedDomain.value);
    outboundLimit.value = outboundLimitState.value.messagesPerHour;
    outboundLimitLoad.clear();
  } catch (error) {
    // Okunamadıysa "limitsiz" GÖSTERMİYORUZ: durum bilinmiyor demek, bilgi vermek değil.
    // Hata da YUTULMUYOR: aksi halde boş alan "limit yok" sanılırdı.
    outboundLimitState.value = null;
    outboundLimitLoad.capture(error);
  }
}

async function saveOutboundLimit() {
  busy.value = true;
  try {
    outboundLimitState.value = await setOutboundLimit(selectedDomain.value, outboundLimit.value);
    outboundLimit.value = outboundLimitState.value.messagesPerHour;
    showNotice(t("mail.outbound_limit_saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}
const selectedDomainInfo = computed(() =>
  workspace.value?.domains.find((item) => item.name.toLowerCase() === selectedDomain.value.toLowerCase()) ?? null);
const notice = ref("");
const noticeError = ref(false);
const noticeDetails = ref("");

// panel: posta domain listesi = mevcut mail domainleri (Stalwart) + henuz postasi acilmamis
// web sitesi alan adlari birlesimi. Boylece kullanici ayri bir "olustur" adimi gormeden
// listeden secip yonetir; postasi yoksa secince otomatik etkinlesir (openDomain).
const mailDomains = computed(() => {
  const map = new Map<string, { name: string; accountCount: number; countMeasured: boolean; active: boolean }>();
  for (const domain of workspace.value?.domains ?? []) {
    map.set(domain.name.toLowerCase(), { name: domain.name, accountCount: domain.accountCount,
      countMeasured: domain.accountCountMeasured === true && Number.isSafeInteger(domain.accountCount) && domain.accountCount >= 0, active: true });
  }
  for (const site of sitesStore.sites) {
    const domain = siteWebDomain(site);
    if (domain && !map.has(domain)) map.set(domain, { name: domain, accountCount: 0, countMeasured: false, active: false });
  }

  // Servis alt alanlarını GİZLE: mail./webmail./autodiscover. gibi host'lar posta sunucusunun KENDİ adresleridir
  // (ör. mail sertifikası alınırken HTTP-01 için siteye 'mail.<domain>' binding'i eklenir) → posta ALAN ADI değildir.
  // Yalnızca ÜST alan adı zaten listedeyse gizle (tek başına 'mail.x' adlı gerçek bir siteyi yanlışlıkla saklamayalım).
  const servicePrefixes = ["mail", "webmail", "autodiscover", "autoconfig", "smtp", "imap", "pop", "pop3", "mx"];
  const names = new Set(map.keys());
  return Array.from(map.values())
    .filter((item) => {
      const dot = item.name.indexOf(".");
      if (dot <= 0) return true;
      const firstLabel = item.name.slice(0, dot);
      const parent = item.name.slice(dot + 1);
      return !(servicePrefixes.includes(firstLabel) && names.has(parent));
    })
    .sort((a, b) => a.name.localeCompare(b.name));
});

// 500 posta kutusu / yüzlerce alan adı tek seferde DOM'a basılmaz (bkz. composables/usePagination.ts).
const mailAccounts = computed(() => workspace.value?.accounts ?? []);
const domainPager = usePagination(mailDomains);
const accountPager = usePagination(mailAccounts);
const accountSelection = useBulkSelection();
const bulk = useBulkAction();
const visibleAccountAddresses = computed(() => accountPager.items.value.map((account) => account.address));

/**
 * TOPLU POSTA KUTUSU SİLME — ONAY ZORUNLU, KISMİ BAŞARISIZLIK SESSİZ KALMAZ.
 *
 * Onay kaç kutunun ve HANGİ ADRESLERİN gideceğini yazar. Posta kutusu silmek kutudaki bütün
 * iletileri de siler; geri dönüşü yoktur. `DELETE /mail/accounts/{address}` tek adresliktir,
 * toplu ucu yoktur: adresler tek tek denenir, biri patlarsa döngü durmaz ve özet dürüst kalır.
 */
async function removeSelectedAccounts() {
  const targetDomain = selectedDomain.value;
  const isCurrent = () => selectedDomain.value === targetDomain;
  const targets = mailAccounts.value.filter((account) => accountSelection.isSelected(account.address));
  if (targets.length === 0) {
    return;
  }

  const onay = await confirm({
    title: t("panel.bulk.confirmDeleteTitle", { count: targets.length }),
    message: t("panel.mail.confirmBulkDelete", { count: targets.length }),
    impact: bulk.impactList(targets.map((account) => account.address)),
    requireText: String(targets.length)
  });
  if (!onay || !isCurrent()) {
    return;
  }

  const outcome = await bulk.runBulk(
    targets,
    (account) => account.address,
    async (account) => {
      const result = await deleteMailAccount(account.address);
      if (isCurrent()) apply(result);
    }, isCurrent
  );
  if (!isCurrent()) return;
  accountSelection.keepOnly((workspace.value?.accounts ?? []).map((account) => account.address));
  showNotice(bulk.deleteSummary(outcome), outcome.failures.length > 0);
}

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
    // Önceki alan adında yapılan seçim taşınmaz: başka domainin kutularını silmeye yol açardı.
    accountSelection.clear();
    accountPager.reset();
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
  // Alan adına bağlı ölçümler taşınmaz: başka bir alan adının sunucu/port bilgisi gösterilirdi.
  clientSettings.value = null;
  clientSettingsLoad.clear();
  // Otomatik yapılandırma raporu da alan adına aittir; taşınırsa başka bir alan adının
  // "yayında" rozeti gösterilirdi.
  autoconfig.value = null;
  autoconfigDomain.value = "";
  autoconfigLoad.clear();
  accountSelection.clear();
}

onMounted(async () => {
  if (sitesStore.sites.length === 0) {
    await sitesStore.fetchSites().catch(() => undefined);
  }
  await refresh();
  await ensureSiteDomain();
  await loadPublicBaseUrl();
});

// Opening a site tab only selects existing mail. Creation requires the list's explicit Manage action.
async function ensureSiteDomain() {
  const target = (props.domain ?? "").trim().toLowerCase();
  if (!target || !workspace.value?.installed || workspace.value?.statusMessage) {
    return;
  }
  const exists = workspace.value.domains.some((item) => item.name.toLowerCase() === target);
  if (exists) {
    selectedDomain.value = target;
    await refresh();
    return;
  }
  selectedDomain.value = "";
  showNotice(`${target}: ${t("panel.mail.mailOff")}`, false);
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
    // DURUM OLCULUR: "cagri patlamadi" ile "kayitlar bolgeye yazildi" ayni sey degildir.
    // Ikincil bolgede ve DNS rolu kurulu degilken yazma basariyla doner ama hicbir sey
    // yayinlanmaz; eski ekran bu durumda da "N kayit uygulandi" yaziyordu.
    if (result.data.published) {
      showNotice(t("panel.mail.dnsAppliedNotice", { count: result.data.appliedCount, zone: result.data.zoneName }), false);
    } else {
      showNotice(
        t("panel.mail.dnsNotPublishedNotice", { zone: result.data.zoneName, reason: t(result.data.statusMessage) }),
        true,
      );
    }
  });
}

async function loadCertificate() {
  if (certificateDisposed || certificateIssuing.value || !isAdmin.value) return;
  const request = ++certificateRequest;
  const domain = selectedDomain.value;
  const current = () => !certificateDisposed && request === certificateRequest;
  certificate.value = null;
  certificateLoad.clear();
  try {
    const result = await getMailCertificate(domain || undefined);
    if (!current()) return;
    if (domain && result.domain.trim().toLowerCase() !== domain.trim().toLowerCase()) {
      if (!certForm.hostName) certForm.hostName = `mail.${domain}`;
      throw new UserFacingError("panel.mail.certWrongDomain");
    }
    certificate.value = result;
    certificateLoad.clear();
    // Formu otomatik doldur ki kullanıcı mail sunucu adını/e-postayı elle yazmasın.
    if (!certForm.hostName && domain) {
      const savedHost = result.hostName.trim();
      const sameDomain = result.domain.trim().toLowerCase() === domain.trim().toLowerCase();
      certForm.hostName = sameDomain && savedHost ? savedHost : `mail.${domain}`;
    }
    if (!certForm.contactEmail && result.suggestedContactEmail) {
      certForm.contactEmail = result.suggestedContactEmail;
    }
    // Otomatik yapılandırma da hizmet ana adları için Let's Encrypt dener; aynı adresi ikinci kez
    // yazdırmayalım. Kullanıcı isterse kartta değiştirebilir.
    if (!autoconfigContactEmail.value && result.suggestedContactEmail) {
      autoconfigContactEmail.value = result.suggestedContactEmail;
    }
  } catch (error) {
    if (!current()) return;
    certificate.value = null;
    certificateLoad.capture(error);
  }
}

async function issueCert(reuseExistingOnly = false) {
  if (certificateDisposed || certificateIssuing.value || busy.value || !isAdmin.value) return;
  const request = ++certificateRequest;
  const current = () => !certificateDisposed && request === certificateRequest;
  const domain = selectedDomain.value;
  const host = certForm.hostName || `mail.${domain}`;
  const email = certForm.contactEmail;
  certificateIssuing.value = true;
  busy.value = true;
  certificate.value = null;
  certificateLoad.clear();
  showNotice("", false);
  try {
    const result = await issueMailCertificate(domain, host, email, reuseExistingOnly);
    if (!current()) return;
    if (result.domain.trim().toLowerCase() !== domain.trim().toLowerCase() ||
      result.hostName.trim().toLowerCase() !== host.trim().toLowerCase())
      throw new UserFacingError("panel.mail.certWrongDomain");
    certificate.value = result;
    certificateLoad.clear();
    showNotice(
      certificate.value.active
        ? t("panel.mail.certIssued")
        : t("panel.mail.certUnverified"),
      !certificate.value.active);
  } catch (error) {
    if (current()) showError(error);
  } finally {
    certificateIssuing.value = false;
    if (!certificateDisposed) busy.value = false;
  }
}

// Tek tık webmail (profesyonel paneller gibi): Roundcube kurulu/yayında değilse panel otomatik kurar,
// sonra yeni sekmede açar. Kullanıcı tam e-posta adresi + şifresiyle giriş yapar.
async function openWebmail() {
  if (busy.value || webmailOpening.value || subscriptionLocked.value) return;
  // Pop-up engelleyiciye takılmamak için sekmeyi tıklama anında aç, URL gelince yönlendir.
  const tab = window.open("", "_blank");
  if (!tab) {
    showNotice(t("panel.common.popupBlocked"), true);
    return;
  }
  tab.opener = null;
  // Sekmeyi boş bırakma: hazırlık sürerken (ilk açılış birkaç saniye + olası kurulum) "hazırlanıyor" göster,
  // yoksa kullanıcı boş sayfa görüp "açılmadı" sanıyor. URL gelince oraya yönlendiririz; hata olursa sekmede yazarız.
  // Aşağıdaki renkler BİLEREK sabit: bu HTML yepyeni ve BOŞ bir sekmeye yazılıyor, panelin
  // stil sayfası orada YÜKLÜ DEĞİL — dolayısıyla `var(--...)` belirteçleri o belgede
  // tanımsızdır. Açılış sayfası bu yüzden kendi renklerini ve dilini taşır.
  if (tab) {
    tab.document.write(webmailPage(locale.value, t("panel.mail.webmailPreparingTitle"), t("panel.mail.webmailPreparingHint")));
    tab.document.close();
  }
  webmailOpening.value = true;
  await run(async () => {
    try {
      const result = await openWebmailApi();
      const url = toReachableUrl(result.url);
      // `toReachableUrl` http(s) DIŞINDAKİ her şeye boş dizge döner. Boşu atamak sessizce
      // YANLIŞ olurdu: `location.href = ""` gezinmez, bulunduğu sayfayı YENİDEN YÜKLER —
      // yani `window.location` dalında yönetici panelin kendisini tazeler ve "açılmadı"
      // sanır. Boş adres bir adres değildir; aşağıdaki catch'e düşsün, sekmede neden yazılsın.
      if (!url) {
        throw new UserFacingError("panel.common.invalidLink");
      }
      if (tab.closed) throw new UserFacingError("panel.common.popupClosed");
      tab.location.replace(url);
    } catch (error) {
      // Sekmeyi sessizce kapatma → kullanıcı ne olduğunu anlamaz. Nedeni sekmede göster.
      if (!tab.closed && tab.document?.body) {
        tab.document.open();
        tab.document.write(webmailPage(locale.value, t("panel.mail.webmailFailedTitle"), t("panel.mail.webmailFailedHint"), true));
        tab.document.close();
      }
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
  // window.prompt kaldırıldı: tarayıcı diyalogları engellenirse kullanıcı hiçbir şey görmüyordu.
  const oldu = await copyToClipboard(value);
  showNotice(t(oldu ? "panel.mail.dnsCopied" : "panel.common.copyFailed"), !oldu);
}

async function configure() {
  await run(async () => {
    apply(await configureMail(adminPassword.value));
    adminPassword.value = "";
    showNotice(t("panel.mail.configured"), false);
  });
}

async function removeDomain() {
  const domain = selectedDomain.value;
  if (!domain) return;

  const impact = [
    `${t("panel.mail.domainName")}: ${domain}`,
    `${t("panel.mail.mailboxes")}: ${workspace.value?.accounts.length ?? 0}`,
    `${t("panel.mail.aliases")}: ${workspace.value?.aliases.length ?? 0}`,
    `${t("panel.mail.lists")}: ${workspace.value?.lists.length ?? 0}`
  ];

  // Yüksek riskli onay: alan adıyla birlikte TÜM posta kutuları ve içindeki postalar silinir.
  await confirm({
    title: t("panel.confirm.deleteMailDomainTitle", { name: domain }),
    message: t("panel.mail.confirmDeleteDomain"),
    impact,
    requireText: domain,
    action: async () => {
      await run(async () => {
        apply(await deleteMailDomain(domain));
        selectedDomain.value = "";
        showNotice(t("panel.mail.domainDeleted"), false);
      });
    },
  });
}

async function addAccount() {
  // Plesk gibi: kısa parolayı SUNUCUYA göndermeden net uyar (yoksa Stalwart reddi sağır "işlem tamamlanamadı" olurdu).
  if ((accountForm.password ?? "").trim().length < 8) {
    showNotice(t("mail.weak_password"), true);
    return;
  }
  await run(async () => {
    apply(await createMailAccount(selectedDomain.value, accountForm.localPart, accountForm.password, accountForm.maxSizeMB));
    accountForm.localPart = "";
    accountForm.password = "";
    showAccountForm.value = false;
    showNotice(t("panel.mail.mailboxCreated"), false);
  });
}

async function removeAccount(address: string) {
  const account = workspace.value?.accounts.find((item) => item.address === address) ?? null;
  const impact = [`${t("panel.mail.mailboxes")}: ${address}`];
  if (account) {
    // Onay metnindeki birim listedekiyle AYNI olmalı: kullanıcı silmeden önce gördüğü sayıyı
    // burada tekrar okur, iki ekranın farklı taban söylemesi doğrulamayı anlamsız kılardı.
    impact.push(`${t("panel.mail.quota")}: ${account.currentSizeMB} / ${account.maxSizeMB} ${t("panel.units.mib")}`);
  }

  // Normal onay: tek posta kutusu (dar kapsam) → ad yazdırmaya gerek yok.
  await confirm({
    title: t("panel.confirm.deleteMailboxTitle", { name: address }),
    message: t("panel.mail.confirmDelete"),
    impact,
    action: async () => {
      await run(async () => {
        apply(await deleteMailAccount(address));
        showNotice(t("panel.mail.mailboxDeleted"), false);
      });
    },
  });
}

// Otomatik yanıt: panel yönetir (backend sistem sieve script'i üretir; Plesk autoresponder paritesi).
async function openAutoResponder(address: string) {
  autoResponderFor.value = address;
  autoResponderForm.enabled = false;
  autoResponderForm.subject = "";
  autoResponderForm.message = "";
  try {
    const current = await getAutoResponder(address);
    autoResponderForm.enabled = current.enabled;
    autoResponderForm.subject = current.subject;
    autoResponderForm.message = current.message;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function saveAutoResponder() {
  if (!autoResponderFor.value) return;
  await run(async () => {
    const saved = await setAutoResponder({
      address: autoResponderFor.value,
      enabled: autoResponderForm.enabled,
      subject: autoResponderForm.subject,
      message: autoResponderForm.message
    });
    autoResponderForm.enabled = saved.enabled;
    autoResponderForm.subject = saved.subject;
    autoResponderForm.message = saved.message;
    showNotice(t("panel.mail.autoResponderSaved"), false);
  });
}

async function changePassword(address: string) {
  // window.prompt DEĞİL: posta parolası açıkta görünüyordu.
  const password = await prompt({
    title: t("panel.mail.enterNewPassword"),
    message: address,
    label: t("panel.form.password"),
    password: true,
    minLength: 8
  });
  if (!password) return;
  if (password.trim().length < 8) {
    showNotice(t("mail.weak_password"), true);
    return;
  }
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
  const alias = workspace.value?.aliases.find((item) => item.address === address) ?? null;
  const impact = [`${t("panel.mail.aliasAddress")}: ${address}`];
  if (alias?.target) {
    impact.push(`${t("panel.mail.aliasTarget")}: ${alias.target}`);
  }

  // Normal onay: yalnız yönlendirme kaydı silinir, hedef posta kutusu etkilenmez.
  await confirm({
    title: t("panel.confirm.deleteAliasTitle", { name: address }),
    message: t("panel.mail.confirmDelete"),
    impact,
    action: async () => {
      await run(async () => {
        apply(await deleteMailAlias(address));
        showNotice(t("panel.mail.aliasDeleted"), false);
      });
    },
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
  if (isMailEnable.value && listForm.postingPolicy !== "anyone") {
    showError(t("panel.mail.mailEnableListPolicyHint"));
    return;
  }
  await run(async () => {
    apply(
      await createMailList(
        selectedDomain.value,
        listForm.localPart,
        parseRecipients(listForm.recipients),
        listForm.description,
        listForm.postingPolicy,
        parseRecipients(listForm.allowedSenders)
      )
    );
    listForm.localPart = "";
    listForm.description = "";
    listForm.recipients = "";
    listForm.postingPolicy = "anyone";
    listForm.allowedSenders = "";
    showListForm.value = false;
    showNotice(t("panel.mail.listCreated"), false);
  });
}

// Panelin genel adresi (yalniz yonetici). Bos = List-Unsubscribe basligi eklenmez.
const publicBaseUrl = ref("");

async function loadPublicBaseUrl() {
  if (!isAdmin.value) return;
  try {
    publicBaseUrl.value = await getMailPublicBaseUrl();
  } catch {
    // Okunamamasi liste yonetimini engellememeli; alan bos kalir.
    publicBaseUrl.value = "";
  }
}

async function savePublicBaseUrl() {
  await run(async () => {
    publicBaseUrl.value = await setMailPublicBaseUrl(publicBaseUrl.value);
    showNotice(t("panel.mail.publicBaseUrlSaved"), false);
  });
}

function policyLabel(policy: MailListPostingPolicy) {
  if (policy === "subscribers") return t("panel.mail.listPolicySubscribers");
  if (policy === "allowed") return t("panel.mail.listPolicyAllowed");
  if (policy === "native-members") return t("panel.mail.listPolicyNativeMembers");
  if (policy === "native-password") return t("panel.mail.listPolicyNativePassword");
  if (policy === "native-moderated") return t("panel.mail.listPolicyNativeModerated");
  if (policy === "native-disabled") return t("panel.mail.listPolicyNativeDisabled");
  if (policy === "native-unknown") return t("panel.mail.listPolicyNativeUnknown");
  return policy === "anyone" ? t("panel.mail.listPolicyAnyone") : policy;
}

// Gonderim politikasi degistirme. Aboneler DEGISTIRILMEZ; yalnizca kural gonderilir.
async function editListPolicy(list: MailList) {
  if (isMailEnable.value) return;
  const value = await prompt({
    title: t("panel.mail.listPostingPolicyPrompt"),
    message: `${list.address} — ${t("panel.mail.listPostingPolicyOptions")}`,
    label: t("panel.mail.listPostingPolicy"),
    defaultValue: list.postingPolicy
  });
  if (value === null) return;

  const policy = value.trim().toLowerCase();
  if (policy !== "anyone" && policy !== "subscribers" && policy !== "allowed") {
    showError(t("panel.mail.listPolicyInvalid"));
    return;
  }

  // "herkes" DISI kiplerde izinli gonderenler sorulur: liste yoneticisi burada yoksa, kendi
  // listesine duyuru yazamaz hale gelir.
  let allowed = list.allowedSenders;
  if (policy !== "anyone") {
    const senders = await prompt({
      title: t("panel.mail.listAllowedSenders"),
      message: t("panel.mail.listAllowedSendersHint"),
      label: t("panel.mail.listAllowedSenders"),
      defaultValue: list.allowedSenders.join(", "),
      allowEmpty: true
    });
    if (senders === null) return;
    allowed = parseRecipients(senders);
  }

  await run(async () => {
    apply(await setMailListPolicy(list.address, policy, allowed));
    showNotice(t("panel.mail.listPolicySaved"), false);
  });
}

// Tek abone ekleme: sunucu guncel kumeyi okuyup ustune ekler; ekrandaki kopya gonderilmez.
async function subscribe(address: string) {
  const value = await prompt({
    title: t("panel.mail.listAddSubscriberPrompt"),
    message: address,
    label: t("panel.mail.listSubscribers")
  });
  if (value === null) return;

  const subscriber = value.trim();
  if (!subscriber) return;

  await run(async () => {
    apply(await subscribeToMailList(address, subscriber));
    showNotice(t("panel.mail.listSubscribed", { address: subscriber }), false);
  });
}

// Abonelikten cikarma. Onay istenir cunku geri donusu YOKTUR: cikarilan kisi, bir daha
// eklenene kadar listeye giden hicbir duyuruyu almaz ve bunu fark etmesinin bir yolu da yoktur.
async function unsubscribe(address: string, subscriber: string) {
  await confirm({
    title: t("panel.confirm.unsubscribeMailListTitle", { name: subscriber }),
    message: t("panel.mail.confirmUnsubscribe"),
    impact: [`${t("panel.mail.listAddress")}: ${address}`, `${t("panel.mail.listSubscribers")}: ${subscriber}`],
    action: async () => {
      await run(async () => {
        apply(await unsubscribeFromMailList(address, subscriber));
        showNotice(t("panel.mail.listUnsubscribed", { address: subscriber }), false);
      });
    }
  });
}

// EK ADRES ekleme. Yerel kisim yeterli ("info"); sunucu LISTENIN alan adiyla tamamlar. Tam adres
// de yazilabilir, ama alan adi kullanicinin kapsaminda olmak ZORUNDA -- sunucu bunu dogruluyor,
// arayuz o kontrolu tekrar etmiyor (iki yerde yazilan kural, birinde gevserdi).
async function addListAlias(listAddress: string) {
  const value = await prompt({
    title: t("panel.mail.listAddAliasPrompt"),
    message: `${listAddress} — ${t("panel.mail.listAliasesHint")}`,
    label: t("panel.mail.listAliases")
  });
  if (value === null) return;

  const alias = value.trim();
  if (!alias) return;

  await run(async () => {
    apply(await addMailListAlias(listAddress, alias));
    showNotice(t("panel.mail.listAliasAdded", { address: alias }), false);
  });
}

// EK ADRES kaldirma. Onay istenir cunku sessiz bir hizmet kesintisidir: o adrese yazanlar
// bundan sonra teslim edilemedi hatasi alir ve listeye yazdiklarini sanmaya devam ederler.
async function removeListAlias(listAddress: string, alias: string) {
  await confirm({
    title: t("panel.confirm.removeMailListAliasTitle", { name: alias }),
    message: t("panel.mail.confirmRemoveAlias"),
    impact: [`${t("panel.mail.listAddress")}: ${listAddress}`, `${t("panel.mail.listAliases")}: ${alias}`],
    action: async () => {
      await run(async () => {
        apply(await removeMailListAlias(listAddress, alias));
        showNotice(t("panel.mail.listAliasRemoved", { address: alias }), false);
      });
    }
  });
}

async function editListRecipients(address: string, current: string[]) {
  const value = await prompt({
    title: t("panel.mail.listEditPrompt"),
    message: address,
    label: t("panel.mail.listRecipients"),
    defaultValue: current.join(", "),
    // Alanı boşaltmak, listenin alıcılarını temizlemenin TEK yolu — window.prompt buna
    // izin veriyordu, yeni diyalog da vermeli.
    allowEmpty: true
  });
  if (value === null) return;
  await run(async () => {
    apply(await setMailListRecipients(address, parseRecipients(value)));
    showNotice(t("panel.mail.listUpdated"), false);
  });
}

async function removeList(address: string) {
  const list = workspace.value?.lists.find((item) => item.address === address) ?? null;
  const impact = [`${t("panel.mail.listAddress")}: ${address}`];
  if (list?.recipients.length) {
    impact.push(`${t("panel.mail.listSubscribers")}: ${list.recipients.length}`);
  }

  // Normal onay: yalnız dağıtım listesi silinir, alıcıların posta kutuları etkilenmez.
  await confirm({
    title: t("panel.confirm.deleteMailListTitle", { name: address }),
    message: t("panel.mail.confirmDelete"),
    impact,
    action: async () => {
      await run(async () => {
        apply(await deleteMailList(address));
        showNotice(t("panel.mail.listDeleted"), false);
      });
    },
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
  if (next.listsMeasured === false && workspace.value?.selectedDomain === next.selectedDomain) {
    next = { ...next, lists: workspace.value.lists };
  }
  workspace.value = next;
  if (next.selectedDomain) {
    selectedDomain.value = next.selectedDomain;
  }
  const domain = next.domains.find((item) => item.name === selectedDomain.value);
  domainQuota.value = domain?.maxSizeMB ?? 0;
  // Giden posta limiti de seçili alan adıyla birlikte tazelenir; aksi halde kart başka bir
  // alan adının değerini gösterirdi. İstemci ayarları da aynı sebeple alan adına bağlı tazelenir.
  void loadOutboundLimit();
  void loadClientSettings();
  // Spam + sertifika uçları admin-only; müşteri/bayide çağırma (gereksiz 403 olmasın).
  if (isAdmin.value && next.configured && !spam.value) {
    void loadSpam();
  }
  if (isAdmin.value && next.configured && !certificate.value) {
    void loadCertificate();
  }
  // Otomatik yapılandırma durumu alan adı BAŞINA ölçülür ve ölçümü pahalıdır (IIS site listesi,
  // web.config, DNS bölgesi). Bu yüzden her `apply()` çağrısında değil, yalnız ölçülen alan adı
  // değiştiğinde tazelenir; kullanıcı ayrıca "Durumu ölç" ile her an elle yenileyebilir.
  if (isAdmin.value && next.configured && selectedDomain.value && autoconfigDomain.value !== selectedDomain.value) {
    void loadAutoconfig();
  }
}

async function loadSpam() {
  try {
    spam.value = await getMailSpam();
    spamLoad.clear();
  } catch (error) {
    spam.value = null;
    spamLoad.capture(error);
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
  const value = await prompt({
    title: t("panel.mail.enterQuota"),
    message: address,
    label: t("panel.mail.quota"),
    defaultValue: String(currentMax)
  });
  if (value === null) return;
  const mb = Number(value);
  if (Number.isNaN(mb) || mb < 0) return;
  await run(async () => {
    apply(await setMailAccountQuota(address, mb));
    showNotice(t("panel.mail.quotaSaved"), false);
  });
}

/**
 * Sunucunun döndürdüğü mesaj ANAHTARINI metne çevirir; çevirisi yoksa `fallback` gösterilir.
 * Tek kaynak: aynı "çeviri var mı" kontrolü hem hata hem başarı yollarında kopyalanmasın.
 */
function translateKey(key: string, fallback = key) {
  if (!key) {
    return fallback;
  }
  const translated = t(key);
  return translated === key ? fallback : translated;
}

function resolveApiMessage(error: unknown) {
  return translateKey(extractApiMessage(error));
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
.webmail-report {
  margin: var(--sp-8) 0 0;
  padding: var(--sp-8) var(--sp-10);
  max-height: 20rem;
  overflow: auto;
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  font-family: var(--font-mono);
  font-size: var(--fs-2xs);
  line-height: 1.45;
  white-space: pre-wrap;
  word-break: break-word;
}

.mail-loading {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-10);
}

.mail-spinner {
  width: 15px;
  height: 15px;
  border: 2px solid color-mix(in srgb, var(--accent-1) 35%, transparent);
  border-top-color: var(--accent-1);
  border-radius: 50%;
  animation: mail-spin 0.7s linear infinite;
}

@keyframes mail-spin {
  to { transform: rotate(360deg); }
}

.mail-panel {
  display: grid;
  gap: var(--sp-12);
  align-content: start;
}

.notice-details pre {
  margin: var(--sp-6) 0 0;
  padding: var(--sp-8) var(--sp-10);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

/*
  BÖLÜM SINIRI — panelin DOĞRUDAN çocuğu olan başlıklar için.

  SAYILDI: alan adı çalışma alanında aynı anda 9 üst düzey blok var ve bunların dördü (posta
  kutuları, takma adlar, listeler, gelişmiş bölümler) kart DEĞİL, panelin akışına serpilmiş çıplak
  başlık + tablo çiftleri. Kalanları kart. Yani aynı işi gören iki desen yan yana duruyordu ve
  çıplak olanlarda bölümün nerede bittiği hiçbir şeyle söylenmiyordu: panel ızgarasının boşluğu
  (12px) başlık ile ÜSTTEKİ tablonun arasında da, başlık ile KENDİ tablosunun arasında da aynıydı.
  Göz, başlığın hangi tabloya ait olduğunu boşluktan çıkaramıyordu.

  Çözüm bir çizgi: başlığın ÜSTÜNE bölüm sınırı çizilir, altındaki boşluk aynı kalır — böylece
  başlık kendi içeriğine yapışır, öncekinden ayrılır. Kartların içindeki başlıklar ETKİLENMEZ:
  `>` seçicisi yalnız panelin doğrudan çocuklarını tutar, `<template>` öge üretmediği için
  koşullu bloklar da doğrudan çocuktur. İlk başlık (varsa) çizgisiz kalır.
*/
.mail-panel > .section-head {
  margin-top: var(--sp-4);
  padding-top: var(--sp-16);
  border-top: 1px solid var(--panel-border);
}

.section-head button,
.row-actions button {
  width: auto;
}

/*
  KOTALAR VE SINIRLAR KARTI. İki şerit tek kartta; aralarındaki ince çizgi "iki ayrı ayar" bilgisini
  kutu kenarlığı olmadan da taşır (bkz. şablondaki gerekçe).
*/
.limits-card .quota-strip + .quota-strip {
  padding-top: var(--sp-12);
  border-top: 1px solid var(--panel-border);
}

.domain-select {
  min-width: min(320px, 60vw);
}

.address-field {
  display: flex;
  align-items: center;
  gap: var(--sp-6);
}

.address-field span {
  color: var(--text-muted);
  white-space: nowrap;
}

.mail-row {
  grid-template-columns: 30px minmax(200px, 1.6fr) 0.7fr 0.7fr auto;
}

.select-cell {
  display: flex;
  align-items: center;
}

.select-cell input {
  width: 16px;
  height: 16px;
  margin: 0;
}

.bulk-bar {
  display: flex;
  align-items: center;
  gap: var(--sp-10);
  flex-wrap: wrap;
  padding: var(--sp-8) var(--sp-10);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.bulk-bar button {
  width: auto;
}

.client-row {
  grid-template-columns: minmax(140px, 1fr) minmax(180px, 1.6fr) 0.5fr 0.8fr;
  align-items: center;
}

.client-settings {
  margin-top: var(--sp-16);
}

.client-settings h4 {
  margin: 0 0 var(--sp-2);
}

.client-username {
  margin-top: var(--sp-8);
}

.copyable {
  cursor: pointer;
  text-decoration: underline dotted;
}

.diag-grid {
  display: grid;
  gap: var(--sp-8);
}

.diag-item {
  display: flex;
  align-items: flex-start;
  gap: var(--sp-12);
  padding: var(--sp-12) var(--sp-12);
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

/* "warn" (eksik ama çalışır) ile "unknown" (ölçemedik) AYRI görünür: ikisini aynı yapmak,
   ölçülemeyen bir adımı bilinen bir eksik gibi göstermek olurdu. */
.diag-item.warn {
  border-color: color-mix(in srgb, var(--warn-1) 35%, transparent);
  background: var(--warn-bg);
}

.diag-dot {
  width: 9px;
  height: 9px;
  margin-top: var(--sp-6);
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

.diag-item.warn .diag-dot {
  background: var(--warn-1);
  box-shadow: 0 0 0 3px var(--warn-bg);
}

.diag-body {
  display: grid;
  gap: var(--sp-4);
  min-width: 0;
  flex: 1;
}

.diag-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-8);
}

.diag-detail {
  color: var(--text-muted);
  word-break: break-word;
}

.diag-detail code {
  padding: 1px var(--sp-6);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  font-size: 0.92em;
}

.autoconfig-card {
  display: grid;
  gap: var(--sp-12);
  align-content: start;
}

.autoconfig-card h4 {
  margin: 0 0 var(--sp-2);
}

.autoconfig-card .meta {
  margin: 0;
}

.autoconfig-meta {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--sp-10);
  margin: 0;
}

.autoconfig-meta dt {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.autoconfig-meta dd {
  margin: var(--sp-2) 0 0;
  font-size: var(--fs-xs);
  word-break: break-all;
}

.autoconfig-block {
  display: grid;
  gap: var(--sp-6);
}

.autoconfig-probes {
  display: grid;
  gap: var(--sp-6);
  margin: 0;
  padding: 0;
  list-style: none;
}

.autoconfig-probes li {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  flex-wrap: wrap;
}

/* Adres uzun ve kırpılmamalı: kullanıcı tam olarak neyi açacağını görmeli. */
.autoconfig-probes a {
  font-size: var(--fs-2xs);
  overflow-wrap: anywhere;
}

.autoconfig-probes button {
  width: auto;
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
  gap: var(--sp-12);
}

.mail-domain-bar .current-domain {
  font-size: var(--fs-lg);
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
  font-size: var(--fs-xs);
  color: var(--text-soft);
}

.list-identity {
  display: flex;
  flex-direction: column;
  gap: var(--sp-2);
  min-width: 0;
}

.list-description {
  color: var(--text-muted);
  overflow-wrap: anywhere;
}

.list-subscribers {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-6);
  min-width: 0;
}

.subscriber-chip {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-4);
  padding: var(--sp-2) var(--sp-4) var(--sp-2) var(--sp-8);
  border: 1px solid var(--panel-border);
  border-radius: 999px;
  background: var(--surface-2);
  font-size: var(--fs-xs);
  overflow-wrap: anywhere;
}

/* Cikarma dugmesi her zaman gorunur: yalnizca uzerine gelince beliren bir "x", dokunmatik
   ekranda hic bulunamaz ve abone cikarmak imkansiz hale gelirdi. */
.chip-remove {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  padding: 0;
  border: 0;
  border-radius: 50%;
  background: transparent;
  color: var(--text-muted);
  font-size: var(--fs-md);
  line-height: 1;
  cursor: pointer;
}

.chip-remove:hover:not(:disabled) {
  background: var(--danger-bg);
  color: var(--danger-text);
}

.chip-remove:disabled {
  cursor: not-allowed;
  opacity: 0.5;
}

/* EK ADRESLER liste kimliginin altinda, abone rozetlerinden GORSEL OLARAK AYRI durur: ikisi de
   rozet olsaydi "listeye yazabilen ikinci adres" ile "postayi alan kisi" karisirdi. */
.list-aliases {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-4);
  margin-top: var(--sp-2);
}

.alias-chip {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-4);
  padding: 1px var(--sp-4) 1px var(--sp-8);
  border: 1px dashed var(--panel-border);
  border-radius: 999px;
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  overflow-wrap: anywhere;
}

.list-limitation {
  margin: var(--sp-8) 0 0;
}

.list-policy {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

/* "Herkes gonderebilir" gorsel olarak AYIRT EDILIR: en gevsek durum, gozden kacmamali. */
.list-policy.policy-open {
  color: var(--warn-text);
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
  font-size: var(--fs-2xs);
  color: var(--text-soft);
}

.dns-value {
  overflow-wrap: anywhere;
  font-size: var(--fs-2xs);
  color: var(--text-soft);
}

/* YIKICI DUGME `secondary` ILE BIRLESTIRILMEZ -- OLCULMUS OKUNMAZLIK.
   `class="secondary compact danger"` yaziliyordu. base.css sirasi geregi `button.danger`
   gradyani `button.secondary` zeminini EZIYOR (ayni ozgulluk, sonra geliyor), yani dugme
   zaten KIRMIZI ciziliyordu; buradaki `.danger { color }` kurali ise kapsamli oldugu icin
   (0,2,0) base'in (0,1,1) murekkebini eziyor ve metni `--danger-text` yapiyordu.
   OLCULDU -- kirmizi metin kirmizi dolgu uzerinde:
     koyu tema  #f0a3a9 / #b54f57 = 2.49  ... / #97333c = 3.68
     acik tema  #a3343e / #c2414c = 1.33  ... / #97333c = 1.09
   Acik temada 1.09, yani "Sil" yazisi pratikte GORUNMUYORDU. Kural silindi; dugmeler
   BackupView'in kanitlanmis kalibina (`class="danger compact"`) gecti ve base'in olculmus
   murekkebini (#fff1f1: 4.53 / 4.60) kullaniyor.
   `.status-pill.danger` etkilenmez: rengini base.css zaten AYNI belirtecle veriyor. */

.quota-strip {
  display: flex;
  gap: var(--sp-12);
  align-items: flex-end;
  flex-wrap: wrap;
}

.inline-field {
  display: flex;
  gap: var(--sp-8);
  align-items: center;
}

.inline-field input {
  width: 120px;
}

.inline-field .hint {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.rdns-input {
  max-width: 240px;
}

.rdns-result {
  display: grid;
  gap: var(--sp-10);
  padding: var(--sp-12) var(--sp-16);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.rdns-status {
  padding: var(--sp-8) var(--sp-12);
  border-radius: var(--radius-md);
  font-weight: 650;
  font-size: var(--fs-sm);
}

.rdns-status.ok {
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-text);
}

.rdns-status.warn {
  background: color-mix(in srgb, var(--warn-1) 16%, transparent);
  color: var(--warn-text);
}

.rdns-status.danger {
  background: color-mix(in srgb, var(--danger-1) 16%, transparent);
  color: var(--danger-text);
}

.rdns-meta {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--sp-10);
  margin: 0;
}

.rdns-meta dt {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.rdns-meta dd {
  margin: var(--sp-2) 0 0;
  font-size: var(--fs-xs);
  word-break: break-all;
}

.rdns-guide {
  margin: 0;
}

@media (max-width: 760px) {
  .rdns-meta,
  .autoconfig-meta {
    grid-template-columns: 1fr;
  }

  .section-head {
    flex-direction: column;
  }
}
</style>
