import { defineStore } from "pinia";
import { toRaw } from "vue";
import { CanceledError } from "axios";
import { getPhpVersions, installPhpVersion, setSitePhpVersion, type PhpVersionRecord, type SitePhpAssignment } from "../api/php";
import { getSessionEpoch } from "../api/client";
import { UserFacingError } from "../utils/apiError";

export class PhpInstallationRefreshError extends UserFacingError {
  constructor(public readonly installedMessageKey: string, public readonly refreshError: unknown) {
    super("panel.software.refreshAfterInstallFailed");
  }
}

type BusyKey = "loading" | "installingComponentId" | "assigningComponentId";
type Requests = { epoch: number; sequence: number; readId: number; assignmentId: number; revision: number; busy: Map<BusyKey, number> };
type Request = { id: number; requests: Requests; revision: number };
const requestsByStore = new WeakMap<object, Requests>();

function beginRequest(store: object, busy?: BusyKey): Request {
  const epoch = getSessionEpoch(), key = toRaw(store);
  let requests = requestsByStore.get(key);
  if (!requests || requests.epoch !== epoch) {
    requests = { epoch, sequence: 0, readId: 0, assignmentId: 0, revision: 0, busy: new Map() };
    requestsByStore.set(key, requests);
  }
  const id = ++requests.sequence;
  if (busy) requests.busy.set(busy, id);
  if (busy === "loading") requests.readId = id;
  if (busy === "assigningComponentId") requests.assignmentId = id;
  if (busy === "installingComponentId") requests.revision++;
  return { id, requests, revision: requests.revision };
}

function assertCurrentSession(request: Request): void {
  // A reset can occur after the HTTP interceptor accepted the response.
  if (request.requests.epoch !== getSessionEpoch()) throw new CanceledError("Session changed");
}

function ownsBusy(request: Request, busy: BusyKey): boolean {
  return request.requests.epoch === getSessionEpoch() && request.requests.busy.get(busy) === request.id;
}

export const usePhpStore = defineStore("php", {
  state: () => ({
    versions: [] as PhpVersionRecord[],
    lastAssignment: null as SitePhpAssignment | null,
    loading: false,
    // Last known rows remain visible after errors, but only a current successful
    // measurement opens writes. A failed refresh must close that gate again.
    loaded: false,
    installingComponentId: "",
    assigningComponentId: ""
  }),
  actions: {
    invalidateVersions() {
      // Mutations outside this store (such as software removal) invalidate all
      // reads already in flight. The actual reader still owns its loading flag.
      const request = beginRequest(this);
      request.requests.revision++;
      request.requests.readId = request.id;
      this.loaded = false;
    },
    async fetchVersions() {
      const request = beginRequest(this, "loading");
      this.loading = true;
      this.loaded = false;

      try {
        const versions = await getPhpVersions();
        assertCurrentSession(request);
        if (request.requests.readId === request.id && request.requests.revision === request.revision) {
          this.versions = versions;
          this.loaded = true;
        }
        return versions;
      } catch (error) {
        assertCurrentSession(request);
        throw error;
      } finally {
        if (ownsBusy(request, "loading")) this.loading = false;
      }
    },
    async installVersion(componentId: string) {
      const request = beginRequest(this, "installingComponentId");
      this.installingComponentId = componentId;
      this.loaded = false;

      try {
        const message = await installPhpVersion(componentId).catch(async error => {
          assertCurrentSession(request);
          request.requests.revision++;
          // A failed native installation can still have changed its inventory.
          // Preserve its primary failure if this one reconciliation read fails.
          await this.fetchVersions().catch(() => undefined);
          assertCurrentSession(request);
          throw error;
        });
        assertCurrentSession(request);
        // Reads started before/during the install cannot undo its final scan.
        request.requests.revision++;
        try { await this.fetchVersions(); }
        catch (error) {
          assertCurrentSession(request);
          throw new PhpInstallationRefreshError(message, error);
        }
        assertCurrentSession(request);
        // Another scan or external mutation may have superseded this scan while
        // it waited. Only the currently committed inventory can confirm the
        // requested component; a valid missing/failed row stays visible as such.
        if (!this.loaded || !this.versions.some(version =>
          version.componentId.trim().toLowerCase() === componentId.trim().toLowerCase() && version.status.toLowerCase() === "installed")) {
          throw new PhpInstallationRefreshError(message, new UserFacingError("panel.php.installationUnconfirmed"));
        }
        return message;
      } catch (error) {
        assertCurrentSession(request);
        throw error;
      } finally {
        if (ownsBusy(request, "installingComponentId")) this.installingComponentId = "";
      }
    },
    async assignVersion(siteName: string, componentId: string) {
      const request = beginRequest(this, "assigningComponentId");
      this.assigningComponentId = componentId;

      try {
        const assignment = await setSitePhpVersion(siteName, componentId);
        assertCurrentSession(request);
        if (request.requests.assignmentId === request.id) this.lastAssignment = assignment;
        return assignment;
      } catch (error) {
        assertCurrentSession(request);
        throw error;
      } finally {
        if (ownsBusy(request, "assigningComponentId")) this.assigningComponentId = "";
      }
    }
  }
});
