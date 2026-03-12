using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Cashu.Data.Migrations
{
    /// <inheritdoc />
    public partial class CashuLightningPayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CashuLightningClientPaymentId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "WalletMnemonic",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "CashuWalletConfig",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LightningClientSecret",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "CashuWalletConfig",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LightningPayments",
                schema: "BTCPayServer.Plugins.Cashu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: true),
                    Mint = table.Column<string>(type: "text", nullable: true),
                    QuoteId = table.Column<string>(type: "text", nullable: true),
                    QuoteState = table.Column<string>(type: "text", nullable: true),
                    PaymentHash = table.Column<string>(type: "text", nullable: true),
                    Bolt11 = table.Column<string>(type: "text", nullable: true),
                    Preimage = table.Column<string>(type: "text", nullable: true),
                    Amount = table.Column<long>(type: "bigint", nullable: true),
                    FeeAmount = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LightningPayments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Proofs_CashuLightningClientPaymentId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                column: "CashuLightningClientPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_LightningPayments_Mint_QuoteState",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "LightningPayments",
                columns: new[] { "Mint", "QuoteState" });

            migrationBuilder.CreateIndex(
                name: "IX_LightningPayments_PaymentHash",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "LightningPayments",
                column: "PaymentHash");

            migrationBuilder.CreateIndex(
                name: "IX_LightningPayments_QuoteId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "LightningPayments",
                column: "QuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_LightningPayments_StoreId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "LightningPayments",
                column: "StoreId");

            migrationBuilder.AddForeignKey(
                name: "FK_Proofs_LightningPayments_CashuLightningClientPaymentId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                column: "CashuLightningClientPaymentId",
                principalSchema: "BTCPayServer.Plugins.Cashu",
                principalTable: "LightningPayments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Proofs_LightningPayments_CashuLightningClientPaymentId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.DropTable(
                name: "LightningPayments",
                schema: "BTCPayServer.Plugins.Cashu");

            migrationBuilder.DropIndex(
                name: "IX_Proofs_CashuLightningClientPaymentId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.DropColumn(
                name: "CashuLightningClientPaymentId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.DropColumn(
                name: "LightningClientSecret",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "CashuWalletConfig");

            migrationBuilder.AlterColumn<string>(
                name: "WalletMnemonic",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "CashuWalletConfig",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
