using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace GrpcEmbed.Sample.Server;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController : ControllerBase
{
    [HttpGet("{id:int}")]
    public Task<OrderDto> Get(int id, [FromQuery] bool includeItems = false) => Task.FromResult(new OrderDto
    {
        Id = id,
        Items = includeItems ? new[] { new OrderItemDto { Sku = "SKU-1", Quantity = 2 } } : Array.Empty<OrderItemDto>()
    });
}

[ApiController]
[Route("api/payments")]
public sealed class PaymentsController : ControllerBase
{
    [HttpPost]
    [Authorize]
    public Task<PaymentDto> Create([FromBody] CreatePaymentRequest request) =>
        Task.FromResult(new PaymentDto { Id = Guid.NewGuid(), Amount = request.Amount, CreatedAt = DateTimeOffset.UtcNow });
}

[ApiController]
[Route("api/files")]
public sealed class FilesController : ControllerBase
{
    [HttpGet("{name}")]
    [GrpcIgnore]
    public FileResult Download(string name) => File(Array.Empty<byte>(), "application/octet-stream", name);
}

[ApiController]
[Route("api/admin")]
[GrpcIgnore]
public sealed class AdminController : ControllerBase
{
    [HttpGet("health")]
    [AllowAnonymous]
    public object Health() => new { status = "ok" };
}

public sealed class OrderDto { public int Id { get; set; } public IReadOnlyList<OrderItemDto> Items { get; set; } = Array.Empty<OrderItemDto>(); }
public sealed class OrderItemDto { public string Sku { get; set; } = ""; public int Quantity { get; set; } }
public sealed class CreatePaymentRequest { [Range(typeof(decimal), "0.01", "1000000")] public decimal Amount { get; set; } }
public sealed class PaymentDto { public Guid Id { get; set; } public decimal Amount { get; set; } public DateTimeOffset CreatedAt { get; set; } }
