using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;

namespace GrpcEmbed.Sample.Server;

[ApiController]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    public static int InvocationCount;
    public static int FilterCount;
    public static int AuthorizationFilterCount;
    public static int ResourceFilterBeforeCount;
    public static int ResourceFilterAfterCount;
    public static int ResourceShortCircuitActionCount;
    [HttpGet("{id:int}")]
    [CountAction]
    public Task<UserDto> Get(int id, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref InvocationCount);
        return Task.FromResult(new UserDto { Id = id, Name = "Test" });
    }

    [HttpPost]
    public Task<UserDto> Create(CreateUserRequest request) => Task.FromResult(new UserDto { Id = 43, Name = request.Name });

    [HttpGet("count/{id:int}")]
    public Task<int> Count(int id) => Task.FromResult(id + 1);

    [HttpGet("validated-scalar/{value:int}")]
    public Task<int> ValidatedScalar([Range(1, 10)] int value) => Task.FromResult(value);

    [HttpGet("versioned/{id:int}")]
    public Task<VersionedDto> Versioned(int id) => Task.FromResult(new VersionedDto { RenamedId = id });

    [HttpGet("state/{id:int}")]
    public Task<UserStateDto> State(int id) => Task.FromResult(new UserStateDto { Id = id, State = UserState.Active });

    [HttpGet("label/{id:int}")]
    public Task<string> Label(int id) => Task.FromResult($"user-{id}");

    [HttpGet("list/{count:int}")]
    public Task<List<UserDto>> List(int count) => Task.FromResult(Enumerable.Range(1, count).Select(id => new UserDto { Id = id, Name = $"User {id}" }).ToList());

    [HttpGet("context/{id:int}")]
    public Task<string> Context(int id, HttpContext context) =>
        Task.FromResult($"{context.User.Identity?.Name}|{context.Request.Path}|{id}");

    [HttpGet("value-task/{id:int}")]
    public ValueTask<UserDto> ValueTaskGet(int id) => ValueTask.FromResult(new UserDto { Id = id, Name = "ValueTask" });

    [HttpGet("authorization-filter/{id:int}")]
    [HeaderAuthorization]
    public Task<UserDto> AuthorizationFiltered(int id) => Task.FromResult(new UserDto { Id = id, Name = "Authorized by filter" });

    [HttpGet("resource-filter/{id:int}")]
    [ResourceProbe]
    public Task<UserDto> ResourceFiltered(int id) => Task.FromResult(new UserDto { Id = id, Name = "Resource pipeline" });

    [HttpGet("resource-short-circuit/{id:int}")]
    [ResourceShortCircuit]
    public Task<UserDto> ResourceShortCircuited(int id)
    {
        Interlocked.Increment(ref ResourceShortCircuitActionCount);
        return Task.FromResult(new UserDto { Id = id, Name = "Action should not run" });
    }

    [HttpPost("upload")]
    public Task<UserDto> Upload(IFormFile file) => Task.FromResult(new UserDto { Name = file.FileName });

    [HttpGet("missing/{id:int}")]
    public ActionResult<UserDto> Missing(int id) => NotFound(new { id });

    [HttpGet("secure/{id:int}")]
    [Authorize]
    public Task<UserDto> Secure(int id) => Task.FromResult(new UserDto { Id = id, Name = User.Identity!.Name! });

    [HttpGet("named/{id:int}")]
    [GrpcName("Find")]
    [GrpcExport]
    public Task<UserDto> Named(int id) => Task.FromResult(new UserDto { Id = id, Name = "Named" });

    [HttpGet("delay/{id:int}")]
    public async Task<UserDto> Delay(int id, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        return new UserDto { Id = id, Name = "Delayed" };
    }

    [HttpGet("hidden/{id:int}")]
    [GrpcIgnore]
    public Task<UserDto> Hidden(int id) => Task.FromResult(new UserDto { Id = id, Name = "Hidden" });
}

public sealed class UserDto { public int Id { get; set; } public string Name { get; set; } = ""; }
public sealed class CreateUserRequest { [Required] public string Name { get; set; } = ""; }
[GrpcReservedField(7, 8)]
public sealed class VersionedDto { [GrpcFieldNumber(10)] public int RenamedId { get; set; } }
public enum UserState { Unknown = 0, Active = 1, Suspended = 2 }
public sealed class UserStateDto { public int Id { get; set; } public UserState State { get; set; } }

public sealed class CountActionAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context) => Interlocked.Increment(ref UsersController.FilterCount);
}

public sealed class HeaderAuthorizationAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        Interlocked.Increment(ref UsersController.AuthorizationFilterCount);
        if (!context.HttpContext.Request.Headers.ContainsKey("x-filter-auth")) context.Result = new UnauthorizedResult();
    }
}

public sealed class ResourceProbeAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        Interlocked.Increment(ref UsersController.ResourceFilterBeforeCount);
        await next();
        Interlocked.Increment(ref UsersController.ResourceFilterAfterCount);
    }
}

public sealed class ResourceShortCircuitAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context) =>
        context.Result = new ObjectResult(new UserDto { Id = 900, Name = "Resource short circuit" });
    public void OnResourceExecuted(ResourceExecutedContext context) { }
}

[ApiController]
[Route("api/internal")]
[GrpcIgnore]
public sealed class InternalController : ControllerBase
{
    [HttpGet] public UserDto Get() => new();
}
