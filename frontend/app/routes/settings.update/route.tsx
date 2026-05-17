import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import type { ConfigItem } from "~/types/backend";
import { defaultConfig } from "../settings/settings-config";

export async function action({ request }: Route.ActionArgs) {
    // get the ConfigItems to update
    const formData = await request.formData();
    const configJson = formData.get("config");
    if (typeof configJson !== "string") {
        throw new Response("Missing config payload", { status: 400 });
    }

    let config: unknown;
    try {
        config = JSON.parse(configJson);
    } catch {
        throw new Response("Invalid config payload", { status: 400 });
    }

    if (!config || typeof config !== "object" || Array.isArray(config)) {
        throw new Response("Invalid config payload", { status: 400 });
    }

    const configItems: ConfigItem[] = [];
    for (const [key, value] of Object.entries(config)) {
        if (!key || key.length > 200 || !(key in defaultConfig) || typeof value !== "string") {
            throw new Response("Invalid config item", { status: 400 });
        }

        configItems.push({
            configName: key,
            configValue: value
        })
    }

    // update the config items
    try {
        await backendClient.updateConfig(configItems);
    } catch (error) {
        throw new Response(error instanceof Error ? error.message : "Failed to update config", { status: 502 });
    }

    return { config: config }
}