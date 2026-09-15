using GrpcEmbed.Client;
using GrpcEmbed;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddGrpcEmbedClient<IUsersApi>(options => { options.Address = new Uri(args.FirstOrDefault() ?? "http://localhost:5001"); options.DefaultTimeout = TimeSpan.FromSeconds(10); options.TransportMode = GrpcEmbedTransportMode.GrpcOnly; });
await using var provider = services.BuildServiceProvider();
var user = await provider.GetRequiredService<IUsersApi>().Get(42);
Console.WriteLine($"{user.Id}: {user.Name}");

public interface IUsersApi { Task<UserDto> Get(int id, CancellationToken cancellationToken = default); }
public sealed class UserDto { public int Id { get; set; } public string Name { get; set; } = ""; }
