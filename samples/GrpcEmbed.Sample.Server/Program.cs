using GrpcEmbed.AspNetCore;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddAuthentication("sample").AddScheme<AuthenticationSchemeOptions, SampleAuthenticationHandler>("sample", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddGrpcEmbed(options => { options.EnableSchemaEndpoint = true; options.EnableReflection = true; });
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGrpcEmbed();
app.Run();

public partial class Program { }
