using System.Security.Cryptography;
using System.Text.Json;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Errors;
using DotNut;
using NBitcoin;
using NBitcoin.Secp256k1;
using Xunit;
using DLEQProof = DotNut.DLEQProof;

namespace BTCPayserver.Plugins.Cashu.Tests
{
    public class CashuUtilsTests
    {

        public static string keysetPath =
            "/Users/d4rp4t/RiderProjects/BTCNutServer/BTCPayserver.Plugins.Cashu.Tests/keys.json";

        public Keyset testKeyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));

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

        #region SelectProofsToSend Tests

        [Fact]
        public void SelectProofsToSend_ExactAmountAvailable_SelectsExactProof()
        {
            var proofs = new List<Proof>
            {
                new Proof { Amount = 10, Secret = new StringSecret("secret1") },
                new Proof { Amount = 20, Secret = new StringSecret("secret2") },
                new Proof { Amount = 30, Secret = new StringSecret("secret3") }
            };
            ulong amountToSend = 20;

            var result = CashuUtils.SelectProofsToSend(proofs, amountToSend);

            Assert.Single(result.Send);
            Assert.Equal((ulong)20, result.Send[0].Amount);
            Assert.Equal(2, result.Keep.Count);
            Assert.Contains(result.Keep, p => p.Amount == 10);
            Assert.Contains(result.Keep, p => p.Amount == 30);
        }

        [Fact]
        public void SelectProofsToSend_CombinationNeeded_SelectsMultipleProofs()
        {
            var proofs = new List<Proof>
            {
                new Proof { Amount = 10, Secret = new StringSecret("secret1") },
                new Proof { Amount = 15, Secret = new StringSecret("secret2") },
                new Proof { Amount = 30, Secret = new StringSecret("secret3") }
            };
            ulong amountToSend = 25;

            var result = CashuUtils.SelectProofsToSend(proofs, amountToSend);

            Assert.Equal(2, result.Send.Count);
            Assert.Equal((ulong)25, result.Send.Select(p => p.Amount).Sum());
            Assert.Single(result.Keep);
            Assert.Equal((ulong)30, result.Keep[0].Amount);
        }

        [Fact]
        public void SelectProofsToSend_OnlyBiggerProofAvailable_SelectsBiggerProof()
        {
            var proofs = new List<Proof>
            {
                new Proof { Amount = 30, Secret = new StringSecret("secret1") },
                new Proof { Amount = 40, Secret = new StringSecret("secret2") },
                new Proof { Amount = 50, Secret = new StringSecret("secret3") }
            };
            ulong amountToSend = 25;

            var result = CashuUtils.SelectProofsToSend(proofs, amountToSend);

            Assert.Single(result.Send);
            Assert.Equal((ulong)30, result.Send[0].Amount);
            Assert.Equal(2, result.Keep.Count);
        }

        [Fact]
        public void SelectProofsToSend_NoProofsAvailable_ReturnsEmptySendList()
        {
            var proofs = new List<Proof>();
            ulong amountToSend = 25;

            var result = CashuUtils.SelectProofsToSend(proofs, amountToSend);

            Assert.Empty(result.Send);
            Assert.Empty(result.Keep);
        }

        [Fact]
        public void SelectProofsToSend_AmountGreaterThanAllProofs_ReturnsAllProofsAsKeep()
        {
            var proofs = new List<Proof>
            {
                new Proof { Amount = 10, Secret = new StringSecret("secret1") },
                new Proof { Amount = 20, Secret = new StringSecret("secret2") }
            };
            ulong amountToSend = 100;

            var result = CashuUtils.SelectProofsToSend(proofs, amountToSend);

            Assert.Empty(result.Send);
            Assert.Equal(2, result.Keep.Count);
        }

        [Fact]
        public void SelectProofsToSend_InsufficientAmount_ReturnsEmptySend()
        {
            var proofs = new List<Proof> { new() { Amount = 5 }, new() { Amount = 5 } };

            var result = CashuUtils.SelectProofsToSend(proofs, 15);

            Assert.Empty(result.Send);
            Assert.Equal(2, result.Keep.Count);
        }

        #endregion

        #region SplitToProofsAmounts Tests

        [Fact]
        public void SplitToProofsAmounts_ExactKeysAvailable_SplitsCorrectly()
        {
            ulong paymentAmount = 16;


            var result = CashuUtils.SplitToProofsAmounts(paymentAmount, testKeyset);

            Assert.Single(result);
            Assert.Contains((ulong)16, result);
        }

        [Fact]
        public void SplitToProofsAmounts_CombinationNeeded_SplitsCorrectly()
        {
            ulong paymentAmount = 2561;
            var result = CashuUtils.SplitToProofsAmounts(paymentAmount, testKeyset);
            Assert.Equal(3, result.Count);
            Assert.Contains((ulong)2048, result);
            Assert.Contains((ulong)512, result);
            Assert.Contains((ulong)1, result);
            Assert.Equal(2561UL, result.Aggregate((a, c) => a + c));
        }

        [Fact]
        public void SplitToProofsAmounts_LargeAmount_SplitsIntoLargestPossibleValues()
        {
            ulong paymentAmount = 100;

            var result = CashuUtils.SplitToProofsAmounts(paymentAmount, testKeyset);

            Assert.Equal(3, result.Count);
            Assert.Equal(1, result.Count(a => a == 64));
            Assert.Equal(1, result.Count(a => a == 32));
            Assert.Equal(1, result.Count(a => a == 4));
            Assert.Equal(100UL, result.Aggregate(0UL, (a, c) => a + c));
        }

        [Fact]
        public void SplitToProofsAmounts_ZeroAmount_ReturnsEmptyList()
        {
            ulong paymentAmount = 0;
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));

            var result = CashuUtils.SplitToProofsAmounts(paymentAmount, keyset);

            Assert.Empty(result);
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

        #region CreateBlankOutputs Tests

        [Fact]
        public void CreateBlankOutputs_PositiveAmount_CreatesCorrectNumberOfOutputs()
        {
            ulong amount = 10;
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));
            //derive keysetId from loaded keys 
            var keysetId = keyset?.GetKeysetId();

            var result = CashuUtils.CreateBlankOutputs(amount, keysetId, keyset);

            Assert.Equal(4, result.BlindedMessages.Length);
            Assert.Equal(4, result.Secrets.Length);
            Assert.Equal(4, result.BlindingFactors.Length);

            foreach (var blindedMessage in result.BlindedMessages)
            {
                Assert.Equal((ulong)1, blindedMessage.Amount); //Blank output has amount = 1
                Assert.Equal(keysetId, blindedMessage.Id);
            }
        }

        [Fact]
        public void CreateBlankOutputs_ZeroAmount_ThrowsArgumentException()
        {
            ulong amount = 0;
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));
            var keysetId = keyset?.GetKeysetId();

            Assert.Throws<ArgumentException>(() => CashuUtils.CreateBlankOutputs(amount, keysetId, keyset));
        }

        [Fact]
        public void CreateBlankOutputs_NegativeAmount_ThrowsArgumentException()
        {
            ulong amount = 0;
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));
            var keysetId = keyset?.GetKeysetId();

            Assert.Throws<ArgumentException>(() => CashuUtils.CreateBlankOutputs(amount, keysetId, keyset));
        }

        [Fact]
        public void CreateBlankOutputs_LargeAmount_GeneratesCorrectCount()
        {
            var keysetId = testKeyset.GetKeysetId();
            var result = CashuUtils.CreateBlankOutputs(1024, keysetId, testKeyset);

            Assert.Equal(10, result.BlindedMessages.Length); // log2(1024) = 10
        }

        #endregion

        #region CreateOutputs Tests

        [Fact]
        public void CreateOutputs_ValidInputs_CreatesCorrectOutputs()
        {
            // Arrange
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));
            var keysetId = keyset?.GetKeysetId();

            int outputAmount = 3;
            var amounts = new List<ulong> { 2, 4, 8 };

            // Act
            var result = CashuUtils.CreateOutputs(amounts, keysetId, keyset);

            // Assert
            Assert.Equal(outputAmount, result.BlindedMessages.Length);
            Assert.Equal(outputAmount, result.Secrets.Length);
            Assert.Equal(outputAmount, result.BlindingFactors.Length);

            Assert.Equal((ulong)2, result.BlindedMessages[0].Amount);
            Assert.Equal((ulong)4, result.BlindedMessages[1].Amount);
            Assert.Equal((ulong)8, result.BlindedMessages[2].Amount);

            foreach (var blindedMessage in result.BlindedMessages)
            {
                Assert.Equal(keysetId, blindedMessage.Id);
            }
        }

        [Fact]
        public void CreateOutputs_InvalidAmounts_ThrowsArgumentException()
        {
            // Arrange
            var keyset = JsonSerializer.Deserialize<Keyset>(File.ReadAllText(keysetPath));
            var keysetId = keyset?.GetKeysetId();

            var amounts = new List<ulong> { 10, 20 }; // Tylko 2 wartości zamiast 3

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                CashuUtils.CreateOutputs(amounts, keysetId, keyset));
        }



        #endregion

        #region StripDleq Tests

        [Fact]
        public void StripDleq_ProofsWithDleq_RemovesDleq()
        {
            // Arrange
            var proofs = new List<Proof>
            {
                new Proof { Amount = 10, DLEQ = new DLEQProof() },
                new Proof { Amount = 20, DLEQ = new DLEQProof() }
            };

            // Act
            var result = CashuUtils.StripDleq(proofs);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.All(result, proof => Assert.Null(proof.DLEQ));
        }

        [Fact]
        public void StripDleq_ProofsWithoutDleq_DoesNothing()
        {
            // Arrange
            var proofs = new List<Proof>
            {
                new Proof { Amount = 10, DLEQ = null },
                new Proof { Amount = 20, DLEQ = null }
            };

            // Act
            var result = CashuUtils.StripDleq(proofs);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.All(result, proof => Assert.Null(proof.DLEQ));
        }

        [Fact]
        public void StripDleq_EmptyProofsList_ReturnsEmptyList()
        {
            // Arrange
            var proofs = new List<Proof>();

            // Act
            var result = CashuUtils.StripDleq(proofs);

            // Assert
            Assert.Empty(result);
        }

        #endregion

        #region CreatePaymentRequest Tests

        [Fact]
        public void CreatePaymentRequest_ValidInputs_ReturnsCorrectRequest()
        {
            // Arrange
            int amount = 100;
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
            int amount = 100;
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
            int amount = 100;
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
            int amount = 100;
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
                CashuUtils.CreatePaymentRequest(-1, invoiceId, endpoint, null));
        }

        [Fact]
        public void CreatePaymentRequest_Amountless_Success()
        {
            // Arrange
            string invoiceId = "invoice123";
            string endpoint = "endpoint123";
            var pr =CashuUtils.CreatePaymentRequest(0, invoiceId, endpoint, null);
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

        #region CalculateNumberOfBlankOutputs Tests

        [Fact]
        public void CalculateNumberOfBlankOutputs_PositiveAmount_ReturnsCorrectNumber()
        {
            // Arrange 
            ulong amount = 10;

            // Act
            var methodInfo = typeof(CashuUtils).GetMethod("CalculateNumberOfBlankOutputs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = (int)methodInfo.Invoke(null, new object[] { amount });

            // Assert
            Assert.Equal(4, result); // Math.Ceiling(log2(10)) = 4
        }

        [Fact]
        public void CalculateNumberOfBlankOutputs_PowerOfTwo_ReturnsExactLog()
        {
            // Arrange
            ulong amount = 16;

            // Act
            var methodInfo = typeof(CashuUtils).GetMethod("CalculateNumberOfBlankOutputs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = (int)methodInfo.Invoke(null, new object[] { amount });

            // Assert
            Assert.Equal(4, result); // log2(16) = 4
        }

        [Fact]
        public void CalculateNumberOfBlankOutputs_ZeroAmount_ReturnsZero()
        {
            // Arrange
            ulong amount = 0;

            // Act
            var methodInfo = typeof(CashuUtils).GetMethod("CalculateNumberOfBlankOutputs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = methodInfo.Invoke(null, new object[] { amount });

            // Assert
            Assert.Equal(0, result);
        }


        #endregion

        #region CreateProofs Tests

        [Fact]
        public void CreateProofs_ValidInputs_CreatesCorrectProofsWithDLEQ()
        {
            var a = new PrivKey(new string('0', 63) + "1");
            var A = a.Key.CreatePubKey();
            //create fake secrets
            var secrets = new List<DotNut.ISecret>
            {
                new StringSecret("fakeSecret"),
                new StringSecret("fakeSecret2")
            };
            //blinding factors
            var blindingFactors = new List<PrivKey>
            {
                new PrivKey(new string('0', 58) + 123456),
                new PrivKey(new string('0', 58) + 789012)
            };
            var blindedMessages = new List<ECPubKey>()
            {
                DotNut.Cashu.ComputeB_(secrets[0].ToCurve(), blindingFactors[0]),
                DotNut.Cashu.ComputeB_(secrets[1].ToCurve(), blindingFactors[1]),
            };

            var randomDleqNonce1 = new PrivKey(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            var randomDleqNonce2 = new PrivKey(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

            var dleq1 = DotNut.Cashu.ComputeProof(blindedMessages[0], a, randomDleqNonce1);
            var dleq2 = DotNut.Cashu.ComputeProof(blindedMessages[1], a, randomDleqNonce2);


            var fakeKeyset = new Keyset
            {
                [1] = A
            };
            var fakeKeysetId = fakeKeyset.GetKeysetId();

            var fakeBlindSignature1 = new BlindSignature()
            {
                Id = fakeKeysetId,
                Amount = 1,
                C_ = DotNut.Cashu.ComputeC_(blindedMessages[0], a),
                DLEQ = new DLEQProof()
                {
                    E = dleq1.e,
                    S = dleq1.s,
                }
            };
            var fakeBlindSignature2 = new BlindSignature()
            {
                Id = fakeKeysetId,
                Amount = 1,
                C_ = DotNut.Cashu.ComputeC_(blindedMessages[1], a),
                DLEQ = new DLEQProof()
                {
                    E = dleq2.e,
                    S = dleq2.s,
                }
            };

            var proofs = CashuUtils.CreateProofs(
                [fakeBlindSignature1, fakeBlindSignature2],
                blindingFactors.ToArray(),
                secrets.ToArray(),
                fakeKeyset
            );

            Assert.Equal(2, proofs.Length);

            Assert.Equal((ulong)1, proofs[0].Amount);
            Assert.Equal((ulong)1, proofs[1].Amount);

            Assert.Equal(secrets[0], proofs[0].Secret);
            Assert.Equal(secrets[1], proofs[1].Secret);

            Assert.Equal(fakeKeysetId, proofs[0].Id);
            Assert.Equal(fakeKeysetId, proofs[1].Id);

            Assert.Equal(proofs[0].C.ToString(),
                DotNut.Cashu.ComputeC(fakeBlindSignature1.C_, blindingFactors[0], A).ToHex());
            Assert.Equal(proofs[1].C.ToString(),
                DotNut.Cashu.ComputeC(fakeBlindSignature2.C_, blindingFactors[1], A).ToHex());

            Assert.Equal(proofs[0].DLEQ.S, fakeBlindSignature1.DLEQ.S);
            Assert.Equal(proofs[0].DLEQ.E, fakeBlindSignature1.DLEQ.E);

            Assert.Equal(proofs[0].DLEQ.R.ToString(), blindingFactors[0].ToString());
            Assert.Equal(proofs[1].DLEQ.S, fakeBlindSignature2.DLEQ.S);
            Assert.Equal(proofs[1].DLEQ.E, fakeBlindSignature2.DLEQ.E);
            Assert.Equal(proofs[1].DLEQ.R.ToString(), blindingFactors[1].ToString());

            Assert.True(proofs[0].Verify(A));
            Assert.True(proofs[1].Verify(A));
        }

        [Fact]
        public void CreateProofs_ValidInputs_CreatesCorrectProofsWithoutDLEQ()
        {
            var a = new PrivKey(new string('0', 63) + "1");
            var A = a.Key.CreatePubKey();
            //create fake secrets
            var secrets = new List<DotNut.ISecret>
            {
                new StringSecret("fakeSecret"),
                new StringSecret("fakeSecret2")
            };
            //blinding factors
            var blindingFactors = new List<PrivKey>
            {
                new PrivKey(new string('0', 58) + 123456),
                new PrivKey(new string('0', 58) + 789012)
            };
            var blindedMessages = new List<ECPubKey>()
            {
                DotNut.Cashu.ComputeB_(secrets[0].ToCurve(), blindingFactors[0]),
                DotNut.Cashu.ComputeB_(secrets[1].ToCurve(), blindingFactors[1]),
            };

            var fakeKeyset = new Keyset
            {
                [1] = A
            };
            var fakeKeysetId = fakeKeyset.GetKeysetId();

            var fakeBlindSignature1 = new BlindSignature()
            {
                Id = fakeKeysetId,
                Amount = 1,
                C_ = DotNut.Cashu.ComputeC_(blindedMessages[0], a),
                DLEQ = null
            };
            var fakeBlindSignature2 = new BlindSignature()
            {
                Id = fakeKeysetId,
                Amount = 1,
                C_ = DotNut.Cashu.ComputeC_(blindedMessages[1], a),
                DLEQ = null
            };

            var proofs = CashuUtils.CreateProofs(
                [fakeBlindSignature1, fakeBlindSignature2],
                blindingFactors.ToArray(),
                secrets.ToArray(),
                fakeKeyset
            );

            Assert.Equal(2, proofs.Length);

            Assert.Equal((ulong)1, proofs[0].Amount);
            Assert.Equal((ulong)1, proofs[1].Amount);

            Assert.Equal(secrets[0], proofs[0].Secret);
            Assert.Equal(secrets[1], proofs[1].Secret);

            Assert.Equal(fakeKeysetId, proofs[0].Id);
            Assert.Equal(fakeKeysetId, proofs[1].Id);

            Assert.Equal(proofs[0].C.ToString(),
                DotNut.Cashu.ComputeC(fakeBlindSignature1.C_, blindingFactors[0], A).ToHex());
            Assert.Equal(proofs[1].C.ToString(),
                DotNut.Cashu.ComputeC(fakeBlindSignature2.C_, blindingFactors[1], A).ToHex());

            Assert.Null(fakeBlindSignature1.DLEQ);
            Assert.Null(fakeBlindSignature2.DLEQ);
            Assert.Null(proofs[0].DLEQ);
            Assert.Null(proofs[1].DLEQ);

            Assert.Throws<NullReferenceException>(() => proofs[0].Verify(A));
            Assert.Throws<NullReferenceException>(() => proofs[1].Verify(A));
        }

        [Fact]
        public void CreateProofs_ThrowsWhenKeysetIdMismatch()
        {
            var a = new PrivKey(new string('0', 63) + "1");
            var A = a.Key.CreatePubKey();
            //create fake secrets
            var secrets = new List<DotNut.ISecret>
            {
                new StringSecret("fakeSecret"),
                new StringSecret("fakeSecret2")
            };
            //blinding factors
            var blindingFactors = new List<PrivKey>
            {
                new PrivKey(new string('0', 58) + 123456),
                new PrivKey(new string('0', 58) + 789012)
            };
            var blindedMessages = new List<ECPubKey>()
            {
                DotNut.Cashu.ComputeB_(secrets[0].ToCurve(), blindingFactors[0]),
                DotNut.Cashu.ComputeB_(secrets[1].ToCurve(), blindingFactors[1]),
            };

            var fakeKeyset = new Keyset
            {
                [1] = A
            };
            var fakeKeysetId = fakeKeyset.GetKeysetId();

            var fakeBlindSignature1 = new BlindSignature()
            {
                Id = fakeKeysetId,
                Amount = 1,
                C_ = DotNut.Cashu.ComputeC_(blindedMessages[0], a),
                DLEQ = null
            };
            var fakeBlindSignature2 = new BlindSignature()
            {
                Id = fakeKeysetId,
                Amount = 1,
                C_ = DotNut.Cashu.ComputeC_(blindedMessages[1], a),
                DLEQ = null
            };

            Assert.Throws<CashuPluginException>(() =>
            {
                var proofs = CashuUtils.CreateProofs(
                    [fakeBlindSignature1, fakeBlindSignature2],
                    blindingFactors.ToArray(),
                    secrets.ToArray(),
                    //use fake keyset
                    testKeyset
                );
            });

        }

        [Fact]
        public void CreateProofs_ThrowsWhenMultipleKeysetIds()
        {
            var a = new PrivKey(new string('0', 63) + "1");
            var A = a.Key.CreatePubKey();
            //create fake secrets
            var secrets = new List<DotNut.ISecret>
            {
                new StringSecret("fakeSecret"),
                new StringSecret("fakeSecret2")
            };
            //blinding factors
            var blindingFactors = new List<PrivKey>
            {
                new PrivKey(new string('0', 58) + 123456),
                new PrivKey(new string('0', 58) + 789012)
            };
            var blindedMessages = new List<ECPubKey>()
            {
                DotNut.Cashu.ComputeB_(secrets[0].ToCurve(), blindingFactors[0]),
                DotNut.Cashu.ComputeB_(secrets[1].ToCurve(), blindingFactors[1]),
            };

            var fakeKeyset = new Keyset
            {
                [1] = A
            };
            var fakeKeysetId = fakeKeyset.GetKeysetId();

            var fakeBlindSignature1 = new BlindSignature()
            {
                Id = fakeKeysetId,
                Amount = 1,
                C_ = DotNut.Cashu.ComputeC_(blindedMessages[0], a),
                DLEQ = null
            };
            var fakeBlindSignature2 = new BlindSignature()
            {
                Id = testKeyset.GetKeysetId(),
                Amount = 1,
                C_ = DotNut.Cashu.ComputeC_(blindedMessages[1], a),
                DLEQ = null
            };

            Assert.Throws<CashuPluginException>(() =>
            {
                var proofs = CashuUtils.CreateProofs(
                    [fakeBlindSignature1, fakeBlindSignature2],
                    blindingFactors.ToArray(),
                    secrets.ToArray(),
                    //use fake keyset
                    fakeKeyset
                );
            });
        }

        #endregion

    }
}
