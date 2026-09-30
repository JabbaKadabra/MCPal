using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCPal.Cloud.Storage.Migrations
{
    /// <summary>
    /// Adds <c>AspNetUsers.Role</c>, <c>ApiKeys.CreatedByUserId</c> and the <c>Invitations</c> table. Every user that exists
    /// before this migration was the only user of a company (there was no way to add a second one), so existing users
    /// become owners; existing keys have no known creator (null) and stay visible to owners only.
    /// </summary>
    public partial class UsersRolesAndInvitations : Migration
    {
        private static readonly string[] CompanyEmailColumns = ["CompanyId", "Email"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "AspNetUsers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Owner");

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserId",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invitations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_CompanyId_Email",
                table: "Invitations",
                columns: CompanyEmailColumns);

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "ApiKeys");
        }
    }
}
