using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableAnalysisQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Analyses_CommitId",
                table: "Analyses");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                table: "Analyses",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Analyses",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionToken",
                table: "Analyses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiration",
                table: "Analyses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_CommitId_CreatedAt",
                table: "Analyses",
                columns: new[] { "CommitId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_Status_CreatedAt",
                table: "Analyses",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Analyses_CommitId_CreatedAt",
                table: "Analyses");

            migrationBuilder.DropIndex(
                name: "IX_Analyses_Status_CreatedAt",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "ExecutionToken",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "LeaseExpiration",
                table: "Analyses");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                table: "Analyses",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_CommitId",
                table: "Analyses",
                column: "CommitId");
        }
    }
}
