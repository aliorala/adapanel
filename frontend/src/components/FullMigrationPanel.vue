<template>
  <section class="full-migration module-shell" aria-labelledby="full-migration-title">
    <header><h3 id="full-migration-title">{{ tr('title') }}</h3><p>{{ tr('intro') }}</p></header>
    <p v-if="error" class="notice error" role="alert">{{ message(error) }}</p>
    <p v-if="pollError" class="notice error" role="alert">{{ message(pollError) }}</p>
    <RemoteTrustPrompt v-if="trust" :prompt="trust" :busy="busy" @accept="acceptTrust" />
    <form class="form-surface migration-card" @submit.prevent="discover">
      <h4>1. {{ tr('source') }}</h4>
      <fieldset :disabled="busy" class="migration-fields">
        <label>{{ tr('provider') }}<select v-model="source.provider"><option value="adapanel">AdaPanel</option><option value="plesk">Plesk (Linux)</option><option value="cpanel">cPanel / WHM (Linux)</option></select></label>
        <label>{{ source.provider === 'adapanel' ? tr('panelUrl') : tr('sshHost') }}<input v-model="source.host" :placeholder="source.provider === 'adapanel' ? 'https://source.example:8890' : 'source.example'" required autocomplete="off" /></label>
        <template v-if="source.provider === 'adapanel'"><label>{{ tr('apiToken') }}<PasswordField v-model="source.token" autocomplete="off" /></label></template>
        <template v-else>
          <label>{{ tr('sshPort') }}<input v-model.number="source.port" type="number" min="1" max="65535" required /></label>
          <label>{{ tr('sshUser') }}<input v-model="source.user" autocomplete="off" required /></label>
          <label>{{ tr('password') }}<PasswordField v-model="source.password" autocomplete="new-password" /></label>
          <details class="full-row"><summary>{{ tr('privateKey') }}</summary><label>{{ tr('privateKey') }}<textarea v-model="source.privateKey" rows="4" autocomplete="off" spellcheck="false" /></label><label>{{ tr('passphrase') }}<PasswordField v-model="source.passphrase" autocomplete="new-password" /></label></details>
        </template>
      </fieldset>
      <p class="meta">{{ source.provider === 'adapanel' ? tr('nativeHint') : tr('vendorHint') }}</p>
      <button type="submit" :disabled="busy">{{ tr('discover') }}</button>
    </form>

    <section v-if="discovery" class="form-surface migration-card">
      <h4>{{ tr('sourceSelection') }}</h4>
      <fieldset :disabled="busy">
        <template v-if="discovery.adaPanel">
          <label class="check"><input v-model="sourceSelection.allSites" type="checkbox" />{{ tr('allSites') }}</label>
          <div v-if="!sourceSelection.allSites" class="choice-grid"><label v-for="site in discovery.adaPanel.sites" :key="site.id" class="check"><input v-model="sourceSelection.siteIds" type="checkbox" :value="site.id" />{{ site.name }}</label></div>
          <label class="check"><input v-model="sourceSelection.allDatabases" type="checkbox" />{{ tr('allDatabases') }}</label>
          <div v-if="!sourceSelection.allDatabases" class="choice-grid"><label v-for="db in discovery.adaPanel.databases" :key="db.id" class="check"><input v-model="sourceSelection.databaseIds" type="checkbox" :value="db.id" />{{ db.name }} · {{ db.engine }}</label></div>
          <label class="check"><input v-model="sourceSelection.includeMail" type="checkbox" />{{ tr('mailOfSites') }}</label>
          <label class="check"><input v-model="sourceSelection.includeDns" type="checkbox" />{{ tr('dnsOfSites') }}</label>
          <label class="check"><input v-model="sourceSelection.includeTasks" type="checkbox" />{{ tr('allTasksOptional') }}</label>
          <ul v-if="discovery.adaPanel.issues.length" class="messages"><li v-for="(issue, i) in discovery.adaPanel.issues" :key="i">{{ issue.resourceId }} · {{ message(issue.code) }}</li></ul>
        </template>
        <template v-else-if="discovery.hostingPanel">
          <p>{{ tr('vendorSelectionHint') }}</p>
          <div class="choice-grid"><label v-for="unit in discovery.hostingPanel.units" :key="unit.id" class="check"><input v-model="sourceUnits" type="checkbox" :value="unit.id" />{{ unit.name }}</label></div>
        </template>
      </fieldset>
      <button :disabled="busy || (!!discovery.hostingPanel && sourceUnits.length === 0)" @click="acquire">{{ tr('acquire') }}</button>
    </section>

    <section v-if="acquisitions.length" class="form-surface migration-card">
      <h4>{{ tr('sourceJobs') }}</h4>
      <article v-for="job in acquisitions" :key="job.id" class="job">
        <div class="job-heading"><strong>{{ job.provider }} · {{ job.sourceHost }}</strong><span class="status-pill">{{ state(job.state) }}</span><button v-if="isRunning(job.state)" class="secondary compact" :disabled="busy || job.cancellationRequested" @click="cancel('acquisition', job.id)">{{ job.cancellationRequested ? tr('cancelling') : tr('cancel') }}</button></div>
        <p class="meta">{{ date(job.createdAtUtc) }} · {{ tr('stage') }}: {{ stage(job.stage) }} · {{ tr('measuredContent') }}: {{ bytes(job.normalizedPayloadBytes) }}</p>
        <p v-if="job.errorCode" class="notice error">{{ message(job.errorCode) }}</p>
        <ul class="messages"><li v-for="warning in job.warnings" :key="warning">{{ message(warning) }}</li></ul>
        <details v-if="job.remoteRecoveries.length || job.packageRecoveries.length"><summary>{{ tr('recovery') }}</summary>
          <p>{{ tr('recoveryHint') }}</p>
          <div v-for="(entry, i) in job.remoteRecoveries" :key="i"><strong>{{ entry.sourceUnitId }}</strong><code>{{ entry.workspace }}</code><code v-for="file in entry.files" :key="file">{{ file }}</code><p>{{ entry.cleanupComplete ? tr('cleanupDone') : tr('cleanupPending') }}</p></div>
          <p v-for="entry in job.packageRecoveries" :key="entry.packageId"><code>{{ entry.packageId }}</code> · {{ message(entry.requiredAction) }}</p>
        </details>
      </article>
    </section>

    <section class="form-surface migration-card">
      <h4>2. {{ tr('targetSelection') }}</h4>
      <p>{{ tr('targetHint') }}</p>
      <p v-if="!packages.length" class="meta">{{ tr('noPackages') }}</p>
      <fieldset v-else :disabled="busy">
        <label v-for="pack in packages" :key="pack.id" class="check"><input v-model="selection.packageIds" type="checkbox" :value="pack.id" />{{ pack.sourceName }} · {{ pack.provider }} · {{ date(pack.createdAtUtc) }}</label>
        <div v-if="selection.packageIds.length" class="target-options">
          <label>{{ tr('targetRoot') }}<input v-model="selection.targetBasePath" /></label>
          <label class="check"><input v-model="selection.allSites" type="checkbox" />{{ tr('allSites') }}</label>
          <div v-if="!selection.allSites" class="choice-grid"><label v-for="site in resources.filter(x => x.kind === 'site')" :key="site.key" class="check"><input v-model="selection.siteIds" type="checkbox" :value="site.key" />{{ site.name }} · {{ site.packageName }}</label></div>
          <label class="check"><input v-model="selection.allDatabases" type="checkbox" />{{ tr('allDatabases') }}</label>
          <div v-if="!selection.allDatabases" class="choice-grid"><label v-for="db in resources.filter(x => x.kind === 'database')" :key="db.key" class="check"><input v-model="selection.databaseIds" type="checkbox" :value="db.key" />{{ db.name }} · {{ db.packageName }}</label></div>
          <label class="check"><input v-model="selection.includeMail" type="checkbox" />{{ tr('mailOfSites') }}</label>
          <label class="check"><input v-model="selection.includeDns" type="checkbox" />{{ tr('dnsOfSites') }}</label>
          <label class="check"><input v-model="selection.includeTasks" type="checkbox" />{{ tr('tasksOptional') }}</label>
          <template v-if="selection.includeTasks"><p class="meta">{{ tr('taskSelectionHint') }}</p><div class="choice-grid"><label v-for="task in resources.filter(x => x.kind === 'task')" :key="task.key" class="check"><input v-model="selection.taskIds" type="checkbox" :value="task.key" />{{ task.name }}</label></div></template>
          <label class="check"><input v-model="selection.acceptNewCredentials" type="checkbox" />{{ tr('newCredentials') }}</label>
          <details><summary>{{ tr('mappings') }}</summary>
            <article v-for="r in selectedResources" :key="r.key" class="mapping">
              <strong>{{ kind(r.kind) }} · {{ r.name }}</strong>
              <div class="migration-fields">
                <label>{{ tr('targetName') }}<input v-model="mapping(r.key, r.name).targetName" :readonly="r.kind !== 'task'" /></label>
                <template v-if="r.kind === 'site'">
                  <label>{{ tr('targetPath') }}<input v-model="mapping(r.key, r.name).targetPath" :placeholder="selection.targetBasePath + '\\' + r.name" /></label>
                  <label>{{ tr('runtime') }}<select v-model="mapping(r.key, r.name).runtimeProfile"><option :value="undefined">{{ tr('sourceRuntime') }}</option><option value="auto">{{ tr('auto') }}</option><option value="static">Static</option><option value="php">PHP</option><option value="aspnet">ASP.NET</option><option value="node">Node.js</option><option value="python">Python</option></select></label>
                </template>
                <label v-if="r.kind === 'database'">{{ tr('databaseUser') }}<input v-model="mapping(r.key, r.name).databaseUserName" /></label>
                <p v-if="r.kind === 'database'" class="full-row meta">{{ tr('databaseNamePreserved') }}</p>
                <label v-if="['database', 'dns', 'task'].includes(r.kind)">{{ tr('linkedSite') }}<select v-model="mapping(r.key, r.name).linkedSiteId"><option :value="undefined">{{ tr('sourceLink') }}</option><option v-for="site in resources.filter(x => x.kind === 'site' && x.key.split(':')[0] === r.key.split(':')[0] && (selection.allSites || selection.siteIds.includes(x.key)))" :key="site.key" :value="site.key">{{ site.name }}</option></select></label>
                <template v-if="r.kind === 'mail'">
                  <p class="full-row meta">{{ tr('mailFallbackHint') }}</p>
                  <label>{{ tr('fallbackQuota') }}<input v-model.number="mapping(r.key, r.name).unknownMailboxQuotaMb" type="number" min="0" :placeholder="tr('undecided')" @change="normalizeQuota(mapping(r.key, r.name))" /></label>
                  <label v-for="field in mailStateFields" :key="field.key">{{ tr(field.label) }}<select v-model="mapping(r.key, r.name)[field.key]"><option :value="undefined">{{ tr('undecided') }}</option><option :value="true">{{ tr('enabled') }}</option><option :value="false">{{ tr('disabled') }}</option></select></label>
                </template>
                <template v-if="r.kind === 'task'">
                  <p class="full-row meta">{{ tr('taskMappingHint') }}</p>
                  <label>{{ tr('taskType') }}<select v-model="mapping(r.key, r.name).taskType"><option :value="undefined">{{ tr('sourceRuntime') }}</option><option value="command">{{ tr('command') }}</option><option value="php">PHP</option><option value="url">URL</option></select></label>
                  <label>{{ tr('command') }}<input v-model="mapping(r.key, r.name).taskCommand" autocomplete="off" /></label>
                  <label>{{ tr('arguments') }}<PasswordField :model-value="mapping(r.key, r.name).taskArguments || ''" autocomplete="off" @update:model-value="mapping(r.key, r.name).taskArguments = $event" /></label>
                  <label>{{ tr('workingDirectory') }}<input v-model="mapping(r.key, r.name).taskWorkingDirectory" /></label>
                  <label>URL<PasswordField :model-value="mapping(r.key, r.name).taskUrl || ''" autocomplete="off" @update:model-value="mapping(r.key, r.name).taskUrl = $event" /></label>
                  <label>{{ tr('schedule') }}<select v-model="mapping(r.key, r.name).taskScheduleType"><option :value="undefined">{{ tr('sourceRuntime') }}</option><option value="daily">{{ tr('daily') }}</option><option value="minute">{{ tr('minutes') }}</option><option value="hourly">{{ tr('hours') }}</option></select></label>
                  <label>{{ tr('interval') }}<input v-model.number="mapping(r.key, r.name).taskInterval" type="number" min="1" /></label><label>{{ tr('time') }}<input v-model="mapping(r.key, r.name).taskTimeOfDay" type="time" /></label>
                  <label class="check full-row"><input v-model="mapping(r.key, r.name).taskMappingConfirmed" type="checkbox" />{{ tr('taskConfirm') }}</label>
                </template>
              </div>
            </article>
          </details>
          <details><summary>{{ tr('addressMappings') }}</summary><p class="meta">{{ tr('addressHint') }}</p>
            <div v-for="(row, index) in addressRows" :key="index" class="address-row"><label>{{ tr('oldAddress') }}<input v-model="row.from" /></label><label>{{ tr('newAddress') }}<input v-model="row.to" /></label><button type="button" class="secondary compact" @click="addressRows.splice(index, 1)">{{ tr('remove') }}</button></div>
            <button type="button" class="secondary compact" @click="addressRows.push({ from: '', to: '' })">{{ tr('addAddress') }}</button>
          </details>
          <button type="button" :disabled="busy" @click="preparePlan">{{ tr('preparePlan') }}</button>
        </div>
      </fieldset>
    </section>

    <section v-if="plan" class="form-surface migration-card">
      <h4>3. {{ tr('plan') }}</h4><p class="meta">{{ tr('expires') }}: {{ date(plan.expiresAtUtc) }}</p>
      <ul class="messages error-text"><li v-for="block in plan.blockers" :key="block">{{ message(block) }}</li></ul>
      <article v-for="item in plan.items" :key="item.id" class="job"><strong>{{ kind(item.kind) }} · {{ item.sourceName }} → {{ item.target.targetName }}</strong><p v-if="item.target.targetPath"><code>{{ item.target.targetPath }}</code></p><ul class="messages error-text"><li v-for="block in item.blockers" :key="block">{{ message(block) }}</li></ul><ul class="messages"><li v-for="warning in item.warnings" :key="warning">{{ message(warning) }}</li></ul></article>
      <ul class="messages"><li v-for="action in plan.requiredActions" :key="action">{{ message(action) }}</li></ul>
      <p>{{ tr('applyHint') }}</p><button :disabled="busy || !plan.canApply" @click="start">{{ tr('apply') }}</button>
    </section>

    <section v-if="operations.length" class="form-surface migration-card">
      <h4>{{ tr('operations') }}</h4>
      <article v-for="operation in operations" :key="operation.id" class="job" :data-outcome="outcomeKind(operation)">
        <div class="job-heading"><strong>{{ date(operation.createdAtUtc) }}</strong><span class="status-pill">{{ state(operation.state) }}</span><button v-if="isRunning(operation.state)" class="secondary compact" :disabled="busy || operation.cancellationRequested" @click="cancel('operation', operation.id)">{{ operation.cancellationRequested ? tr('cancelling') : tr('cancel') }}</button></div>
        <p v-if="operation.dataTransferred">{{ tr('dataTransferred') }}</p>
        <div v-for="item in operation.items" :key="item.id" class="result"><strong>{{ kind(item.kind) }} · {{ item.sourceName }} → {{ item.targetName }}</strong><span>{{ state(item.state) }}</span><p v-if="item.errorCode" class="notice error">{{ message(item.errorCode) }}</p><p v-if="item.partialStatePossible" class="notice error">{{ tr('partialHint') }}</p>
          <ul class="messages"><li v-for="(evidence, index) in item.evidence" :key="index">{{ message(evidence) }}</li><li v-for="action in item.requiredActions" :key="action">{{ message(action) }}</li></ul>
          <details v-if="item.createdResources.length"><summary>{{ tr('createdResources') }}</summary><code v-for="resource in item.createdResources" :key="resource">{{ resource }}</code></details>
        </div>
        <ul class="messages"><li v-for="action in operation.requiredActions" :key="action">{{ message(action) }}</li></ul>
        <button v-if="operation.credentialsAvailable && credentialOperation !== operation.id" class="secondary" :disabled="busy" @click="showCredentials(operation.id)">{{ tr('showCredentials') }}</button>
        <div v-if="credentialOperation === operation.id" class="credentials"><p>{{ tr('credentialsHint') }}</p><label v-for="entry in credentials" :key="entry.itemId + entry.userName">{{ entry.resourceName }} · {{ entry.userName }} · {{ entry.host }}<PasswordField :model-value="entry.password" readonly autocomplete="off" /></label><button class="secondary" @click="hideCredentials">{{ tr('hideCredentials') }}</button></div>
      </article>
    </section>
  </section>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import PasswordField from './PasswordField.vue';
