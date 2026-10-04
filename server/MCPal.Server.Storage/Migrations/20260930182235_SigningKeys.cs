using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCPal.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SigningKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SigningKeys",
                columns: table => new
                {
                    Kid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Algorithm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PublicJwkJson = table.Column<string>(type: "text", nullable: false),
                    ProtectedPrivateKey = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatesAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RemoveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningKeys", x => x.Kid);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SigningKeys_RemoveAt",
                table: "SigningKeys",
                column: "RemoveAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SigningKeys");
        }
    }
}
