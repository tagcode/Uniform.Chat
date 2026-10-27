using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Uniform.Matrix.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteInvites",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RoomId = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    Sender = table.Column<string>(type: "TEXT", nullable: false),
                    RoomVersion = table.Column<string>(type: "TEXT", nullable: false),
                    EventJson = table.Column<string>(type: "TEXT", nullable: false),
                    StrippedStateJson = table.Column<string>(type: "TEXT", nullable: false),
                    Membership = table.Column<string>(type: "TEXT", nullable: false),
                    Stream = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteInvites", x => new { x.UserId, x.RoomId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteInvites_UserId_Stream",
                table: "RemoteInvites",
                columns: new[] { "UserId", "Stream" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteInvites");
        }
    }
}
