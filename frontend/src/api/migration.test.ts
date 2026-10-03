import { afterEach, expect, it, vi } from "vitest";
import axios from "axios";
import { apiClient, invalidateSessionRequests, setReadOnlyMode, setSessionExpiredHandler } from "./client";
import { migrateSiteStream, type MigrationProgress, type MigrateSitePayload } from "./migration";

const originalBaseUrl = apiClient.defaults.baseURL;
afterEach(() => {
  vi.restoreAllMocks(); vi.unstubAllGlobals();
  apiClient.defaults.baseURL = originalBaseUrl;
  setSessionExpiredHandler(null); invalidateSessionRequests();
});
const payload = { siteName: "test.example" } as MigrateSitePayload;
const complete = 'data: {"phase":"done","overallSuccess":true}\r\n\r\n';

function response(chunks: Uint8Array[]) {
  const stream = new ReadableStream<Uint8Array>({ start(controller) {
    chunks.forEach(chunk => controller.enqueue(chunk)); controller.close();
  } });
  vi.spyOn(apiClient, "post").mockResolvedValue({ data: stream });
  return stream;
}

it("reads CRLF and UTF-8 split at every possible byte boundary", async () => {
  const text = ': keep-alive\r\n\r\ndata: {"phase":"result","step":"files","success":true,"detail":"Taşındı ✓"}\r\n\r\n' + complete;
  const bytes = new TextEncoder().encode(text);
  for (let split = 1; split < bytes.length; split++) {
    const stream = response([bytes.slice(0, split), bytes.slice(split)]);
    const events: MigrationProgress[] = [];
    await migrateSiteStream(payload, event => events.push(event));
    expect(events.map(event => event.phase)).toEqual(["result", "done"]);
    expect(events[0].detail).toBe("Taşındı ✓");
    expect(stream.locked).toBe(false);
  }
});

it("accepts multiline data and reports a partial result as partial", async () => {
  response([new TextEncoder().encode('data: {"phase":"done",\ndata: "overallSuccess":false}\n\n')]);
  const received = vi.fn();
  await migrateSiteStream(payload, received);
  expect(received).toHaveBeenCalledWith({ phase: "done", overallSuccess: false });
  expect(apiClient.post).toHaveBeenCalledWith("/migration/site/stream", payload, expect.objectContaining({ adapter: "fetch", responseType: "stream" }));
});

it.each(["", 'data: {"phase":"start","step":"files"}\n\n', 'data: {"phase":"done","overallSuccess":true}', 'data: broken\n\n', 'data: {"phase":"done"}\n\n'])
  ("rejects incomplete or corrupt streams instead of silently finishing: %s", async text => {
    const stream = response([new TextEncoder().encode(text)]);
    await expect(migrateSiteStream(payload, vi.fn())).rejects.toMatchObject({ messageKey: "panel.migration.streamFailed" });
    expect(stream.locked).toBe(false);
  });

it("does not swallow a callback failure", async () => {
  response([new TextEncoder().encode(complete)]);
  const failure = new Error("render failed");
  await expect(migrateSiteStream(payload, () => { throw failure; })).rejects.toBe(failure);
});

it("does not deliver data from an obsolete session", async () => {
  const stream = response([new TextEncoder().encode(complete)]);
  const received = vi.fn();
  const result = migrateSiteStream(payload, received).catch(error => error);
  invalidateSessionRequests();
  expect(axios.isCancel(await result)).toBe(true);
  expect(received).not.toHaveBeenCalled();
  expect(stream.locked).toBe(false);
});

function mockTransport(status = 200) {
  invalidateSessionRequests();
  apiClient.defaults.baseURL = "https://panel.test/api/v1";
  vi.stubGlobal("window", { location: { pathname: "/secret-entry" } });
  const fetcher = vi.fn(async (request: Request | string) => {
    if (typeof request === "string" && request.endsWith("/csrf")) {
      return new Response(JSON.stringify({ success: true, data: { requestToken: "csrf-proof" } }));
    }
    return new Response(status === 200 ? complete : JSON.stringify({ message: "auth.unauthorized" }), {
      status, headers: { "Content-Type": status === 200 ? "text/event-stream" : "application/json" }
    });
  });
  vi.stubGlobal("fetch", fetcher);
  return fetcher;
}

it("uses the real fetch adapter with CSRF, secure entry headers and credentials", async () => {
  const transport = mockTransport();
  const received = vi.fn();
  await migrateSiteStream(payload, received);
  const request = transport.mock.calls.map(call => call[0]).find(item => item instanceof Request) as Request;
  expect(request.method).toBe("POST");
  expect(request.headers.get("X-CSRF-TOKEN")).toBe("csrf-proof");
  expect(request.headers.get("X-AdaPanel-Entry-Path")).toBe("secret-entry");
  expect(request.credentials).toBe("include");
  expect(received).toHaveBeenCalledWith({ phase: "done", overallSuccess: true });
});

it("does not dispatch a stream mutation for a suspended subscription", async () => {
  const transport = mockTransport();
  setReadOnlyMode("subscription.suspended_readonly");
  await expect(migrateSiteStream(payload, vi.fn())).rejects.toMatchObject({ response: { status: 403 } });
  expect(transport).not.toHaveBeenCalled();
});

it("returns to the login flow when the stream endpoint rejects an expired session", async () => {
  mockTransport(401);
  const expired = vi.fn(); setSessionExpiredHandler(expired);
  await expect(migrateSiteStream(payload, vi.fn())).rejects.toMatchObject({ response: { status: 401 } });
  expect(expired).toHaveBeenCalledOnce();
});

it("preserves the server error envelope when the streaming endpoint refuses to start", async () => {
  mockTransport(409);
  await expect(migrateSiteStream(payload, vi.fn())).rejects.toMatchObject({
    response: { status: 409, data: { message: "auth.unauthorized" } }
  });
});
