<template>
  <section class="panel module-shell backup-panel">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.backup.title") }}</h3>
        <p class="meta">{{ t("panel.backup.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ t("panel.backup.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div class="module-toolbar">
      <div class="segmented-tabs">
        <button :class="{ active: activeView === 'list' }" @click="activeView = 'list'">
          {{ t("panel.backup.list") }}
        </button>
        <button :class="{ active: activeView === 'create' }" @click="activeView = 'create'">
          {{ t("panel.backup.new") }}
        </button>
        <button :class="{ active: activeView === 'schedules' }" @click="openSchedules">
          {{ t("panel.backup.schedules") }}
        </button>
      </div>
      <span class="status-pill">{{ backups.length }} {{ t("panel.backup.records") }}</span>
    </div>

    <div v-if="activeView === 'list'" class="data-table backup-table">
      <!-- BEŞ sütun. Önceden sekizdi ve dördü (tür, hedef türü, boyut, tarih) tek kelimelik
           değerler için tam sütun harcıyordu; ad, yol ve dört düğme aynı satırda sıkışıyordu.
           Birbirine ait olanlar tek hücrede: kaynağın türü kaynağın parçası, boyut hedefin
           parçası, oluşturma zamanı durumun parçası. -->
      <div class="data-row table-head backup-row">
        <span>{{ t("panel.backup.name") }}</span>
        <span>{{ t("panel.backup.source") }}</span>
        <span>{{ t("panel.backup.targetType") }}</span>
        <span>{{ t("panel.backup.statusColumn") }}</span>
        <span>{{ t("panel.software.action") }}</span>
      </div>
      <template v-for="backup in pager.items.value" :key="backup.id">
        <div class="data-row backup-row">
          <div class="cell-main">
            <strong>{{ backup.name }}</strong>
            <span class="pill-row">
              <!-- Zincirdeki YER: artımsal halka mı, zincirin tam yedeği mi. Zinciri olmayan
                   tekil yedekte rozet YOKTUR (yanlış bir bağ hissi vermesin). -->
              <span v-if="backup.chainId && backup.backupMode === 'incremental'" class="status-pill chain-link">
                {{ t("panel.backup.chain.linkBadge", { index: backup.chainIndex }) }}
              </span>
              <span v-else-if="backup.chainId" class="status-pill chain-head">
                {{ t("panel.backup.chain.headBadge") }}
              </span>
              <span v-if="backup.itemCount > 0" class="status-pill">
                {{ t("panel.backup.itemCount", { count: backup.itemCount }) }}
              </span>
            </span>
            <small>{{ backup.id }}</small>
          </div>
          <div class="cell-main">
            <strong>{{ backupSourceLabel(backup) }}</strong>
            <small>{{ backupKindLabel(backup.type) }}</small>
          </div>
          <div class="cell-main">
            <strong>{{ backup.remoteUri ? t("panel.backup.remoteTarget") : t("panel.backup.localTarget") }}</strong>
            <small>{{ formatBytes(backup.size) }}</small>
          </div>
          <div class="cell-main">
            <span class="pill-row">
              <span :class="['status-pill', statusTone(backup.status)]">{{ formatStatus(backup.status) }}</span>
              <!-- Kilit rozeti: geri yüklerken parola isteneceğini kullanıcı LİSTEDE görsün. -->
              <span v-if="backup.encrypted" class="status-pill" :title="t('panel.backup.encryptedHint')">
                {{ t("panel.backup.encryptedBadge") }}
              </span>
              <!-- Yarım kalan/riskli adımlar sessizce yutulmaz: rozet TIKLANIR ve sebepler açılır
                   (ipucu metni dokunmatik ekranda hiç görünmüyordu). -->
              <button
                v-if="backup.warnings?.length"
                type="button"
                class="status-pill warn warning-toggle"
                :aria-expanded="expandedWarnings === backup.id"
                @click="toggleWarnings(backup.id)">
                {{ t("panel.backup.warningsBadge", { count: backup.warnings.length }) }}
              </button>
            </span>
            <small>{{ new Date(backup.createdAtUtc).toLocaleString() }}</small>
          </div>
          <div class="row-actions">
            <button class="secondary compact row-button" v-bind="writeGate(busy)" @click="restore(backup)">
              {{ t("panel.backup.restore") }}
            </button>
            <button
              v-if="backup.format === 'zip'"
              class="secondary compact row-button"
              :disabled="busy"
              @click="openBrowser(backup)">
              {{ t("panel.backup.browse") }}
            </button>
            <button
              v-if="backup.chainId"
              class="secondary compact row-button"
              :disabled="busy"
              @click="openChain(backup)">
              {{ t("panel.backup.chain.view") }}
            </button>
            <button class="secondary compact row-button" :disabled="busy" @click="download(backup)">
              {{ t("panel.backup.download") }}
            </button>
            <button class="danger compact row-button" v-bind="writeGate(busy)" @click="removeBackup(backup)">
              {{ t("panel.backup.delete") }}
            </button>
          </div>
        </div>
        <div v-if="expandedWarnings === backup.id" class="warning-detail">
          <strong>{{ t("panel.backup.warningsListTitle") }}</strong>
          <ul>
            <li v-for="(warning, index) in backup.warnings" :key="index">
              {{ warningLine(warning) }}
              <small v-if="warning.detail">{{ warning.detail }}</small>
            </li>
          </ul>
        </div>
      </template>
      <!-- "Yükleniyor" / "kayıt yok" / "okunamadı" ÜÇÜ AYRI: birini diğerinin yerine göstermek,
           var olan yedekleri yok gibi sunmak olurdu. -->
      <p v-if="loading && backups.length === 0" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
      <SectionLoadError
        v-else-if="listLoad.message.value"
        :message="listLoad.message.value"
        :busy="loading"
        @retry="refresh" />
      <p v-else-if="backups.length === 0" class="empty-state">{{ t("panel.backup.empty") }}</p>
    </div>

    <TablePager v-if="activeView === 'list'" :state="pager" :disabled="busy" />

    <!-- ================= ARTIMSAL ZİNCİR ================= -->
    <section v-if="activeView === 'list' && chainRecord" class="form-surface chain-panel">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.chain.title") }}: {{ chainRecord.name }}</h3>
          <p class="meta">{{ t("panel.backup.chain.hint") }}</p>
        </div>
        <button class="secondary compact" @click="closeChain">{{ t("panel.common.cancel") }}</button>
      </div>

      <p v-if="chainLoading" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
      <SectionLoadError
        v-else-if="chainLoad.message.value"
        :message="chainLoad.message.value"
        :busy="busy"
        @retry="reloadChain" />
      <template v-else-if="chain">
        <!-- KOPUK ZİNCİR: eksik halkanın ADI yazılır. "Bir şeyler eksik" demek, kullanıcının
             hangi yedeği geri getireceğini bilmemesi demektir. -->
        <p v-if="chain.broken" class="notice error" role="alert">
          <strong>{{ t("panel.backup.chain.broken") }}</strong><br />
          {{ t("panel.backup.chain.missing") }}: {{ chain.missingLinks.join(", ") || "—" }}
        </p>
        <p v-else class="field-hint">{{ t("panel.backup.chain.intact", { count: chain.links.length }) }}</p>

        <!-- ŞİFRELİ + ARTIMSAL: parola kaybı TEK HALKAYI değil ZİNCİRİN TAMAMINI götürür. -->
        <p v-if="chainRecord.encrypted" class="notice encryption-loss" role="status">
          {{ t("panel.backup.encryptionChainWarning") }}
        </p>

        <ol v-if="chain.links.length" class="chain-links">
          <li
            v-for="link in chain.links"
            :key="link.backupId"
            :class="['chain-item', { missing: !link.available, current: link.backupId === chainRecord.id }]">
            <span :class="['status-pill', link.backupMode === 'incremental' ? 'chain-link' : 'chain-head']">
              {{ link.backupMode === "incremental"
                ? t("panel.backup.chain.linkBadge", { index: link.chainIndex })
                : t("panel.backup.mode.full") }}
            </span>
            <span class="chain-name">
              {{ link.name }}
              <small v-if="link.backupId === chainRecord.id">{{ t("panel.backup.chain.currentLink") }}</small>
            </span>
            <span class="cell-muted">{{ new Date(link.createdAtUtc).toLocaleString() }}</span>
            <span class="cell-muted">{{ formatBytes(link.size) }}</span>
            <span :class="['status-pill', link.available ? 'ok' : 'danger']">
              {{ link.available ? t("panel.backup.chain.fileOk") : t("panel.backup.chain.fileMissing") }}
            </span>
          </li>
        </ol>
        <p v-else class="empty-state">{{ t("panel.backup.chain.none") }}</p>

        <div class="row-actions">
          <button
            class="compact"
            v-bind="writeGate(busy || chain.broken, chain.broken ? t('panel.backup.chain.restoreBlocked') : undefined)"
            @click="restore(chainRecord, true)">
            {{ t("panel.backup.restore") }}
          </button>
          <button class="danger compact" v-bind="writeGate(busy)" @click="removeChain(chainRecord)">
            {{ t("panel.backup.chain.delete") }} ({{ chainDeleteTargets(chainRecord).length }})
          </button>
        </div>
        <p class="field-hint">{{ t("panel.backup.chain.deleteWarning") }}</p>
      </template>
    </section>

    <!-- Otomatik zamanlanmış yedek (Plesk gibi): motor Otomasyon servisidir; bu kart en çok aranan
         ayarları (sıklık + saklama) yedek ekranının içine taşır. Gelişmiş seçenekler Araçlar > Otomasyon'da. -->
    <section v-if="activeView === 'list' && isAdmin && autoForm" class="form-surface auto-backup-card">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.autoTitle") }}</h3>
          <p class="meta">{{ t("panel.backup.autoHint") }}</p>
        </div>
        <span :class="['status-pill', autoForm.autoBackupEnabled ? 'ok' : '']">
          {{ autoForm.autoBackupEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
        </span>
      </div>
      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.autoEnabled") }}</span>
          <select v-model="autoForm.autoBackupEnabled">
            <option :value="true">{{ t("panel.status.enabled") }}</option>
            <option :value="false">{{ t("panel.status.disabled") }}</option>
          </select>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.autoFrequency") }}</span>
          <select v-model="autoFrequency">
            <option value="daily">{{ t("panel.backup.freq.daily") }}</option>
            <option value="weekly">{{ t("panel.backup.freq.weekly") }}</option>
            <option value="monthly">{{ t("panel.backup.freq.monthly") }}</option>
            <option v-if="autoFrequency === 'custom'" value="custom">
              {{ t("panel.backup.freq.custom", { hours: autoForm.backupIntervalHours }) }}
            </option>
          </select>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.autoRetention") }}</span>
          <input v-model.number="autoForm.backupRetentionDays" type="number" min="0" />
        </label>
      </div>
      <p class="field-hint">
        {{ t("panel.backup.autoRuns", {
          last: autoStatus?.lastBackupRunUtc ? new Date(autoStatus.lastBackupRunUtc).toLocaleString() : "-",
          next: autoStatus?.nextBackupRunUtc ? new Date(autoStatus.nextBackupRunUtc).toLocaleString() : "-"
        }) }}
        {{ t("panel.backup.autoAdvancedHint") }}
      </p>
      <div class="row-actions">
        <button class="compact" :disabled="busy" @click="saveAutoBackup">
          {{ t("panel.backup.autoSave") }}
        </button>
      </div>
    </section>

    <!--
      YEDEK ŞİFRELEME (yalnız yönetici; uç de `Administrator`).

      Parola alanları GERÇEK form alanı: `window.prompt` bu panelde yasak (parolayı düz metin
      gösterir ve tarayıcı diyalogları kapattığında sessizce null döner).
    -->
    <section v-if="activeView === 'list' && isAdmin && encryption" class="form-surface encryption-card">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.encryptionTitle") }}</h3>
          <p class="meta">{{ t("panel.backup.encryptionHint") }}</p>
        </div>
        <span :class="['status-pill', encryptionTone]">{{ t(encryptionStateKey) }}</span>
      </div>

      <!-- Kayıt var ama ÇÖZÜLEMİYOR: zamanlanmış yedekler şifrelenemez. Sessiz kalmak,
           yedeklerin sessizce şifresiz alınması demekti. -->
      <p v-if="encryption.enabled && !encryption.passwordUsable" class="notice error" role="alert">
        {{ t("panel.backup.encryptionBroken") }}
      </p>

      <!-- ZORUNLU UYARI: parolayı BELİRLERKEN görünür, geri yükleme anında değil. -->
      <p class="notice encryption-loss" role="status">{{ t("panel.backup.encryptionLossWarning") }}</p>
      <p class="notice encryption-loss" role="status">{{ t("panel.backup.encryptionChainWarning") }}</p>

      <div class="form-grid two">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.encryptionPassword") }}</span>
          <input v-model="encryptionForm.password" type="password" autocomplete="new-password" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.encryptionPasswordConfirm") }}</span>
          <input v-model="encryptionForm.confirm" type="password" autocomplete="new-password" />
        </label>
      </div>

      <label class="check-field">
        <input v-model="encryptionForm.acknowledged" type="checkbox" />
        <span>{{ t("panel.backup.encryptionAck") }}</span>
      </label>

      <p v-if="encryptionMismatch" class="notice error" role="alert">{{ t("panel.backup.encryptionMismatch") }}</p>

      <p class="field-hint">
        {{ t("panel.backup.encryptionAlgorithm") }}: {{ encryption.algorithm }}
        <template v-if="encryption.enabled">
          · {{ t("panel.backup.encryptionUpdatedAt") }}: {{ new Date(encryption.updatedAtUtc).toLocaleString() }}
        </template>
      </p>

      <div class="row-actions">
        <button class="compact" :disabled="busy || !canSaveEncryption" @click="saveEncryption">
          {{ t("panel.backup.encryptionSave") }}
        </button>
        <button v-if="encryption.enabled" class="danger compact" :disabled="busy" @click="disableEncryption">
          {{ t("panel.backup.encryptionDisable") }}
        </button>
      </div>
    </section>

    <!-- ================= YEDEK İÇERİĞİ + SEÇEREK GERİ YÜKLEME ================= -->
    <section v-if="activeView === 'list' && browseRecord" class="form-surface backup-browser">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.browseTitle") }}: {{ browseRecord.name }}</h3>
          <p class="meta">{{ t("panel.backup.browseHint") }}</p>
        </div>
        <button class="secondary compact" @click="closeBrowser">{{ t("panel.common.cancel") }}</button>
      </div>

      <!--
        ABONELİK arşivinde sunucu "orijinal konum"u çözemez (uç yalnız SITE yedeğinde çözer) ve
        `backup.target_path_required` döner. Kapsamlı kullanıcıda hedef yol GÜVENLİK GEREĞİ zorla
        boşaltıldığı için bu yol o kullanıcıya tamamen kapalıdır — düğmeye bastırıp hata
        göstermektense sebebi ÖNCEDEN söylüyoruz.
      -->
      <p v-if="browseRecord.type === 'subscription' && !isAdmin" class="notice error" role="alert">
        {{ t("panel.backup.browseSubscriptionScoped") }}
      </p>
      <p v-else-if="browseRecord.type === 'subscription'" class="notice warn" role="status">
        {{ t("panel.backup.browseSubscriptionTarget") }}
      </p>

      <div class="breadcrumb">
        <button class="secondary compact" :disabled="busy" @click="navigateTo('')">/</button>
        <button
          v-for="crumb in breadcrumbs"
          :key="crumb.prefix"
          class="secondary compact"
          :disabled="busy"
          @click="navigateTo(crumb.prefix)">
          {{ crumb.name }}
        </button>
      </div>

      <div class="data-table">
        <div class="data-row table-head entry-row">
          <span></span>
          <span>{{ t("panel.backup.name") }}</span>
          <span>{{ t("panel.backup.size") }}</span>
        </div>
        <!-- Kökte girdiler ÖNEKLERİNE göre gruplanır (Dosyalar / Veritabanları / Posta / FTP):
             abonelik arşivinin kökü aksi hâlde anlamsız dört klasör adı olurdu. -->
        <template v-for="group in groupedEntries" :key="group.key || 'flat'">
          <div v-if="group.key" class="entry-group-head">
            <strong>{{ t(`panel.backup.group.${group.key}`) }}</strong>
            <span class="cell-muted">{{ group.entries.length }}</span>
          </div>
          <div v-for="entry in group.entries" :key="entry.path" class="data-row entry-row">
            <input v-model="selectedItems" type="checkbox" :value="entry.path" :aria-label="entry.name" />
            <button
              v-if="entry.isDirectory"
              class="secondary compact entry-link"
              :disabled="busy"
              @click="navigateTo(entry.path)">
              📁 {{ entry.name }}
            </button>
            <strong v-else class="entry-file">{{ entry.name }}</strong>
            <span class="cell-muted">{{ entry.isDirectory ? "—" : formatBytes(entry.size) }}</span>
          </div>
        </template>
        <p v-if="browseLoading" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
        <SectionLoadError
          v-else-if="browseLoad.message.value"
          :message="browseLoad.message.value"
          :busy="busy"
          @retry="navigateTo(browsePrefix)" />
        <p v-else-if="browseEntries.length === 0" class="empty-state">{{ t("panel.backup.browseEmpty") }}</p>
      </div>
      <p v-if="browseTruncated" class="field-hint">{{ t("panel.backup.browseTruncated") }}</p>

      <div class="form-grid two">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.itemsTarget") }}</span>
          <input v-model.trim="itemsTargetPath" :placeholder="t('panel.backup.itemsTargetOriginal')" />
        </label>
      </div>
      <div class="row-actions">
        <button
          class="compact"
          v-bind="writeGate(busy || selectedItems.length === 0)"
          @click="restoreSelectedItems">
          {{ t("panel.backup.restoreSelected") }} ({{ selectedItems.length }})
        </button>
        <button v-if="selectedItems.length" class="secondary compact" :disabled="busy" @click="selectedItems = []">
          {{ t("panel.bulk.clearSelection") }}
        </button>
      </div>
    </section>

    <!-- ================= YENİ YEDEK ================= -->
    <section v-if="showCreateForm" class="form-surface backup-create">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.new") }}</h3>
          <p class="meta">{{ backupKindHint }}</p>
        </div>
        <div class="segmented-tabs">
          <button :class="{ active: backupKind === 'site' }" @click="backupKind = 'site'">
            {{ t("panel.backup.siteTitle") }}
          </button>
          <button :class="{ active: backupKind === 'subscription' }" @click="backupKind = 'subscription'">
            {{ t("panel.backup.subscription.title") }}
          </button>
          <button :class="{ active: backupKind === 'database' }" @click="backupKind = 'database'">
            {{ t("panel.backup.dbTitle") }}
          </button>
          <button :class="{ active: backupKind === 'mail' }" @click="backupKind = 'mail'">
            {{ t("panel.backup.mailTitle") }}
          </button>
          <button v-if="isAdmin" :class="{ active: backupKind === 'panel' }" @click="backupKind = 'panel'">
            {{ t("panel.backup.panelTitle") }}
          </button>
        </div>
      </div>

      <div class="form-grid">
        <label v-if="backupKind === 'site'" class="stacked-field">
          <span class="field-label">{{ t("panel.php.targetSite") }}</span>
          <select v-model="siteName" required>
            <option value="">{{ t("panel.php.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
          </select>
        </label>
        <label v-else-if="backupKind === 'subscription'" class="stacked-field">
          <span class="field-label">{{ t("panel.backup.subscription.customer") }}</span>
          <select v-model="subscriptionUserId">
            <option value="">{{ t("panel.backup.subscription.selectCustomer") }}</option>
            <option v-for="choice in subscriptionChoices" :key="choice.id" :value="choice.id">
              {{ choice.label }}
            </option>
          </select>
        </label>
        <template v-else-if="backupKind === 'database'">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.database.engineLabel") }}</span>
            <select v-model="databaseEngine">
              <option value="mariadb">MariaDB</option>
              <option value="mysql">MySQL</option>
              <option value="postgresql">PostgreSQL</option>
            </select>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.database.databaseName") }}</span>
            <input v-model="databaseName" :placeholder="t('panel.backup.allDatabases')" />
          </label>
        </template>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.targetType") }}</span>
          <select v-model="targetForm.targetType">
            <option value="local">{{ t("panel.backup.target.local") }}</option>
            <option value="ftp">{{ t("panel.backup.target.ftp") }}</option>
            <option value="s3">{{ t("panel.backup.target.s3") }}</option>
            <option value="webdav">{{ t("panel.backup.target.webdav") }}</option>
          </select>
        </label>
      </div>

      <!-- Abonelik kapsamı: hepsi kapatılırsa arşivde manifestten başka bir şey kalmaz. -->
      <template v-if="backupKind === 'subscription'">
        <p class="field-hint">{{ t("panel.backup.subscription.hint") }}</p>
        <p v-if="subscriptionChoices.length === 0" class="notice warn" role="status">
          {{ t("panel.backup.subscription.noCustomers") }}
        </p>
        <div class="scope-grid">
          <label class="check-field">
            <input v-model="subscriptionScope.includeFiles" type="checkbox" />
            <span>{{ t("panel.backup.scopeFiles") }}</span>
          </label>
          <label class="check-field">
            <input v-model="subscriptionScope.includeDatabases" type="checkbox" />
            <span>{{ t("panel.backup.scopeDatabases") }}</span>
          </label>
          <label class="check-field">
            <input v-model="subscriptionScope.includeMail" type="checkbox" />
            <span>{{ t("panel.backup.scopeMail") }}</span>
          </label>
          <label class="check-field">
            <input v-model="subscriptionScope.includeFtp" type="checkbox" />
            <span>{{ t("panel.backup.scopeFtp") }}</span>
          </label>
        </div>
        <!-- ZORUNLU NOT: posta bölümü ENVANTERDİR, iletiler değil. -->
        <p class="notice" role="status">{{ t("panel.backup.subscription.mailNote") }}</p>
      </template>

      <!-- Artımsal yedek: yalnız DOSYA taşıyan yedeklerde anlamlıdır (site + abonelik). -->
      <div v-if="backupKind === 'site' || backupKind === 'subscription'" class="form-grid">
        <label class="check-field">
          <input v-model="incrementalForm.incremental" type="checkbox" />
          <span>{{ t("panel.backup.incremental") }}</span>
        </label>
        <label v-if="incrementalForm.incremental" class="stacked-field">
          <span class="field-label">{{ t("panel.backup.schedule.fullEvery") }}</span>
          <input v-model.number="incrementalForm.fullBackupEveryDays" type="number" min="1" max="365" />
        </label>
      </div>
      <p v-if="(backupKind === 'site' || backupKind === 'subscription') && incrementalForm.incremental" class="field-hint">
        {{ t("panel.backup.incrementalHint") }}
      </p>

      <div v-if="targetForm.targetType !== 'local'" class="form-grid">
        <label class="stacked-field remote-url">
          <span class="field-label">{{ t("panel.backup.remoteUrl") }}</span>
          <input v-model="targetForm.remoteUrl" :placeholder="remoteUrlPlaceholder" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ targetForm.targetType === 's3' ? t("panel.backup.remoteAccessKey") : t("panel.backup.remoteUser") }}</span>
          <input v-model="targetForm.remoteUserName" autocomplete="off" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ targetForm.targetType === 's3' ? t("panel.backup.remoteSecretKey") : t("panel.backup.remotePassword") }}</span>
          <input v-model="targetForm.remotePassword" type="password" autocomplete="new-password" />
        </label>
        <label v-if="targetForm.targetType === 's3'" class="stacked-field">
          <span class="field-label">{{ t("panel.backup.remoteRegion") }}</span>
          <input v-model="targetForm.remoteRegion" placeholder="eu-central-1" />
        </label>
      </div>

      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.perBackupPassword") }}</span>
          <input v-model="targetForm.encryptionPassword" type="password" autocomplete="new-password" />
          <small class="field-hint">{{ t("panel.backup.perBackupPasswordHint") }}</small>
        </label>
      </div>

      <!-- Şifreli ZİNCİRDE parola kaybı TEK halkayı değil zincirin TAMAMINI götürür; uyarı,
           parolanın yazıldığı yerde durur. -->
      <p
        v-if="incrementalForm.incremental && (backupKind === 'site' || backupKind === 'subscription')"
        class="notice encryption-loss"
        role="status">
        {{ t("panel.backup.encryptionChainWarning") }}
      </p>

      <!-- Uzak hedefe ŞİFRESİZ gitmek üzere: uyarı gönderimden ÖNCE, kullanıcı parolayı hâlâ
           yazabilecekken görünür (sonradan gelen `backup.remote_unencrypted` rozeti geç kalır). -->
      <p v-if="remoteUnencryptedRisk" class="notice encryption-loss" role="status">
        {{ t("panel.backup.remoteUnencryptedWarning") }}
      </p>

      <p class="field-hint">{{ t("panel.backup.targetHint") }}</p>
      <div class="row-actions">
        <button class="compact" v-bind="writeGate(busy)" @click="createSelectedBackup">
          {{ createButtonLabel }}
        </button>
        <button class="secondary compact" @click="activeView = 'list'">{{ t("panel.common.cancel") }}</button>
      </div>
    </section>

    <!-- ================= ABONELİK YEDEK PROGRAMLARI ================= -->
    <section v-if="activeView === 'schedules'" class="form-surface schedules-panel">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.schedule.title") }}</h3>
          <p class="meta">{{ t("panel.backup.schedule.hint") }}</p>
        </div>
        <button class="secondary compact" :disabled="schedulesLoading" @click="loadSchedules">
          {{ t("panel.backup.refresh") }}
        </button>
      </div>

      <div class="data-table">
        <div class="data-row table-head schedule-row">
          <span>{{ t("panel.backup.schedule.customer") }}</span>
          <span>{{ t("panel.backup.statusColumn") }}</span>
          <span>{{ t("panel.backup.schedule.interval") }}</span>
          <span>{{ t("panel.backup.schedule.retentionColumn") }}</span>
          <span>{{ t("panel.backup.schedule.mode") }}</span>
          <span>{{ t("panel.backup.schedule.lastRun") }}</span>
          <span>{{ t("panel.software.action") }}</span>
        </div>
        <div v-for="schedule in schedulePager.items.value" :key="schedule.userId" class="data-row schedule-row">
          <div class="cell-main">
            <strong>{{ schedule.userName || schedule.userId }}</strong>
            <small>{{ schedule.planName || t("panel.backup.schedule.noPlan") }}</small>
          </div>
          <div class="cell-main">
            <span :class="['status-pill', schedule.enabled ? 'ok' : '']">
              {{ schedule.enabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
            </span>
            <small v-if="schedule.throttledByQuota" class="quota-warn">
              {{ t("panel.backup.schedule.throttled", { hours: schedule.intervalHours }) }}
            </small>
          </div>
          <div class="cell-main">
            <strong>{{ t("panel.backup.schedule.every", { hours: schedule.intervalHours }) }}</strong>
            <small>{{ t("panel.backup.schedule.nextRun") }}: {{ formatDate(schedule.nextRunUtc) }}</small>
          </div>
          <div class="cell-main">
            <strong>{{ retentionLabel(schedule) }}</strong>
            <small>{{ targetLabel(schedule.targetType) }}</small>
          </div>
          <div class="cell-main">
            <strong>{{ schedule.incremental ? t("panel.backup.mode.incremental") : t("panel.backup.mode.full") }}</strong>
            <small v-if="schedule.incremental">
              {{ t("panel.backup.schedule.fullEvery") }}: {{ schedule.fullBackupEveryDays }}
            </small>
          </div>
          <div class="cell-main">
            <span :class="['status-pill', lastStatusTone(schedule.lastStatus)]">{{ lastStatusLabel(schedule) }}</span>
            <small>{{ formatDate(schedule.lastRunUtc) }}</small>
            <!-- Kısmi başarısızlık SEBEBİYLE birlikte durur; "çalıştı" demek yetmez. -->
            <small v-if="schedule.lastDetails" class="last-details">{{ schedule.lastDetails }}</small>
          </div>
          <div class="row-actions">
            <button class="secondary compact row-button" :disabled="busy" @click="openScheduleForm(schedule.userId)">
              {{ t("panel.common.edit") }}
            </button>
            <button class="secondary compact row-button" v-bind="writeGate(busy)" @click="runScheduleNow(schedule)">
              {{ t("panel.backup.schedule.runNow") }}
            </button>
            <button class="danger compact row-button" v-bind="writeGate(busy)" @click="removeSchedule(schedule)">
              {{ t("panel.backup.schedule.remove") }}
            </button>
          </div>
        </div>
        <p v-if="schedulesLoading" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
        <SectionLoadError
          v-else-if="schedulesLoad.message.value"
          :message="schedulesLoad.message.value"
          :busy="schedulesLoading"
          @retry="loadSchedules" />
        <p v-else-if="schedules.length === 0" class="empty-state">{{ t("panel.backup.schedule.empty") }}</p>
      </div>
      <TablePager :state="schedulePager" :disabled="busy" />

      <div class="form-grid two schedule-add">
        <label v-if="canPickSubscription" class="stacked-field">
          <span class="field-label">{{ t("panel.backup.subscription.customer") }}</span>
          <select v-model="newScheduleUserId">
            <option value="">{{ t("panel.backup.subscription.selectCustomer") }}</option>
            <option v-for="choice in subscriptionChoices" :key="choice.id" :value="choice.id">
              {{ choice.label }}
            </option>
          </select>
        </label>
        <div class="row-actions">
          <button
            class="compact"
            v-bind="writeGate(busy || !newScheduleUserId, newScheduleUserId ? undefined : t('panel.backup.subscription.required'))"
            @click="openScheduleForm(newScheduleUserId)">
            {{ t("panel.backup.schedule.add") }}
          </button>
        </div>
      </div>
      <p v-if="!canPickSubscription && !ownSubscriptionId" class="notice warn" role="status">
        {{ t("panel.backup.schedule.noOwnSubscription") }}
      </p>

      <form v-if="scheduleForm" class="form-surface schedule-form" @submit.prevent="saveSchedule">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.backup.schedule.editTitle", { name: scheduleForm.userName || scheduleForm.userId }) }}</h3>
            <p class="meta">{{ scheduleForm.planName || t("panel.backup.schedule.noPlan") }}</p>
          </div>
          <button type="button" class="secondary compact" @click="scheduleForm = null">
            {{ t("panel.common.cancel") }}
          </button>
        </div>

        <!-- Kota kullanıcının GİRDİĞİ sıklığı kabul etmemiş olabilir: sunucu KOTAYA ÇEKİLMİŞ
             değeri döner ve form onunla tazelenir. -->
        <p v-if="scheduleThrottled" class="notice warn" role="alert">
          {{ t("panel.backup.schedule.throttled", { hours: scheduleForm.intervalHours }) }}
        </p>

        <div class="form-grid">
          <label class="check-field">
            <input v-model="scheduleForm.enabled" type="checkbox" />
            <span>{{ t("panel.backup.schedule.enabled") }}</span>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.backup.schedule.interval") }}</span>
            <input v-model.number="scheduleForm.intervalHours" type="number" min="1" max="720" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.backup.schedule.retentionDays") }}</span>
            <input v-model.number="scheduleForm.retentionDays" type="number" min="0" max="3650" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.backup.schedule.keepCount") }}</span>
            <input v-model.number="scheduleForm.keepCount" type="number" min="0" max="1000" />
          </label>
        </div>

        <div class="form-grid">
          <label class="check-field">
            <input v-model="scheduleForm.incremental" type="checkbox" />
            <span>{{ t("panel.backup.schedule.incremental") }}</span>
          </label>
          <label v-if="scheduleForm.incremental" class="stacked-field">
            <span class="field-label">{{ t("panel.backup.schedule.fullEvery") }}</span>
            <input v-model.number="scheduleForm.fullBackupEveryDays" type="number" min="1" max="365" />
          </label>
        </div>
        <p v-if="scheduleForm.incremental" class="notice encryption-loss" role="status">
          {{ t("panel.backup.encryptionChainWarning") }}
        </p>

        <span class="field-label">{{ t("panel.backup.schedule.scope") }}</span>
        <div class="scope-grid">
          <label class="check-field">
            <input v-model="scheduleForm.includeFiles" type="checkbox" />
            <span>{{ t("panel.backup.scopeFiles") }}</span>
          </label>
          <label class="check-field">
            <input v-model="scheduleForm.includeDatabases" type="checkbox" />
            <span>{{ t("panel.backup.scopeDatabases") }}</span>
          </label>
          <label class="check-field">
            <input v-model="scheduleForm.includeMail" type="checkbox" />
            <span>{{ t("panel.backup.scopeMail") }}</span>
          </label>
          <label class="check-field">
            <input v-model="scheduleForm.includeFtp" type="checkbox" />
            <span>{{ t("panel.backup.scopeFtp") }}</span>
          </label>
        </div>
        <p class="field-hint">{{ t("panel.backup.subscription.mailNote") }}</p>

        <div class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.backup.targetType") }}</span>
            <select v-model="scheduleForm.targetType">
              <option value="local">{{ t("panel.backup.target.local") }}</option>
              <option value="ftp">{{ t("panel.backup.target.ftp") }}</option>
              <option value="s3">{{ t("panel.backup.target.s3") }}</option>
              <option value="webdav">{{ t("panel.backup.target.webdav") }}</option>
            </select>
          </label>
          <template v-if="scheduleForm.targetType !== 'local'">
            <label class="stacked-field remote-url">
              <span class="field-label">{{ t("panel.backup.remoteUrl") }}</span>
              <input v-model="scheduleForm.remoteUrl" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ scheduleForm.targetType === 's3' ? t("panel.backup.remoteAccessKey") : t("panel.backup.remoteUser") }}</span>
              <input v-model="scheduleForm.remoteUserName" autocomplete="off" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ scheduleForm.targetType === 's3' ? t("panel.backup.remoteSecretKey") : t("panel.backup.remotePassword") }}</span>
              <input v-model="scheduleForm.remotePassword" type="password" autocomplete="new-password" />
              <small class="field-hint">
                {{ scheduleForm.remotePasswordSet
                  ? t("panel.backup.schedule.passwordKept")
                  : t("panel.backup.schedule.passwordEmpty") }}
              </small>
            </label>
            <label v-if="scheduleForm.targetType === 's3'" class="stacked-field">
              <span class="field-label">{{ t("panel.backup.remoteRegion") }}</span>
              <input v-model="scheduleForm.remoteRegion" placeholder="eu-central-1" />
            </label>
          </template>
        </div>

        <!-- Plan kotası KALICI olarak görünür: kullanıcı sıklığı yazarken sınırı bilmelidir. -->
        <p v-if="scheduleForm.maxBackupsPerDay > 0" class="field-hint">
          {{ t("panel.backup.schedule.quotaHint", { count: scheduleForm.maxBackupsPerDay }) }}
        </p>

        <div class="row-actions">
          <button type="submit" class="compact" v-bind="writeGate(busy)">{{ t("panel.common.save") }}</button>
        </div>
      </form>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import {
  backupDatabase,
  backupMail,
  backupSite,
  backupPanel,
  backupSubscription,
  deleteBackup,
  deleteBackupChain,
  deleteBackupSchedule,
  downloadBackup,
  getBackupChain,
  getBackupEncryption,
  getBackupEntries,
  getBackups,
  getBackupSchedule,
  getBackupSchedules,
  restoreBackup,
  restoreBackupItems,
  runBackupScheduleNow,
  setBackupEncryption,
  updateBackupSchedule,
  type BackupChain,
  type BackupEncryptionSettings,
  type BackupEntry,
  type BackupRecord,
  type BackupRestoreWarning,
  type BackupTargetPayload,
  type SubscriptionBackupSchedule
} from "../api/backup";
import { getAutomation, updateAutomation, type AutomationSettings, type AutomationStatus } from "../api/automation";
import { getMySubscription } from "../api/subscriptions";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import TablePager from "../components/TablePager.vue";
import { useBulkAction } from "../composables/useBulkSelection";
import { useNotice, useSectionError } from "../composables/useNotice";
import { usePagination } from "../composables/usePagination";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const { confirm } = useConfirm();
const { prompt } = usePrompt();
const { writeGate } = useSubscriptionGate();
const { runBulk } = useBulkAction();
const { notice, noticeError, showNotice, showError } = useNotice();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const isReseller = computed(() => authStore.session?.user?.role === "Reseller");
const backups = ref<BackupRecord[]>([]);
const pager = usePagination(backups);
const activeView = ref<"list" | "create" | "schedules">("list");
const backupKind = ref<"site" | "subscription" | "database" | "mail" | "panel">("site");
const expandedWarnings = ref("");

/** Yedek listesi ÇEKİLEMEDİ. Boş listeyle karıştırılmaz: biri "yok", diğeri "bilmiyoruz". */
const listLoad = useSectionError();

const backupKindHint = computed(() => {
  if (backupKind.value === "site") return t("panel.backup.siteHint");
  if (backupKind.value === "subscription") return t("panel.backup.subscription.hint");
  if (backupKind.value === "database") return t("panel.backup.dbHint");
  if (backupKind.value === "panel") return t("panel.backup.panelHint");
  return t("panel.backup.mailHint");
});

const createButtonLabel = computed(() => {
  if (backupKind.value === "site") return t("panel.backup.siteAction");
  if (backupKind.value === "subscription") return t("panel.backup.subscription.action");
  if (backupKind.value === "mail") return t("panel.backup.mailAction");
  if (backupKind.value === "panel") return t("panel.backup.panelAction");
  return t("panel.backup.dbAction");
});

// Liste görünümünde form panel açık değilken görünür (eski davranış); zincir/gözat panelleri
// açıkken gizlenir ki ekranda iki ayrı iş aynı anda durmasın.
const showCreateForm = computed(() =>
  activeView.value === "create" ||
  (activeView.value === "list" && !browseRecord.value && !chainRecord.value));

const siteName = ref(sitesStore.selectedSiteName);
const databaseEngine = ref("mariadb");
const databaseName = ref("");
const loading = ref(false);
const busy = ref(false);
const targetForm = reactive<BackupTargetPayload>({
  targetType: "local",
  remoteUrl: "",
  remoteUserName: "",
  remotePassword: "",
  remoteRegion: "",
  encryptionPassword: ""
});

// Artımsal seçenekler site ve abonelik yedeğinde ORTAK (sunucuda da ortak taban:
// `BackupArchiveRequest`), bu yüzden tek yerde tutulur.
const incrementalForm = reactive({ incremental: false, fullBackupEveryDays: 7 });
const subscriptionScope = reactive({
  includeFiles: true,
  includeDatabases: true,
  includeMail: true,
  includeFtp: true
});

// --- Abonelik seçimi ---
const subscriptionUserId = ref("");
/** Kapsamlı kullanıcının KENDİ aboneliği (alt-kullanıcıda ana aboneliğin kimliği). */
const ownSubscriptionId = ref("");
const ownSubscriptionName = ref("");
const canPickSubscription = computed(() => isAdmin.value || isReseller.value);

/**
 * Yedeği alınabilecek abonelikler.
 *
 * Yönetici/bayi müşteri listesinden seçer (bayi zaten yalnız kendi müşterilerini görür); bayinin
 * ve müşterinin KENDİ aboneliği ayrıca başa eklenir. Alt-kullanıcılar liste dışıdır: abonelik
 * kaynakları ana hesapta toplanır, alt-kullanıcının ayrı bir aboneliği yoktur.
 */
const subscriptionChoices = computed(() => {
  const items: { id: string; label: string }[] = [];
  if (ownSubscriptionId.value) {
    items.push({
      id: ownSubscriptionId.value,
      label: t("panel.backup.subscription.own", { name: ownSubscriptionName.value || ownSubscriptionId.value })
    });
  }

  if (canPickSubscription.value) {
    for (const user of authStore.users) {
      if (user.role !== "Customer" || user.parentUserId) continue;
      if (normalizeId(user.id) === normalizeId(ownSubscriptionId.value)) continue;
      items.push({ id: user.id, label: user.userName });
    }
  }

  return items;
});

/** Guid'in tiresiz karşılaştırma biçimi (sunucu abonelik kimliğini "N" biçiminde döner). */
function normalizeId(value: string) {
  return value.replace(/-/g, "").toLowerCase();
}

/** Abonelik yedeğinin kaynağı ham kullanıcı kimliğidir; listede ADI gösterilir. */
function backupSourceLabel(backup: BackupRecord) {
  if (backup.type !== "subscription") {
    return backup.source;
  }

  const key = normalizeId(backup.subscriptionUserId || backup.source);
  if (key && normalizeId(ownSubscriptionId.value) === key) {
    return ownSubscriptionName.value || backup.source;
  }

  const user = authStore.users.find((item) => normalizeId(item.id) === key);
  return user?.userName || backup.source;
}

function backupKindLabel(type: string) {
  const key = `panel.backup.kind.${type}`;
  const translated = t(key);
  return translated === key ? type : translated;
}

// Panelin varsayılan yedek şifreleme ayarı (uç yalnız yöneticiye açık).
const encryption = ref<BackupEncryptionSettings | null>(null);
const encryptionForm = reactive({ password: "", confirm: "", acknowledged: false });

const encryptionMismatch = computed(() =>
  encryptionForm.confirm.length > 0 && encryptionForm.password !== encryptionForm.confirm);

// Kaydetme şartları: iki alan dolu ve EŞİT + "anladım" işaretli. Parola kaybı geri dönüşsüz
// olduğu için onay kutusu olmadan kaydetmeyiz.
const canSaveEncryption = computed(() =>
  encryptionForm.password.length > 0 &&
  encryptionForm.password === encryptionForm.confirm &&
  encryptionForm.acknowledged);

const encryptionStateKey = computed(() => {
  if (!encryption.value?.enabled) return "panel.status.disabled";
  return encryption.value.passwordUsable ? "panel.status.enabled" : "panel.backup.encryptionUnusable";
});

const encryptionTone = computed(() => {
  if (!encryption.value?.enabled) return "";
  return encryption.value.passwordUsable ? "ok" : "danger";
});

/*
 * Uzak hedefe ŞİFRESİZ gitme riski.
 *
 * Yönetici için kesin ölçülür (kayıtlı parola durumu elimizde). Kiracıda `GET /backup/encryption`
 * kapalı olduğundan kayıtlı parolanın varlığı BİLİNMEZ; bu yüzden uyarı metni "panelde kayıtlı
 * parola yoksa" koşuluyla yazılmıştır — iki durumda da doğrudur.
 */
const remoteUnencryptedRisk = computed(() =>
  targetForm.targetType !== "local" &&
  targetForm.encryptionPassword.length === 0 &&
  !(encryption.value?.enabled && encryption.value.passwordUsable));

// --- Granüler geri yükleme (zip içeriğine gözatma) ---
const browseRecord = ref<BackupRecord | null>(null);
const browsePrefix = ref("");
const browseEntries = ref<BackupEntry[]>([]);
const browseTruncated = ref(false);
const browseLoading = ref(false);
const browseLoad = useSectionError();
const selectedItems = ref<string[]>([]);
const itemsTargetPath = ref("");

/**
 * Arşiv kökündeki girdilerin ÖNEK gruplaması.
 *
 * Abonelik arşivinde yollar `sites/<site>/files/…`, `databases/…`, `mail/<alan>.json`,
 * `ftp/accounts.json`; site arşivinde `files/…`, `databases/…`. Kök listesi bu gruplarla
 * anlamlanır. Alt dizinde gruplama YAPILMAZ: orada zaten tek bir bağlam vardır.
 */
const ENTRY_GROUPS: { key: string; prefixes: string[] }[] = [
  { key: "files", prefixes: ["files/", "sites/"] },
  { key: "databases", prefixes: ["databases/"] },
  { key: "mail", prefixes: ["mail/"] },
  { key: "ftp", prefixes: ["ftp/"] }
];

const groupedEntries = computed(() => {
  if (browsePrefix.value) {
    return [{ key: "", entries: browseEntries.value }];
  }

  const claimed = new Set<string>();
  const groups: { key: string; entries: BackupEntry[] }[] = [];
  for (const group of ENTRY_GROUPS) {
    const entries = browseEntries.value.filter((entry) =>
      group.prefixes.some((prefix) => entry.path.toLowerCase().startsWith(prefix)));
    for (const entry of entries) {
      claimed.add(entry.path);
    }
    if (entries.length) {
      groups.push({ key: group.key, entries });
    }
  }

  const other = browseEntries.value.filter((entry) => !claimed.has(entry.path));
  if (other.length) {
    groups.push({ key: "other", entries: other });
  }

  return groups;
});

const breadcrumbs = computed(() => {
  const parts = browsePrefix.value.split("/").filter(Boolean);
  return parts.map((name, index) => ({
    name,
    prefix: parts.slice(0, index + 1).join("/") + "/"
  }));
});

const remoteUrlPlaceholder = computed(() => {
  if (targetForm.targetType === "ftp") {
    return "ftp://backup.example.com/adapanel/";
  }

  if (targetForm.targetType === "webdav") {
    return "https://dav.example.com/backups/";
  }

  return "https://s3.eu-central-1.amazonaws.com/bucket-adi/yedekler/";
});

watch(() => sitesStore.selectedSiteName, (value) => {
  if (value) {
    siteName.value = value;
  }
});

// --- Artımsal zincir ---
const chainRecord = ref<BackupRecord | null>(null);
const chain = ref<BackupChain | null>(null);
const chainLoading = ref(false);
const chainLoad = useSectionError();

// --- Abonelik yedek programları ---
const schedules = ref<SubscriptionBackupSchedule[]>([]);
const schedulePager = usePagination(schedules);
const schedulesLoading = ref(false);
const schedulesLoaded = ref(false);
const schedulesLoad = useSectionError();
const newScheduleUserId = ref("");

type ScheduleFormState = {
  userId: string;
  userName: string;
  planName: string;
  maxBackupsPerDay: number;
  remotePasswordSet: boolean;
  enabled: boolean;
  intervalHours: number;
  retentionDays: number;
  keepCount: number;
  incremental: boolean;
  fullBackupEveryDays: number;
  includeFiles: boolean;
  includeDatabases: boolean;
  includeMail: boolean;
  includeFtp: boolean;
  targetType: string;
  remoteUrl: string;
  remoteUserName: string;
  remoteRegion: string;
  remotePassword: string;
};

const scheduleForm = ref<ScheduleFormState | null>(null);
const scheduleThrottled = ref(false);

// Otomatik yedek programı (yalnız admin; motor = Otomasyon servisi, bu kart en sık kullanılan ayarların kısayolu).
const autoStatus = ref<AutomationStatus | null>(null);
const autoForm = ref<AutomationSettings | null>(null);
// Sıklık ön ayarı ↔ saat eşlemesi: günlük=24, haftalık=168, aylık=720; başka değer "custom" olarak korunur.
const autoFrequency = computed({
  get(): "daily" | "weekly" | "monthly" | "custom" {
    const hours = autoForm.value?.backupIntervalHours ?? 24;
    if (hours === 24) return "daily";
    if (hours === 168) return "weekly";
    if (hours === 720) return "monthly";
    return "custom";
  },
  set(value: "daily" | "weekly" | "monthly" | "custom") {
    if (!autoForm.value || value === "custom") return;
    autoForm.value.backupIntervalHours = value === "daily" ? 24 : value === "weekly" ? 168 : 720;
  }
});

onMounted(async () => {
  if (sitesStore.sites.length === 0) {
    await sitesStore.fetchSites().catch(() => undefined);
  }
  await refresh();
  await loadSubscriptionChoices();
  if (isAdmin.value) {
    await loadAutoBackup();
    await loadEncryption();
  }
});

/**
 * Abonelik seçicisinin kaynakları.
 *
 * Yönetici/bayi müşteri listesini `/users`ten alır. Kapsamlı kullanıcı kendi abonelik KİMLİĞİNİ
 * `/subscriptions/me`den alır: oturumdaki kullanıcı kimliğini kullanmak alt-kullanıcıda yanlış
 * olurdu (alt-kullanıcının kendi aboneliği yoktur, ana hesabınkini paylaşır).
 */
async function loadSubscriptionChoices() {
  if (canPickSubscription.value && authStore.users.length === 0) {
    await authStore.fetchUsers().catch((error: unknown) => showError(error));
  }

  if (!isAdmin.value) {
    try {
      const usage = await getMySubscription();
      ownSubscriptionId.value = usage.userId;
      ownSubscriptionName.value = usage.userName;
      if (!subscriptionUserId.value) {
        subscriptionUserId.value = usage.userId;
      }
      if (!newScheduleUserId.value) {
        newScheduleUserId.value = usage.userId;
      }
    } catch {
      // Abonelik kaydı yoksa seçici boş kalır ve ekranda sebebi yazan uyarı görünür;
      // uydurma bir kimlikle istek göndermek 403 üretirdi.
      ownSubscriptionId.value = "";
    }
  }
}

async function loadEncryption() {
  try {
    encryption.value = await getBackupEncryption();
  } catch (error) {
    // Kasa bu kurulumda yoksa (`backup.encryption_unavailable`) kart gizlenir; yedek listesi
    // etkilenmez. Sebep yine de şeritte söylenir — sessizce kaybolmaz.
    encryption.value = null;
    showError(error);
  }
}

async function saveEncryption() {
  if (!canSaveEncryption.value) {
    return;
  }

  busy.value = true;
  try {
    encryption.value = await setBackupEncryption(encryptionForm.password);
    resetEncryptionForm();
    showNotice(t("backup.encryption_saved"), false);
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}

async function disableEncryption() {
  // Yıkıcı: bundan sonraki yedekler şifresiz alınır. MEVCUT şifreli yedekler yine parola ister —
  // onay metni bunu açıkça söylüyor.
  await confirm({
    title: t("panel.backup.encryptionDisableTitle"),
    message: t("panel.backup.encryptionDisableMessage"),
    confirmLabel: t("panel.backup.encryptionDisable"),
    action: async () => {
      busy.value = true;
      try {
        // Boş parola = kaydı sil + şifrelemeyi kapat (sunucu sözleşmesi).
        encryption.value = await setBackupEncryption("");
        resetEncryptionForm();
        showNotice(t("backup.encryption_saved"), false);
      } catch (error) {
        showError(error);
      } finally {
        busy.value = false;
      }
    }
  });
}

function resetEncryptionForm() {
  // Parola bellekte/ekranda ASILI KALMAZ.
  encryptionForm.password = "";
  encryptionForm.confirm = "";
  encryptionForm.acknowledged = false;
}

async function loadAutoBackup() {
  try {
    autoStatus.value = await getAutomation();
    autoForm.value = { ...autoStatus.value.settings };
  } catch {
    // Otomasyon durumu okunamazsa kart gizli kalır; yedek listesi etkilenmez.
  }
}

async function saveAutoBackup() {
  if (!autoForm.value) return;
  busy.value = true;
  try {
    // backupRemotePassword boş = sunucudaki kayıtlı parola korunur (Otomasyon ekranıyla aynı sözleşme).
    autoStatus.value = await updateAutomation({ ...autoForm.value, backupRemotePassword: "" });
    autoForm.value = { ...autoStatus.value.settings };
    showNotice(t("panel.backup.autoSaved"), false);
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}

async function refresh() {
  loading.value = true;
  try {
    backups.value = await getBackups();
    listLoad.clear();
  } catch (error) {
    // Liste ÇEKİLEMEDİ: eski içerik silinip "yedek yok" gösterilmez.
    listLoad.capture(error);
    showError(error);
  } finally {
    loading.value = false;
  }
}

function toggleWarnings(backupId: string) {
  expandedWarnings.value = expandedWarnings.value === backupId ? "" : backupId;
}

async function createSelectedBackup() {
  if (backupKind.value === "site") {
    await createSiteBackup();
    return;
  }

  if (backupKind.value === "subscription") {
    await createSubscriptionBackup();
    return;
  }

  if (backupKind.value === "mail") {
    // Yıkıcı değil (yalnız servis kısa süre durur) → nötr ton: kırmızı düğme ve "geri alınamaz" uyarısı yok.
    const approved = await confirm({
      title: t("panel.confirm.mailBackupTitle"),
      message: t("panel.backup.confirmMailBackup"),
      tone: "neutral",
      confirmLabel: t("panel.backup.mailAction"),
    });
    if (!approved) return;
    await runBackup(() => backupMail(buildTargetPayload()));
    return;
  }

  if (backupKind.value === "panel") {
    await runBackup(() => backupPanel(buildTargetPayload()));
    return;
  }

  await createDatabaseBackup();
}

async function createSiteBackup() {
  if (!siteName.value) {
    showNotice(t("panel.php.siteRequired"), true);
    return;
  }

  await runBackup(() => backupSite(
    siteName.value,
    { incremental: incrementalForm.incremental, fullBackupEveryDays: incrementalForm.fullBackupEveryDays },
    buildTargetPayload()));
}

async function createSubscriptionBackup() {
  if (!subscriptionUserId.value) {
    showNotice(t("panel.backup.subscription.required"), true);
    return;
  }

  await runBackup(() => backupSubscription(
    subscriptionUserId.value,
    { ...subscriptionScope, ...incrementalForm },
    buildTargetPayload()));
}

async function createDatabaseBackup() {
  await runBackup(() => backupDatabase(databaseEngine.value, databaseName.value, buildTargetPayload()));
}

async function runBackup(operation: () => Promise<BackupRecord>) {
  busy.value = true;
  try {
    const created = await operation();
    await refresh();
    activeView.value = "list";
    // Bu yedeğe özel parola tek seferliktir; formda asılı kalıp bir sonraki yedeğe sessizce
    // taşınmaz (kullanıcı hangi parolayla şifrelediğini karıştırmasın).
    targetForm.encryptionPassword = "";
    reportCreateResult(created);
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}

/**
 * Yedek ALINIRKEN atlanan öğeler sessizce yutulmaz.
 *
 * `created_with_errors` durumunda arşiv OLUŞTU ama içine giremeyen öğeler var; yeşil "tamam"
 * göstermek, olmayan bir veritabanının geri yükleme anında keşfedilmesi demektir.
 */
function reportCreateResult(record: BackupRecord) {
  const warnings = record.warnings ?? [];
  if (warnings.length === 0) {
    showNotice(t("panel.backup.created"), false);
    return;
  }

  expandedWarnings.value = record.id;
  showNotice([
    t("panel.backup.createdWithErrors"),
    `${t("panel.backup.warningsListTitle")}:`,
    warningText(record)
  ].join("\n"), true);
}

/**
 * Geri yükleme.
 *
 * Artımsal halkada zincir ÖNCE ölçülür: sunucu kopuk zinciri zaten `backup.chain_broken` ile
 * reddeder, ama kullanıcıyı yüksek riskli onaydan geçirip sonra reddetmek, ona ne olduğunu
 * anlatmayan bir yol olurdu. Kopuksa zincir paneli açılır ve eksik halkanın ADI görünür.
 */
async function restore(backup: BackupRecord, chainChecked = false) {
  if (!chainChecked && backup.chainId) {
    // Zinciri ÖLÇEMEDİYSEK de devam etmeyiz: kopuk bir zinciri geri yüklemeye başlamak, canlı
    // içeriğin üzerine eksik bir durum yazmak demektir. Sebep şeritte yazar, kullanıcı yineleyebilir.
    let resolved: BackupChain;
    busy.value = true;
    try {
      resolved = await getBackupChain(backup.id);
    } catch (error) {
      showError(error);
      return;
    } finally {
      busy.value = false;
    }

    // Zincir SAĞLAMSA panel durumuna dokunulmaz (kullanıcı onu açmadı); kopuksa panel bu kayda
    // bağlanır ve eksik halkanın ADI ekranda görünür.
    if (resolved.broken) {
      chainRecord.value = backup;
      chain.value = resolved;
      chainLoad.clear();
      browseRecord.value = null;
      activeView.value = "list";
      showNotice(
        `${t("panel.backup.chain.broken")} — ${t("panel.backup.chain.missing")}: ${resolved.missingLinks.join(", ")}`,
        true);
      return;
    }
  }

  // Şifreli yedekte parola ÖNCE sorulur: onay diyaloğunu geçtikten sonra "parola lazım" demek,
  // kullanıcıyı yüksek riskli onayı ikinci kez yapmaya zorlardı.
  let password = "";
  if (backup.encrypted) {
    const entered = await askBackupPassword();
    if (entered === null) {
      return;
    }

    password = entered;
  }

  // Yüksek riskli onay: geri yükleme canlı veriyi ÜZERİNE yazar → yedek adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.restoreBackupTitle", { name: backup.name }),
    message: t("panel.backup.restoreConfirm"),
    impact: backupImpact(backup),
    requireText: backup.name,
    confirmLabel: t("panel.backup.restore"),
    busyLabel: t("panel.backup.restoring"),
    action: async () => {
      busy.value = true;
      try {
        const restored = await restoreBackup(backup.id, "", password);
        await refresh();
        reportRestoreResult(restored);
      } catch (error) {
        showError(error);
      } finally {
        busy.value = false;
        password = "";
      }
    },
  });
}

/**
 * Şifreli yedeğin parolasını sorar. Boş bırakılabilir: sunucu o zaman panelde KAYITLI parolayı
 * dener. İptalde `null` döner (akış durur).
 */
async function askBackupPassword() {
  return prompt({
    title: t("panel.backup.restorePasswordTitle"),
    message: t("panel.backup.encryptionLossWarning"),
    label: t("panel.backup.encryptionPassword"),
    hint: t("panel.backup.restorePasswordHint"),
    password: true,
    allowEmpty: true,
    confirmLabel: t("panel.backup.restore")
  });
}

/**
 * Geri yükleme sonucunu OLDUĞU GİBİ söyler.
 *
 * NEDEN: sunucu yarım kalan adımları `warnings[]` ile döndürüyor ve durumu
 * `restored_with_errors` yapıyor. Koşulsuz "geri yüklendi" demek, kullanıcının elinde ne eski
 * hâl ne de dönmüş veritabanı varken ona "tamam" dedirtirdi. Üzerine yazılan içeriğin taşındığı
 * klasör (`preservedPath`) de söylenir: "yanlış yedeği geri yükledim" denince dönülecek tek yer orası.
 */
function reportRestoreResult(record: BackupRecord) {
  const lines: string[] = [];
  const failed = (record.warnings ?? []).length > 0;

  lines.push(failed ? t("panel.backup.restoredWithErrors") : t("panel.backup.restored"));

  if (failed) {
    expandedWarnings.value = record.id;
    lines.push(`${t("panel.backup.restoreWarningsTitle")}:`, warningText(record));
  }

  if (record.preservedPath) {
    lines.push(t("panel.backup.preservedPath", { path: record.preservedPath }));
  }

  showNotice(lines.join("\n"), failed);
}

/** Tek bir uyarının çevrilmiş satırı ("hedef: sebep"). */
function warningLine(warning: BackupRestoreWarning) {
  const translated = t(warning.messageKey, { target: warning.target });
  const message = translated === warning.messageKey ? warning.messageKey : translated;
  // Anahtar `{target}` içermiyorsa hedef kaybolmasın diye başa yazılır.
  return message.includes(warning.target) || !warning.target ? message : `${warning.target}: ${message}`;
}

/** Kayıt üzerindeki uyarıların çevrilmiş özeti (bildirim şeridi için). */
function warningText(backup: BackupRecord) {
  return (backup.warnings ?? []).map((warning) => warningLine(warning)).join("\n");
}

// Onay kutusundaki "etkilenecekler" listesi: ekranın zaten elinde olan yedek bilgileri.
function backupImpact(backup: BackupRecord) {
  const rows = [
    `${t("panel.backup.name")}: ${backup.name}`,
    `${t("panel.backup.type")}: ${backupKindLabel(backup.type)} (${backupSourceLabel(backup)})`,
    `${t("panel.backup.size")}: ${formatBytes(backup.size)}`,
    `${t("panel.backup.createdAt")}: ${new Date(backup.createdAtUtc).toLocaleString()}`
  ];

  if (backup.chainId) {
    rows.push(backup.backupMode === "incremental"
      ? t("panel.backup.chain.linkBadge", { index: backup.chainIndex })
      : t("panel.backup.chain.headBadge"));
  }

  return rows;
}

// =====================================================================================
// ZİNCİR
// =====================================================================================

async function openChain(backup: BackupRecord) {
  browseRecord.value = null;
  activeView.value = "list";
  await loadChain(backup);
}

function closeChain() {
  chainRecord.value = null;
  chain.value = null;
  chainLoad.clear();
}

async function reloadChain() {
  if (chainRecord.value) {
    await loadChain(chainRecord.value);
  }
}

/**
 * Zinciri çözer ve paneli BU kayda bağlar.
 *
 * Panelin kaydı ile gösterilen zincir HER ZAMAN aynı yedeğe aittir: ikisini ayrı ayrı set etmek,
 * A yedeğinin başlığı altında B yedeğinin zincirini göstermeye açık kapı bırakıyordu.
 */
async function loadChain(backup: BackupRecord) {
  chainRecord.value = backup;
  chainLoading.value = true;
  try {
    chain.value = await getBackupChain(backup.id);
    chainLoad.clear();
    return chain.value;
  } catch (error) {
    chain.value = null;
    chainLoad.capture(error);
    return null;
  } finally {
    chainLoading.value = false;
  }
}

/**
 * Bu yedek silinirse KULLANILAMAZ hâle gelecek kayıtlar (kendisi dahil).
 *
 * Sunucudaki ölçüyle aynı: aynı zincirde SONRA gelen halkalar. Kullanıcı kaç yedeğin gideceğini
 * onay diyaloğunda görür; sunucunun 409 ayrıntısı da adlarını yazar.
 */
function chainDeleteTargets(backup: BackupRecord) {
  if (!backup.chainId) {
    return [backup];
  }

  const dependents = backups.value.filter((item) =>
    item.id !== backup.id &&
    item.chainId === backup.chainId &&
    (item.chainIndex > backup.chainIndex ||
      (item.chainIndex === backup.chainIndex && item.createdAtUtc > backup.createdAtUtc)));

  return [backup, ...dependents];
}

async function removeBackup(backup: BackupRecord) {
  // 409 "bağımlı halka var" sonucunda İKİNCİ bir onay gerekir; onay diyaloğu aynı anda tek
  // örnektir, bu yüzden zincir onayı ilk diyalog KAPANDIKTAN sonra açılır.
  let dependentsRejected = false;
  let dependentsDetail = "";

  await confirm({
    title: t("panel.confirm.deleteBackupTitle", { name: backup.name }),
    message: t("panel.backup.confirmDelete", { name: backup.name }),
    impact: backupImpact(backup),
    action: async () => {
      busy.value = true;
      try {
        await deleteBackup(backup.id);
        await refresh();
        if (browseRecord.value?.id === backup.id) {
          closeBrowser();
        }
        if (chainRecord.value?.id === backup.id) {
          closeChain();
        }
        showNotice(t("panel.backup.deletedNotice"), false);
      } catch (error) {
        if (extractApiMessage(error) === "backup.chain_has_dependents") {
          // Otomatik olarak zincir silmeye GEÇMEYİZ: kullanıcı ne kaybedeceğini görmeli.
          // Ayrıntı (bağımlı halkaların adları) boş gelse bile akış devam eder; kaybedilecek
          // kayıtları ekran zaten kendi listesinden de çözebiliyor.
          dependentsRejected = true;
          dependentsDetail = extractApiDetails(error);
          return;
        }
        showError(error);
      } finally {
        busy.value = false;
      }
    },
  });

  // Sunucu 409 verdiyse kullanıcı zinciri görerek ikinci bir karar verir.
  if (dependentsRejected) {
    await removeChain(backup, dependentsDetail);
  }
}

/**
 * Yedeği VE ona bağımlı halkaları siler. YIKICI ve geri dönüşsüz olduğu için yedek adının
 * yazılması istenir; kaç yedeğin gideceği ve adları onay kutusunda listelenir.
 */
async function removeChain(backup: BackupRecord, dependentsDetail = "") {
  const targets = chainDeleteTargets(backup);
  const impact = targets.map((item) => item.name);
  if (dependentsDetail) {
    impact.push(dependentsDetail);
  }

  await confirm({
    title: t("panel.backup.chain.deleteTitle", { count: targets.length }),
    message: dependentsDetail
      ? `${t("backup.chain_has_dependents")}\n\n${t("panel.backup.chain.deleteMessage", { count: targets.length })}`
      : t("panel.backup.chain.deleteMessage", { count: targets.length }),
    impact,
    requireText: backup.name,
    confirmLabel: t("panel.backup.chain.delete"),
    action: async () => {
      busy.value = true;
      try {
        const result = await deleteBackupChain(backup.id);
        await refresh();
        if (chainRecord.value && result.deletedIds.includes(chainRecord.value.id)) {
          closeChain();
        }
        if (browseRecord.value && result.deletedIds.includes(browseRecord.value.id)) {
          closeBrowser();
        }
        showNotice(t("panel.backup.chain.deletedNotice", {
          count: result.deletedIds.length,
          names: result.deletedNames.join(", ")
        }), false);
      } catch (error) {
        showError(error);
      } finally {
        busy.value = false;
      }
    },
  });
}

async function download(backup: BackupRecord) {
  busy.value = true;
  try {
    await downloadBackup(backup.id, backup.name);
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}

function buildTargetPayload() {
  return { ...targetForm };
}

// =====================================================================================
// GÖZAT + SEÇEREK GERİ YÜKLE
// =====================================================================================

async function openBrowser(backup: BackupRecord) {
  browseRecord.value = backup;
  chainRecord.value = null;
  activeView.value = "list";
  selectedItems.value = [];
  itemsTargetPath.value = "";
  await navigateTo("");
}

function closeBrowser() {
  browseRecord.value = null;
  browseEntries.value = [];
  selectedItems.value = [];
  browseLoad.clear();
}

async function navigateTo(prefix: string) {
  if (!browseRecord.value) return;
  browseLoading.value = true;
  busy.value = true;
  try {
    const result = await getBackupEntries(browseRecord.value.id, prefix);
    browsePrefix.value = result.prefix;
    browseEntries.value = result.entries;
    browseTruncated.value = result.truncated;
    browseLoad.clear();
  } catch (error) {
    /*
     * Gözatma ucu parola ALMAZ (bilerek: parolayı sorgu dizesine koymak onu IIS erişim
     * günlüklerine ve tarayıcı geçmişine yazmak olurdu). Şifreli yedekte gözatma yalnız panelde
     * KAYITLI parola varsa çalışır — genel "parolayı girin" metni burada yanlış yol gösterirdi.
     */
    browseEntries.value = [];
    if (extractApiMessage(error) === "backup.password_required") {
      browseLoad.message.value = t("panel.backup.browseEncryptedHint");
    } else {
      browseLoad.capture(error);
    }
  } finally {
    browseLoading.value = false;
    busy.value = false;
  }
}

/**
 * SEÇİLEN öğeleri geri yükler ve sonucu DÜRÜST raporlar.
 *
 * Tek çağrı hızlıdır ama yarıda hata alırsa sunucu "hiçbir şey olmadı" demez: o ana kadar
 * çıkarılan dosyalar diskte kalır. Bu yüzden hata durumunda öğeler TEK TEK denenir ve özet
 * "N öğeden K tanesi geri yüklendi, M tanesi YÜKLENMEDİ: sebep" biçiminde verilir. Sağlıklı
 * durumda ek istek yapılmaz.
 */
async function restoreSelectedItems() {
  const record = browseRecord.value;
  if (!record || selectedItems.value.length === 0) return;

  // Şifreli yedekte parola önce sorulur (gözatma kayıtlı parolayla çalışmış olabilir, ama
  // geri yükleme ucu isteğe ait parolayı da kabul eder).
  let password = "";
  if (record.encrypted) {
    const entered = await askBackupPassword();
    if (entered === null) {
      return;
    }

    password = entered;
  }

  const items = [...selectedItems.value];
  const impact = [
    `${t("panel.backup.name")}: ${record.name}`,
    `${t("panel.backup.restoreSelected")}: ${items.length}`,
    `${t("panel.backup.itemsTarget")}: ${itemsTargetPath.value || t("panel.backup.itemsTargetOriginal")}`
  ];

  // Yüksek riskli onay: seçilen öğeler hedeftekilerin ÜZERİNE yazılır → yedek adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.restoreBackupTitle", { name: record.name }),
    message: t("panel.backup.confirmRestoreItems"),
    impact,
    requireText: record.name,
    confirmLabel: t("panel.backup.restore"),
    busyLabel: t("panel.backup.restoring"),
    action: async () => {
      busy.value = true;
      try {
        const result = await restoreBackupItems(record.id, items, itemsTargetPath.value, password);
        selectedItems.value = [];
        showNotice(t("panel.backup.itemsRestoredAll", {
          items: items.length,
          count: result.restoredCount,
          target: result.targetRoot
        }), false);
      } catch (error) {
        if (items.length === 1) {
          showError(error);
          return;
        }

        await reportPartialItemRestore(record, items, password);
      } finally {
        busy.value = false;
        password = "";
      }
    },
  });
}

/** Toplu çağrı yarıda kaldı: hangi öğenin geçtiğini TEK TEK ölçüp sebebiyle raporlar. */
async function reportPartialItemRestore(record: BackupRecord, items: string[], password: string) {
  const outcome = await runBulk(
    items,
    (item) => item,
    (item) => restoreBackupItems(record.id, [item], itemsTargetPath.value, password));

  selectedItems.value = outcome.failures.map((failure) => failure.key);

  if (outcome.failures.length === 0) {
    showNotice(t("panel.backup.itemsRestoredAll", {
      items: outcome.total,
      count: outcome.done,
      target: itemsTargetPath.value || t("panel.backup.itemsTargetOriginal")
    }), false);
    return;
  }

  const head = t("panel.backup.itemsRestoredPartial", {
    total: outcome.total,
    done: outcome.done,
    failed: outcome.failures.length
  });
  const lines = outcome.failures.map((failure) =>
    t("panel.bulk.failedItem", { name: failure.key, reason: failure.reason }));
  showNotice([head, ...lines].join("\n"), true);
}

// =====================================================================================
// ABONELİK YEDEK PROGRAMLARI
// =====================================================================================

async function openSchedules() {
  activeView.value = "schedules";
  if (!schedulesLoaded.value) {
    await loadSchedules();
  }
}

async function loadSchedules() {
  schedulesLoading.value = true;
  try {
    schedules.value = await getBackupSchedules();
    schedulesLoad.clear();
  } catch (error) {
    schedules.value = [];
    schedulesLoad.capture(error);
  } finally {
    schedulesLoading.value = false;
    schedulesLoaded.value = true;
  }
}

/** Kayıtlı programı (yoksa VARSAYILANLARI) forma alır. */
async function openScheduleForm(userId: string) {
  if (!userId) {
    showNotice(t("panel.backup.subscription.required"), true);
    return;
  }

  busy.value = true;
  try {
    const dto = await getBackupSchedule(userId);
    applyScheduleToForm(dto);
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}

function applyScheduleToForm(dto: SubscriptionBackupSchedule) {
  scheduleThrottled.value = dto.throttledByQuota;
  scheduleForm.value = {
    userId: dto.userId,
    userName: dto.userName,
    planName: dto.planName,
    maxBackupsPerDay: dto.maxBackupsPerDay,
    remotePasswordSet: dto.remotePasswordSet,
    enabled: dto.enabled,
    // KOTAYA ÇEKİLMİŞ değer: formu istenen değerle doldurmak, kabul edilmemiş bir sıklığı
    // kayıtlı göstermek olurdu.
    intervalHours: dto.intervalHours,
    retentionDays: dto.retentionDays,
    keepCount: dto.keepCount,
    incremental: dto.incremental,
    fullBackupEveryDays: dto.fullBackupEveryDays,
    includeFiles: dto.includeFiles,
    includeDatabases: dto.includeDatabases,
    includeMail: dto.includeMail,
    includeFtp: dto.includeFtp,
    targetType: dto.targetType,
    remoteUrl: dto.remoteUrl,
    remoteUserName: dto.remoteUserName,
    remoteRegion: dto.remoteRegion,
    // Parola sunucudan ASLA dönmez; boş gönderim "değiştirme" demektir.
    remotePassword: ""
  };
}

async function saveSchedule() {
  const form = scheduleForm.value;
  if (!form) return;

  busy.value = true;
  try {
    const dto = await updateBackupSchedule(form.userId, {
      enabled: form.enabled,
      intervalHours: form.intervalHours,
      retentionDays: form.retentionDays,
      keepCount: form.keepCount,
      incremental: form.incremental,
      fullBackupEveryDays: form.fullBackupEveryDays,
      includeFiles: form.includeFiles,
      includeDatabases: form.includeDatabases,
      includeMail: form.includeMail,
      includeFtp: form.includeFtp,
      targetType: form.targetType,
      remoteUrl: form.remoteUrl,
      remoteUserName: form.remoteUserName,
      remoteRegion: form.remoteRegion,
      remotePassword: form.remotePassword
    });

    applyScheduleToForm(dto);
    await loadSchedules();

    if (dto.throttledByQuota) {
      // Kaydedildi AMA istenen sıklıkla değil: sebebi (plan kotası) söylenmeden "kaydedildi"
      // demek, kullanıcının kurmadığı bir programı kurdu sanmasıdır.
      showNotice(t("panel.backup.schedule.throttled", { hours: dto.intervalHours }), true);
      return;
    }

    showNotice(t("backup.schedule_saved"), false);
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}

async function removeSchedule(schedule: SubscriptionBackupSchedule) {
  await confirm({
    title: t("panel.backup.schedule.removeTitle"),
    message: t("panel.backup.schedule.removeMessage"),
    impact: [schedule.userName || schedule.userId],
    confirmLabel: t("panel.backup.schedule.remove"),
    action: async () => {
      busy.value = true;
      try {
        await deleteBackupSchedule(schedule.userId);
        if (scheduleForm.value?.userId === schedule.userId) {
          scheduleForm.value = null;
        }
        await loadSchedules();
        showNotice(t("backup.schedule_deleted"), false);
      } catch (error) {
        showError(error);
      } finally {
        busy.value = false;
      }
    },
  });
}

async function runScheduleNow(schedule: SubscriptionBackupSchedule) {
  const approved = await confirm({
    title: t("panel.backup.schedule.runTitle"),
    message: t("panel.backup.schedule.runMessage"),
    tone: "neutral",
    confirmLabel: t("panel.backup.schedule.runNow"),
    impact: [schedule.userName || schedule.userId]
  });
  if (!approved) return;

  busy.value = true;
  try {
    const dto = await runBackupScheduleNow(schedule.userId);
    await loadSchedules();
    await refresh();
    if (scheduleForm.value?.userId === dto.userId) {
      applyScheduleToForm(dto);
    }

    // "Başlatıldı" demek yetmez: tur bittiğinde sonucu kaydın kendisi taşır.
    if (dto.lastStatus === "failed" || dto.lastStatus === "completed_with_errors") {
      showNotice([lastStatusLabel(dto), dto.lastDetails].filter(Boolean).join("\n"), true);
      return;
    }

    showNotice([t("backup.schedule_run"), dto.lastDetails].filter(Boolean).join("\n"), false);
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}

function retentionLabel(schedule: SubscriptionBackupSchedule) {
  const parts: string[] = [];
  parts.push(schedule.retentionDays > 0
    ? t("panel.backup.schedule.retentionValue", { days: schedule.retentionDays })
    : t("panel.backup.schedule.unlimited"));
  if (schedule.keepCount > 0) {
    parts.push(t("panel.backup.schedule.keepValue", { count: schedule.keepCount }));
  }
  return parts.join(" · ");
}

function targetLabel(targetType: string) {
  const key = `panel.backup.target.${targetType}`;
  const translated = t(key);
  return translated === key ? targetType : translated;
}

function lastStatusLabel(schedule: SubscriptionBackupSchedule) {
  if (!schedule.lastStatus) {
    return t("panel.backup.schedule.never");
  }

  const key = `panel.backup.schedule.lastStatus.${schedule.lastStatus}`;
  const translated = t(key);
  return translated === key ? schedule.lastStatus : translated;
}

function lastStatusTone(status: string) {
  if (status === "failed") return "danger";
  if (status === "completed_with_errors") return "warn";
  if (status === "completed") return "ok";
  return "";
}

function formatDate(value: string | null) {
  return value ? new Date(value).toLocaleString() : "—";
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 ** 2) return `${(value / 1024).toFixed(1)} KiB`;
  if (value < 1024 ** 3) return `${(value / 1024 ** 2).toFixed(1)} MiB`;
  return `${(value / 1024 ** 3).toFixed(1)} GiB`;
}

function formatStatus(status: string) {
  const key = `panel.backup.status.${status}`;
  const translated = t(key);
  return translated === key ? status : translated;
}

/** Eksik öğe taşıyan durumlar YEŞİL gösterilmez; "tamam" rozeti gerçeği örterdi. */
function statusTone(status: string) {
  if (status === "created_with_errors" || status === "restored_with_errors" || status === "created_remote_failed") {
    return "warn";
  }
  return "ok";
}
</script>

<style scoped>
.backup-panel,
.backup-create {
  align-content: start;
}

.section-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: flex-start;
  flex-wrap: wrap;
}

/* Eylem sütunu SABİT genişlikte: düğmeler her satırda aynı düzende otursun, satırdan satıra
   yeri kaymasın (oransal genişlikte uzun yedek adları düğme sütununu daraltıp diziliş bozuyordu). */
.backup-row {
  grid-template-columns: minmax(200px, 1.3fr) minmax(0, 1.2fr) minmax(0, 0.9fr) minmax(0, 1fr) 320px;
  align-items: start;
}

.backup-row .row-actions {
  align-content: start;
  gap: 6px;
}

.schedule-row {
  grid-template-columns:
    minmax(140px, 1.1fr) minmax(0, 0.8fr) minmax(0, 0.9fr)
    minmax(0, 0.9fr) minmax(0, 0.7fr) minmax(0, 1.2fr) 250px;
  align-items: start;
}

.schedule-row .row-actions {
  align-content: start;
  gap: 6px;
}

.last-details {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.quota-warn {
  color: var(--warn, #b45309);
}

.entry-row {
  grid-template-columns: auto minmax(240px, 1fr) 120px;
  min-width: 480px;
}

.entry-row input[type="checkbox"] {
  width: auto;
}

.entry-group-head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 4px 2px;
  border-top: 1px solid var(--border, rgba(127, 127, 127, 0.25));
}

.entry-link {
  justify-self: start;
  text-align: left;
}

.entry-file {
  overflow-wrap: anywhere;
}

.breadcrumb {
  display: flex;
  gap: 4px;
  flex-wrap: wrap;
}

.breadcrumb button {
  width: auto;
}

.backup-browser,
.chain-panel,
.schedules-panel {
  align-content: start;
  display: grid;
  gap: 12px;
}

.row-button,
.row-actions button {
  width: auto;
}

.remote-url {
  grid-column: span 1;
}

.encryption-card {
  align-content: start;
  display: grid;
  gap: 12px;
}

/* Parola kaybı geri dönüşsüz: uyarı satırı gövde metninden ayrışsın. */
.encryption-loss {
  font-weight: 600;
}

/* Durum + kilit + uyarı rozetleri aynı satırda, dar ekranda alt satıra sarar. */
.pill-row {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

/* Rozet aynı zamanda düğme: dokunmatik ekranda `title` ipucu hiç görünmüyordu. */
.warning-toggle {
  width: auto;
  cursor: pointer;
  border: none;
}

.warning-detail {
  padding: 8px 12px 12px;
  display: grid;
  gap: 4px;
}

.warning-detail ul {
  margin: 0;
  padding-left: 18px;
  display: grid;
  gap: 4px;
}

.warning-detail small {
  display: block;
  opacity: 0.75;
  overflow-wrap: anywhere;
}

/* Zincir dikey bir zaman çizgisi: halkaların SIRASI geri yükleme sırasıdır. */
.chain-links {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 6px;
}

.chain-item {
  display: grid;
  grid-template-columns: 130px minmax(160px, 1fr) 170px 90px 110px;
  gap: 8px;
  align-items: center;
  padding: 6px 8px;
  border-left: 3px solid var(--border, rgba(127, 127, 127, 0.35));
}

.chain-item.current {
  border-left-color: var(--accent, #2563eb);
  font-weight: 600;
}

/* Dosyası olmayan halka: üstü çizili + kırmızı. Zincirin nerede koptuğu tek bakışta görünmeli. */
.chain-item.missing .chain-name {
  text-decoration: line-through;
  color: var(--danger, #b91c1c);
}

.chain-name {
  overflow-wrap: anywhere;
  display: grid;
}

.scope-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.schedule-form {
  display: grid;
  gap: 12px;
}

.schedule-add {
  align-items: end;
}

@media (max-width: 720px) {
  .section-head {
    flex-direction: column;
  }

  .chain-item {
    grid-template-columns: 1fr;
  }
}
</style>
