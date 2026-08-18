<template>
  <main v-if="mode !== 'dashboard'" class="setup-shell">
    <section class="auth-card">
      <!--
        GİRİŞ EKRANI MARKASI: burada gösterilen HER ZAMAN sunucu geneli (yöneticinin) markasıdır.
        Bayi bazlı marka giriş ekranında GÖSTERİLEMEZ: kullanıcı henüz kimliğini kanıtlamadığı
        için hangi bayiye ait olduğu bilinmez. Adres/alan adından tahmin etmek ise kimlik
        doğrulanmadan "bu sunucuda şu bayi var" bilgisini herkese açardı. Plesk'in davranışı da
        budur (merkezî giriş sayfası bayi bazında markalanmaz).
      -->
      <div v-if="brandLogo || brandName" class="auth-brand">
        <img v-if="brandLogo" class="auth-logo" :src="brandLogo" :alt="brandName" />
        <span class="auth-brand-name">{{ brandName }}</span>
      </div>

      <div class="auth-copy">
        <span class="status-line"></span>
        <p class="meta">{{ t("panel.layout.subtitle") }}</p>
        <h1>{{ heading }}</h1>
        <p class="lead">{{ subtitle }}</p>
      </div>

      <!-- GİRİŞ EKRANI HATA ŞERİDİ: role olmadan ekran okuyucu yanlış parolayı hiç duymuyordu. -->
      <p v-if="banner" :class="['notice', { error: bannerError }]" :role="bannerError ? 'alert' : 'status'">
        {{ banner }}
      </p>

      <section v-if="mode === 'setup' && installing" class="setup-progress">
        <h3>{{ installProgress?.failed ? t("panel.setup.progress.failedTitle") : installProgress?.completed ? t("panel.setup.progress.doneTitle") : t("panel.setup.progress.title") }}</h3>
        <p class="meta">{{ t("panel.setup.progress.hint") }}</p>
        <ul class="progress-steps">
          <li v-for="(step, index) in installProgress?.steps ?? []" :key="index" :class="['progress-step', step.status]">
            <span class="step-icon" aria-hidden="true">
              <span v-if="step.status === 'running'" class="step-spinner"></span>
              <span v-else-if="step.status === 'done'">✓</span>
              <span v-else>✕</span>
            </span>
            <span class="step-name">{{ setupStepLabel(step.name) }}</span>
          </li>
          <li v-if="(installProgress?.steps?.length ?? 0) === 0" class="progress-step running">
            <span class="step-icon"><span class="step-spinner"></span></span>
            <span class="step-name">{{ t("panel.setup.progress.preparing") }}</span>
          </li>
        </ul>
        <p v-if="installProgress?.completed" class="notice" role="status">{{ t("panel.setup.progress.redirecting") }}</p>
        <p v-if="installProgress?.failed" class="notice error" role="alert">{{ installProgress?.error }}</p>
      </section>

      <form v-if="mode === 'setup' && !installing" class="form-grid" @submit.prevent="submitSetup">
        <label>
          <span>{{ t("panel.form.adminUserName") }}</span>
          <input v-model="setupForm.userName" minlength="3" maxlength="32" required />
        </label>
        <label>
          <span>{{ t("panel.form.email") }}</span>
          <input v-model="setupForm.email" type="email" required />
        </label>
        <label>
          <span>{{ t("panel.form.password") }}</span>
          <PasswordField v-model="setupForm.password" minlength="12" required />
        </label>
        <label>
          <span>{{ t("panel.form.confirmPassword") }}</span>
          <PasswordField v-model="setupForm.confirmPassword" minlength="12" required />
        </label>
        <label>
          <span>{{ t("panel.form.securePath") }}</span>
          <input v-model="setupForm.secureEntryPath" pattern="^[a-z0-9]+(?:-[a-z0-9]+)*$" required />
        </label>
        <label>
          <span>{{ t("panel.form.language") }}</span>
          <select v-model="setupForm.preferredLanguage">
            <option value="tr">{{ t("panel.language.tr") }}</option>
            <option value="en">{{ t("panel.language.en") }}</option>
          </select>
        </label>
        <label>
          <span>{{ t("panel.form.port") }}</span>
          <input v-model.number="setupForm.preferredHttpsPort" type="number" min="1" max="65535" required />
        </label>
        <label class="checkbox">
          <input v-model="setupForm.installRecommendedComponents" type="checkbox" />
          <span>{{ t("panel.setup.installRecommended") }}</span>
        </label>

        <button type="submit" :disabled="setupStore.loading">
          {{ t("panel.setup.submit") }}
        </button>
      </form>

      <form v-else-if="authView === 'login'" class="form-grid" @submit.prevent="submitLogin">
        <label>
          <span>{{ t("panel.auth.userNameOrEmail") }}</span>
          <input v-model="loginForm.userNameOrEmail" required />
        </label>
        <label>
          <span>{{ t("panel.form.password") }}</span>
          <PasswordField v-model="loginForm.password" minlength="12" required />
        </label>
        <label v-if="requiresTwoFactor">
          <span>{{ t("panel.auth.twoFactorCode") }}</span>
          <input v-model="loginForm.twoFactorCode" maxlength="14" autocomplete="one-time-code" required />
          <small class="field-hint">{{ t("panel.auth.twoFactorRecoveryHint") }}</small>
        </label>
        <label class="checkbox">
          <input v-model="loginForm.rememberMe" type="checkbox" />
          <span>{{ t("panel.auth.rememberMe") }}</span>
        </label>

        <button type="submit" :disabled="authStore.loading">
          {{ requiresTwoFactor ? t("panel.auth.verify") : t("panel.auth.login") }}
        </button>
        <button type="button" class="secondary link-button" @click="authView = 'forgot'">
          {{ t("panel.auth.forgotPassword") }}
        </button>
      </form>

      <form v-else-if="authView === 'forgot'" class="form-grid" @submit.prevent="submitForgotPassword">
        <p class="lead">{{ t("panel.auth.forgotHint") }}</p>
        <label>
          <span>{{ t("panel.form.email") }}</span>
          <input v-model="forgotForm.email" type="email" required />
        </label>
        <button type="submit" :disabled="authStore.loading">{{ t("panel.auth.forgotSubmit") }}</button>
        <button type="button" class="secondary link-button" @click="authView = 'login'">
          {{ t("panel.auth.backToLogin") }}
        </button>
      </form>

      <form v-else-if="authView === 'reset'" class="form-grid" @submit.prevent="submitResetPassword">
        <p class="lead">{{ t("panel.auth.resetHint") }}</p>
        <label>
          <span>{{ t("panel.auth.newPassword") }}</span>
          <PasswordField v-model="resetForm.newPassword" minlength="12" required />
        </label>
        <label>
          <span>{{ t("panel.auth.newPasswordConfirm") }}</span>
          <PasswordField v-model="resetForm.confirmPassword" minlength="12" required />
        </label>
        <button type="submit" :disabled="authStore.loading">{{ t("panel.auth.resetSubmit") }}</button>
        <button type="button" class="secondary link-button" @click="authView = 'login'">
          {{ t("panel.auth.backToLogin") }}
        </button>
      </form>
    </section>
  </main>

  <MainLayout
    v-else
    :sections="dashboardSections"
    :active-section="activeSection"
    :title="activeSectionMeta.label"
    :description="activeSectionMeta.description"
    :subtitle="t('panel.layout.subtitle')"
    :panel-name="brandName"
    :logo-url="branding.logoDataUrl"
    :logo-link-url="branding.logoLinkUrl"
    :machine-name="systemStore.overview?.machineName || t('panel.common.notAvailable')"
    :secure-entry-path="currentSecurePath"
    :user-name="authStore.session?.user?.userName || '-'"
    :user-email="authStore.session?.user?.email || '-'"
    :role="authStore.session?.user?.role || '-'"
    :panel-version="systemStore.overview?.panelVersion || '-'"
    :is-two-factor-enabled="authStore.session?.user?.isTwoFactorEnabled ?? false"
    :resolved-theme="resolvedThemeMode"
    :attention-summary="attentionSummary"
    :attention-loading="attentionLoading"
    :attention-error="attentionError"
    :attention-open="attentionOpen"
    @navigate="handleNavigation"
    @open-site="openSiteFromSearch"
    @open-mail-domain="openMailDomainFromSearch"
    @refresh="refreshDashboard"
    @toggle-theme="toggleTheme"
    @logout="logout"
    @attention-toggle="toggleAttention"
    @attention-close="attentionOpen = false"
    @attention-refresh="loadAttention(false)">
    <template #banner>
      <div class="banner-stack">
        <!--
          ASKI ŞERİDİ (P0): askıdaki abonelikte sunucu bütün yazma isteklerini
          `403 subscription.suspended_readonly` ile reddediyor. Bu şerit olmadan kullanıcı her
          düğmede kırmızı hata görür ve sebebini ancak hatayı okursa anlar. Sebep ADIYLA yazılır;
          "askıda" tek kelimesi "ne yapmam gerekiyor" sorusunu cevaplamıyordu.
        -->
        <p v-if="subscriptionStore.isSuspended" class="notice error suspended-banner" role="alert">
          <strong>{{ t("panel.subscription.suspendedBannerTitle") }}</strong>
          <span>{{ t(`panel.subscription.reason.${subscriptionStore.suspendReason}`) }}</span>
        </p>
        <!-- Bulundugunuz sayfaya ait dikkat maddeleri. Tek yerden beslenir; hedef sayfalarin
             hicbirine ayri ayri eklemek gerekmez. Madde yoksa hicbir sey gorunmez. -->
        <AttentionSectionNotice
          :items="attentionItemsFor(activeSection)"
          @open="attentionOpen = true" />
        <p v-if="showDefaultPortWarning" class="notice warn-banner" role="status">
          {{ t("panel.dashboard.defaultPortWarning") }}
        </p>
        <p v-if="banner" :class="['notice', { error: bannerError }]" :role="bannerError ? 'alert' : 'status'">
          {{ banner }}
        </p>
      </div>
    </template>

    <DashboardOverviewView
      v-if="activeSection === 'home'"
      :overview="systemStore.overview"
      :loading="systemStore.loading"
      :sites-count="sitesStore.sites.length"
      :total-binding-count="totalBindingCount"
      :ftp-user-count="ftpUserCount"
      :database-count="databaseCount"
      :security-status-label="securityStatusLabel"
      :secure-path="currentSecurePath"
      :audit-items="authStore.auditLogs"
      :allowed-sections="dashboardSections.map((section) => section.id)"
      :panel-update="panelUpdate"
      :panel-update-error="panelUpdateLoad.message.value"
      :attention="attentionSummary"
      @navigate="handleNavigation"
      @retry-panel-update="loadPanelUpdate" />

    <WebsiteWorkspaceView
      v-else-if="activeSection === 'sites' && (isAdmin || isReseller || isCustomer)"
      :open-request="siteOpenRequest" />

    <SiteBuilderView v-else-if="activeSection === 'sitebuilder'" />

    <DatabasesView v-else-if="activeSection === 'database' && isAdmin" />

    <MyDatabasesView v-else-if="activeSection === 'database' && (isReseller || isCustomer)" />

    <MailView
      v-else-if="activeSection === 'mail' && (isAdmin || isCustomer || isReseller)"
      :key="mailDomainFromSearch"
      :domain="mailDomainFromSearch" />

    <MonitoringView
      v-else-if="activeSection === 'monitor'"
      :overview="systemStore.overview"
      :loading="systemStore.loading" />

    <SecurityView v-else-if="activeSection === 'security' && isAdmin" />
    <!-- Denetim kayıtları HER role açık: uç kapsamı kendisi uyguluyor (AuditVisibility), müşteri
         kendi aboneliğini, bayi kendi müşterilerini, yönetici sunucu genelini görür. Ekranı yalnız
         yöneticiye göstermek, kiracının kendi hesabında ne olduğunu göremediği anlamına gelirdi. -->
    <AuditLogsView v-else-if="activeSection === 'audit'" />

    <BillingView v-else-if="activeSection === 'billing' && isAdmin" />

    <FilesView v-else-if="activeSection === 'files' && (isAdmin || isCustomer || isReseller)" />

    <!-- Yedek bölümü müşteride VE bayide: BackupController `Reseller` rolüne izin veriyor
         (kapsam EnsureSiteAsync ile kendi müşterilerinin siteleriyle sınırlı). Menüde yoktu,
         yani bayi kendi müşterisinin yedeğini alamıyordu. -->
    <BackupView v-else-if="activeSection === 'backup' && (isCustomer || isReseller)" />

    <CronView v-else-if="activeSection === 'tasks' && (isAdmin || isReseller || isCustomer)" />

    <SubUsersView v-else-if="activeSection === 'subusers' && isCustomer && !isSubUser" />

    <!-- Hesap yönetimi (Müşteriler & Bayiler) ve "Hesabım" artık ayrı ekranlar. -->
    <UsersView v-else-if="activeSection === 'accounts' && (isAdmin || isReseller)" @notify="showBanner" />

    <AccountView v-else-if="activeSection === 'account'" @notify="showBanner" />
    <AiConnectionView v-else-if="activeSection === 'ai'" />

    <ServerToolsView v-else-if="activeSection === 'tools' && isAdmin" />

    <MigrationView v-else-if="activeSection === 'migration' && isAdmin" />

    <SoftwareView v-else-if="activeSection === 'software' && isAdmin" />

    <AdvisorView v-else-if="activeSection === 'advisor' && isAdmin" @navigate="handleNavigation" />

    <!-- Bayi de bu bölüme girer ama YALNIZ "Marka" sekmesini görür: sunucu ayarları (güvenlik,
         bildirim, güncelleme, lisans) hem sekme hem de uç düzeyinde yöneticiye kilitli kalır. -->
    <section v-else-if="activeSection === 'settings' && (isAdmin || isReseller)" class="panel module-shell settings-workspace">
      <div class="module-head">
        <div>
          <h2>{{ t("panel.settings.title") }}</h2>
          <p class="meta">{{ t("panel.settings.subtitle") }}</p>
        </div>
        <span :class="['status-pill', securityStatusLabel === t('panel.status.enabled') ? 'ok' : 'warn']">
          {{ securityStatusLabel }}
        </span>
      </div>

      <div class="module-toolbar">
        <div class="segmented-tabs settings-tabs">
          <button v-if="isAdmin" :class="{ active: settingsTab === 'general' }" @click="settingsTab = 'general'">
            {{ t("panel.settings.tab.general") }}
          </button>
          <!-- Marka sekmesi bayide de var: bayi kendi markasını kendisi yönetir (Plesk modeli). -->
          <button :class="{ active: settingsTab === 'branding' }" @click="openBrandingTab">
            {{ t("panel.branding.tab") }}
          </button>
          <button v-if="isAdmin" :class="{ active: settingsTab === 'security' }" @click="settingsTab = 'security'">
            {{ t("panel.settings.tab.security") }}
          </button>
          <button v-if="isAdmin" :class="{ active: settingsTab === 'errorReports' }" @click="settingsTab = 'errorReports'">
            {{ t("panel.settings.tab.errorReports") }}
          </button>
          <button v-if="isAdmin" :class="{ active: settingsTab === 'notifications' }" @click="settingsTab = 'notifications'">
            {{ t("panel.settings.tab.notifications") }}
          </button>
          <button v-if="isAdmin" :class="{ active: settingsTab === 'updates' }" @click="settingsTab = 'updates'">
            {{ t("panel.settings.tab.updates") }}
          </button>
          <!-- Lisans YALNIZ yöneticide (uç de `Administrator`). Lisanslama kapalı kurulumlarda
               sekme yine görünür ama içerik "kapalı" bilgi kutusundan ibaret olur: kullanıcı
               "acaba lisans ekranı var mı" diye panelde aramasın. -->
          <button v-if="isAdmin" :class="{ active: settingsTab === 'license' }" @click="settingsTab = 'license'">
            {{ t("panel.license.title") }}
          </button>
        </div>
        <div class="settings-account">
          <strong>{{ authStore.session?.user?.userName }}</strong>
          <span>{{ authStore.session?.user?.email }}</span>
        </div>
      </div>

      <section v-if="settingsTab === 'general' && isAdmin" class="form-surface settings-section">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.settings.generalTitle") }}</h3>
            <p class="meta">{{ t("panel.settings.generalHint") }}</p>
          </div>
          <span v-if="panelPreferences.restartRequired" class="status-pill warn">{{ t("panel.settings.restartRequired") }}</span>
        </div>

        <!-- Tercihler gelmeden alanlar varsayılan değerleriyle doluydu; kaydedilirse gerçek
             ayarların üzerine yazılıyordu. -->
        <p v-if="!panelPreferencesLoaded" class="empty-state" role="status" aria-live="polite">
          {{ t("panel.common.loading") }}
        </p>

        <template v-else>
        <div class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.settings.panelName") }}</span>
            <input v-model="panelPreferences.panelName" maxlength="64" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.form.language") }}</span>
            <select v-model="panelPreferences.defaultLanguage">
              <option value="tr">{{ t("panel.language.tr") }}</option>
              <option value="en">{{ t("panel.language.en") }}</option>
            </select>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.settings.themeMode") }}</span>
            <select v-model="panelPreferences.themeMode">
              <option value="dark">{{ t("panel.settings.theme.dark") }}</option>
              <option value="light">{{ t("panel.settings.theme.light") }}</option>
              <option value="system">{{ t("panel.settings.theme.system") }}</option>
            </select>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.settings.uiDensity") }}</span>
            <select v-model="panelPreferences.uiDensity">
              <option value="compact">{{ t("panel.settings.density.compact") }}</option>
              <option value="comfortable">{{ t("panel.settings.density.comfortable") }}</option>
            </select>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.settings.serverAddress") }}</span>
            <input v-model="panelPreferences.serverAddress" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.settings.defaultSiteRoot") }}</span>
            <input v-model="panelPreferences.defaultSiteRoot" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.settings.defaultBackupRoot") }}</span>
            <input v-model="panelPreferences.defaultBackupRoot" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.form.port") }}</span>
            <input v-model.number="panelPreferences.preferredHttpsPort" type="number" min="1" max="65535" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.settings.lockoutMinutes") }}</span>
            <input v-model.number="panelPreferences.loginLockoutMinutes" type="number" min="1" max="1440" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.settings.defaultRuntimeProfile") }}</span>
            <select v-model="panelPreferences.defaultRuntimeProfile">
              <option value="auto">{{ t("panel.sites.runtime.auto") }}</option>
              <option value="static">{{ t("panel.sites.runtime.static") }}</option>
              <option value="php">{{ t("panel.sites.runtime.php") }}</option>
              <option value="aspnet">{{ t("panel.sites.runtime.aspnet") }}</option>
              <option value="classic-asp">{{ t("panel.sites.runtime.classicAsp") }}</option>
              <option value="mixed">{{ t("panel.sites.runtime.mixed") }}</option>
              <option value="node">{{ t("panel.sites.runtime.node") }}</option>
              <option value="python">{{ t("panel.sites.runtime.python") }}</option>
            </select>
          </label>
          <label class="check-field settings-check">
            <input v-model="panelPreferences.allowHttpFallback" type="checkbox" />
            <span>{{ t("panel.settings.allowHttpFallback") }}</span>
          </label>
          <label class="check-field settings-check">
            <input v-model="panelPreferences.installRuntimeIfMissing" type="checkbox" />
            <span>{{ t("panel.settings.installRuntimeIfMissing") }}</span>
          </label>
        </div>
        <div class="row-actions">
          <button class="compact" @click="savePanelPreferences">{{ t("panel.settings.saveGeneral") }}</button>
        </div>

        <!-- Panel erişimi: paneli yöneticinin KENDİ alan adıyla ve geçerli sertifikayla dışarıya
             açar. Yapay zeka istemcileri kendinden imzalı sertifikayı kabul etmiyor, bu yüzden
             MCP bağlantısının ön koşulu. -->
        <PanelAccessCard />
        </template>
      </section>

      <!-- ÖZEL MARKA (white-label). Yönetici sunucu markasını, bayi kendi markasını düzenler;
           hangi kapsama yazıldığı isteğin GÖVDESİNDEN değil oturumdan belirlenir (BrandingController). -->
      <section v-else-if="settingsTab === 'branding'" class="form-surface settings-section">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.branding.title") }}</h3>
            <p class="meta">{{ isReseller ? t("panel.branding.resellerHint") : t("panel.branding.serverHint") }}</p>
          </div>
          <span class="status-pill">{{ isReseller ? t("panel.branding.scopeReseller") : t("panel.branding.scopeServer") }}</span>
        </div>

        <p v-if="!brandingLoaded" class="empty-state" role="status" aria-live="polite">
          {{ t("panel.common.loading") }}
        </p>

        <template v-else>
          <div class="form-grid">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.branding.panelName") }}</span>
              <input v-model="brandingForm.panelName" maxlength="64" :placeholder="panelPreferences.panelName" />
              <small class="field-hint">{{ t("panel.branding.panelNameHint") }}</small>
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.branding.accent") }}</span>
              <span class="accent-row">
                <input v-model="brandingForm.accentColor" maxlength="7" placeholder="#2563eb" />
                <input
                  class="accent-swatch"
                  type="color"
                  :value="brandingForm.accentColor || '#2563eb'"
                  :title="t('panel.branding.accent')"
                  @input="pickAccentColor" />
              </span>
              <small class="field-hint">{{ t("panel.branding.accentHint") }}</small>
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.branding.logoLink") }}</span>
              <input v-model="brandingForm.logoLinkUrl" maxlength="255" placeholder="https://" />
              <small class="field-hint">{{ t("panel.branding.logoLinkHint") }}</small>
            </label>
          </div>
          <div class="row-actions">
            <button class="compact" @click="saveBranding">{{ t("panel.branding.save") }}</button>
          </div>

          <!--
            GÖRSELLER SEÇİLİR SEÇİLMEZ yüklenir (ayrı bir "kaydet" yok): dosya seçmek zaten bir
            onaydır ve iki adımlı akışta kullanıcı, seçtiği logonun kaydedilmediğini ancak sayfayı
            yenileyince fark ederdi. Kabul kararı SUNUCUDA, dosyanın İÇERİĞİNE bakılarak verilir;
            buradaki `accept` yalnızca dosya seçicinin varsayılan süzgecidir, bir güvenlik denetimi
            DEĞİLDİR.
          -->
          <div class="brand-assets">
            <div class="brand-asset">
              <span class="field-label">{{ t("panel.branding.logo") }}</span>
              <div class="brand-asset-preview">
                <img v-if="brandingForm.logoDataUrl" :src="brandingForm.logoDataUrl" :alt="t('panel.branding.logo')" />
                <span v-else class="meta">{{ t("panel.branding.none") }}</span>
              </div>
              <input
                type="file"
                accept="image/png,image/jpeg,image/gif,image/webp"
                @change="uploadBrandingAsset('logo', $event)" />
              <button
                type="button"
                class="secondary compact"
                :disabled="!brandingForm.logoDataUrl"
                @click="removeBrandingAsset('logo')">
                {{ t("panel.branding.remove") }}
              </button>
              <small class="field-hint">{{ t("panel.branding.logoHint") }}</small>
            </div>

            <div class="brand-asset">
              <span class="field-label">{{ t("panel.branding.favicon") }}</span>
              <div class="brand-asset-preview">
                <img v-if="brandingForm.faviconDataUrl" :src="brandingForm.faviconDataUrl" :alt="t('panel.branding.favicon')" />
                <span v-else class="meta">{{ t("panel.branding.none") }}</span>
              </div>
              <input
                type="file"
                accept="image/png,image/x-icon,image/vnd.microsoft.icon,.ico"
                @change="uploadBrandingAsset('favicon', $event)" />
              <button
                type="button"
                class="secondary compact"
                :disabled="!brandingForm.faviconDataUrl"
                @click="removeBrandingAsset('favicon')">
                {{ t("panel.branding.remove") }}
              </button>
              <small class="field-hint">{{ t("panel.branding.faviconHint") }}</small>
            </div>
          </div>
        </template>
      </section>

      <section v-else-if="settingsTab === 'security' && isAdmin" class="form-surface settings-section">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.security.title") }}</h3>
            <p class="meta">{{ t("panel.settings.securityHint") }}</p>
          </div>
        </div>
        <div class="form-grid two">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.form.securePath") }}</span>
            <input v-model="securityForm.secureEntryPath" pattern="^[a-z0-9]+(?:-[a-z0-9]+)*$" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.security.allowedIps") }}</span>
            <textarea v-model="securityForm.allowedIpAddressesText" rows="4" :placeholder="t('panel.security.allowedIpsPlaceholder')"></textarea>
          </label>
        </div>
        <div class="row-actions">
          <button class="compact" @click="saveSecuritySettings">{{ t("panel.security.save") }}</button>
        </div>
      </section>

      <NotificationsView v-else-if="settingsTab === 'notifications' && isAdmin" />
      <ErrorReportsView v-else-if="settingsTab === 'errorReports' && isAdmin" />
      <UpdateView v-else-if="settingsTab === 'updates' && isAdmin" />
      <LicenseView v-else-if="settingsTab === 'license' && isAdmin" />
    </section>
  </MainLayout>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import MainLayout from "../layouts/MainLayout.vue";
