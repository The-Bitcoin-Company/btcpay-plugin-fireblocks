using BTCPayServer.Plugins.Fireblocks.Flow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Fireblocks.Controllers;

[Area(Plugin.Area)]
[Route("stores/{storeId}/fireblocks/webhook")]
[AllowAnonymous]
public sealed class FlowWebhookController(IFlowWebhookService webhookService) : Controller
{
    public const int MaximumBodyBytes = 1024 * 1024;

    [HttpPost]
    [Consumes("application/json")]
    [IgnoreAntiforgeryToken] // Provider HMAC authentication replaces browser CSRF tokens.
    public async Task<IActionResult> Receive(string storeId, CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaximumBodyBytes)
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge);
        if (Request.Headers["x-dynamic-signature-256"].Count != 1)
            return Unauthorized();

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await Request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumBodyBytes)
                return StatusCode(StatusCodes.Status413RequestEntityTooLarge);
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        try
        {
            await webhookService.ProcessAsync(
                storeId,
                buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)),
                Request.Headers["x-dynamic-signature-256"][0],
                cancellationToken);
            return Ok();
        }
        catch (FlowWebhookException exception)
        {
            // Rejections are intentionally opaque and never reflect provider data.
            return StatusCode(exception.StatusCode);
        }
    }
}
