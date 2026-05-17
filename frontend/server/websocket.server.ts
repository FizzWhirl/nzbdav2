import WebSocket, { WebSocketServer } from 'ws';
import { isAuthenticated } from "../app/auth/authentication.server";
import type { IncomingMessage } from 'http';
import { errorDetails, logJson } from './logger.server.js';
import { runtimeConfig } from './runtime-config.server.js';

type BackendTopicMessage = {
    Topic: string;
    Message: unknown;
};

function isBackendTopicMessage(value: unknown): value is BackendTopicMessage {
    if (!value || typeof value !== 'object') return false;
    const candidate = value as Record<string, unknown>;
    return typeof candidate.Topic === 'string' && 'Message' in candidate;
}

function initializeWebsocketServer(wss: WebSocketServer) {
    // keep track of socket subscriptions
    const websockets = new Map<WebSocket, any>();
    const subscriptions = new Map<string, Set<WebSocket>>();
    const lastMessage = new Map<string, string>();
    initializeWebsocketClient(subscriptions, lastMessage);

    // authenticate new websocket sessions
    wss.on("connection", async (ws: WebSocket, request: IncomingMessage) => {
        try {
            // ensure user is logged in
            if (!await isAuthenticated(request)) {
                ws.close(1008, "Unauthorized");
                return;
            }

            // handle topic subscription
            ws.onmessage = (event: WebSocket.MessageEvent) => {
                try {
                    var topics = JSON.parse(event.data.toString());
                    websockets.set(ws, topics);
                    for (const topic in topics) {
                        var topicSubscriptions = subscriptions.get(topic);
                        if (topicSubscriptions) topicSubscriptions.add(ws);
                        else subscriptions.set(topic, new Set<WebSocket>([ws]));
                        if (topics[topic] === 'state') {
                            var messageToSend = lastMessage.get(topic);
                            if (messageToSend) ws.send(messageToSend);
                        }
                    }
                } catch {
                    ws.close(1003, "Could not process topic subscription. If recently updated, try refreshing the page.");
                }
            };

            // unsubscribe from topics
            ws.onclose = () => {
                var topics = websockets.get(ws);
                if (topics) {
                    websockets.delete(ws);
                    for (const topic in topics) {
                        var topicSubscriptions = subscriptions.get(topic);
                        if (topicSubscriptions) topicSubscriptions.delete(ws);
                    }
                }
            };
        } catch (error) {
            logJson("error", "Error authenticating websocket session", { error: errorDetails(error) });
            ws.close(1011, "Internal server error");
            return;
        }
    });
}

export function initializeWebsocketClient(subscriptions: Map<string, Set<WebSocket>>, lastMessage: Map<string, string>) {
    let reconnectTimeout: NodeJS.Timeout | null = null;
    const backoff = createReconnectBackoff();
    const url = runtimeConfig.backendWebsocketUrl;

    function connect() {
        const socket = new WebSocket(url);

        socket.onopen = () => {
            logJson("info", "WebSocket connected");
            backoff.reset();
            if (reconnectTimeout) {
                clearTimeout(reconnectTimeout);
                reconnectTimeout = null;
            }

            socket.send(Buffer.from(runtimeConfig.frontendBackendApiKey, "utf-8"), { binary: false });
        };

        socket.onmessage = (event: WebSocket.MessageEvent) => {
            var rawMessage = event.data.toString();
            let topicMessage: unknown;
            try {
                topicMessage = JSON.parse(rawMessage);
            } catch (error) {
                logJson("warn", "Dropped malformed backend WebSocket message", {
                    error: errorDetails(error),
                    byteLength: Buffer.byteLength(rawMessage)
                });
                return;
            }

            if (!isBackendTopicMessage(topicMessage)) {
                logJson("warn", "Dropped backend WebSocket message with unexpected shape", {
                    byteLength: Buffer.byteLength(rawMessage)
                });
                return;
            }

            var [topic] = [topicMessage.Topic];
            if (!topic) return;
            lastMessage.set(topic, rawMessage);
            var subscribed = subscriptions.get(topic) || [];
            subscribed.forEach(client => {
                if (client.readyState === client.OPEN) {
                    client.send(rawMessage);
                }
            });
        };

        socket.onerror = (event: WebSocket.ErrorEvent) => {
            logJson("error", "WebSocket error", { error: { message: event.message } });
        };

        socket.onclose = (event: WebSocket.CloseEvent) => {
            logJson("warn", `WebSocket closed (code: ${event.code}, reason: ${event.reason})`, {
                code: event.code,
                reason: event.reason
            });
            scheduleReconnect();
        };
    }

    function scheduleReconnect() {
        if (reconnectTimeout) clearTimeout(reconnectTimeout);

        const delay = backoff.nextDelayMs();
        logJson("info", `WebSocket reconnecting in ${delay}ms...`, { delayMs: delay });

        reconnectTimeout = setTimeout(() => {
            connect();
        }, delay);
    }

    connect();
}

function createReconnectBackoff(initialDelayMs = 1000, maxDelayMs = 30000, jitterRatio = 0.25) {
    let attempt = 0;

    return {
        reset() {
            attempt = 0;
        },
        nextDelayMs() {
            const baseDelay = Math.min(maxDelayMs, initialDelayMs * Math.pow(2, attempt));
            attempt += 1;
            const jitterWindow = Math.round(baseDelay * jitterRatio);
            const jitter = jitterWindow > 0 ? Math.floor(Math.random() * jitterWindow) : 0;
            return Math.min(maxDelayMs, baseDelay + jitter);
        }
    };
}

export const websocketServer = {
    initialize: initializeWebsocketServer
}