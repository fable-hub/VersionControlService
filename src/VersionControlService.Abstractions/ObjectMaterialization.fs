namespace VersionControlService.Abstractions

/// Materialization state of one lazily-hydrated object.
type ObjectState = {
    Path: RepositoryPath
    IsMaterialized: bool
    /// Whether the object bytes are available in provider-local storage.
    IsLocallyAvailable: bool
    SizeBytes: float option
    ObjectId: string option
}

/// Stable codes of the failures and recoveries that object materialization returns. A provider that has the
/// functions of `IObjectMaterialization` uses them, and a consumer decides by the code because the messages can change.
module ObjectMaterializationCodes =

    /// A path has local changes that the call would overwrite, for example an edited file. A call returns it
    /// when every failed path was refused for this reason.
    [<Literal>]
    let LocalChanges = "local_changes"

    /// Some paths of a Materialize call with several paths failed, and not all of them for local changes.
    [<Literal>]
    let BatchMaterializationIncomplete = "batch_materialization_incomplete"

    /// Some paths of a Dematerialize call with several paths failed, and not all of them for local changes.
    [<Literal>]
    let BatchDematerializationIncomplete = "batch_dematerialization_incomplete"

    /// Recovery: materialize the affected paths again. An operation that downloads large objects after its
    /// own transfer, such as an update, also uses it when that download did not finish.
    [<Literal>]
    let RetryMaterializationRecovery = "retry_materialization"

    /// Recovery: dematerialize the affected paths again.
    [<Literal>]
    let RetryDematerializationRecovery = "retry_dematerialization"

/// The object materialization functions of a provider, one entry for each.
type ObjectMaterializationFunctions = {
    ListObjects: Entry<Repository -> OperationContext -> Async<OperationResult<ObjectState[]>>>
    Materialize: Entry<Repository -> RepositoryPath[] -> OperationContext -> Async<OperationResult<unit>>>
    Dematerialize: Entry<Repository -> RepositoryPath[] -> OperationContext -> Async<OperationResult<unit>>>
}

/// Object materialization. A provider has these functions when its workspace holds large or
/// lazily fetched objects as references that a call replaces with the object's content, or the reverse.
/// ListObjects reports the state of those objects. Materialize and Dematerialize take an array of paths, so
/// one call serves a single file and a whole folder, and a provider that handles many paths in one operation
/// is faster than one call per path.
///
/// Paths are exact keys and a duplicate counts once. An empty array or paths that are already in the target
/// state give Succeeded with an Unchanged effect. When some paths fail and others changed, the result is
/// PartiallySucceeded. When none changed, it is Failed. A failure lists the failed paths in AffectedPaths and
/// one "<path>: <message>" line per failed path in Details. The outcome's AffectedPaths lists the changed
/// paths. A canceled run reports the paths that finished as changed and fails the others with a Canceled
/// failure (PartiallySucceeded when some changed, Failed when none).
///
/// A call with one path fails with that path's own code and message. With several paths, the failure code
/// is `ObjectMaterializationCodes.LocalChanges` when every failed path was refused for local changes.
/// Otherwise it is `ObjectMaterializationCodes.BatchMaterializationIncomplete` for Materialize and
/// `ObjectMaterializationCodes.BatchDematerializationIncomplete` for Dematerialize. A partial success carries
/// the recovery `ObjectMaterializationCodes.RetryMaterializationRecovery` or
/// `ObjectMaterializationCodes.RetryDematerializationRecovery`.
type IObjectMaterialization =
    abstract ListObjects: repository: Repository -> context: OperationContext -> Async<CallResult<ObjectState[]>>

    /// Replaces the references of the given paths with their content.
    abstract Materialize:
        repository: Repository -> paths: RepositoryPath[] -> context: OperationContext -> Async<CallResult<unit>>

    /// Replaces the content of the given paths with their references. For each path that changes, it also
    /// removes the provider-local copy of the object. A provider does this only after checking that its remote
    /// has the object, and it keeps the copy when the local storage is shared with other workspaces.
    abstract Dematerialize:
        repository: Repository -> paths: RepositoryPath[] -> context: OperationContext -> Async<CallResult<unit>>
