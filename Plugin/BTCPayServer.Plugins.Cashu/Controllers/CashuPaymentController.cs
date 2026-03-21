#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Errors;
using BTCPayServer.Plugins.Cashu.PaymentHandlers;
using DotNut;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Cashu.Controllers;

[EnableCors(CorsPolicies.All)]
[IgnoreAntiforgeryToken]
public class CashuPaymentController : Controller
{
    public CashuPaymentController(CashuPaymentService cashuPaymentService)
    {
        _cashuPaymentService = cashuPaymentService;
    }

    private readonly CashuPaymentService _cashuPaymentService;

    /// <summary>
    /// Api endpoint for Paying Invoice via Post Request, by sending all the data exclusively.
    /// </summary>
    /// <param name="token">V4 encoded Cashu Token</param>
    /// <param name="invoiceId"></param>
    /// <returns></returns>
    /// <exception cref="CashuPaymentException"></exception>
    [AllowAnonymous]
    [HttpPost("~/cashu/pay-invoice")]
    public async Task<IActionResult> PayByToken(string token, string invoiceId)
    {
        try
        {
            if (!CashuUtils.TryDecodeToken(token, out var decodedToken))
            {
                throw new CashuPaymentException("Invalid token");
            }
            await _cashuPaymentService.ProcessPaymentAsync(decodedToken, invoiceId);
        }
        catch (CashuPaymentException cex)
        {
            return BadRequest($"Payment Error: {cex.Message} ");
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }

        return Redirect(
            Url.ActionAbsolute(
                this.Request,
                nameof(UIInvoiceController.Checkout),
                "UIInvoice",
                new { invoiceId = invoiceId }
            ).AbsoluteUri
        );
    }

    /// <summary>
    /// Api endpoint for Paying Invoice via Post Request, by sending nut19 payment payload.
    /// </summary>
    /// <param name="payload"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    [AllowAnonymous]
    [HttpPost("~/cashu/pay-invoice-pr")]
    public async Task<ActionResult> PayByPaymentRequest()
    {
        try
        {
            // FIXME: idk why but i couldn't make it work with [FromBody].
            var body = await new StreamReader(Request.Body).ReadToEndAsync();
            var payload = JsonSerializer.Deserialize<PaymentRequestPayload>(
                body,
                JsonSerializerOptions.Default
            );
            if (
                payload.PaymentId == null
                || payload.Mint == null
                || payload.Unit == null
                || payload.Proofs == null
            )
            {
                throw new ArgumentException("Required fields are missing in the payload.");
            }

            var token = new CashuToken
            {
                Tokens =
                [
                    new CashuToken.Token { Mint = payload.Mint, Proofs = payload.Proofs.ToList() },
                ],
                Memo = payload.Memo,
                Unit = payload.Unit,
            };

            await _cashuPaymentService.ProcessPaymentAsync(token, payload.PaymentId);
            return Ok("Payment sent!");
        }
        catch (CashuPaymentException cex)
        {
            return BadRequest($"Payment Error: {cex.Message}");
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
