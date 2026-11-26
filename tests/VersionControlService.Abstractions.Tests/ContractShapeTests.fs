module VersionControlService.Abstractions.Tests.ContractShapeTests

open Expecto
open VersionControlService.Abstractions

/// The repository the fake providers hand out.
type FakeRepository(descriptor: RepositoryDescriptor) =
    inherit Repository(descriptor)

/// Fake providers shared by the shape and portable-consumer tests. They list only the functions
/// they have and give a reason for every other function.
module FakeProvider =

    let providerId (name: string) =
        match ProviderId.tryCreate name with
        | Ok id -> id
        | Error message -> failwith message

    let providerRef (name: string) =
        match ProviderRef.tryCreate name with
        | Ok reference -> reference
        | Error message -> failwith message

    let repositoryPath (path: string) =
        match RepositoryPath.tryCreate path with
        | Ok value -> value
        | Error message -> failwith message

    let progress: OperationProgress = {
        PhaseCode = "transfer"
        Item = None
        Completed = Some 3_000_000_000.0
        Total = Some 4_000_000_000.0
        DisplayMessage = None
    }

    /// The reason of every entry that the fakes leave out, except the preview of an update.
    let noFunction = "The fake provider does not have this function."

    /// The reason of the preview entry that the final synchronization of the fakes leaves out.
    let previewUpdateReason = "The fake provider cannot preview an update."

    let storagePolicy: StoragePolicyFunctions = {
        SetPathPolicy = Implemented(fun _ (_path: RepositoryPath) _enabled _context -> async { return OperationResult.succeeded () })
        GetSettings =
            Implemented(fun _ _ ->
                async {
                    return
                        OperationResult.succeeded {
                            AutoPolicyThresholdMb = Some 1
                            MaterializeLargeObjects = true
                        }
                })
        SetSettings = Implemented(fun _ _ _ -> async { return OperationResult.succeeded () })
    }

    let private finalPath = repositoryPath "portable.txt"

    let private finalTargetRef: LogicalRef = {
        Name = "fake-target"
        ProviderRef = providerRef "fake-target"
        Kind = RemoteRef
        IsCurrent = false
    }

    let private finalConflictItem: ConflictItem = {
        Path = finalPath
        Candidates = [||]
        CombinedPreview = Some(TextPreview "combined preview")
        SupportsResolvedContent = true
    }

    let private finalObjectState: ObjectState = {
        Path = finalPath
        IsMaterialized = true
        IsLocallyAvailable = true
        SizeBytes = None
        ObjectId = None
    }

    let private noSynchronization: SynchronizationFunctions = {
        Refresh = Missing noFunction
        PreviewUpdate = Missing noFunction
        Update = Missing noFunction
        Publish = Missing noFunction
        Synchronize = Missing noFunction
    }

    let private noConflictResolution: ConflictResolutionFunctions = {
        GetActiveSession = Missing noFunction
        Resolve = Missing noFunction
        Finalize = Missing noFunction
        Cancel = Missing noFunction
    }

    let private noTextDiff: TextDiffFunctions = {
        Open = Missing noFunction
        ReadPage = Missing noFunction
        ReplayPage = Missing noFunction
        Expand = Missing noFunction
        ReadLine = Missing noFunction
        GetSourceInfo = Missing noFunction
        Close = Missing noFunction
    }

    let private noHistory: HistoryFunctions = {
        ListRevisions = Missing noFunction
        GetRevisionChanges = Missing noFunction
    }

    let private noRepositoryBrowser: RepositoryBrowserFunctions = { GetRepositoryWebUrl = Missing noFunction }

    let private noObjectMaterialization: ObjectMaterializationFunctions = {
        ListObjects = Missing noFunction
        Materialize = Missing noFunction
        Dematerialize = Missing noFunction
    }

    let private noStoragePolicy: StoragePolicyFunctions = {
        SetPathPolicy = Missing noFunction
        GetSettings = Missing noFunction
        SetSettings = Missing noFunction
    }

    let private noMaintenance: MaintenanceFunctions = {
        Prune = Missing noFunction
        Deduplicate = Missing noFunction
        CanDeduplicate = Missing noFunction
    }

    let createFinalSynchronization () : SynchronizationFunctions =
        let state = {
            BaseRevision = None
            LocalRevision = None
            TargetRevision = None
            TargetRef = Some finalTargetRef
            LocalRevisionCount = None
            TargetRevisionCount = None
            RemoteChangedPaths = None
            Relationship = UpToDate
        }

        {
            noSynchronization with
                Refresh = Implemented(fun _ _ -> async { return OperationResult.succeeded state })
                PreviewUpdate = Missing previewUpdateReason
        }

    let createFinalConflictResolution () : ConflictResolutionFunctions = {
        noConflictResolution with
            GetActiveSession =
                Implemented(fun _ _ ->
                    async {
                        return
                            OperationResult.succeeded (
                                Some {
                                    Handle = {
                                        SessionId = "fake-conflict-session"
                                        Version = "1"
                                    }
                                    Items = [| finalConflictItem |]
                                }
                            )
                    })
    }

    let createFinalObjectMaterialization () : ObjectMaterializationFunctions = {
        noObjectMaterialization with
            ListObjects = Implemented(fun _ _ -> async { return OperationResult.succeeded [| finalObjectState |] })
    }

    let fakeLocation (id: ProviderId) : RepositoryLocation = {
        ProviderId = id
        DisplayName = None
        ProviderLocation = "fake://repository"
        ConnectionProfileId = None
    }

    let private repositoryOf (id: ProviderId) (root: string) (location: RepositoryLocation option) : Repository =
        let descriptor: RepositoryDescriptor = {
            ProviderId = id
            RepositoryRoot = root
            Location = location
        }

        FakeRepository descriptor

    /// A minimal well-behaved in-memory provider for contract-shape tests: the repository-opening functions
    /// and the eight core functions. Every group is missing. GetStatus refuses a repository that another
    /// provider opened.
    let createFunctions (id: ProviderId) : ProviderFunctions =
        let mutable version = 1
        let mutable revisionCounter = 0
        let currentToken () = $"fake-token-{version}"

        let status () = {
            CurrentRef = None
            RepositoryVersion = currentToken ()
            Changes = [||]
            ActiveConflictSession = None
            Synchronization = None
        }

        {
            VerifyLocation =
                Implemented(fun request _ ->
                    async {
                        return
                            OperationResult.succeeded {
                                Location = request.Location
                                GrantedIntents = request.Intents
                                DeniedIntents = [||]
                            }
                    })
            Initialize =
                Implemented(fun request _ ->
                    async {
                        let location = request.Location |> Option.defaultValue (fakeLocation id)
                        return OperationResult.succeeded (repositoryOf id request.TargetPath (Some location))
                    })
            Clone =
                Implemented(fun request _ ->
                    async { return OperationResult.succeeded (repositoryOf id request.TargetPath (Some request.Location)) })
            Bind =
                Implemented(fun request _ ->
                    async {
                        return OperationResult.succeeded (repositoryOf id request.RepositoryRoot (Some request.Location))
                    })
            Open =
                Implemented(fun request _ ->
                    async { return OperationResult.succeeded (repositoryOf id request.RepositoryRoot None) })
            CheckDependencies = Implemented(fun _ -> async { return OperationResult.succeeded [||] })
            InstallDependency = Missing noFunction
            GetStatus =
                Implemented(fun repository _ ->
                    async {
                        match repository with
                        | :? FakeRepository -> return OperationResult.succeeded (status ())
                        | _ -> return OperationResult.failed (Repository.ofAnotherProvider repository)
                    })
            ListRefs = Implemented(fun _ _ -> async { return OperationResult.succeeded [||] })
            CreateRef =
                Implemented(fun _ request _ ->
                    async {
                        version <- version + 1

                        return
                            OperationResult.succeeded {
                                Name = request.Name
                                ProviderRef = providerRef request.Name
                                Kind = LocalRef
                                IsCurrent = request.SwitchTo
                            }
                    })
            PreflightSwitchRef =
                Implemented(fun _ _ _ -> async { return OperationResult.succeeded { PathsAtRisk = [||]; IsSafe = true } })
            SwitchRef =
                Implemented(fun _ _ _ ->
                    async {
                        version <- version + 1
                        return OperationResult.succeeded (status ())
                    })
            CreateRevision =
                Implemented(fun _ request _ ->
                    async {
                        if request.ExpectedRepositoryVersion <> currentToken () then
                            return
                                OperationResult.failed (
                                    OperationFailure.create Concurrency "precondition_failed" "Stale workspace version."
                                )
                        else
                            version <- version + 1
                            revisionCounter <- revisionCounter + 1

                            match RevisionId.tryCreate $"fake-rev-{revisionCounter}" with
                            | Ok revisionId -> return OperationResult.succeeded revisionId
                            | Error message -> return failwith message
                    })
            RestorePaths =
                Implemented(fun _ _ _ ->
                    async {
                        version <- version + 1
                        return OperationResult.succeeded ()
                    })
            GetDiffSummary = Implemented(fun _ _ -> async { return OperationResult.succeeded { Entries = [||] } })
            Synchronization = noSynchronization
            ConflictResolution = noConflictResolution
            TextDiff = noTextDiff
            History = noHistory
            RepositoryBrowser = noRepositoryBrowser
            ObjectMaterialization = noObjectMaterialization
            StoragePolicy = noStoragePolicy
            Maintenance = noMaintenance
        }

    /// A synchronization state of a repository without a target.
    let noTargetState: SynchronizationState = {
        BaseRevision = None
        LocalRevision = None
        TargetRevision = None
        TargetRef = None
        LocalRevisionCount = None
        TargetRevisionCount = None
        RemoteChangedPaths = None
        Relationship = NoTarget
    }

    let createSynchronization () : SynchronizationFunctions =
        let state = noTargetState

        {
            Refresh = Implemented(fun _ _ -> async { return OperationResult.succeeded state })
            PreviewUpdate =
                Implemented(fun _ _ ->
                    async {
                        return
                            OperationResult.succeeded {
                                ChangedPaths = [||]
                                OverlappingPaths = [||]
                                PredictedConflictPaths = None
                                HasDataLossRisk = false
                                WouldCreateConflictSession = false
                            }
                    })
            Update =
                Implemented(fun _ _ _ -> async { return OperationResult.unchanged (Some "no target configured") state })
            Publish =
                Implemented(fun _ _ _ -> async { return OperationResult.unchanged (Some "no target configured") state })
            Synchronize =
                Implemented(fun _ _ _ -> async { return OperationResult.unchanged (Some "no target configured") state })
        }

    let createBrowser () : RepositoryBrowserFunctions = {
        GetRepositoryWebUrl = Implemented(fun _ _ -> async { return OperationResult.succeeded None })
    }

    /// A provider built from a list, with no folder it detects and nothing to release on close.
    let create (functions: ProviderFunctions) : IVersionControl =
        ListedProvider((fun _ -> async { return NotDetected }), (fun _ -> async.Return()), functions)