import RemoteTrustPrompt from './RemoteTrustPrompt.vue';
import { useFullMigration } from '../composables/useFullMigration';
import { isRunning, outcomeKind, type Mapping } from '../api/fullMigration';
const { t, te } = useI18n();
const tr = (key: string) => t('panel.fullMigration.' + key);
const state = (value: string) => t('panel.fullMigration.state.' + value);
const kind = (value: string) => t('panel.fullMigration.kind.' + value);
const stage = (value: string) => te('panel.fullMigration.stage.' + value) ? t('panel.fullMigration.stage.' + value) : value;
const message = (value: string) => {
  const match = /^((?:migration|cpanel|plesk)\.[a-zA-Z0-9_.]+)(?::| · )?([\s\S]*)$/.exec(value);
  if (match && te(match[1])) return t(match[1]) + (match[2] ? ' · ' + match[2] : '');
  if (value.startsWith('attempted:')) return tr('attempted') + ' · ' + value.slice(10);
  return te(value) ? t(value) : value;
};
const date = (value: string) => new Date(value).toLocaleString();
const bytes = (value: number) => new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value / 1024 / 1024) + ' MiB';
const mailStateFields = [ { key: 'unknownMailboxActive', label: 'fallbackMailboxActive' }, { key: 'unknownMailDomainActive', label: 'fallbackDomainActive' }, { key: 'unknownMailAliasActive', label: 'fallbackAliasActive' } ] as const;
function normalizeQuota(entry: Mapping) { if ((entry.unknownMailboxQuotaMb as unknown) === '') entry.unknownMailboxQuotaMb = null; }
const { source, sourceUnits, sourceSelection, trust, discovery, selection, addressRows, packages, resources, selectedResources, acquisitions, operations, plan, credentials, credentialOperation, busy, error, pollError, discover, acceptTrust, acquire, mapping, preparePlan, start, cancel, showCredentials, hideCredentials } = useFullMigration();
</script>

