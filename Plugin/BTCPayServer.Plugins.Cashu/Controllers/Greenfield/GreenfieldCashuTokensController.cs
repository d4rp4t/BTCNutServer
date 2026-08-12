using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Cashu.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Cashu.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public class GreenfieldCashuTokensController(
    CashuDbContextFactory cashuDbContextFactory
) : ControllerBase
{
    [HttpGet("~/api/v1/stores/{storeId}/cashu/tokens")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetExportedTokens(string storeId)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        var tokens = await db.ExportedTokens
            .Where(t => t.StoreId == storeId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

        return Ok(tokens.Select(GreenfieldCashuWalletController.ToExportedTokenResponseDto));
    }

    [HttpGet("~/api/v1/stores/{storeId}/cashu/tokens/{tokenId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetExportedToken(string storeId, Guid tokenId)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        var token = await db.ExportedTokens
            .Include(t => t.Proofs)
            .SingleOrDefaultAsync(t => t.Id == tokenId && t.StoreId == storeId);

        if (token is null)
        {
            return this.CreateAPIError(404, "token-not-found", "The exported token was not found.");
        }
        return Ok(GreenfieldCashuWalletController.ToExportedTokenResponseDto(token));
    }
}
