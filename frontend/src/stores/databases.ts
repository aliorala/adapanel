import { defineStore } from "pinia";
import { toRaw } from "vue";
import { CanceledError } from "axios";
import { getSessionEpoch } from "../api/client";
import {
  createDatabase,
  createDatabaseUser,
  importDatabase,
  deleteDatabase,
  grantDatabaseAccess,
  deleteDatabaseUser,
  setDatabaseUserPassword,
  getDatabaseEngines,
  getDatabaseWorkspace,
  registerDatabaseCredentials,
  type CreateDatabasePayload,
  type CreateDatabaseUserPayload,
  type ImportDatabasePayload,
  type DatabaseEngineStatus,
  type DatabaseWorkspace,
  type RegisterCredentialsPayload
} from "../api/databases";

type BusyKey = "loadingEngines" | "loadingEngine" | "creatingEngine" | "deletingKey" |
  "grantingKey" | "creatingUserEngine" | "deletingUserKey" | "registeringEngine";
type WorkspaceRequests = {
  readId: number;
  writeId: number;
  revision: number;
  credentialsId: number;
  credentialsRevision: number;
};
type Requests = {
  epoch: number;
  sequence: number;
  enginesReadId: number;
  workspaces: Map<string, WorkspaceRequests>;
  busy: Map<BusyKey, number>;
};
type Request = { id: number; requests: Requests };
type WorkspaceWrite = Request & { scope: WorkspaceRequests; revision: number };
const requestsByStore = new WeakMap<object, Requests>();

function beginRequest(store: object, busy?: BusyKey): Request {
  const epoch = getSessionEpoch();
  const key = toRaw(store);
  let requests = requestsByStore.get(key);
  if (!requests || requests.epoch !== epoch) {
    requests = { epoch, sequence: 0, enginesReadId: 0, workspaces: new Map(), busy: new Map() };
    requestsByStore.set(key, requests);
  }
  const id = ++requests.sequence;
  if (busy) requests.busy.set(busy, id);
  return { id, requests };
}

function assertCurrentSession(request: Request): void {
  // The response interceptor can accept a response just before a session reset.
  if (request.requests.epoch !== getSessionEpoch()) throw new CanceledError("Session changed");
}

function ownsBusy(request: Request, busy: BusyKey): boolean {
  return request.requests.epoch === getSessionEpoch() && request.requests.busy.get(busy) === request.id;
}

function workspaceRequests(request: Request, engine: string): WorkspaceRequests {
  let scope = request.requests.workspaces.get(engine);
  if (!scope) {
    scope = { readId: 0, writeId: 0, revision: 0, credentialsId: 0, credentialsRevision: 0 };
    request.requests.workspaces.set(engine, scope);
  }
  return scope;
}

function beginWorkspaceWrite(store: object, engine: string, busy?: BusyKey): WorkspaceWrite {
  const request = beginRequest(store, busy);
  const scope = workspaceRequests(request, engine);
  scope.writeId = request.id;
  return { ...request, scope, revision: ++scope.revision };
}

async function applyWorkspaceWrite(
  store: { workspaces: Record<string, DatabaseWorkspace>; fetchWorkspace(engine: string): Promise<DatabaseWorkspace> },
  request: WorkspaceWrite,
  engine: string,
  workspace: DatabaseWorkspace
): Promise<DatabaseWorkspace> {
  assertCurrentSession(request);
  const unchanged = request.scope.writeId === request.id && request.scope.revision === request.revision;
  // Invalidate reads started before OR during this write. Overlapping writes may
  // have produced their snapshots in either order, so reconcile from the server.
  request.scope.revision++;
  if (unchanged) store.workspaces[engine] = workspace;
  else await store.fetchWorkspace(engine);
  assertCurrentSession(request);
  return workspace;
}

