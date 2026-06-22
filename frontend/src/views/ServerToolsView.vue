<template>
  <section class="server-tools">
    <nav class="tools-switcher" :aria-label="t('panel.serverTools.title')">
      <button :class="{ active: activeTool === 'health' }" @click="activeTool = 'health'">
        <span>HP</span>
        <strong>{{ t("panel.health.title") }}</strong>
      </button>
      <button :class="{ active: activeTool === 'firewall' }" @click="activeTool = 'firewall'">
        <span>FW</span>
        <strong>{{ t("panel.firewall.title") }}</strong>
      </button>
      <button :class="{ active: activeTool === 'backup' }" @click="activeTool = 'backup'">
        <span>BK</span>
        <strong>{{ t("panel.backup.title") }}</strong>
      </button>
      <button :class="{ active: activeTool === 'automation' }" @click="activeTool = 'automation'">
        <span>AU</span>
        <strong>{{ t("panel.automation.title") }}</strong>
      </button>
      <button :class="{ active: activeTool === 'nginx' }" @click="activeTool = 'nginx'">
        <span>NX</span>
        <strong>{{ t("panel.nginx.title") }}</strong>
      </button>
      <button :class="{ active: activeTool === 'logs' }" @click="activeTool = 'logs'">
        <span>LG</span>
        <strong>{{ t("panel.logs.title") }}</strong>
      </button>
      <button :class="{ active: activeTool === 'terminal' }" @click="activeTool = 'terminal'">
        <span>TM</span>
        <strong>{{ t("panel.terminal.title") }}</strong>
      </button>
      <button :class="{ active: activeTool === 'docker' }" @click="activeTool = 'docker'">
        <span>DK</span>
        <strong>{{ t("panel.docker.title") }}</strong>
      </button>
    </nav>

    <ServerHealthView v-if="activeTool === 'health'" />
    <FirewallView v-else-if="activeTool === 'firewall'" />
    <BackupView v-else-if="activeTool === 'backup'" />
    <AutomationView v-else-if="activeTool === 'automation'" />
    <NginxView v-else-if="activeTool === 'nginx'" />
    <LogsView v-else-if="activeTool === 'logs'" />
    <TerminalView v-else-if="activeTool === 'terminal'" />
    <DockerView v-else />
  </section>
</template>

<script setup lang="ts">
import { ref } from "vue";
import { useI18n } from "vue-i18n";
import AutomationView from "./AutomationView.vue";
import BackupView from "./BackupView.vue";
import DockerView from "./DockerView.vue";
import FirewallView from "./FirewallView.vue";
import LogsView from "./LogsView.vue";
import NginxView from "./NginxView.vue";
import TerminalView from "./TerminalView.vue";
import ServerHealthView from "./ServerHealthView.vue";

const { t } = useI18n();
const activeTool = ref<"health" | "firewall" | "backup" | "automation" | "nginx" | "logs" | "terminal" | "docker">("health");
</script>

<style scoped>
.server-tools {
  display: grid;
  gap: 10px;
}

.tools-switcher {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 6px;
  padding: 7px;
  border: 1px solid var(--panel-border);
  border-radius: 15px;
  background: var(--surface-2);
}

.tools-switcher button {
  display: flex;
  gap: 9px;
  align-items: center;
  background: transparent;
  color: var(--text-soft);
  text-align: left;
}

.tools-switcher button > span {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: 9px;
  background: color-mix(in srgb, var(--accent-1) 10%, transparent);
  color: var(--accent-1);
  font-size: 0.67rem;
}

.tools-switcher button.active {
  background: linear-gradient(145deg, color-mix(in srgb, var(--accent-2) 24%, transparent), rgba(22, 29, 41, 0.42));
}

@media (max-width: 700px) {
  .tools-switcher {
    grid-template-columns: 1fr;
  }
}
</style>
