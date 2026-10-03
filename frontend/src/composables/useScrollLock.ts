import { onScopeDispose, watch } from "vue";

const locks = new Set<symbol>();
let previousOverflow = "";

/** İç içe pencereler kapanırken, hâlâ açık olan pencerenin kaydırma kilidini korur. */
export function useScrollLock(isOpen: () => boolean) {
  const owner = Symbol("scroll-lock");
  function release() {
    if (locks.delete(owner) && locks.size === 0) document.body.style.overflow = previousOverflow;
  }
  watch(isOpen, open => {
    if (!open) { release(); return; }
    if (locks.size === 0) previousOverflow = document.body.style.overflow;
    locks.add(owner);
    document.body.style.overflow = "hidden";
  }, { immediate: true, flush: "sync" });
  onScopeDispose(release);
}
