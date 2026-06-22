import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type TerminalCommandResult = {
  exitCode: number;
  standardOutput: string;
  standardError: string;
  workingDirectory: string;
  durationMilliseconds: number;
  timedOut: boolean;
};

export async function executeTerminalCommand(command: string, workingDirectory: string, timeoutSeconds: number) {
  const response = await apiClient.post<ApiEnvelope<TerminalCommandResult>>("/terminal/execute", {
    command,
    workingDirectory,
    timeoutSeconds
  });
  return response.data.data;
}
