# Standard Protobuf wire-format correction

The unreleased interoperability fix changes the wire encoding of special CLR
types. Do not overwrite published 2.1.2 packages or treat this as a wire-compatible
rolling upgrade. A new major release is recommended; no release tag or package
version has been assigned by this change.

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

The existing stand and diagnostic files continue to describe the deployed 2.1.2
server until a new release is published and deployed. Updating source alone does
not change its responses. Do not edit a downloaded schema to pretend legacy GUID
bytes are strings; migrate the serializer and server together.
