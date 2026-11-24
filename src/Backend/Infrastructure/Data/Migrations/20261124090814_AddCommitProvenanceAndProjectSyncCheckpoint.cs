using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommitProvenanceAndProjectSyncCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSyncedAt",
                table: "Projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncedBranch",
                table: "Projects",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncedCommitHash",
                table: "Projects",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncedRepositoryHost",
                table: "Projects",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncedRepositoryId",
                table: "Projects",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsVerified",
                table: "Commits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string[]>(
                name: "ParentHashes",
                table: "Commits",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSyncedAt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LastSyncedBranch",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LastSyncedCommitHash",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LastSyncedRepositoryHost",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LastSyncedRepositoryId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "IsVerified",
                table: "Commits");

            migrationBuilder.DropColumn(
                name: "ParentHashes",
                table: "Commits");
        }
    }
}
