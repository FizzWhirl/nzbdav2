import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";

export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const range = url.searchParams.get('range') || 'all';

    try {
        const stats = await backendClient.getProviderStats(range);
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
