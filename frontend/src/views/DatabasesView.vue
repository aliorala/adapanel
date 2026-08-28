<template>
  <section class="panel module-shell databases-panel">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.database.title") }}</h2>
        <p class="meta">{{ t("panel.database.subtitle") }}</p>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <div class="segmented-tabs primary-tabs" role="tablist">
      <button role="tab" :aria-selected="activeModule === 'databases'" :class="{ active: activeModule === 'databases' }" @click="activeModule = 'databases'">
        {{ t("panel.database.module.databases") }}
      </button>
      <button role="tab" :aria-selected="activeModule === 'webdb'" :class="{ active: activeModule === 'webdb' }" @click="activeModule = 'webdb'">
        {{ t("panel.database.module.webdb") }}
      </button>
      <button role="tab" :aria-selected="activeModule === 'cache'" :class="{ active: activeModule === 'cache' }" @click="activeModule = 'cache'">
        {{ t("panel.database.module.cache") }}
      </button>
      <button role="tab" :aria-selected="activeModule === 'transfer'" :class="{ active: activeModule === 'transfer' }" @click="activeModule = 'transfer'">
        {{ t("panel.database.module.transfer") }}
      </button>
      <button role="tab" :aria-selected="activeModule === 'settings'" :class="{ active: activeModule === 'settings' }" @click="activeModule = 'settings'">
        {{ t("panel.database.module.settings") }}
      </button>
      <!-- "Şu an kim kullanıyor" veritabanı ekranında da olmalı: bir sorgu yavaşladığında
           kullanıcı zaten buradadır; cevabı görmek için İzleme sayfasına gitmek zorunda
           kalmasın. Bileşen ve ucu aynı — yeni bir kopya yok. -->
      <button role="tab" :aria-selected="activeModule === 'consumers'" v-if="isAdmin" :class="{ active: activeModule === 'consumers' }" @click="activeModule = 'consumers'">
        {{ t("panel.database.module.consumers") }}
      </button>
    </div>

    <section v-if="activeModule === 'consumers'" class="panel">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.consumers.title") }}</h3>
          <p class="meta">{{ t("panel.consumers.subtitle", { name: consumerSourceLabel }) }}</p>
        </div>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.consumers.source") }}</span>
          <select v-model="consumerSource" class="compact-select">
            <option value="mariadb">MariaDB</option>
            <option value="mysql">MySQL</option>
            <option value="postgresql">PostgreSQL</option>
          </select>
        </label>
      </div>

      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="consumerTab === 'live'" :class="{ active: consumerTab === 'live' }" @click="consumerTab = 'live'">
          {{ t("panel.consumers.tabLive") }}
        </button>
        <button role="tab" :aria-selected="consumerTab === 'history'" :class="{ active: consumerTab === 'history' }" @click="consumerTab = 'history'">
          {{ t("panel.consumers.tabHistory") }}
        </button>
      </div>

      <ResourceConsumers v-if="consumerTab === 'live'" :source="consumerSource" />
      <QueryHistory v-else />
    </section>

    <WebDbView v-else-if="activeModule === 'webdb'" />
    <CacheView v-else-if="activeModule === 'cache'" />
    <DbTransferView v-else-if="activeModule === 'transfer'" />
    <DbToolSettingsView v-else-if="activeModule === 'settings'" />

    <template v-else>
      <div class="module-toolbar engine-toolbar">
        <div class="segmented-tabs engine-tabs" role="tablist">
          <button
            v-for="engine in engines"
            :key="engine.key"
            type="button"
            role="tab"
            :title="engineDescription(engine.key)"
            :aria-selected="selectedEngine === engine.key"
            :class="{ active: selectedEngine === engine.key }"
            @click="selectEngine(engine.key)">
            <span>{{ engine.label }}</span>
            <i :class="['engine-state', engineBadgeClass(engine.key)]"></i>
          </button>
        </div>

        <div class="toolbar-group">
          <span :class="['status-pill', engineBadgeClass(selectedEngine)]">
            {{ engineBadgeLabel(selectedEngine) }}
          </span>
          <button
            class="secondary compact"
            :disabled="!selectedEngineInstalled || databasesStore.loadingEngine === selectedEngine"
            @click="refreshWorkspace">
            {{ t("panel.database.refresh") }}
          </button>
          <!-- Sertleştirme adımlarının hepsi MySQL/MariaDB'ye özgü (anonim kullanıcılar, uzak
               root@'%', parolasız root, boş 'test' veritabanı). Diğer motorlarda düğme GÖSTERİLMEZ:
               önceden görünüyor ve basılınca hiçbir şey yapmadan "sertleştirildi" diyordu. -->
          <button
            v-if="hardeningSupported"
            class="secondary compact"
            :disabled="!selectedEngineInstalled || hardening"
            :title="t('panel.database.hardenHint')"
            @click="hardenSecurity">
            {{ hardening ? t("panel.database.hardening") : t("panel.database.harden") }}
          </button>
        </div>
      </div>

      <section v-if="selectedEngine === 'mssql' && !workspace" class="compact-state">
        <strong>{{ t("panel.database.mssqlTitle") }}</strong>
        <span>{{ t("panel.database.mssqlPlaceholder") }}</span>
      </section>

      <section v-else-if="!selectedEngineInstalled" class="compact-state install-state">
        <div>
          <strong>{{ t("panel.database.installTitle") }}</strong>
          <span>{{ t("panel.database.installHint") }}</span>
        </div>
        <div class="install-meta">
          <span>{{ selectedEngineMeta?.label }}</span>
          <code>{{ selectedEngineMeta?.componentId || "-" }}</code>
          <button
            v-if="selectedEngineMeta?.componentId"
            class="compact"
            :disabled="softwareStore.installingComponentId === selectedEngineMeta.componentId"
            @click="installSelectedEngine">
            {{ softwareStore.installingComponentId === selectedEngineMeta?.componentId ? t("panel.software.installing") : t("panel.database.installAction") }}
          </button>
        </div>
      </section>

      <section v-else-if="needsCredentials" class="form-surface manage-existing">
        <div class="manage-head">
          <strong>{{ t("panel.database.manageExistingTitle") }}</strong>
          <span class="status-pill warn">{{ t("panel.database.detected") }}</span>
        </div>
        <p class="meta">{{ t("panel.database.manageExistingHint") }}</p>
        <div class="form-grid">
          <label>
            <span class="field-label">{{ t("panel.database.adminUser") }}</span>
            <input v-model="credForm.adminUserName" :placeholder="defaultAdminUser" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.database.adminPassword") }}</span>
            <PasswordField v-model="credForm.password" @keyup.enter="submitRegisterCredentials" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.database.portLabel") }}</span>
            <input v-model.number="credForm.port" type="number" min="1" max="65535" />
          </label>
        </div>
        <div class="row-actions">
          <button
            class="compact"
            :disabled="databasesStore.registeringEngine === selectedEngine || !credForm.password"
            @click="submitRegisterCredentials">
            {{ databasesStore.registeringEngine === selectedEngine ? t("panel.database.connecting") : t("panel.database.connectAndSave") }}
          </button>
        </div>
      </section>

      <template v-else-if="workspace">
        <div class="summary-strip database-summary">
          <div class="summary-item">
            <span>{{ t("panel.database.endpoint") }}</span>
            <strong>{{ workspace.host }}:{{ workspace.port }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.database.databaseCount") }}</span>
            <strong>{{ workspace.databases.length }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.database.userCount") }}</span>
            <strong>{{ workspace.users.length }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.database.service") }}</span>
            <strong>{{ workspace.serviceName || "-" }}</strong>
          </div>
        </div>

        <div class="module-toolbar workspace-toolbar">
          <div class="segmented-tabs" role="tablist">
            <button role="tab" :aria-selected="activeSection === 'databases'" :class="{ active: activeSection === 'databases' }" @click="openSection('databases')">
              {{ t("panel.database.section.databases") }}
            </button>
            <button role="tab" :aria-selected="activeSection === 'users'" :class="{ active: activeSection === 'users' }" @click="openSection('users')">
              {{ t("panel.database.section.users") }}
            </button>
            <button role="tab" :aria-selected="activeSection === 'remote'" :class="{ active: activeSection === 'remote' }" @click="openSection('remote')">
              {{ t("panel.database.section.remote") }}
            </button>
            <button role="tab" :aria-selected="activeSection === 'connection'" :class="{ active: activeSection === 'connection' }" @click="openSection('connection')">
              {{ t("panel.database.section.connection") }}
            </button>
          </div>

          <div class="toolbar-group">
            <input
              v-if="activeSection === 'databases' || activeSection === 'users'"
              v-model="databaseSearch"
              class="database-search"
              :aria-label="t('panel.database.search')"
              :placeholder="t('panel.database.searchPlaceholder')" />
            <button
              v-if="activeSection === 'databases'"
              class="compact"
              @click="showDatabaseForm = !showDatabaseForm">
              {{ showDatabaseForm ? t("panel.database.cancelAction") : t("panel.database.createDbTitle") }}
            </button>
            <button
              v-if="activeSection === 'databases'"
              class="secondary compact"
              @click="showImportForm = !showImportForm">
              {{ showImportForm ? t("panel.database.cancelAction") : t("panel.database.importTitle") }}
            </button>
            <button
              v-if="activeSection === 'users'"
              class="compact"
              @click="showUserForm = !showUserForm">
              {{ showUserForm ? t("panel.database.cancelAction") : t("panel.database.createUserTitle") }}
            </button>
          </div>
        </div>

        <template v-if="activeSection === 'databases'">
          <section v-if="showDatabaseForm" class="form-surface">
            <p class="meta create-hint">{{ t("panel.database.createWithUserHint") }}</p>
            <div class="form-grid">
              <label>
                <span class="field-label">{{ t("panel.database.databaseName") }}</span>
                <input v-model="databaseForm.name" @keyup.enter="submitCreateDatabase" />
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.userName") }}</span>
                <input v-model="databaseForm.userName" :placeholder="t('panel.database.optional')" />
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.userPassword") }}</span>
                <PasswordField v-model="databaseForm.password" :placeholder="t('panel.database.optional')" />
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.linkSite") }}</span>
                <select v-model="databaseForm.siteName">
                  <option value="">{{ t("panel.database.linkSiteNone") }}</option>
                  <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
                </select>
                <small class="field-hint">{{ t("panel.database.linkSiteHint") }}</small>
              </label>
            </div>
            <div class="row-actions">
              <button
                class="compact"
                :disabled="databasesStore.creatingEngine === selectedEngine"
                @click="submitCreateDatabase">
                {{ databasesStore.creatingEngine === selectedEngine ? t("panel.database.saving") : t("panel.database.createDatabase") }}
              </button>
            </div>
          </section>

          <section v-if="showImportForm" class="form-surface">
            <p class="meta create-hint">{{ t("panel.database.importHint") }}</p>
            <div class="form-grid">
              <label>
                <span class="field-label">{{ t("panel.database.databaseName") }}</span>
                <!-- FORM listeleri arama kutusundan ETKİLENMEZ.
                     Arama, alttaki TABLOYU daraltmak içindir. Buraya bağlanınca kutuda bir şey
                     yazılı olduğu anda açılır liste boşalıyor, yanındaki kullanıcı listesi ise
                     (filtresiz beslendiği için) dolu kalıyordu: kullanıcı "veritabanları
                     listelenmiyor ama kullanıcılar dolu" diyordu ve haklıydı. -->
                <select v-model="importForm.databaseName">
                  <option value="">{{ t("panel.database.selectDatabase") }}</option>
                  <option v-for="db in (workspace?.databases ?? [])" :key="db.name" :value="db.name">{{ db.name }}</option>
                </select>
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.userName") }}</span>
                <select v-model="importForm.userName">
                  <option value="">{{ t("panel.database.selectUser") }}</option>
                  <option v-for="u in (workspace?.users ?? [])" :key="`${u.userName}@${u.host}`" :value="u.userName">{{ u.userName }}</option>
                </select>
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.userPassword") }}</span>
                <PasswordField v-model="importForm.password" />
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.linkSite") }}</span>
                <select v-model="importForm.siteName">
                  <option value="">{{ t("panel.database.linkSiteNone") }}</option>
                  <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
                </select>
              </label>
            </div>
            <div class="row-actions">
              <button
                class="compact"
                :disabled="!importForm.databaseName || !importForm.userName || !importForm.password"
                @click="submitImport">
                {{ t("panel.database.import") }}
              </button>
            </div>
          </section>

          <section class="data-table">
            <div class="data-row table-head database-row">
              <span>{{ t("panel.database.databaseName") }}</span>
              <span>{{ t("panel.database.dbUsers") }}</span>
              <span>{{ t("panel.database.encoding") }}</span>
              <span>{{ t("panel.database.collation") }}</span>
              <span>{{ t("panel.files.actions") }}</span>
            </div>
            <template v-for="item in databasePager.items.value" :key="item.name">
            <div class="data-row database-row">
              <span class="cell-main">
                <strong>{{ item.name }}</strong>
                <small>{{ workspace.displayName }}</small>
              </span>
              <span :class="{ 'cell-muted': !item.users.length }">
                <!-- Kullanıcısız veritabanı bir UYARIDIR, nötr bir bilgi değil: o hâliyle hiçbir
                     uygulama bağlanamaz. Gri bir "—" ile geçiştirmek sorunu görünmez kılıyordu. -->
                <span v-if="item.users.length">{{ item.users.join(", ") }}</span>
                <span v-else class="status-pill warn" :title="t('panel.database.noUserWarnHint')">
                  {{ t("panel.database.noUserWarn") }}
                </span>
              </span>
              <span>{{ item.encoding || "-" }}</span>
              <span class="cell-muted">{{ item.collation || "-" }}</span>
              <span class="row-actions">
                <button class="secondary compact" @click="toggleDetail(item.name)">
                  {{ openDetail === item.name ? t("panel.database.detailClose") : t("panel.database.detailOpen") }}
                </button>
                <button
                  v-if="selectedEngine === 'mariadb' || selectedEngine === 'mysql'"
                  class="secondary compact"
                  :disabled="openingDb === item.name"
                  @click="openInPhpMyAdmin(item.name)">
                  {{ openingDb === item.name ? t("panel.database.opening") : t("panel.database.openInPhpMyAdmin") }}
                </button>
                <!-- Adminer'in phpMyAdmin gibi tek-tik giris mekanizmasi YOKTUR; kendi giris
                     formunu gosterir. Bu yuzden yalnizca PANEL VEKILI uzerinden acilir --
                     ham IIS portu loopback'e bagli oldugu icin disaridan 400 dondurur. -->
                <button
                  class="secondary compact"
                  :disabled="openingDb === item.name"
                  @click="openInAdminer(item.name)">
                  {{ t("panel.database.openInAdminer") }}
                </button>
                <button
                  class="danger compact"
                  :disabled="databasesStore.deletingKey === `${selectedEngine}:${item.name}`"
                  @click="removeDatabase(item.name)">
                  {{ databasesStore.deletingKey === `${selectedEngine}:${item.name}` ? t("panel.database.removing") : t("panel.database.deleteDatabase") }}
                </button>
              </span>
            </div>

            <!-- DETAY KARTI: veritabanının kendi satırının hemen altında. -->
            <section v-if="openDetail === item.name" class="db-detail">
              <div v-if="!detail" class="db-detail-loading">{{ t("panel.common.loading") }}</div>
              <template v-else>
                <div class="db-detail-grid">
                  <div>
                    <span class="db-label">{{ t("panel.database.host") }}</span>
                    <strong>{{ detail.host }}:{{ detail.port }} ({{ detail.engineName }})</strong>
                  </div>
                  <div>
                    <span class="db-label">{{ t("panel.database.size") }}</span>
                    <!-- "Ölçülemedi" ile "0 bayt" AYRI gösterilir: biri bilmediğimiz, diğeri boş
                         bir veritabanı. Aynı göstermek sorunu yokmuş gibi okuturdu. -->
                    <strong v-if="detail.sizeBytes === null">{{ t("panel.database.sizeUnknown") }}</strong>
                    <strong v-else :title="t('panel.database.sizeApprox')">
                      {{ formatBytes(detail.sizeBytes) }}
                      <small>· {{ t("panel.database.tableCount", { count: detail.tableCount }) }}</small>
                    </strong>
                  </div>
                  <div>
                    <span class="db-label">{{ t("panel.database.linkedSite") }}</span>
                    <strong>{{ detail.siteName || t("panel.database.linkedSiteNone") }}</strong>
                  </div>
                </div>

                <!-- KULLANICILAR -->
                <div class="db-block">
                  <span class="db-label">{{ t("panel.database.dbUsers") }}</span>
                  <p v-if="detail.users.length" class="db-users">
                    <span v-for="u in detail.users" :key="`${u.userName}@${u.host}`" class="status-pill">
                      {{ u.userName }}@{{ u.host }}
                    </span>
                  </p>
                  <p v-else class="db-warn">{{ t("panel.database.noUserWarnHint") }}</p>

                  <div class="db-attach">
                    <select v-model="attachUserName">
                      <!-- Etiket duruma gore: kullanicisi olan bir veritabanina BASKA bir
                           kullanici eklemek de mesrudur (uygulama kullanicisi + salt-okunur rapor
                           kullanicisi gibi), ama birincil cagri olmamali. -->
                      <option value="">
                        {{ detail.users.length ? t("panel.database.attachAnother") : t("panel.database.attachExisting") }}
                      </option>
                      <option v-for="u in (workspace?.users ?? [])" :key="`${u.userName}@${u.host}`" :value="`${u.userName}|${u.host}`">
                        {{ u.userName }}@{{ u.host }}
                      </option>
                    </select>
                    <button class="compact" :disabled="!attachUserName || attaching" @click="attachUser(item.name)">
                      {{ attaching ? t("panel.database.saving") : t("panel.database.attachDo") }}
                    </button>
                  </div>
                </div>

                <!-- BAĞLANTI BİLGİSİ -->
                <div class="db-block">
                  <span class="db-label">{{ t("panel.database.connectionInfo") }}</span>
                  <pre class="db-conn">{{ connectionSnippet(detail) }}</pre>
                  <div class="row-actions">
                    <button class="secondary compact" @click="copyConnection(detail)">
                      {{ copied ? t("panel.database.copied") : t("panel.database.copy") }}
                    </button>
                  </div>
                  <!-- Parola GÖSTERİLMEZ. Panel kullanıcı parolalarını geri döndürülebilir
                       biçimde saklamamalı; bilmiyorsa kullanıcı yenisini belirler. -->
                  <small class="field-hint">{{ t("panel.database.passwordNotShown") }}</small>
                </div>

                <!-- ABONELIGE TASI -->
                <div class="db-block">
                  <span class="db-label">{{ t("panel.database.moveTo") }}</span>
                  <div class="db-attach">
                    <select v-model="moveTarget">
                      <option value="">{{ t("panel.database.moveSelect") }}</option>
                      <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
                    </select>
                    <button class="compact" :disabled="!moveTarget || moving" @click="doMove(item.name)">
                      {{ moving ? t("panel.database.saving") : t("panel.database.moveDo") }}
                    </button>
                  </div>
                  <small class="field-hint">{{ t("panel.database.moveHint") }}</small>
                </div>

                <!-- DENETLE VE ONAR -->
                <div class="db-block">
                  <div class="row-actions">
                    <button class="secondary compact" :disabled="checking" @click="runCheck(item.name)">
                      {{ checking ? t("panel.database.checking") : t("panel.database.checkRepair") }}
                    </button>
                  </div>

                  <template v-if="checkResult && checkResult.database === item.name">
                    <p v-if="checkResult.problemCount === 0" class="db-ok">
                      {{ t("panel.database.checkClean", { count: checkResult.tables.length }) }}
                    </p>
                    <p v-else class="db-warn">
                      {{ t("panel.database.checkProblems", { count: checkResult.problemCount }) }}
                    </p>

                    <div class="db-tables">
                      <div v-for="tbl in checkResult.tables" :key="tbl.table" class="db-table-row">
                        <!-- MOTOR SÜTUNU ZORUNLU: kullanıcının bazı tablolarda neden "Onar"
                             düğmesi olmadığını anlamasının tek yolu budur. -->
                        <label>
                          <input
                            v-if="tbl.status === 'problem' && (tbl.canRepair || tbl.canRebuild)"
                            v-model="selectedTables"
                            type="checkbox"
                            :value="tbl.table" />
                          <code>{{ tbl.table }}</code>
                        </label>
                        <span class="cell-muted">{{ tbl.engine }}</span>
                        <span :class="['status-pill', tbl.status === 'problem' ? 'warn' : tbl.status === 'healthy' ? 'ok' : '']">
                          {{ tbl.status === "healthy" ? t("panel.database.statusHealthy")
                            : tbl.status === "problem" ? t("panel.database.statusProblem")
                            : t("panel.database.statusUnsupported") }}
                        </span>
                        <small v-if="tbl.status === 'problem' && !tbl.canRepair && tbl.canRebuild" :title="t('panel.database.rebuildHint')">
                          {{ t("panel.database.rebuild") }}
                        </small>
                        <small v-else-if="tbl.status === 'problem' && !tbl.canRepair && !tbl.canRebuild">
                          {{ t("panel.database.repairUnsupportedHint") }}
                        </small>
                      </div>
                    </div>

                    <div v-if="selectedTables.length" class="row-actions">
                      <button class="compact" :disabled="checking" @click="runRepair(item.name)">
                        {{ checking ? t("panel.database.repairing") : t("panel.database.repairSelected") }}
                      </button>
                    </div>
                  </template>
                </div>
              </template>
            </section>
            </template>
            <p v-if="databasesStore.loadingEngine === selectedEngine && filteredDatabases.length === 0" class="empty-state">
              {{ t("panel.common.loading") }}
            </p>
            <p v-else-if="filteredDatabases.length === 0" class="empty-state">
              {{ t("panel.database.noDatabases") }}
            </p>
            <TablePager :state="databasePager" />
          </section>
        </template>

        <template v-else-if="activeSection === 'users'">
          <section v-if="showUserForm" class="form-surface">
            <div class="form-grid">
              <label>
                <span class="field-label">{{ t("panel.database.userName") }}</span>
                <input v-model="userForm.userName" />
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.userPassword") }}</span>
                <PasswordField v-model="userForm.password" />
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.grantToDatabase") }}</span>
                <!-- TÜM veritabanları listelenir, süzülmüş liste DEĞİL. Arama kutusu tablodaki
                     satırları daraltmak içindir; bu açılır liste ona bağlanınca kullanıcı arama
                     yaptığı anda "yetki verilecek veritabanı" seçeneksiz kalıyordu. -->
                <select v-model="userForm.databaseName">
                  <option value="">{{ t("panel.database.grantNone") }}</option>
                  <option v-for="db in allDatabases" :key="db.name" :value="db.name">{{ db.name }}</option>
                </select>
              </label>
            </div>
            <div class="row-actions">
              <button
                class="compact"
                :disabled="databasesStore.creatingUserEngine === selectedEngine"
                @click="submitCreateUser">
                {{ databasesStore.creatingUserEngine === selectedEngine ? t("panel.database.saving") : t("panel.database.createUser") }}
              </button>
              <button class="secondary compact" @click="showUserForm = false">
                {{ t("panel.database.cancelAction") }}
              </button>
            </div>
          </section>

          <section class="data-table">
            <div class="data-row table-head user-row">
              <span>{{ t("panel.database.userName") }}</span>
              <span>{{ t("panel.database.access") }}</span>
              <span>{{ t("panel.database.loginState") }}</span>
              <span>{{ t("panel.files.actions") }}</span>
            </div>
            <div v-for="item in userPager.items.value" :key="`${item.userName}:${item.host}`" class="data-row user-row">
              <span class="cell-main">
                <strong>{{ item.userName }}</strong>
                <small>{{ workspace.displayName }}</small>
              </span>
              <span class="cell-muted">{{ item.host === '%' ? t('panel.database.accessAnywhere') : (item.host || t('panel.database.accessLocal')) }}</span>
              <span :class="['status-pill', item.canLogin ? 'ok' : 'warn']">
                {{ item.canLogin ? t("panel.status.enabled") : t("panel.status.disabled") }}
              </span>
              <span class="row-actions">
                <button class="secondary compact" @click="changeUserPassword(item.userName, item.host)">
                  {{ t("panel.database.changePassword") }}
                </button>
                <button
                  class="danger compact"
                  :disabled="databasesStore.deletingUserKey === `${selectedEngine}:${item.userName}:${item.host}`"
                  @click="removeUser(item.userName, item.host)">
                  {{ databasesStore.deletingUserKey === `${selectedEngine}:${item.userName}:${item.host}` ? t("panel.database.removing") : t("panel.database.deleteUser") }}
                </button>
              </span>
            </div>
            <p v-if="databasesStore.loadingEngine === selectedEngine && filteredUsers.length === 0" class="empty-state">
              {{ t("panel.common.loading") }}
            </p>
            <p v-else-if="filteredUsers.length === 0" class="empty-state">
              {{ t("panel.database.noUsers") }}
            </p>
            <TablePager :state="userPager" />
          </section>
        </template>

        <template v-else-if="activeSection === 'remote'">
          <section class="form-surface remote-intro">
            <p class="meta">{{ t("panel.database.remoteUsers.subtitle") }}</p>

            <p v-if="remoteEngine && !remoteEngine.supported" class="empty-state">
              {{ remoteEngine.warningKey ? t(remoteEngine.warningKey) : t("panel.database.remoteUsers.unsupported") }}
            </p>

            <template v-else-if="remoteEngine">
              <!-- Ne olacağı AÇIKÇA yazılır: motor mekanizması, port ve güvenlik duvarının o anki hâli. -->
              <div class="remote-facts">
                <span>
                  {{ t("panel.database.remoteUsers.endpoint") }}
                  <strong>{{ remoteEngine.serverHost || "-" }}:{{ remoteEngine.port }}</strong>
                </span>
                <span>
                  {{ t("panel.database.remoteUsers.mechanismLabel") }}
                  <strong>{{ t(`panel.database.remoteUsers.mechanism.${remoteEngine.mechanism}`) }}</strong>
                </span>
                <span :class="['status-pill', remoteEngine.firewallOpen ? 'warn' : 'ok']">
                  {{ remoteEngine.firewallOpen
                    ? t("panel.database.remoteUsers.firewallOpen", { addresses: remoteEngine.firewallAllowedAddresses.join(", ") })
                    : t("panel.database.remoteUsers.firewallClosed") }}
                </span>
              </div>

              <p v-if="remoteEngine.warningKey" class="empty-state">{{ t(remoteEngine.warningKey) }}</p>

              <!-- Motorda erişim var ama port kapalıysa kullanıcı bağlanamaz; bunu söylemek şart. -->
              <p v-if="remoteGrantedButPortClosed" class="empty-state">
                {{ t("panel.database.remoteUsers.grantedButPortClosed") }}
              </p>
            </template>
          </section>

          <section v-if="remoteEngine && remoteEngine.supported" class="data-table">
            <div class="data-row table-head remote-row">
              <span>{{ t("panel.database.userName") }}</span>
              <span>{{ t("panel.database.remoteUsers.state") }}</span>
              <span>{{ t("panel.database.remoteUsers.engineHosts") }}</span>
              <span>{{ t("panel.files.actions") }}</span>
            </div>

            <template v-for="item in remoteEngine.users" :key="item.userName">
              <div class="data-row remote-row">
                <span class="cell-main">
                  <strong>{{ item.userName }}</strong>
                  <small>{{ item.databases.join(", ") || t("panel.database.remoteUsers.noDatabases") }}</small>
                </span>
                <span :class="['status-pill', item.mode === 'anywhere' ? 'danger' : item.mode === 'allowlist' ? 'warn' : 'ok']">
                  {{ t(`panel.database.remoteUsers.mode.${item.mode}`) }}
                </span>
                <span class="cell-muted">{{ item.engineHosts.join(", ") || "-" }}</span>
                <span class="row-actions">
                  <button class="secondary compact" @click="openRemoteEditor(item)">
                    {{ remoteEditor.userName === item.userName ? t("panel.database.cancelAction") : t("panel.database.remoteUsers.edit") }}
                  </button>
                </span>
              </div>

              <div v-if="remoteEditor.userName === item.userName" class="form-surface remote-editor">
                <div class="form-grid">
                  <label>
                    <span class="field-label">{{ t("panel.database.remoteUsers.modeLabel") }}</span>
                    <select v-model="remoteEditor.mode">
                      <option value="closed">{{ t("panel.database.remoteUsers.mode.closed") }}</option>
                      <option value="allowlist">{{ t("panel.database.remoteUsers.mode.allowlist") }}</option>
                      <option value="anywhere">{{ t("panel.database.remoteUsers.mode.anywhere") }}</option>
                    </select>
                  </label>
                  <label v-if="remoteEditor.mode === 'allowlist'">
                    <span class="field-label">{{ t("panel.database.remoteUsers.origins") }}</span>
                    <input v-model="remoteEditor.origins" :placeholder="t('panel.database.remoteUsers.originsPlaceholder')" />
                  </label>
                </div>

                <p v-if="remoteEditor.mode === 'allowlist'" class="meta">
                  {{ t("panel.database.remoteUsers.originsHint") }}
                </p>

                <!-- "Her yer" tek tıkla seçilemez: kullanıcı ne yaptığını ayrıca beyan eder. -->
                <label v-if="remoteEditor.mode === 'anywhere'" class="remote-confirm">
                  <input v-model="remoteEditor.confirmAnywhere" type="checkbox" />
                  <span>{{ t("panel.database.remoteUsers.confirmAnywhere") }}</span>
                </label>

                <!-- Port SESSİZCE açılmaz; ne olacağı yazılır ve ayrıca onay istenir. -->
                <label class="remote-confirm">
                  <input v-model="remoteEditor.applyFirewall" type="checkbox" />
                  <span>{{ t("panel.database.remoteUsers.applyFirewall", { port: remoteEngine.port }) }}</span>
                </label>
                <p class="meta">
                  {{ remoteEditor.applyFirewall
                    ? t("panel.database.remoteUsers.firewallWillChange", { port: remoteEngine.port })
                    : t("panel.database.remoteUsers.firewallUntouched") }}
                </p>

                <div class="row-actions">
                  <button class="compact" :disabled="remoteSaving" @click="submitRemoteAccess">
                    {{ remoteSaving ? t("panel.database.saving") : t("panel.database.remoteUsers.save") }}
                  </button>
                  <button class="secondary compact" @click="remoteEditor.userName = ''">
                    {{ t("panel.database.cancelAction") }}
                  </button>
                </div>
              </div>
            </template>

            <p v-if="remoteLoading" class="empty-state">{{ t("panel.common.loading") }}</p>
            <p v-else-if="remoteEngine.users.length === 0" class="empty-state">{{ t("panel.database.noUsers") }}</p>
          </section>
        </template>

        <section v-else class="data-table connection-table">
          <div class="data-row table-head connection-row">
            <span>{{ t("panel.database.connectionProperty") }}</span>
            <span>{{ t("panel.database.connectionValue") }}</span>
          </div>
          <div class="data-row connection-row">
            <span>{{ t("panel.database.endpoint") }}</span>
            <strong>{{ workspace.host }}:{{ workspace.port }}</strong>
          </div>
          <div class="data-row connection-row">
            <span>{{ t("panel.database.adminUser") }}</span>
            <strong>{{ workspace.adminUserName }}</strong>
          </div>
          <div class="data-row connection-row">
            <span>{{ t("panel.database.adminPassword") }}</span>
            <code>{{ workspace.adminPassword || "-" }}</code>
          </div>
          <div class="data-row connection-row">
            <span>{{ t("panel.database.service") }}</span>
            <strong>{{ workspace.serviceName || "-" }}</strong>
          </div>
          <div class="data-row connection-row">
            <span>{{ t("panel.database.userScope") }}</span>
            <strong>{{ workspace.supportsHostScopedUsers ? t("panel.database.hostScopedUsers") : t("panel.database.roleUsers") }}</strong>
          </div>
        </section>
      </template>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import QueryHistory from "../components/QueryHistory.vue";
import ResourceConsumers from "../components/ResourceConsumers.vue";
import TablePager from "../components/TablePager.vue";
import type { ConsumerSource } from "../api/consumers";
import { usePagination } from "../composables/usePagination";
import { useAuthStore } from "../stores/auth";
import { lazyView } from "../utils/lazyView";

// Yan sekmeler talep üzerine iner (bkz. utils/lazyView.ts).
const CacheView = lazyView(() => import("./CacheView.vue"));
const DbToolSettingsView = lazyView(() => import("./DbToolSettingsView.vue"));
const DbTransferView = lazyView(() => import("./DbTransferView.vue"));
const WebDbView = lazyView(() => import("./WebDbView.vue"));
import {
  checkAndRepairDatabase,
  getDatabaseDetail,
  moveDatabase,
  openAdminer,
  hardenDatabase,
  openPhpMyAdmin,
  getUserRemoteAccess,
  setUserRemoteAccess,
  type DatabaseCheckResult,
  type DatabaseDetail,
  type DatabaseRemoteAccessEngine,
  type DatabaseUserRemoteAccess,
  type RemoteAccessMode
} from "../api/databases";
import { openReachable } from "../utils/url";
import { copyToClipboard } from "../utils/clipboard";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import { extractApiMessage } from "../stores/auth";
import { useDatabasesStore } from "../stores/databases";
import { useSoftwareStore } from "../stores/software";
import { useSitesStore } from "../stores/sites";
import { formatBytes as sharedFormatBytes } from "../utils/bytes";

type DatabaseModule = "databases" | "webdb" | "cache" | "transfer" | "settings" | "consumers";
type WorkspaceSection = "databases" | "users" | "remote" | "connection";

const { t } = useI18n();
const { confirm } = useConfirm();
const { prompt } = usePrompt();
const databasesStore = useDatabasesStore();
const softwareStore = useSoftwareStore();
const sitesStore = useSitesStore();
// Site↔DB bağı dropdown'u için site listesini yükle (Plesk gibi: DB'yi siteye bağla).
onMounted(() => {
  if (sitesStore.sites.length === 0) {
    sitesStore.fetchSites().catch(() => undefined);
  }
});

const notice = ref("");
const noticeError = ref(false);
const databaseSearch = ref("");
// Kaynak tüketicileri ucu yönetici-özel (yanıt BAŞKA müşterilerin sorgu metinlerini içerir);
// arayüz de bunu yansıtır, aksi halde sekme görünüp 403 alınırdı.
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");

const activeModule = ref<DatabaseModule>("databases");
const consumerTab = ref<"live" | "history">("live");
const consumerSource = ref<ConsumerSource>("mariadb");
const consumerSourceLabel = computed(() =>
  consumerSource.value === "mysql" ? "MySQL" : consumerSource.value === "postgresql" ? "PostgreSQL" : "MariaDB"
);
const activeSection = ref<WorkspaceSection>("databases");
const showDatabaseForm = ref(false);
const showUserForm = ref(false);

const databaseForm = reactive({
  name: "",
  userName: "",
  password: "",
  siteName: ""
});

const userForm = reactive({
  userName: "",
  password: "",
  databaseName: ""
});

/**
 * Veritabani DETAY karti.
 *
 * Boyut listeye DEGIL karta konur: information_schema'nin boyut sutunlari sunucunun en yavas
 * sutunlaridir ve listeye katmak, on veritabanli bir sayfanin acilisini onlara baglardi. Kullanici
 * ise cogu zaman TEK bir veritabaninin boyutunu merak eder.
 */
const openDetail = ref("");
const detail = ref<DatabaseDetail | null>(null);
const attachUserName = ref("");
const attaching = ref(false);
const copied = ref(false);
const checking = ref(false);
const checkResult = ref<DatabaseCheckResult | null>(null);
const selectedTables = ref<string[]>([]);

async function toggleDetail(name: string) {
  if (openDetail.value === name) {
    openDetail.value = "";
    return;
  }

  openDetail.value = name;
  // Onceki veritabaninin verisi yeni kartta gorunmesin.
  detail.value = null;
  checkResult.value = null;
  selectedTables.value = [];
  attachUserName.value = "";

  try {
    detail.value = await getDatabaseDetail(selectedEngine.value, name);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
    openDetail.value = "";
  }
}

async function attachUser(databaseName: string) {
  const [userName, host] = attachUserName.value.split("|");
  if (!userName) return;

  attaching.value = true;
  try {
    // Host boşsa '%' UYDURULMAZ: sunucu kullanıcının gerçek hesaplarını motordan okuyup hepsine verir.
    await databasesStore.grantAccess(selectedEngine.value, databaseName, userName, host || "");
    showNotice(t("panel.database.attached"), false);
    detail.value = await getDatabaseDetail(selectedEngine.value, databaseName);
    attachUserName.value = "";
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    attaching.value = false;
  }
}

/** Uygulamanin kopyalayip kullanabilecegi baglanti bilgisi. Parola YER TUTUCUDUR. */
function connectionSnippet(d: DatabaseDetail) {
  const user = d.users.length ? d.users[0].userName : "KULLANICI_ADI";
  return [
    `Host:      ${d.host}`,
    `Port:      ${d.port}`,
    `Database:  ${d.name}`,
    `User:      ${user}`,
    `Password:  ********`
  ].join("\n");
}

async function copyConnection(d: DatabaseDetail) {
  const oldu = await copyToClipboard(connectionSnippet(d));
  if (oldu) {
    copied.value = true;
    window.setTimeout(() => { copied.value = false; }, 2000);
    return;
  }

  // Kırmızı kutuda tek kelime "Kopyala" yazıyordu; bu bir hata mesajı değil, düğme adıdır.
  // Kullanıcının ne yapacağını söylemek gerekir: metin zaten ekranda, seçip kopyalayabilir.
  showNotice(t("panel.common.copyFailed"), true);
}

const moveTarget = ref("");
const moving = ref(false);

async function doMove(databaseName: string) {
  moving.value = true;
  try {
    await moveDatabase(selectedEngine.value, databaseName, moveTarget.value);
    showNotice(t("panel.database.moved"), false);
    detail.value = await getDatabaseDetail(selectedEngine.value, databaseName);
    moveTarget.value = "";
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    moving.value = false;
  }
}

async function runCheck(name: string) {
  checking.value = true;
  selectedTables.value = [];
  try {
    checkResult.value = await checkAndRepairDatabase(selectedEngine.value, name, []);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    checking.value = false;
  }
}

async function runRepair(name: string) {
  checking.value = true;
  try {
    // Onarim sonrasi sonuc, sunucunun YENIDEN DENETLEDIGI hali doner; "onarildi" sozu olcume
    // dayanir, niyete degil.
    checkResult.value = await checkAndRepairDatabase(selectedEngine.value, name, selectedTables.value);
    selectedTables.value = [];
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    checking.value = false;
  }
}

/** Bayt bicimi panelin geri kalaniyla AYNI (bkz. utils/bytes.ts); ekran kendi bicimini uydurmaz. */
function formatBytes(value: number) {
  return sharedFormatBytes(value, t);
}

const showImportForm = ref(false);
const importForm = reactive({
  databaseName: "",
  userName: "",
  password: "",
  siteName: ""
});

const credForm = reactive({
  adminUserName: "",
  password: "",
  port: 3306
});

const engines = computed(() => ([
  { key: "mariadb", label: t("panel.database.engine.mariadb"), componentId: "mariadb-11" },
  { key: "mysql", label: t("panel.database.engine.mysql"), componentId: "mysql-8" },
  { key: "postgresql", label: t("panel.database.engine.postgresql"), componentId: "postgresql-16" },
  { key: "mssql", label: t("panel.database.engine.mssql"), componentId: "" }
]));

const installedComponentIds = computed(() => new Set(
  (softwareStore.catalog?.installed ?? [])
    .filter((component) => component.status.toLowerCase() === "installed")
    .map((component) => component.componentId)));

const selectedEngine = computed(() => databasesStore.selectedEngine);
const selectedEngineMeta = computed(() =>
  engines.value.find((engine) => engine.key === selectedEngine.value) ?? engines.value[0]);

const selectedEngineInstalled = computed(() =>
  selectedEngine.value === "mssql" ||
  (!!selectedEngineMeta.value?.componentId &&
   installedComponentIds.value.has(selectedEngineMeta.value.componentId)));

const currentEngineStatus = computed(() => databasesStore.currentEngineStatus);
const needsCredentials = computed(() =>
  selectedEngine.value !== "mssql" &&
  selectedEngineInstalled.value &&
  currentEngineStatus.value !== null &&
  !currentEngineStatus.value.ready);
const defaultAdminUser = computed(() =>
  selectedEngine.value === "postgresql" ? "postgres" : "root");

const workspace = computed(() => databasesStore.currentWorkspace);

// Arama kutusundan ETKİLENMEYEN tam liste: form açılır listeleri bunu kullanır. Bir formdaki
// seçenekleri tablo aramasına bağlamak, kullanıcının aradığı anda seçim yapamamasına yol açar.
const allDatabases = computed(() => workspace.value?.databases ?? []);

const filteredDatabases = computed(() => {
  const query = databaseSearch.value.trim().toLowerCase();
  const rows = workspace.value?.databases ?? [];
  if (!query) {
    return rows;
  }

  return rows.filter((item) =>
    [item.name, item.owner, item.encoding, item.collation, ...item.users]
      .some((value) => (value ?? "").toLowerCase().includes(query)));
});

const filteredUsers = computed(() => {
  const query = databaseSearch.value.trim().toLowerCase();
  const rows = workspace.value?.users ?? [];
  if (!query) {
    return rows;
  }

  return rows.filter((item) =>
    [item.userName, item.host]
      .some((value) => (value ?? "").toLowerCase().includes(query)));
});

// Sayfalama ARAMADAN SONRA gelir: kullanıcı bütün listede arar, yalnız görüneni sayfalarız
// (bkz. composables/usePagination.ts).
const databasePager = usePagination(filteredDatabases);
const userPager = usePagination(filteredUsers);

// Arama değişince ilk sayfaya dön: 4. sayfadayken filtre daralınca boş tabloya düşülüyordu.
watch(databaseSearch, () => {
  databasePager.reset();
  userPager.reset();
});

onMounted(async () => {
  try {
    if (!softwareStore.catalog && !softwareStore.loading) {
      await softwareStore.fetchCatalog();
    }

    await databasesStore.fetchEngines();
    selectInitialEngine();
    await loadSelectedEngine();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
});

watch(() => databasesStore.selectedEngine, async () => {
  try {
    await loadSelectedEngine();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
});

// Seçili motor "yönetilebilir" ise çalışma alanını getir; kimlik bilgisi gerekiyorsa
// 409 gürültüsü üretmeden kayıt formunu hazırla (portu makul varsayılana çek).
async function loadSelectedEngine() {
  if (!selectedEngineInstalled.value || selectedEngine.value === "mssql") {
    return;
  }

  if (needsCredentials.value) {
    credForm.adminUserName = currentEngineStatus.value?.adminUserName ?? "";
    credForm.password = "";
    credForm.port = currentEngineStatus.value?.port || (selectedEngine.value === "postgresql" ? 5432 : 3306);
    return;
  }

  await databasesStore.fetchWorkspace(selectedEngine.value);
}

async function submitRegisterCredentials() {
  if (!credForm.password) {
    return;
  }

  try {
    await databasesStore.registerCredentials({
      engine: selectedEngine.value,
      adminUserName: credForm.adminUserName.trim() || undefined,
      password: credForm.password,
      port: credForm.port > 0 ? credForm.port : undefined
    });
    credForm.password = "";
    showNotice(t("panel.database.credentialsSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

function selectInitialEngine() {
  const preferred = engines.value.find((engine) =>
    engine.key !== "mssql" &&
    engine.componentId &&
    installedComponentIds.value.has(engine.componentId));

  databasesStore.selectEngine(preferred?.key ?? "mariadb");
}

function selectEngine(engine: string) {
  databaseSearch.value = "";
  showDatabaseForm.value = false;
  showUserForm.value = false;
  databasesStore.selectEngine(engine);
}

function openSection(section: WorkspaceSection) {
  activeSection.value = section;
  databaseSearch.value = "";

  if (section === "remote") {
    void loadRemoteAccess();
  }
}

// ---------------------------------------------------------------------------------------
// Uzak veritabanı erişimi (kullanıcı bazlı)
// ---------------------------------------------------------------------------------------

const remoteEngines = ref<DatabaseRemoteAccessEngine[]>([]);
const remoteLoading = ref(false);
const remoteSaving = ref(false);

const remoteEditor = reactive({
  userName: "",
  mode: "closed" as RemoteAccessMode,
  origins: "",
  confirmAnywhere: false,
  applyFirewall: false
});

const remoteEngine = computed(
  () => remoteEngines.value.find((item) => item.engine === selectedEngine.value) ?? null
);

/// Motor tarafında erişim verilmiş ama port kapalıysa kullanıcı yine de bağlanamaz.
/// Bunu söylemezsek kullanıcı "açtım ama çalışmıyor" ile baş başa kalır.
const remoteGrantedButPortClosed = computed(() => {
  const engine = remoteEngine.value;
  return !!engine && !engine.firewallOpen && engine.users.some((item) => item.remoteOpen);
});

async function loadRemoteAccess() {
  remoteLoading.value = true;
  try {
    remoteEngines.value = await getUserRemoteAccess();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    remoteLoading.value = false;
  }
}

function openRemoteEditor(item: DatabaseUserRemoteAccess) {
  if (remoteEditor.userName === item.userName) {
    remoteEditor.userName = "";
    return;
  }

  remoteEditor.userName = item.userName;
  remoteEditor.mode = item.mode;
  remoteEditor.origins = item.allowedOrigins.join(", ");
  // Onaylar her açılışta SIFIRLANIR: bir önceki kullanıcı için verilen onay devralınmamalı.
  remoteEditor.confirmAnywhere = false;
  remoteEditor.applyFirewall = false;
}

async function submitRemoteAccess() {
  const engine = remoteEngine.value;
  if (!engine) return;

  remoteSaving.value = true;
  try {
    const updated = await setUserRemoteAccess({
      engine: engine.engine,
      userName: remoteEditor.userName,
      mode: remoteEditor.mode,
      confirmAnywhere: remoteEditor.confirmAnywhere,
      applyFirewall: remoteEditor.applyFirewall,
      // Virgül, boşluk ya da noktalı virgülle ayrılmış liste kabul edilir; doğrulama sunucuda.
      allowedOrigins: remoteEditor.origins
        .split(/[\s,;]+/)
        .map((item) => item.trim())
        .filter((item) => item.length > 0)
    });

    remoteEngines.value = remoteEngines.value.map((item) => (item.engine === updated.engine ? updated : item));
    remoteEditor.userName = "";
    showNotice(t("panel.database.remoteUsers.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    remoteSaving.value = false;
  }
}

function engineStatusFor(engineKey: string) {
  return databasesStore.engines.find((item) => item.engine === engineKey) ?? null;
}

function engineBadgeClass(engineKey: string) {
  if (engineKey === "mssql") {
    return databasesStore.workspaces.mssql ? "ok" : "muted";
  }

  if (!installedComponentIds.value.has(selectedComponentId(engineKey))) {
    return "warn";
  }

  // Kurulu ama yönetici parolası yoksa (harici algılanan motor) sarı uyarı göster.
  const status = engineStatusFor(engineKey);
  return status && !status.ready ? "warn" : "ok";
}

function engineBadgeLabel(engineKey: string) {
  if (engineKey === "mssql") {
    return databasesStore.workspaces.mssql
      ? t("panel.php.installedState")
      : t("panel.database.autoDetect");
  }

  if (!installedComponentIds.value.has(selectedComponentId(engineKey))) {
    return t("panel.database.notInstalled");
  }

  const status = engineStatusFor(engineKey);
  return status && !status.ready
    ? t("panel.database.needsCredentials")
    : t("panel.php.installedState");
}

function engineDescription(engineKey: string) {
  return t(`panel.database.engineDescription.${engineKey}`);
}

function selectedComponentId(engineKey: string) {
  return engines.value.find((engine) => engine.key === engineKey)?.componentId ?? "";
}

async function installSelectedEngine() {
  if (!selectedEngineMeta.value?.componentId) {
    return;
  }

  try {
    await softwareStore.installComponent(selectedEngineMeta.value.componentId);
    await softwareStore.fetchCatalog();
    await databasesStore.fetchEngines();
    await loadSelectedEngine();
    showNotice(t("software.install.completed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function refreshWorkspace() {
  if (!selectedEngineInstalled.value) {
    return;
  }

  try {
    await databasesStore.fetchEngines();
    await loadSelectedEngine();
    showNotice(t("panel.database.refreshed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function submitCreateDatabase() {
  try {
    await databasesStore.createDatabase({
      engine: selectedEngine.value,
      name: databaseForm.name,
      userName: databaseForm.userName.trim() || undefined,
      password: databaseForm.password || undefined,
      siteName: databaseForm.siteName || undefined
    });
    databaseForm.name = "";
    databaseForm.userName = "";
    databaseForm.password = "";
    databaseForm.siteName = "";
    showDatabaseForm.value = false;
    showNotice(t("database.create.success"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function removeDatabase(name: string) {
  const record = workspace.value?.databases.find((item) => item.name === name);
  const impact = [`${t("panel.database.databaseName")}: ${name} (${workspace.value?.displayName || selectedEngine.value})`];
  if (record?.owner) {
    impact.push(`${t("panel.database.owner")}: ${record.owner}`);
  }
  if (record?.users.length) {
    impact.push(`${t("panel.database.dbUsers")}: ${record.users.join(", ")}`);
  }

  // Yüksek riskli onay: veritabanı ve içindeki tüm veri gider → DB adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.deleteDatabaseTitle", { name }),
    message: t("panel.confirm.deleteDatabaseMessage", { name }),
    impact,
    requireText: name,
    busyLabel: t("panel.database.removing"),
    action: async () => {
      try {
        await databasesStore.deleteDatabase(selectedEngine.value, name);
        showNotice(t("database.delete.success"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      }
    },
  });
}

async function submitCreateUser() {
  try {
    await databasesStore.createUser({
      engine: selectedEngine.value,
      userName: userForm.userName,
      password: userForm.password,
      // Host seçtirilmez; sunucu GÜVENLİ (yerel) varsayılanı uygular. Uzak erişim ayrı bir
      // ekrandan, açık onayla verilir — yeni hesap her yerden geçerli doğmaz.
      allowedHost: "",
      // Seçilirse o veritabanına otomatik GRANT ALL (kullanıcı "Access denied to database" almasın).
      databaseName: userForm.databaseName
    });

    userForm.userName = "";
    userForm.password = "";
    userForm.databaseName = "";
    showUserForm.value = false;
    showNotice(t("database.user_created"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

// Var olan DB+kullanıcıyı panele içe aktar (GRANT ALL eksikse + parolayı sakla → phpMyAdmin + yönetim).
async function submitImport() {
  if (!importForm.databaseName || !importForm.userName || !importForm.password) return;
  try {
    await databasesStore.importDatabase({
      engine: selectedEngine.value,
      databaseName: importForm.databaseName,
      userName: importForm.userName,
      host: "%",
      password: importForm.password,
      siteName: importForm.siteName || undefined
    });
    importForm.databaseName = "";
    importForm.userName = "";
    importForm.password = "";
    importForm.siteName = "";
    showImportForm.value = false;
    showNotice(t("panel.database.imported"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function removeUser(userName: string, host: string) {
  const linked = (workspace.value?.databases ?? [])
    .filter((item) => item.users.includes(userName))
    .map((item) => item.name);
  const impact = [
    `${t("panel.database.userName")}: ${userName}${host ? `@${host}` : ""}`,
    `${t("panel.confirm.impactDbUser")}: ${linked.length ? linked.join(", ") : "—"}`
  ];

  // Normal onay: yalnız erişim hesabı düşer, veritabanları ve içindeki veri yerinde kalır.
  await confirm({
    title: t("panel.confirm.deleteDatabaseUserTitle", { name: userName }),
    message: t("panel.database.confirmDeleteUser"),
    impact,
    busyLabel: t("panel.database.removing"),
    action: async () => {
      try {
        await databasesStore.deleteUser(selectedEngine.value, userName, host);
        showNotice(t("database.user_deleted"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      }
    },
  });
}

const openingDb = ref("");
/**
 * Adminer'i panelin kimlik-dogrulamali vekili uzerinden acar.
 *
 * Ham porta (http://sunucu:8897/) gitmek IIS'ten "400 - Invalid Hostname" aliyordu: o site
 * yalnizca loopback'e baglidir ve disaridan gelen istegin Host basligi bagla uymaz.
 */
async function openInAdminer(databaseName: string) {
  openingDb.value = databaseName;
  try {
    const result = await openAdminer(selectedEngine.value, databaseName);
    openReachable(result.url);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    openingDb.value = "";
  }
}

async function openInPhpMyAdmin(databaseName: string) {
  openingDb.value = databaseName;
  try {
    const result = await openPhpMyAdmin(selectedEngine.value, databaseName);
    // Backend 127.0.0.1 dönebilir; uzak tarayıcı için paneli gördüğümüz host'a çevir.
    openReachable(result.url);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    openingDb.value = "";
  }
}

async function changeUserPassword(userName: string, host: string) {
  // window.prompt DEĞİL: parolayı ekranda açık gösteriyordu ve tarayıcı diyalogları
  // engellendiğinde akış sessizce ölüyordu. Asgari uzunluk da artık ÖNDEN yazılı.
  const password = await prompt({
    title: t("panel.database.newPasswordPrompt", { user: userName }),
    label: t("panel.database.userPassword"),
    password: true,
    minLength: 6,
    confirmLabel: t("panel.database.changePassword")
  });

  if (password === null) {
    return;
  }
  if (password.length < 6) {
    showNotice(t("panel.database.passwordTooShort"), true);
    return;
  }
  try {
    await databasesStore.setUserPassword(selectedEngine.value, userName, password, host);
    showNotice(t("panel.database.passwordChanged"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

const hardening = ref(false);
// Sertleştirme yalnız MySQL/MariaDB'de anlamlı; backend de diğerlerinde artık 400 dönüyor.
const hardeningSupported = computed(() => selectedEngine.value === "mysql" || selectedEngine.value === "mariadb");

async function hardenSecurity() {
  if (!selectedEngineInstalled.value) {
    return;
  }
  hardening.value = true;
  try {
    const removed = await hardenDatabase(selectedEngine.value);
    showNotice(
      removed.length === 0
        ? t("panel.database.hardenedClean")
        : t("panel.database.hardenedRemoved", { items: removed.join(", ") }),
      false,
    );
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    hardening.value = false;
  }
}

function resolveApiMessage(error: unknown): string {
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
.db-detail {
  padding: 0.9rem 1.1rem 1.1rem;
  border-left: 3px solid var(--accent-1);
  background: color-mix(in srgb, var(--accent-1) 5%, transparent);
  display: grid;
  gap: 0.9rem;
}

.db-detail-loading { opacity: 0.7; font-size: 0.88rem; }

.db-detail-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.8rem;
}

.db-label {
  display: block;
  font-size: 0.74rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  opacity: 0.65;
  margin-bottom: 0.15rem;
}

.db-block { display: grid; gap: 0.4rem; }

.db-users { display: flex; flex-wrap: wrap; gap: 0.35rem; margin: 0; }

.db-attach { display: flex; gap: 0.5rem; flex-wrap: wrap; align-items: center; }
.db-attach select { min-width: 220px; }

.db-conn {
  margin: 0;
  padding: 0.6rem 0.8rem;
  border-radius: 6px;
  background: var(--surface-2, rgba(0, 0, 0, 0.16));
  font-family: ui-monospace, Consolas, monospace;
  font-size: 0.78rem;
  line-height: 1.5;
  white-space: pre-wrap;
  overflow-x: auto;
}

.db-warn { margin: 0; color: var(--warn-text); font-size: 0.86rem; }
.db-ok { margin: 0; font-size: 0.86rem; opacity: 0.85; }

.db-tables { display: grid; gap: 0.3rem; max-height: 20rem; overflow: auto; }

.db-table-row {
  display: grid;
  grid-template-columns: 2fr 1fr 1fr 1.4fr;
  gap: 0.5rem;
  align-items: center;
  font-size: 0.82rem;
  padding: 0.25rem 0;
}

.db-table-row label { display: flex; align-items: center; gap: 0.4rem; }

/* Dar ekranda tablo satiri kendi icinde kaysin, sayfa yatay kaymasin. */
@media (max-width: 720px) {
  .db-table-row { grid-template-columns: 1fr; gap: 0.15rem; }
}

.primary-tabs {
  width: fit-content;
  max-width: 100%;
}

.engine-toolbar {
  align-items: stretch;
}

.engine-tabs {
  flex: 1;
}

.engine-tabs button {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.engine-state {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--text-muted);
}

.engine-state.ok {
  background: var(--accent-1);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-1) 11%, transparent);
}

.engine-state.warn {
  background: var(--warn-text);
}

.compact-state {
  display: flex;
  justify-content: space-between;
  gap: 14px;
  align-items: center;
  padding: 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--input-bg);
}

.compact-state div,
.compact-state > span {
  display: grid;
  gap: 4px;
}

.compact-state span {
  color: var(--text-muted);
}

.install-meta {
  display: flex !important;
  align-items: center;
  gap: 10px !important;
}

.install-meta code,
.connection-row code {
  color: var(--text-soft);
  word-break: break-all;
}

.database-summary {
  grid-template-columns: 1.2fr 0.7fr 0.7fr 1fr;
}

.workspace-toolbar {
  align-items: center;
}

.database-search {
  width: min(310px, 38vw);
}

.inline-create {
  grid-template-columns: minmax(220px, 430px) auto;
  align-items: end;
}

.inline-create button {
  width: fit-content;
}

.database-row {
  grid-template-columns: minmax(190px, 1.35fr) 0.8fr 0.7fr minmax(180px, 1.1fr) auto;
}

.user-row {
  grid-template-columns: minmax(190px, 1.2fr) 0.9fr 1fr 0.7fr;
}

.grant-grid {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.grant-surface {
  max-width: 1180px;
}

.form-action {
  width: fit-content;
}

.manage-existing {
  max-width: 760px;
}

.manage-head {
  display: flex;
  align-items: center;
  gap: 12px;
  justify-content: space-between;
}

.connection-table {
  max-width: 900px;
}

.connection-row {
  grid-template-columns: minmax(190px, 0.7fr) 1.3fr;
  min-width: 560px;
}

.compact-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.compact-head h3,
.compact-head p {
  margin: 0;
}

.section-head {
  display: flex;
  gap: 12px;
  justify-content: space-between;
}

.meta {
  margin: 0;
  color: var(--text-muted);
}

@media (max-width: 980px) {
  .database-summary,
  .grant-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .workspace-toolbar {
    align-items: flex-start;
  }

  .database-search {
    width: min(100%, 310px);
  }
}

@media (max-width: 720px) {
  .compact-state,
  .install-meta {
    align-items: flex-start;
    flex-direction: column;
  }

  .database-summary,
  .grant-grid,
  .inline-create {
    grid-template-columns: 1fr;
  }

  .workspace-toolbar .toolbar-group {
    width: 100%;
  }

  .database-search {
    flex: 1;
    min-width: 180px;
  }
}
</style>
