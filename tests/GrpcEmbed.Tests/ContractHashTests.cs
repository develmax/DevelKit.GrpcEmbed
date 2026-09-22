using System.Reflection;
using System.Reflection.Emit;
using GrpcEmbed.AspNetCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrpcEmbed.Tests;

public sealed class ContractHashTests : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Routing_mode_and_prefix_are_part_of_the_contract_hash()
    {
        var methods = _factory.Services.GetRequiredService<RuntimeRegistry>().Get(_factory.Services);
        var native = SchemaGenerator.Generate(methods).Sha256;
        var rest = SchemaGenerator.Generate(methods, true, new GrpcEmbedRoutingOptions { Mode = GrpcEmbedRoutingMode.Rest }).Sha256;
        var first = SchemaGenerator.Generate(methods, true, new GrpcEmbedRoutingOptions { Mode = GrpcEmbedRoutingMode.Method, Prefix = "grpc" }).Sha256;
        var second = SchemaGenerator.Generate(methods, true, new GrpcEmbedRoutingOptions { Mode = GrpcEmbedRoutingMode.Method, Prefix = "rpc" }).Sha256;
        Assert.NotEqual(native, rest);
        Assert.NotEqual(first, second);
    }
    private readonly WebApplicationFactory<Program> _factory;
    public ContractHashTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void Disabled_hashing_omits_schema_and_operation_hashes()
    {
        var methods = _factory.Services.GetRequiredService<RuntimeRegistry>().Get(_factory.Services);
        Assert.Empty(SchemaGenerator.Generate(methods, false).Sha256);
        var manifest = SchemaManifestManager.Create(methods, false);
        Assert.Null(manifest.SchemaHash);
        Assert.All(manifest.Services!.Values.SelectMany(service => service.Methods.Values), method =>
        {
            Assert.Null(method.RequestShapeHash);
            Assert.Null(method.ResponseShapeHash);
        });
    }

    [Fact]
    public void Hash_ignores_discovery_order_and_assembly_version_but_tracks_field_changes()
    {
        var methods = _factory.Services.GetRequiredService<RuntimeRegistry>().Get(_factory.Services);
        Assert.Equal(SchemaGenerator.Generate(methods).Sha256,
            SchemaGenerator.Generate(methods.Reverse().ToArray()).Sha256);
        var first = Model(1, false, false);
        var second = Model(2, true, false);
        var changed = Model(3, false, true);
        var template = methods[0];
        string Hash(Type type) => SchemaGenerator.Generate(new[]
        {
            template with { RequestType = type, ResponseType = type },
        }).Sha256;
        Assert.Equal(Hash(first), Hash(second));
        Assert.Equal(GrpcEmbedContractShape.Hash(first), GrpcEmbedContractShape.Hash(second));
        Assert.NotEqual(Hash(first), Hash(changed));
        Assert.NotEqual(GrpcEmbedContractShape.Hash(first), GrpcEmbedContractShape.Hash(changed));
    }

    private static Type Model(int version, bool reverse, bool changed)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("ContractVersionTest") { Version = new Version(version, 0, 0, 0) },
            AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Contracts").DefineType("StableDto", TypeAttributes.Public);
        var fields = new[] { ("Id", typeof(int)), ("Name", changed ? typeof(int) : typeof(string)) };
        foreach (var (name, fieldType) in reverse ? fields.Reverse() : fields)
        {
            var field = type.DefineField("_" + name, fieldType, FieldAttributes.Private);
            var property = type.DefineProperty(name, PropertyAttributes.None, fieldType, null);
            var getter = type.DefineMethod("get_" + name, MethodAttributes.Public | MethodAttributes.SpecialName, fieldType, Type.EmptyTypes);
            var il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            var setter = type.DefineMethod("set_" + name, MethodAttributes.Public | MethodAttributes.SpecialName, null, new[] { fieldType });
            il = setter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            property.SetGetMethod(getter); property.SetSetMethod(setter);
        }
        return type.CreateType()!;
    }
}
