import { defineStore } from "pinia";
import {
  createDatabase,
  createDatabaseUser,
  deleteDatabase,
  deleteDatabaseUser,
  setDatabaseUserPassword,
  getDatabaseEngines,
  getDatabaseWorkspace,
  registerDatabaseCredentials,
  type CreateDatabasePayload,
  type CreateDatabaseUserPayload,
  type DatabaseEngineStatus,
  type DatabaseWorkspace,
  type RegisterCredentialsPayload
} from "../api/databases";

export const useDatabasesStore = defineStore("databases", {
  state: () => ({
    selectedEngine: "mariadb",
    engines: [] as DatabaseEngineStatus[],
    workspaces: {} as Record<string, DatabaseWorkspace>,
    loadingEngines: false,
    loadingEngine: "",
    creatingEngine: "",
    deletingKey: "",
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
      this.loadingEngines = true;

      try {
        this.engines = await getDatabaseEngines();
        return this.engines;
      } finally {
        this.loadingEngines = false;
      }
    },
    async registerCredentials(payload: RegisterCredentialsPayload) {
      this.registeringEngine = payload.engine;

      try {
        const status = await registerDatabaseCredentials(payload);
        const index = this.engines.findIndex((item) => item.engine === status.engine);
        if (index >= 0) {
          this.engines[index] = status;
        } else {
          this.engines.push(status);
        }
        await this.fetchWorkspace(payload.engine);
        return status;
      } finally {
        if (this.registeringEngine === payload.engine) {
          this.registeringEngine = "";
        }
      }
    },
    async fetchWorkspace(engine: string) {
      this.loadingEngine = engine;

      try {
        const workspace = await getDatabaseWorkspace(engine);
        this.workspaces[engine] = workspace;
        return workspace;
      } finally {
        if (this.loadingEngine === engine) {
          this.loadingEngine = "";
        }
      }
    },
    async createDatabase(payload: CreateDatabasePayload) {
      this.creatingEngine = payload.engine;

      try {
        const workspace = await createDatabase(payload);
        this.workspaces[payload.engine] = workspace;
        return workspace;
      } finally {
        if (this.creatingEngine === payload.engine) {
          this.creatingEngine = "";
        }
      }
    },
    async deleteDatabase(engine: string, name: string) {
      this.deletingKey = `${engine}:${name}`;

      try {
        await deleteDatabase(engine, name);
        await this.fetchWorkspace(engine);
      } finally {
        this.deletingKey = "";
      }
    },
    async createUser(payload: CreateDatabaseUserPayload) {
      this.creatingUserEngine = payload.engine;

      try {
        const workspace = await createDatabaseUser(payload);
        this.workspaces[payload.engine] = workspace;
        return workspace;
      } finally {
        if (this.creatingUserEngine === payload.engine) {
          this.creatingUserEngine = "";
        }
      }
    },
    async deleteUser(engine: string, userName: string, host: string) {
      this.deletingUserKey = `${engine}:${userName}:${host}`;

      try {
        await deleteDatabaseUser(engine, userName, host);
        await this.fetchWorkspace(engine);
      } finally {
        this.deletingUserKey = "";
      }
    },
    async setUserPassword(engine: string, userName: string, password: string, allowedHost?: string) {
      const workspace = await setDatabaseUserPassword({ engine, userName, password, allowedHost });
      this.workspaces[engine] = workspace;
      return workspace;
    }
  }
});
