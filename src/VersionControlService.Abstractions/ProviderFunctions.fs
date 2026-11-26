namespace VersionControlService.Abstractions

/// Everything a provider says about its functions: one entry per function of the interface except
/// Probe and Close, which every provider has. A provider writes every entry, so the compiler
/// reports the entries that a new function of the interface needs.
type ProviderFunctions = {
    VerifyLocation: Entry<VerifyLocationRequest -> OperationContext -> Async<OperationResult<AccessReport>>>
    Initialize: Entry<InitializeRequest -> OperationContext -> Async<OperationResult<Repository>>>
    Clone: Entry<CloneRequest -> OperationContext -> Async<OperationResult<Repository>>>
    Bind: Entry<BindRequest -> OperationContext -> Async<OperationResult<Repository>>>
    Open: Entry<OpenRequest -> OperationContext -> Async<OperationResult<Repository>>>
    CheckDependencies: Entry<OperationContext -> Async<OperationResult<DependencyStatus[]>>>
    InstallDependency: Entry<string -> OperationContext -> Async<OperationResult<DependencyStatus>>>
    GetStatus: Entry<Repository -> OperationContext -> Async<OperationResult<RepositoryStatus>>>
    ListRefs: Entry<Repository -> OperationContext -> Async<OperationResult<LogicalRef[]>>>
    CreateRef: Entry<Repository -> CreateRefRequest -> OperationContext -> Async<OperationResult<LogicalRef>>>
    PreflightSwitchRef: Entry<Repository -> SwitchRefRequest -> OperationContext -> Async<OperationResult<SwitchPreflight>>>
    SwitchRef: Entry<Repository -> SwitchRefRequest -> OperationContext -> Async<OperationResult<RepositoryStatus>>>
    CreateRevision: Entry<Repository -> CreateRevisionRequest -> OperationContext -> Async<OperationResult<RevisionId>>>
    RestorePaths: Entry<Repository -> RestoreRequest -> OperationContext -> Async<OperationResult<unit>>>
    GetDiffSummary: Entry<Repository -> OperationContext -> Async<OperationResult<DiffSummary>>>
    Synchronization: SynchronizationFunctions
    ConflictResolution: ConflictResolutionFunctions
    TextDiff: TextDiffFunctions
    History: HistoryFunctions
    RepositoryBrowser: RepositoryBrowserFunctions
    ObjectMaterialization: ObjectMaterializationFunctions
    StoragePolicy: StoragePolicyFunctions
    Maintenance: MaintenanceFunctions
}
