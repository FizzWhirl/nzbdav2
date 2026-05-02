type LogLevel = "trace" | "debug" | "info" | "warn" | "error" | "fatal";

export function logJson(level: LogLevel, message: string, extra: Record<string, unknown> = {}) {
    const payload = {
        timestamp: new Date().toISOString(),
        level,
        source: "frontend",
        message,
        ...extra
    };
    const line = JSON.stringify(payload);

    if (level === "error" || level === "fatal") console.error(line);
    else console.log(line);
}

export function errorDetails(error: unknown) {
    if (error instanceof Error) {
        return {
            name: error.name,
            message: error.message,
            stack: error.stack
        };
    }

    return { message: String(error) };
}