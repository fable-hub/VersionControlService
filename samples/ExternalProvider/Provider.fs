module ExternalProvider.Provider

open VersionControlService.Abstractions

let private providerId =
    match ProviderId.tryCreate "sample.external" with
    | Ok value -> value
    | Error message -> failwith message

/// The repository this provider opens. A provider with state keeps it in its own subclass.
type private SampleRepository(descriptor: RepositoryDescriptor) =
    inherit Repository(descriptor)

/// Every function that works on a repository starts here, so a repository that another provider
/// opened gets the failure for it.
let private ownRepository (repository: Repository) (result: OperationResult<'T>) : Async<OperationResult<'T>> =
    async {
        match repository with
        | :? SampleRepository -> return result
        | _ -> return OperationResult.failed (Repository.ofAnotherProvider repository)
    }

let private readOnly = "The sample provider is read-only."

let private functions: ProviderFunctions = {
    VerifyLocation = Missing "The sample provider has no remote location to verify."
    Initialize = Missing readOnly
    Clone = Missing readOnly
    Bind = Missing readOnly
    Open =
        Implemented(fun request _ ->
            async {
                let descriptor: RepositoryDescriptor = {
                    ProviderId = providerId
                    RepositoryRoot = request.RepositoryRoot
                    Location = None
                }

                return OperationResult.succeeded (SampleRepository descriptor :> Repository)
            })
    CheckDependencies = Implemented(fun _ -> async { return OperationResult.succeeded [||] })
    InstallDependency = Missing "The sample provider needs no dependency."
    GetStatus =
        Implemented(fun repository _ ->
            ownRepository
                repository
                (OperationResult.succeeded {
                    CurrentRef = None
                    RepositoryVersion = "sample-read-only"
                    Changes = [||]
                    ActiveConflictSession = None
                    Synchronization = None
                }))
    ListRefs = Implemented(fun repository _ -> ownRepository repository (OperationResult.succeeded [||]))
    CreateRef = Missing readOnly
    PreflightSwitchRef = Missing readOnly
    SwitchRef = Missing readOnly
    CreateRevision = Missing readOnly
    RestorePaths = Missing readOnly
    GetDiffSummary =
        Implemented(fun repository _ -> ownRepository repository (OperationResult.succeeded { Entries = [||] }))
    Synchronization = {
        Refresh = Missing "The sample provider has no remote to synchronize with."
        PreviewUpdate = Missing "The sample provider has no remote to synchronize with."
        Update = Missing "The sample provider has no remote to synchronize with."
        Publish = Missing "The sample provider has no remote to synchronize with."
        Synchronize = Missing "The sample provider has no remote to synchronize with."
    }
    ConflictResolution = {
        GetActiveSession = Missing readOnly
        Resolve = Missing readOnly
        Finalize = Missing readOnly
        Cancel = Missing readOnly
    }
    TextDiff = {
        Open = Missing "The sample provider has no text diff."
        ReadPage = Missing "The sample provider has no text diff."
        ReplayPage = Missing "The sample provider has no text diff."
        Expand = Missing "The sample provider has no text diff."
        ReadLine = Missing "The sample provider has no text diff."
        GetSourceInfo = Missing "The sample provider has no text diff."
        Close = Missing "The sample provider has no text diff."
    }
    History = {
        ListRevisions = Missing "The sample provider has no history."
        GetRevisionChanges = Missing "The sample provider has no history."
    }
    RepositoryBrowser = { GetRepositoryWebUrl = Missing "The sample provider has no web address for a repository." }
    ObjectMaterialization = {
        ListObjects = Missing "The sample provider has no large objects."
        Materialize = Missing "The sample provider has no large objects."
        Dematerialize = Missing "The sample provider has no large objects."
    }
    StoragePolicy = {
        SetPathPolicy = Missing "The sample provider has no large objects."
        GetSettings = Missing "The sample provider has no large objects."
        SetSettings = Missing "The sample provider has no large objects."
    }
    Maintenance = {
        Prune = Missing "The sample provider has no local storage to maintain."
        Deduplicate = Missing "The sample provider has no local storage to maintain."
        CanDeduplicate = Missing "The sample provider has no local storage to maintain."
    }
}

let provider: IVersionControl =
    ListedProvider((fun _ -> async { return NotDetected }), (fun _ -> async.Return()), functions)
