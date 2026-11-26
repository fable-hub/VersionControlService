namespace VersionControlService.Abstractions

/// One opened per-workspace session. Service presence controls feature discovery.
/// A provider leaves a service absent when it has nothing to offer there, and a
/// consumer checks presence once and enables the matching UI.
///
/// After prerelease stabilization this record and its service records are frozen: new
/// capabilities are added as new optional service records, never as new members
/// on already-published records.
type WorkspaceSession = {
    Descriptor: WorkspaceDescriptor
    Core: CoreVersionControl
    Synchronization: SynchronizationService option
    TextDiff: TextDiffService option
    ConflictResolution: ConflictResolutionService option
    ObjectMaterialization: ObjectMaterializationService option
    StoragePolicy: StoragePolicyService option
    Maintenance: StorageMaintenanceService option
    RepositoryBrowser: RepositoryBrowserService option
    History: HistoryService option
    /// Releases session-owned resources (locks, clients, caches).
    Close: unit -> Async<unit>
}

/// Reports which optional services the provider supplies on an opened session.
type ServiceAvailability = {
    Synchronization: bool
    TextDiff: bool
    ConflictResolution: bool
    ObjectMaterialization: bool
    StoragePolicy: bool
    Maintenance: bool
    RepositoryBrowser: bool
    History: bool
}

module WorkspaceSession =

    /// Which optional services the provider supplies.
    let availability (session: WorkspaceSession) : ServiceAvailability = {
        Synchronization = session.Synchronization.IsSome
        TextDiff = session.TextDiff.IsSome
        ConflictResolution = session.ConflictResolution.IsSome
        ObjectMaterialization = session.ObjectMaterialization.IsSome
        StoragePolicy = session.StoragePolicy.IsSome
        Maintenance = session.Maintenance.IsSome
        RepositoryBrowser = session.RepositoryBrowser.IsSome
        History = session.History.IsSome
    }

    /// A core-only session: every optional service absent. Providers add the
    /// services they genuinely support.
    let createCoreOnly (descriptor: WorkspaceDescriptor) (core: CoreVersionControl) : WorkspaceSession = {
        Descriptor = descriptor
        Core = core
        Synchronization = None
        TextDiff = None
        ConflictResolution = None
        ObjectMaterialization = None
        StoragePolicy = None
        Maintenance = None
        RepositoryBrowser = None
        History = None
        Close = fun () -> async.Return()
    }

/// Provider entry point: probing, verification, provisioning, binding, and opening
/// per-workspace sessions.
type ProviderFactory = {
    Id: ProviderId
    /// Probes a directory for provider ownership. Must not throw; failures are ProbeFailed.
    Probe: string -> Async<ProbeResult>
    /// Verifies requested access intents against a repository location.
    VerifyLocation: VerifyLocationRequest -> OperationContext -> Async<OperationResult<AccessReport>>
    Initialize: InitializeRequest -> OperationContext -> Async<OperationResult<WorkspaceBinding>>
    Clone: CloneRequest -> OperationContext -> Async<OperationResult<WorkspaceBinding>>
    /// Registers an existing provider-owned workspace without cloning.
    Adopt: AdoptRequest -> OperationContext -> Async<OperationResult<WorkspaceBinding>>
    /// Attaches or re-targets an existing local workspace without cloning.
    Bind: BindRequest -> OperationContext -> Async<OperationResult<WorkspaceBinding>>
    Open: WorkspaceBinding -> OperationContext -> Async<OperationResult<WorkspaceSession>>
    /// Reports installed/missing/incompatible dependency components with remediation.
    CheckDependencies: OperationContext -> Async<OperationResult<DependencyStatus[]>>
    /// Attempts remediation for a component reported by CheckDependencies.
    InstallDependency: string -> OperationContext -> Async<OperationResult<DependencyStatus>>
}
