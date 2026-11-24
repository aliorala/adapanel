import { describe, expect, it } from "vitest";
import { taskCommandDisplay, taskUrlDisplay } from "./taskUrlDisplay";

describe("scheduled task URL presentation", () => {
  it("leaves a plain URL intact", () => {
    expect(taskUrlDisplay("https://app.example/job")).toBe("https://app.example/job");
  });
  it("omits credentials, query and fragment values without changing the saved URL", () => {
    const saved = "https://user:secret@app.example/job?custom=private#sensitive";
    expect(taskUrlDisplay(saved)).toBe("https://app.example/job?•••#•••");
    expect(saved).toContain("custom=private");
  });
  it("does not display raw runner arguments from legacy tasks", () => {
    expect(taskUrlDisplay('-url "https://app.example/?token=fixture"')).toBe("•••");
  });
  it("masks private URLs embedded in executable arguments", () => {
    expect(taskCommandDisplay('fetch "https://app.example/run?key=fixture"')).toBe('fetch "https://app.example/run?•••"');
  });
});
