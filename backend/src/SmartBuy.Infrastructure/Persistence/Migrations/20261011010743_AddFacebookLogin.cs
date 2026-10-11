using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBuy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFacebookLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FacebookSubject",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_FacebookSubject",
                table: "Users",
                column: "FacebookSubject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_FacebookSubject",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FacebookSubject",
                table: "Users");
        }
    }
}
