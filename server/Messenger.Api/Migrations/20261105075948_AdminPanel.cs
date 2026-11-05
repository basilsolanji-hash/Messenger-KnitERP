using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Messenger.Api.Migrations
{
    /// <inheritdoc />
    public partial class AdminPanel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuspendReason",
                table: "accounts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SuspendedAt",
                table: "accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "admin_audit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    Login = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Remote = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Target = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Detail = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "admin_users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Login = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TotpSecret = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    TotpLastStep = table.Column<long>(type: "bigint", nullable: false),
                    FailedLogins = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "admin_sessions",
                columns: table => new
                {
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    AdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Network = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_sessions", x => x.TokenHash);
                    table.ForeignKey(
                        name: "FK_admin_sessions_admin_users_AdminId",
                        column: x => x.AdminId,
                        principalTable: "admin_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_Action",
                table: "admin_audit",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_At",
                table: "admin_audit",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_AdminId",
                table: "admin_sessions",
                column: "AdminId");

            migrationBuilder.CreateIndex(
                name: "IX_admin_users_Login",
                table: "admin_users",
                column: "Login",
                unique: true);

            // The audit log is append-only for the application: UPDATE, DELETE and TRUNCATE are refused by the database.
            migrationBuilder.Sql("""
                CREATE FUNCTION admin_audit_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'admin_audit is append-only'; END $$;
                CREATE TRIGGER admin_audit_no_change BEFORE UPDATE OR DELETE ON admin_audit
                    FOR EACH ROW EXECUTE FUNCTION admin_audit_append_only();
                CREATE TRIGGER admin_audit_no_truncate BEFORE TRUNCATE ON admin_audit
                    FOR EACH STATEMENT EXECUTE FUNCTION admin_audit_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS admin_audit_no_truncate ON admin_audit; DROP TRIGGER IF EXISTS admin_audit_no_change ON admin_audit; DROP FUNCTION IF EXISTS admin_audit_append_only();");
            migrationBuilder.DropTable(
                name: "admin_audit");

            migrationBuilder.DropTable(
                name: "admin_sessions");

            migrationBuilder.DropTable(
                name: "admin_users");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "SuspendReason",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "SuspendedAt",
                table: "accounts");
        }
    }
}
