using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalInformationSystem.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddAuthenticationSessionsAndAdministratorSetup : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "authentication_version",
            table: "refresh_sessions",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "administrator_setup_challenges",
            columns: table => new
            {
                challenge_id = table.Column<Guid>(type: "uuid", nullable: false),
                administrator_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_name = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                password_hash = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                totp_secret_protected = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                failed_attempts = table.Column<int>(type: "integer", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_administrator_setup_challenges", x => x.challenge_id);
                table.CheckConstraint("ck_setup_challenge_attempts", "failed_attempts >= 0");
                table.CheckConstraint("ck_setup_challenge_dates", "expires_at_utc > created_at_utc");
            });

        migrationBuilder.CreateTable(
            name: "administrator_totp_verification",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                last_accepted_step = table.Column<long>(type: "bigint", nullable: false),
                failed_attempts = table.Column<int>(type: "integer", nullable: false),
                locked_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_administrator_totp_verification", x => x.user_id);
                table.CheckConstraint("ck_totp_failed_attempts", "failed_attempts >= 0");
                table.CheckConstraint("ck_totp_step", "last_accepted_step >= 0");
                table.ForeignKey(
                    name: "FK_administrator_totp_verification_AspNetUsers_user_id",
                    column: x => x.user_id,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_administrator_setup_challenges_expires_at_utc",
            table: "administrator_setup_challenges",
            column: "expires_at_utc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "administrator_setup_challenges");

        migrationBuilder.DropTable(
            name: "administrator_totp_verification");

        migrationBuilder.DropColumn(
            name: "authentication_version",
            table: "refresh_sessions");
    }
}
