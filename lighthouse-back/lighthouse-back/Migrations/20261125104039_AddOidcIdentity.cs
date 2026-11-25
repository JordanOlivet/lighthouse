using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lighthouse.Migrations
{
    /// <inheritdoc />
    public partial class AddOidcIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "Users",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "OidcIssuer",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OidcSubject",
                table: "Users",
                type: "TEXT",
                nullable: true);


            migrationBuilder.CreateIndex(
                name: "IX_Users_OidcIssuer_OidcSubject",
                table: "Users",
                columns: new[] { "OidcIssuer", "OidcSubject" },
                unique: true,
                filter: "\"OidcSubject\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_OidcIssuer_OidcSubject",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OidcIssuer",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OidcSubject",
                table: "Users");

            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
