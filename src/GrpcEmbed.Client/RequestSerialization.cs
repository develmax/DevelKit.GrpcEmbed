using Grpc.Core;

namespace GrpcEmbed.Client;

internal static class RequestSerialization
{
    // Only generated request envelopes containing stable scalar arguments are
    // measured. DTOs and collections retain single-pass serialization: measuring
    // them could enumerate a one-shot sequence or invoke application getters twice.
    internal static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) ||
            type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) || type == typeof(TimeSpan);
    }

    internal static void Write<T>(T value, SerializationContext context, bool measureRequest)
    {
        if (measureRequest)
        {
            // A known length lets Grpc.Net.Client serialize into its pooled frame
            // buffer instead of growing an intermediate ArrayBufferWriter and
            // copying it into a second buffer. MeasureState reuses length hints.
            using var measured = GrpcEmbedRuntimeModel.Model.Measure(value);
            context.SetPayloadLength(checked((int)measured.Length));
            measured.Serialize(context.GetBufferWriter());
        }
        else
        {
            GrpcEmbedRuntimeModel.Model.Serialize(context.GetBufferWriter(), value);
        }
        context.Complete();
    }
}
