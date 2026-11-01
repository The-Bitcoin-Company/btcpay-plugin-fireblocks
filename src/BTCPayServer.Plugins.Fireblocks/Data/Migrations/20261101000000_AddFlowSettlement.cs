using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Fireblocks.Data.Migrations;

[DbContext(typeof(FireblocksDbContext))]
[Migration("20261101000000_AddFlowSettlement")]
public sealed class AddFlowSettlement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_reconciled_at", schema: "Fireblocks", table: "fireblocks_payment_attempts",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "provider_status", schema: "Fireblocks", table: "fireblocks_payment_attempts",
            type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "risk_status", schema: "Fireblocks", table: "fireblocks_payment_attempts",
            type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "settlement_transaction_id", schema: "Fireblocks", table: "fireblocks_payment_attempts",
            type: "character varying(128)", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "settlement_amount_atomic", schema: "Fireblocks", table: "fireblocks_payment_attempts",
            type: "character varying(78)", maxLength: 78, nullable: true);
        migrationBuilder.AddColumn<decimal>(
            name: "settlement_amount", schema: "Fireblocks", table: "fireblocks_payment_attempts",
            type: "numeric(38,18)", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "settlement_completed_at", schema: "Fireblocks", table: "fireblocks_payment_attempts",
            type: "timestamp with time zone", nullable: true);

        migrationBuilder.CreateTable(
            name: "fireblocks_webhook_events",
            schema: "Fireblocks",
            columns: table => new
            {
                id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                store_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                flow_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_fireblocks_webhook_events", x => x.id));
        migrationBuilder.CreateIndex(
            name: "ix_fireblocks_webhooks_store_flow",
            schema: "Fireblocks",
            table: "fireblocks_webhook_events",
            columns: ["store_id", "flow_id"]);
    }
}
