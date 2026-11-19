using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waypoint.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(WaypointDb))]
    [Migration("20261119170000_AddMilestonesAndMeasures")]
    public partial class AddMilestonesAndMeasures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(name: "ReachedAt", table: "Milestones", type: "datetime(6)", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "AskedAt", table: "Milestones", type: "datetime(6)", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "NotReachedAt", table: "Milestones", type: "datetime(6)", nullable: true);
            migrationBuilder.AddColumn<string>(name: "MeasureName", table: "Milestones", type: "varchar(64)", maxLength: 64, nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<string>(name: "MeasureUnit", table: "Milestones", type: "varchar(24)", maxLength: 24, nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<double>(name: "MeasureStart", table: "Milestones", type: "double", nullable: true);
            migrationBuilder.AddColumn<double>(name: "MeasureHopedFor", table: "Milestones", type: "double", nullable: true);
            migrationBuilder.AddColumn<string>(name: "TestForMilestoneRef", table: "ScheduledActions", type: "varchar(32)", maxLength: 32, nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "MeasureReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AspirationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    MilestoneRef = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<double>(type: "double", nullable: false),
                    Source = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClientId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasureReadings", x => x.Id);
                    table.ForeignKey(name: "FK_MeasureReadings_Aspirations_AspirationId", column: x => x.AspirationId,
                        principalTable: "Aspirations", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(name: "FK_MeasureReadings_Users_UserId", column: x => x.UserId,
                        principalTable: "Users", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(name: "IX_MeasureReadings_AspirationId", table: "MeasureReadings", column: "AspirationId");
            migrationBuilder.CreateIndex(name: "IX_MeasureReadings_UserId_AspirationId_Date", table: "MeasureReadings", columns: new[] { "UserId", "AspirationId", "Date" });
            migrationBuilder.CreateIndex(name: "IX_MeasureReadings_UserId_ClientId", table: "MeasureReadings", columns: new[] { "UserId", "ClientId" }, unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MeasureReadings");
            migrationBuilder.DropColumn(name: "ReachedAt", table: "Milestones");
            migrationBuilder.DropColumn(name: "AskedAt", table: "Milestones");
            migrationBuilder.DropColumn(name: "NotReachedAt", table: "Milestones");
            migrationBuilder.DropColumn(name: "MeasureName", table: "Milestones");
            migrationBuilder.DropColumn(name: "MeasureUnit", table: "Milestones");
            migrationBuilder.DropColumn(name: "MeasureStart", table: "Milestones");
            migrationBuilder.DropColumn(name: "MeasureHopedFor", table: "Milestones");
            migrationBuilder.DropColumn(name: "TestForMilestoneRef", table: "ScheduledActions");
        }
    }
}
