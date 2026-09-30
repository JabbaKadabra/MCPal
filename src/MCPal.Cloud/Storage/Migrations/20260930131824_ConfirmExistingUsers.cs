using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCPal.Cloud.Storage.Migrations
{
    /// <summary>
    /// Portal sign-in now requires a confirmed email address. Accounts created before that (no confirmation mail was ever
    /// sent to them) are marked confirmed, so an upgrade does not lock anyone out. Only new accounts start unconfirmed.
    /// </summary>
    public partial class ConfirmExistingUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "AspNetUsers" SET "EmailConfirmed" = TRUE WHERE "EmailConfirmed" = FALSE;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Confirmed accounts cannot be told apart from the ones that confirmed themselves; nothing to undo.
        }
    }
}