import { readSecureEntryPath } from "../api/client";
import { getFtpUsers } from "../api/ftp";
import {
  emptyBranding,
  getBranding,
  getBrandingSettings,
  getPanelPreferences,
  removeBrandingImage,
  updateBrandingSettings,
  updatePanelPreferences,
  uploadBrandingImage,
  type Branding,
  type BrandingImageKind,
  type PanelPreferences
} from "../api/panelSettings";
import { useAppsStore } from "../stores/apps";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useDatabasesStore } from "../stores/databases";
import { usePhpStore } from "../stores/php";
import { useProxyStore } from "../stores/proxy";
import { useSetupStore, type OperationProgress } from "../stores/setup";
import { useSitesStore } from "../stores/sites";
import { useSoftwareStore } from "../stores/software";
import { useSubscriptionStore } from "../stores/subscription";
import { useSystemStore } from "../stores/system";
import AttentionSectionNotice from "../components/AttentionSectionNotice.vue";
import PanelAccessCard from "../components/PanelAccessCard.vue";
import DashboardOverviewView from "./DashboardOverviewView.vue";
import { checkForUpdate, type UpdateStatus } from "../api/update";
import { useSectionError } from "../composables/useNotice";
import { getAttentionSummary, type AttentionSummary } from "../api/attention";
import { lazyView } from "../utils/lazyView";

