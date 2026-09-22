# Upgrading from 1.0.0 to 2.0.0

This release is not a drop-in compatibility promise. Update the GrpcEmbed package
family together and test your application's routes and DTOs before deployment.
The existing callback-based AddGrpcEmbed and AddGrpcEmbedClient APIs remain.
New API overloads can require disambiguation of explicit null arguments.

## Compatibility boundaries

| Combination | Conditions |
| --- | --- |
| 1.0 client to 2.0 server | Keep Validation Disabled or IfPresent. Preserve RPC routes and DTO field numbers. Regenerate any pinned ExpectedSchemaHash. |
| 2.0 client to 1.0 server | Keep Contract.Fetch=Never and SendHash=false. Preserve legacy method names with StripAsyncSuffix=false or GrpcName where needed. |
| Guarded 2.0 client to 1.0 server | Unsupported: legacy metadata lacks the new fingerprints and hash handshake. |
| 1.0 client to Required server | Rejected before action execution because no request contract hash is supplied. |

These are code-level compatibility conditions, not results of a mixed-version
binary test matrix. Current tests exercise the new implementation; production
mixed-version rollout has not been verified.

## Behavior changes

- Hashes are now based on canonical schema text. Even unchanged APIs have different
  hashes from 1.0.0. Hard-coded ExpectedSchemaHash values must be updated in sync
  with servers; that legacy response check is not a pre-execution safety mechanism.
- Client RPC names strip Async by default. GrpcName takes precedence;
  StripAsyncSuffix=false preserves the former naming rule.
- Protobuf field numbering is explicitly little-endian. This preserves the
  previous algorithm on common Windows/Linux x64 and ARM64 deployments, but not
  old big-endian deployments.
- The new AddGrpcEmbed(configuration) overload requires Server.Enabled=true.
  Server.ExposeAll defaults to false, selecting GrpcExport attributes. The old
  application-level GrpcEmbed.Enabled key is intentionally not supported here.
  Existing AddGrpcEmbed(options => ...) retains its default export-all behavior.
- Task/ValueTask and untyped IActionResult actions can now be discovered.
  Untyped results carrying a body fail instead of silently dropping data.
  Audit export-all applications and use GrpcIgnore for unsuitable actions.
- Proxies support inherited interface methods and cancellation overload pairs;
  ambiguous wire operation names fail during initialization.
- Channels are DI-owned and created on resolution/initialization instead of at
  registration time. InitializeGrpcEmbedClients can force early construction.

## Staged rollout

1. Verify method names and DTO field numbers; update all package references in
   each application together. Explicit field numbers are recommended for contracts
   that must remain stable across model changes.
2. Upgrade servers with validation Disabled or IfPresent and expose protected
   metadata. Keep existing REST routes.
3. After old server pods are gone, enable contract fetching and SendHash on clients.
   Configure the metadata URL explicitly when an ingress changes route prefixes.
4. Enable RefreshAndRetry if desired. Only the explicit pre-execution rejection
   can trigger a retry; timeout or ordinary failures cannot.
5. Set server Validation=Required only after all callers send contract hashes.

During a mixed-server rollout, metadata and RPC calls may hit different versions.
Retries are bounded and fail safely rather than guaranteeing availability.
Exact model fingerprints intentionally reject some additive changes. Metadata
refresh does not rewrite compiled DTOs, so this is not general schema evolution.
When rolling back a server to 1.0, first disable guarded fetching on its clients.

See [contract safety](contract-safety.md) for all settings and opt-out configuration.
