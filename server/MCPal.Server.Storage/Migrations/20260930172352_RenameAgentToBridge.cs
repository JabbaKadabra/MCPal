using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCPal.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RenameAgentToBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AgentName",
                table: "ToolCallAudits",
                newName: "BridgeName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "BridgeName",
                table: "ToolCallAudits",
                newName: "AgentName");
        }
    }
}
