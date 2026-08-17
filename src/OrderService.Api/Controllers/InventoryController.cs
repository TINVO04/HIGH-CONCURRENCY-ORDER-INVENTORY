using Microsoft.AspNetCore.Mvc;
using OrderService.Application;

namespace OrderService.Api.Controllers;

[ApiController]
[Route("api/inventory")]
public sealed class InventoryController(IInventoryService inventory) : ControllerBase
{
    [HttpGet("{productId:guid}")]
    public async Task<IActionResult> Get(Guid productId, CancellationToken cancellationToken)
    {
        var result = await inventory.GetAsync(productId, cancellationToken);
        return result is null
            ? NotFound(new ErrorResponse("INVENTORY_NOT_FOUND", "The inventory row was not found.", HttpContext.TraceIdentifier, []))
            : Ok(result);
    }
}
