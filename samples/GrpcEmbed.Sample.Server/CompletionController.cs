using Microsoft.AspNetCore.Mvc;

namespace GrpcEmbed.Sample.Server;

[ApiController]
[Route("api/completion")]
public sealed class CompletionController : ControllerBase
{
    [HttpPost("task")]
    public async Task Complete(CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken);
    }

    [HttpPost("value-task")]
    public ValueTask CompleteValue() => ValueTask.CompletedTask;

    [HttpPost("no-content")]
    public Task<IActionResult> CompleteResult() => Task.FromResult<IActionResult>(NoContent());
}
