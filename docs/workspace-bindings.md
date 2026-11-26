# Workspace bindings

The host owns binding persistence. VersionControlService does not keep an application settings database and does not write a generic binding file into managed workspaces.

## Binding contents

`WorkspaceBinding` contains:

- the schema version and provider ID
- `RepositoryRoot`, the path the provider received. For `Adopt`, this is the root Git reports.
- an optional opaque provider state reference
- a nonsecret repository location
- an optional connection profile ID

`ProviderResolver.normalizePath` is the comparison form hosts use for lookups. It is not the stored form.

Do not serialize access keys, tokens, passwords, credential-bearing URLs, process arguments, or provider clients into a binding. The host resolves `ConnectionProfileId` through the credential strategy injected into the selected factory.

HTTP and HTTPS drop all userinfo and SSH keeps the user name with its password removed. The provider keeps the location the caller gave wherever it reaches the repository.

## Lookup order

For each workspace request:

1. Look up the host-persisted binding by normalized workspace root. A binding selects the factory of its provider, and no probe runs.
2. Without a binding, the host asks the `Probe` of each provider it has and decides itself.
3. If the user or host policy accepts a workspace that a probe found, call that factory's `Adopt` method.
4. Persist the returned binding in the host store, then call `Open`.

Probing is a hint. It must not mutate provider or host state.

The lakeFS provider options take `CaseSensitive` or `CaseInsensitive` path comparison to match the filesystem. `ProviderResolver.normalizePath` normalizes separators and trailing separators for root comparison, but it preserves case and Unicode spelling.

## Adoption and initialization

Adoption registers an existing provider-owned workspace without cloning. The provider derives whatever repository location it can verify. If it cannot derive a safe binding, it returns `Unsupported` rather than guessing.

Initialization is for a directory that the provider does not already own. Providers preserve existing user files and report them as ordinary workspace changes. Clone retains the stricter missing-or-empty destination rule. If a workspace is already initialized, use adoption.

## External provider state

`ProviderStateRef` is opaque to the host. The host persists it unchanged and gives it back to `Open`.

Providers that need mutable indexes, transactions, or recovery data keep them outside the managed workspace. The lakeFS factory requires a host-selected `StateRoot`, and every state reference resolves beneath that root and outside every workspace. Missing, corrupt, mismatched, or in-workspace state fails structurally. The provider does not recreate state silently.

Keeping this boundary matters for two reasons: provider metadata cannot collide with repository objects, and a failed materialization can recover without treating transaction files as user content.

## Session ownership

An opened session owns its locks, clients, and caches. Call `WorkspaceSession.Close` when the host releases it. Do not reuse a session for a different binding or connection profile. Multiple sessions can exist at once. Mutation safety comes from workspace-version and conflict-handle checks, which is why no global singleton state is needed.
