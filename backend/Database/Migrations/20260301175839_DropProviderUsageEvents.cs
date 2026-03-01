using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class DropProviderUsageEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_ProviderUsageEvents_CreatedAt_ProviderHost_ProviderType_OperationType";
                """);
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_ProviderUsageEvents_ProviderHost";
                """);
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS "ProviderUsageEvents";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderUsageEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BytesTransferred = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OperationType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ProviderHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderUsageEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageEvents_CreatedAt_ProviderHost_ProviderType_OperationType",
                table: "ProviderUsageEvents",
                columns: new[] { "CreatedAt", "ProviderHost", "ProviderType", "OperationType" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageEvents_ProviderHost",
                table: "ProviderUsageEvents",
                column: "ProviderHost");
        }
    }
}
