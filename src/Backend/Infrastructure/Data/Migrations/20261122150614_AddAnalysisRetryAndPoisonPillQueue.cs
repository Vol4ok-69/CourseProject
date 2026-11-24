using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisRetryAndPoisonPillQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "Analyses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxAttempts",
                table: "Analyses",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "Analyses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_Status_NextAttemptAt_CreatedAt",
                table: "Analyses",
                columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Analyses_AttemptCount",
                table: "Analyses",
                sql: "\"AttemptCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Analyses_MaxAttempts",
                table: "Analyses",
                sql: "\"MaxAttempts\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Analyses_Status_NextAttemptAt_CreatedAt",
                table: "Analyses");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Analyses_AttemptCount",
                table: "Analyses");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Analyses_MaxAttempts",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "MaxAttempts",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "Analyses");
        }
    }
}
