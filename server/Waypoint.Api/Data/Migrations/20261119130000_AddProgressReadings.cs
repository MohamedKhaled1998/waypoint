using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waypoint.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(WaypointDb))]
    [Migration("20261119130000_AddProgressReadings")]
    public partial class AddProgressReadings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProgressReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AspirationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RefreshId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PlanVersionId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    At = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Pace = table.Column<double>(type: "double", nullable: true),
                    State = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MiddleOver = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LateEarly = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PastSettleFor = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Spoke = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgressReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProgressReadings_Aspirations_AspirationId",
                        column: x => x.AspirationId,
                        principalTable: "Aspirations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProgressReadings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ProgressReadings_AspirationId",
                table: "ProgressReadings",
                column: "AspirationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProgressReadings_UserId_AspirationId_At",
                table: "ProgressReadings",
                columns: new[] { "UserId", "AspirationId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProgressReadings");
        }
    }
}
