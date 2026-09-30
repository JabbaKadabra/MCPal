using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCPal.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AccessGroupsAndGrants : Migration
    {
        private static readonly string[] GrantCompanyGroupColumns = ["CompanyId", "GroupId"];
        private static readonly string[] MemberCompanyUserColumns = ["CompanyId", "UserId"];
        private static readonly string[] GroupCompanyNameColumns = ["CompanyId", "Name"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsEveryone = table.Column<bool>(type: "boolean", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessGroups_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerPattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ToolPatterns = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessGrants_AccessGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "AccessGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessGrants_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessGroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_AccessGroupMembers_AccessGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "AccessGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessGroupMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessGroupMembers_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessGrants_CompanyId_GroupId",
                table: "AccessGrants",
                columns: GrantCompanyGroupColumns);

            migrationBuilder.CreateIndex(
                name: "IX_AccessGrants_GroupId",
                table: "AccessGrants",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessGroupMembers_CompanyId_UserId",
                table: "AccessGroupMembers",
                columns: MemberCompanyUserColumns);

            migrationBuilder.CreateIndex(
                name: "IX_AccessGroupMembers_UserId",
                table: "AccessGroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessGroups_CompanyId_Name",
                table: "AccessGroups",
                columns: GroupCompanyNameColumns);

            migrationBuilder.CreateIndex(
                name: "IX_AccessGroups_Everyone",
                table: "AccessGroups",
                column: "CompanyId",
                unique: true,
                filter: "\"IsEveryone\"");

            // Existing companies keep working as before: their implicit Everyone group may use every tool until an owner narrows it.
            migrationBuilder.Sql(
                """
                INSERT INTO "AccessGroups" ("Id", "CompanyId", "Name", "IsEveryone", "CreatedAt")
                SELECT gen_random_uuid(), c."Id", 'Everyone', true, now()
                FROM "Companies" c;
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO "AccessGrants" ("Id", "GroupId", "CompanyId", "ServerPattern", "ToolPatterns")
                SELECT gen_random_uuid(), g."Id", g."CompanyId", '*', ARRAY['*']::text[]
                FROM "AccessGroups" g
                WHERE g."IsEveryone";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessGrants");

            migrationBuilder.DropTable(
                name: "AccessGroupMembers");

            migrationBuilder.DropTable(
                name: "AccessGroups");
        }
    }
}
