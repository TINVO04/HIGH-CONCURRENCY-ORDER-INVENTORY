using Microsoft.AspNetCore.Mvc;
using OrderService.Application;

namespace OrderService.Api.Controllers;

[ApiController]
[Route("api/orders")]
[Produces("application/json")]
public sealed class OrdersController(IOrderService orders) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create(
        [FromBody] CreateOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new ErrorResponse("VALIDATION_ERROR", "The order request is invalid.", HttpContext.TraceIdentifier, []));
        }
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return BadRequest(new ErrorResponse("MISSING_IDEMPOTENCY_KEY", "The Idempotency-Key header is required.", HttpContext.TraceIdentifier, []));
        }
        var key = idempotencyKey.Trim();
        if (key.Length > 128) return BadRequest(new ErrorResponse("VALIDATION_ERROR", "The Idempotency-Key is too long.", HttpContext.TraceIdentifier, []));
        if (request.CustomerId == Guid.Empty || request.Items is null || request.Items.Count == 0 || request.Items.Any(x => x.ProductId == Guid.Empty || x.Quantity <= 0) || request.Items.GroupBy(x => x.ProductId).Any(x => x.Count() > 1))
        {
            return BadRequest(new ErrorResponse("VALIDATION_ERROR", "The order request is invalid.", HttpContext.TraceIdentifier, []));
        }
        var result = await orders.CreateAsync(request, key, "/api/orders", HttpContext.TraceIdentifier, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/confirm")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await orders.ConfirmAsync(id, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await orders.CancelAsync(id, cancellationToken));

    private IActionResult ToActionResult(OperationResult<OrderResponse> result)
    {
        if (result.Error is not null)
        {
            var error = string.IsNullOrEmpty(result.Error.TraceId)
                ? result.Error with { TraceId = HttpContext.TraceIdentifier }
                : result.Error;
            return StatusCode(result.StatusCode, error);
        }

        return result.StatusCode == StatusCodes.Status201Created && result.Location is not null
            ? Created(result.Location, result.Value)
            : StatusCode(result.StatusCode, result.Value);
    }
}
