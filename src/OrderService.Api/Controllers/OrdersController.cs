using Microsoft.AspNetCore.Mvc;
using OrderService.Application;

namespace OrderService.Api.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(IOrderService orders) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateOrderRequest request, CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var header) || string.IsNullOrWhiteSpace(header))
        {
            return BadRequest(new ErrorResponse("MISSING_IDEMPOTENCY_KEY", "The Idempotency-Key header is required.", HttpContext.TraceIdentifier, []));
        }
        var key = header.ToString().Trim();
        if (key.Length > 128) return BadRequest(new ErrorResponse("VALIDATION_ERROR", "The Idempotency-Key is too long.", HttpContext.TraceIdentifier, []));
        if (request.CustomerId == Guid.Empty || request.Items is null || request.Items.Count == 0 || request.Items.Any(x => x.ProductId == Guid.Empty || x.Quantity <= 0) || request.Items.GroupBy(x => x.ProductId).Any(x => x.Count() > 1))
        {
            return BadRequest(new ErrorResponse("VALIDATION_ERROR", "The order request is invalid.", HttpContext.TraceIdentifier, []));
        }
        var result = await orders.CreateAsync(request, key, "/api/orders", cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await orders.ConfirmAsync(id, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await orders.CancelAsync(id, cancellationToken));

    private IActionResult ToActionResult(OperationResult<OrderResponse> result)
        => result.Error is not null
            ? StatusCode(result.StatusCode, result.Error with { TraceId = HttpContext.TraceIdentifier })
            : StatusCode(result.StatusCode, result.Value);
}
