using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Cashu.Data.Migrations
{
    /// <inheritdoc />
    public partial class CashuLightningInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LightningInvoices",
                schema: "BTCPayServer.Plugins.Cashu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Mint = table.Column<string>(type: "text", nullable: true),
                    StoreId = table.Column<string>(type: "text", nullable: true),
                    QuoteId = table.Column<string>(type: "text", nullable: true),
                    KeysetId = table.Column<string>(type: "text", nullable: true),
                    OutputData = table.Column<string>(type: "text", nullable: true),
                    QuoteState = table.Column<string>(type: "text", nullable: true),
                    InvoiceId = table.Column<string>(type: "text", nullable: true),
                    Amount = table.Column<long>(type: "bigint", nullable: true),
                    Bolt11 = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Expiry = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LightningInvoices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LightningInvoices_InvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "LightningInvoices",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_LightningInvoices_Mint_QuoteState",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "LightningInvoices",
                columns: new[] { "Mint", "QuoteState" });

            migrationBuilder.CreateIndex(
                name: "IX_LightningInvoices_QuoteId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "LightningInvoices",
                column: "QuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_LightningInvoices_StoreId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "LightningInvoices",
                column: "StoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LightningInvoices",
                schema: "BTCPayServer.Plugins.Cashu");
        }
    }
}
