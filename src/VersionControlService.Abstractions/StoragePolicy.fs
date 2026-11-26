namespace VersionControlService.Abstractions

type StoragePolicySettings = {
    /// Threshold in megabytes above which new objects use large-object storage, when supported.
    AutoPolicyThresholdMb: int option
    /// Whether large objects are materialized during clone/update by default.
    MaterializeLargeObjects: bool
}

/// The storage policy functions of a provider, one entry for each.
type StoragePolicyFunctions = {
    SetPathPolicy: Entry<Repository -> RepositoryPath -> bool -> OperationContext -> Async<OperationResult<unit>>>
    GetSettings: Entry<Repository -> OperationContext -> Async<OperationResult<StoragePolicySettings>>>
    SetSettings: Entry<Repository -> StoragePolicySettings -> OperationContext -> Async<OperationResult<unit>>>
}

/// Large-object storage policy over literal repository paths.
type IStoragePolicy =
    /// True marks the path as a large object. False records an explicit opt-out that
    /// outranks the automatic policy of later revisions until the path is marked again.
    /// A provider that changes the policy reports the path in AffectedPaths, and the next
    /// revision that includes the path stores it under the new policy.
    abstract SetPathPolicy:
        repository: Repository ->
        path: RepositoryPath ->
        enabled: bool ->
        context: OperationContext ->
            Async<CallResult<unit>>

    abstract GetSettings:
        repository: Repository -> context: OperationContext -> Async<CallResult<StoragePolicySettings>>

    abstract SetSettings:
        repository: Repository -> settings: StoragePolicySettings -> context: OperationContext -> Async<CallResult<unit>>

/// How one selected regular file is stored in the revision being created. Only providers
/// with a large-object representation (Git with Git LFS) act on it.
[<RequireQualifiedAccess>]
type RevisionPathPolicy =
    /// The provider decides from its automatic threshold and its own rules.
    | Automatic
    /// The plain content, even above the threshold and even when a provider rule says large object.
    | Inline
    /// Large-object storage, even below the threshold.
    | LargeObject

/// What the strategy sees for each selected regular file. SizeInBytes is the size of the
/// content that would be committed, so for a file that is a recognized large-object
/// reference it is the declared payload size, not the length of the reference.
type RevisionPathPolicyRequest = {
    Path: RepositoryPath
    SizeInBytes: float
}

/// Immutable configuration handed to a provider at creation. The function must be fast,
/// deterministic and free of side effects. It must not touch the file system or the network.
type RevisionPolicyStrategy = {
    ResolvePathPolicy: RevisionPathPolicyRequest -> RevisionPathPolicy
}

module RevisionPolicyStrategy =

    let automatic: RevisionPolicyStrategy = {
        ResolvePathPolicy = fun _ -> RevisionPathPolicy.Automatic
    }