/*
 * BÖLÜMLER TALEP ÜZERİNE İNDİRİLİR (bkz. utils/lazyView.ts).
 *
 * Bu kabuk bölüm görünümlerini statik import ediyordu: giriş ekranını görmek için bile tüm
 * panelin kodu (tek parça 1,1 MB) indiriliyordu. Artık her bölüm kendi paketinde ve yalnız
 * açıldığında iner.
 *
 * DashboardOverviewView BİLEREK statik: her girişte açılan ilk ekran o; onu da ayırmak her
 * oturum açılışına bir gidiş-dönüş ve bir "Yükleniyor…" titremesi eklerdi.
 */
const AuditLogsView = lazyView(() => import("./AuditLogsView.vue"));
const BillingView = lazyView(() => import("./BillingView.vue"));
const CronView = lazyView(() => import("./CronView.vue"));
const DatabasesView = lazyView(() => import("./DatabasesView.vue"));
const MigrationView = lazyView(() => import("./MigrationView.vue"));
const MyDatabasesView = lazyView(() => import("./MyDatabasesView.vue"));
const AdvisorView = lazyView(() => import("./AdvisorView.vue"));
const FilesView = lazyView(() => import("./FilesView.vue"));
const MailView = lazyView(() => import("./MailView.vue"));
const MonitoringView = lazyView(() => import("./MonitoringView.vue"));
const NotificationsView = lazyView(() => import("./NotificationsView.vue"));
const SecurityView = lazyView(() => import("./SecurityView.vue"));
const ServerToolsView = lazyView(() => import("./ServerToolsView.vue"));
const BackupView = lazyView(() => import("./BackupView.vue"));
const SubUsersView = lazyView(() => import("./SubUsersView.vue"));
const UsersView = lazyView(() => import("./UsersView.vue"));
const AccountView = lazyView(() => import("./AccountView.vue"));
const AiConnectionView = lazyView(() => import("./AiConnectionView.vue"));
const ErrorReportsView = lazyView(() => import("./ErrorReportsView.vue"));
const LicenseView = lazyView(() => import("./LicenseView.vue"));
const SoftwareView = lazyView(() => import("./SoftwareView.vue"));
const UpdateView = lazyView(() => import("./UpdateView.vue"));
const WebsiteWorkspaceView = lazyView(() => import("./WebsiteWorkspaceView.vue"));
const SiteBuilderView = lazyView(() => import("./SiteBuilderView.vue"));

