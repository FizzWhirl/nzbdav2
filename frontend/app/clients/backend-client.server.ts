import { type ConnectionUsageContext, ConnectionUsageType } from "~/types/connections";
import type { BandwidthSample, ProviderBandwidthSnapshot } from "~/types/bandwidth";
import type { HealthCheckResult, MissingArticleItem, MappedFile } from "~/types/stats";
import type {
    QueueResponse, QueueSlot, HistoryResponse, HistorySlot,
    DirectoryItem, SearchResult, ConfigItem,
    HealthCheckQueueResponse, HealthCheckQueueItem,
    HealthCheckHistoryResponse, HealthCheckStats,
    AnalysisItem, AnalysisHistoryResponse, FileDetails, ProviderStatistic,
    HealthCheckInfoType, DashboardSummary
} from "~/types/backend";
import type { DashboardData } from "~/types/dashboard";
import type { ProviderStatsResponse } from "~/types/provider-stats";

type JsonRecord = Record<string, unknown>;

async function assertResponseOk(response: Response, action: string): Promise<void> {
    if (response.ok) return;
    const message = await readResponseError(response);
    throw new Error(`Failed to ${action}: ${message}`);
}

async function readResponseJson(response: Response, action: string): Promise<unknown> {
    try {
        return await response.json();
    } catch (error) {
        throw new Error(`Failed to ${action}: response was not valid JSON`);
    }
}

async function readResponseError(response: Response): Promise<string> {
    const contentType = response.headers.get("content-type") ?? "";
    const text = await response.text();
    if (contentType.includes("application/json") && text.trim().length > 0) {
        try {
            const data = JSON.parse(text);
            if (isRecord(data) && typeof data.error === "string" && data.error.length > 0) return data.error;
            if (isRecord(data) && typeof data.message === "string" && data.message.length > 0) return data.message;
        } catch {
            // Fall back to response text below.
        }
    }

    return text.trim() || `${response.status} ${response.statusText}`;
}

