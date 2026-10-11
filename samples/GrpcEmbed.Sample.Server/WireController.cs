using Microsoft.AspNetCore.Mvc;

namespace GrpcEmbed.Sample.Server;

/// <summary>Demonstrates special CLR types interoperating with standard Protobuf clients.</summary>
[ApiController, GrpcExport, GrpcName("Wire")]
[Route("api/wire")]
public sealed class WireController : ControllerBase
{
    [HttpPost("echo")]
    public Task<WireFixture> Echo([FromBody] WireFixture request) => Task.FromResult(request);
}

public sealed class WireFixture
{
    [GrpcFieldNumber(1)] public Guid Id { get; set; }
    [GrpcFieldNumber(2)] public Guid? OptionalId { get; set; }
    [GrpcFieldNumber(3)] public decimal Amount { get; set; }
    [GrpcFieldNumber(4)] public DateTime At { get; set; }
    [GrpcFieldNumber(5)] public TimeSpan Elapsed { get; set; }
    [GrpcFieldNumber(6)] public DateTimeOffset OffsetAt { get; set; }
    [GrpcFieldNumber(7)] public List<WireState?> States { get; set; } = new();
    [GrpcFieldNumber(8)] public List<int?> Values { get; set; } = new();
    [GrpcFieldNumber(9)] public List<Guid?> Ids { get; set; } = new();
}

public enum WireState { Unknown, Active }
