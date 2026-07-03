<template>
  <main v-if="mode !== 'dashboard'" class="setup-shell">
    <section class="auth-card">
      <div class="auth-copy">
        <span class="status-line"></span>
        <p class="meta">{{ t("panel.layout.subtitle") }}</p>
        <h1>{{ heading }}</h1>
        <p class="lead">{{ subtitle }}</p>
      </div>

      <p v-if="banner" :class="['notice', { error: bannerError }]">
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
        <p v-if="installProgress?.completed" class="notice">{{ t("panel.setup.progress.redirecting") }}</p>
        <p v-if="installProgress?.failed" class="notice error">{{ installProgress?.error }}</p>
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
          <input v-model="setupForm.password" type="password" minlength="12" required />
        </label>
        <label>
          <span>{{ t("panel.form.confirmPassword") }}</span>
          <input v-model="setupForm.confirmPassword" type="password" minlength="12" required />
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
          <input v-model="loginForm.password" type="password" minlength="12" required />
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
          <input v-model="resetForm.newPassword" type="password" minlength="12" required />
        </label>
        <label>
          <span>{{ t("panel.auth.newPasswordConfirm") }}</span>
          <input v-model="resetForm.confirmPassword" type="password" minlength="12" required />
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
    :panel-name="panelPreferences.panelName"
    :machine-name="systemStore.overview?.machineName || t('panel.common.notAvailable')"
    :secure-entry-path="currentSecurePath"
    :user-name="authStore.session?.user?.userName || '-'"
    :user-email="authStore.session?.user?.email || '-'"
    :role="authStore.session?.user?.role || '-'"
    :panel-version="systemStore.overview?.panelVersion || '-'"
    :is-two-factor-enabled="authStore.session?.user?.isTwoFactorEnabled ?? false"
    :resolved-theme="resolvedThemeMode"
    @navigate="handleNavigation"
    @refresh="refreshDashboard"
    @toggle-theme="toggleTheme"
    @logout="logout">
    <template #banner>
      <div class="banner-stack">
        <p v-if="showDefaultPortWarning" class="notice warn-banner">
          {{ t("panel.dashboard.defaultPortWarning") }}
        </p>
        <p v-if="banner" :class="['notice', { error: bannerError }]">
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
      @navigate="handleNavigation" />

    <WebsiteWorkspaceView v-else-if="activeSection === 'sites' && (isAdmin || isReseller || isCustomer)" />

    <DatabasesView v-else-if="activeSection === 'database' && isAdmin" />

    <MyDatabasesView v-else-if="activeSection === 'database' && (isReseller || isCustomer)" />

    <MailView v-else-if="activeSection === 'mail' && (isAdmin || isCustomer || isReseller)" />

    <MonitoringView
      v-else-if="activeSection === 'monitor'"
      :overview="systemStore.overview"
      :loading="systemStore.loading" />

    <SecurityView v-else-if="activeSection === 'security' && isAdmin" />

    <FilesView v-else-if="activeSection === 'files' && (isAdmin || isCustomer || isReseller)" />

    <BackupView v-else-if="activeSection === 'backup' && isCustomer" />

    <CronView v-else-if="activeSection === 'tasks' && (isAdmin || isReseller || isCustomer)" />

    <ServerToolsView v-else-if="activeSection === 'tools' && isAdmin" />

    <MigrationView v-else-if="activeSection === 'migration' && isAdmin" />

    <SoftwareView v-else-if="activeSection === 'software' && isAdmin" />

    <section v-else-if="activeSection === 'settings'" class="panel module-shell settings-workspace">
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
          <button :class="{ active: settingsTab === 'account' }" @click="settingsTab = 'account'">
            {{ t("panel.settings.tab.account") }}
          </button>
          <button v-if="isAdmin" :class="{ active: settingsTab === 'security' }" @click="settingsTab = 'security'">
            {{ t("panel.settings.tab.security") }}
          </button>
          <button v-if="isAdmin || isReseller" :class="{ active: settingsTab === 'users' }" @click="settingsTab = 'users'">
            {{ isReseller ? t("panel.navigation.customers") : t("panel.settings.tab.users") }}
          </button>
          <button v-if="isAdmin" :class="{ active: settingsTab === 'notifications' }" @click="settingsTab = 'notifications'">
            {{ t("panel.settings.tab.notifications") }}
          </button>
          <button v-if="isAdmin" :class="{ active: settingsTab === 'updates' }" @click="settingsTab = 'updates'">
            {{ t("panel.settings.tab.updates") }}
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
      </section>

      <section v-else-if="settingsTab === 'account'" class="form-surface settings-section">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.account.title") }}</h3>
            <p class="meta">
              {{ t("panel.account.status") }}:
              {{ authStore.session?.user?.isTwoFactorEnabled ? t("panel.account.enabled") : t("panel.account.disabled") }}
            </p>
          </div>
          <span :class="['status-pill', authStore.session?.user?.isTwoFactorEnabled ? 'ok' : 'warn']">
            {{ authStore.session?.user?.isTwoFactorEnabled ? t("panel.account.enabled") : t("panel.account.disabled") }}
          </span>
        </div>

        <div v-if="authStore.twoFactorSetup" class="form-grid two">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.account.manualKey") }}</span>
            <input :value="authStore.twoFactorSetup.manualEntryKey" readonly />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.account.verificationCode") }}</span>
            <input v-model="twoFactorVerificationCode" inputmode="numeric" maxlength="8" />
          </label>
          <label class="stacked-field settings-wide-field">
            <span class="field-label">{{ t("panel.account.provisioningUri") }}</span>
            <textarea :value="authStore.twoFactorSetup.provisioningUri" rows="3" readonly />
          </label>
          <div class="row-actions settings-wide-field">
            <button class="compact" @click="confirmTwoFactor">{{ t("panel.account.confirm2fa") }}</button>
          </div>
        </div>

        <div v-else class="settings-inline-action">
          <button v-if="!(authStore.session?.user?.isTwoFactorEnabled ?? false)" class="compact" @click="startTwoFactor">
            {{ t("panel.account.start2fa") }}
          </button>
          <template v-else>
            <button class="secondary compact" @click="regenerateRecoveryCodes">{{ t("panel.account.regenerateRecovery") }}</button>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.account.disablePassword") }}</span>
              <input v-model="disableTwoFactorPassword" type="password" minlength="12" />
            </label>
            <button class="danger compact" @click="disableTwoFactor">{{ t("panel.account.disable2fa") }}</button>
          </template>
        </div>

        <div v-if="recoveryCodes.length" class="recovery-codes">
          <strong>{{ t("panel.account.recoveryTitle") }}</strong>
          <p class="meta">{{ t("panel.account.recoveryHint") }}</p>
          <ul class="recovery-list">
            <li v-for="rc in recoveryCodes" :key="rc">{{ rc }}</li>
          </ul>
          <div class="row-actions">
            <button class="secondary compact" @click="copyRecoveryCodes">{{ t("panel.account.recoveryCopy") }}</button>
            <button class="secondary compact" @click="recoveryCodes = []">{{ t("panel.common.cancel") }}</button>
          </div>
        </div>
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

      <section v-else-if="settingsTab === 'users' && (isAdmin || isReseller)" class="settings-section">
        <!-- Servis planları (panel): limit şablonu tanımla, müşteriye ata. Yalnız admin. -->
        <div v-if="isAdmin" class="form-surface plans-card">
          <div class="plans-head">
            <div>
              <strong>{{ t("panel.plans.title") }}</strong>
              <p class="meta">{{ t("panel.plans.hint") }}</p>
            </div>
            <button type="button" class="compact" @click="showPlanForm = !showPlanForm">
              {{ showPlanForm ? t("panel.common.cancel") : t("panel.plans.add") }}
            </button>
          </div>

          <div v-if="servicePlans.length" class="plans-grid">
            <article v-for="plan in servicePlans" :key="plan.id" class="plan-card">
              <div class="plan-card-head">
                <strong class="plan-name">{{ plan.name }}</strong>
                <button class="secondary compact danger" @click="removeServicePlan(plan.id)">{{ t("panel.common.delete") }}</button>
              </div>
              <ul class="plan-limits">
                <li><span>{{ t("panel.plans.sitesLabel") }}</span><b>{{ plan.maxSites || "∞" }}</b></li>
                <li><span>{{ t("panel.plans.diskLabel") }}</span><b>{{ plan.maxDiskMb ? plan.maxDiskMb + " MB" : "∞" }}</b></li>
                <li><span>{{ t("panel.plans.databasesLabel") }}</span><b>{{ plan.maxDatabases || "∞" }}</b></li>
                <li><span>{{ t("panel.plans.mailboxesLabel") }}</span><b>{{ plan.maxMailboxes || "∞" }}</b></li>
                <li><span>{{ t("panel.plans.scheduledTasksLabel") }}</span><b>{{ plan.maxScheduledTasks || "∞" }}</b></li>
              </ul>
            </article>
          </div>
          <p v-else class="empty-state plans-empty">{{ t("panel.plans.empty") }}</p>

          <form v-if="showPlanForm" class="plan-form-grid" @submit.prevent="addServicePlan">
            <label class="stacked-field plan-name-cell">
              <span class="field-label">{{ t("panel.plans.name") }}</span>
              <input v-model.trim="planForm.name" :placeholder="t('panel.plans.namePlaceholder')" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.users.maxSites") }}</span>
              <input v-model.number="planForm.maxSites" type="number" min="0" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.users.maxDiskMb") }}</span>
              <input v-model.number="planForm.maxDiskMb" type="number" min="0" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.users.maxDatabases") }}</span>
              <input v-model.number="planForm.maxDatabases" type="number" min="0" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.users.maxMailboxes") }}</span>
              <input v-model.number="planForm.maxMailboxes" type="number" min="0" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.users.maxScheduledTasks") }}</span>
              <input v-model.number="planForm.maxScheduledTasks" type="number" min="0" />
            </label>
            <p class="plan-form-hint">{{ t("panel.plans.formHint") }}</p>
            <div class="plan-form-actions">
              <button type="submit" class="compact" :disabled="!planForm.name">{{ t("panel.plans.add") }}</button>
            </div>
          </form>
        </div>

        <div class="module-toolbar">
          <div>
            <strong>{{ t("panel.users.title") }}</strong>
            <p class="meta">{{ t("panel.settings.usersHint") }}</p>
          </div>
          <button class="compact" @click="showUserCreateForm = !showUserCreateForm">
            {{ showUserCreateForm ? t("panel.common.cancel") : t("panel.users.create") }}
          </button>
        </div>

        <form v-if="showUserCreateForm" class="form-surface form-grid user-create-grid" @submit.prevent="createUser">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.users.userName") }}</span>
            <input v-model="createUserForm.userName" minlength="3" maxlength="32" required />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.form.email") }}</span>
            <input v-model="createUserForm.email" type="email" required />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.form.password") }}</span>
            <input v-model="createUserForm.password" type="password" minlength="12" required />
          </label>
          <label v-if="isAdmin" class="stacked-field">
            <span class="field-label">{{ t("panel.users.role") }}</span>
            <select v-model="createUserForm.role">
              <option value="Administrator">{{ t("panel.role.administrator") }}</option>
              <option value="Operator">{{ t("panel.role.operator") }}</option>
              <option value="Reseller">{{ t("panel.role.reseller") }}</option>
              <option value="Customer">{{ t("panel.role.customer") }}</option>
            </select>
          </label>
          <label v-if="isAdmin && createUserForm.role === 'Customer' && resellers.length > 0" class="stacked-field">
            <span class="field-label">{{ t("panel.users.owner") }}</span>
            <select v-model="createUserForm.ownerId">
              <option value="">{{ t("panel.users.ownerNone") }}</option>
              <option v-for="reseller in resellers" :key="reseller.id" :value="reseller.id">{{ reseller.userName }}</option>
            </select>
            <small class="field-hint">{{ t("panel.users.ownerHint") }}</small>
          </label>
          <div v-if="createUserForm.role === 'Customer'" class="stacked-field customer-sites">
            <span class="field-label">{{ t("panel.users.assignedSites") }}</span>
            <label v-for="site in sitesStore.sites" :key="site.name" class="checkbox">
              <input v-model="createUserForm.assignedSites" type="checkbox" :value="site.name" />
              <span>{{ site.name }}</span>
            </label>
            <small v-if="sitesStore.sites.length === 0" class="field-hint">{{ t("panel.users.noSitesToAssign") }}</small>
          </div>
          <label v-if="createUserForm.role === 'Customer' && servicePlans.length > 0" class="stacked-field">
            <span class="field-label">{{ t("panel.plans.select") }}</span>
            <select v-model="createPlanId" @change="applyPlanToCreate">
              <option value="">{{ t("panel.plans.custom") }}</option>
              <option v-for="plan in servicePlans" :key="plan.id" :value="plan.id">{{ plan.name }}</option>
            </select>
          </label>
          <label v-if="createUserForm.role === 'Customer'" class="stacked-field">
            <span class="field-label">{{ t("panel.users.maxSites") }}</span>
            <input v-model.number="createUserForm.maxSites" type="number" min="0" />
          </label>
          <label v-if="createUserForm.role === 'Customer'" class="stacked-field">
            <span class="field-label">{{ t("panel.users.maxDiskMb") }}</span>
            <input v-model.number="createUserForm.maxDiskMb" type="number" min="0" />
          </label>
          <label v-if="createUserForm.role === 'Customer'" class="stacked-field">
            <span class="field-label">{{ t("panel.users.maxDatabases") }}</span>
            <input v-model.number="createUserForm.maxDatabases" type="number" min="0" />
          </label>
          <label v-if="createUserForm.role === 'Customer'" class="stacked-field">
            <span class="field-label">{{ t("panel.users.maxMailboxes") }}</span>
            <input v-model.number="createUserForm.maxMailboxes" type="number" min="0" />
          </label>
          <label v-if="createUserForm.role === 'Customer'" class="stacked-field">
            <span class="field-label">{{ t("panel.users.maxScheduledTasks") }}</span>
            <input v-model.number="createUserForm.maxScheduledTasks" type="number" min="0" />
          </label>
          <p v-if="createUserForm.role === 'Customer'" class="quota-hint">{{ t("panel.plans.formHint") }}</p>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.form.language") }}</span>
            <select v-model="createUserForm.preferredLanguage">
              <option value="tr">{{ t("panel.language.tr") }}</option>
              <option value="en">{{ t("panel.language.en") }}</option>
            </select>
          </label>
          <label class="check-field welcome-cell">
            <input v-model="createUserForm.sendWelcomeEmail" type="checkbox" />
            <span>{{ t("panel.users.sendWelcomeEmail") }}</span>
          </label>
          <div class="form-submit-cell">
            <button type="submit" class="compact">{{ t("panel.users.create") }}</button>
          </div>
        </form>

        <div class="data-table">
          <div class="data-row table-head user-row">
            <span>{{ t("panel.users.userName") }}</span>
            <span>{{ t("panel.users.role") }}</span>
            <span>{{ t("panel.users.status") }}</span>
            <span>{{ t("panel.users.twoFactor") }}</span>
            <span>{{ t("panel.software.action") }}</span>
          </div>
          <div v-for="user in authStore.users" :key="user.id" class="data-row user-row">
            <div class="cell-main">
              <strong>{{ user.userName }}</strong>
              <small>{{ user.email }}</small>
              <small v-if="user.role === 'Customer'" class="customer-meta">
                {{ user.assignedSites.length }} {{ t("panel.users.sitesShort") }}
                · {{ user.diskUsageMb }} / {{ user.maxDiskMb > 0 ? user.maxDiskMb : "∞" }} MB
                · {{ user.databaseCount }} / {{ user.maxDatabases > 0 ? user.maxDatabases : "∞" }} {{ t("panel.users.databasesShort") }}
                <template v-if="user.ownerName"> · {{ t("panel.users.owner") }}: {{ user.ownerName }}</template>
              </small>
            </div>
            <span>{{ user.role }}</span>
            <span :class="['status-pill', user.isActive ? 'ok' : 'warn']">
              {{ user.isActive ? t("panel.status.active") : t("panel.status.inactive") }}
            </span>
            <span :class="['status-pill', user.isTwoFactorEnabled ? 'ok' : 'warn']">
              {{ user.isTwoFactorEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
            </span>
            <span class="user-row-actions">
              <button
                v-if="user.role === 'Customer'"
                class="secondary compact row-button"
                @click="impersonateUser(user)">
                {{ t("panel.users.loginAs") }}
              </button>
              <button class="secondary compact row-button" @click="selectUser(user)">{{ t("panel.users.edit") }}</button>
            </span>
          </div>
        </div>

        <form v-if="selectedUserId" class="form-surface form-grid user-edit-grid" @submit.prevent="saveUserChanges">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.form.email") }}</span>
            <input v-model="selectedUserForm.email" type="email" required />
          </label>
          <label v-if="isAdmin" class="stacked-field">
            <span class="field-label">{{ t("panel.users.role") }}</span>
            <select v-model="selectedUserForm.role">
              <option value="Administrator">{{ t("panel.role.administrator") }}</option>
              <option value="Operator">{{ t("panel.role.operator") }}</option>
              <option value="Reseller">{{ t("panel.role.reseller") }}</option>
              <option value="Customer">{{ t("panel.role.customer") }}</option>
            </select>
          </label>
          <label v-if="isAdmin && selectedUserForm.role === 'Customer' && resellers.length > 0" class="stacked-field">
            <span class="field-label">{{ t("panel.users.owner") }}</span>
            <select v-model="selectedUserForm.ownerId">
              <option value="">{{ t("panel.users.ownerNone") }}</option>
              <option v-for="reseller in resellers" :key="reseller.id" :value="reseller.id">{{ reseller.userName }}</option>
            </select>
          </label>
          <div v-if="selectedUserForm.role === 'Customer'" class="stacked-field customer-sites">
            <span class="field-label">{{ t("panel.users.assignedSites") }}</span>
            <label v-for="site in sitesStore.sites" :key="site.name" class="checkbox">
              <input v-model="selectedUserForm.assignedSites" type="checkbox" :value="site.name" />
              <span>{{ site.name }}</span>
            </label>
            <small v-if="sitesStore.sites.length === 0" class="field-hint">{{ t("panel.users.noSitesToAssign") }}</small>
          </div>
          <label v-if="selectedUserForm.role === 'Customer'" class="stacked-field">
            <span class="field-label">{{ t("panel.users.maxSites") }}</span>
            <input v-model.number="selectedUserForm.maxSites" type="number" min="0" />
          </label>
          <label v-if="selectedUserForm.role === 'Customer'" class="stacked-field">
            <span class="field-label">{{ t("panel.users.maxDiskMb") }}</span>
            <input v-model.number="selectedUserForm.maxDiskMb" type="number" min="0" />
          </label>
          <label v-if="selectedUserForm.role === 'Customer'" class="stacked-field">
            <span class="field-label">{{ t("panel.users.maxDatabases") }}</span>
            <input v-model.number="selectedUserForm.maxDatabases" type="number" min="0" />
          </label>
          <label v-if="selectedUserForm.role === 'Customer'" class="stacked-field">
            <span class="field-label">{{ t("panel.users.maxMailboxes") }}</span>
            <input v-model.number="selectedUserForm.maxMailboxes" type="number" min="0" />
          </label>
          <label v-if="selectedUserForm.role === 'Customer'" class="stacked-field">
            <span class="field-label">{{ t("panel.users.maxScheduledTasks") }}</span>
            <input v-model.number="selectedUserForm.maxScheduledTasks" type="number" min="0" />
          </label>
          <p v-if="selectedUserForm.role === 'Customer'" class="quota-hint">{{ t("panel.plans.formHint") }}</p>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.form.language") }}</span>
            <select v-model="selectedUserForm.preferredLanguage">
              <option value="tr">{{ t("panel.language.tr") }}</option>
              <option value="en">{{ t("panel.language.en") }}</option>
            </select>
          </label>
          <label class="check-field">
            <input v-model="selectedUserForm.isActive" type="checkbox" />
            <span>{{ t("panel.users.isActive") }}</span>
          </label>
          <div class="row-actions">
            <button type="submit" class="compact">{{ t("panel.users.save") }}</button>
            <button type="button" class="secondary compact" @click="selectedUserId = ''">{{ t("panel.common.cancel") }}</button>
          </div>
        </form>
      </section>

      <NotificationsView v-else-if="settingsTab === 'notifications' && isAdmin" />
      <UpdateView v-else-if="settingsTab === 'updates' && isAdmin" />
    </section>
  </MainLayout>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import MainLayout from "../layouts/MainLayout.vue";
import { readSecureEntryPath } from "../api/client";
import { getFtpUsers } from "../api/ftp";
import { getPanelPreferences, updatePanelPreferences, type PanelPreferences } from "../api/panelSettings";
import { getServicePlans, createServicePlan, deleteServicePlan, type ServicePlan } from "../api/servicePlans";
import { useAppsStore } from "../stores/apps";
import { extractApiDetails, extractApiMessage, useAuthStore, type ManagedUser } from "../stores/auth";
import { useDatabasesStore } from "../stores/databases";
import { usePhpStore } from "../stores/php";
import { useProxyStore } from "../stores/proxy";
import { useSetupStore, type OperationProgress } from "../stores/setup";
import { useSitesStore } from "../stores/sites";
import { useSoftwareStore } from "../stores/software";
import { useSystemStore } from "../stores/system";
import CronView from "./CronView.vue";
import DatabasesView from "./DatabasesView.vue";
import MigrationView from "./MigrationView.vue";
import MyDatabasesView from "./MyDatabasesView.vue";
import DashboardOverviewView from "./DashboardOverviewView.vue";
import FilesView from "./FilesView.vue";
import MailView from "./MailView.vue";
import MonitoringView from "./MonitoringView.vue";
import NotificationsView from "./NotificationsView.vue";
import SecurityView from "./SecurityView.vue";
import ServerToolsView from "./ServerToolsView.vue";
import BackupView from "./BackupView.vue";
import SoftwareView from "./SoftwareView.vue";
import UpdateView from "./UpdateView.vue";
import { checkForUpdate, type UpdateStatus } from "../api/update";
import WebsiteWorkspaceView from "./WebsiteWorkspaceView.vue";

type ViewMode = "setup" | "auth" | "dashboard";
type SectionItem = { id: string; label: string; description: string; glyph: string; badge?: string };
type SettingsTab = "general" | "account" | "security" | "users" | "notifications" | "updates";

const i18n = useI18n();
const t = (key: string) => {
  try {
    const value = i18n.t(key);
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
const twoFactorVerificationCode = ref("");
const disableTwoFactorPassword = ref("");
const recoveryCodes = ref<string[]>([]);
const selectedUserId = ref("");
const settingsTab = ref<SettingsTab>("general");
const showUserCreateForm = ref(false);
const showPlanForm = ref(false);
const activeSection = ref("home");
// Ana sayfa "yeni sürüm" bandı için panel güncelleme durumu (yalnızca admin doldurur).
const panelUpdate = ref<UpdateStatus | null>(null);
const ftpUserCount = ref(0);
let refreshHandle: number | null = null;

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

const createUserForm = reactive({
  userName: "",
  email: "",
  password: "",
  role: "Operator",
  ownerId: "",
  assignedSites: [] as string[],
  maxSites: 0,
  maxDiskMb: 0,
  maxDatabases: 0,
  maxMailboxes: 0,
  maxScheduledTasks: 0,
  sendWelcomeEmail: false,
  preferredLanguage: "tr"
});

const selectedUserForm = reactive({
  email: "",
  role: "Operator",
  ownerId: "",
  assignedSites: [] as string[],
  maxSites: 0,
  maxDiskMb: 0,
  maxDatabases: 0,
  maxMailboxes: 0,
  maxScheduledTasks: 0,
  preferredLanguage: "tr",
  isActive: true
});

// Müşteriye sahip atamak için bayi (Reseller) kullanıcı listesi.
const resellers = computed(() => authStore.users.filter((user) => user.role === "Reseller"));

const securityForm = reactive({
  secureEntryPath: "",
  allowedIpAddressesText: ""
});

// Servis planları (Servis Planlari): müşterilere atanabilen limit şablonları.
const servicePlans = ref<ServicePlan[]>([]);
const planForm = reactive({ name: "", maxSites: 0, maxDiskMb: 0, maxDatabases: 0, maxMailboxes: 0, maxScheduledTasks: 0 });
const createPlanId = ref("");

async function loadServicePlans() {
  servicePlans.value = await getServicePlans().catch(() => []);
}

async function addServicePlan() {
  if (!planForm.name.trim()) {
    return;
  }
  await createServicePlan({ name: planForm.name.trim(), maxSites: planForm.maxSites, maxDiskMb: planForm.maxDiskMb, maxDatabases: planForm.maxDatabases, maxMailboxes: planForm.maxMailboxes, maxScheduledTasks: planForm.maxScheduledTasks });
  planForm.name = "";
  planForm.maxSites = 0;
  planForm.maxDiskMb = 0;
  planForm.maxDatabases = 0;
  planForm.maxMailboxes = 0;
  planForm.maxScheduledTasks = 0;
  showPlanForm.value = false;
  await loadServicePlans();
}

async function removeServicePlan(id: string) {
  await deleteServicePlan(id);
  await loadServicePlans();
}

function applyPlanToCreate() {
  const plan = servicePlans.value.find((item) => item.id === createPlanId.value);
  if (plan) {
    createUserForm.maxSites = plan.maxSites;
    createUserForm.maxDiskMb = plan.maxDiskMb;
    createUserForm.maxDatabases = plan.maxDatabases;
    createUserForm.maxMailboxes = plan.maxMailboxes;
    createUserForm.maxScheduledTasks = plan.maxScheduledTasks;
  }
}
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

const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const isCustomer = computed(() => authStore.session?.user?.role === "Customer");
const isReseller = computed(() => authStore.session?.user?.role === "Reseller");

// Bayi: create formunda rol Customer'a sabit (rol seçimi gizli) + Ayarlar'da doğrudan
// "Müşteriler" sekmesine düşsün ("general" sekmesi bayiye kapalı).
watch(isReseller, (value) => {
  if (value) {
    createUserForm.role = "Customer";
    settingsTab.value = "users";
  }
}, { immediate: true });
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

const dashboardSections = computed<SectionItem[]>(() => {
  const sections: SectionItem[] = [
    // panel sırası: Web Siteleri → Posta → Dosyalar → Veritabanları → İstatistikler →
    // Uygulamalar/Mağaza → Zamanlanmış Görevler → Güvenlik → Araçlar/Ayarlar.
    { id: "home", label: t("panel.navigation.home"), description: t("panel.dashboard.subtitle"), glyph: "HM" },
    { id: "sites", label: t("panel.navigation.sites"), description: t("panel.sites.workspaceSubtitle"), glyph: "WS", badge: sitesStore.sites.length ? String(sitesStore.sites.length) : undefined },
    // Plesk gibi üst-düzey "Müşteriler & Bayiler" girişi (admin) / "Müşteriler" (bayi). Mevcut yönetim
    // ekranını (Ayarlar > Kullanıcılar) doğrudan açar; handleNavigation bunu settings+users sekmesine yönlendirir.
    { id: "accounts", label: isReseller.value ? t("panel.navigation.customers") : t("panel.navigation.accounts"), description: t("panel.navigation.accountsDesc"), glyph: "MU" },
    { id: "mail", label: t("panel.navigation.mail"), description: t("panel.mail.subtitle"), glyph: "ML" },
    { id: "files", label: t("panel.navigation.files"), description: t("panel.files.subtitle"), glyph: "FL" },
    { id: "database", label: t("panel.navigation.database"), description: t("panel.database.subtitle"), glyph: "DB" },
    { id: "monitor", label: t("panel.navigation.monitor"), description: t("panel.monitor.subtitle"), glyph: "MN" },
    { id: "software", label: t("panel.navigation.store"), description: t("panel.software.subtitle"), glyph: "ST", badge: installedSoftwareCount.value ? String(installedSoftwareCount.value) : undefined },
    { id: "tasks", label: t("panel.navigation.tasks"), description: t("panel.tasks.subtitle"), glyph: "TK" },
    { id: "security", label: t("panel.navigation.security"), description: t("panel.security.subtitle"), glyph: "SC" },
    { id: "tools", label: t("panel.navigation.serverTools"), description: t("panel.serverTools.subtitle"), glyph: "AR" },
    { id: "migration", label: t("panel.navigation.migration"), description: t("panel.migration.subtitle"), glyph: "MG" },
    { id: "settings", label: t("panel.navigation.settings"), description: t("panel.settings.subtitle"), glyph: "AY" },
    { id: "exit", label: t("panel.navigation.exit"), description: t("panel.auth.logout"), glyph: "EX" }
  ];

  if (isReseller.value) {
    // Bayi: kendi müşterilerini yönetir (Ayarlar > Müşteriler) + müşterilerinin tüm site
    // kaynakları (Web Siteleri hub'ı: PHP/SSL/DNS/FTP/Proxy/WP/Stats/Backup/Hosting — hepsi
    // sahip-kapsamına göre EnsureSiteAsync ile filtreli; server-level araçlar hub'da gizli).
    return sections.filter((section) =>
      section.id === "home" ||
      section.id === "sites" ||
      section.id === "accounts" ||
      section.id === "mail" ||
      section.id === "files" ||
      section.id === "database" ||
      section.id === "tasks" ||
      section.id === "settings" ||
      section.id === "exit");
  }

  if (isCustomer.value) {
    // Müşteri KENDİ sitelerini + veritabanlarını yönetir (site/DB oluşturma kotaya tabi, kapsamı kendi sitelerine kilitli).
    const customerSections = sections.filter((section) =>
      section.id === "home" ||
      section.id === "sites" ||
      section.id === "mail" ||
      section.id === "database" ||
      section.id === "files" ||
      section.id === "tasks" ||
      section.id === "settings" ||
      section.id === "exit");
    // Müşteriye özel yedek bölümü (kendi sitelerinin yedekleri) — Dosyalar'dan hemen sonra.
    const filesIndex = customerSections.findIndex((section) => section.id === "files");
    customerSections.splice(filesIndex >= 0 ? filesIndex + 1 : customerSections.length - 1, 0, {
      id: "backup",
      label: t("panel.navigation.backup"),
      description: t("panel.backup.subtitle"),
      glyph: "YD"
    });
    return customerSections;
  }

  if (!isAdmin.value) {
    return sections.filter((section) =>
      section.id === "home" ||
      section.id === "monitor" ||
      section.id === "settings" ||
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
    requiresTwoFactor.value = false;
    showBanner(t("panel.auth.logoutSuccess"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function loadDashboardData() {
  await systemStore.fetchOverview();

  if (isCustomer.value) {
    // Müşteri için site listesi (kapsam ucu yalnızca atanmış siteleri döner).
    await Promise.allSettled([sitesStore.fetchSites()]);
    if (activeSection.value === "home" && sitesStore.sites.length > 0) {
      activeSection.value = "sites";
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
    loadServicePlans(),
    getPanelPreferences().then((preferences) => {
      Object.assign(panelPreferences, preferences);
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

function handleNavigation(sectionId: string) {
  if (sectionId === "exit") {
    void logout();
    return;
  }

  // Plesk gibi üst-düzey "Müşteriler & Bayiler" girişi → mevcut hesap yönetimi ekranı (Ayarlar > Kullanıcılar).
  if (sectionId === "accounts") {
    activeSection.value = "settings";
    settingsTab.value = "users";
    window.scrollTo({ top: 0, behavior: "auto" });
    return;
  }

  // Ana sayfadaki "Yeni sürüm" bandı → Ayarlar > Güncellemeler sekmesi.
  if (sectionId === "updates") {
    activeSection.value = "settings";
    settingsTab.value = "updates";
    window.scrollTo({ top: 0, behavior: "auto" });
    return;
  }

  activeSection.value = sectionId;
  if (sectionId === "settings" && !isAdmin.value) {
    settingsTab.value = "account";
  }
  window.scrollTo({ top: 0, behavior: "auto" });
}

// Ana sayfa banner'ı için panel güncelleme durumunu bir kez çek (admin; GitHub Releases'e bakar, hata olursa sessiz).
async function loadPanelUpdate() {
  if (!isAdmin.value) {
    return;
  }

  try {
    panelUpdate.value = await checkForUpdate();
  } catch {
    panelUpdate.value = null;
  }
}

async function startTwoFactor() {
  try {
    await authStore.beginTwoFactorSetup();
    showBanner(t("panel.account.started"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function confirmTwoFactor() {
  try {
    recoveryCodes.value = await authStore.confirmTwoFactorSetup(twoFactorVerificationCode.value);
    twoFactorVerificationCode.value = "";
    showBanner(t("panel.account.enabledMessage"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function regenerateRecoveryCodes() {
  try {
    recoveryCodes.value = await authStore.regenerateRecoveryCodes();
    showBanner(t("panel.account.recoveryRegenerated"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function copyRecoveryCodes() {
  try {
    await navigator.clipboard.writeText(recoveryCodes.value.join("\n"));
    showBanner(t("panel.account.recoveryCopied"), false);
  } catch {
    showBanner(t("panel.account.recoveryCopyFailed"), true);
  }
}

async function disableTwoFactor() {
  try {
    await authStore.disableTwoFactor(disableTwoFactorPassword.value);
    disableTwoFactorPassword.value = "";
    recoveryCodes.value = [];
    showBanner(t("panel.account.disabledMessage"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
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

async function createUser() {
  try {
    await authStore.createUser({ ...createUserForm, ownerId: createUserForm.ownerId || null });
    createUserForm.userName = "";
    createUserForm.email = "";
    createUserForm.password = "";
    // Bayi YALNIZ müşteri oluşturabilir → rolü Customer'da tut (yoksa kota/site/plan alanları gizlenir).
    createUserForm.role = isReseller.value ? "Customer" : "Operator";
    createUserForm.ownerId = "";
    createUserForm.assignedSites = [];
    createUserForm.maxSites = 0;
    createUserForm.maxDiskMb = 0;
    createUserForm.maxDatabases = 0;
    createUserForm.maxMailboxes = 0;
    createUserForm.maxScheduledTasks = 0;
    createUserForm.sendWelcomeEmail = false;
    createUserForm.preferredLanguage = "tr";
    showUserCreateForm.value = false;
    showBanner(t("panel.users.created"), false);
  } catch (error) {
    showBanner(resolveApiMessage(error), true);
  }
}

async function impersonateUser(user: ManagedUser) {
  try {
    await authStore.impersonate(user.id);
    // Müşteri arayüzüne temiz geçiş için paneli yeniden yükle.
    window.location.reload();
  } catch (error) {
    void error;
  }
}

function selectUser(user: ManagedUser) {
  selectedUserId.value = user.id;
  selectedUserForm.email = user.email;
  selectedUserForm.role = user.role;
  selectedUserForm.ownerId = user.ownerId ?? "";
  selectedUserForm.assignedSites = [...(user.assignedSites ?? [])];
  selectedUserForm.maxSites = user.maxSites ?? 0;
  selectedUserForm.maxDiskMb = user.maxDiskMb ?? 0;
  selectedUserForm.maxDatabases = user.maxDatabases ?? 0;
  selectedUserForm.maxMailboxes = user.maxMailboxes ?? 0;
  selectedUserForm.maxScheduledTasks = user.maxScheduledTasks ?? 0;
  selectedUserForm.preferredLanguage = user.preferredLanguage;
  selectedUserForm.isActive = user.isActive;
}

async function saveUserChanges() {
  if (!selectedUserId.value) {
    return;
  }

  try {
    await authStore.updateUser(selectedUserId.value, { ...selectedUserForm, ownerId: selectedUserForm.ownerId || null });
    if (selectedUserId.value === authStore.session?.user?.id) {
      await authStore.fetchSession();
    }
    showBanner(t("panel.users.saved"), false);
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
}

function stopRefreshLoop() {
  if (refreshHandle !== null) {
    window.clearInterval(refreshHandle);
    refreshHandle = null;
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

.customer-sites {
  grid-column: 1 / -1;
  display: grid;
  gap: 6px;
  max-height: 180px;
  overflow-y: auto;
  padding: 10px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
}

.customer-sites .checkbox {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.customer-sites .checkbox input {
  width: auto;
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

.section-head,
.settings-inline-action {
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

.settings-inline-action {
  justify-content: flex-start;
  align-items: end;
}

.settings-inline-action .stacked-field {
  min-width: min(340px, 100%);
}

.settings-inline-action button,
.row-button,
.row-actions button {
  width: auto;
}

.settings-wide-field {
  grid-column: 1 / -1;
}

.user-row {
  grid-template-columns: minmax(220px, 1.5fr) 0.75fr 0.7fr 0.7fr auto;
}

.user-row-actions {
  display: inline-flex;
  gap: 6px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.plans-card {
  display: grid;
  gap: 14px;
}

.plans-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.plans-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
  gap: 10px;
}

.plan-card {
  border: 1px solid var(--panel-border);
  border-radius: 12px;
  background: var(--surface-1);
  padding: 12px 14px;
  display: grid;
  gap: 10px;
  align-content: start;
}

.plan-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.plan-card-head .plan-name {
  font-size: 0.98rem;
  min-width: 0;
  overflow-wrap: anywhere;
}

.plan-limits {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 6px;
}

.plan-limits li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  font-size: 0.84rem;
  padding: 4px 0;
  border-top: 1px solid color-mix(in srgb, var(--panel-border) 60%, transparent);
}

.plan-limits li:first-child {
  border-top: none;
}

.plan-limits li span {
  color: var(--text-muted);
}

.plan-limits li b {
  font-weight: 700;
}

.plans-empty {
  padding: 14px 0;
}

.plan-form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 12px;
  align-items: end;
  padding-top: 14px;
  border-top: 1px solid var(--panel-border);
}

.plan-form-grid .plan-name-cell {
  grid-column: 1 / -1;
}

.plan-form-hint {
  grid-column: 1 / -1;
  margin: 0;
  color: var(--text-muted);
  font-size: 0.8rem;
}

.plan-form-actions {
  grid-column: 1 / -1;
  display: flex;
  justify-content: flex-end;
}

.customer-meta {
  color: var(--accent-text);
}

.form-submit-cell {
  display: flex;
  align-items: end;
}

.form-submit-cell button {
  width: auto;
}

.user-edit-grid {
  align-items: end;
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

label {
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

.user-create-grid {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.quota-hint {
  grid-column: 1 / -1;
  margin: 0;
  color: var(--text-muted);
  font-size: 0.8rem;
}

@media (max-width: 980px) {
  .user-create-grid,
  .user-edit-grid {
    grid-template-columns: 1fr;
  }

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

  .settings-wide-field {
    grid-column: auto;
  }
}
</style>