type ViewMode = "setup" | "auth" | "dashboard";
type SectionItem = {
  id: string;
  label: string;
  description: string;
  glyph: string;
  badge?: string;
  /** Rozetin üzerine gelince görünen açıklama: eksikleri ADIYLA sayar. */
  badgeTitle?: string;
};
// Hesap yönetimi ("accounts" → UsersView) ve "Hesabım" ("account" → AccountView) artık ayrı bölümler;
// Ayarlar yalnızca yönetici kapsamındaki sunucu/panel ayarlarını barındırır.
type SettingsTab = "general" | "branding" | "security" | "notifications" | "errorReports" | "updates" | "license";

const i18n = useI18n();
// params: "{count} tane daha" gibi sayi iceren metinler icin. Verilmezse davranis aynidir.
const t = (key: string, params?: Record<string, unknown>) => {
  try {
    const value = params ? i18n.t(key, params) : i18n.t(key);
    if (typeof value !== "string") {
      console.error("Unexpected i18n value", key, value);
      return key;
    }

    return value;
  } catch (error) {
    console.error("Failed i18n key", key, error);
    return key;
  }
};
const setupStore = useSetupStore();
const authStore = useAuthStore();
const appsStore = useAppsStore();
const proxyStore = useProxyStore();
const softwareStore = useSoftwareStore();
const systemStore = useSystemStore();
const sitesStore = useSitesStore();
const phpStore = usePhpStore();
const databasesStore = useDatabasesStore();
const subscriptionStore = useSubscriptionStore();

const mode = ref<ViewMode>("setup");
const installing = ref(false);
const installProgress = ref<OperationProgress | null>(null);
const setupSecurePath = ref("");
const authView = ref<"login" | "forgot" | "reset">("login");
const forgotForm = reactive({ email: "" });
const resetForm = reactive({ token: "", newPassword: "", confirmPassword: "" });
const banner = ref("");
const bannerError = ref(false);
const requiresTwoFactor = ref(false);
const settingsTab = ref<SettingsTab>("general");
const activeSection = ref("home");
// Ana sayfa "yeni sürüm" bandı için panel güncelleme durumu (yalnızca admin doldurur).
const panelUpdate = ref<UpdateStatus | null>(null);
// Denetim yapılamadıysa bunu SÖYLERİZ; sessizlik "güncel" gibi okunuyordu.
const panelUpdateLoad = useSectionError();
const ftpUserCount = ref(0);
// "Dikkat" toplayıcısı (menü rozetleri + üst bar çekmecesi). Yalnız admin doldurur (uç admin-only).
const attentionSummary = ref<AttentionSummary | null>(null);
const attentionLoading = ref(false);
const attentionError = ref("");
const attentionOpen = ref(false);
let refreshHandle: number | null = null;
let attentionHandle: number | null = null;

const setupForm = reactive({
  userName: "admin",
  email: "",
  password: "",
  confirmPassword: "",
  secureEntryPath: "panel",
  preferredLanguage: "tr",
  preferredHttpsPort: 8888,
  installRecommendedComponents: true
});

const loginForm = reactive({
  userNameOrEmail: "admin",
  password: "",
  twoFactorCode: "",
  rememberMe: true
});

const securityForm = reactive({
  secureEntryPath: "",
  allowedIpAddressesText: ""
});

const panelPreferences = reactive<PanelPreferences>({
  panelName: "AdaPanel",
  defaultLanguage: "tr",
  themeMode: "light",
  uiDensity: "compact",
  defaultSiteRoot: "C:\\inetpub\\wwwroot",
  defaultBackupRoot: "%ProgramData%\\AdaPanel\\backups",
  serverAddress: "",
  preferredHttpsPort: 8888,
  allowHttpFallback: true,
  loginLockoutMinutes: 15,
  defaultRuntimeProfile: "auto",
  installRuntimeIfMissing: true,
  restartRequired: false
});
// Tercihler sunucudan gelmeden yukarıdaki varsayılanlar gerçek ayar sanılıyordu (anahtarlar
// yanlış konumda görünüp yanlışlıkla üzerine kaydedilebiliyordu).
const panelPreferencesLoaded = ref(false);

/*
 * ÖZEL MARKA (white-label).
 *
 * İKİ AYRI DURUM, bilerek:
 *  - `branding`     : oturum açanın GÖRDÜĞÜ marka (giriş ekranı + panel kabuğu). Bayinin
 *                     müşterisi burada BAYİNİN markasını görür.
 *  - `brandingForm` : düzenlenen KENDİ kaydı (yöneticide sunucu, bayide bayinin kendisi).
 * İkisini tek nesnede tutmak, bayinin kendi formunu düzenlerken müşterisinin gördüğü değeri
 * değiştirdiğini sanmasına ya da tam tersine yol açardı.
 */
const branding = ref<Branding>(emptyBranding());
const brandingForm = ref<Branding>(emptyBranding());
const brandingLoaded = ref(false);

const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const isCustomer = computed(() => authStore.session?.user?.role === "Customer");
const isReseller = computed(() => authStore.session?.user?.role === "Reseller");
// Abonelik altı ek kullanıcı (Plesk "additional user"): menü izin listesine göre daraltılır,
// "Kullanıcılar" bölümü gizlenir. Asıl dayatma backend'de (RequireSubUserSection).
const isSubUser = computed(() => authStore.session?.isSubUser === true);
const subUserAllowedSections = computed(() => authStore.session?.allowedSections ?? null);

const installedSoftwareCount = computed(() => softwareStore.catalog?.installed.length ?? 0);
const totalBindingCount = computed(() => sitesStore.sites.reduce((sum, site) => sum + site.bindings.length, 0));
const databaseCount = computed(() => Object.values(databasesStore.workspaces)
  .reduce((sum, workspace) => sum + workspace.databases.length, 0));
const currentSecurePath = computed(() => securityForm.secureEntryPath || readSecureEntryPath() || setupForm.secureEntryPath);
const showDefaultPortWarning = computed(() => setupForm.preferredHttpsPort === 8888);
const securityStatusLabel = computed(() => {
  const twoFactorEnabled = authStore.session?.user?.isTwoFactorEnabled ?? false;
  const hasIpRestrictions = authStore.securitySettings?.hasIpRestrictions ?? securityForm.allowedIpAddressesText.trim().length > 0;
  return twoFactorEnabled && hasIpRestrictions
    ? t("panel.status.enabled")
    : (twoFactorEnabled || hasIpRestrictions ? t("panel.status.active") : t("panel.status.disabled"));
});

const heading = computed(() => mode.value === "setup" ? t("panel.setup.title") : t("panel.auth.title"));
const subtitle = computed(() => mode.value === "setup" ? t("panel.setup.subtitle") : t("panel.auth.subtitle"));

// Backend "dns" / "health" / "updates" gibi mantıksal bölümler döndürür; bunların üst menüde
// birebir karşılığı yok (DNS site araçlarında, servis sağlığı Sunucu Araçları'nda, güncellemeler
// Ayarlar > Güncellemeler sekmesinde). Rozet ve gezinme aynı eşlemeyi kullanır.
const ATTENTION_SECTION_ALIASES: Record<string, string> = {
  dns: "sites",
  health: "tools",
  updates: "settings"
};

// Bölüm başına dikkat sayısı (takma adlar katlanmış hâlde).
const attentionCounts = computed<Record<string, number>>(() => {
  const counts: Record<string, number> = {};
  for (const [section, count] of Object.entries(attentionSummary.value?.sectionCounts ?? {})) {
    const target = ATTENTION_SECTION_ALIASES[section] ?? section;
    counts[target] = (counts[target] ?? 0) + count;
  }

  return counts;
});

function attentionBadge(sectionId: string): string | undefined {
  const count = attentionCounts.value[sectionId] ?? 0;
  return count > 0 ? String(count) : undefined;
}

/**
 * Bir bölüme ait dikkat maddeleri (takma adlar katlanmış hâlde).
 *
 * Rozet şimdiye kadar yalnız SectionCounts okuyordu, yani öğelerin kendisi arayüze hiç
 * ulaşmıyordu; kullanıcı "3" görüyor ama neyin eksik olduğunu ancak tıklayıp arayarak
 * öğrenebiliyordu.
 */
