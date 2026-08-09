<template>
  <!--
    Sayfalama çubuğu — TEK KAYNAK (bkz. composables/usePagination.ts).
    Liste tek sayfaya sığıyorsa hiç çizilmez: kullanıcı olmayan bir sayfa arayışına girmesin.
  -->
  <div v-if="total > size" class="row-actions table-pager">
    <button type="button" class="secondary compact" :disabled="disabled || !hasPrevious" @click="state.go(-1)">
      {{ t("panel.pager.previous") }}
    </button>

    <span class="meta pager-range" role="status" aria-live="polite">
      {{ t("panel.pager.range", { from, to, total }) }}
    </span>

    <button type="button" class="secondary compact" :disabled="disabled || !hasNext" @click="state.go(1)">
      {{ t("panel.pager.next") }}
    </button>

    <label class="pager-size">
      <span class="meta">{{ t("panel.pager.pageSize") }}</span>
      <select v-model.number="size" :disabled="disabled">
        <option v-for="option in PAGE_SIZE_OPTIONS" :key="option" :value="option">{{ option }}</option>
      </select>
    </label>
  </div>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { useI18n } from "vue-i18n";
import { PAGE_SIZE_OPTIONS, type PaginationState } from "../composables/usePagination";

const props = withDefaults(
  defineProps<{ state: PaginationState<unknown>; disabled?: boolean }>(),
  { disabled: false }
);

const { t } = useI18n();

// Prop olarak gelen composable nesnesindeki ref'ler şablonda kendiliğinden açılmaz; tek yerde açılır.
const total = computed(() => props.state.total.value);
const from = computed(() => props.state.from.value);
const to = computed(() => props.state.to.value);
const hasPrevious = computed(() => props.state.hasPrevious.value);
const hasNext = computed(() => props.state.hasNext.value);
const size = computed({
  get: () => props.state.size.value,
  set: (value: number) => {
    props.state.size.value = value;
    // Sayfa boyutu değişince başa dönülür: 3. sayfadayken 25→100 seçmek kullanıcıyı listenin
    // ortasına düşürüyordu ve neyi kaçırdığı belli olmuyordu.
    props.state.reset();
  }
});
</script>

<style scoped>
.table-pager {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  flex-wrap: wrap;
  padding-top: 4px;
}

.table-pager button {
  width: auto;
}

.pager-range {
  font-variant-numeric: tabular-nums;
}

.pager-size {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.pager-size select {
  width: auto;
  min-width: 72px;
}
</style>
