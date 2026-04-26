import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";

export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const hours = Number(url.searchParams.get('hours')) || 24;

    try {
        const data = await backendClient.getDashboard(hours);
        return Response.json(data);
    } catch (error) {
        console.error('Failed to fetch dashboard data:', error);
        const message = error instanceof Error ? error.message : String(error);
        return Response.json(
            {
                error: 'Failed to fetch dashboard data from backend',
                details: message,
                timeWindowHours: hours
            },
            { status: 502 }
        );
    }
}
