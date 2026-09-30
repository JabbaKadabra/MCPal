using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCPal.Cloud.Storage.Migrations
{
    /// <summary>
    /// Adds <c>ApiKeys.Purpose</c> (0 any, 1 agent, 2 client) and <c>ApiKeys.AllowedServers</c> (text[], empty = all servers).
    /// Existing keys get purpose 0 and no server restriction, so they keep working exactly as before.
    /// </summary>
    public partial class ApiKeyPurposeAndAllowedServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "AllowedServers",
                table: "ApiKeys",
                type: "text[]",
                nullable: false,
                defaultValue: Array.Empty<string>());

            migrationBuilder.AddColumn<int>(
                name: "Purpose",
                table: "ApiKeys",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedServers",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "ApiKeys");
        }
    }
}
