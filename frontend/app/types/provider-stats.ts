export type ProviderStatsResponse = {
    providers: ProviderStats[];
    totalOperations: number;
    calculatedAt: string;
    timeWindow: string;
    timeWindowHours: number;
};

export type ProviderStats = {
    providerIndex: number;
    providerHost: string;
    providerType: string;
    totalOperations: number;
    operationCounts: { [key: string]: number };
    percentageOfTotal: number;
    totalBytes: number;
    averageSpeedMbps: number;
};