function attentionItemsFor(sectionId: string) {
  return (attentionSummary.value?.items ?? []).filter(
    (item) => (ATTENTION_SECTION_ALIASES[item.section] ?? item.section) === sectionId
  );
}

/** Rozetin üzerine gelince görünecek metin: maddeleri ADIYLA sayar. */
function attentionBadgeTitle(sectionId: string): string | undefined {
  const items = attentionItemsFor(sectionId);
  if (items.length === 0) {
    return undefined;
  }

  // En fazla üç ad; gerisi "ve N tane daha" olarak özetlenir ki ipucu okunabilir kalsin.
  const names = items.slice(0, 3).map((item) => t(item.titleKey));
  const kalan = items.length - names.length;

  return kalan > 0
    ? `${names.join(" · ")} — ${t("panel.attention.badgeMore", { count: kalan })}`
    : names.join(" · ");
}

/**
 * Kiracı menüsüne "Yedekler" bölümünü ekler (Dosyalar'dan hemen sonra, yoksa sondan bir önceye).
 *
 * TEK KAYNAK: müşteri ve bayi aynı ekranı görür (`BackupController` her ikisine de açık, kapsamı
 * kendi sitelerine kilitli). İki yere elle kopyalandığında bayi dalı unutulmuştu.
 */
function injectBackupSection(list: SectionItem[]) {
  const filesIndex = list.findIndex((section) => section.id === "files");
  list.splice(filesIndex >= 0 ? filesIndex + 1 : Math.max(list.length - 1, 0), 0, {
    id: "backup",
    label: t("panel.navigation.backup"),
    description: t("panel.backup.subtitle"),
    glyph: "YD"
  });
}

const dashboardSections = computed<SectionItem[]>(() => {
  const sections: SectionItem[] = [
    // panel sırası: Web Siteleri → Posta → Dosyalar → Veritabanları → İstatistikler →
    // Uygulamalar/Mağaza → Zamanlanmış Görevler → Güvenlik → Araçlar/Ayarlar.
    { id: "home", label: t("panel.navigation.home"), description: t("panel.dashboard.subtitle"), glyph: "HM" },
    { id: "sites", label: t("panel.navigation.sites"), description: t("panel.sites.workspaceSubtitle"), glyph: "WS", badge: sitesStore.sites.length ? String(sitesStore.sites.length) : undefined },
    // Plesk gibi üst-düzey "Müşteriler & Bayiler" girişi (admin) / "Müşteriler" (bayi):
    // hesap yönetimi ekranı (UsersView) — kullanıcılar, planlar, kotalar, alt kullanıcılar.
    { id: "accounts", label: isReseller.value ? t("panel.navigation.customers") : t("panel.navigation.accounts"), description: t("panel.navigation.accountsDesc"), glyph: "MU" },
    { id: "sitebuilder", label: t("panel.navigation.siteBuilder"), description: t("panel.siteBuilder.subtitle"), glyph: "SK" },
    { id: "mail", label: t("panel.navigation.mail"), description: t("panel.mail.subtitle"), glyph: "ML" },
    { id: "files", label: t("panel.navigation.files"), description: t("panel.files.subtitle"), glyph: "FL" },
    { id: "database", label: t("panel.navigation.database"), description: t("panel.database.subtitle"), glyph: "DB" },
    { id: "monitor", label: t("panel.navigation.monitor"), description: t("panel.monitor.subtitle"), glyph: "MN", badgeTitle: attentionBadgeTitle("monitor"), badge: attentionBadge("monitor") },
    // Danışman rozeti = bekleyen dikkat maddesi sayısı; Mağaza rozeti = bekleyen güncelleme
    // sayısı (yoksa eskisi gibi kurulu bileşen sayısı).
    { id: "advisor", label: t("panel.navigation.advisor"), description: t("panel.advisor.subtitle"), glyph: "AD", badgeTitle: attentionBadgeTitle("advisor"), badge: attentionBadge("advisor") },
    { id: "software", label: t("panel.navigation.store"), description: t("panel.software.subtitle"), glyph: "ST", badgeTitle: attentionBadgeTitle("software"), badge: attentionBadge("software") ?? (installedSoftwareCount.value ? String(installedSoftwareCount.value) : undefined) },
    { id: "tasks", label: t("panel.navigation.tasks"), description: t("panel.tasks.subtitle"), glyph: "TK" },
    { id: "security", label: t("panel.navigation.security"), description: t("panel.security.subtitle"), glyph: "SC", badgeTitle: attentionBadgeTitle("security"), badge: attentionBadge("security") },
    { id: "audit", label: t("panel.navigation.audit"), description: t("panel.audit.subtitleAdmin"), glyph: "DK" },
    { id: "billing", label: t("panel.navigation.billing"), description: t("panel.billing.subtitle"), glyph: "FT" },
    { id: "tools", label: t("panel.navigation.serverTools"), description: t("panel.serverTools.subtitle"), glyph: "AR", badgeTitle: attentionBadgeTitle("tools"), badge: attentionBadge("tools") },
    { id: "migration", label: t("panel.navigation.migration"), description: t("panel.migration.subtitle"), glyph: "MG" },
    // "Hesabım": oturum açan kullanıcının kendi profili (2FA, kurtarma kodları) — her rolde açık.
    // Yapay zeka baglantisi HER role acik: anahtarin yetkisi sahibinin rolunden turer, yani
    // musteri anahtari musterinin gordugu kadarini gorur. (Plesk yalniz yoneticiye izin veriyor.)
    { id: "ai", label: t("panel.navigation.aiConnection"), description: t("panel.aiConnection.subtitle"), glyph: "YZ" },
    { id: "account", label: t("panel.settings.tab.account"), description: t("panel.account.title"), glyph: "HS" },
    { id: "settings", label: t("panel.navigation.settings"), description: t("panel.settings.subtitle"), glyph: "AY", badgeTitle: attentionBadgeTitle("settings"), badge: attentionBadge("settings") },
    { id: "exit", label: t("panel.navigation.exit"), description: t("panel.auth.logout"), glyph: "EX" }
  ];

  if (isReseller.value) {
    // Bayi: kendi müşterilerini yönetir ("Müşteriler" ekranı) + müşterilerinin tüm site
    // kaynakları (Web Siteleri hub'ı: PHP/SSL/DNS/FTP/Proxy/WP/Stats/Backup/Hosting — hepsi
    // sahip-kapsamına göre EnsureSiteAsync ile filtreli; server-level araçlar hub'da gizli).
    // "Ayarlar" bayide artık GÖRÜNÜR ama tek sekmesi MARKA'dır: bayinin kendi müşterilerine
    // gösterdiği logo/ad/renk buradan yönetilir (Plesk'te de bayi kendi markasını kendisi kurar).
    // Sunucu ayarları — güvenlik, bildirim, güncelleme, lisans — hem sekme hem uç düzeyinde
    // yöneticiye kilitli kalır.
    const resellerSections = sections.filter((section) =>
      section.id === "home" ||
      section.id === "sites" ||
      section.id === "sitebuilder" ||
      section.id === "accounts" ||
      section.id === "mail" ||
      section.id === "files" ||
      section.id === "database" ||
      section.id === "tasks" ||
      section.id === "audit" ||
      section.id === "ai" ||
      section.id === "account" ||
      section.id === "settings" ||
      section.id === "exit");

    // Yedek bölümü bayide de olmalı: BackupController `Reseller` rolüne izin veriyor ama menüde
    // yoktu, yani bayi kendi müşterisinin yedeğini panelden hiç alamıyordu.
    injectBackupSection(resellerSections);

    if (!resellerSections.some((section) => section.id === activeSection.value)) {
      activeSection.value = "home";
    }

    return resellerSections;
  }

  if (isCustomer.value) {
    // Müşteri KENDİ sitelerini + veritabanlarını yönetir (site/DB oluşturma kotaya tabi, kapsamı kendi sitelerine kilitli).
    let customerSections = sections.filter((section) =>
      section.id === "home" ||
      section.id === "sites" ||
      section.id === "sitebuilder" ||
      section.id === "mail" ||
      section.id === "database" ||
      section.id === "files" ||
      section.id === "tasks" ||
      section.id === "audit" ||
      section.id === "ai" ||
      section.id === "account" ||
      section.id === "exit");
    // Kiracıya özel yedek bölümü (kendi kapsamındaki sitelerin yedekleri) — Dosyalar'dan sonra.
    injectBackupSection(customerSections);

    if (isSubUser.value) {
      // Alt-kullanıcı: izin listesi doluysa menü ona göre daraltılır (home/account/exit hep açık).
      const allowed = subUserAllowedSections.value;
      if (allowed) {
        const allowedSet = new Set(allowed.map((section) => section.toLowerCase()));
        customerSections = customerSections.filter((section) =>
          section.id === "home" || section.id === "ai" || section.id === "account" || section.id === "exit" || allowedSet.has(section.id));
      }
    } else {
      // Top-level müşteri: aboneliği altındaki ek kullanıcıları yönetir (Plesk "Users" sekmesi) — Hesabım'dan önce.
      const accountIndex = customerSections.findIndex((section) => section.id === "account");
      customerSections.splice(accountIndex >= 0 ? accountIndex : customerSections.length - 1, 0, {
        id: "subusers",
        label: t("panel.navigation.subUsers"),
        description: t("panel.subUsers.subtitle"),
        glyph: "EK"
      });
    }

    if (!customerSections.some((section) => section.id === activeSection.value)) {
      activeSection.value = "home";
    }

    return customerSections;
  }

  if (!isAdmin.value) {
    // Operator vb.: panel ayarları kapalı; kendi hesabı (2FA) açık.
    return sections.filter((section) =>
      section.id === "home" ||
      section.id === "monitor" ||
      section.id === "ai" ||
      section.id === "account" ||
      section.id === "exit");
  }

  // Tam panel: domaine özel araçlar (SSL/DNS/FTP/PHP/Dosyalar) üst menüde DEĞİL, yalnızca
  // Web Siteleri > domain > araçlar altında. "Dosyalar" admin üst menüsünden çıkarılır
  // (hub'da "Dosyalar" aracı var). Müşteride üst menüde kalır (onların site hub'ı yok).
  const adminSections = sections.filter((section) => section.id !== "files");

  if (!adminSections.some((section) => section.id === activeSection.value)) {
    activeSection.value = "home";
  }

  return adminSections;
});

