using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foxfire.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Videos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character(11)", fixedLength: true, maxLength: 11, nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    processing_mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    upload_grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    upload_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    upload_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    upload_last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tus_file_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    declared_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    source_bytes = table.Column<long>(type: "bigint", nullable: false),
                    storage_bytes = table.Column<long>(type: "bigint", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    video_codec = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    audio_codec = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_videos", x => x.id);
                    table.ForeignKey(
                        name: "fk_videos_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_videos_application_id_created_at",
                table: "videos",
                columns: new[] { "application_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_videos_application_id_external_id",
                table: "videos",
                columns: new[] { "application_id", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_videos_public_id",
                table: "videos",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_videos_status",
                table: "videos",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_videos_tus_file_id",
                table: "videos",
                column: "tus_file_id",
                unique: true,
                filter: "tus_file_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "videos");
        }
    }
}
