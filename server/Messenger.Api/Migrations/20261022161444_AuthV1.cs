using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messenger.Api.Migrations
{
    /// <inheritdoc />
    public partial class AuthV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "AuthKey",
                table: "devices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PhoneIndex",
                table: "accounts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PinFailures",
                table: "accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PinHash",
                table: "accounts",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PinLockedUntil",
                table: "accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "auth_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestHash = table.Column<byte[]>(type: "bytea", nullable: true),
                    Operation = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    PhoneIndex = table.Column<byte[]>(type: "bytea", nullable: true),
                    Region = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    CodeVerifier = table.Column<byte[]>(type: "bytea", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<int>(type: "integer", nullable: false),
                    IdentityKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    AuthKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_challenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "auth_counters",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    WindowEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_counters", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "refresh_families",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AuthenticatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_families", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_families_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Verifier = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsumedByChallenge = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplacedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_refresh_families_FamilyId",
                        column: x => x.FamilyId,
                        principalTable: "refresh_families",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_PhoneIndex",
                table: "accounts",
                column: "PhoneIndex",
                unique: true,
                filter: "\"PhoneIndex\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_auth_challenges_ExpiresAt",
                table: "auth_challenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_auth_challenges_PhoneIndex_CreatedAt",
                table: "auth_challenges",
                columns: new[] { "PhoneIndex", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_auth_challenges_RequestId",
                table: "auth_challenges",
                column: "RequestId",
                unique: true,
                filter: "\"RequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_auth_challenges_State_CreatedAt",
                table: "auth_challenges",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_auth_counters_WindowEnd",
                table: "auth_counters",
                column: "WindowEnd");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_families_AccountId",
                table: "refresh_families",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_families_DeviceId",
                table: "refresh_families",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_ExpiresAt",
                table: "refresh_tokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_FamilyId",
                table: "refresh_tokens",
                column: "FamilyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_challenges");

            migrationBuilder.DropTable(
                name: "auth_counters");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "refresh_families");

            migrationBuilder.DropIndex(
                name: "IX_accounts_PhoneIndex",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "AuthKey",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "PhoneIndex",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "PinFailures",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "PinHash",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "PinLockedUntil",
                table: "accounts");
        }
    }
}
