import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";

export async function loader({}: Route.LoaderArgs) {
    try {
        const stats = await backendClient.getProviderStats();
        return Response.json(stats);
    } catch (error) {
        console.error('Failed to fetch provider stats:', error);
        const message = error instanceof Error ? error.message : String(error);
        return Response.json(
            {
                error: 'Failed to fetch provider stats from backend',
                details: message
            },
            { status: 502 }
        );
    }
}