const activeSectionMeta = computed(() =>
  dashboardSections.value.find((section) => section.id === activeSection.value) ?? dashboardSections.value[0]);

onMounted(async () => {
  // Marka İLK İŞ olarak istenir ve beklenmez: giriş ekranı marka gelmeden de kullanılabilir
  // olmalı, ama marka geldiğinde (çoğu zaman form doldurulmadan önce) yerine oturur.
  void loadBranding();
  await initializeView();
  await loadPanelUpdate();
});

onBeforeUnmount(() => {
  stopRefreshLoop();
});

async function initializeView() {
  try {
    const status = await setupStore.fetchStatus();
    setupForm.secureEntryPath = status.secureEntryPath ? status.secureEntryPath.replace("/", "") : "panel";
    setupForm.preferredLanguage = status.defaultLanguage.startsWith("en") ? "en" : "tr";
    i18n.locale.value = setupForm.preferredLanguage;
    setupForm.preferredHttpsPort = status.preferredHttpsPort;

    if (!status.isInitialized) {
      mode.value = "setup";
      return;
    }

    // Admin oluşturulmuş ama önerilen yazılım kurulumu HÂLÂ arka planda sürüyorsa, kullanıcıyı
    // login'e geçirme (kurulum bitmeden giriş yapmasın). Sayfa yenilense bile canlı ilerlemeyi
    // göster; kurulum bitince sayfayı yenileyip girişe düş.
    try {
      const progress = await setupStore.fetchProgress();
      if (progress.running && progress.title === "setup.installing_recommended") {
        mode.value = "setup";
        installing.value = true;
        installProgress.value = progress;
        void pollInstallProgress();
        return;
      }
    } catch {
      // İlerleme okunamadıysa normal akışa devam et.
    }

    mode.value = "auth";

    // Parola sıfırlama bağlantısıyla gelindiyse reset ekranını aç.
    const resetToken = new URLSearchParams(window.location.search).get("resetToken");
    if (resetToken) {
      resetForm.token = resetToken;
      authView.value = "reset";
      return;
    }

    await restoreSession();
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function submitForgotPassword() {
  try {
    await authStore.forgotPassword(forgotForm.email);
    forgotForm.email = "";
    authView.value = "login";
    // Numaralandırmayı engellemek için her zaman aynı bilgilendirme.
    showBanner(t("panel.auth.forgotSent"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function submitResetPassword() {
  if (resetForm.newPassword !== resetForm.confirmPassword) {
    showBanner(t("panel.auth.passwordMismatch"), true);
    return;
  }

  try {
    await authStore.resetPassword(resetForm.token, resetForm.newPassword);
    resetForm.token = "";
    resetForm.newPassword = "";
    resetForm.confirmPassword = "";
    authView.value = "login";
    // URL'deki token'ı temizle.
    window.history.replaceState({}, "", window.location.pathname);
    showBanner(t("panel.auth.resetSuccess"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function restoreSession() {
  const session = await authStore.fetchSession();

  if (!session.isAuthenticated) {
    stopRefreshLoop();
    mode.value = "auth";
    return;
  }

  mode.value = "dashboard";
  activeSection.value = "home";
  await loadDashboardData();
  startRefreshLoop();
}

async function submitSetup() {
  try {
    const result = await setupStore.initialize({ ...setupForm });
    setupSecurePath.value = result.secureEntryPath;

    // Önerilen yazılım yığını arka planda kuruluyorsa: ekranda donmuş gibi durmasın diye
    // adım adım canlı ilerlemeyi göster; bitince güvenli giriş yoluna geç.
    if (result.installationInProgress) {
      installing.value = true;
      await pollInstallProgress();
      return;
    }

    showBanner(`${t("panel.setup.success")} ${result.secureEntryPath}`, false);
    window.location.assign(result.secureEntryPath);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function pollInstallProgress() {
  while (true) {
    try {
      installProgress.value = await setupStore.fetchProgress();
    } catch {
      // Geçici hata (yeniden başlatma vb.); kısa bekleyip yeniden dene.
    }

    if (installProgress.value && (installProgress.value.completed || installProgress.value.failed)) {
      break;
    }

    await new Promise((resolve) => setTimeout(resolve, 1500));
  }

  // Kısa bir "tamamlandı" anı gösterip girişe geç. Submit akışında güvenli giriş yolu
  // elimizde (assign); sayfa yenilenip gelindiyse güvenli yol gizli olduğundan sayfayı
  // yeniden yükle (durum "kurulu" + ilerleme bittiği için login ekranı açılır).
  await new Promise((resolve) => setTimeout(resolve, 1200));
  if (setupSecurePath.value) {
    window.location.assign(setupSecurePath.value);
  } else {
    window.location.reload();
  }
}

function setupStepLabel(name: string): string {
  // İsim bir i18n anahtarıysa çevir; değilse (ürün adı: "MySQL 8" gibi) olduğu gibi göster.
  return i18n.te(name) ? t(name) : name;
}

async function submitLogin() {
  try {
    const result = await authStore.login({ ...loginForm, twoFactorCode: loginForm.twoFactorCode || undefined });
    if (result.requiresTwoFactor) {
      requiresTwoFactor.value = true;
      showBanner(t("panel.auth.twoFactorRequired"), false);
      return;
    }

    requiresTwoFactor.value = false;
    loginForm.password = "";
    loginForm.twoFactorCode = "";
    mode.value = "dashboard";
    activeSection.value = "home";
    // Giriş yapan kişi bir bayinin müşterisiyse artık O BAYİNİN markasını görmeli; giriş
    // ekranındaki sunucu markası panelin içine taşınmamalı.
    void loadBranding();
    // "Giriş başarılı" yalnız ana sayfada bir kez görünüp ~4 sn sonra kaybolur.
    showBanner(t("panel.auth.loginSuccess"), false, 4000);
    await loadDashboardData();
    startRefreshLoop();
    // Girişten hemen sonra da "yeni sürüm" bandı görünsün (mount'taki kontrol login öncesiydi).
    void loadPanelUpdate();
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function logout() {
  try {
    await authStore.logout();
    stopRefreshLoop();
    mode.value = "auth";
    activeSection.value = "home";
    attentionOpen.value = false;
    attentionSummary.value = null;
    requiresTwoFactor.value = false;
    // Salt okunur bayrağı bir sonraki kullanıcıya sızmamalı.
    subscriptionStore.reset();
    // MARKA DA SIZMAMALI: çıkan kullanıcı bir bayinin müşterisiyse, o bayinin logosu/adı bir
    // sonraki kişinin giriş ekranında asılı kalırdı. Yeniden çözülünce sunucu markasına döner.
    brandingLoaded.value = false;
    void loadBranding();
    showBanner(t("panel.auth.logoutSuccess"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function loadDashboardData() {
  /*
   * ASKI DURUMU HER ŞEYDEN ÖNCE ölçülür: salt okunur kapısı (api/client.ts) bu çağrıda
   * kuruluyor. Yönetici askıya takılmadığı için sorulmaz, bayrak da temizlenir.
   */
  if (isCustomer.value || isReseller.value) {
    await subscriptionStore.fetchMine();
  } else {
    subscriptionStore.reset();
  }

  await systemStore.fetchOverview();

  if (isCustomer.value) {
    // Müşteri için site listesi (kapsam ucu yalnızca atanmış siteleri döner).
    // Alt-kullanıcıda "sites" izni yoksa uç 403 döner (allSettled yutar) ve otomatik geçiş yapılmaz.
    const sitesAllowed = !isSubUser.value || !subUserAllowedSections.value ||
      subUserAllowedSections.value.some((section) => section.toLowerCase() === "sites");
    if (sitesAllowed) {
      await Promise.allSettled([sitesStore.fetchSites()]);
      if (activeSection.value === "home" && sitesStore.sites.length > 0) {
        activeSection.value = "sites";
      }
    }
    return;
  }

  if (isReseller.value) {
    // Bayi: kendi müşterileri (UsersController sahip-filtreli) + müşterilerinin siteleri
    // (Müşteriler sekmesi ve Posta/Dosyalar için gerekli).
    await Promise.allSettled([authStore.fetchUsers(), sitesStore.fetchSites()]);
    return;
  }

  if (!isAdmin.value) {
    return;
  }

  const [settings] = await Promise.all([
    authStore.fetchSecuritySettings(),
    authStore.fetchUsers(),
    authStore.fetchAuditLogs(),
    softwareStore.fetchCatalog(),
    getPanelPreferences().then((preferences) => {
      Object.assign(panelPreferences, preferences);
      panelPreferencesLoaded.value = true;
      i18n.locale.value = preferences.defaultLanguage;
      applyAppearance(preferences);
    })
  ]);

  await Promise.allSettled([sitesStore.fetchSites(), phpStore.fetchVersions()]);
  const installedComponentIds = new Set((softwareStore.catalog?.installed ?? []).map((component) => component.componentId));
  const databasePrefetches: Promise<unknown>[] = [
    getFtpUsers().then((users) => {
      ftpUserCount.value = users.length;
    })
  ];

  if (installedComponentIds.has("mariadb-11")) {
    databasePrefetches.push(databasesStore.fetchWorkspace("mariadb"));
  }

  if (installedComponentIds.has("mysql-8")) {
    databasePrefetches.push(databasesStore.fetchWorkspace("mysql"));
  }

  if (installedComponentIds.has("postgresql-16")) {
    databasePrefetches.push(databasesStore.fetchWorkspace("postgresql"));
  }

  await Promise.allSettled(databasePrefetches);

  if (sitesStore.selectedSiteName) {
    await Promise.allSettled([
      appsStore.fetchConfiguration(sitesStore.selectedSiteName),
      proxyStore.fetchConfiguration(sitesStore.selectedSiteName)
    ]);
  }

  securityForm.secureEntryPath = settings.secureEntryPath.replace("/", "");
  securityForm.allowedIpAddressesText = settings.allowedIpAddresses.join("\n");
}

async function savePanelPreferences() {
  try {
    const updated = await updatePanelPreferences({ ...panelPreferences });
    Object.assign(panelPreferences, updated);
    i18n.locale.value = updated.defaultLanguage;
    applyAppearance(updated);
    setupForm.preferredHttpsPort = updated.preferredHttpsPort;
    showBanner(t("panel.settings.saved"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

function applyAppearance(preferences: PanelPreferences) {
  const resolvedTheme = preferences.themeMode === "system"
    ? (window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark")
    : preferences.themeMode;
  document.documentElement.dataset.theme = resolvedTheme;
  document.documentElement.dataset.density = preferences.uiDensity;
  resolvedThemeMode.value = resolvedTheme;
  // Oturum öncesi (login ekranı) ve sonraki açılışlarda temayı korumak için.
  try {
    localStorage.setItem("adapanel-theme", resolvedTheme);
  } catch {
    // localStorage kapalıysa sessizce geç
  }
}

/*
 * ÖZEL MARKA — yükleme ve uygulama.
 *
 * Marka, oturum durumu HER DEĞİŞTİĞİNDE yeniden çözülür (açılış, giriş, çıkış): kimin markasını
 * göreceğimiz kim olduğumuza bağlı. Çıkışta yenilenmezse, çıkan bayi müşterisinin markası bir
 * sonraki kişinin GİRİŞ ekranında asılı kalırdı.
 */
const brandName = computed(() => branding.value.panelName || panelPreferences.panelName || "AdaPanel");
const brandLogo = computed(() => branding.value.logoDataUrl);

async function loadBranding() {
  try {
    applyBranding(await getBranding());
  } catch {
    // Marka okunamazsa panel ürün görünümüyle çalışmaya DEVAM eder; giriş ekranını bir marka
    // isteği yüzünden kilitlemek, çalışan bir paneli görsel bir ayar uğruna erişilemez kılardı.
  }
}

function applyBranding(brand: Branding) {
  branding.value = brand;

  const documentTitle = brand.panelName || panelPreferences.panelName || "AdaPanel";
  document.title = documentTitle;

  const iconLink = document.querySelector<HTMLLinkElement>('link[rel~="icon"]');
  if (iconLink) {
    iconLink.href = brand.faviconDataUrl || "/favicon.svg";
  }

  // Vurgu rengi bir CSS bildirimine yazılıyor: sunucu doğrulasa da burada BİR KEZ DAHA süzülür.
  // Değer stil sayfasındaki `:root[data-brand-accent]` kuralını açar (bkz. styles/base.css).
  const root = document.documentElement;
  if (/^#[0-9a-fA-F]{6}$/.test(brand.accentColor)) {
    root.style.setProperty("--brand-accent", brand.accentColor);
    root.dataset.brandAccent = "";
  } else {
    root.style.removeProperty("--brand-accent");
    delete root.dataset.brandAccent;
  }
}

function openBrandingTab() {
  settingsTab.value = "branding";
  void loadBrandingSettings();
}

async function loadBrandingSettings() {
  try {
    brandingForm.value = await getBrandingSettings();
    brandingLoaded.value = true;
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function saveBranding() {
  try {
    const saved = await updateBrandingSettings({
      panelName: brandingForm.value.panelName,
      accentColor: brandingForm.value.accentColor,
      logoLinkUrl: brandingForm.value.logoLinkUrl
    });
    brandingForm.value = saved;
    // Kaydeden kişi kendi markasının kapsamındadır (yönetici→sunucu, bayi→kendisi), yani
    // değişiklik kendi ekranında da GÖRÜNMELİ; aksi halde "kaydettim ama bir şey olmadı" denir.
    await loadBranding();
    showBanner(t("panel.branding.saved"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function uploadBrandingAsset(kind: BrandingImageKind, event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) {
    return;
  }

  try {
    brandingForm.value = await uploadBrandingImage(kind, file);
    await loadBranding();
    showBanner(t("panel.branding.saved"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  } finally {
    // Seçim TEMİZLENİR: aynı dosya reddedildikten sonra düzeltilip yeniden seçilebilsin
    // (tarayıcı aynı dosya adı için `change` olayını tekrar tetiklemez).
    input.value = "";
  }
}

async function removeBrandingAsset(kind: BrandingImageKind) {
  try {
    brandingForm.value = await removeBrandingImage(kind);
    await loadBranding();
    showBanner(t("panel.branding.saved"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

function pickAccentColor(event: Event) {
  brandingForm.value.accentColor = (event.target as HTMLInputElement).value;
}

const resolvedThemeMode = ref(document.documentElement.dataset.theme === "light" ? "light" : "dark");

// Üst bardaki tema düğmesi: anında uygular, tercihi kalıcılaştırır.
async function toggleTheme() {
  const next = resolvedThemeMode.value === "light" ? "dark" : "light";
  panelPreferences.themeMode = next;
  applyAppearance(panelPreferences);
  if (isAdmin.value) {
    try {
      await updatePanelPreferences({ ...panelPreferences });
    } catch {
      // kaydedilemese bile görsel tercih localStorage'da kalır
    }
  }
}

async function refreshDashboard() {
  try {
    await loadDashboardData();
    showBanner(t("panel.dashboard.refreshed"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

/*
 * ÜST BARDAKİ ARAMADAN GELEN VARLIK SEÇİMİ.
 *
 * Arama artık site/alan adı da buluyor; sonuç tıklanınca kullanıcıyı "Web Siteleri" listesine
 * bırakmak yetmez — aradığı sitenin panosunda olmalı. `seq` sayacı ŞART: aynı siteyi arka arkaya
 * seçmek prop değerini değiştirmediği için izleyici tetiklenmez ve ikinci tıklama ölü kalırdı.
 */
const siteOpenRequest = ref<{ siteName: string; seq: number } | null>(null);
const mailDomainFromSearch = ref("");
let siteOpenSeq = 0;

function openSiteFromSearch(siteName: string) {
  sitesStore.selectSite(siteName);
  siteOpenSeq += 1;
  siteOpenRequest.value = { siteName, seq: siteOpenSeq };
  activeSection.value = "sites";
  window.scrollTo({ top: 0, behavior: "auto" });
}

function openMailDomainFromSearch(domain: string) {
  // MailView seçili alan adını YALNIZ kurulum anında okur (`selectedDomain` ref'i props.domain
  // ile başlatılır). Bu yüzden `:key` ile yeniden kurulur: zaten Posta bölümündeyken başka bir
  // alan adı aramak aksi halde hiçbir şey yapmazdı.
  mailDomainFromSearch.value = domain;
  activeSection.value = "mail";
  window.scrollTo({ top: 0, behavior: "auto" });
}

function handleNavigation(sectionId: string) {
  if (sectionId === "exit") {
    void logout();
    return;
  }

  // Ana sayfadaki özet kartı → dikkat çekmecesini aç (ayrı bir ekrana gitmeden).
  if (sectionId === "attention") {
    openAttention();
    return;
  }

  // Ana sayfadaki "Yeni sürüm" bandı → Ayarlar > Güncellemeler sekmesi.
  if (sectionId === "updates") {
    activeSection.value = "settings";
    settingsTab.value = "updates";
    window.scrollTo({ top: 0, behavior: "auto" });
    return;
  }

  // Dikkat maddelerinin mantıksal bölümleri ("dns" → Web Siteleri, "health" → Sunucu Araçları).
  activeSection.value = ATTENTION_SECTION_ALIASES[sectionId] ?? sectionId;
  // Bayide "Ayarlar"ın tek sekmesi MARKA'dır; varsayılan sekme ("general") yönetici içindir ve
  // bayide boş bir ekran gösterirdi.
  if (activeSection.value === "settings" && !isAdmin.value) {
    openBrandingTab();
  }
  // Menüden Posta'ya gitmek her zaman alan adı LİSTESİNDEN başlar: aramayla açılmış eski bir
  // alan adı burada seçili kalsaydı kullanıcı yanlış alan adında işlem yapabilirdi.
  if (activeSection.value === "mail") {
    mailDomainFromSearch.value = "";
  }
  window.scrollTo({ top: 0, behavior: "auto" });
}

// Dikkat çekmecesi: açılışta taze veri çeker (rozet 60 sn'de bir tazelenir, açılınca beklenmez).
function openAttention() {
  attentionOpen.value = true;
  void loadAttention(false);
}

function toggleAttention() {
  if (attentionOpen.value) {
    attentionOpen.value = false;
    return;
  }

  openAttention();
}

/**
 * Dikkat özetini çeker. `silent` (arka plan turu) hata gösterme; kullanıcı tetiklediyse
 * çekmecede hata satırı görünür. Uç admin-only olduğundan diğer roller için hiç çağrılmaz.
 */
async function loadAttention(silent = true) {
  if (!isAdmin.value || mode.value !== "dashboard") {
    return;
  }

  if (!silent) {
    attentionLoading.value = true;
    attentionError.value = "";
  }

  try {
    attentionSummary.value = await getAttentionSummary();
    attentionError.value = "";
  } catch (error) {
    if (!silent) {
      attentionError.value = resolveApiMessage(error);
    }
  } finally {
    attentionLoading.value = false;
  }
}

// Ana sayfa banner'ı için panel güncelleme durumunu bir kez çek (admin; GitHub Releases'e bakar, hata olursa sessiz).
async function loadPanelUpdate() {
  if (!isAdmin.value) {
    return;
  }

  try {
    panelUpdate.value = await checkForUpdate();
    panelUpdateLoad.clear();
  } catch (error) {
    panelUpdate.value = null;
    panelUpdateLoad.capture(error);
  }
}

async function saveSecuritySettings() {
  try {
    const allowedIpAddresses = securityForm.allowedIpAddressesText.split(/[\n,]/g).map((item) => item.trim()).filter(Boolean);
    const previousPath = readSecureEntryPath();
    const updated = await authStore.updateSecuritySettings({
      secureEntryPath: securityForm.secureEntryPath,
      allowedIpAddresses,
      hasIpRestrictions: allowedIpAddresses.length > 0
    });

    showBanner(t("panel.security.saved"), false);
    const nextPath = updated.secureEntryPath.replace("/", "");
    if (nextPath && nextPath !== previousPath) {
      window.location.assign(updated.secureEntryPath);
    }
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

function startRefreshLoop() {
  stopRefreshLoop();
  refreshHandle = window.setInterval(() => {
    if (mode.value === "dashboard") {
      void systemStore.fetchOverview();
    }
  }, 15000);

  // Dikkat toplayıcısı AĞIR (servis/DNS/güncelleme taraması) → 60 sn'de bir, sekme
  // arka plandayken hiç değil.
  attentionHandle = window.setInterval(() => {
    if (mode.value === "dashboard" && !document.hidden) {
      void loadAttention(true);
    }
  }, 60000);

  // Panele girer girmez rozet dolsun (kullanıcı hiçbir sekmeye gitmeden sorunu görsün).
  void loadAttention(true);
}

function stopRefreshLoop() {
  if (refreshHandle !== null) {
    window.clearInterval(refreshHandle);
    refreshHandle = null;
  }

  if (attentionHandle !== null) {
    window.clearInterval(attentionHandle);
    attentionHandle = null;
  }
}

let bannerDismissTimer: ReturnType<typeof setTimeout> | null = null;

function showBanner(message: string, isError: boolean, autoDismissMs = 0) {
  if (bannerDismissTimer) {
    clearTimeout(bannerDismissTimer);
    bannerDismissTimer = null;
  }

  banner.value = message;
  bannerError.value = isError;

  // Geçici bilgi mesajları (örn. "giriş başarılı") bir kez gösterilip kaybolur;
  // her sayfada asılı kalmaz.
  if (autoDismissMs > 0) {
    bannerDismissTimer = setTimeout(() => {
      banner.value = "";
      bannerDismissTimer = null;
    }, autoDismissMs);
  }
}

function resolveApiMessage(error: unknown): string {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function formatUtc(value: string | null) {
  return value ? new Date(value).toLocaleString() : t("panel.common.notAvailable");
}
</script>

<style scoped>
.setup-progress {
  display: grid;
  gap: 10px;
}

.setup-progress h3 {
  margin: 0;
}

.progress-steps {
  list-style: none;
  margin: 6px 0 0;
  padding: 0;
  display: grid;
  gap: 8px;
  max-height: 320px;
  overflow-y: auto;
}

.progress-step {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--input-bg);
  font-size: 0.92rem;
}

.progress-step.done {
  color: var(--accent-text, #2e7d32);
}

.progress-step.failed {
  color: var(--danger-text, #c0392b);
}

.step-icon {
  display: inline-grid;
  place-items: center;
  width: 18px;
  height: 18px;
  font-weight: 700;
}

.step-spinner {
  width: 13px;
  height: 13px;
  border: 2px solid color-mix(in srgb, var(--accent-1, #2e7d32) 40%, transparent);
  border-top-color: var(--accent-1, #2e7d32);
  border-radius: 50%;
  animation: setup-spin 0.7s linear infinite;
}

@keyframes setup-spin {
  to { transform: rotate(360deg); }
}

.setup-shell {
  display: grid;
  place-items: center;
  min-height: 100vh;
  padding: 20px;
}

.auth-card {
  width: min(720px, 100%);
  display: grid;
  gap: 20px;
  padding: 28px;
  border-radius: 28px;
  border: 1px solid var(--panel-border);
  background: linear-gradient(180deg, var(--surface-1), var(--surface-0));
  box-shadow: var(--panel-shadow);
}

.auth-copy {
  display: grid;
  gap: 10px;
}

.auth-copy h1 {
  margin: 0;
  font-size: clamp(1.65rem, 4vw, 2.2rem);
}

.lead {
  margin: 0;
  color: var(--text-muted);
  line-height: 1.7;
}

.status-line {
  width: 64px;
  height: 6px;
  border-radius: 999px;
  background: linear-gradient(135deg, var(--accent-1), color-mix(in srgb, var(--accent-1) 20%, transparent));
}

.form-grid,
.stack,
.subgrid,
.banner-stack,
.audit-list {
  display: grid;
  gap: 16px;
}

.warn-banner {
  border-color: color-mix(in srgb, var(--warn-1) 20%, transparent);
  background: color-mix(in srgb, var(--warn-1) 12%, transparent);
  color: var(--warn-text);
}

/* Askı şeridi: başlık + sebep iki satır. `.notice.error` renklendirmeyi zaten veriyor. */
.suspended-banner {
  display: grid;
  gap: 4px;
}

.audit-item {
  border: 1px solid var(--panel-border);
  background: var(--surface-1);
}

.settings-workspace {
  align-content: start;
}

.settings-tabs {
  max-width: 100%;
}

.settings-tabs button {
  width: auto;
}

.settings-account {
  display: grid;
  gap: 1px;
  min-width: 180px;
  text-align: right;
}

.settings-account span {
  color: var(--text-muted);
  font-size: 0.8rem;
}

.settings-section {
  display: grid;
  gap: 12px;
}

.section-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.settings-check {
  align-self: end;
  min-height: 39px;
}

.row-actions button {
  width: auto;
}

.audit-item {
  padding: 16px;
  border-radius: var(--radius-md);
}

.audit-item p,
.audit-item small {
  margin: 0;
}

.audit-top {
  display: flex;
  justify-content: space-between;
  gap: 12px;
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
  gap: 8px;
}

.checkbox {
  grid-template-columns: auto 1fr;
  align-items: center;
}

.checkbox input {
  width: 18px;
  height: 18px;
  margin: 0;
}

.checkbox span {
  margin-left: 8px;
}

input,
select,
textarea,
button {
  width: 100%;
}

/* --- ÖZEL MARKA (white-label) --- */

/* Giriş ekranının marka başlığı. Logo yoksa yalnız ad görünür; ikisi de yoksa blok hiç basılmaz. */
.auth-brand {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

/* Yüklenen görselin ölçüsü kartı BOZAMAZ: yükseklik sabit, genişlik oranla ve üst sınırlı. */
.auth-logo {
  height: 44px;
  width: auto;
  max-width: 220px;
  object-fit: contain;
}

.auth-brand-name {
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--text-strong);
}

.accent-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.accent-swatch {
  width: 44px;
  min-width: 44px;
  height: 34px;
  padding: 2px;
  cursor: pointer;
}

.brand-assets {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 12px;
}

.brand-asset {
  display: grid;
  gap: 8px;
  align-content: start;
  padding: 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

/* Önizleme alanı SABİT yükseklikte: logo yüklendiğinde/kaldırıldığında form zıplamasın. */
.brand-asset-preview {
  display: grid;
  place-items: center;
  min-height: 72px;
  padding: 8px;
  border: 1px dashed var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.brand-asset-preview img {
  max-height: 56px;
  max-width: 100%;
  object-fit: contain;
}

.brand-asset button {
  width: auto;
  justify-self: start;
}

@media (max-width: 980px) {
  .settings-account {
    display: none;
  }
}

@media (max-width: 760px) {
  .audit-top {
    flex-direction: column;
    align-items: flex-start;
  }

  .settings-tabs {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    width: 100%;
    overflow: visible;
  }

  .settings-tabs button {
    width: 100%;
    white-space: normal;
  }
}
</style>
