using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Uniform.Matrix.Client.Migrations
{
    /// <inheritdoc />
    public partial class AddEventLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Line",
                table: "Events",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelatesTo",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_RelatesTo",
                table: "Events",
                column: "RelatesTo");

            migrationBuilder.CreateIndex(
                name: "IX_Events_RoomId_Line",
                table: "Events",
                columns: new[] { "RoomId", "Line" });

            // Stored events: the target of each change, as ClientStore.ChangeTarget finds it
            migrationBuilder.Sql("""
                UPDATE "Events" SET "RelatesTo" = CASE
                    WHEN "Type" = 'm.room.redaction' THEN coalesce(json_extract("Json", '$.redacts'), json_extract("Json", '$.content.redacts'))
                    WHEN "StateKey" IS NULL AND json_extract("Json", '$.content."m.relates_to".rel_type') IN ('m.annotation', 'm.replace') THEN json_extract("Json", '$.content."m.relates_to".event_id')
                END;
                """);

            // Lines of their own from time, with the count of earlier events of the same millisecond
            migrationBuilder.Sql("""
                UPDATE "Events" SET "Line" = numbered."Line"
                FROM (
                    SELECT "EventId", "OriginServerTs" * 1024 + min(ROW_NUMBER() OVER (PARTITION BY "RoomId", "OriginServerTs" ORDER BY "EventId") - 1, 1023) AS "Line"
                    FROM "Events"
                    WHERE "RelatesTo" IS NULL AND "Type" NOT IN ('m.room.redaction', 'm.reaction')
                ) AS numbered
                WHERE "Events"."EventId" = numbered."EventId";
                """);

            // Changes onto their target's line; twice, for a redaction of a reaction
            for (int pass = 0; pass < 2; pass++)
            {
                migrationBuilder.Sql("""
                    UPDATE "Events" SET "Line" = (SELECT target."Line" FROM "Events" AS target WHERE target."EventId" = "Events"."RelatesTo")
                    WHERE "RelatesTo" IS NOT NULL AND "Line" IS NULL;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_RelatesTo",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_RoomId_Line",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Line",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "RelatesTo",
                table: "Events");
        }
    }
}
