export function abortableDelay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const abort = () => { clearTimeout(timer); signal.removeEventListener("abort", abort); reject(new DOMException("Aborted", "AbortError")); };
    const timer = setTimeout(() => { signal.removeEventListener("abort", abort); resolve(); }, ms);
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) abort();
  });
}

/** Connectivity failure never changes the last known server-side operation status. */
export async function pollProgress<T>(options: {
  signal: AbortSignal;
  fetch: () => Promise<T>;
  receive: (value: T) => void;
  complete: (value: T) => boolean;
  connection: (failures: number) => void;
  interval?: number;
}): Promise<"complete" | "paused" | "aborted"> {
  let failures = 0;
  while (!options.signal.aborted) {
    try {
      const progress = await options.fetch();
      if (options.signal.aborted) return "aborted";
      failures = 0;
      options.connection(0);
      options.receive(progress);
      if (options.complete(progress)) return "complete";
    } catch {
      if (options.signal.aborted) return "aborted";
      options.connection(++failures);
      if (failures >= 3) return "paused";
    }
    try { await abortableDelay(options.interval ?? 1500, options.signal); }
    catch { return "aborted"; }
  }
  return "aborted";
}
