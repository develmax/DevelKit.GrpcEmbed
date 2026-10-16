# Server response serialization diagnostics

`DevelKit.GrpcEmbed.AspNetCore` 4.0.7 adds the **disabled-by-default** EventSource
`DevelKit-GrpcEmbed-Server`. No listener is installed by the library. Enable it
only for a separate diagnostic experiment, using an `EventListener` or an
external EventPipe collector at Verbose level, keyword `0x1`.

Event **1**, `ResponseSerialization`, contains only:

| Field | Meaning |
|---|---|
| `startTimestamp` | `Stopwatch.GetTimestamp()` immediately before the response marshaller |
| `elapsedTicks` | Time in protobuf serialization and `SerializationContext.Complete()`, including failure paths |
| `succeeded` | 1 when both serialization and completion return successfully; otherwise 0 |

Convert ticks using `Stopwatch.Frequency` from the **same server process**. Do
not compare these timestamps directly with a client's clock. An in-process
listener can associate the synchronous event with its current HTTP request;
the event itself contains no identifiers, URLs, DTOs, headers or exception text.

The event is response-only. Without a listener, the marshaller does not read a
clock, emit an event or allocate a timing payload. A listener introduces work,
so disable it again for the primary performance comparison.

## What this does not measure

- `OnStarting` is not a network-send timestamp. It may run before protobuf
  serialization and before a response audit middleware finishes.
- The event does not include response auditing, framing outside the marshaller,
  Kestrel's response completion, proxy processing or client receipt.
- A measured `HttpResponse.CompleteAsync()` includes the server's response
  completion and pending output/trailers flush. It is not a network RTT or proof
  that the client has consumed the response. Calling it explicitly changes the
  diagnostic path; do not silently add it to the normal production pipeline.

Keep authentication, body auditing, masking, deadlines and cancellation active
in every comparison. Never infer full-request latency savings from an isolated
serializer duration or allocation reduction.
