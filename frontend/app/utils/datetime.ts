export function formatDateTime(value: string | number | Date | null | undefined, fallback = "—") {
    const date = parseDate(value);
    if (!date) return fallback;

    return new Intl.DateTimeFormat(undefined, {
        year: "numeric",
        month: "short",
        day: "2-digit",
        hour: "numeric",
        minute: "2-digit",
        timeZoneName: "short"
    }).format(date);
}

export function formatDateOnly(value: string | number | Date | null | undefined, fallback = "—") {
    const date = parseDate(value);
    if (!date) return fallback;

    return new Intl.DateTimeFormat(undefined, {
        year: "numeric",
        month: "short",
        day: "2-digit",
        timeZoneName: "short"
    }).format(date);
}

export function formatTimeOnly(value: string | number | Date | null | undefined, fallback = "—") {
    const date = parseDate(value);
    if (!date) return fallback;

    return new Intl.DateTimeFormat(undefined, {
        hour: "numeric",
        minute: "2-digit",
        timeZoneName: "short"
    }).format(date);
}

export function formatCompactDateTime(value: string | number | Date | null | undefined, fallback = "—") {
    const date = parseDate(value);
    if (!date) return fallback;

    return isSameLocalDate(date, new Date())
        ? formatTimeOnly(date, fallback)
        : formatDateOnly(date, fallback);
}

export function formatHealthDueDate(value: string | null | undefined, fallback = "ASAP") {
    const date = parseDate(value);
    if (!date || date.getFullYear() <= 1901) return fallback;
    return formatCompactDateTime(date, fallback);
}

function parseDate(value: string | number | Date | null | undefined) {
    if (value === null || value === undefined || value === "") return null;
    const date = value instanceof Date ? value : new Date(value);
    return Number.isNaN(date.getTime()) ? null : date;
}

function isSameLocalDate(one: Date, two: Date) {
    return (
        one.getFullYear() === two.getFullYear() &&
        one.getMonth() === two.getMonth() &&
        one.getDate() === two.getDate()
    );
}
