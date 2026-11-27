module VersionControlService.FableConsumer.Tests.PortableConsumer

open Fable.Core
open VersionControlService.Abstractions
open Vitest

let private fakeProviderId =
    match ProviderId.tryCreate "fake.portable" with
    | Ok providerId -> providerId
    | Error message -> failwith message

let private fakeLocation: RepositoryLocation = {
    ProviderId = fakeProviderId
    DisplayName = None
    ProviderLocation = "fake://repository"
    ConnectionProfileId = None
}

let private fakePath =
    match RepositoryPath.tryCreate "portable.txt" with
    | Ok path -> path
    | Error message -> failwith message

let private fakeProviderRef =
    match ProviderRef.tryCreate "fake-portable-target" with
    | Ok reference -> reference
    | Error message -> failwith message

let private fakeTargetRef: LogicalRef = {
    Name = "portable-target"
    ProviderRef = fakeProviderRef
    Kind = RemoteRef
    IsCurrent = false
}

let private fakeSynchronizationState: SynchronizationState = {
    BaseRevision = None
    LocalRevision = None
    TargetRevision = None
    TargetRef = Some fakeTargetRef
    LocalRevisionCount = None
    TargetRevisionCount = None
    RemoteChangedPaths = None
    Relationship = UpToDate
}

let private fakeConflictItem: ConflictItem = {
    Path = fakePath
    Candidates = [||]
    CombinedPreview = Some(TextPreview "<<<<<<< local\n=======\n>>>>>>> target\n")
    SupportsResolvedContent = true
}

let private fakeObjectState: ObjectState = {
    Path = fakePath
    IsMaterialized = false
    IsLocallyAvailable = true
    SizeBytes = None
    ObjectId = None
}

let private noFunction = "The fake provider does not have this function."

let private previewUpdateReason = "The fake provider cannot preview an update."

type private FakeRepository(descriptor: RepositoryDescriptor) =
    inherit Repository(descriptor)

let private createFakeSynchronization () : SynchronizationFunctions = {
    Refresh = Implemented(fun _ _ -> async { return OperationResult.succeeded fakeSynchronizationState })
    PreviewUpdate = Missing previewUpdateReason
    Update = Missing noFunction
    Publish = Missing noFunction
    Synchronize = Missing noFunction
}

let private createFakeConflictResolution () : ConflictResolutionFunctions = {
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
                            Items = [| fakeConflictItem |]
                        }
                    )
            })
    Resolve = Missing noFunction
    Finalize = Missing noFunction
    Cancel = Missing noFunction
}

let private createFakeObjectMaterialization () : ObjectMaterializationFunctions = {
    ListObjects = Implemented(fun _ _ -> async { return OperationResult.succeeded [| fakeObjectState |] })
    Materialize = Missing noFunction
    Dematerialize = Missing noFunction
}

/// The list of a fake provider whose diff summary is a 50-step long operation reporting progress each step
/// and observing cancellation between steps.
let private createFakeFunctions () : ProviderFunctions =
    let status = {
        CurrentRef = None
        RepositoryVersion = "fake-revision-1"
        Changes = [||]
        ActiveConflictSession = None
        Synchronization = None
    }

    {
        VerifyLocation = Missing noFunction
        Initialize = Missing noFunction
        Clone = Missing noFunction
        Bind =
            Implemented(fun request _ ->
                async {
                    let descriptor: RepositoryDescriptor = {
                        ProviderId = fakeProviderId
                        RepositoryRoot = request.RepositoryRoot
                        Location = Some request.Location
                    }

                    return OperationResult.succeeded (FakeRepository descriptor :> Repository)
                })
        Open = Missing noFunction
        CheckDependencies = Implemented(fun _ -> async { return OperationResult.succeeded [||] })
        InstallDependency = Missing noFunction
        GetStatus = Implemented(fun _ _ -> async { return OperationResult.succeeded status })
        ListRefs = Implemented(fun _ _ -> async { return OperationResult.succeeded [||] })
        CreateRef = Missing noFunction
        PreflightSwitchRef = Missing noFunction
        SwitchRef = Missing noFunction
        CreateRevision = Missing noFunction
        RestorePaths = Missing noFunction
        GetDiffSummary =
            Implemented(fun _ context -> async {
                let mutable canceled = false
                let mutable step = 0

                while not canceled && step < 50 do
                    do! Async.Sleep 1
                    step <- step + 1

                    context.ReportProgress {
                        PhaseCode = "fake-step"
                        Item = None
                        Completed = Some(float step)
                        Total = Some 50.0
                        DisplayMessage = None
                    }

                    if context.Cancellation.IsCancellationRequested() then
                        canceled <- true

                if canceled then
                    return OperationResult.canceled $"The fake operation stopped after {step} steps."
                else
                    return OperationResult.succeeded { Entries = [||] }
            })
        Synchronization = createFakeSynchronization ()
        ConflictResolution = createFakeConflictResolution ()
        TextDiff = {
            Open = Missing noFunction
            ReadPage = Missing noFunction
            ReplayPage = Missing noFunction
            Expand = Missing noFunction
            ReadLine = Missing noFunction
            GetSourceInfo = Missing noFunction
            Close = Missing noFunction
        }
        History = {
            ListRevisions = Missing noFunction
            GetRevisionChanges = Missing noFunction
        }
        RepositoryBrowser = { GetRepositoryWebUrl = Missing noFunction }
        ObjectMaterialization = createFakeObjectMaterialization ()
        StoragePolicy = {
            SetPathPolicy = Missing noFunction
            GetSettings = Missing noFunction
            SetSettings = Missing noFunction
        }
        Maintenance = {
            Prune = Missing noFunction
            Deduplicate = Missing noFunction
            CanDeduplicate = Missing noFunction
        }
    }

