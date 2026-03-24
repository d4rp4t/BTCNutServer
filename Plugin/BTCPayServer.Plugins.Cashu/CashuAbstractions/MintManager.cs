using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Cashu.CashuAbstractions;

public class MintManager
{
    private readonly CashuDbContextFactory _dbContextFactory;

    public MintManager(CashuDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<Mint> GetOrCreateMint(string mintUrl)
    {
        mintUrl = NormalizeMintUrl(mintUrl);
        await using var db = _dbContextFactory.CreateContext();

        var mint = await db.Mints.FirstOrDefaultAsync(m => m.Url == mintUrl);
        if (mint != null)
        {
            return mint;
        }

        mint = new Mint(mintUrl);
        db.Mints.Add(mint);

        try
        {
            await db.SaveChangesAsync();
            return mint;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // race condition: another thread created this mint
            var existingMint = await db.Mints.AsNoTracking().FirstOrDefaultAsync(m => m.Url == mintUrl);
            if (existingMint != null)
            {
                return existingMint;
            }
            throw; // should not happen, but rethrow if it does
        }
    }

    public async Task SaveKeyset(string mintUrl, KeysetId keysetId, Keyset keyset, string unit)
    {
        mintUrl = NormalizeMintUrl(mintUrl);
        await using var db = _dbContextFactory.CreateContext();

        var mint = await db.Mints.FirstOrDefaultAsync(m => m.Url == mintUrl);
        if (mint == null)
        {
            mint = new Mint(mintUrl);
            db.Mints.Add(mint);
            await db.SaveChangesAsync();
        }

        var existingEntry = await db.MintKeys.FirstOrDefaultAsync(mk =>
            mk.MintId == mint.Id && mk.KeysetId == keysetId
        );

        if (existingEntry != null)
        {
            return;
        }

        var keysetInOtherMint = await db.MintKeys
            .Include(mk => mk.Mint)
            .FirstOrDefaultAsync(mk => mk.KeysetId == keysetId && mk.MintId != mint.Id);

        if (keysetInOtherMint != null)
        {
            throw new InvalidOperationException(
                $"KeysetId {keysetId} already exists in another mint."
            );
        }

        var newMintKey = new MintKeys
        {
            MintId = mint.Id,
            Mint = mint,
            KeysetId = keysetId,
            Unit = unit,
            Keyset = keyset,
        };

        db.MintKeys.Add(newMintKey);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            var conflicting = await db.MintKeys
                .Include(mk => mk.Mint)
                .AsNoTracking()
                .FirstOrDefaultAsync(mk => mk.KeysetId == keysetId);

            if (conflicting != null && conflicting.MintId != mint.Id)
            {
                throw new InvalidOperationException(
                    $"SECURITY: Race condition detected - KeysetId {keysetId} was just added by mint " +
                    $"({conflicting.Mint.Url}) while we tried to add it for mint ({mintUrl}). " +
                    $"Refusing to proceed."
                );
            }
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // unique constraint violation
        return ex.InnerException?.Message?.Contains("duplicate key") == true ||
               ex.InnerException?.Message?.Contains("unique constraint") == true ||
               ex.InnerException?.Message?.Contains("UNIQUE") == true;
    }

    public async Task<(string MintUrl, string Unit)?> GetKeysetInfo(KeysetId keysetId)
    {
        await using var db = _dbContextFactory.CreateContext();

        var mintKey = await db.MintKeys
            .Include(mk => mk.Mint)
            .FirstOrDefaultAsync(mk => mk.KeysetId == keysetId);

        if (mintKey == null)
        {
            return null;
        }

        return (mintKey.Mint.Url, mintKey.Unit);
    }

    public async Task<Dictionary<string, (string MintUrl, string Unit)>> MapKeysetIdsToMints(IEnumerable<KeysetId> keysetIds)
    {
        await using var db = _dbContextFactory.CreateContext();

        var keysetIdList = keysetIds.ToList();

        var mintKeysets = await db.MintKeys
            .Include(mk => mk.Mint)
            .Where(mk => keysetIdList.Contains(mk.KeysetId))
            .ToListAsync();

        return mintKeysets.ToDictionary(
            mk => mk.KeysetId.ToString(),
            mk => (MintUrl: mk.Mint.Url, Unit: mk.Unit)
        );
    }

    public async Task<bool> MintExists(string mintUrl)
    {
        mintUrl = NormalizeMintUrl(mintUrl);
        await using var db = _dbContextFactory.CreateContext();
        return await db.Mints.AnyAsync(m => m.Url == mintUrl);
    }

    /// <summary>
    /// Checks if any of the provided keyset IDs are already assigned to a different mint in the DB.
    /// Should be called BEFORE melt/swap to avoid losing funds on conflict.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when a keyset ID conflict is detected</exception>
    public async Task ValidateKeysetOwnership(string mintUrl, IEnumerable<KeysetId> keysetIds)
    {
        mintUrl = NormalizeMintUrl(mintUrl);
        await using var db = _dbContextFactory.CreateContext();

        var mint = await db.Mints.FirstOrDefaultAsync(m => m.Url == mintUrl);
        if (mint == null)
        {
            // Mint not in DB yet, no conflicts possible
            return;
        }

        var ids = keysetIds.Distinct().ToList();

        var conflicting = await db.MintKeys
            .Include(mk => mk.Mint)
            .Where(mk => ids.Contains(mk.KeysetId) && mk.MintId != mint.Id)
            .FirstOrDefaultAsync();

        if (conflicting != null)
        {
            throw new InvalidOperationException(
                $"KeysetId {conflicting.KeysetId} belongs to mint ({conflicting.Mint.Url}), " +
                $"but token claims to be from ({mintUrl}). Refusing to process."
            );
        }
    }

    public static string NormalizeMintUrl(string mintUrl)
    {
        var uri = new Uri(mintUrl);
        return uri.AbsoluteUri.TrimEnd('/');
    }
}