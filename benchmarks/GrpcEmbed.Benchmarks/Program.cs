using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ProtoBuf;
using System.Text.Json;
using System.Buffers;
using GrpcEmbed;
using GrpcEmbed.AspNetCore;
using GrpcEmbed.Client;
using GrpcEmbed.Sample.Server;
using GrpcEmbed.Benchmarks.Generated;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;

BenchmarkSwitcher.FromAssembly(typeof(SerializationBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
[ShortRunJob]
public class SerializationBenchmarks
{
    private readonly BenchmarkDto _value = new() { Id = 42, Name = new string('x', 180) };
    private readonly ArrayBufferWriter<byte> _jsonBuffer = new(256);
    private readonly ArrayBufferWriter<byte> _protobufBuffer = new(256);
    private readonly Utf8JsonWriter _jsonWriter;
    public SerializationBenchmarks() { _jsonWriter = new Utf8JsonWriter(_jsonBuffer); GrpcEmbed.GrpcEmbedRuntimeModel.Configure(typeof(BenchmarkDto)); }

    [Benchmark(Baseline = true)] public int SystemTextJson()
    {
        _jsonBuffer.Clear(); _jsonWriter.Reset(_jsonBuffer); JsonSerializer.Serialize(_jsonWriter, _value); _jsonWriter.Flush(); return _jsonBuffer.WrittenCount;
    }
    [Benchmark] public int GrpcEmbedProtobuf()
    {
        _protobufBuffer.Clear(); GrpcEmbed.GrpcEmbedRuntimeModel.Model.Serialize(_protobufBuffer, _value); return _protobufBuffer.WrittenCount;
    }
}

public sealed class BenchmarkDto { public int Id { get; set; } public string Name { get; set; } = ""; }

[MemoryDiagnoser]
[ShortRunJob]
public class TransportBenchmarks
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private GrpcChannel _channel = null!;
    private Users.UsersClient _generated = null!;
    private ServiceProvider _clientServices = null!;
    private IBenchmarkUsersApi _contract = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(UsersController).Assembly);
        builder.Services.AddGrpcEmbed();
        _app = builder.Build();
        _app.MapControllers();
        _app.MapGrpcEmbed();
        await _app.StartAsync();
        _http = _app.GetTestClient();
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _app.GetTestServer().CreateHandler() });
        _generated = new Users.UsersClient(_channel);
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IBenchmarkUsersApi>(options => { options.Address = new Uri("http://localhost"); options.HttpHandler = _app.GetTestServer().CreateHandler(); });
        _clientServices = services.BuildServiceProvider();
        _contract = _clientServices.GetRequiredService<IBenchmarkUsersApi>();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> MvcJson() => (await _http.GetFromJsonAsync<GrpcEmbed.Sample.Server.UserDto>("/api/users/42"))!.Id;

    [Benchmark]
    public async Task<int> GrpcEmbedProxy() => (await _contract.Get(42)).Id;

    [Benchmark]
    public async Task<int> GeneratedGrpc() => (await _generated.GetAsync(new Users_Get_Request { Id = 42 })).Id;

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _clientServices.DisposeAsync();
        _channel.Dispose();
        _http.Dispose();
        await _app.DisposeAsync();
    }

    [GrpcName("Users")]
    public interface IBenchmarkUsersApi
    {
        [GrpcName("Get")]
        Task<GrpcEmbed.Sample.Server.UserDto> Get(int id, CancellationToken cancellationToken = default);
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class PayloadTransportBenchmarks
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private GrpcChannel _channel = null!;
    private Echo.EchoClient _generatedEmbed = null!;
    private NativeEcho.NativeEchoClient _native = null!;
    private ServiceProvider _clientServices = null!;
    private IBenchmarkEchoApi _contract = null!;
    private PayloadDto _payload = null!;
    private Echo_Send_Request _wireRequest = null!;

    [Params(32, 200, 2048, 20480)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(EchoController).Assembly);
        builder.Services.AddGrpcEmbed();
        _app = builder.Build();
        _app.MapControllers();
        _app.MapGrpcEmbed();
        _app.MapGrpcService<NativeEchoService>();
        await _app.StartAsync();
        _http = _app.GetTestClient();
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _app.GetTestServer().CreateHandler() });
        _generatedEmbed = new Echo.EchoClient(_channel);
        _native = new NativeEcho.NativeEchoClient(_channel);
        var services = new ServiceCollection();
        services.AddGrpcEmbedClient<IBenchmarkEchoApi>(options => { options.Address = new Uri("http://localhost"); options.HttpHandler = _app.GetTestServer().CreateHandler(); });
        _clientServices = services.BuildServiceProvider();
        _contract = _clientServices.GetRequiredService<IBenchmarkEchoApi>();
        _payload = new PayloadDto { Value = new string('x', PayloadBytes) };
        _wireRequest = new Echo_Send_Request { Request = new GrpcEmbed.Benchmarks.Generated.PayloadDto { Value = _payload.Value } };
    }

    [Benchmark(Baseline = true)]
    public async Task<int> MvcJson() => (await (await _http.PostAsJsonAsync("/api/echo", _payload)).Content.ReadFromJsonAsync<PayloadDto>())!.Value.Length;

    [Benchmark]
    public async Task<int> NativeGrpc() => (await _native.SendAsync(_wireRequest)).Value.Length;

    [Benchmark]
    public async Task<int> GrpcEmbedGeneratedClient() => (await _generatedEmbed.SendAsync(_wireRequest)).Value.Length;

    [Benchmark]
    public async Task<int> GrpcEmbedContractProxy() => (await _contract.Send(_payload)).Value.Length;

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _clientServices.DisposeAsync();
        _channel.Dispose();
        _http.Dispose();
        await _app.DisposeAsync();
    }

    [GrpcName("Echo")]
    public interface IBenchmarkEchoApi
    {
        Task<PayloadDto> Send(PayloadDto request, CancellationToken cancellationToken = default);
    }
}

[ApiController]
[Route("api/echo")]
[GrpcName("Echo")]
public sealed class EchoController : ControllerBase
{
    [HttpPost]
    [GrpcName("Send")]
    public Task<PayloadDto> Send(PayloadDto request) => Task.FromResult(request);
}

public sealed class PayloadDto { public string Value { get; set; } = ""; }

public sealed class NativeEchoService : NativeEcho.NativeEchoBase
{
    public override Task<GrpcEmbed.Benchmarks.Generated.PayloadDto> Send(Echo_Send_Request request, ServerCallContext context) => Task.FromResult(request.Request);
}
