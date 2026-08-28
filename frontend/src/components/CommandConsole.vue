<template>
  <div class="cmd-console">
    <div class="cmd-head">
      <div>
        <h4>{{ title }}</h4>
        <p v-if="hint" class="meta">{{ hint }}</p>
      </div>
      <button v-if="lines.length" class="secondary compact" :disabled="busy" @click="clear">
        {{ t("panel.console.clear") }}
      </button>
    </div>

    <div v-if="presets.length" class="cmd-presets">
      <button
        v-for="preset in presets"
        :key="preset"
        type="button"
        class="preset-chip"
        :disabled="busy"
        @click="usePreset(preset)">
        {{ preset }}
      </button>
    </div>

    <pre ref="outputEl" class="cmd-output">{{ lines.length ? lines.join("\n") : t("panel.console.empty") }}</pre>

    <div class="cmd-input-row">
      <span class="cmd-prompt">$</span>
      <input
        v-model="command"
        class="cmd-input"
        :placeholder="placeholder"
        :disabled="busy"
        spellcheck="false"
        autocapitalize="off"
        autocomplete="off"
        @keyup.enter="run" />
      <button class="compact" v-bind="writeGate(busy || !command.trim())" @click="run">
        {{ busy ? t("panel.console.running") : t("panel.console.run") }}
      </button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { nextTick, ref } from "vue";
import { useI18n } from "vue-i18n";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const props = defineProps<{
  title: string;
  placeholder: string;
  hint?: string;
  presets?: string[];
  runner: (command: string) => Promise<{ exitCode: number; output: string; error: string }>;
}>();

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const command = ref("");
const busy = ref(false);
const lines = ref<string[]>([]);
const outputEl = ref<HTMLElement | null>(null);
const presets = props.presets ?? [];

function usePreset(preset: string) {
  command.value = preset;
}

async function run() {
  const cmd = command.value.trim();
  if (!cmd || busy.value) {
    return;
  }
  busy.value = true;
  append(`$ ${displayPrefix.value}${cmd}`);
  try {
    const result = await props.runner(cmd);
    const body = [result.output, result.error].filter(Boolean).join("\n").trimEnd();
    if (body) {
      append(body);
    }
    append(result.exitCode === 0
      ? `✓ ${t("panel.console.exit", { code: result.exitCode })}`
      : `✗ ${t("panel.console.exit", { code: result.exitCode })}`);
  } catch (error) {
    const key = extractApiMessage(error);
    const translated = t(key);
    const message = translated === key ? key : translated;
    const details = extractApiDetails(error);
    append(`✗ ${details ? `${message}\n${details}` : message}`);
  } finally {
    busy.value = false;
    command.value = "";
    await nextTick();
    if (outputEl.value) {
      outputEl.value.scrollTop = outputEl.value.scrollHeight;
    }
  }
}

// Komut satırında gösterilecek önek (ör. "php artisan ", "composer ", "npm ").
const displayPrefix = ref("");

function append(text: string) {
  lines.value.push(text);
}

function clear() {
  lines.value = [];
}
</script>

<style scoped>
.cmd-console {
  display: grid;
  gap: 10px;
}

.cmd-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
}

.cmd-head h4 {
  margin: 0 0 2px;
}

.cmd-presets {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.preset-chip {
  width: auto;
  padding: 5px 10px;
  border: 1px solid var(--panel-border);
  border-radius: 999px;
  background: var(--surface-2);
  color: var(--text-soft);
  font-size: 0.76rem;
  font-family: var(--font-mono, ui-monospace, monospace);
  cursor: pointer;
}

.preset-chip:hover {
  border-color: color-mix(in srgb, var(--accent-1) 40%, transparent);
  color: var(--accent-1);
}

.cmd-output {
  margin: 0;
  min-height: 220px;
  max-height: 440px;
  overflow: auto;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-radius: 10px;
  /* Koyu konsol yüzeyi BİLEREK sabit (bkz. base.css `pre` kuralı; bu öge bir <pre>, yani
     açık temada base.css zaten devralıyor). Kabuk çıktısı açık zeminde okunmaz. */
  background: #0b0f17;
  color: #d6e2f0;
  font-family: var(--font-mono, ui-monospace, "Cascadia Code", Consolas, monospace);
  font-size: 0.82rem;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.cmd-input-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.cmd-prompt {
  color: var(--accent-1);
  font-family: var(--font-mono, ui-monospace, monospace);
  font-weight: 800;
}

.cmd-input {
  flex: 1 1 auto;
  border-radius: var(--radius-sm);
  border: 1px solid var(--panel-border);
  padding: 11px 13px;
  background: var(--input-bg);
  color: var(--text-strong);
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 0.84rem;
}

.cmd-input-row button {
  width: auto;
  min-width: 96px;
}
</style>