<style scoped>
.full-migration { min-width: 0; }
.migration-card { display: grid; gap: 14px; padding: 18px; min-width: 0; }
.migration-card h4, .migration-card p { margin: 0; }
fieldset { border: 0; padding: 0; margin: 0; min-width: 0; display: grid; gap: 10px; }
.migration-fields, .choice-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 260px), 1fr)); gap: 14px; }
label { display: grid; gap: 6px; min-width: 0; overflow-wrap: anywhere; }
label.check { display: flex; align-items: center; gap: 8px; }
.check input { width: auto; flex: 0 0 auto; }
input, select, textarea { min-width: 0; max-width: 100%; width: 100%; box-sizing: border-box; }
.full-row { grid-column: 1 / -1; }
.target-options, .credentials { display: grid; gap: 14px; margin-top: 14px; }
.mapping, .job { padding: 14px 0; border-top: 1px solid var(--panel-border); display: grid; gap: 10px; min-width: 0; }
.job-heading, .address-row { display: flex; gap: 12px; flex-wrap: wrap; align-items: center; }
.address-row label { flex: 1 1 220px; }
.messages { margin: 0; padding-left: 22px; overflow-wrap: anywhere; }
.error-text { color: var(--danger-text); }
.result { display: grid; gap: 8px; margin: 10px 0; }
code { display: block; overflow-wrap: anywhere; white-space: pre-wrap; }
summary { cursor: pointer; margin: 8px 0; }
button { justify-self: start; max-width: 100%; white-space: normal; }
@media (max-width: 600px) { .migration-card { padding: 12px; } .job-heading { align-items: flex-start; flex-direction: column; } }
</style>
