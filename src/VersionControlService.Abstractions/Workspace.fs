namespace VersionControlService.Abstractions

/// Result of probing a directory for provider ownership. Provider metadata on disk
/// is a hint. A host's explicit choice of provider always wins.
type ProbeResult =
    | NotDetected
    | Detected of root: string * confidence: int * bindingHint: string option
    | Invalid of message: string
    | ProbeFailed of OperationFailure

/// Access intents a consumer can ask a provider to verify against a repository location.
type AccessIntent =
    | ReadIntent
    | WriteObjectIntent
    | CreateRevisionIntent
    | CreateBranchIntent
    | MergeIntent
    | PublishIntent

/// Result of remote access verification: which requested intents are granted.
type AccessReport = {
    Location: RepositoryLocation
    GrantedIntents: AccessIntent[]
    DeniedIntents: AccessIntent[]
}

type VerifyLocationRequest = {
    Location: RepositoryLocation
    Intents: AccessIntent[]
}

type InitializeRequest = {
    /// Local directory to initialize as a new provider workspace.
    TargetPath: string
    /// Optional repository location the new workspace should target.
    Location: RepositoryLocation option
}

type CloneRequest = {
    Location: RepositoryLocation
    TargetPath: string
    /// Optional exact provider ref to check out after cloning. A provider returns an Unsupported
    /// failure for a ref it cannot check out. lakeFS returns a Validation failure (`target_ref_mismatch`)
    /// for a ref that contradicts the location. It never ignores the ref.
    TargetRef: ProviderRef option
    /// Whether large/lazily-materialized objects are hydrated during clone.
    MaterializeAllObjects: bool
}

/// Opens an existing provider-owned repository on disk without cloning or changing it.
/// The provider finds the repository root from the given folder and reports it in the
/// descriptor of the opened repository.
type OpenRequest = {
    RepositoryRoot: string
    ConnectionProfileId: string option
}

/// Attaches or re-targets an existing local workspace to a repository location
/// without cloning (the neutral replacement for "connect remote").
type BindRequest = {
    RepositoryRoot: string
    Location: RepositoryLocation
}

/// Identity of one opened repository.
type RepositoryDescriptor = {
    ProviderId: ProviderId
    /// The local workspace root as the provider received it. For Open it is the root the provider reports, and
    /// the Git provider reports it as Git spells it, with links resolved. A host keys its own table by the folder
    /// it passed to Open and compares folders with `ProviderResolver.normalizePath`.
    RepositoryRoot: string
    Location: RepositoryLocation option
}

/// Installed/missing/incompatible dependency component with remediation instructions.
type DependencyStatus = {
    Component: string
    Installed: bool
    Version: string option
    Compatible: bool
    Remediation: string option
    /// The executable the provider ran or tried to run for this check, as an absolute path when it could
    /// be resolved. None when the provider runs no executable, and also when it could not resolve the
    /// executable it would run.
    ProbeExecutablePath: string option
}
