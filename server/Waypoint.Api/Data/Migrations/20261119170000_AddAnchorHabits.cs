using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waypoint.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnchorHabits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "SlotId",
                table: "ScheduledActions",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "AnchorId",
                table: "ScheduledActions",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<bool>(
                name: "AtRisk",
                table: "ScheduledActions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TargetMinutes",
                table: "ScheduledActions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AnchorId",
                table: "Habits",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<int>(
                name: "FullTimesPerWeek",
                table: "Habits",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrepFor",
                table: "Habits",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "ReminderOn",
                table: "Habits",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "Since",
                table: "Habits",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StopOffered",
                table: "Habits",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "StoppedByOffer",
                table: "Habits",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TargetMinutes",
                table: "Habits",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnchorQueueJson",
                table: "Aspirations",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Anchors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Words = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Time = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Days = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Anchors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Anchors_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledActions_AnchorId",
                table: "ScheduledActions",
                column: "AnchorId");

            migrationBuilder.CreateIndex(
                name: "IX_Anchors_UserId",
                table: "Anchors",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ScheduledActions_Anchors_AnchorId",
                table: "ScheduledActions",
                column: "AnchorId",
                principalTable: "Anchors",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ScheduledActions_Anchors_AnchorId",
                table: "ScheduledActions");

            migrationBuilder.DropTable(
                name: "Anchors");

            migrationBuilder.DropIndex(
                name: "IX_ScheduledActions_AnchorId",
                table: "ScheduledActions");

            migrationBuilder.DropColumn(
                name: "AnchorId",
                table: "ScheduledActions");

            migrationBuilder.DropColumn(
                name: "AtRisk",
                table: "ScheduledActions");

            migrationBuilder.DropColumn(
                name: "TargetMinutes",
                table: "ScheduledActions");

            migrationBuilder.DropColumn(
                name: "AnchorId",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "FullTimesPerWeek",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "PrepFor",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "ReminderOn",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "Since",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "StopOffered",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "StoppedByOffer",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "TargetMinutes",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "AnchorQueueJson",
                table: "Aspirations");

            migrationBuilder.AlterColumn<Guid>(
                name: "SlotId",
                table: "ScheduledActions",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");
        }
    }
}
