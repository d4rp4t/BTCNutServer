using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Cashu.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropInvoiceStatusColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "LightningInvoices");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "BTCPayServer.Plugins.Cashu",
                table: "LightningInvoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
