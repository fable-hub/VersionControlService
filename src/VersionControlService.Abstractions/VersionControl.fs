namespace VersionControlService.Abstractions

/// A version control provider. Every function is a member here or in one of the group interfaces.
/// A function the provider does not have answers `NoOp` with the reason, and a call never throws for that.
type IVersionControl =
    /// Probes a directory for provider ownership. Must not throw; failures are ProbeFailed.
    abstract Probe: folder: string -> Async<ProbeResult>

    /// Verifies requested access intents against a repository location.
    abstract VerifyLocation:
        request: VerifyLocationRequest -> context: OperationContext -> Async<CallResult<AccessReport>>

    abstract Initialize: request: InitializeRequest -> context: OperationContext -> Async<CallResult<Repository>>
    abstract Clone: request: CloneRequest -> context: OperationContext -> Async<CallResult<Repository>>

    /// Attaches or re-targets an existing local workspace without cloning.
    abstract Bind: request: BindRequest -> context: OperationContext -> Async<CallResult<Repository>>

    /// Opens an existing local repository without cloning it.
    abstract Open: request: OpenRequest -> context: OperationContext -> Async<CallResult<Repository>>

    /// Reports installed/missing/incompatible dependency components with remediation.
    abstract CheckDependencies: context: OperationContext -> Async<CallResult<DependencyStatus[]>>

    /// Attempts remediation for a component reported by CheckDependencies.
    abstract InstallDependency:
        dependency: string -> context: OperationContext -> Async<CallResult<DependencyStatus>>

    abstract GetStatus: repository: Repository -> context: OperationContext -> Async<CallResult<RepositoryStatus>>
    abstract ListRefs: repository: Repository -> context: OperationContext -> Async<CallResult<LogicalRef[]>>

    abstract CreateRef:
        repository: Repository -> request: CreateRefRequest -> context: OperationContext -> Async<CallResult<LogicalRef>>

    abstract PreflightSwitchRef:
        repository: Repository ->
        request: SwitchRefRequest ->
        context: OperationContext ->
            Async<CallResult<SwitchPreflight>>

    abstract SwitchRef:
        repository: Repository ->
        request: SwitchRefRequest ->
        context: OperationContext ->
            Async<CallResult<RepositoryStatus>>

    /// Creates a revision from exact selected paths and an expected repository version.
    abstract CreateRevision:
        repository: Repository ->
        request: CreateRevisionRequest ->
        context: OperationContext ->
            Async<CallResult<RevisionId>>

    /// Restores the paths to their base revision. A path whose object is available locally stays
    /// materialized.
    abstract RestorePaths:
        repository: Repository -> request: RestoreRequest -> context: OperationContext -> Async<CallResult<unit>>

    abstract GetDiffSummary: repository: Repository -> context: OperationContext -> Async<CallResult<DiffSummary>>

    /// Releases what the repository holds (locks, clients, caches).
    abstract Close: repository: Repository -> Async<unit>

    abstract Synchronization: ISynchronization
    abstract ConflictResolution: IConflictResolution
    abstract TextDiff: ITextDiff
    abstract History: IHistory
    abstract RepositoryBrowser: IRepositoryBrowser
    abstract ObjectMaterialization: IObjectMaterialization
    abstract StoragePolicy: IStoragePolicy
    abstract Maintenance: IMaintenance

