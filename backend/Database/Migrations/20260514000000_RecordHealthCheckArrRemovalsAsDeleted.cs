using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class RecordHealthCheckArrRemovalsAsDeleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "HealthCheckResults"
                SET "RepairStatus" = 2
                WHERE "RepairStatus" = 1
                  AND "Message" LIKE '%Successfully triggered Arr to remove file%'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "HealthCheckResults"
                SET "RepairStatus" = 1
                WHERE "RepairStatus" = 2
                  AND "Message" LIKE '%Successfully triggered Arr to remove file%'
                """);
        }
    }
}