using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Grpc.Core;
using GrpcEmbed.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace GrpcEmbed.Tests;

public class ResultValidationPlanTests
{
    [Fact]
    public void Prepared_result_readers_preserve_values_nested_results_and_errors()
    {
        var options = new GrpcEmbedOptions();
        var value = new Payload { Name = "synthetic" };
        MvcResultReaders.Prepare(typeof(Task<ActionResult<Payload>>));
        Assert.Same(value, GrpcEmbedService.UnwrapActionResult(new ActionResult<Payload>(value), options));
        Assert.Same(value, GrpcEmbedService.UnwrapActionResult(new ActionResult<Payload>(new OkObjectResult(value)), options));
        Assert.Same(value, GrpcEmbedService.UnwrapActionResult(new ObjectResult(new ActionResult<Payload>(value)), options));
        Assert.Equal(42, GrpcEmbedService.UnwrapActionResult(new ActionResult<int>(42), options));
        Assert.Null(GrpcEmbedService.UnwrapActionResult(new ActionResult<Payload>((Payload)null!), options));
        Assert.Null(GrpcEmbedService.UnwrapActionResult(new ActionResult<Payload>(new NoContentResult()), options));
        var failure = Assert.Throws<RpcException>(() => GrpcEmbedService.UnwrapActionResult(
            new ActionResult<Payload>(new BadRequestObjectResult("invalid")), options));
        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);
        Assert.Equal(StatusCode.Unimplemented, Assert.Throws<RpcException>(() =>
            GrpcEmbedService.UnwrapActionResult(new FileContentResult(Array.Empty<byte>(), "text/plain"), options)).StatusCode);
        Assert.Same(MvcResultReaders.Get(typeof(ActionResult<Payload>)), MvcResultReaders.Get(typeof(ActionResult<Payload>)));
    }

    [Fact]
    public void Parameter_property_and_object_validation_are_not_skipped()
    {
        Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => Validate("RequiredParameter", null)).StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => Validate("RangeParameter", 0)).StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => Validate("ObjectParameter", new Payload())).StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => Validate("ObjectParameter", new ValidatedPayload())).StatusCode);
        Validate("RangeParameter", 10);
        Validate("ObjectParameter", new Payload { Name = "synthetic" });
        Validate("ObjectParameter", null);
    }

    [Fact]
    public void Empty_parameter_attribute_plan_reduces_allocations_without_skipping_object_validation()
    {
        var parameter = GetParameter("RangeParameter");
        var plan = new[] { new RuntimeArgument(parameter, RuntimeArgumentSource.Transport, 0, Array.Empty<ValidationAttribute>()) };
        object?[] arguments = { 10 };
        static void Legacy(object?[] values, RuntimeArgument[] parameters)
        {
            var errors = new List<ValidationResult>();
            var context = new ValidationContext(values[0]!) { MemberName = parameters[0].Parameter.Name, DisplayName = parameters[0].Parameter.Name! };
            Validator.TryValidateValue(values[0]!, context, errors, parameters[0].ValidationAttributes);
            Validator.TryValidateObject(values[0]!, new ValidationContext(values[0]!), errors, true);
        }
        for (var i = 0; i < 1000; i++) { Legacy(arguments, plan); GrpcEmbedService.Validate(arguments, plan); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) Legacy(arguments, plan);
        var legacy = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) GrpcEmbedService.Validate(arguments, plan);
        Assert.True(legacy - (GC.GetAllocatedBytesForCurrentThread() - before) >= 64000);
    }

    private static void Validate(string method, object? value)
    {
        var parameter = GetParameter(method);
        GrpcEmbedService.Validate(new[] { value }, new[] { new RuntimeArgument(parameter,
            RuntimeArgumentSource.Transport, 0, parameter.GetCustomAttributes<ValidationAttribute>().ToArray()) });
    }
    private static ParameterInfo GetParameter(string method) => typeof(ResultValidationPlanTests)
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters()[0];
    private static void RequiredParameter([Required] string? value) { }
    private static void RangeParameter([Range(1, 100)] int value) { }
    private static void ObjectParameter(object? value) { }
    public class Payload { [Required] public string? Name { get; set; } }
    private sealed class ValidatedPayload : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
            => new[] { new ValidationResult("synthetic invalid object") };
    }
}