/// Builds the interface from a provider's list. It is the only implementation of IVersionControl in the library.
[<Sealed>]
type ListedProvider(probe: string -> Async<ProbeResult>, close: Repository -> Async<unit>, functions: ProviderFunctions) =
    let execute (entry: Entry<'call>) (invoke: 'call -> Async<OperationResult<'T>>) : Async<CallResult<'T>> =
        match entry with
        | Implemented call ->
            async {
                let! result = invoke call
                return Executed result
            }
        | Missing reason -> async { return NoOp reason }

    let synchronization =
        { new ISynchronization with
            member _.Refresh repository context =
                execute functions.Synchronization.Refresh (fun call -> call repository context)

            member _.PreviewUpdate repository context =
                execute functions.Synchronization.PreviewUpdate (fun call -> call repository context)

            member _.Update repository request context =
                execute functions.Synchronization.Update (fun call -> call repository request context)

            member _.Publish repository request context =
                execute functions.Synchronization.Publish (fun call -> call repository request context)

            member _.Synchronize repository request context =
                execute functions.Synchronization.Synchronize (fun call -> call repository request context) }

    let conflictResolution =
        { new IConflictResolution with
            member _.GetActiveSession repository context =
                execute functions.ConflictResolution.GetActiveSession (fun call -> call repository context)

            member _.Resolve repository request context =
                execute functions.ConflictResolution.Resolve (fun call -> call repository request context)

            member _.Finalize repository request context =
                execute functions.ConflictResolution.Finalize (fun call -> call repository request context)

            member _.Cancel repository request context =
                execute functions.ConflictResolution.Cancel (fun call -> call repository request context) }

    let textDiff =
        { new ITextDiff with
            member _.Open repository request context =
                execute functions.TextDiff.Open (fun call -> call repository request context)

            member _.ReadPage repository request context =
                execute functions.TextDiff.ReadPage (fun call -> call repository request context)

            member _.ReplayPage repository request context =
                execute functions.TextDiff.ReplayPage (fun call -> call repository request context)

            member _.Expand repository request context =
                execute functions.TextDiff.Expand (fun call -> call repository request context)

            member _.ReadLine repository request context =
                execute functions.TextDiff.ReadLine (fun call -> call repository request context)

            member _.GetSourceInfo repository request context =
                execute functions.TextDiff.GetSourceInfo (fun call -> call repository request context)

            member _.Close repository handle context =
                execute functions.TextDiff.Close (fun call -> call repository handle context) }

    let history =
        { new IHistory with
            member _.ListRevisions repository request context =
                execute functions.History.ListRevisions (fun call -> call repository request context)

            member _.GetRevisionChanges repository revision context =
                execute functions.History.GetRevisionChanges (fun call -> call repository revision context) }

    let repositoryBrowser =
        { new IRepositoryBrowser with
            member _.GetRepositoryWebUrl repository context =
                execute functions.RepositoryBrowser.GetRepositoryWebUrl (fun call -> call repository context) }

    let objectMaterialization =
        { new IObjectMaterialization with
            member _.ListObjects repository context =
                execute functions.ObjectMaterialization.ListObjects (fun call -> call repository context)

            member _.Materialize repository paths context =
                execute functions.ObjectMaterialization.Materialize (fun call -> call repository paths context)

            member _.Dematerialize repository paths context =
                execute functions.ObjectMaterialization.Dematerialize (fun call -> call repository paths context) }

    let storagePolicy =
        { new IStoragePolicy with
            member _.SetPathPolicy repository path enabled context =
                execute functions.StoragePolicy.SetPathPolicy (fun call -> call repository path enabled context)

            member _.GetSettings repository context =
                execute functions.StoragePolicy.GetSettings (fun call -> call repository context)

            member _.SetSettings repository settings context =
                execute functions.StoragePolicy.SetSettings (fun call -> call repository settings context) }

    let maintenance =
        { new IMaintenance with
            member _.Prune repository context =
                execute functions.Maintenance.Prune (fun call -> call repository context)

            member _.Deduplicate repository context =
                execute functions.Maintenance.Deduplicate (fun call -> call repository context)

            member _.CanDeduplicate repository context =
                execute functions.Maintenance.CanDeduplicate (fun call -> call repository context) }

    /// The list the provider was built from.
    member _.Functions = functions

    interface IVersionControl with
        member _.Probe folder = probe folder

        member _.VerifyLocation request context =
            execute functions.VerifyLocation (fun call -> call request context)

        member _.Initialize request context =
            execute functions.Initialize (fun call -> call request context)

        member _.Clone request context =
            execute functions.Clone (fun call -> call request context)

        member _.Bind request context =
            execute functions.Bind (fun call -> call request context)

        member _.Open request context =
            execute functions.Open (fun call -> call request context)

        member _.CheckDependencies context =
            execute functions.CheckDependencies (fun call -> call context)

        member _.InstallDependency dependency context =
            execute functions.InstallDependency (fun call -> call dependency context)

        member _.GetStatus repository context =
            execute functions.GetStatus (fun call -> call repository context)

        member _.ListRefs repository context =
            execute functions.ListRefs (fun call -> call repository context)

        member _.CreateRef repository request context =
            execute functions.CreateRef (fun call -> call repository request context)

        member _.PreflightSwitchRef repository request context =
            execute functions.PreflightSwitchRef (fun call -> call repository request context)

        member _.SwitchRef repository request context =
            execute functions.SwitchRef (fun call -> call repository request context)

        member _.CreateRevision repository request context =
            execute functions.CreateRevision (fun call -> call repository request context)

        member _.RestorePaths repository request context =
            execute functions.RestorePaths (fun call -> call repository request context)

        member _.GetDiffSummary repository context =
            execute functions.GetDiffSummary (fun call -> call repository context)

        member _.Close repository = close repository
        member _.Synchronization = synchronization
        member _.ConflictResolution = conflictResolution
        member _.TextDiff = textDiff
        member _.History = history
        member _.RepositoryBrowser = repositoryBrowser
        member _.ObjectMaterialization = objectMaterialization
        member _.StoragePolicy = storagePolicy
        member _.Maintenance = maintenance