export const useDatabasesStore = defineStore("databases", {
  state: () => ({
    selectedEngine: "mariadb",
    engines: [] as DatabaseEngineStatus[],
    workspaces: {} as Record<string, DatabaseWorkspace>,
    loadingEngines: false,
    loadingEngine: "",
    creatingEngine: "",
    deletingKey: "",
    grantingKey: "",
    creatingUserEngine: "",
    deletingUserKey: "",
    registeringEngine: ""
  }),
  getters: {
    currentWorkspace: (state) => state.workspaces[state.selectedEngine] ?? null,
    currentEngineStatus: (state) =>
      state.engines.find((item) => item.engine === state.selectedEngine) ?? null
  },
  actions: {
    selectEngine(engine: string) {
      this.selectedEngine = engine;
    },
    async fetchEngines() {
      const request = beginRequest(this, "loadingEngines");
      request.requests.enginesReadId = request.id;
      const revisions = new Map([...request.requests.workspaces].map(([engine, scope]) => [engine, scope.credentialsRevision]));
      this.loadingEngines = true;

      try {
        const engines = await getDatabaseEngines();
        assertCurrentSession(request);
        if (request.requests.enginesReadId === request.id) {
          const statuses = new Map(engines.map(status => [status.engine, status]));
          // Preserve changed credentials without dropping fresh statuses for other engines.
          for (const [engine, scope] of request.requests.workspaces) {
            if (scope.credentialsRevision === (revisions.get(engine) ?? 0)) continue;
            const current = this.engines.find(status => status.engine === engine);
            if (current) statuses.set(engine, current);
            else statuses.delete(engine);
          }
          this.engines = [...statuses.values()];
        }
        return engines;
      } finally {
        if (ownsBusy(request, "loadingEngines")) this.loadingEngines = false;
      }
    },
    async registerCredentials(payload: RegisterCredentialsPayload) {
      const input = { ...payload };
      const request = beginWorkspaceWrite(this, input.engine, "registeringEngine");
      request.scope.credentialsId = request.id;
      const revision = ++request.scope.credentialsRevision;
      this.registeringEngine = input.engine;

      try {
        const status = await registerDatabaseCredentials(input);
        assertCurrentSession(request);
        const unchanged = request.scope.credentialsId === request.id && request.scope.credentialsRevision === revision;
        request.scope.credentialsRevision++;
        request.scope.revision++;
        if (unchanged) {
          const index = this.engines.findIndex((item) => item.engine === status.engine);
          if (index >= 0) this.engines[index] = status;
          else this.engines.push(status);
        } else {
          await this.fetchEngines();
        }
        assertCurrentSession(request);
        await this.fetchWorkspace(input.engine);
        assertCurrentSession(request);
        return status;
      } finally {
        if (ownsBusy(request, "registeringEngine")) {
          this.registeringEngine = "";
        }
      }
    },
    async fetchWorkspace(engine: string) {
      const request = beginRequest(this, "loadingEngine");
      const scope = workspaceRequests(request, engine);
      scope.readId = request.id;
      const revision = scope.revision;
      this.loadingEngine = engine;

      try {
        const workspace = await getDatabaseWorkspace(engine);
        assertCurrentSession(request);
        if (scope.readId === request.id && scope.revision === revision) this.workspaces[engine] = workspace;
        return workspace;
      } finally {
        if (ownsBusy(request, "loadingEngine")) {
          this.loadingEngine = "";
        }
      }
    },
    async createDatabase(payload: CreateDatabasePayload) {
      const input = { ...payload };
      const request = beginWorkspaceWrite(this, input.engine, "creatingEngine");
      this.creatingEngine = input.engine;

      try {
        const workspace = await createDatabase(input);
        return await applyWorkspaceWrite(this, request, input.engine, workspace);
      } finally {
        if (ownsBusy(request, "creatingEngine")) {
          this.creatingEngine = "";
        }
      }
    },
    async deleteDatabase(engine: string, name: string) {
      const request = beginWorkspaceWrite(this, engine, "deletingKey");
      this.deletingKey = `${engine}:${name}`;

      try {
        await deleteDatabase(engine, name);
        assertCurrentSession(request);
        request.scope.revision++;
        await this.fetchWorkspace(engine);
        assertCurrentSession(request);
      } finally {
        if (ownsBusy(request, "deletingKey")) this.deletingKey = "";
      }
    },
    /**
     * Var olan bir kullaniciya bir veritabani uzerinde yetki verir.
     *
     * Panelde bir veritabanina SONRADAN kullanici baglamanin arayuzde karsiligi yoktu; disaridan
     * gelmis ya da kullanicisi silinmis bir veritabani kullanicisiz kaliyor ve o hâliyle hicbir
     * uygulama ona baglanamiyordu.
     */
    async grantAccess(engine: string, databaseName: string, userName: string, allowedHost: string) {
      const request = beginWorkspaceWrite(this, engine, "grantingKey");
      this.grantingKey = `${engine}:${databaseName}`;

      try {
        const workspace = await grantDatabaseAccess({ engine, databaseName, userName, allowedHost });
        return await applyWorkspaceWrite(this, request, engine, workspace);
      } finally {
        if (ownsBusy(request, "grantingKey")) this.grantingKey = "";
      }
    },
    async createUser(payload: CreateDatabaseUserPayload) {
      const input = { ...payload };
      const request = beginWorkspaceWrite(this, input.engine, "creatingUserEngine");
      this.creatingUserEngine = input.engine;

      try {
        const workspace = await createDatabaseUser(input);
        return await applyWorkspaceWrite(this, request, input.engine, workspace);
      } finally {
        if (ownsBusy(request, "creatingUserEngine")) {
          this.creatingUserEngine = "";
        }
      }
    },
    async importDatabase(payload: ImportDatabasePayload) {
      const input = { ...payload };
      const request = beginWorkspaceWrite(this, input.engine);
      const workspace = await importDatabase(input);
      return applyWorkspaceWrite(this, request, input.engine, workspace);
    },
    async deleteUser(engine: string, userName: string, host: string) {
      const request = beginWorkspaceWrite(this, engine, "deletingUserKey");
      this.deletingUserKey = `${engine}:${userName}:${host}`;

      try {
        await deleteDatabaseUser(engine, userName, host);
        assertCurrentSession(request);
        request.scope.revision++;
        await this.fetchWorkspace(engine);
        assertCurrentSession(request);
      } finally {
        if (ownsBusy(request, "deletingUserKey")) this.deletingUserKey = "";
      }
    },
    async setUserPassword(engine: string, userName: string, password: string, allowedHost?: string) {
      const request = beginWorkspaceWrite(this, engine);
      const workspace = await setDatabaseUserPassword({ engine, userName, password, allowedHost });
      return applyWorkspaceWrite(this, request, engine, workspace);
    }
  }
});
