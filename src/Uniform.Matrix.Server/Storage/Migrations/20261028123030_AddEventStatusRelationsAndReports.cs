using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Uniform.Matrix.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddEventStatusRelationsAndReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SoftFailed",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "StateGroupBefore",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "EventRelations",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    RelatesTo = table.Column<string>(type: "TEXT", nullable: false),
                    RelType = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventRelations", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Reporter = table.Column<string>(type: "TEXT", nullable: false),
                    RoomId = table.Column<string>(type: "TEXT", nullable: true),
                    EventId = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    Resolved = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventRelations_RelatesTo_RelType",
                table: "EventRelations",
                columns: new[] { "RelatesTo", "RelType" });

            // Relations of the events already stored
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO EventRelations (EventId, RelatesTo, RelType)
                SELECT EventId, json_extract(Json, '$.content."m.relates_to".event_id'), json_extract(Json, '$.content."m.relates_to".rel_type')
                FROM Events
                WHERE json_valid(Json)
                  AND json_type(Json, '$.content."m.relates_to".event_id') = 'text'
                  AND json_type(Json, '$.content."m.relates_to".rel_type') = 'text';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventRelations");

            migrationBuilder.DropTable(
                name: "Reports");

            migrationBuilder.DropColumn(
                name: "SoftFailed",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "StateGroupBefore",
                table: "Events");
        }
    }
}