let private createProvider (functions: ProviderFunctions) : IVersionControl =
    ListedProvider((fun _ -> async { return NotDetected }), (fun _ -> async.Return()), functions)

let private createFakeProvider () : IVersionControl = createProvider (createFakeFunctions ())

/// A provider whose only synchronization function is Refresh, which records its run in the log.
let private createLoggingProvider (log: ResizeArray<string>) : IVersionControl =
    let functions = createFakeFunctions ()

    createProvider {
        functions with
            Synchronization = {
                functions.Synchronization with
                    Refresh =
                        Implemented(fun _ _ ->
                            async {
                                log.Add "refresh"
                                return OperationResult.succeeded fakeSynchronizationState
                            })
            }
    }

let private executed (call: Async<CallResult<'T>>) : Async<OperationResult<'T>> =
    async {
        let! result = call

        match result with
        | Executed operationResult -> return operationResult
        | NoOp reason -> return failwith $"The provider did not run the call: {reason}"
    }

let private expectSucceeded (operationName: string) (result: OperationResult<'T>) : 'T =
    match result with
    | Succeeded outcome -> outcome.Value
    | PartiallySucceeded _ -> failwith $"{operationName} unexpectedly returned partial success."
    | Failed failure -> failwith $"{operationName} failed ({failure.Category}/{failure.Code}): {failure.Message}"

let private openFakeRepository (provider: IVersionControl) (context: OperationContext) =
    async {
        let! bindResult =
            executed (
                provider.Bind
                    {
                        RepositoryRoot = "/fake/workspace"
                        Location = fakeLocation
                    }
                    context
            )

        return expectSucceeded "bind" bindResult
    }

Vitest.describe (
    "Portable abstractions consumer (Fable)",
    fun () ->
        Vitest.test (
            "a Fable consumer awaits one core operation through the Async carrier",
            fun () -> promise {
                let workflow = async {
                    let provider = createFakeProvider ()
                    let context = OperationContext.detached "portable-open"

                    let! repository = openFakeRepository provider context
                    let! statusResult = executed (provider.GetStatus repository context)
                    return expectSucceeded "get status" statusResult
                }

                let! status = Async.StartAsPromise workflow
                Vitest.expect(status.RepositoryVersion).toBe ("fake-revision-1")
            }
        )

        Vitest.test (
            "cancellation reaches and stops a Promise-backed fake long operation",
            fun () -> promise {
                let provider = createFakeProvider ()
                let source = OperationCancellation.Source()
                let mutable lastCompleted = 0.0

                // Deterministic cancellation: cancel from the progress callback at step 3.
                let context =
                    OperationContext.create "portable-cancel" source.Cancellation (fun progress ->
                        match progress.Completed with
                        | Some completed ->
                            lastCompleted <- completed

                            if completed = 3.0 then
                                source.Cancel()
                        | None -> ())

                let workflow = async {
                    let! repository = openFakeRepository provider context
                    return! executed (provider.GetDiffSummary repository context)
                }

                let! diffResult = Async.StartAsPromise workflow

                match diffResult with
                | Failed failure ->
                    Vitest.expect(failure.Category).toEqual (Canceled)
                    Vitest.expect(failure.Code).toBe ("operation_canceled")
                    Vitest.expect(failure.StateChanged).toBe (false)
                | Succeeded _
                | PartiallySucceeded _ -> failwith "Expected the canceled operation to fail."

                Vitest.expect(lastCompleted < 50).toBe (true)
            }
        )

        Vitest.test (
            "progress callbacks flow through the operation context in Fable",
            fun () -> promise {
                let provider = createFakeProvider ()
                let progressPhases = ResizeArray<string>()

                let context =
                    OperationContext.create "portable-progress" OperationCancellation.none (fun progress ->
                        progressPhases.Add progress.PhaseCode)

                let workflow = async {
                    let! repository = openFakeRepository provider context
                    return! executed (provider.GetDiffSummary repository context)
                }

                let! diffResult = Async.StartAsPromise workflow
                expectSucceeded "get diff summary" diffResult |> ignore

                Vitest.expect(progressPhases.Count).toBe (50)
                Vitest.expect(progressPhases |> Seq.forall (fun phase -> phase = "fake-step")).toBe (true)
            }
        )

        Vitest.test (
            "final SPI additions round-trip through the portable consumer",
            fun () -> promise {
                let provider = createFakeProvider ()
                let context = OperationContext.detached "portable-final-spi"

                let workflow = async {
                    let! repository = openFakeRepository provider context

                    let! summaryResult = executed (provider.ConflictResolution.GetActiveSession repository context)
                    let summary = expectSucceeded "get active conflict session" summaryResult

                    let conflict =
                        summary |> Option.defaultWith (fun () -> failwith "Expected an active fake conflict session.")

                    let! stateResult = executed (provider.Synchronization.Refresh repository context)
                    let state = expectSucceeded "refresh synchronization" stateResult

                    let! objectsResult = executed (provider.ObjectMaterialization.ListObjects repository context)
                    let objectState = expectSucceeded "list objects" objectsResult |> Array.exactlyOne

                    return conflict.Items[0].CombinedPreview, state.TargetRef, objectState.IsLocallyAvailable
                }

                let! combinedPreview, targetRef, isLocallyAvailable = Async.StartAsPromise workflow

                Vitest.expect(combinedPreview).toEqual (Some(TextPreview "<<<<<<< local\n=======\n>>>>>>> target\n"))
                Vitest.expect(targetRef).toEqual (Some fakeTargetRef)
                Vitest.expect(isLocallyAvailable).toBe (true)
            }
        )

        Vitest.test (
            "a missing function answers NoOp with the reason of its entry and runs nothing",
            fun () -> promise {
                let log = ResizeArray<string>()
                let provider = createLoggingProvider log
                let context = OperationContext.detached "portable-missing-function"

                let workflow = async {
                    let! repository = openFakeRepository provider context
                    return! provider.Synchronization.PreviewUpdate repository context
                }

                let! result = Async.StartAsPromise workflow

                match result with
                | NoOp reason -> Vitest.expect(reason).toBe (previewUpdateReason)
                | Executed _ -> failwith "Expected NoOp for a missing function."

                Vitest.expect(log.Count).toBe (0)
            }
        )

        Vitest.test (
            "subscribing to a missing function fails with the reason of its entry",
            fun () -> promise {
                let provider = createLoggingProvider (ResizeArray<string>())

                match Subscribe.single (fun functions -> functions.Synchronization.PreviewUpdate) provider with
                | Error reason -> Vitest.expect(reason).toBe (previewUpdateReason)
                | Ok _ -> failwith "Expected an error for a missing function."
            }
        )

        Vitest.test (
            "subscribing to a function the provider has gives the function and its direct result",
            fun () -> promise {
                let log = ResizeArray<string>()
                let provider = createLoggingProvider log
                let context = OperationContext.detached "portable-subscribed-refresh"

                let workflow = async {
                    let! repository = openFakeRepository provider context

                    match Subscribe.single (fun functions -> functions.Synchronization.Refresh) provider with
                    | Error reason -> return failwith reason
                    | Ok refresh ->
                        let! result = refresh repository context
                        return expectSucceeded "refresh" result
                }

                let! state = Async.StartAsPromise workflow

                Vitest.expect(state).toEqual (fakeSynchronizationState)
                Vitest.expect(log.Count).toBe (1)
            }
        )
)
