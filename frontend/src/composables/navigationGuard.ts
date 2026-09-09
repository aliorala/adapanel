import { onBeforeUnmount } from "vue";

type Guard = () => boolean | Promise<boolean>;
const guards = new Set<Guard>();
let pending: Promise<boolean> | null = null;
export function registerNavigationGuard(guard: Guard) {
  guards.add(guard);
  return () => { guards.delete(guard); };
}
export function useNavigationGuard(guard: Guard) {
  onBeforeUnmount(registerNavigationGuard(guard));
}
export function allowNavigation(): Promise<boolean> {
  if (pending) return Promise.resolve(false);
  const current = [...guards];
  function check(index: number): boolean | Promise<boolean> {
    if (index === current.length) return true;
    const result = current[index]();
    if (typeof result === "boolean") return result && check(index + 1);
    return result.then((allowed) => allowed ? check(index + 1) : false);
  }
  const result = check(0);
  // Unmodified views never lock out a simultaneous history/context update.
  if (typeof result === "boolean") return Promise.resolve(result);
  pending = result.finally(() => { pending = null; });
  return pending;
}
