using System.Text.Json;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Errors;
using DotNut;
using NBitcoin;
using Xunit;
using Mnemonic = DotNut.NBitcoin.BIP39.Mnemonic;

namespace BTCPayserver.Plugins.Cashu.Tests
{
    public class CashuUtilsTests
    {

        public static string keysetPath =
            "/Users/d4rp4t/RiderProjects/BTCNutServer/BTCPayserver.Plugins.Cashu.Tests/Unit/keys.json";

        public Keyset testKeyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));

        public Mnemonic testMnemonic =
            new Mnemonic(
                "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");

        #region GetCashuHttpClient Tests

        [Fact]
        public void GetCashuHttpClient_ValidUrl_ReturnsClient()
        {
            string mintUrl = "https://examplemint.com";

            var client = CashuUtils.GetCashuHttpClient(mintUrl);

            Assert.NotNull(client);
        }

        [Fact]
        public void GetCashuHttpClient_InvalidUrl_ThrowsUriFormatException()
        {
            string invalidUrl = "invalid-url";

            Assert.Throws<UriFormatException>(() => CashuUtils.GetCashuHttpClient(invalidUrl));
        }

        [Fact]
        public async Task GetCashuHttpClient_TimeoutExceeded_ThrowsTimeoutException()
        {
            var mintUrl = "https://nofees.testnut.cashu.space";
            var client = CashuUtils.GetCashuHttpClient(mintUrl);

            await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await client.GetKeysets(new CancellationTokenSource(TimeSpan.FromMilliseconds(1)).Token));
        }

        #endregion

        #region GetTokenSatRate Tests

        [Fact]
        public async Task GetTokenSatRate_ValidToken_ReturnsCorrectRateForSat()
        {
            var token =
                "cashuBo2F0gaJhaUgAFo194X2Lm2Fwg6NhYQhhc3hANDE1ZDAxNWEzY2UwZTcxYTgyODMxMDgzNDRlMmJmMTNmNzZjNTM3MjdiNWIyNGI1ODViMTQ2Y2NlMmM3ZDVmOGFjWCEDgjrv5E_oIeBxJTJuLAkenzhRCTvYtIw5ymN6ah8egDGjYWEBYXN4QDk4OTFjYTg2OWY5ZjA2YjY2Zjk0YzRmYjE4OWM0YmQ0NjcyM2QzYzFhYzFjNTM3OWEzM2Y5NmI1MTliMDJhNmRhY1ghAv5YHuOCclMCO_VJ7FfuuMm48hIIAOR0WCIQ4pwFe6zQo2FhAWFzeEA4MDA0ZmIyMDM1MzQzYjJiOTZkYWNlNmNlMTcxNGQ3NGRiMGM2ZmNjYzQ4ZGJiYmQ3NDE3MzBiMWYwN2NiZWU3YWNYIQNpFWP0sn7lKRWi7hADXfj6PxmQb1ZB2k_9rszuhdikVmFtdWh0dHA6Ly8xMjcuMC4wLjE6MzMzOGF1Y3NhdA";
            var deserialized = CashuTokenHelper.Decode(token, out _);
            var network = Network.RegTest;

            var result = await CashuUtils.GetTokenSatRate(deserialized, network);

            Assert.Equal(1, result);
        }

        [Fact]
        public async Task GetTokenSatRate_ValidToken_ReturnsCorrectRateForUsdToken()
        {
            var token =
                "cashuBo2FteBtodHRwczovL3Rlc3RudXQuY2FzaHUuc3BhY2VhdWN1c2RhdIGiYWlIAMB0uWx-Kw5hcIOkYWEYQGFzeEBhMWRkYjRjMzgyMjM3MjU0NTYwMmViY2IzYzQ3ODNlZTY2ZTUyYjdkOTY1NTYwZWUzNDdkYThmMDkyNjc4NDQ5YWNYIQItYHXSMJ4LFFO9K1aU3Uc78aeCsOsI6jAXyW-DWyhh_WFko2FlWCBRVIHPN9Zrh0hZhXhU82cdPuiJY0K8NoXsr4mY3dYb12FzWCCvTPfDoyUBn-4YymH5Hx5rKu8bN_CHxnub--KiSMVJ-mFyWCAw_0W5mYr9tWLlKRwALm9uawkPDteOKoZXwK82srwaTqRhYRggYXN4QDY4ZmI2MzZmZDVhOGYwYjk0MTQ2Zjg4NGI5MjY4OTFmOTgwOTdmZmUyNWMxOTZiOTZhNDIwMWVmYmY2YjFlM2VhY1ghA2MajW5MWD7UsgIoi0WD0jLtDXL_nHniRC2UWRja2dvZYWSjYWVYIJfASNWmMVlZYJUOcQiK-GywJAKJCI7f6vtAOVCEGWR2YXNYIGC-HgpOagMEg-FgA6F8nqK_YyYPuLAFIGCrAGph5UFRYXJYICiCI6WLgT-n1hkq_weAtj7IFETzJi7F1B0opFwUcXtkpGFhBGFzeEA4NzkyNzVjNDE5MzY0NjdiMzU2ZGIwZDdmMjYzMmVjYmE5YTYxODRhZmZmODI5ODE2MmY5MGU2YTA1NzVkZGUzYWNYIQKNu2a42OcmBXb_aavucyKNkjNmjEMCzBIu5cMJ3QtzvGFko2FlWCBXmMRmJlpZsMpRjtQBW0JfqNq8pdu5chq4uqGEH7GZNmFzWCABSr1ZdUab2HKtuOU1Cx3_uVplHYii2pffs1XpWMp5Q2FyWCAzvsQlJylm5ZBkTQ6HncBuLbpFdng23IOqnCDdDzGNmQ";
            var deserialized = CashuTokenHelper.Decode(token, out _);
            //TestNut returns mainnet invoice
            var network = Network.Main;

            var result = await CashuUtils.GetTokenSatRate(deserialized, network);
            // Assert one usd token to be 12 sat (18.04.2025)
            Assert.True(result <= 11 || result >= 13);
        }


        #endregion

        #region SimplifyToken Tests

        [Fact]
        public void SimplifyToken_ValidToken_ReturnsSimplifiedToken()
        {
            var encodedToken =
                "cashuBo2FteBtodHRwczovL3Rlc3RudXQuY2FzaHUuc3BhY2VhdWN1c2RhdIGiYWlIAMB0uWx-Kw5hcIOkYWEYQGFzeEBhMWRkYjRjMzgyMjM3MjU0NTYwMmViY2IzYzQ3ODNlZTY2ZTUyYjdkOTY1NTYwZWUzNDdkYThmMDkyNjc4NDQ5YWNYIQItYHXSMJ4LFFO9K1aU3Uc78aeCsOsI6jAXyW-DWyhh_WFko2FlWCBRVIHPN9Zrh0hZhXhU82cdPuiJY0K8NoXsr4mY3dYb12FzWCCvTPfDoyUBn-4YymH5Hx5rKu8bN_CHxnub--KiSMVJ-mFyWCAw_0W5mYr9tWLlKRwALm9uawkPDteOKoZXwK82srwaTqRhYRggYXN4QDY4ZmI2MzZmZDVhOGYwYjk0MTQ2Zjg4NGI5MjY4OTFmOTgwOTdmZmUyNWMxOTZiOTZhNDIwMWVmYmY2YjFlM2VhY1ghA2MajW5MWD7UsgIoi0WD0jLtDXL_nHniRC2UWRja2dvZYWSjYWVYIJfASNWmMVlZYJUOcQiK-GywJAKJCI7f6vtAOVCEGWR2YXNYIGC-HgpOagMEg-FgA6F8nqK_YyYPuLAFIGCrAGph5UFRYXJYICiCI6WLgT-n1hkq_weAtj7IFETzJi7F1B0opFwUcXtkpGFhBGFzeEA4NzkyNzVjNDE5MzY0NjdiMzU2ZGIwZDdmMjYzMmVjYmE5YTYxODRhZmZmODI5ODE2MmY5MGU2YTA1NzVkZGUzYWNYIQKNu2a42OcmBXb_aavucyKNkjNmjEMCzBIu5cMJ3QtzvGFko2FlWCBXmMRmJlpZsMpRjtQBW0JfqNq8pdu5chq4uqGEH7GZNmFzWCABSr1ZdUab2HKtuOU1Cx3_uVplHYii2pffs1XpWMp5Q2FyWCAzvsQlJylm5ZBkTQ6HncBuLbpFdng23IOqnCDdDzGNmQ";
            var token = CashuTokenHelper.Decode(encodedToken, out _);

            var simplifiedToken = CashuUtils.SimplifyToken(token);

            Assert.NotNull(simplifiedToken);
            Assert.Equal("https://testnut.cashu.space", simplifiedToken.Mint);
            Assert.Equal(3, simplifiedToken.Proofs.Count);
            Assert.Equal((ulong)64, simplifiedToken.Proofs[0].Amount);
            Assert.Equal((ulong)32, simplifiedToken.Proofs[1].Amount);
            Assert.Equal((ulong)4, simplifiedToken.Proofs[2].Amount);
            Assert.Equal((ulong)100, simplifiedToken.SumProofs);
            Assert.Null(simplifiedToken.Memo);
            Assert.Equal("usd", simplifiedToken.Unit);
        }

        [Fact]
        public void SimplifyToken_TokenWithMultipleMints_ThrowsCashuPaymentException()
        {
            var token = new CashuToken
            {
                Tokens = new List<CashuToken.Token>
                {
                    new CashuToken.Token
                    {
                        Mint = "https://examplemint1.com",
                        Proofs = new List<Proof> { new Proof { Amount = 10, Secret = new StringSecret("secret1") } }
                    },
                    new CashuToken.Token
                    {
                        Mint = "https://examplemint2.com",
                        Proofs = new List<Proof> { new Proof { Amount = 20, Secret = new StringSecret("secret2") } }
                    }
                }
            };

            Assert.Throws<CashuPaymentException>(() => CashuUtils.SimplifyToken(token));
        }

        [Fact]
        public void SimplifyToken_TokenWithDefaultUnitValue_UsesDefaultSat()
        {
            var token = new CashuToken
            {
                Tokens = new List<CashuToken.Token>
                {
                    new CashuToken.Token
                    {
                        Mint = "https://examplemint.com",
                        Proofs = new List<Proof> { new Proof { Amount = 10, Secret = new StringSecret("secret1") } }
                    }
                },
                Unit = null
            };

            var simplifiedToken = CashuUtils.SimplifyToken(token);

            Assert.Equal("sat", simplifiedToken.Unit);
        }


        #endregion
        
        #region SplitAmountsForPayment Tests

        [Fact]
        public void SplitAmountsForPayment_ExactAmount_ReturnsCorrectSplit()
        {
            var inputAmounts = new List<ulong> { 1, 2, 4, 16, 32, 64 };
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));
            ulong requestedAmount = 30;

            var (keep, send) = CashuUtils.SplitAmountsForPayment(inputAmounts, keyset, requestedAmount);

            Assert.Equal(requestedAmount, send.Aggregate(0UL, (a, c) => a + c));
            Assert.Equal(inputAmounts.Aggregate(0UL, (a, c) => a + c) - send.Aggregate(0UL, (a, c) => a + c),
                keep.Aggregate(0UL, (a, c) => a + c));
        }

        [Fact]
        public void SplitAmountsForPayment_ExactAmount_ReturnsCorrectSplit2()
        {
            var inputAmounts = new List<ulong> { 1, 2, 4, 16, 32, 64 };
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));
            ulong requestedAmount = 20;

            var (keep, send) = CashuUtils.SplitAmountsForPayment(inputAmounts, keyset, requestedAmount);

            Assert.Equal(4, keep.Count);
            Assert.Contains((ulong)1, keep);
            Assert.Contains((ulong)2, keep);
            Assert.Contains((ulong)32, keep);
            Assert.Contains((ulong)64, keep);
            Assert.Equal(2, send.Count);
            Assert.Contains((ulong)16, send);
            Assert.Contains((ulong)4, send);
        }

        [Fact]
        public void SplitAmountsForPayment_NoChange_ReturnsEmptyKeepList()
        {
            var inputAmounts = new List<ulong> { 64 };
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));

            ulong requestedAmount = 64;

            var (keep, send) = CashuUtils.SplitAmountsForPayment(inputAmounts, keyset, requestedAmount);

            Assert.Empty(keep);
            Assert.Single(send);
            Assert.Equal(64UL, send[0]);
        }

        [Fact]
        public void SplitAmountsForPayment_InvalidInputs_ThrowsInvalidOperationException()
        {
            var inputAmounts = new List<ulong> { 10, 20 };
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));

            ulong requestedAmount = 50;

            Assert.Throws<InvalidOperationException>(() =>
                CashuUtils.SplitAmountsForPayment(inputAmounts, keyset, requestedAmount));
        }

        [Fact]
        public void SplitAmountsForPayment_AmountGreaterThanInput_ThrowsInvalidOperationException()
        {
            var inputAmounts = new List<ulong> { 2, 4 };
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));

            ulong requestedAmount = 8;

            Assert.Throws<InvalidOperationException>(() =>
                CashuUtils.SplitAmountsForPayment(inputAmounts, keyset, requestedAmount));
        }
        #endregion

        #region CreatePaymentRequest Tests

        [Fact]
        public void CreatePaymentRequest_ValidInputs_ReturnsCorrectRequest()
        {
            // Arrange
            var amount = Money.Satoshis(100);
            string invoiceId = "invoice123";
            string endpoint = "https://example.com/payment";
            string[] trustedMintsUrls = ["https://mint1.com", "https://mint2.com"];

            // Act
            var result = CashuUtils.CreatePaymentRequest(amount, invoiceId, endpoint, trustedMintsUrls);
            var resultParsed = DotNut.PaymentRequest.Parse(result);
            // Assert
            Assert.NotNull(result);
            Assert.Equal(resultParsed.Amount, (ulong)amount);
            Assert.Contains(resultParsed.Unit, "sat");
            Assert.Equal(resultParsed.PaymentId, invoiceId);
            Assert.Single(resultParsed.Transports);
            Assert.Equal("post", resultParsed.Transports.First().Type);
            Assert.Equal(resultParsed.Transports.First().Target, endpoint);
            Assert.NotEmpty(resultParsed.Mints);
            Assert.NotNull(resultParsed.Mints);
            Assert.Contains(resultParsed.Mints, m => m.ToString() == trustedMintsUrls[0]);
            Assert.Contains(resultParsed.Mints, m => m.ToString() == trustedMintsUrls[1]);

        }

        [Fact]
        public void CreatePaymentRequest_NullMints_UsesEmptyArray()
        {
            // Arrange
            var amount = Money.Satoshis(100);
            string invoiceId = "invoice123";
            string endpoint = "https://example.com/payment";

            // Act
            var result = CashuUtils.CreatePaymentRequest(amount, invoiceId, endpoint, null);
            var resultParsed = PaymentRequest.Parse(result);
            // Assert
            Assert.NotNull(result);
            Assert.NotNull(resultParsed.Mints);
            Assert.Empty(resultParsed.Mints);
        }

        [Fact]
        public void CreatePaymentRequest_NullOrEmptyEndpoint_ThrowsArgumentNullException()
        {
            // Arrange
            var amount = Money.Satoshis(100);
            string invoiceId = "invoice123";
            string endpoint = "";

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                CashuUtils.CreatePaymentRequest(amount, invoiceId, null, null));
            Assert.Throws<ArgumentNullException>(() =>
                CashuUtils.CreatePaymentRequest(amount, invoiceId, endpoint, null));
        }

        [Fact]
        public void CreatePaymentRequest_NullOrEmptyInvoiceId_ThrowsArgumentNullException()
        {
            // Arrange
            var amount = Money.Satoshis(100);
            string invoiceId = "";
            string endpoint = "endpoint123";

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                CashuUtils.CreatePaymentRequest(amount, null, endpoint, null));
            Assert.Throws<ArgumentNullException>(() =>
                CashuUtils.CreatePaymentRequest(amount, invoiceId, endpoint, null));
        }

        [Fact]
        public void CreatePaymentRequest_InvalidAmount_ThrowsArgumentException()
        {
            // Arrange
            string invoiceId = "invoice123";
            string endpoint = "endpoint123";
            
            Assert.Throws<ArgumentException>(() =>
                CashuUtils.CreatePaymentRequest(Money.Satoshis(-1), invoiceId, endpoint, null));
        }

        [Fact]
        public void CreatePaymentRequest_Amountless_Success()
        {
            // Arrange
            string invoiceId = "invoice123";
            string endpoint = "endpoint123";
            var pr =CashuUtils.CreatePaymentRequest(Money.Zero, invoiceId, endpoint, null);
            Assert.NotNull(pr);
            var deserializedPr = PaymentRequest.Parse(pr);
            Assert.Null(deserializedPr.Amount);
        }

        #endregion

        #region TryDecodeToken Tests

        [Fact]
        public void TryDecodeToken_ValidToken_ReturnsTrue()
        {
            // Arrange
            string validToken =
                "cashuBo2F0gaJhaUgAFo194X2Lm2Fwg6NhYQhhc3hANDE1ZDAxNWEzY2UwZTcxYTgyODMxMDgzNDRlMmJmMTNmNzZjNTM3MjdiNWIyNGI1ODViMTQ2Y2NlMmM3ZDVmOGFjWCEDgjrv5E_oIeBxJTJuLAkenzhRCTvYtIw5ymN6ah8egDGjYWEBYXN4QDk4OTFjYTg2OWY5ZjA2YjY2Zjk0YzRmYjE4OWM0YmQ0NjcyM2QzYzFhYzFjNTM3OWEzM2Y5NmI1MTliMDJhNmRhY1ghAv5YHuOCclMCO_VJ7FfuuMm48hIIAOR0WCIQ4pwFe6zQo2FhAWFzeEA4MDA0ZmIyMDM1MzQzYjJiOTZkYWNlNmNlMTcxNGQ3NGRiMGM2ZmNjYzQ4ZGJiYmQ3NDE3MzBiMWYwN2NiZWU3YWNYIQNpFWP0sn7lKRWi7hADXfj6PxmQb1ZB2k_9rszuhdikVmFtdWh0dHA6Ly8xMjcuMC4wLjE6MzMzOGF1Y3NhdA";

            // Act
            bool result = CashuUtils.TryDecodeToken(validToken, out var cashuToken);

            // Assert
            Assert.True(result);
            Assert.NotNull(cashuToken);
        }

        [Fact]
        public void TryDecodeToken_InvalidToken_ReturnsFalse()
        {
            // Arrange
            string invalidToken = "invalid-token";

            // Act
            bool result = CashuUtils.TryDecodeToken(invalidToken, out var cashuToken);

            // Assert
            Assert.False(result);
            Assert.Null(cashuToken);
        }

        [Fact]
        public void TryDecodeToken_NullOrEmptyToken_ReturnsFalse()
        {
            // Arrange
            string nullToken = null;
            string emptyToken = "";

            // Act
            bool result1 = CashuUtils.TryDecodeToken(nullToken, out var cashuToken1);
            bool result2 = CashuUtils.TryDecodeToken(emptyToken, out var cashuToken2);

            // Assert
            Assert.False(result1);
            Assert.Null(cashuToken1);
            Assert.False(result2);
            Assert.Null(cashuToken2);
        }

        #endregion
        
        #region FormatAmount Tests
                
        [Theory]
        [InlineData(100000000, "BTC", 1)]
        [InlineData(100, "BTC", 0.00000100)]
        [InlineData(1, "SAT", 1)]
        [InlineData(1000, "MSAT", 1)]
        [InlineData(123456, "msat", 123.456)]
        [InlineData(0, "SAT", 0)]
        [InlineData(-100000000, "BTC", -1)]
        public void FormatAmount_BitcoinUnits_Should_Format_Correctly(decimal input, string unit, decimal expected)
        {
            var (amount, returnedUnit) = CashuUtils.FormatAmount(input, unit);
            Assert.Equal(expected, Math.Round(amount, 12));
            Assert.Equal(unit.ToUpperInvariant(), returnedUnit);
        }

        [Theory]
        [InlineData(1000, "BHD", 1)]
        [InlineData(1000, "IQD", 1)]
        [InlineData(1000, "JPY", 1000)]
        [InlineData(123456, "CLF", 12.3456)]
        [InlineData(123456, "UYW", 12.3456)]
        [InlineData(123456, "XOF", 123456)]
        [InlineData(0, "TND", 0)]
        [InlineData(-123456, "BHD", -123.456)]
        public void FormatAmount_SpecialFiatUnits_Should_Format_Correctly(decimal input, string unit, decimal expected)
        {
            var (amount, returnedUnit) = CashuUtils.FormatAmount(input, unit);
            Assert.Equal(expected, Math.Round(amount, 6));
            Assert.Equal(unit.ToUpperInvariant(), returnedUnit);
        }

        [Theory]
        [InlineData(123456, "USD", 1234.56)]
        [InlineData(123456, "eur", 1234.56)]
        [InlineData(100, "", 1)]
        [InlineData(100, null, 1)]
        public void FormatAmount_UnknownOrEmptyUnit_Should_DefaultProperly(decimal input, string unit, decimal expected)
        {
            var (amount, returnedUnit) = CashuUtils.FormatAmount(input, unit);
            if (string.IsNullOrEmpty(unit))
            {
                Assert.Equal(100, amount);
                Assert.Equal("SAT", returnedUnit);
            }
            else
            {
                Assert.Equal(expected, Math.Round(amount, 2));
                Assert.Equal(unit.ToUpperInvariant(), returnedUnit);
            }
        }

        [Fact]
        public void FormatAmount_Should_Handle_Zero_Amount_Correctly()
        {
            var (amount, unit) = CashuUtils.FormatAmount(0, "USD");
            Assert.Equal(0, amount);
            Assert.Equal("USD", unit);
        }

        [Fact]
        public void FormatAmount_Should_Handle_MaxAndMinValues()
        {
            var (maxAmount, _) = CashuUtils.FormatAmount(decimal.MaxValue, "SAT");
            Assert.Equal(decimal.MaxValue, maxAmount);

            var (minAmount, _) = CashuUtils.FormatAmount(decimal.MinValue, "SAT");
            Assert.Equal(decimal.MinValue, minAmount);
        }
        #endregion
    }
}
