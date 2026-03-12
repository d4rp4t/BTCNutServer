using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Cashu.Data.Migrations
{
    /// <inheritdoc />
    public partial class LightningInvoiceProofs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CashuLightningClientInvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Proofs_CashuLightningClientInvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                column: "CashuLightningClientInvoiceId");

            migrationBuilder.AddForeignKey(
                name: "FK_Proofs_LightningInvoices_CashuLightningClientInvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs",
                column: "CashuLightningClientInvoiceId",
                principalSchema: "BTCPayServer.Plugins.Cashu",
                principalTable: "LightningInvoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Proofs_LightningInvoices_CashuLightningClientInvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.DropIndex(
                name: "IX_Proofs_CashuLightningClientInvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");

            migrationBuilder.DropColumn(
                name: "CashuLightningClientInvoiceId",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "Proofs");
        }
    }
}
