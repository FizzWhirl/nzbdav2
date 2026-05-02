import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { Dashboard } from "./components/dashboard";

export function meta({}: Route.MetaArgs) {
  return [
    { title: "Dashboard | NzbDav" },
    { name: "description", content: "NzbDav System Dashboard" },
  ];
}

export async function loader({ request }: Route.LoaderArgs) {
    const [dashboardData, connections, currentBandwidth] = await Promise.all([
        backendClient.getDashboard(24),
        backendClient.getActiveConnections(),
        backendClient.getCurrentBandwidth()
    ]);
    return { dashboardData, connections, currentBandwidth };
}

export default function Index({ loaderData }: Route.ComponentProps) {
    const { dashboardData, connections, currentBandwidth } = loaderData;

    return (
        <Dashboard initialData={dashboardData} initialConnections={connections} initialBandwidth={currentBandwidth} />
    );
}