# Automated NuGet.org publishing

The `Publish NuGet` workflow publishes all five `DevelKit.GrpcEmbed` packages
and their five `.snupkg` files. It runs for pushed `v<version>` tags, after
Windows/Linux CI, the required Windows HTTP/3 test and CodeQL complete
successfully. Ordinary commits and pull requests do not publish packages.

## One-time NuGet.org setup

Sign in to the NuGet account that owns these packages (`develmax` currently).
Open **Trusted Publishing** and add a GitHub policy with:

| Field | Value |
|---|---|
| Repository Owner | `develmax` |
| Repository | `DevelKit.GrpcEmbed` |
| Workflow File | `publish-nuget.yml` (filename only) |
| Environment | `nuget` |
| Package scope | `DevelKit.GrpcEmbed*` |
| Allowed operation | Publish new versions of existing packages |

Choose the individual or organization owner that actually owns the packages.
If the NuGet login changes, set GitHub repository variable `NUGET_USER` to that
profile username, not an email address. Otherwise the workflow defaults to
`develmax`. No long-lived API key secret is needed.

The `nuget` GitHub environment can additionally restrict deployments to release
tags and require approval. Required reviewers intentionally add a manual step.
Restrict tag creation and workflow changes to trusted maintainers.

NuGet validates GitHub's OIDC identity and issues a short-lived key. Only the
publishing job receives `id-token: write`; builds and dry runs have no NuGet
credentials. The policy cannot be created by editing repository files: an
authorized package owner must configure it on NuGet.org before the first upload.

[Official Trusted Publishing setup](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)

## Release a new version

1. Update `Version` and release notes in `Directory.Build.props`, the changelog
   and versioned installation examples. Merge the changes into `main`.
2. Create and push a matching tag from that commit, for example:

   ```shell
   git switch main
   git pull --ff-only
   git tag v2.1.3
   git push origin v2.1.3
   ```

   This example requires `Version` to be `2.1.3`. Prerelease tags such as
   `v2.2.0-rc.1` are also supported. Tags must match the declared version exactly
   and point to commits in `main` history; version overrides are not used.
3. Wait for `Publish NuGet` to finish. NuGet.org may need additional time to
   validate and index packages and symbols after accepting the uploads.

The publishing job downloads the package artifact from CI in the **same run**;
it does not rebuild or select an artifact from another commit. Before obtaining
credentials it validates the ten filenames, package IDs, versions, primary
package repository commits and symbol package types. Packages are pushed first,
then symbols explicitly, using the NuGet.org V3 source.

## Dry run and recovery

Run **Actions → Publish NuGet → Run workflow** with `publish` unchecked (the
default). It runs the same CI and CodeQL, checks all ten archives and prints the
upload plan, but never requests an OIDC token or uploads anything.

To retry an interrupted release, rerun its failed jobs or manually run the
workflow against its existing version tag with `publish` checked. Branch
publication is rejected. `--skip-duplicate` permits completed uploads to be
skipped; explicit `.snupkg` pushes ensure symbols are retried even if the primary
package already exists. Uploading five packages is not an atomic transaction.

Do not move an existing release tag or reuse a published version for changed
binaries. NuGet packages are immutable; publish a new version instead. A manually
published 2.1.2 must not be repackaged under a later commit as the same version.
The first automated release can therefore be 2.1.3 after the policy is configured.

[Symbol package publishing and validation](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg)
