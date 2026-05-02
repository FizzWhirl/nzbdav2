import compression from "compression";
import express from "express";
import morgan from "morgan";
import http from "http";
import { WebSocketServer } from "ws";
import { logJson } from "./server/logger.server.js";

// Short-circuit the type-checking of the built output.
const BUILD_PATH = "../build/server/index.js";
const DEVELOPMENT = process.env.NODE_ENV === "development";
const PORT = Number.parseInt(process.env.PORT || "3000");

// Initialize the express app
const app = express();
app.use(compression());
app.disable("x-powered-by");

// Initialize the websocket server as soon as both it and the server-module are ready
let _serverModule: any = null;
let _websocketServer: WebSocketServer | null = null;
const setWebsocketServer = (websocketServer: WebSocketServer) => {
  if (_websocketServer != null) return;
  if (_serverModule != null) _serverModule.initializeWebsocketServer(websocketServer);
  _websocketServer = websocketServer;
}
const setServerModule = (serverModule: any) => {
  if (_serverModule != null) return;
  if (_websocketServer != null) serverModule.initializeWebsocketServer(_websocketServer);
  _serverModule = serverModule;
}

// Handle development vs production
if (DEVELOPMENT) {
  logJson("info", "Starting development server");
  const viteDevServer = await import("vite").then((vite) =>
    vite.createServer({
      server: { middlewareMode: true },
    }),
  );
  app.use(viteDevServer.middlewares);
  app.use(async (req, res, next) => {
    try {
      const serverModule = await viteDevServer.ssrLoadModule("./server/app.ts");
      setServerModule(serverModule);
      return await serverModule.app(req, res, next);
    } catch (error) {
      if (typeof error === "object" && error instanceof Error) {
        viteDevServer.ssrFixStacktrace(error);
      }
      next(error);
    }
  });
} else {
  logJson("info", "Starting production server");
  app.use(
    "/assets",
    express.static("build/client/assets", { immutable: true, maxAge: "1y" }),
  );
  app.use(morgan((tokens, req, res) => {
    const method = tokens.method(req, res) ?? "UNKNOWN";
    const url = tokens.url(req, res) ?? req.url;
    const status = Number(tokens.status(req, res) ?? 0);
    return JSON.stringify({
      timestamp: new Date().toISOString(),
      level: status >= 500 ? "error" : "warn",
      source: "frontend-http",
      message: `${method} ${url} ${status}`,
      http: {
        method,
        url,
        status,
        responseTimeMs: Number(tokens["response-time"](req, res) ?? 0),
        contentLength: tokens.res(req, res, "content-length") ?? null
      }
    });
  }, {
    skip: (req, res) => {
      return res.statusCode < 400
        || req.url === "/favicon.ico"
    }
  }));
  app.use(express.static("build/client", { maxAge: "1h" }));
  const serverModule = await import(BUILD_PATH);
  app.use(serverModule.app);
  setServerModule(serverModule);
}

// Create both the http and websocket servers
const server = http.createServer(app);
setWebsocketServer(new WebSocketServer({ server }));

// Begin listening for connections
server.listen(PORT, () => {
  logJson("info", `Server is running on http://localhost:${PORT}`, { port: PORT });
});
