using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCPal.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class UserBoundCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // OAuth tokens and codes were bound to a key or a company, not to a user. Clients authorize again (breaking, pre-release).
            migrationBuilder.Sql("""DELETE FROM "OAuthTokens";""");
            migrationBuilder.Sql("""DELETE FROM "AuthorizationCodes";""");

            migrationBuilder.DropIndex(
                name: "IX_OAuthTokens_ApiKeyId",
                table: "OAuthTokens");

            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "OAuthTokens");

            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "AuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "AllowedServers",
                table: "ApiKeys");

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "OAuthTokens",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "AuthorizationCodes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Disabled",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "AspNetUsers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalIssuer",
                table: "AspNetUsers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSubject",
                table: "AspNetUsers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            // Purposes were Any = 0, Bridge = 1, Client = 2; they are now Personal = 0, Bridge = 1.
            // A Client key becomes the personal access token of its creator (a key without a creator of this company cannot act as a user and is deleted).
            // An Any key keeps running tunnels alive, so it becomes a bridge key.
            migrationBuilder.Sql(
                """
                DELETE FROM "ApiKeys" k
                WHERE k."Purpose" = 2
                  AND NOT EXISTS (SELECT 1 FROM "AspNetUsers" u WHERE u."Id" = k."CreatedByUserId" AND u."CompanyId" = k."CompanyId");
                """);
            migrationBuilder.Sql(
                """
                UPDATE "ApiKeys"
                SET "UserId" = CASE WHEN "Purpose" = 2 THEN "CreatedByUserId" END,
                    "Purpose" = CASE WHEN "Purpose" = 2 THEN 0 ELSE 1 END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthTokens_UserId",
                table: "OAuthTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationCodes_UserId",
                table: "AuthorizationCodes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_UserId",
                table: "ApiKeys",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApiKeys_AspNetUsers_UserId",
                table: "ApiKeys",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AuthorizationCodes_AspNetUsers_UserId",
                table: "AuthorizationCodes",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OAuthTokens_AspNetUsers_UserId",
                table: "OAuthTokens",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApiKeys_AspNetUsers_UserId",
                table: "ApiKeys");

            migrationBuilder.DropForeignKey(
                name: "FK_AuthorizationCodes_AspNetUsers_UserId",
                table: "AuthorizationCodes");

            migrationBuilder.DropForeignKey(
                name: "FK_OAuthTokens_AspNetUsers_UserId",
                table: "OAuthTokens");

            migrationBuilder.DropIndex(
                name: "IX_OAuthTokens_UserId",
                table: "OAuthTokens");

            migrationBuilder.DropIndex(
                name: "IX_AuthorizationCodes_UserId",
                table: "AuthorizationCodes");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_UserId",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "OAuthTokens");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "AuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "Disabled",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ExternalIssuer",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ExternalSubject",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ApiKeys");

            migrationBuilder.AddColumn<Guid>(
                name: "ApiKeyId",
                table: "OAuthTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApiKeyId",
                table: "AuthorizationCodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "AllowedServers",
                table: "ApiKeys",
                type: "text[]",
                nullable: false,
                defaultValue: Array.Empty<string>());

            // Best effort: personal access tokens go back to Client (2), bridge keys stay Bridge (1). Dropped tokens and server lists are not restored.
            migrationBuilder.Sql("""UPDATE "ApiKeys" SET "Purpose" = CASE WHEN "Purpose" = 0 THEN 2 ELSE 1 END;""");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthTokens_ApiKeyId",
                table: "OAuthTokens",
                column: "ApiKeyId");
        }
    }
}
