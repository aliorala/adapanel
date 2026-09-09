import { describe, expect, it, vi } from "vitest";
import { allowNavigation, registerNavigationGuard } from "./navigationGuard";

describe("navigation decisions", () => {
  it("does not block concurrent context and history updates in an unmodified view", async () => {
    const remove = registerNavigationGuard(() => true);
    try { expect(await Promise.all([allowNavigation(), allowNavigation()])).toEqual([true, true]); }
    finally { remove(); }
  });
  it("blocks another destination while a save/discard decision is pending", async () => {
    let decide!: (value: boolean) => void;
    const remove = registerNavigationGuard(() => new Promise<boolean>((resolve) => { decide = resolve; }));
    try {
      const first = allowNavigation();
      expect(await allowNavigation()).toBe(false);
      decide(false);
      expect(await first).toBe(false);
    } finally { remove(); }
  });
  it("cannot leave while saving and removes the guard when its view is disposed", async () => {
    const guard = vi.fn(() => false);
    const remove = registerNavigationGuard(guard);
    expect(await allowNavigation()).toBe(false);
    remove();
    expect(await allowNavigation()).toBe(true);
    expect(guard).toHaveBeenCalledOnce();
  });
});
