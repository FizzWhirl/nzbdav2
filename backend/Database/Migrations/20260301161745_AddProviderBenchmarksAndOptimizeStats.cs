using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderBenchmarksAndOptimizeStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent: table may already exist from a prior broken migration
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "ProviderBenchmarkResults" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ProviderBenchmarkResults" PRIMARY KEY,
                    "RunId" TEXT NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "TestFileName" TEXT NOT NULL,
                    "TestFileSize" INTEGER NOT NULL,
                    "TestSizeMb" INTEGER NOT NULL,
                    "ProviderIndex" INTEGER NOT NULL,
                    "ProviderHost" TEXT NOT NULL,
                    "ProviderType" TEXT NOT NULL,
                    "IsLoadBalanced" INTEGER NOT NULL,
                    "BytesDownloaded" INTEGER NOT NULL,
                    "ElapsedSeconds" REAL NOT NULL,
                    "SpeedMbps" REAL NOT NULL,
                    "Success" INTEGER NOT NULL,
                    "ErrorMessage" TEXT NULL
                );
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_ProviderBenchmarkResults_CreatedAt"
                    ON "ProviderBenchmarkResults" ("CreatedAt");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_ProviderBenchmarkResults_RunId"
                    ON "ProviderBenchmarkResults" ("RunId");
                """);

            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_ProviderUsageEvents_CreatedAt";
                """);

            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_ProviderUsageEvents_OperationType";
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_ProviderUsageEvents_CreatedAt_ProviderHost_ProviderType_OperationType"
                    ON "ProviderUsageEvents" ("CreatedAt", "ProviderHost", "ProviderType", "OperationType");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProviderUsageEvents_CreatedAt_ProviderHost_ProviderType_OperationType",
                table: "ProviderUsageEvents");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageEvents_CreatedAt",
                table: "ProviderUsageEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageEvents_OperationType",
                table: "ProviderUsageEvents",
                column: "OperationType");

            migrationBuilder.DropTable(
                name: "ProviderBenchmarkResults");
        }
    }
}
