using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Fireblocks.Data.Migrations;

[DbContext(typeof(FireblocksDbContext))]
[Migration("20261031000000_AddFlowPaymentAttempts")]
public sealed class AddFlowPaymentAttempts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("Fireblocks");
        migrationBuilder.CreateTable(
            name: "fireblocks_payment_attempts",
            schema: "Fireblocks",
            columns: table => new
            {
                id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                store_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                invoice_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                requested_usd = table.Column<decimal>(type: "numeric(38,18)", nullable: false),
                creation_request = table.Column<string>(type: "text", nullable: false),
                state = table.Column<int>(type: "integer", nullable: false),
                flow_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                payment_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_fireblocks_payment_attempts", x => x.id));
        migrationBuilder.CreateIndex(
            name: "ix_fireblocks_attempts_invoice_created",
            schema: "Fireblocks",
            table: "fireblocks_payment_attempts",
            columns: ["store_id", "invoice_id", "created_at"]);
        migrationBuilder.CreateIndex(
            name: "ix_fireblocks_attempts_flow_id",
            schema: "Fireblocks",
            table: "fireblocks_payment_attempts",
            column: "flow_id",
            unique: true,
            filter: "flow_id IS NOT NULL");
    }
}
