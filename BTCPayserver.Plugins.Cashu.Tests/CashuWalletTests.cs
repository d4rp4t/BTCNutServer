using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Errors;
using DotNut;
using DotNut.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NBitcoin;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests;

public class CashuWalletTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    //RegTest local nutshell with polar backend
    private readonly string _localMint = "http://127.0.0.1:3338";
    //test mint, using mainnet invoices
    private readonly string _testnutMint  = "https://testnut.cashu.space";
    //this mint is mainnet
    private readonly string _stablenut = "https://stablenut.cashu.network/keysets";
    private readonly string _localKeysetId = "00168d7de17d8b9b";
    private readonly ILightningClient _lightningClient;


    public CashuWalletTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        var factory = new LightningClientFactory(Network.RegTest);
        _lightningClient = factory.Create(
            "type=lnd-rest;server=https://127.0.0.1:8086/;macaroon=0201036c6e640258030a107e1b055cd181bf990391021ece5d78e01201301a160a0761646472657373120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a0f0a076f6e636861696e12047265616400000620b896b2d3441b00de57f2864fffa0ea6dd5365f22fc03ea43de3d938e66e4446b;allowinsecure=true");
    }

    
    #region Test environment
    [Fact]
    public void DoesDbWork()
    {
        var dbf = CreateDb();
        using var db = dbf.CreateContext();
        
        db.Mints.Add(new Mint(_localMint));
        db.SaveChanges();
        
        var savedMint = db.Mints.Single();
        Assert.Equal(_localMint, savedMint.Url);
    }
    #endregion

    #region Constructor tests
    [Fact]
    public void Constructor_WithLightningClient_InitializesCorrectly()
    {
        var dbf = CreateDb();
        var wallet = new CashuWallet(
            _lightningClient,
            _testnutMint,
            "sat",
            dbf);

        Assert.NotNull(wallet);
    }

    [Fact]
    public void Constructor_WithLightningClient_WithoutDbContextFactory_InitializesCorrectly()
    {
        var wallet = new CashuWallet(_lightningClient, _testnutMint);

        Assert.NotNull(wallet);
    }

    [Fact]
    public void Constructor_WithoutLightningClient_InitializesCorrectly()
    {
        var dbf = CreateDb();
        var wallet = new CashuWallet(_testnutMint, "sat", dbf);

        Assert.NotNull(wallet);
    }

    [Fact]
    public void Constructor_WithoutLightningClient_WithoutDbContextFactory_InitializesCorrectly()
    {
        var wallet = new CashuWallet(_testnutMint);

        Assert.NotNull(wallet);
    }
    #endregion

    #region GetKeysets Tests

    [Fact]
    public async Task GetKeysets_ReturnsAllKeysets()
    {
        var wallet = new CashuWallet(_testnutMint);
        var testnutKeysets = await wallet.GetKeysets();
        Assert.NotNull(testnutKeysets);
        //keyset rotation killing my tests......... 
        // Assert.Equal(4, testnutKeysets.Count);
        var testnutKeysetsStr = testnutKeysets.Select(k=>k.Id.ToString()).ToList();
        Assert.Contains("009a1f293253e41e", testnutKeysetsStr);
        Assert.Contains("00c074b96c7e2b0e", testnutKeysetsStr);
        Assert.Contains("0042ade98b2a370a", testnutKeysetsStr);
        Assert.Contains("0054e990037fea46", testnutKeysetsStr);
        
        var wallet2 = new CashuWallet(_localMint);
        var localKeysets = await wallet2.GetKeysets();
        Assert.NotNull(localKeysets);
        Assert.Single(localKeysets);
        Assert.Equal("00168d7de17d8b9b", localKeysets.First().Id.ToString());
        
        //multiple keysets
        var wallet3 = new CashuWallet(_stablenut);
        var keysets3 = await wallet3.GetKeysets();
        Assert.NotNull(keysets3);
        //at this point we're ignoring old, base64 keyset format
        Assert.Equal(5, keysets3.Count);
        var keysetsStr = keysets3.Select(x => x.Id.ToString()).ToList();
        Assert.Contains("004146bdf4a9afab", keysetsStr);
        Assert.Contains("00f684e8a1ba8696", keysetsStr);
        Assert.Contains("005c2502034d4f12", keysetsStr);
        Assert.Contains("00f65219d21b5edf", keysetsStr);
        Assert.Contains("00c669f8fa51dc1d", keysetsStr);
        //old pre-v1 keyset format - "We don't talk about those."
        Assert.DoesNotContain("BDH4KBM+Xuyi", keysetsStr);
        Assert.DoesNotContain("vdVXOWRIk/HD", keysetsStr);
        Assert.DoesNotContain("xL7sGXXn7OZm", keysetsStr);
        Assert.DoesNotContain("Yc4wRPMzpNPQ", keysetsStr);
    }

    [Fact]
    public async Task GetKeysets_AddsToDb()
    {
        var dbf = CreateDb();
        var wallet = new CashuWallet(_testnutMint,"sat", dbf);
        var testnutKeysets = await wallet.GetKeysets();

        await using var ctx = dbf.CreateContext();
        var dbKeysets = ctx.MintKeys.Where(m=>m.Mint.Url == _testnutMint);
        Assert.NotNull(dbKeysets);
        Assert.Equal(testnutKeysets.Count, dbKeysets.Count());
        
        
        var dbKeysetsStr = dbKeysets.Select(k=>k.KeysetId.ToString());
        Assert.Contains("009a1f293253e41e", dbKeysetsStr);
        Assert.Contains("00c074b96c7e2b0e", dbKeysetsStr);
        Assert.Contains("0042ade98b2a370a", dbKeysetsStr);
        Assert.Contains("0054e990037fea46", dbKeysetsStr);
    }
    
    [Fact]
    public async Task GetKeysets_WithoutDbContext_DoesNotThrow()
    {
        var wallet = new CashuWallet(_testnutMint); // bez podania DbContextFactory
        var exception = await Record.ExceptionAsync(() => wallet.GetKeysets());
        Assert.Null(exception);
    }
    
    [Fact]
    public async Task GetKeysets_InvalidMint_ThrowsHttpRequestException()
    {
        var wallet = new CashuWallet("https://ihatewritingtests.com");
        await Assert.ThrowsAsync<HttpRequestException>(()=>wallet.GetKeysets());
    }
    
    
    [Fact]
    public async Task SaveKeysetToDb_StoresDataCorrectly()
    {
        var dbf = CreateDb();
        var wallet = new CashuWallet(_testnutMint, "sat", dbf);

        await using var ctx = dbf.CreateContext();
        var initialCount = await ctx.MintKeys.CountAsync();
        await wallet.GetKeysets();
        var newCount = await ctx.MintKeys.CountAsync();
        
        Assert.True(newCount > initialCount);
    }

    
    #endregion
    
    #region GetActiveKeyset Tests
    
    [Fact]
    public async Task GetActiveKeyset_WithCustomUnit_ReturnsActiveKeysetForUnit()
    {
        var wallet = new CashuWallet(_testnutMint, "usd");
        
        var result = await wallet.GetActiveKeyset();
        
        Assert.Equal("usd", result.Unit);
        Assert.True(result.Active);
        Assert.Equal((ulong)100, result.InputFee);
        Assert.Equal("00c074b96c7e2b0e", result.Id.ToString());
    }


    [Fact]
    public async Task GetActiveKeyset_WithCustomUnit_ReturnsActiveKeysetForDefaultUnit()
    {
        var wallet = new CashuWallet(_testnutMint);
    
        var result = await wallet.GetActiveKeyset();

        Assert.Equal("sat", result.Unit);
        Assert.True(result.Active);
        Assert.Equal((ulong)100, result.InputFee);
        // Assert.Equal("009a1f293253e41e", result.Id.ToString()); 
        // damn keyset rotation
    }
    
    [Fact]
    public async Task GetActiveKeyset_AddsKeysetsToDb()
    {
        //while getting keysets they still should be saved to db.
        var dbf = CreateDb();
        var wallet = new CashuWallet(_testnutMint, "usd", dbf);
        var activeKeyset = await wallet.GetActiveKeyset();
        Assert.NotNull(activeKeyset);
        var db = dbf.CreateContext();
        var dbKeysets = db.MintKeys.Where(m=>m.Mint.Url == _testnutMint);
        Assert.NotNull(dbKeysets);
        //keyset amount has changed since they're rotating so fast now.
        // Assert.Equal(4, dbKeysets.Count());

        var dbKeysetsStr = dbKeysets.Select(k=>k.KeysetId.ToString());
        Assert.Contains("009a1f293253e41e", dbKeysetsStr);
        Assert.Contains("00c074b96c7e2b0e", dbKeysetsStr);
        Assert.Contains("0042ade98b2a370a", dbKeysetsStr);
        Assert.Contains("0054e990037fea46", dbKeysetsStr);
        
        var dbMint = db.MintKeys.Where(m => m.Mint.Url == _testnutMint);
        Assert.NotNull(dbMint);
    }
    

    #endregion
    
    #region GetKeys Tests

    [Fact]
    public async Task GetKeys_ReturnsActiveKeysForNull()
    {
        var dbf = CreateDb();
        var wallet = new CashuWallet(_testnutMint, "usd", dbf);
        var keys = await wallet.GetKeys(null);
        var keysets = await wallet.GetKeysets();
        Assert.NotNull(keys);
        var corresponding = keysets.Single(k => k.Id.ToString() == keys.GetKeysetId().ToString());
        Assert.True(corresponding.Active);
        Assert.Equal("usd", corresponding.Unit);
    }
    
    [Fact]
    public async Task GetKeys_ReturnsActiveKeysForNullSatUnit()
    {
        var dbf = CreateDb();
        var wallet = new CashuWallet(_testnutMint, "sat", dbf);
        var keys = await wallet.GetKeys(null);
        var keysets = await wallet.GetKeysets();
        Assert.NotNull(keys);
        var corresponding = keysets.Single(k => k.Id.ToString() == keys.GetKeysetId().ToString());
        Assert.True(corresponding.Active);
        Assert.Equal("sat", corresponding.Unit);
    }
    
    
    [Fact]
    public async Task GetKeys_ReturnsCorrectKeysForKeyset()
    {
        var wallet = new CashuWallet(_testnutMint);
        var keysets = await wallet.GetKeysets();
        //wallets unit is 'sat', but let's choose the usd keyset
        var choosenKeyset = keysets.First(k => k.Unit == "usd");
        var correspondingKeys = await wallet.GetKeys(choosenKeyset.Id);
        Assert.NotNull(correspondingKeys);
        Assert.Equal(choosenKeyset.Id.ToString(), correspondingKeys.GetKeysetId().ToString());
    }
    
    [Fact]
    public async Task GetKeys_InvalidKeysetId_ReturnsEmpty()
    {
        var wallet = new CashuWallet(_testnutMint);
        var invalidKeysetId = new KeysetId("ffffffffffffffff");
        //error 12001 keyset not known
        await Assert.ThrowsAsync<CashuProtocolException>(async () => await wallet.GetKeys(invalidKeysetId));
    }
    

    #endregion
    
    #region Receive Tests
    [Fact]
    public async Task Receive_SwapsProofsCorrectlyWith0Fee()
    {
        //Use fresh token while testing! 
        var tokenStr = "cashuBo2FtdWh0dHA6Ly8xMjcuMC4wLjE6MzMzOGF1Y3NhdGF0gaJhaUgAFo194X2Lm2Fwg6RhYRhAYXN4QDg1MTY4NDZlMjFkZTEzMjEwNmUxNTU5Y2YyMWE3ZTAzZGFlYjhjZGI0NGU5MzlmOWQ2YmEyN2IxNWNkYjAwOWFhY1ghAiNf44mv55BYQ33EHNwoqHs22-DbmR2CzkNQ8xglBv2sYWSjYWVYIPzdOe13Lz2sKpDjLWgXTN_sOCiav66sh3DlvOpYPjbgYXNYIDHqelzz6GF53wAHkRH-fRgRDGWJikCkjCQbbbL8_S99YXJYIIzPds7HliwG9VhokeG7oOLf-Rgp0mBKoU3TTi2LsFOUpGFhGCBhc3hAMTJlN2Y3ZThlMmQ5MTI4NTIyZTcxODg2MTY1NDE0ZmRhMWQ1MTRhZTVhMzM1YmM5Nzg3NTliMDEyZTgxY2JkZGFjWCED3CcqBxNebv2P5mLoXX0WeeDg66NcsnOYBtqtPfY7ch1hZKNhZVggbOQbrSKjr8U1A7mtOQpEoVaJsrwRKyL86Vr34ZVqmT5hc1ggovNqAAVd2DsWp79yjQmBbrscyckAm1XpzLLyi3h0MlZhclggAgvZXsk55qYIJ8a1jtBNhpu58uVMnyMKT5iMtfYnsS-kYWEEYXN4QDFiZDYxNjYyMDVhZmUxZGNkMDRjY2RjNDJjOGRjNjNiMGI1MGMwYzRmYmNiYTI5MTA3ZGU2YmU1OGNmMjY2MzBhY1ghAlnVsIT_rfhddPklsLO1FFvcJ4_damtP-j6pW1zZaZeBYWSjYWVYILvxAl5BwTSJxoiwIAIreoS2tBGkCs_V9dSiSJXzOvPLYXNYIFU8hCVaS0b-9Qmm6QQzb348uxwz5ob2Lv_w9TEERKKwYXJYIFmmFfb4tSg6VgCji8uKLM-I6lEJhbfN_vexYEcwEUd7";
        var token = CashuTokenHelper.Decode(tokenStr, out _);
        var simpleToken = CashuUtils.SimplifyToken(token);
        var dbf = CreateDb();
        var wallet = new CashuWallet(_localMint, "sat", dbf);
        var response = await wallet.Receive(simpleToken.Proofs);
        Assert.True(response.Success);
        Assert.Null(response.Error);
        Assert.NotNull(response.ResultProofs);
        //all change proofs should be signed by one keyset
        var keyset = response.ResultProofs.Select(c => c.Id).Distinct().ToList();
        Assert.Single(keyset);
        var usedKeyset = keyset.Single();
        var keys = await wallet.GetKeys(usedKeyset);
        for (var index = 0; index < simpleToken.Proofs.Count; index++)
        {
            var proof = simpleToken.Proofs[index];
            var changeProof = response.ResultProofs[index];
            Assert.True(proof.Amount.Equals(changeProof.Amount));
            Assert.True(changeProof.Verify(keys[proof.Amount]));
        }
    }

    [Fact]
    public async Task Receive_SwapProofsCorrectlyWithFees()
    {
        //Use fresh token while testing! 
        var tokenStr = "cashuBo2FteBtodHRwczovL3Rlc3RudXQuY2FzaHUuc3BhY2VhdWNzYXRhdIGiYWlIAJofKTJT5B5hcISkYWEQYXN4QDNmYmI4ZTA0ZmQ5ZGEyMzUyYzg4MmRjODhlYTBhNDUwMzQxMzU4OTEzZWU3ZjNmOTY0NmMyODAzMzljOTA5YWZhY1ghAyyKLUDc9I1U8icTfGfD64ZlYp9XlvG0lCOO9-BqGyWxYWSjYWVYIPeEw0GNCkyDfHjLhv1NIM5_NAl5O0Xc0OUhrDc_iTzrYXNYIJ43HBEPGzjD_KK3SlgaH_LrErqJR4ttV3bx0mOCy-BMYXJYIOCsAJkUb1tMjFjRePgcFln5Sbh4dYXNsZ9vx7GDQbStpGFhEGFzeEA2MmYzOTRiNGIwMTFkYzQ1MWViMTBjMTM0OGI2MGY5YTZlMDUzZTY2ODJiYWM2MjgyZDBjZTg0YzIzOWIwYTU4YWNYIQLjsBKCQOSUi0t6UK5kM0p5xlMDZYvHmfcy1WN8EQ94-mFko2FlWCCmH4k0VqEX9ajxEHWY8xvSd6pelVUGaOXK-XhpwHn61WFzWCC3DzTVZd6EZzhH346gXu2fRfYc4UzbcpYPCVquYZrczmFyWCDBJXabhhwWt3SqX8MgZj11Oh-nx--NG4y8htYGF7ILjqRhYRBhc3hAOWVmZTMwNGI3NzczYTY2ZDAzYTEyMDNhM2IxZDQwNWYwNmY3NWNiOTU0OWIxYmY4NWVhZTZiNTU2MDhhNGY1MWFjWCECiCFenG2AnRXxwx6tjLVG5n1O7LPilXX1WPJwFcYxOQZhZKNhZVggKujRAVMpl1pF_Z5ZyKuYMDvz2ZFuTwiX4uwXeDojIpphc1ggYrgPIev7r-O6n9c6cPPAfQ8d4O0JHUG5YbagLXDQ1a1hclggj2HEqsLZx8wa9CiN6BfchAQGr1mBOHpVtSijB-J1goakYWECYXN4QGEyYzliODczODYyZTUzOGMxNzQ0MjI3YzA3NGI4NmZiOGM5N2Y1M2ExNjJlNjkyMzA4YTMyZWYyN2I5MDRmNzRhY1ghA92IX0LX-dkDorKGRRmiG79iiXcaV0OM9zutTpVKRa_JYWSjYWVYILndz7y3JcAo-mOFd9-BTOPlBctzPyF80iD4WHNDLFZYYXNYIJH-bfbDtU-zh6ApQhkCNykNUV_f40UFU6SuVrltG8pBYXJYIOmpwQ05xP83f21kYTFwBDz145eBG6ueqTwr_PzCBMVF";
        var token = CashuTokenHelper.Decode(tokenStr, out _);
        var simpleToken = CashuUtils.SimplifyToken(token);
        var dbf = CreateDb();
        var wallet = new CashuWallet(_testnutMint, "sat" , dbf);
        var keysets =await wallet.GetKeysets();
        var fee = simpleToken.Proofs.ComputeFee(keysets.ToDictionary(k => k.Id, k=>k.InputFee??0));
        var response = await wallet.Receive(simpleToken.Proofs, fee);
        if (!response.Success)
        {
            _testOutputHelper.WriteLine(response.Error.Message);
        }
        Assert.True(response.Success);
        
        Assert.NotNull(response.ResultProofs);
        
        var keyset = response.ResultProofs.Select(c => c.Id).Distinct();
        var keysetIds = keyset.ToList();
        Assert.Single(keysetIds);
        var usedKeyset = keysetIds.Single();
        var keys = await wallet.GetKeys(usedKeyset);
        for (var index = 0; index < simpleToken.Proofs.Count; index++)
        {
            var changeProof = response.ResultProofs[index];
            Assert.True(changeProof.Verify(keys[changeProof.Amount]));
        }
        Assert.True(simpleToken.Proofs.Select(p => p.Amount).Sum() == response.ResultProofs.Select(c=>c.Amount).Sum()+fee);
    }

    [Fact]
    public async Task Receive_AlradyUsedToken()
    {
        var tokenStr =
            "cashuBo2F0gaJhaUgAFo194X2Lm2FwgqNhYQJhc3hAZDhkYzc2ZGI1MWM2MmU5YTYzYjEyZGRkMDNiYzAxMGFkMTk0YzZiNDMzYmIzOWJhMTQwZmY0MjBiMWIwY2IzZGFjWCECr4RBkkvwdIiNtCT-h4NOiHPMa1mTgK-1CcUTUPp4csajYWEIYXN4QDk0ZTdkN2M2ZDA2ZGU1NGM0MzM3NmQxMjZlZTkxNDJhYmY5NDE3MzgwYjI0NTViMDUwNGFkNjFiOGY5YzNjNmVhY1ghAo5y3AqWR5T_-PVshNrEZd9J9bd8JOGI38J1g8NEdlI3YW11aHR0cDovLzEyNy4wLjAuMTozMzM4YXVjc2F0";
        var token = CashuTokenHelper.Decode(tokenStr, out _);
        var simpleToken = CashuUtils.SimplifyToken(token);
        var dbf = CreateDb();
        var wallet = new CashuWallet(_localMint, "sat", dbf);
        var result =  await wallet.Receive(simpleToken.Proofs);
        
        // Error 11001 - token already spent
        Assert.False(result.Success);
        Assert.IsType<CashuProtocolException>(result.Error);
    }
    [Fact]
    public async Task Receive_InvalidKeysetInToken_Throws()
    {
        var invalidToken = new CashuUtils.SimplifiedCashuToken()
        {
            Mint = _localMint,
            Unit = "yen",
            Proofs =
            [
                new Proof()
                {
                    Amount = 128,
                    C = new PrivKey(new string('0', 63) + "1").Key.CreatePubKey(),
                    Secret = new StringSecret(new string('1', 64)),
                    Id = new KeysetId(new string('1', 16))
                }
            ],
        };
        var cashuWallet = new CashuWallet(_localMint, "sat", CreateDb());

        var result = await cashuWallet.Receive(invalidToken.Proofs);
        
        Assert.False(result.Success);
        //Keyset
        Assert.IsType<CashuProtocolException>(result.Error);
        //Invalid Keyset
 }
    
    [Fact]
    public async Task Receive_InvalidAmountsInToken_Throws()
    {
        var invalidToken = new CashuUtils.SimplifiedCashuToken()
        {
            Mint = _localMint,
            Unit = "yen",
            Proofs =
            [
                new Proof()
                {
                    Amount = 100,
                    C = new PrivKey(new string('0', 63) + "1").Key.CreatePubKey(),
                    Secret = new StringSecret(new string('1', 64)),
                    Id = new KeysetId(new string('1', 16))
                }
            ],
        };
        var cashuWallet = new CashuWallet(_localMint, "sat", CreateDb());
        
        //Invalid amounts
       await Assert.ThrowsAsync<ArgumentException>( async () => await cashuWallet.Receive(invalidToken.Proofs)); 
    }
    
    [Fact]
    public async Task Receive_FeeBiggerThanAmount_Throws()
    {
        var invalidToken = new CashuUtils.SimplifiedCashuToken()
        {
            Mint = _localMint,
            Unit = "yen",
            Proofs =
            [
                new Proof()
                {
                    Amount = 2,
                    C = new PrivKey(new string('0', 63) + "1").Key.CreatePubKey(),
                    Secret = new StringSecret(new string('1', 64)),
                    Id = new KeysetId(new string('1', 16))
                }
            ],
        };
        var cashuWallet = new CashuWallet(_localMint, "sat", CreateDb());
        //Fee bigger tha provided amount
        await Assert.ThrowsAsync<CashuPluginException>(async () => await cashuWallet.Receive(invalidToken.Proofs, 4));
    }   
    
    [Fact]
    public async Task Receive_Throws()
    {
        var cashuWallet = new CashuWallet(_localMint, "sat", CreateDb());

        var invalidToken = new CashuUtils.SimplifiedCashuToken()
        {
            Mint = _localMint,
            Unit = "yen",
            Proofs =
            [
                new Proof()
                {
                    Amount = 2,
                    C = new PrivKey(new string('0', 63) + "1").Key.CreatePubKey(),
                    Secret = new StringSecret(new string('1', 64)),
                    Id = (await cashuWallet.GetActiveKeyset()).Id
                }
            ],
        };
        var result = await cashuWallet.Receive(invalidToken.Proofs);
        Assert.False(result.Success);
        //Invalid signature - can't be verified
        Assert.IsType<CashuProtocolException>(result.Error);
    }   
    #endregion
    
    
    #region swap 
    //swap method was mostly covered in receive method tests
    #endregion
    
    
    #region CreateMeltQuote
    [Fact]
    public async Task CreateMeltQuote_WithValidToken_ReturnsValidQuote()
    { 
        //let's use fresh, 100 sat token
        var tokenStr = "cashuBo2F0gaJhaUgAFo194X2Lm2Fwg6NhYQRhc3hAZWQzZDc5ZmY1ZWQzMDZiZGNhYzc5MTc4OGYyM2YxNDhhYmFmZDM3Yzg2MDM0YzYxMTVmOWNkZGYxNDk3ZjNmYWFjWCECFabppMb7eJW2bXk8oC1eEz28fjT1IOduvf3HqkRgVm6jYWEYIGFzeEBkNDUwNGVkNTdmNjE4YjMwNmE2YjA4ZDI2NjI4ZGUzZjgwNzBhZDg5ZTY4OTM1NjE4Njc0ZTBiOWIwZWEzYzAzYWNYIQPWWa_yGY5yVxLkMYydiTusJwyAvieLdGyi81ge2s5qpKNhYRhAYXN4QDZkNWIyOGQyZDhjYmNkOTlkYTI5MjkxNGQ0ZDZlYzk5ZTE2M2FlNjI0NjQwNmE5MmIxNWQzMTg2NjMzZWMwODhhY1ghA6_wYZIKyYCBkNKgT6eLW2CFNY6wuVcsoAdERoov8WTTYW11aHR0cDovLzEyNy4wLjAuMTozMzM4YXVjc2F0";
        var token = CashuUtils.SimplifyToken(CashuTokenHelper.Decode(tokenStr, out _));
        var singleUnitPrice = 1;
        
        var wallet = new CashuWallet(_lightningClient, _localMint, "sat", CreateDb());
        var keysets = await wallet.GetKeysets();
        var result = await wallet.CreateMeltQuote(token, singleUnitPrice, keysets);
        
        //My local nutshell instance is configured for 2% fee reserve. For 100 sats it should be 2 sat
        //Also, there's no input fee
        Assert.True(result.Success);
        Assert.NotNull(result.MeltQuote);
        Assert.Equal(2, result.MeltQuote.FeeReserve);
        Assert.Equal((ulong)98, result.MeltQuote.Amount);
        Assert.Null(result.MeltQuote.Change);
        Assert.NotNull(result.MeltQuote.Quote);
        Assert.NotNull(result.Invoice);
        Assert.Equal(result.Invoice.Amount, LightMoney.Satoshis(98));
        
}

    [Fact]
    public async Task CreateMeltQuote_WithoutLightningClient_ReturnsError() 
    {
        var tokenStr = "cashuBo2F0gaJhaUgAFo194X2Lm2Fwg6NhYQRhc3hAZWQzZDc5ZmY1ZWQzMDZiZGNhYzc5MTc4OGYyM2YxNDhhYmFmZDM3Yzg2MDM0YzYxMTVmOWNkZGYxNDk3ZjNmYWFjWCECFabppMb7eJW2bXk8oC1eEz28fjT1IOduvf3HqkRgVm6jYWEYIGFzeEBkNDUwNGVkNTdmNjE4YjMwNmE2YjA4ZDI2NjI4ZGUzZjgwNzBhZDg5ZTY4OTM1NjE4Njc0ZTBiOWIwZWEzYzAzYWNYIQPWWa_yGY5yVxLkMYydiTusJwyAvieLdGyi81ge2s5qpKNhYRhAYXN4QDZkNWIyOGQyZDhjYmNkOTlkYTI5MjkxNGQ0ZDZlYzk5ZTE2M2FlNjI0NjQwNmE5MmIxNWQzMTg2NjMzZWMwODhhY1ghA6_wYZIKyYCBkNKgT6eLW2CFNY6wuVcsoAdERoov8WTTYW11aHR0cDovLzEyNy4wLjAuMTozMzM4YXVjc2F0";
        var token = CashuUtils.SimplifyToken(CashuTokenHelper.Decode(tokenStr, out _));
        var singleUnitPrice = 1;
    
        var wallet = new CashuWallet(_localMint);
        var keysets = await wallet.GetKeysets();

        var response = await wallet.CreateMeltQuote(token, singleUnitPrice, keysets);
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Null(response.MeltQuote);
        Assert.Equal("Lightning client is not configured", response.Error.Message);
    }

    [Fact]
    public async Task CreateMeltQuote_ValidToken_WithKeysetFees()
    {
        //let's use fresh, 100 sat token
        // this one has 18 proofs, and this mint has input fee 100
        var tokenStr = "cashuBo2FteBtodHRwczovL3Rlc3RudXQuY2FzaHUuc3BhY2VhdWNzYXRhdIGiYWlIAJofKTJT5B5hcJKkYWEBYXN4QDYzYzAwZjJhNDhjMGRhMWMxMGI1ZTY5MjllNTZkMjg3YzU0N2M3OGUxMGJhMDgyMWVkNjJmZmMwN2ZlYzg3MDNhY1ghAvmxdD1pFHpdHAvbSyz8lgr0-MjWw71yTHmVcleodVBOYWSjYWVYIPvm1px5in6-KQUqxHZ55LUAfIYMOH4hIzRsz02EwkgTYXNYIGxHdMFhQFFuQtYVKxsfqoKA8hDXy-TggqM6xi4ZxGkrYXJYIL9ZkQAJPbsycNzD-qAZmuJz5stw1GlZL3uQpOpYQDJypGFhAWFzeEBjNjJjYjEwM2EyMDJjNjk4ZDRlMjcwOGU1NDc4Zjg0ZTQzMWUzM2RlMWYzNzNjYWFmNTAxNDJlYjBkZjI4NjQ5YWNYIQNU5CucNLO7lunOFrJRXNLp1ZKKa1Q1viV0dEewrKnaUWFko2FlWCDEozOmOE_H9e9_-Rt9c8U4eJ5_2e4ygy8-IN1eXZ6RSmFzWCBxt20RZ3YmHu2UF6oHvO1Yuqk45gfd_f5w3JyncgkJGWFyWCDvdt1mBV2PXjqJPMgexb92m9661E2o9Ksd5Vgf_HHrj6RhYQFhc3hAMjJjNmU3NWVlYWJlYjVjYjE3NzY4MzY0YzM3YzQ5ZWNiMzc5MGJjZjMzMzdlYjE1ZTMzYTJiODAzNWNmNmY1OWFjWCECgzdN_Imb6kUSLZJyAgJcBYIhr4qIxgN8iBoc2vZY_WthZKNhZVggHPS3YBsa1ZNnLBRhAQlQ-R4EmzfLHaA50zGSLfACz0lhc1ggsbEJ3fJGE-772oeb14Wn27zqX3oQE957I1qlWpKF3nlhclggs41jKilPwpHKjgV4J500RDLAT9MofQDRp5aqBDJs-BikYWEBYXN4QDdmYWEyNThjNjdjOTIwNTgxNDcwMTAwZDY4MGQwZjBhNzczMTMxMzRlN2I4NjM3MDRiMzM5NjAxNDk0OWFiZDFhY1ghA2j2Td5akIwHQnLG06R-JAln_O8eHQQIVFg7YgAbO0hkYWSjYWVYILeq7UYZQCQsJdPybJFqnWYUKUBHn2C1jYqDX6mAFPuNYXNYIK7IpsPtoe8vse8R6Lx5s5YOjAkR11x7nGuJLnYYtrR4YXJYIIHY7QBfbEMN2OoNQY2oMHxhY1a3bbG7xMQd2chppD6PpGFhAmFzeEA2MGYzZmM2NzVkZjRiMzU1NWFmMWM3MjJlM2ZhZmE0YzA4MDFjYjk0NjkxMDk5NzBkOTQzYWQ5ZDZiZGJjNTMwYWNYIQOwWVFcKT5ODF_rDvsqnPgJtY6JegJytpXGY6Zy4YOx_mFko2FlWCDYshcsEhpeh4rwSPe0DcBXwAaqdU9-aCONAlqqXWAIoWFzWCDm_xDtpRuAA8YNIFdPafII8HC2I9S0LaDB9bDZbPKiR2FyWCDzyDa5RNrIsQTx6vlUcg5MBu35OVPTWFg4Qtc9quj_VKRhYQJhc3hAZjM3OGNkMjY1YTE2ZjQyMDJiZDNiMjQ4ZGI0ZjZlMDU1NWE0YmUyNzg2MDk2NGQ5ZWRiYWM5MjM5MjI5NWI1NWFjWCECmO1feKOs7T67Bn6mntcddopiSGe4u-tV2dmHv1_GkTJhZKNhZVggJCsriDko50TFH7hq7x9ApsmPJsv54V1e5U7St_CGJOJhc1ggBYpJAUDBr-hdDYIWdXKBO_S0aWEaZyDPjBqYWwOdrAlhclggOpoK9BofmO4v1bJgCB81_iQOSPkQ-zrSmC-viwSmifqkYWECYXN4QGU0MDRiY2E5MTg2ZmYyMWZkZjI0ZWI3Nzk5OWFiNDBiMjE1YTk0MTVkZGY1NzAyNzgwODVlNDlmMDYwMzRjNDBhY1ghAu0DsPQm3cvstcbpg_jLB6Cn4uqBdFODFuX9hgNwT4goYWSjYWVYIOKjnhE0dQLu7-oArtG0DKs_QldOf_q6BflmaUcZUEyjYXNYIIAHHA3Nh5NBJjf0hdJHn1w_CfCwJHHdABvEYrfyUBfAYXJYICA-uoE18Lcza7AzzBocHK8ZqCFx-wf4Ur_WfRnUn0_KpGFhAmFzeEBhMmM5Yjg3Mzg2MmU1MzhjMTc0NDIyN2MwNzRiODZmYjhjOTdmNTNhMTYyZTY5MjMwOGEzMmVmMjdiOTA0Zjc0YWNYIQPdiF9C1_nZA6KyhkUZohu_Yol3GldDjPc7rU6VSkWvyWFko2FlWCC53c-8tyXAKPpjhXffgUzj5QXLcz8hfNIg-FhzQyxWWGFzWCCR_m32w7VPs4egKUIZAjcpDVFf3-NFBVOkrla5bRvKQWFyWCDpqcENOcT_N39tZGExcAQ89eOXgRurnqk8K_z8wgTFRaRhYQRhc3hAZTBjMDJlOGM5MzhkYTczODRhNDMxNGM4YTUzNTQ1MmYyYmFiYmFlZTFjNWQzZWM0NjI2MjdhNmNlZmE2YWVjMGFjWCEC5hI-vJL0UHZQj83g8MmolnvxnpmjO4kbB4P-Q9W1xZthZKNhZVggtSZFszwrfhNfrFyzAt5fvKfLDFVX70IYbDLrnH3lQGRhc1ggUcM2eqep1gqw1ZVNMKb6ceCLpqPal32bXaerG7D1BmphclggtjZL709-tiolBjOPJL-yaw-SCESxX0eE0903F1f1DVykYWEEYXN4QDAzYmZiYjFkNTgxMzZjM2IxZWI2NzNjYTdmY2MwNTYwNTFmZmIyYzBiODExOGU3YjU4MjgxMGNmZTBkYzViMTRhY1ghAhtHW9NLRYi4itlSApBobSJ0Fnj69ulX5KkkptZ4BfkDYWSjYWVYIFmDtZLI_RNAPXRIrRAMbwNwzGplYBeSVhq1s7KWBxxDYXNYIK7ayIKppwZRsY9vCNnzHhPJJZnyid1eVWOKo60sjHhdYXJYIEhZlfIdCw-zgnZ4Tr351D9eZy01Ws8yIUmQ99KUgp6PpGFhBGFzeEBiZWFhNmViZDhiMDg2YmJiM2EzZWRlZjYxYzFlOTIzN2EyZDAyZWZjNjdlNjk1MzVjMzU1NjZhYjkzODI5MTA3YWNYIQKgOANqCi83P19XcFyZIDRwmpr863wqCaZWGCchC2xzn2Fko2FlWCBpK0ScDVRF2YNK09GTtU5ZirL1CuigFIRCpzUxVqIxKWFzWCDDCmYGe-U_RcGUoemIX2WItTKGp3ILmnt3Vf3JIiSASmFyWCAH_84Cev4ZcROA6NuRJqehT1OAZTzsAhnanpWz4iIx0qRhYQRhc3hAOTFkMGIyYWU3Y2ZkZTJiZjE0ZGRkZjg0MTkxNzczNTBjNWZlMjEyYTA4ZWE4N2MzMDUxMDBjMGY3MGQ4M2I1ZWFjWCECr7X8UKEAKqjHPFLMadH5IqFL6dxwathFVLR3F4pGLx9hZKNhZVgge3wDaeJ6vcRaCwFOL4_GOXO3LjCiAiv6pltu2HoWaq9hc1ggNnWln_M4xfskI2K9DKKVpJ1I7EkQzK5Y6KzzWa_elQZhclggyJTDC7Nwy4K-8kXFl7OLf95OUqpfcabA7BFGObniJRakYWEIYXN4QDQyNjI3MjI3ZDQ5NmQ3OWE5MTljNDJmMTAxMjUwMzMwNjk0YjQyYThkMzAzYTU2MTgzMGJjYmFkY2YzNGVhNjVhY1ghAgWZahF6YZmxxa6JsN6H7M9kPfbDbd5y3_xSu9mzFOinYWSjYWVYIAkI_2Tg61XBjRatxGBM4Tmnx0FuydLs2Qr1LZq-FEtnYXNYIIwzuK-JyksX42fJy-l6Bkkm_84SmGNq3_WWi3zvsHrGYXJYIKUG9XK0r5wChE7Jkst84_w7llyjg5tsAc2R4FqJM7TXpGFhCGFzeEA3OTJjZjNlMDI1YzI1OTBkYmVlZGIxMDAzMTdlNzIwODQ0MTQ5ODdkOTBjZDI3MTQ1MWMwM2UwZDU1YTU2OTg5YWNYIQLkBy0nad2602aU_7ijLFPLI7IuZ5cw9TIEun3f2N9r5GFko2FlWCDenENtfGw4TQpmPG5mlndcZ0jlCPPWZWQsXlR9n2A9hGFzWCDWhaTvAHe4SbG2dTn3QRf-vVe5ZmuZiLARQrn-qjYTbmFyWCD9tjxPkz8-YelSKv2lgSuT1pPbxfyqbDP-xFllckE9N6RhYQhhc3hAMDE0MTAwZmIwNTQ1NjMwMzVmNGQ5ZDVkMDkzMDNiZDUwMzBkM2NiNTUyYjA5YmEzYzdmOTk4ZTIwNTY0NGQ5YmFjWCECgcB_ZLXELa0nyMo1wBxTGaVV8VLzfrFB7Umj3CeiieJhZKNhZVggULlCbHOSy8JUFaT2VcFBbIyiz5iEsZccC7xpKz9PoTdhc1ggUYv9g8wkOcKZvAw8p1VKkQUwVEykLF5-D8yTJoyZwMphclggOQIehXnkh6dM-B3hXsXTUAgzD9yyMvlLMHEOWTDcisekYWEQYXN4QDllZmUzMDRiNzc3M2E2NmQwM2ExMjAzYTNiMWQ0MDVmMDZmNzVjYjk1NDliMWJmODVlYWU2YjU1NjA4YTRmNTFhY1ghAoghXpxtgJ0V8cMerYy1RuZ9Tuyz4pV19VjycBXGMTkGYWSjYWVYICro0QFTKZdaRf2eWcirmDA789mRbk8Il-LsF3g6IyKaYXNYIGK4DyHr-6_jup_XOnDzwH0PHeDtCR1BuWG2oC1w0NWtYXJYII9hxKrC2cfMGvQojegX3IQEBq9ZgTh6VbUoowfidYKGpGFhEGFzeEAzZmJiOGUwNGZkOWRhMjM1MmM4ODJkYzg4ZWEwYTQ1MDM0MTM1ODkxM2VlN2YzZjk2NDZjMjgwMzM5YzkwOWFmYWNYIQMsii1A3PSNVPInE3xnw-uGZWKfV5bxtJQjjvfgahslsWFko2FlWCD3hMNBjQpMg3x4y4b9TSDOfzQJeTtF3NDlIaw3P4k862FzWCCeNxwRDxs4w_yit0pYGh_y6xK6iUeLbVd28dJjgsvgTGFyWCDgrACZFG9bTIxY0Xj4HBZZ-Um4eHWFzbGfb8exg0G0raRhYRBhc3hANjJmMzk0YjRiMDExZGM0NTFlYjEwYzEzNDhiNjBmOWE2ZTA1M2U2NjgyYmFjNjI4MmQwY2U4NGMyMzliMGE1OGFjWCEC47ASgkDklItLelCuZDNKecZTA2WLx5n3MtVjfBEPePphZKNhZVggph-JNFahF_Wo8RB1mPMb0neqXpVVBmjlyvl4acB5-tVhc1ggtw801WXehGc4R9-OoF7tn0X2HOFM23KWDwlarmGa3M5hclggwSV2m4YcFrd0ql_DIGY9dTofp8fvjRuMvIbWBheyC44";
        var token = CashuUtils.SimplifyToken(CashuTokenHelper.Decode(tokenStr, out _));
        var singleUnitPrice = 1;
        
        var wallet = new CashuWallet(_lightningClient, _testnutMint, "sat", CreateDb());
        var keysets = await wallet.GetKeysets();
        var result = await wallet.CreateMeltQuote(token, singleUnitPrice, keysets);
        
        // Math.Ceiling(18*100/1000) = 2
        var keysetFee = token.Proofs.ComputeFee(keysets.ToDictionary(k => k.Id, k => k.InputFee??0));
        Assert.Equal((ulong)2, keysetFee);
        Assert.True(result.Success);
        Assert.NotNull(result.MeltQuote);
        Assert.Equal(2, result.MeltQuote.FeeReserve);
        //100-2-2 = 96
        Assert.Equal((ulong)96, result.MeltQuote.Amount);
        Assert.Equal("UNPAID", result.MeltQuote.State);
        Assert.Null(result.MeltQuote.Change);
        Assert.NotNull(result.MeltQuote.Quote);
        Assert.NotNull(result.Invoice);
        Assert.Equal(result.Invoice.Amount, LightMoney.Satoshis(96));
    }

    [Fact]
    public async Task CreateMeltQuote_InvalidToken_Throws()
    {
        var cashuWallet = new CashuWallet(_lightningClient, _testnutMint, "sat", CreateDb());
        var keysets = await cashuWallet.GetKeysets();
        var invalidToken = new CashuUtils.SimplifiedCashuToken()
        {
            Mint = _localMint,
            Unit = "yen",
            Proofs =
            [
                new Proof()
                {
                    Amount = 2,
                    C = new PrivKey(new string('0', 63) + "1").Key.CreatePubKey(),
                    Secret = new StringSecret(new string('1', 64)),
                    Id = (await cashuWallet.GetActiveKeyset()).Id
                }
            ],
        };
        //Invalid token - can't verify mints signature
        var result = await cashuWallet.CreateMeltQuote(invalidToken, 1, keysets);
        Assert.False(result.Success);
        Assert.IsType<CashuProtocolException>(result.Error);
    }

    #endregion

    #region Melt tests
    //each test should run on freshly created token. 
    [Fact]
    public async Task Melt_ValidToken_MeltsCorrectly()
    {
        //create melt quote
        var tokenStr = "cashuBo2F0gaJhaUgAFo194X2Lm2FwhaNhYRggYXN4QDI4ZWVhNjVkYmY2NjZhYjE1ZDEwMDE0NDVjN2FiNzk2OTY2MjA4Yjg2ODFlYjg0ZDI4MDFmODI5MDIwMDEzOGZhY1ghAidYlTXPlxo9AsMUxBp7s_9KdcMQPflFuCy1o1nmAcNzo2FhGCBhc3hANTZlYTg4NTNhZGU3NmZhODYzMjQ5YTgyYzdhMGY4MmY1Y2M3OWUyZTkwOWQwMzk3NzVkOGE4ZGZhNWY0MjBmMWFjWCEDrYJsBQ8dzScui0_A1XyhqwfJOAQkWpupFuT_i8dtJ9mjYWEQYXN4QGFkMzhiMjYwNzY1YWM1MzQxYTlhZTI3OWNlMjBhNTIzZTYyMTM4NDgxZmFhNmExMjdkNzVmYzUzODg4OTYwMzNhY1ghAorZGGSs2Drh8qSfSOugvR20-Xuy3KyVx328qz88YRF3o2FhEGFzeEBkN2JjZTNiZDlkMTY1NjNjNjRmYTkzNDE4NGJjNTc4YWJmNzAwMDBiYzU1NmE2Yzk4Yzg3YTJmN2MxZmZhNDE3YWNYIQJHElr2xbbZiT4AL3Rnunp5YjhrobeMU0lDOW8vNrVhR6NhYQRhc3hANzc2NjhhZjkyNmZkMTA2MmNlZjU2ZmEzZGFlMWJlZDJhOTE3NjdmMmJlYjg1ZDg5Nzc0NjliMGMyZTMwYzQxZGFjWCECioqg0Jh2Xk5HSa7b9T3irEraTOy2K_kjyWZZlne7YmBhbXVodHRwOi8vMTI3LjAuMC4xOjMzMzhhdWNzYXQ";
        var token = CashuUtils.SimplifyToken(CashuTokenHelper.Decode(tokenStr, out _));
        var singleUnitPrice = 1;
        var wallet = new CashuWallet(_lightningClient, _localMint, "sat", CreateDb());
        var keysets = await wallet.GetKeysets();
        var quote = await wallet.CreateMeltQuote(token, singleUnitPrice, keysets);
        Assert.NotNull(quote.MeltQuote);
        Assert.NotNull(quote.Invoice);
        
        //melt
        var response = await wallet.Melt(quote.MeltQuote, token.Proofs);
        
        Assert.True(response.Success);
        Assert.NotNull(response.Quote);
        Assert.NotNull(response.ChangeProofs);
        Assert.NotEmpty(response.ChangeProofs);
        Assert.Equal("PAID", response.Quote.State);
        //each proof should be signed by the same keyset 
        var keys = await wallet.GetKeys(response.ChangeProofs.First().Id);
    
        foreach (var proof in response.ChangeProofs)
        {
            Assert.True(proof.Verify(keys[proof.Amount]));
        }
        var postMeltInvoice = await _lightningClient.GetInvoice(quote.Invoice.Id);
        
        Assert.Equal(LightningInvoiceStatus.Paid, postMeltInvoice.Status);
    }
    [Fact]
    public async Task Melt_AlreadySpentToken_Throws()
    {
        //create melt quote
        var tokenStr = "cashuBo2F0gaJhaUgAFo194X2Lm2Fwg6NhYQRhc3hAZWQzZDc5ZmY1ZWQzMDZiZGNhYzc5MTc4OGYyM2YxNDhhYmFmZDM3Yzg2MDM0YzYxMTVmOWNkZGYxNDk3ZjNmYWFjWCECFabppMb7eJW2bXk8oC1eEz28fjT1IOduvf3HqkRgVm6jYWEYIGFzeEBkNDUwNGVkNTdmNjE4YjMwNmE2YjA4ZDI2NjI4ZGUzZjgwNzBhZDg5ZTY4OTM1NjE4Njc0ZTBiOWIwZWEzYzAzYWNYIQPWWa_yGY5yVxLkMYydiTusJwyAvieLdGyi81ge2s5qpKNhYRhAYXN4QDZkNWIyOGQyZDhjYmNkOTlkYTI5MjkxNGQ0ZDZlYzk5ZTE2M2FlNjI0NjQwNmE5MmIxNWQzMTg2NjMzZWMwODhhY1ghA6_wYZIKyYCBkNKgT6eLW2CFNY6wuVcsoAdERoov8WTTYW11aHR0cDovLzEyNy4wLjAuMTozMzM4YXVjc2F0";
        var token = CashuUtils.SimplifyToken(CashuTokenHelper.Decode(tokenStr, out _));
        var singleUnitPrice = 1;
        var wallet = new CashuWallet(_lightningClient, _localMint, "sat", CreateDb());
        var keysets = await wallet.GetKeysets();
        var quote = await wallet.CreateMeltQuote(token, singleUnitPrice, keysets);
        //melt
        var result = await wallet.Melt(quote.MeltQuote, token.Proofs);
        Assert.False(result.Success);
        Assert.IsType<CashuProtocolException>(result.Error);
        
        Assert.Equal(11001, (result.Error as CashuProtocolException).Error.Code);
    }

    [Fact]
    public async Task Melt_InvalidToken_Throws()
    {
        var invalidToken = new CashuUtils.SimplifiedCashuToken()
        {
            Mint = _localMint,
            Unit = "sat",
            Proofs =
            [
                new Proof()
                {
                    Amount = 128,
                    C = new PrivKey(new string('0', 63) + "1").Key.CreatePubKey(),
                    Secret = new StringSecret(new string('1', 64)),
                    Id = new KeysetId(new string('1', 16))
                }
            ],
        };
        
        var singleUnitPrice = 1;
        var wallet = new CashuWallet(_lightningClient, _localMint, "sat", CreateDb());
        var keysets = await wallet.GetKeysets();
        var quote = await wallet.CreateMeltQuote(invalidToken, singleUnitPrice, keysets);
        //melt
        var result = await wallet.Melt(quote.MeltQuote, invalidToken.Proofs);
        Assert.False(result.Success);
        Assert.IsType<CashuProtocolException>(result.Error);
    }
    [Fact]
    public async Task Melt_FakedAmount_Throws()
    {
        var tokenStr = "cashuBo2F0gaJhaUgAFo194X2Lm2FwhqNhYQJhc3hAODhmODg1ZjQwMDJkZDNkMGM4MDUwNWEwMGZhMGRjNzFhOWEyMTdlMzY3ZGFlZjRiMWM4N2Y4MDA4YjI3YzgyYWFjWCECLdMmvfiGAh5NlaCGk3ffGURLV2_JxecA1lVO45mPVM6jYWECYXN4QGI0YTJhNTU4YzZjMzBmM2Y5YzA3ZWQwYWFkZmZmZDliMzZjNTBjY2M3NzI5MGNhMGE5YzQxYmVhNDRhNGU5NTBhY1ghA47duf7y3PAv89VHOixo7Rn8NaetZj0bSTAG8_8gTMJMo2FhAmFzeEAwMmNkNmUzNzg3NDM5OTFjZmJiNGZkY2ZlZDQ0YmQzODI3MTJiYjY2MWUxMDRlMjY1MzVmMmIwZjc5ZjA5MWUzYWNYIQNTjy8arg5rLvt6u8vCRQDga1Wz6qb7znUtnI1vYa0dz6NhYQJhc3hAYTFiYjIzNjg5YTk4YzQ1MTg0MjlmZDk3MTVhYWYxMGNiNGNlMzcxNGM2NDBjMTg0ZjM0YjMwMGEwMDllOWYyZWFjWCEDXOuDv9qEyuHBg7s684oyrW58lhOdVzy7CLxHc-ynl_ujYWEBYXN4QDg3NTYzM2MxOWZhY2FkODE5ZTMyNzQ5ZjQ3NGIxNzU0NzYxMTE5MTIxOGFlZWVjYTVlMzYwNmQ0ODBkYTQ2MDRhY1ghA7LjyhGUAAvP4Z9u8wrMSCY2M63DZUvnAfj7Dq8MoVMgo2FhAWFzeEBhYjRmNmQ0NzM3NmU5YjYyZjdmOWRlYjA4YWNjOWIyNWMwZDQxODliMjBmZTMwOTM5ZTY1NWMwNWM1MWUzNTc3YWNYIQOPpoOK61Zxg7H2mIF2WSOVOPlDQ8c_0Km9hGWsg66MiWFtdWh0dHA6Ly8xMjcuMC4wLjE6MzMzOGF1Y3NhdA";
        decimal rate = 3; //it's sat token so rate is faked to be 3x bigger
        var token = CashuUtils.SimplifyToken(CashuTokenHelper.Decode(tokenStr, out _));
        var wallet = new CashuWallet(_lightningClient, _localMint, "sat", CreateDb());
        var keysets = await wallet.GetKeysets();
        var quote = await wallet.CreateMeltQuote(token, rate, keysets);
        //melt
        var result = await wallet.Melt(quote.MeltQuote, token.Proofs);
        Assert.False(result.Success);
        Assert.IsType<CashuProtocolException>(result.Error);
    }

    #endregion
    private class TestCashuDbContextFactory : CashuDbContextFactory
    {
        private readonly DbContextOptions<CashuDbContext> _options;

        public TestCashuDbContextFactory(DbContextOptions<CashuDbContext> options) 
            : base(Options.Create(new DatabaseOptions()))
        {
            _options = options;
        }

        public override CashuDbContext CreateContext(
            Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            return new CashuDbContext(_options);
        }
    }
    private CashuDbContextFactory CreateDb()
    {
        var options = new DbContextOptionsBuilder<CashuDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestCashuDbContextFactory(options);
    }
}