using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waypoint.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CheckIns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ScheduledActionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ClientId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    State = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MinutesSpent = table.Column<int>(type: "int", nullable: true),
                    Reasons = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Note = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TappedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EchoMessageId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckIns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckIns_ScheduledActions_ScheduledActionId",
                        column: x => x.ScheduledActionId,
                        principalTable: "ScheduledActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CheckIns_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CheckIns_ScheduledActionId",
                table: "CheckIns",
                column: "ScheduledActionId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckIns_UserId_ClientId",
                table: "CheckIns",
                columns: new[] { "UserId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CheckIns_UserId_ScheduledActionId",
                table: "CheckIns",
                columns: new[] { "UserId", "ScheduledActionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckIns");
        }
    }
}
