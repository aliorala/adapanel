import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type DockerContainer = {
  id: string;
  name: string;
  image: string;
  state: string;
  status: string;
  ports: string;
};

export type DockerImage = {
  id: string;
  repository: string;
  tag: string;
  size: string;
};

export type DockerWorkspace = {
  installed: boolean;
  daemonRunning: boolean;
  version: string;
  containers: DockerContainer[];
  images: DockerImage[];
};

export type DockerLogs = { containerId: string; output: string };

export async function getDocker() {
  const response = await apiClient.get<ApiEnvelope<DockerWorkspace>>("/docker");
  return response.data.data;
}

export async function dockerContainerAction(id: string, action: "start" | "stop" | "restart") {
  const response = await apiClient.post<ApiEnvelope<DockerWorkspace>>(`/docker/containers/${encodeURIComponent(id)}/${action}`, {});
  return response.data.data;
}

export async function removeDockerContainer(id: string) {
  const response = await apiClient.delete<ApiEnvelope<DockerWorkspace>>(`/docker/containers/${encodeURIComponent(id)}`);
  return response.data.data;
}

export async function getDockerLogs(id: string, tail = 200) {
  const response = await apiClient.get<ApiEnvelope<DockerLogs>>(`/docker/containers/${encodeURIComponent(id)}/logs`, {
    params: { tail }
  });
  return response.data.data;
}

export async function runDockerContainer(payload: {
  image: string;
  name: string;
  ports: string;
  environment: string;
  autoRestart: boolean;
}) {
  const response = await apiClient.post<ApiEnvelope<DockerWorkspace>>("/docker/containers/run", payload);
  return response.data.data;
}

export async function pullDockerImage(image: string) {
  const response = await apiClient.post<ApiEnvelope<DockerWorkspace>>("/docker/images/pull", { image });
  return response.data.data;
}

export async function removeDockerImage(id: string) {
  const response = await apiClient.delete<ApiEnvelope<DockerWorkspace>>(`/docker/images/${encodeURIComponent(id)}`);
  return response.data.data;
}
