using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentProviderEvidenceToMissingArticleSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SegmentProviderEvidenceJson",
                table: "MissingArticleSummaries",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SegmentProviderEvidenceJson",
                table: "MissingArticleSummaries");
        }
    }
}