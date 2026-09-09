<template>
  <section class="panel terminal-panel">
    <div class="section-head">
      <div>
        <h2>{{ t("panel.terminal.title") }}</h2>
        <p class="meta">{{ t("panel.terminal.subtitle") }}</p>
      </div>
      <span class="shell-label">PowerShell</span>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div class="terminal-toolbar">
      <label class="path-field">
        <span>{{ t("panel.terminal.workingDirectory") }}</span>
        <input v-model="workingDirectory" placeholder="%ProgramData%\AdaPanel" />
      </label>
      <label>
        <span>{{ t("panel.terminal.timeout") }}</span>
        <input v-model.number="timeoutSeconds" type="number" min="1" max="60" />
      </label>
      <button class="secondary compact" :disabled="running" @click="clearOutput">
        {{ t("panel.terminal.clear") }}
      </button>
    </div>

    <div class="console">
      <div v-for="entry in history" :key="entry.id" class="console-entry">
        <div class="prompt-line">
          <span>PS {{ entry.workingDirectory }}&gt;</span>
          <code>{{ entry.command }}</code>
        </div>
        <pre v-if="entry.output">{{ entry.output }}</pre>
        <pre v-if="entry.error" class="error-output">{{ entry.error }}</pre>
        <small>{{ t("panel.terminal.exitCode") }} {{ entry.exitCode }} · {{ entry.duration }} ms</small>
      </div>
      <p v-if="history.length === 0" class="console-empty">{{ t("panel.terminal.ready") }}</p>
    </div>

    <form class="command-row" @submit.prevent="execute">
      <span class="prompt-mark">&gt;_</span>
      <textarea
        v-model="command"
        rows="2"
        :placeholder="t('panel.terminal.commandHint')"
        @keydown.ctrl.enter.prevent="execute" />
      <button type="submit" :disabled="running || !command.trim()">
        {{ running ? t("panel.terminal.running") : t("panel.terminal.run") }}
      </button>
    </form>
  </section>
</template>

<script setup lang="ts">
import { ref } from "vue";
import { useI18n } from "vue-i18n";
import { executeTerminalCommand } from "../api/terminal";
import { extractApiMessage } from "../stores/auth";

type HistoryEntry = {
  id: number;
  command: string;
  workingDirectory: string;
  output: string;
  error: string;
  exitCode: number;
  duration: number;
};

const { t } = useI18n();
const command = ref("Get-ChildItem");
const workingDirectory = ref("%ProgramData%\\AdaPanel");
const timeoutSeconds = ref(30);
const running = ref(false);
const notice = ref("");
const noticeError = ref(false);
const history = ref<HistoryEntry[]>([]);

async function execute() {
  const submittedDraft = command.value;
  const pendingCommand = submittedDraft.trim();
  const submittedDirectory = workingDirectory.value;
  if (!pendingCommand || running.value) {
    return;
  }

  running.value = true;
  notice.value = "";
  try {
    const result = await executeTerminalCommand(pendingCommand, submittedDirectory, timeoutSeconds.value);
    if (workingDirectory.value === submittedDirectory) {
      workingDirectory.value = result.workingDirectory;
    }
    history.value.push({
      id: Date.now(),
      command: pendingCommand,
      workingDirectory: result.workingDirectory,
      output: result.standardOutput,
      error: [result.standardError, result.timedOut ? t("panel.terminal.timedOut") : ""].filter(Boolean).join("\n"),
      exitCode: result.exitCode,
      duration: result.durationMilliseconds
    });
    if (command.value === submittedDraft) {
      command.value = "";
    }
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    running.value = false;
  }
}

function clearOutput() {
  history.value = [];
  notice.value = "";
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
.terminal-panel {
  gap: 12px;
}

.terminal-toolbar,
.command-row {
  display: flex;
  gap: 10px;
  align-items: flex-end;
}

.shell-label {
  padding: 5px 9px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--accent-1) 12%, transparent);
  color: var(--accent-1);
  /* 0.72rem = 9.72px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
  font-weight: 750;
}

.terminal-toolbar label {
  display: grid;
  gap: 5px;
}

.terminal-toolbar .path-field {
  flex: 1;
}

.terminal-toolbar button,
.command-row button {
  width: auto;
}

.console {
  min-height: 320px;
  max-height: 58vh;
  overflow: auto;
  padding: 14px;
  border: 1px solid color-mix(in srgb, var(--accent-2) 28%, transparent);
  border-radius: var(--radius-md);
  /* Terminal yüzeyi BİLEREK her iki temada da koyu: base.css `.code-viewer` ve `pre` için aynı
     kararı veriyor (açık temada bile #161c28'e sabitliyor). Konsol çıktısı kabuk renk kodları
     taşır ve açık zeminde okunmaz; bu yüzden zemin belirtece bağlanMADI.
     Ama METİN belirteçliydi ve BOZUKTU: `--accent-text` açık temada #0f62c4'e (koyu mavi)
     döner ve bu neredeyse siyah zeminde ÖLÇÜLDÜ: 3.28:1 — AA'nın 4.5 eşiğinin altında.
     base.css'in `pre` düzeltmesi buraya ULAŞMIYOR, çünkü bu öge bir <div> (`pre` değil).
     Metin de zemin gibi sabitlendi: 10.34:1, koyu temanın bugünkü görünümü birebir aynı. */
  background: #0a0e16;
  color: #8fc1ff;
  font-family: "Cascadia Code", "Consolas", monospace;
  font-size: 0.8rem;
}

.console-entry {
  display: grid;
  gap: 6px;
  padding-bottom: 14px;
}

.prompt-line {
  display: flex;
  gap: 8px;
  color: var(--accent-1);
}

.prompt-line code {
  color: var(--text-strong);
  white-space: pre-wrap;
}

pre {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
}

.error-output {
  color: var(--danger-text);
}

.console small,
.console-empty {
  color: var(--text-muted);
}

.command-row {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
}

.prompt-mark {
  color: var(--accent-1);
  font-family: "Cascadia Code", "Consolas", monospace;
  font-weight: 800;
}

@media (max-width: 760px) {
  .terminal-toolbar,
  .command-row {
    display: grid;
    grid-template-columns: 1fr;
  }

  .terminal-toolbar button,
  .command-row button {
    width: 100%;
  }
}
</style>
