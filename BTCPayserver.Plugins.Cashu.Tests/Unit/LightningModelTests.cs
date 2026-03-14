using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class LightningModelTests
{
    private static CashuLightningClientInvoice MakeInvoice(
        string quoteState = "UNPAID",
        DateTimeOffset? expiry = null) => new()
    {
        Id = Guid.NewGuid(),
        StoreId = "store",
        Mint = "https://mint.test",
        QuoteId = "quote1",
        InvoiceId = "inv1",
        KeysetId = new KeysetId("000000000000001a"),
        OutputData = [],
        QuoteState = quoteState,
        Amount = LightMoney.Satoshis(1000),
        Bolt11 = "lnbc1...",
        Created = DateTimeOffset.UtcNow,
        Expiry = expiry ?? DateTimeOffset.UtcNow.AddHours(1),
    };

    private static CashuLightningClientPayment MakePayment(
        string quoteState = "PAID",
        LightMoney? feeAmount = null) => new()
    {
        Id = Guid.NewGuid(),
        StoreId = "store",
        Mint = "https://mint.test",
        QuoteId = "quote2",
        QuoteState = quoteState,
        PaymentHash = "abc123",
        Bolt11 = "lnbc1...",
        Preimage = "preimage123",
        Amount = LightMoney.Satoshis(500),
        FeeAmount = feeAmount,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // ── Invoice.Status (computed property) ────────────────────────────────────

    [Theory]
    [InlineData("ISSUED", LightningInvoiceStatus.Paid)]
    [InlineData("issued", LightningInvoiceStatus.Paid)]   // case-insensitive
    [InlineData("PAID", LightningInvoiceStatus.Unpaid)]   // PAID = LN received, tokens not yet minted
    [InlineData("UNPAID", LightningInvoiceStatus.Unpaid)]
    [InlineData("EXPIRED", LightningInvoiceStatus.Expired)]
    [InlineData("expired", LightningInvoiceStatus.Expired)]
    [InlineData("UNKNOWN_STATE", LightningInvoiceStatus.Unpaid)]
    [InlineData(null, LightningInvoiceStatus.Unpaid)]
    public void Invoice_Status_MapsCorrectly(string? quoteState, LightningInvoiceStatus expected)
    {
        var inv = MakeInvoice(quoteState!);
        inv.QuoteState = quoteState!;
        Assert.Equal(expected, inv.Status);
    }

    [Fact]
    public void Invoice_Status_UNPAID_NotExpired_IsUnpaid()
    {
        var inv = MakeInvoice(expiry: DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(LightningInvoiceStatus.Unpaid, inv.Status);
    }

    [Fact]
    public void Invoice_Status_UNPAID_AlreadyExpired_IsExpired()
    {
        var inv = MakeInvoice(expiry: DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.Equal(LightningInvoiceStatus.Expired, inv.Status);
    }

    [Fact]
    public void ToLightningInvoice_MapsAllFields()
    {
        var paidAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var inv = MakeInvoice("ISSUED", expiry: expiry);
        inv.InvoiceId = "myid";
        inv.Bolt11 = "lnbcrt1...";
        inv.Amount = LightMoney.Satoshis(42_000);
        inv.PaidAt = paidAt;

        var li = inv.ToLightningInvoice();

        Assert.Equal("myid", li.Id);
        Assert.Equal("lnbcrt1...", li.BOLT11);
        Assert.Equal(LightMoney.Satoshis(42_000), li.Amount);
        Assert.Equal(LightningInvoiceStatus.Paid, li.Status);
        Assert.Equal(expiry, li.ExpiresAt);
        Assert.Equal(paidAt, li.PaidAt);
    }

    [Fact]
    public void ToLightningInvoice_NullPaidAt_IsPreserved()
    {
        var inv = MakeInvoice();
        inv.PaidAt = null;

        var li = inv.ToLightningInvoice();

        Assert.Null(li.PaidAt);
    }

    [Theory]
    [InlineData("PAID", LightningPaymentStatus.Complete)]
    [InlineData("PENDING", LightningPaymentStatus.Pending)]
    [InlineData("UNPAID", LightningPaymentStatus.Pending)]
    [InlineData("EXPIRED", LightningPaymentStatus.Unknown)]
    [InlineData("BOGUS", LightningPaymentStatus.Unknown)]
    [InlineData(null, LightningPaymentStatus.Unknown)]
    public void Payment_QuoteState_MapsToCorrectStatus(string? quoteState, LightningPaymentStatus expected)
    {
        var p = MakePayment(quoteState!);
        p.QuoteState = quoteState!;
        Assert.Equal(expected, p.ToLightningPayment().Status);
    }

    [Fact]
    public void ToLightningPayment_MapsAllFields()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var payment = MakePayment(feeAmount: LightMoney.Satoshis(10));
        payment.PaymentHash = "deadbeef";
        payment.Preimage = "preim";
        payment.Amount = LightMoney.Satoshis(200);
        payment.FeeAmount = LightMoney.Satoshis(5);
        payment.Bolt11 = "lnbcrt2...";
        payment.CreatedAt = createdAt;

        var lp = payment.ToLightningPayment();

        Assert.Equal("deadbeef", lp.Id);
        Assert.Equal("deadbeef", lp.PaymentHash);
        Assert.Equal("preim", lp.Preimage);
        Assert.Equal(LightMoney.Satoshis(200), lp.Amount);
        Assert.Equal(LightMoney.Satoshis(5), lp.Fee);
        Assert.Equal(LightMoney.Satoshis(205), lp.AmountSent);
        Assert.Equal("lnbcrt2...", lp.BOLT11);
        Assert.Equal(createdAt, lp.CreatedAt);
        Assert.Equal(LightningPaymentStatus.Complete, lp.Status);
    }

    [Fact]
    public void ToLightningPayment_NullFee_AmountSentEqualsAmount()
    {
        var payment = MakePayment(feeAmount: null);
        payment.Amount = LightMoney.Satoshis(300);
        payment.FeeAmount = null;

        var lp = payment.ToLightningPayment();

        Assert.Null(lp.Fee);
        Assert.Equal(LightMoney.Satoshis(300), lp.AmountSent);
    }

    [Fact]
    public void ToLightningPayment_EmptyPreimage_IsNullInResult()
    {
        var payment = MakePayment();
        payment.Preimage = "";

        var lp = payment.ToLightningPayment();

        Assert.Null(lp.Preimage);
    }
}
