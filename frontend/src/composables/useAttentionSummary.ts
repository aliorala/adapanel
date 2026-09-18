import { onScopeDispose, ref } from "vue";
import { getAttentionSummary, type AttentionSummary } from "../api/attention";

/** Background polling and the drawer share one request; an old session cannot restore its data. */
export function useAttentionSummary(
  describeError: (error: unknown) => string,
  fetchSummary: (signal: AbortSignal) => Promise<AttentionSummary> = getAttentionSummary
) {
  const summary = ref<AttentionSummary | null>(null);
  const loading = ref(false);
  const error = ref("");
  let pending: { controller: AbortController; showErrors: boolean; promise: Promise<void> } | null = null;

  function refresh(silent = true): Promise<void> {
    if (pending) {
      pending.showErrors ||= !silent;
      return pending.promise;
    }
    const request = { controller: new AbortController(), showErrors: !silent, promise: Promise.resolve() };
    pending = request;
    loading.value = true;
    if (!silent) error.value = "";
    request.promise = (async () => {
      try {
        const result = await fetchSummary(request.controller.signal);
        if (pending !== request) return;
        summary.value = result;
        error.value = "";
      } catch (cause) {
        if (pending === request && request.showErrors && !request.controller.signal.aborted)
          error.value = describeError(cause);
      } finally {
        if (pending === request) {
          pending = null;
          loading.value = false;
        }
      }
    })();
    return request.promise;
  }

  function reset() {
    const previous = pending;
    pending = null;
    previous?.controller.abort();
    summary.value = null;
    error.value = "";
    loading.value = false;
  }
  onScopeDispose(reset);
  return { summary, loading, error, refresh, reset };
}
