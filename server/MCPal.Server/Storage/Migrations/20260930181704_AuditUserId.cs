using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCPal.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AuditUserId : Migration
    {
        private static readonly string[] CompanyUserOccurredAtColumns = ["CompanyId", "UserId", "OccurredAt"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "ToolCallAudits",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToolCallAudits_CompanyId_UserId_OccurredAt",
                table: "ToolCallAudits",
                columns: CompanyUserOccurredAtColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ToolCallAudits_CompanyId_UserId_OccurredAt",
                table: "ToolCallAudits");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ToolCallAudits");
        }
    }
}