function isRecord(value: unknown): value is JsonRecord {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

function expectRecord(value: unknown, context: string): JsonRecord {
    if (isRecord(value)) return value;
    throw new Error(`${context} response had unexpected shape`);
}

function expectArray<T>(value: unknown, context: string): T[] {
    if (Array.isArray(value)) return value as T[];
    throw new Error(`${context} response had unexpected shape`);
}

function expectBoolean(value: unknown, context: string): boolean {
    if (typeof value === "boolean") return value;
    throw new Error(`${context} response had unexpected shape`);
}

class BackendClient {
    private async fetchWithTimeout(url: string, options: RequestInit = {}): Promise<Response> {
        return fetch(url, {
            ...options,
            signal: options.signal ?? AbortSignal.timeout(30000)
        });
    }

    public async isOnboarding(): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/is-onboarding";

        const response = await this.fetchWithTimeout(url, {
            method: "GET",
            headers: {
                "Content-Type": "application/json",
                "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || ""
            }
        });

        await assertResponseOk(response, "fetch onboarding status");
        const data = expectRecord(await readResponseJson(response, "fetch onboarding status"), "Onboarding status");
        return expectBoolean(data.isOnboarding, "Onboarding status");
    }

    public async createAccount(username: string, password: string): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/create-account";

        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: {
                "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || ""
            },
            body: (() => {
                const form = new FormData();
                form.append("username", username);
                form.append("password", password);
                form.append("type", "admin");
                return form;
            })()
        });

        await assertResponseOk(response, "create account");
        const data = expectRecord(await readResponseJson(response, "create account"), "Create account");
        return expectBoolean(data.status, "Create account");
    }

    public async authenticate(username: string, password: string): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/authenticate";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("username", username);
                form.append("password", password);
                form.append("type", "admin");
                return form;
            })()
        });

        await assertResponseOk(response, "authenticate");
        const data = expectRecord(await readResponseJson(response, "authenticate"), "Authenticate");
        return expectBoolean(data.authenticated, "Authenticate");
    }

    public async getQueue(limit: number): Promise<QueueResponse> {
        const url = process.env.BACKEND_URL + `/api?mode=queue&limit=${limit}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        await assertResponseOk(response, "get queue");
        const data = expectRecord(await readResponseJson(response, "get queue"), "Queue");
        return expectRecord(data.queue, "Queue") as QueueResponse;
    }

    public async getHistory(limit: number, showHidden: boolean = false): Promise<HistoryResponse> {
        const showHiddenParam = showHidden ? '&show_hidden=1' : '';
        const url = process.env.BACKEND_URL + `/api?mode=history&pageSize=${limit}${showHiddenParam}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        await assertResponseOk(response, "get history");
        const data = expectRecord(await readResponseJson(response, "get history"), "History");
        return expectRecord(data.history, "History") as HistoryResponse;
    }

    public async addNzb(nzbFile: File): Promise<string> {
        var config = await this.getConfig(["api.manual-category"]);
        var category = config.find(item => item.configName === "api.manual-category")?.configValue || "uncategorized";
        const url = process.env.BACKEND_URL + `/api?mode=addfile&cat=${category}&priority=0&pp=0`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("nzbFile", nzbFile, nzbFile.name);
                return form;
            })()
        });

        await assertResponseOk(response, "add nzb file");
        const data = expectRecord(await readResponseJson(response, "add nzb file"), "Add NZB");
        const nzoIds = expectArray<unknown>(data.nzo_ids, "Add NZB nzo_ids");
        if (nzoIds.length !== 1 || typeof nzoIds[0] !== "string") {
            throw new Error(`Failed to add nzb file: unexpected response format`);
        }
        return nzoIds[0];
    }

    public async listWebdavDirectory(directory: string): Promise<DirectoryItem[]> {
        const url = process.env.BACKEND_URL + "/api/list-webdav-directory";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("directory", directory);
                return form;
            })()
        });

        await assertResponseOk(response, "list webdav directory");
        const data = expectRecord(await readResponseJson(response, "list webdav directory"), "WebDAV directory");
        return expectArray<DirectoryItem>(data.items, "WebDAV directory items");
    }

    public async searchWebdav(query: string, directory: string): Promise<SearchResult[]> {
        const url = process.env.BACKEND_URL + "/api/search-webdav";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("query", query);
                form.append("directory", directory);
                return form;
            })()
        });

        await assertResponseOk(response, "search webdav");
        const data = expectRecord(await readResponseJson(response, "search webdav"), "WebDAV search");
        return expectArray<SearchResult>(data.results, "WebDAV search results");
    }

    public async getConfig(keys: string[]): Promise<ConfigItem[]> {
        const url = process.env.BACKEND_URL + "/api/get-config";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                for (const key of keys) {
                    form.append("config-keys", key);
                }
                return form;
            })()
        });

        await assertResponseOk(response, "get config items");
        const data = expectRecord(await readResponseJson(response, "get config items"), "Config items");
        return data.configItems === undefined
            ? []
            : expectArray<ConfigItem>(data.configItems, "Config items");
    }

    public async updateConfig(configItems: ConfigItem[]): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/update-config";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                for (const item of configItems) {
                    form.append(item.configName, item.configValue);
                }
                return form;
            })()
        });

        await assertResponseOk(response, "update config items");
        const data = expectRecord(await readResponseJson(response, "update config items"), "Update config");
        return expectBoolean(data.status, "Update config");
    }

    public async getHealthCheckQueue(
        pageSize?: number,
        page?: number,
        search?: string,
        showAll?: boolean,
        showFailed?: boolean,
        showUnhealthy?: boolean
    ): Promise<HealthCheckQueueResponse> {
        const params = new URLSearchParams();
        if (pageSize !== undefined) params.set('pageSize', pageSize.toString());
        if (page !== undefined) params.set('page', page.toString());
        if (search) params.set('search', search);
        if (showAll !== undefined) params.set('showAll', showAll.toString());
        if (showFailed !== undefined) params.set('showFailed', showFailed.toString());
        if (showUnhealthy !== undefined) params.set('showUnhealthy', showUnhealthy.toString());

        const url = process.env.BACKEND_URL + "/api/get-health-check-queue?" + params.toString();

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        await assertResponseOk(response, "get health check queue");
        return expectRecord(await readResponseJson(response, "get health check queue"), "Health check queue") as HealthCheckQueueResponse;
    }

    public async getHealthCheckHistory(pageSize?: number): Promise<HealthCheckHistoryResponse> {
        let url = process.env.BACKEND_URL + "/api/get-health-check-history";

        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        await assertResponseOk(response, "get health check history");
        return expectRecord(await readResponseJson(response, "get health check history"), "Health check history") as HealthCheckHistoryResponse;
    }

    public async getActiveConnections(): Promise<Record<number, ConnectionUsageContext[]>> {
        const url = process.env.BACKEND_URL + "/api/stats/connections";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get active connections: ${(await response.json()).error}`);
        return response.json();
    }

    public async getCurrentBandwidth(): Promise<ProviderBandwidthSnapshot[]> {
        const url = process.env.BACKEND_URL + "/api/stats/bandwidth/current";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get current bandwidth: ${(await response.json()).error}`);
        return response.json();
    }

    public async getBandwidthHistory(range: string): Promise<BandwidthSample[]> {
        const url = process.env.BACKEND_URL + `/api/stats/bandwidth/history?range=${range}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get bandwidth history: ${(await response.json()).error}`);
        return response.json();
    }

    public async getDeletedFiles(page: number = 1, pageSize: number = 50, search: string = ""): Promise<{ items: HealthCheckResult[], totalCount: number }> {
        const url = process.env.BACKEND_URL + `/api/stats/deleted-files?page=${page}&pageSize=${pageSize}&search=${encodeURIComponent(search)}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get deleted files: ${(await response.json()).error}`);
        return response.json();
    }

    public async getMissingArticles(page: number = 1, pageSize: number = 10, search: string = "", blocking?: boolean, orphaned?: boolean, isImported?: boolean): Promise<{ items: MissingArticleItem[], totalCount: number }> {
        let url = process.env.BACKEND_URL + `/api/stats/missing-articles?page=${page}&pageSize=${pageSize}&search=${encodeURIComponent(search)}`;
        if (blocking !== undefined) {
            url += `&blocking=${blocking}`;
        }
        if (orphaned !== undefined) {
            url += `&orphaned=${orphaned}`;
        }
        if (isImported !== undefined) {
            url += `&isImported=${isImported}`;
        }
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get missing articles: ${(await response.json()).error}`);
        return response.json();
    }

    public async getMappedFiles(page: number = 1, pageSize: number = 10, search: string = "", hasMediaInfo?: boolean, missingVideo?: boolean, sortBy: string = "linkPath", sortDirection: string = "asc"): Promise<{ items: MappedFile[], totalCount: number }> {
        let url = process.env.BACKEND_URL + `/api/stats/mapped-files?page=${page}&pageSize=${pageSize}&search=${encodeURIComponent(search)}&sortBy=${sortBy}&sortDirection=${sortDirection}`;
        if (hasMediaInfo !== undefined) url += `&hasMediaInfo=${hasMediaInfo}`;
        if (missingVideo !== undefined) url += `&missingVideo=${missingVideo}`;
        
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get mapped files: ${(await response.json()).error}`);
        return response.json();
    }

    public async getDashboardSummary(): Promise<DashboardSummary> {
        const url = process.env.BACKEND_URL + "/api/stats/dashboard/summary";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get dashboard summary: ${(await response.json()).error}`);
        return response.json();
    }

    public async getDashboard(hours: number = 24): Promise<DashboardData> {
        const url = process.env.BACKEND_URL + `/api/stats/dashboard?hours=${hours}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get dashboard: ${(await response.json()).error}`);
        return response.json();
    }

    public async clearMissingArticles(filename?: string): Promise<void> {

            let url = process.env.BACKEND_URL + `/api/stats/missing-articles`;
            if (filename) {
                url += `?filename=${encodeURIComponent(filename)}`;
            }

            const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";

            const response = await this.fetchWithTimeout(url, {

                method: "DELETE",

                headers: { "x-api-key": apiKey }

            });

            if (!response.ok) throw new Error(`Failed to clear missing articles: ${(await response.json()).error}`);

        }

    

        public async clearDeletedFiles(): Promise<void> {

            const url = process.env.BACKEND_URL + `/api/stats/deleted-files`;

            const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";

            const response = await this.fetchWithTimeout(url, {

                method: "DELETE",

                headers: { "x-api-key": apiKey }

            });

            if (!response.ok) throw new Error(`Failed to clear deleted files: ${(await response.json()).error}`);

        }

    public async resetConnections(type?: number): Promise<void> {
        const url = process.env.BACKEND_URL + "/api/maintenance/reset-connections";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { 
                "x-api-key": apiKey,
                "Content-Type": "application/json"
            },
            body: JSON.stringify({ type })
        });

        if (!response.ok) throw new Error(`Failed to reset connections: ${(await response.json()).error}`);
    }

    public async triggerRepair(filePaths: string[], davItemIds?: string[]): Promise<void> {
        const url = process.env.BACKEND_URL + `/api/stats/repair`;
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "X-Api-Key": process.env.FRONTEND_BACKEND_API_KEY || "",
            },
            body: JSON.stringify({ filePaths, davItemIds }),
        });
        if (!response.ok) throw new Error(`Failed to trigger repair: ${(await response.json()).error}`);
    }

    public async getActiveAnalyses(): Promise<AnalysisItem[]> {
        const url = process.env.BACKEND_URL + "/api/maintenance/active-analyses";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get active analyses: ${(await response.json()).error}`);
        return response.json();
    }

    public async getAnalysisHistory(page: number = 0, pageSize: number = 100, search: string = "", showFailedOnly: boolean = false, type: string = "all", showActionNeededOnly: boolean = false): Promise<AnalysisHistoryResponse> {
        const url = process.env.BACKEND_URL + `/api/analysis-history?page=${page}&pageSize=${pageSize}&search=${encodeURIComponent(search)}&showFailedOnly=${showFailedOnly}&type=${encodeURIComponent(type)}&showActionNeededOnly=${showActionNeededOnly}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get analysis history: ${(await response.json()).error}`);
        return response.json();
    }

    public async getFileDetails(davItemId: string): Promise<FileDetails> {
        const url = process.env.BACKEND_URL + `/api/file-details/${davItemId}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get file details: ${(await response.json()).error}`);
        return response.json();
    }

    public async resetProviderStats(jobName?: string): Promise<{ message: string; deletedCount: number }> {
        const url = process.env.BACKEND_URL + `/api/reset-provider-stats${jobName ? `?jobName=${encodeURIComponent(jobName)}` : ''}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: 'POST',
            headers: { "x-api-key": apiKey }
        });
        if (!response.ok) throw new Error(`Failed to reset provider stats: ${(await response.json()).error}`);
        return response.json();
    }

    public async resetHealthStatus(davItemIds: string[]): Promise<number> {
        const url = process.env.BACKEND_URL + "/api/health/reset";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: {
                "x-api-key": apiKey,
                "Content-Type": "application/json"
            },
            body: JSON.stringify({ davItemIds })
        });

        await assertResponseOk(response, "reset health status");
        const data = expectRecord(await readResponseJson(response, "reset health status"), "Reset health status");
        const resetCount = data.resetCount;
        if (typeof resetCount !== "number") throw new Error("Reset health status response had unexpected shape");
        return resetCount;
    }

    public async getProviderStats(range: string = "all"): Promise<ProviderStatsResponse> {
        const url = process.env.BACKEND_URL + `/api/provider-stats?range=${encodeURIComponent(range)}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        await assertResponseOk(response, "get provider stats");
        return expectRecord(await readResponseJson(response, "get provider stats"), "Provider stats") as ProviderStatsResponse;
    }
}

    

export const backendClient = new BackendClient();

export type { ProviderStatsResponse, ProviderStats } from "~/types/provider-stats";