/// Awaits a call through the interface and fails the test when the provider answered NoOp.
let executed (call: Async<CallResult<'T>>) : Async<OperationResult<'T>> =
    async {
        match! call with
        | Executed result -> return result
        | NoOp reason -> return failtest $"The provider did not run the call: {reason}"
    }

let private expectSucceeded (operationName: string) (result: OperationResult<'T>) : 'T =
    match result with
    | Succeeded outcome -> outcome.Value
    | PartiallySucceeded _ -> failtest $"{operationName} unexpectedly returned partial success."
    | Failed failure -> failtest $"{operationName} failed ({failure.Category}/{failure.Code}): {failure.Message}"

/// The consumer-side feature discovery pattern: read the provider's entries once and enable the UI for
/// each group whose first function is there.
let private discoverFeatures (provider: IVersionControl) =
    let isThere entry =
        match entry with
        | Implemented _ -> true
        | Missing _ -> false

    match Subscribe.all provider with
    | Error message -> failtest message
    | Ok functions ->
        [
            if isThere functions.Synchronization.Refresh then
                "synchronization"
            if isThere functions.TextDiff.Open then
                "text-diff"
            if isThere functions.ConflictResolution.GetActiveSession then
                "conflicts"
            if isThere functions.ObjectMaterialization.ListObjects then
                "materialization"
            if isThere functions.StoragePolicy.SetPathPolicy then
                "storage-policy"
            if isThere functions.Maintenance.Prune then
                "maintenance"
            if isThere functions.RepositoryBrowser.GetRepositoryWebUrl then
                "browser"
            if isThere functions.History.ListRevisions then
                "history"
        ]

let private openRepository (provider: IVersionControl) =
    async {
        let context = OperationContext.detached "contract-shape"
        let location = FakeProvider.fakeLocation (FakeProvider.providerId "fake.workspace")

        let! bindResult =
            executed (
                provider.Bind
                    {
                        RepositoryRoot = "/fake/workspace"
                        Location = location
                    }
                    context
            )

        return expectSucceeded "bind" bindResult
    }

/// A repository class of a second fake provider.
type private OtherRepository(descriptor: RepositoryDescriptor) =
    inherit Repository(descriptor)

/// A provider with some functions and without others: the only synchronization function it has is Refresh,
/// which records its run in the log.
let private createPartialProvider (log: ResizeArray<string>) : IVersionControl =
    let synchronization = {
        FakeProvider.createFinalSynchronization () with
            Refresh =
                Implemented(fun _ _ ->
                    async {
                        log.Add "refresh"
                        return OperationResult.succeeded FakeProvider.noTargetState
                    })
    }

    FakeProvider.create {
        FakeProvider.createFunctions (FakeProvider.providerId "fake.partial") with
            Synchronization = synchronization
    }

[<Tests>]
let contractShapeTests =
    testList "ContractShapes" [
        testCaseAsync "typed provider fakes retain byte-scale progress and storage settings"
        <| async {
            Expect.equal FakeProvider.progress.Completed (Some 3_000_000_000.0) "Completed progress is not limited to 32 bits."
            Expect.equal FakeProvider.progress.Total (Some 4_000_000_000.0) "Total progress is not limited to 32 bits."

            let context = OperationContext.detached "storage-policy-settings"

            let provider =
                FakeProvider.create {
                    FakeProvider.createFunctions (FakeProvider.providerId "fake.storage") with
                        StoragePolicy = FakeProvider.storagePolicy
                }

            let! repository = openRepository provider
            let! settingsResult = executed (provider.StoragePolicy.GetSettings repository context)
            let settings = expectSucceeded "get settings" settingsResult

            Expect.equal settings.AutoPolicyThresholdMb (Some 1) "The fake returns typed storage settings."
            Expect.isTrue settings.MaterializeLargeObjects "The fake returns its materialization setting."
        }

        testCaseAsync "an opened repository returns all final SPI additions through groups and operation results"
        <| async {
            let id = FakeProvider.providerId "fake.final-surface"

            let provider =
                FakeProvider.create {
                    FakeProvider.createFunctions id with
                        ConflictResolution = FakeProvider.createFinalConflictResolution ()
                        Synchronization = FakeProvider.createFinalSynchronization ()
                        ObjectMaterialization = FakeProvider.createFinalObjectMaterialization ()
                }

            let! repository = openRepository provider
            let context = OperationContext.detached "final-service-surface"

            let! summaryResult = executed (provider.ConflictResolution.GetActiveSession repository context)
            let summary = expectSucceeded "get active conflict session" summaryResult
            let conflict = summary |> Option.defaultWith (fun () -> failtest "Expected an active conflict session.")
            Expect.equal conflict.Items[0].CombinedPreview (Some(TextPreview "combined preview")) "Combined preview comes from the session result."

            let! stateResult = executed (provider.Synchronization.Refresh repository context)
            let state = expectSucceeded "refresh synchronization" stateResult
            Expect.equal (state.TargetRef |> Option.map _.Name) (Some "fake-target") "Target ref comes from the refresh result."

            let! objectsResult = executed (provider.ObjectMaterialization.ListObjects repository context)
            let objectState = expectSucceeded "list objects" objectsResult |> Array.exactlyOne
            Expect.isTrue objectState.IsLocallyAvailable "Local availability comes from the object-list result."
        }

        testCaseAsync "a core-only provider has no group function"
        <| async {
            let id = FakeProvider.providerId "fake.coreonly"
            let provider = FakeProvider.create (FakeProvider.createFunctions id)
            let! repository = openRepository provider

            Expect.equal (discoverFeatures provider) [] "No group is discovered."

            let context = OperationContext.detached "core-only-status"
            let! statusResult = executed (provider.GetStatus repository context)
            let status = expectSucceeded "core status" statusResult

            Expect.equal status.RepositoryVersion "fake-token-1" "The core works without any group."
        }

        testCaseAsync "a synchronization provider needs no object-storage group"
        <| async {
            let id = FakeProvider.providerId "fake.sync"

            let provider =
                FakeProvider.create {
                    FakeProvider.createFunctions id with
                        Synchronization = FakeProvider.createSynchronization ()
                }

            let! repository = openRepository provider

            Expect.equal (discoverFeatures provider) [ "synchronization" ] "Only synchronization is discovered."

            let! refreshResult =
                executed (provider.Synchronization.Refresh repository (OperationContext.detached "sync-refresh"))

            let state = expectSucceeded "refresh" refreshResult
            Expect.equal state.Relationship NoTarget "Truthful state without invented counters."
        }

        testCaseAsync "an unknown external provider ID with one group round-trips"
        <| async {
            let id = FakeProvider.providerId "vendor.example-vcs"

            let provider =
                FakeProvider.create {
                    FakeProvider.createFunctions id with
                        RepositoryBrowser = FakeProvider.createBrowser ()
                }

            let! repository = openRepository provider

            Expect.equal (ProviderId.value repository.Descriptor.ProviderId) "vendor.example-vcs" "External ID round-trips unchanged."
            Expect.equal (discoverFeatures provider) [ "browser" ] "Only the browser group is discovered."
        }

        testCaseAsync "stale workspace versions are rejected with the stable concurrency code"
        <| async {
            let id = FakeProvider.providerId "fake.concurrency"
            let provider = FakeProvider.create (FakeProvider.createFunctions id)

            let! repository = openRepository provider
            let context = OperationContext.detached "stale-mutation"

            let! revisionResult =
                executed (
                    provider.CreateRevision
                        repository
                        {
                            Message = "stale attempt"
                            Paths = [||]
                            ExpectedRepositoryVersion = "fake-stale-revision"
                        }
                        context
                )

            match revisionResult with
            | Failed failure ->
                Expect.equal failure.Category Concurrency "Stale mutations fail with Concurrency."
                Expect.equal failure.Code "precondition_failed" "The stable code is precondition_failed."
            | Succeeded _
            | PartiallySucceeded _ -> failtest "Expected the stale mutation to fail."
        }

        testCaseAsync "a missing function answers NoOp with the reason of its entry and runs nothing"
        <| async {
            let log = ResizeArray<string>()
            let provider = createPartialProvider log
            let! repository = openRepository provider

            let! result =
                provider.Synchronization.PreviewUpdate repository (OperationContext.detached "missing-function")

            match result with
            | NoOp reason -> Expect.equal reason FakeProvider.previewUpdateReason "The reason is the one of the entry."
            | Executed _ -> failtest "Expected NoOp for a missing function."

            Expect.isEmpty log "No function of the provider ran."
        }

        testCaseAsync "subscribing to a missing function fails with the reason of its entry"
        <| async {
            let provider = createPartialProvider (ResizeArray<string>())

            match Subscribe.single (fun functions -> functions.Synchronization.PreviewUpdate) provider with
            | Error reason -> Expect.equal reason FakeProvider.previewUpdateReason "The reason is the one of the entry."
            | Ok _ -> failtest "Expected an error for a missing function."
        }

        testCaseAsync "subscribing to a function the provider has gives the function and its direct result"
        <| async {
            let log = ResizeArray<string>()
            let provider = createPartialProvider log
            let! repository = openRepository provider

            match Subscribe.single (fun functions -> functions.Synchronization.Refresh) provider with
            | Error reason -> failtest reason
            | Ok refresh ->
                let! result = refresh repository (OperationContext.detached "subscribed-refresh")
                let state = expectSucceeded "refresh" result
                Expect.equal state.Relationship NoTarget "The result is the one of the provider function, without a wrapper."
                Expect.equal (List.ofSeq log) [ "refresh" ] "The provider function ran once."
        }

        testCaseAsync "subscribing to everything shows which functions the provider has"
        <| async {
            let provider = createPartialProvider (ResizeArray<string>())

            match Subscribe.all provider with
            | Error message -> failtest message
            | Ok functions ->
                match functions.Synchronization.Refresh, functions.Synchronization.PreviewUpdate with
                | Implemented _, Missing reason -> Expect.equal reason FakeProvider.previewUpdateReason "A missing entry carries its reason."
                | _ -> failtest "Expected Refresh to be there and PreviewUpdate to be missing."

                match functions.InstallDependency with
                | Missing reason -> Expect.equal reason FakeProvider.noFunction "A missing entry carries its reason."
                | Implemented _ -> failtest "Expected InstallDependency to be missing."
        }

        testCaseAsync "a repository of another provider is refused with the stable validation code"
        <| async {
            let provider = FakeProvider.create (FakeProvider.createFunctions (FakeProvider.providerId "fake.own"))
            let otherId = FakeProvider.providerId "fake.other"

            let other =
                FakeProvider.create {
                    FakeProvider.createFunctions otherId with
                        Open =
                            Implemented(fun request _ ->
                                async {
                                    let descriptor: RepositoryDescriptor = {
                                        ProviderId = otherId
                                        RepositoryRoot = request.RepositoryRoot
                                        Location = None
                                    }

                                    return OperationResult.succeeded (OtherRepository descriptor :> Repository)
                                })
                }

            let context = OperationContext.detached "other-provider"

            let! openResult =
                executed (
                    other.Open
                        {
                            RepositoryRoot = "/other/workspace"
                            ConnectionProfileId = None
                        }
                        context
                )

            let foreign = expectSucceeded "open" openResult

            match! provider.GetStatus foreign context with
            | Executed(Failed failure) ->
                Expect.equal failure.Code "repository_of_another_provider" "The stable code names the problem."
                Expect.equal failure.Category Validation "The refusal is a validation failure."
                Expect.isFalse failure.Retryable "Handing the same repository again fails again."
                Expect.stringContains failure.Message "fake.other" "The message names the provider of the repository."
            | _ -> failtest "Expected a failed result for a repository of another provider."
        }

        testCase "subscribing to an interface that was not built from a list gives an error"
        <| fun () ->
            let notImplemented () = failwith "Not implemented."

            let alien =
                { new IVersionControl with
                    member _.Probe _ = notImplemented ()
                    member _.VerifyLocation _ _ = notImplemented ()
                    member _.Initialize _ _ = notImplemented ()
                    member _.Clone _ _ = notImplemented ()
                    member _.Bind _ _ = notImplemented ()
                    member _.Open _ _ = notImplemented ()
                    member _.CheckDependencies _ = notImplemented ()
                    member _.InstallDependency _ _ = notImplemented ()
                    member _.GetStatus _ _ = notImplemented ()
                    member _.ListRefs _ _ = notImplemented ()
                    member _.CreateRef _ _ _ = notImplemented ()
                    member _.PreflightSwitchRef _ _ _ = notImplemented ()
                    member _.SwitchRef _ _ _ = notImplemented ()
                    member _.CreateRevision _ _ _ = notImplemented ()
                    member _.RestorePaths _ _ _ = notImplemented ()
                    member _.GetDiffSummary _ _ = notImplemented ()
                    member _.Close _ = notImplemented ()
                    member _.Synchronization = notImplemented ()
                    member _.ConflictResolution = notImplemented ()
                    member _.TextDiff = notImplemented ()
                    member _.History = notImplemented ()
                    member _.RepositoryBrowser = notImplemented ()
                    member _.ObjectMaterialization = notImplemented ()
                    member _.StoragePolicy = notImplemented ()
                    member _.Maintenance = notImplemented () }

            match Subscribe.all alien with
            | Error _ -> ()
            | Ok _ -> failtest "Expected an error from Subscribe.all."

            match Subscribe.single (fun functions -> functions.GetStatus) alien with
            | Error _ -> ()
            | Ok _ -> failtest "Expected an error from Subscribe.single."
    ]
