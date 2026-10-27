using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Uniform.Matrix.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPushers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Pushers",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    AppId = table.Column<string>(type: "TEXT", nullable: false),
                    PushKey = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    AppDisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceDisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileTag = table.Column<string>(type: "TEXT", nullable: true),
                    Lang = table.Column<string>(type: "TEXT", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pushers", x => new { x.UserId, x.AppId, x.PushKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Pushers_AppId_PushKey",
                table: "Pushers",
                columns: new[] { "AppId", "PushKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Pushers");
        }
    }
}
