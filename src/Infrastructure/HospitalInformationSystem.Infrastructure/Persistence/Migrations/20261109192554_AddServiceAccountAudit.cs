using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalInformationSystem.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddServiceAccountAudit : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_audit_actor",
            table: "security_audit_events");

        migrationBuilder.AddColumn<Guid>(
            name: "actor_service_account_id",
            table: "security_audit_events",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_security_audit_events_actor_service_account_id",
            table: "security_audit_events",
            column: "actor_service_account_id");

        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_actor",
            table: "security_audit_events",
            sql: "(actor_kind = 'user' AND actor_user_id IS NOT NULL AND actor_service_account_id IS NULL)\nOR (actor_kind = 'service' AND actor_user_id IS NULL AND actor_service_account_id IS NOT NULL)\nOR (actor_kind IN ('anonymous', 'system') AND actor_user_id IS NULL AND actor_service_account_id IS NULL)");

        migrationBuilder.AddForeignKey(
            name: "FK_security_audit_events_service_accounts_actor_service_accoun~",
            table: "security_audit_events",
            column: "actor_service_account_id",
            principalTable: "service_accounts",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE security_audit_events IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            BEGIN
                IF EXISTS (SELECT 1 FROM security_audit_events WHERE actor_kind = 'service') THEN
                    RAISE EXCEPTION 'Нельзя отменить поддержку сервисного аудита: журнал содержит события сервисных аккаунтов.';
                END IF;
            END;
            $guard$;
            """);

        migrationBuilder.DropForeignKey(
            name: "FK_security_audit_events_service_accounts_actor_service_accoun~",
            table: "security_audit_events");

        migrationBuilder.DropIndex(
            name: "IX_security_audit_events_actor_service_account_id",
            table: "security_audit_events");

        migrationBuilder.DropCheckConstraint(
            name: "ck_audit_actor",
            table: "security_audit_events");

        migrationBuilder.DropColumn(
            name: "actor_service_account_id",
            table: "security_audit_events");

        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_actor",
            table: "security_audit_events",
            sql: "actor_kind IN ('user', 'anonymous', 'system') AND ((actor_kind = 'user' AND actor_user_id IS NOT NULL) OR (actor_kind <> 'user' AND actor_user_id IS NULL))");
    }
}
