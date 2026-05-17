export type RuntimeConfig = {
  backendUrl: string;
  backendWebsocketUrl: string;
  frontendBackendApiKey: string;
};

const DEFAULT_BACKEND_URL = "http://localhost:8080";

function readRequiredEnv(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) {
    throw new Error(`${name} must be set before starting the frontend server.`);
  }
  return value;
}

function readBackendUrl(): string {
  const value = process.env.BACKEND_URL?.trim() || DEFAULT_BACKEND_URL;
  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    throw new Error(`BACKEND_URL must be a valid URL. Received: ${value}`);
  }

  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    throw new Error(`BACKEND_URL must use http or https. Received protocol: ${parsed.protocol}`);
  }

  return parsed.toString().replace(/\/$/, "");
}

function toWebsocketUrl(backendUrl: string): string {
  return `${backendUrl}/ws`.replace(/^http/i, "ws");
}

export const runtimeConfig: RuntimeConfig = (() => {
  const backendUrl = readBackendUrl();
  const frontendBackendApiKey = readRequiredEnv("FRONTEND_BACKEND_API_KEY");

  return {
    backendUrl,
    backendWebsocketUrl: toWebsocketUrl(backendUrl),
    frontendBackendApiKey
  };
})();