using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalInformationSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceRefreshSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_refresh_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    authentication_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replaced_by_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoke_reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_refresh_sessions", x => x.id);
                    table.UniqueConstraint("AK_service_refresh_sessions_id_service_account_id_family_id", x => new { x.id, x.service_account_id, x.family_id });
                    table.CheckConstraint("ck_service_refresh_dates", "expires_at_utc > created_at_utc\nAND (consumed_at_utc IS NULL OR consumed_at_utc >= created_at_utc)\nAND (revoked_at_utc IS NULL OR revoked_at_utc >= created_at_utc)");
                    table.CheckConstraint("ck_service_refresh_hash", "token_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_service_refresh_ids", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND family_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_service_refresh_replacement", "(consumed_at_utc IS NULL AND replaced_by_session_id IS NULL)\nOR (consumed_at_utc IS NOT NULL AND replaced_by_session_id IS NOT NULL AND replaced_by_session_id <> id)");
                    table.CheckConstraint("ck_service_refresh_revoke_reason", "(revoked_at_utc IS NULL AND revoke_reason IS NULL)\nOR (revoked_at_utc IS NOT NULL AND revoke_reason IS NOT NULL\n    AND char_length(btrim(revoke_reason)) BETWEEN 1 AND 128)");
                    table.CheckConstraint("ck_service_refresh_version", "authentication_version > 0");
                    table.ForeignKey(
                        name: "FK_service_refresh_sessions_service_accounts_service_account_id",
                        column: x => x.service_account_id,
                        principalTable: "service_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_refresh_sessions_service_refresh_sessions_replaced_~",
                        columns: x => new { x.replaced_by_session_id, x.service_account_id, x.family_id },
                        principalTable: "service_refresh_sessions",
                        principalColumns: new[] { "id", "service_account_id", "family_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_service_refresh_sessions_family_id",
                table: "service_refresh_sessions",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_refresh_sessions_replaced_by_session_id_service_acc~",
                table: "service_refresh_sessions",
                columns: new[] { "replaced_by_session_id", "service_account_id", "family_id" });

            migrationBuilder.CreateIndex(
                name: "IX_service_refresh_sessions_service_account_id_revoked_at_utc",
                table: "service_refresh_sessions",
                columns: new[] { "service_account_id", "revoked_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_service_refresh_sessions_token_hash",
                table: "service_refresh_sessions",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE service_refresh_sessions IN ACCESS EXCLUSIVE MODE;
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM service_refresh_sessions) THEN
                        RAISE EXCEPTION 'Нельзя отменить поддержку сервисных сессий: таблица содержит сохранённые сессии.';
                    END IF;
                END;
                $guard$;
                """);

            migrationBuilder.DropTable(
                name: "service_refresh_sessions");
        }
    }
}
