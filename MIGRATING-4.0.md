# Migrating to GrpcEmbed 4.0

4.0 is a **breaking Protobuf wire-format change**, not a rolling-compatible patch
for 3.x. Update all five package IDs together to 4.0.0 where they are referenced.
REST endpoints and generated REST clients do not change.

## Why

A plain repeated field cannot distinguish an absent collection (`null`) from an
empty collection (`[]`). This caused nested empty collections to deserialize as
null, even when the REST response contained an empty array. A client-side
"replace every null with []" workaround would corrupt genuine nulls.

4.0 uses protobuf-net's public [presence-wrapper attributes](https://docs.protobuf-net.dev/nullwrappers),
applied to startup-generated wire surrogates. Serialization conversion delegates
are compiled once. Wire surrogates are value types; conversion does not create
an extra heap DTO on serialization. Do not assume the new wrappers improve
allocation or latency: measure the complete audited request path.

## Representation

The DTO field keeps its number but now refers to a collection message. That
message contains repeated `items = 1`. Reference/nullable items use another message
with `value = 1`; scalar/enum values are proto3 `optional` to preserve presence.

```protobuf
message Example { ExampleItems items = 10; }
message ExampleItems { repeated ExampleItem items = 1; }
message ExampleItem { optional int32 value = 1; }
```

| CLR value | Wire representation |
| --- | --- |
| `null` collection | field 10 absent |
| empty collection | field 10 present, empty message |
| `[null, 0, 42]` | collection with empty item, present zero value, present 42 value |

Nested collections, arrays and supported collection interfaces use the same
rules. `byte[]` remains a scalar Protobuf bytes field, not a repeated collection.
Special CLR types retain level-300 encoding; `Guid.Empty` uses the empty-string
default (with explicit presence in nullable item wrappers), nonempty GUIDs use
D-format strings. Deserialization skips DTO constructors/initializers, so absent
fields retain CLR defaults rather than application initializer defaults.

## Safe cutover

1. Update client and server dependencies together. Build and test both peers.
2. Use a coordinated cutover or a separate deployment/address. Do not mix 3.x
   and 4.x pods behind the same gRPC service address, even temporarily.
3. Enable contract discovery (`FirstCall` or `Startup`), hash sending, server
   `Contract.Validation = Required`, and schema/hash generation. Shape hashes
   include `grpcembed-shape-v3-level300-collection-presence`; schema hashes include
   `grpcembed-contract-v4-level300-collection-presence`. Discovered incompatible
   shapes fail before an RPC is sent; stale request hashes are rejected before
   action execution. Refreshing a hash does not make old binary encoding safe.
4. Fetch-free clients or validation-disabled installations are not protected by
   those guards. Coordinate their versions explicitly. There is no automatic
   legacy decoder or REST fallback.
5. Refresh `.proto`, descriptors, pinned hashes and Insomnia contracts; regenerate
   external Protobuf clients. Field numbers alone do not provide compatibility.
6. Verify request and response DTO equality with REST for null, empty, populated
   and nullable-item collections. Retain authorization, audit and masking checks.

Top-level null MVC results still follow existing error handling; this does not
convert a missing business result into a successful empty response.

Package publication and application deployment are separate steps. Updating
source code alone does not change a deployed service's wire format.
