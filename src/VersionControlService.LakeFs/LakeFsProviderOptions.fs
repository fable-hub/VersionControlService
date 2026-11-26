module VersionControlService.LakeFs.LakeFsProviderOptions

open VersionControlService.Abstractions

module LakeFsCredentials = VersionControlService.LakeFs.LakeFsCredentials

/// Test seams, mirroring the Git provider: named barrier points let harnesses
/// advance destinations inside check-then-act-then-verify windows.
type LakeFsSessionHooks = {
    Barrier: (string -> string -> OperationContext -> Async<unit>) option
}

module LakeFsSessionHooks =

    let none: LakeFsSessionHooks = { Barrier = None }

/// What the host gives the lakeFS provider: where it keeps its state, how paths compare, the test
/// seams and how a connection profile resolves to a lakeFS connection.
type LakeFsProviderOptions = {
    /// Host-owned location for all provider state. It must remain outside every
    /// managed workspace and is never serialized into a workspace directory.
    StateRoot: string
    PathCaseSensitivity: PathCaseSensitivity
    /// Test seams. LakeFsSessionHooks.none sets no hook.
    Hooks: LakeFsSessionHooks
    /// Resolves a connection profile to the lakeFS endpoint and its keys.
    Credentials: LakeFsCredentials.LakeFsCredentialStrategy
}
