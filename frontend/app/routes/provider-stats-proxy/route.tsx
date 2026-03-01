import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";

export async function loader({}: Route.LoaderArgs) {
    try {
        const stats = await backendClient.getProviderStats();
        return Response.json(stats);
    } catch (error) {
        console.error('Failed to fetch provider stats:', error);
        return Response.json(
            {
                providers: [],
                totalOperations: 0,
                calculatedAt: new Date().toISOString(),
                timeWindow: 'cumulative',
                timeWindowHours: 0
            },
            { status: 200 }
        );
    }
}
