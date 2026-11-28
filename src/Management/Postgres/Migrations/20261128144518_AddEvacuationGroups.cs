using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cassini.Management.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddEvacuationGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evacuation_groups",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evacuation_groups", x => x.GroupId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evacuation_groups");
        }
    }
}
