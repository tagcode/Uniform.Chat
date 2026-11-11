using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Uniform.Matrix.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDelayedEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DelayedEvents",
                columns: table => new
                {
                    DelayId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    RoomId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    StateKey = table.Column<string>(type: "TEXT", nullable: true),
                    ContentJson = table.Column<string>(type: "TEXT", nullable: false),
                    DelayMs = table.Column<long>(type: "INTEGER", nullable: false),
                    RunningSinceTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DelayedEvents", x => x.DelayId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DelayedEvents_UserId",
                table: "DelayedEvents",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DelayedEvents");
        }
    }
}
