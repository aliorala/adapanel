import { afterEach, expect, it, vi } from "vitest";
import { effectScope, ref } from "vue";
import { useScrollLock } from "./useScrollLock";

afterEach(() => vi.unstubAllGlobals());
it("keeps a parent dialog locked when a nested dialog closes and restores the original style", () => {
  const style = { overflow: "auto" };
  vi.stubGlobal("document", { body: { style } });
  const parent = ref(false), child = ref(false);
  const a = effectScope(), b = effectScope();
  a.run(() => useScrollLock(() => parent.value));
  b.run(() => useScrollLock(() => child.value));
  try {
    parent.value = true; child.value = true;
    child.value = false;
    expect(style.overflow).toBe("hidden");
    parent.value = false;
    expect(style.overflow).toBe("auto");
  } finally { a.stop(); b.stop(); }
});

it("releases an open dialog on unmount without unlocking another one", () => {
  const style = { overflow: "" };
  vi.stubGlobal("document", { body: { style } });
  const a = effectScope(), b = effectScope();
  a.run(() => useScrollLock(() => true));
  b.run(() => useScrollLock(() => true));
  a.stop(); expect(style.overflow).toBe("hidden");
  b.stop(); expect(style.overflow).toBe("");
});
