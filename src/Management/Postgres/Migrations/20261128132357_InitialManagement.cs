using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cassini.Management.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cases",
                columns: table => new
                {
                    CaseId = table.Column<string>(type: "text", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cases", x => x.CaseId);
                });

            migrationBuilder.CreateTable(
                name: "command_receipts",
                columns: table => new
                {
                    CommandId = table.Column<string>(type: "text", nullable: false),
                    Identity = table.Column<string>(type: "text", nullable: false),
                    Result = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_command_receipts", x => x.CommandId);
                });

            migrationBuilder.CreateTable(
                name: "ewss_broadcasts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ewss_broadcasts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                columns: table => new
                {
                    Receipt = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => x.Receipt);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    IncidentId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.IncidentId);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alerts_Sequence",
                table: "alerts",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ewss_broadcasts_Sequence",
                table: "ewss_broadcasts",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_Sequence",
                table: "outbox",
                column: "Sequence",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "cases");

            migrationBuilder.DropTable(
                name: "command_receipts");

            migrationBuilder.DropTable(
                name: "ewss_broadcasts");

            migrationBuilder.DropTable(
                name: "inbox");

            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.DropTable(
                name: "outbox");
        }
    }
}
