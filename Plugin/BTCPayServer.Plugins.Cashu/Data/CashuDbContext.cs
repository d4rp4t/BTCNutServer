using System;
using System.Linq;
using System.Text.Json;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using DotNut.JsonConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DefaultPluginSchema);

        modelBuilder.Entity<StoredProof>(entity =>
        {
            entity.HasKey(sk => sk.ProofId);
            entity.HasIndex(sk => sk.Id);
            entity.HasIndex(sk => sk.StoreId);
            entity.HasIndex(sk => sk.Amount);

            entity.Property(p => p.C)
                .HasConversion(
                    pk => pk.ToString(),
                    pk => new PubKey(pk, false)
                );
            entity.Property(p => p.Id)
                .HasConversion(
                    ki => ki.ToString(),
                    ki => new KeysetId(ki.ToString())
                );
            entity.Property(p => p.Secret)
                .HasConversion(
                    s => JsonSerializer.Serialize(s,
                        new JsonSerializerOptions { Converters = { new SecretJsonConverter() } }),
                    s => JsonSerializer.Deserialize<ISecret>(s,
                        new JsonSerializerOptions { Converters = { new SecretJsonConverter() } })
                );
            entity.Property(p => p.DLEQ)
                .HasConversion(
                    d => d == null ? null : JsonSerializer.Serialize(d, (JsonSerializerOptions)null!),
                    d => d == null ? null : JsonSerializer.Deserialize<DLEQProof>(d, (JsonSerializerOptions)null!)
                );
        });

        modelBuilder.Entity<MintKeys>(entity =>
        {
            entity.Property(mk => mk.KeysetId).HasConversion(
                kid => kid.ToString(),
                kid => new KeysetId(kid.ToString())
            );
            entity.HasKey(mk => new { mk.MintId, mk.KeysetId });

            entity.HasIndex(mk => mk.MintId);

            entity.HasOne(mk => mk.Mint)
                .WithMany(m => m.Keysets)
                .HasForeignKey(mk => mk.MintId);


            entity.Property(mk => mk.Keyset).HasConversion(
                ks => JsonSerializer.Serialize(ks,
                    new JsonSerializerOptions { Converters = { new KeysetJsonConverter() } }),
                ks => JsonSerializer.Deserialize<Keyset>(ks,
                    new JsonSerializerOptions { Converters = { new KeysetJsonConverter() } }));
        });

        
        modelBuilder.Entity<FailedTransaction>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.InvoiceId);
            entity.OwnsOne(t => t.MeltDetails);
            entity.OwnsOne(t => t.OutputData, fo =>
            {
                //Secrets conversion
                fo.Property(f => f.Secrets)
                    .HasConversion(
                        s => JsonSerializer.Serialize(s, new JsonSerializerOptions
                            { Converters = { new SecretJsonConverter() } }),
                        s => JsonSerializer.Deserialize<ISecret[]>(s, new JsonSerializerOptions
                            { Converters = { new SecretJsonConverter() } })
                    ).Metadata.SetValueComparer(
                        new ValueComparer<ISecret[]>(
                            (c1, c2) => c1.SequenceEqual(c2),
                            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                            c => c.ToArray()));

                //BlindingFactors conversion
                fo.Property(f => f.BlindingFactors)
                    .HasConversion(
                        bf => JsonSerializer.Serialize(bf, new JsonSerializerOptions
                            { Converters = { new PrivKeyJsonConverter() } }),
                        bf => JsonSerializer.Deserialize<PrivKey[]>(bf, new JsonSerializerOptions
                            { Converters = { new PrivKeyJsonConverter() } })
                    ).Metadata.SetValueComparer(
                        new ValueComparer<PrivKey[]>(
                            (c1, c2) => c1.SequenceEqual(c2),
                            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                            c => c.ToArray()));

                //BlindedMessages conversion
                fo.Property(f => f.BlindedMessages)
                    .HasConversion(
                        bm => JsonSerializer.Serialize(bm, (JsonSerializerOptions)null!),
                        bm => JsonSerializer.Deserialize<BlindedMessage[]>(bm, (JsonSerializerOptions)null!)
                    ).Metadata.SetValueComparer(
                        new ValueComparer<BlindedMessage[]>(
                            (c1, c2) => c1.SequenceEqual(c2),
                            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                            c => c.ToArray()));
            });
        });

    }
}

