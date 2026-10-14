# Standard Protobuf wire-format correction

This document describes the historical **3.0** correction. For the subsequent
breaking collection-presence change, use [Migrating to 4.0](../MIGRATING-4.0.md).
The plain repeated-field limitations below apply to 3.x, not 4.x.

The 3.0.0 interoperability fix changes the wire encoding of special CLR
types. Do not overwrite published 2.1.2 packages or treat this as a wire-compatible
rolling upgrade. All five packages are released together as version 3.0.0.

## What was wrong

Manual `RuntimeTypeModel.Add(type, false)` skipped compatibility-level resolution.
Although GrpcEmbed set the model default to level 300, manually registered fields
could still use legacy protobuf-net encoding. The .proto advertised string GUIDs
and decimals and standard Timestamp/Duration messages. Matching .NET serializers
could round-trip those values while ordinary Protobuf clients failed to decode
them correctly. Insomnia exposed GUID bytes displayed as malformed strings.

Nullable collection elements also produced unresolved `Nullable` type references
in .proto and descriptors. Scalar descriptors included an explicitly empty
`type_name`, which standard descriptor resolvers attempted to resolve as a symbol.

## Corrected representation

Types are registered explicitly with compatibility level 300 before adding fields:

| CLR type | Protobuf representation |
| --- | --- |
| Guid / Guid? | string, canonical D-format GUID |
| decimal | invariant decimal string |
| DateTime | google.protobuf.Timestamp |
| TimeSpan | google.protobuf.Duration |
| DateTimeOffset | existing round-trip O-format string (unchanged) |
| Collection of Nullable<T> | repeated underlying scalar/enum type |

Protobuf repeated scalar fields do not represent null elements. Nullable CLR
element types do not add that capability; do not send null collection entries.
Property field numbers remain unchanged. JSON REST endpoints are unaffected.

## Upgrade safely

1. Inventory both server and client package versions. Include fetch-free clients,
   pinned contracts, and consumers of reflection or downloaded descriptors.
2. Deploy updated clients and servers as a coordinated cutover or a separate
   deployment/address. Do not mix legacy and corrected serializers behind the
   same service address during a rolling update.
3. Keep contract validation Required and hash sending enabled during migration.
   Contract hashes now use `grpcembed-contract-v3-level300`; CLR model-shape hashes
   use `grpcembed-shape-v2-level300`. Old/new configured clients fail model-shape
   validation before sending a business request, even after a contract refresh.
4. Refresh downloaded .proto/descriptors and regenerate standard clients if
   necessary. Invalidate pinned hashes and verify representative requests and
   responses, including GUIDs and dates, against REST and a standard gRPC client.
5. Fetch-free Native clients and installations with validation disabled require
   operational version coordination; fingerprints cannot protect calls that do
   not use them. There is no automatic REST fallback or legacy binary decoder.

Existing deployments and diagnostic files describe the deployed server version
until 3.0.0 is installed and their contracts refreshed. Updating source alone does
not change server responses. Do not edit a downloaded schema to pretend legacy GUID
bytes are strings; migrate the serializer and server together.
