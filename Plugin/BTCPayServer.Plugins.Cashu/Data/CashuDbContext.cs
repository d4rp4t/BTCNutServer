using System.Collections.Generic;
using System.Text.Json;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.Data.Migrations;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using DotNut.JsonConverters;
using DotNut.NBitcoin.BIP39;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using FailedTransaction = BTCPayServer.Plugins.Cashu.Data.Models.FailedTransaction;
using ISecret = DotNut.ISecret;

namespace BTCPayServer.Plugins.Cashu.Data;

public class CashuDbContext(DbContextOptions<CashuDbContext> options, bool designTime = false)
    : DbContext(options)
{
    public static string DefaultPluginSchema = "BTCPayServer.Plugins.Cashu";
    public DbSet<Mint> Mints { get; set; }
    public DbSet<MintKeys> MintKeys { get; set; }
    public DbSet<StoredProof> Proofs { get; set; }
    public DbSet<FailedTransaction> FailedTransactions { get; set; }
    public DbSet<ExportedToken> ExportedTokens { get; set; }
    public DbSet<CashuWalletConfig> CashuWalletConfig { get; set; }
    public DbSet<StoreKeysetCounter> StoreKeysetCounters { get; set; }
    public DbSet<CashuLightningClientInvoice> LightningInvoices { get; set; }
    // public DbSet<CashuLightningClientPayment> LightningPayments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var outputDataJsonOptions = new JsonSerializerOptions
        {
            Converters = { new SecretJsonConverter(), new PrivKeyJsonConverter() }
        };
        
        modelBuilder.HasDefaultSchema(DefaultPluginSchema);

        modelBuilder.Entity<StoredProof>(entity =>
        {
            entity.HasKey(sk => sk.ProofId);
            entity.HasIndex(sk => sk.Id);
            entity.HasIndex(sk => sk.StoreId);
            entity.HasIndex(sk => sk.Amount);
            entity.HasIndex(sk => sk.Status);
            entity.HasIndex(sk => sk.ExportedTokenId);
            entity.HasIndex(sk => sk.Secret).IsUnique();

            entity
                .Property(p => p.C)
                .HasConversion(pk => pk.ToString(), pk => new PubKey(pk, false));
            entity
                .Property(p => p.Id)
                .HasConversion(ki => ki.ToString(), ki => new KeysetId(ki.ToString()));
            entity
                .Property(p => p.Secret)
                .HasConversion(
                    s =>
                        JsonSerializer.Serialize(
                            s,
                            new JsonSerializerOptions { Converters = { new SecretJsonConverter() } }
                        ),
                    s =>
                        JsonSerializer.Deserialize<ISecret>(
                            s,
                            new JsonSerializerOptions { Converters = { new SecretJsonConverter() } }
                        )
                );
            entity
                .Property(p => p.DLEQ)
                .HasConversion(
                    d =>
                        d == null
                            ? null
                            : JsonSerializer.Serialize(d, (JsonSerializerOptions)null!),
                    d =>
                        d == null
                            ? null
                            : JsonSerializer.Deserialize<DLEQProof>(d, (JsonSerializerOptions)null!)
                );
            entity
                .Property(p => p.P2PkE)
                .HasConversion(
                    e => e == null ? null : e.ToString(),
                    e => e == null ? null : new PubKey(e, false)
                );
        });

        modelBuilder.Entity<Mint>(entity =>
        {
            entity.HasIndex(m => m.Url).IsUnique();
        });

        modelBuilder.Entity<MintKeys>(entity =>
        {
            entity
                .Property(mk => mk.KeysetId)
                .HasConversion(kid => kid.ToString(), kid => new KeysetId(kid.ToString()))
                .Metadata.SetValueComparer(new ValueComparer<KeysetId>(
                    (k1, k2) => k1 == k2,
                    k => k.GetHashCode(),
                    k => k
                ));
            
            entity.HasKey(mk => new { mk.MintId, mk.KeysetId });

            entity.HasIndex(mk => mk.MintId);
            
            entity.HasIndex(mk => mk.KeysetId).IsUnique();

            entity.HasOne(mk => mk.Mint).WithMany(m => m.Keysets).HasForeignKey(mk => mk.MintId);

            var keysetJsonOptions = new JsonSerializerOptions { Converters = { new KeysetJsonConverter() } };
            entity
                .Property(mk => mk.Keyset)
                .HasConversion(
                    ks => JsonSerializer.Serialize(ks, keysetJsonOptions),
                    ks => JsonSerializer.Deserialize<Keyset>(ks, keysetJsonOptions)
                )
                .Metadata.SetValueComparer(new ValueComparer<Keyset>(
                    (k1, k2) => JsonSerializer.Serialize(k1, keysetJsonOptions) == JsonSerializer.Serialize(k2, keysetJsonOptions),
                    k => JsonSerializer.Serialize(k, keysetJsonOptions).GetHashCode(),
                    k => JsonSerializer.Deserialize<Keyset>(JsonSerializer.Serialize(k, keysetJsonOptions), keysetJsonOptions)!
                ));
        });

        modelBuilder.Entity<FailedTransaction>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.InvoiceId);
            entity.OwnsOne(t => t.MeltDetails);
            
            
            entity
                .Property(t => t.OutputData)
                .HasConversion(
                    od => JsonSerializer.Serialize(od, outputDataJsonOptions),
                    json => JsonSerializer.Deserialize<List<DotNut.Abstractions.OutputData>>(json, outputDataJsonOptions) ?? new List<DotNut.Abstractions.OutputData>()
                )
                .Metadata.SetValueComparer(new ValueComparer<List<DotNut.Abstractions.OutputData>>(
                    (l1, l2) => JsonSerializer.Serialize(l1, outputDataJsonOptions) == JsonSerializer.Serialize(l2, outputDataJsonOptions),
                    l => JsonSerializer.Serialize(l, outputDataJsonOptions).GetHashCode(),
                    l => JsonSerializer.Deserialize<List<DotNut.Abstractions.OutputData>>(JsonSerializer.Serialize(l, outputDataJsonOptions), outputDataJsonOptions) ?? new List<DotNut.Abstractions.OutputData>()
                ));
        });

        modelBuilder.Entity<ExportedToken>(entity =>
        {
            entity
                .HasMany(et => et.Proofs)
                .WithOne()
                .HasForeignKey(sp => sp.ExportedTokenId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CashuWalletConfig>(entity =>
        {
            entity
                .Property(cwc => cwc.WalletMnemonic)
                .HasConversion(m => m.ToString(), wm => new Mnemonic(wm, Wordlist.English));

            entity.HasKey(cwc => cwc.StoreId);
        });

        modelBuilder.Entity<StoreKeysetCounter>(entity =>
        {
            entity
                .Property(skc => skc.KeysetId)
                .HasConversion(ki => ki.ToString(), ki => new KeysetId(ki.ToString()));

            entity.HasKey(skc => new { skc.StoreId, skc.KeysetId });
        });

        modelBuilder.Entity<CashuLightningClientInvoice>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.HasIndex(i => i.QuoteId);
            entity.HasIndex(i => i.InvoiceId);
            entity.HasIndex(i => i.StoreId);
            entity.HasIndex(i => new { i.Mint, i.QuoteState });
            entity
                .Property(i => i.Amount)
                .HasConversion(
                    a => a.MilliSatoshi,
                    v => new LightMoney(v, LightMoneyUnit.MilliSatoshi)
                    );
            entity
                .Property(i => i.KeysetId)
                .HasConversion(
                    kid => kid.ToString(),
                    v => new KeysetId(v)
                );
            entity
                .Property(i => i.OutputData)
                .HasConversion(
                    od => JsonSerializer.Serialize(od, outputDataJsonOptions),
                    json =>
                        JsonSerializer.Deserialize<List<DotNut.Abstractions.OutputData>>(json, outputDataJsonOptions)
                        ?? new List<DotNut.Abstractions.OutputData>()
                );
        });
    }
}
