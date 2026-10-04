using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCPal.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddToolCallAudit : Migration
    {
        private static readonly string[] CompanyOccurredAtColumns = ["CompanyId", "OccurredAt"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToolCallAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    AuthKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    OAuthClientId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AgentName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ServerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ToolName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PublicName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolCallAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToolCallAudits_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ToolCallAudits_CompanyId_OccurredAt",
                table: "ToolCallAudits",
                columns: CompanyOccurredAtColumns);

            migrationBuilder.CreateIndex(
                name: "IX_ToolCallAudits_OccurredAt",
                table: "ToolCallAudits",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToolCallAudits");
        }
    }
}
