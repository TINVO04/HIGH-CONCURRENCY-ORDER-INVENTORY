using Microsoft.AspNetCore.Mvc;
using OrderService.Application;

namespace OrderService.Api.Controllers;

[ApiController]
[Route("api/inventory")]
[Produces("application/json")]
public sealed class InventoryController(IInventoryService inventory) : ControllerBase
{
    [HttpGet("{productId:guid}")]
    [ProducesResponseType(typeof(InventoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(Guid productId, CancellationToken cancellationToken)
    {
        var result = await inventory.GetAsync(productId, cancellationToken);
        return result is null
            ? NotFound(new ErrorResponse("INVENTORY_NOT_FOUND", "The inventory row was not found.", HttpContext.TraceIdentifier, []))
            : Ok(result);
    }
}
