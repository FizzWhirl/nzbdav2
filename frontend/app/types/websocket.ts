export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

export type WebsocketTopicMessage = {
    Topic: string;
    Message: JsonValue;
};

export function isWebsocketTopicMessage(value: unknown): value is WebsocketTopicMessage {
    if (!value || typeof value !== "object" || Array.isArray(value)) return false;
    const candidate = value as Record<string, unknown>;
    return typeof candidate.Topic === "string" && isJsonValue(candidate.Message);
}

function isJsonValue(value: unknown): value is JsonValue {
    if (value === null) return true;
    if (typeof value === "string" || typeof value === "number" || typeof value === "boolean") return true;
    if (Array.isArray(value)) return value.every(isJsonValue);
    if (typeof value !== "object") return false;
    return Object.values(value as Record<string, unknown>).every(isJsonValue);
}