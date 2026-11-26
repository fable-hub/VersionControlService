module VersionControlService.Tests.Contracts.ProviderHarness

open System
open Fable.Core
open VersionControlService.Abstractions

/// A mutation applied to the shared target as another authorized client would:
/// Some content writes/overwrites the object, None deletes it.
type TargetMutation = {
    Path: string
    Content: string option
}

/// One isolated local workspace with an open repository of the provider.
type HarnessWorkspace = {
    Provider: IVersionControl
    Repository: Repository
    /// Repository-relative UTF-8 file IO against the local workspace.
    WriteFile: string -> string -> JS.Promise<unit>
    ReadFile: string -> JS.Promise<string option>
    RemoveFile: string -> JS.Promise<unit>
    /// True when the local store aliases case-different names (case-insensitive filesystem).
    LocalFileSystemAliasesCase: bool
    /// True when the local store aliases canonically equivalent NFC/NFD names.
    LocalFileSystemAliasesNormalization: bool
    /// True when the local store enforces Windows naming rules (reserved names, <>:"|?* etc.).
    LocalFileSystemWindowsRules: bool
}

/// Everything a provider supplies so the shared conformance suites can run against it.
/// Real harnesses own isolated repository creation, remote mutation, credential
/// variants, deterministic barriers, and cleanup.
type ProviderTestHarness = {
    /// Exact describe prefix: "fake", "Git", or "lakeFS".
    Name: string
    Provider: IVersionControl
    /// Names of the groups of functions this provider is expected to have.
    ExpectedServices: string list
    /// Fresh, empty, authorized repository location for provisioning tests.
    CreateLocation: unit -> JS.Promise<RepositoryLocation>
    /// A location the configured credentials cannot write to.
    CreateUnauthorizedLocation: unit -> JS.Promise<RepositoryLocation>
    /// Fresh empty local directory path.
    CreateLocalPath: unit -> JS.Promise<string>
    /// Isolated workspace with one base revision (containing base.txt) and an open repository.
    CreateWorkspace: unit -> JS.Promise<HarnessWorkspace>
    /// Second workspace bound to the SAME target as `anchor`, with its own connection profile.
    CreateLinkedWorkspace: HarnessWorkspace -> JS.Promise<HarnessWorkspace>
    /// A second independent repository object over the SAME local workspace.
    OpenSecondRepository: HarnessWorkspace -> JS.Promise<Repository>
    /// Applies mutations to the workspace's target as another authorized client (commit + publish there).
    AdvanceTarget: HarnessWorkspace -> TargetMutation[] -> JS.Promise<unit>
    /// Creates a new ref whose tip contains exactly the given (path, content) pairs on
    /// top of the base revision, without touching the local workspace files.
    SeedRevisionOnNewRef: HarnessWorkspace -> string -> (string * string)[] -> JS.Promise<ProviderRef>
    /// Makes Publish fail recoverably until RestorePublish runs.
    BreakPublish: HarnessWorkspace -> JS.Promise<unit>
    RestorePublish: HarnessWorkspace -> JS.Promise<unit>
    /// Arms a deterministic destination advance between the next verified mutation's
    /// pre-check and its post-operation verification.
    ArmDestinationRace: HarnessWorkspace -> TargetMutation[] -> JS.Promise<unit>
    /// Arms a deterministic pause in the next transfer-heavy operation so cancellation
    /// can interrupt it; the operation reports progress while paused.
    ArmSlowTransfer: HarnessWorkspace -> JS.Promise<unit>
    Cleanup: unit -> JS.Promise<unit>
}

/// Stable failure codes shared by the suites.
module ConformanceCodes =

    [<Literal>]
    let PreconditionFailed = "precondition_failed"

    [<Literal>]
    let PathCollision = "path_collision"

    [<Literal>]
    let UnrepresentablePath = "unrepresentable_path"

    [<Literal>]
    let ConflictsDetected = "conflicts_detected"

    [<Literal>]
    let TargetNotEmpty = "target_not_empty"

/// Shared redaction property: no failure message, detail, or progress output may
/// contain likely credential material.
let assertRedacted (text: string) =
    if not (isNull text) then
        let lowered = text.ToLowerInvariant()

        if lowered.Contains "bearer " && not (lowered.Contains "[redacted]") then
            failwith $"Credential material leaked (bearer token): {text}"

        if
            System.Text.RegularExpressions.Regex.IsMatch(text, "://[^/@\\s]*:[^/@\\s]+@")
            && not (lowered.Contains "[redacted]")
        then
            failwith $"Credential material leaked (URL credentials): {text}"

/// Helpers shared by every conformance suite module.
module SuiteHelpers =

    open Vitest

    let run (operation: Async<'T>) : JS.Promise<'T> = Async.StartAsPromise operation

    let ctx (name: string) = OperationContext.detached name

    let mkPath (value: string) =
        match RepositoryPath.tryCreate value with
        | Ok path -> path
        | Error message -> failwith message

    let pathValues (paths: RepositoryPath[]) =
        paths |> Array.map RepositoryPath.value |> Array.sort

    let expectOutcome (operationName: string) (result: OperationResult<'T>) : OperationOutcome<'T> =
        match result with
        | Succeeded outcome -> outcome
        | PartiallySucceeded(_, failure) ->
            failwith $"{operationName} unexpectedly returned partial success ({failure.Code})."
        | Failed failure -> failwith $"{operationName} failed ({failure.Category}/{failure.Code}): {failure.Message}"

    let expectValue (operationName: string) (result: OperationResult<'T>) : 'T =
        (expectOutcome operationName result).Value

    let expectUnchanged (operationName: string) (result: OperationResult<'T>) : 'T =
        let outcome = expectOutcome operationName result

        match outcome.Effect with
        | Unchanged _ -> outcome.Value
        | Performed -> failwith $"{operationName} was expected to be Unchanged but reported Performed."

    let expectPerformed (operationName: string) (result: OperationResult<'T>) : OperationOutcome<'T> =
        let outcome = expectOutcome operationName result

        match outcome.Effect with
        | Performed -> outcome
        | Unchanged reason -> failwith $"{operationName} was unexpectedly Unchanged ({reason})."

    let expectFailure (operationName: string) (result: OperationResult<'T>) : OperationFailure =
        match result with
        | Failed failure -> failure
        | PartiallySucceeded _
        | Succeeded _ -> failwith $"Expected {operationName} to fail."

    /// The failure carried by either a Failed or PartiallySucceeded result.
    let carriedFailure (operationName: string) (result: OperationResult<'T>) : OperationFailure =
        match result with
        | Failed failure -> failure
        | PartiallySucceeded(_, failure) -> failure
        | Succeeded _ -> failwith $"Expected {operationName} to carry a failure."

    /// Awaits a call through the interface and fails the test with the reason when the provider answered NoOp.
    let executed (call: Async<CallResult<'T>>) : Async<OperationResult<'T>> = async {
        match! call with
        | Executed result -> return result
        | NoOp reason -> return failwith $"The provider did not run the call: {reason}"
    }

    let repositoryStatus (provider: IVersionControl) (repository: Repository) : JS.Promise<RepositoryStatus> = promise {
        let! result = run (executed (provider.GetStatus repository (ctx "suite-status")))
        return expectValue "get status" result
    }

    let getStatus (workspace: HarnessWorkspace) : JS.Promise<RepositoryStatus> =
        repositoryStatus workspace.Provider workspace.Repository

    /// The location the repository of the workspace reports.
    let locationOf (workspace: HarnessWorkspace) : RepositoryLocation =
        match workspace.Repository.Descriptor.Location with
        | Some location -> location
        | None -> failwith "The repository reports no location."

    let changePaths (status: RepositoryStatus) =
        status.Changes
        |> Array.map (fun change -> RepositoryPath.value change.Path)
        |> Array.sort

    /// Feature discovery used across suites: which groups of functions the provider has. A group is there
    /// when all of its entries are Implemented.
    let discoverFeatures (provider: IVersionControl) : string list =
        let isThere (entry: Entry<'call>) =
            match entry with
            | Implemented _ -> true
            | Missing _ -> false

        match Subscribe.all provider with
        | Error message -> failwith message
        | Ok functions ->
            let synchronization = functions.Synchronization
            let conflicts = functions.ConflictResolution
            let textDiff = functions.TextDiff
            let materialization = functions.ObjectMaterialization
            let storagePolicy = functions.StoragePolicy
            let maintenance = functions.Maintenance
            let history = functions.History

            [
                "synchronization",
                [
                    isThere synchronization.Refresh
                    isThere synchronization.PreviewUpdate
                    isThere synchronization.Update
                    isThere synchronization.Publish
                    isThere synchronization.Synchronize
                ]
                "text-diff",
                [
                    isThere textDiff.Open
                    isThere textDiff.ReadPage
                    isThere textDiff.ReplayPage
                    isThere textDiff.Expand
                    isThere textDiff.ReadLine
                    isThere textDiff.GetSourceInfo
                    isThere textDiff.Close
                ]
                "conflicts",
                [
                    isThere conflicts.GetActiveSession
                    isThere conflicts.Resolve
                    isThere conflicts.Finalize
                    isThere conflicts.Cancel
                ]
                "materialization",
                [
                    isThere materialization.ListObjects
                    isThere materialization.Materialize
                    isThere materialization.Dematerialize
                ]
                "storage-policy",
                [
                    isThere storagePolicy.SetPathPolicy
                    isThere storagePolicy.GetSettings
                    isThere storagePolicy.SetSettings
                ]
                "maintenance",
                [
                    isThere maintenance.Prune
                    isThere maintenance.Deduplicate
                    isThere maintenance.CanDeduplicate
                ]
                "browser", [ isThere functions.RepositoryBrowser.GetRepositoryWebUrl ]
                "history", [ isThere history.ListRevisions; isThere history.GetRevisionChanges ]
            ]
            |> List.filter (snd >> List.forall id)
            |> List.map fst
            |> List.sort

    let suiteTestOptions = TestOptions(timeout = 120000)

// ---------------------------------------------------------------------------
// In-memory fake provider: the reference implementation the discovery test runs
// every suite against. It is deliberately well-behaved.
// ---------------------------------------------------------------------------

module FakeHarness =

    [<Emit("$0.normalize('NFC')")>]
    let private normalizeNfc (_text: string) : string = jsNative

    let private mkProviderId name =
        match ProviderId.tryCreate name with
        | Ok id -> id
        | Error message -> failwith message

    let private mkPath value =
        match RepositoryPath.tryCreate value with
        | Ok path -> path
        | Error message -> failwith message

    let private mkRevisionId value =
        match RevisionId.tryCreate value with
        | Ok id -> id
        | Error message -> failwith message

    let private mkProviderRef value =
        match ProviderRef.tryCreate value with
        | Ok reference -> reference
        | Error message -> failwith message

    let fakeId = mkProviderId "fake.reference"

    type FakeRevision = {
        Id: string
        Parents: string list
        Files: Map<string, string>
        Message: string
    }

    /// Shared repository target: an object/revision store plus published refs.
    type FakeTarget = {
        mutable Revisions: Map<string, FakeRevision>
        mutable Refs: Map<string, string>
        mutable PublishBroken: bool
        mutable RaceMutations: TargetMutation[] option
        mutable SlowTransferArmed: bool
        Locked: bool
    }

    /// Globally unique revision IDs so re-targeted workspaces never confuse
    /// revisions from different targets.
    let mutable private globalRevisionCounter = 0

    type FakeConflictItem = {
        ItemPath: string
        BaseContent: string option
        OursContent: string option
        TheirsContent: string option
        mutable ResolvedContent: string option option
    }

    type FakeConflictSession = {
        SessionId: string
        mutable HandleVersion: int
        mutable ConflictItems: FakeConflictItem list
        TheirRevisionId: string
        PreUpdateFiles: Map<string, string>
        PreUpdateBase: string
    }

    type FakeWorkspace = {
        mutable Target: FakeTarget
        Root: string
        Profile: string option
        mutable Location: RepositoryLocation
        mutable LocalFiles: Map<string, string>
        mutable BaseRevisionId: string
        mutable CurrentRef: string
        mutable LocalRefs: Map<string, string>
        mutable MutationCounter: int
        mutable ActiveConflict: FakeConflictSession option
    }

    /// The repository the fake hands out: its descriptor over the shared fake workspace.
    type private FakeRepository(descriptor: RepositoryDescriptor, workspace: FakeWorkspace) =
        inherit Repository(descriptor)
        member _.Workspace = workspace

    let private repositoryOf (workspace: FakeWorkspace) : Repository =
        let descriptor: RepositoryDescriptor = {
            ProviderId = fakeId
            RepositoryRoot = workspace.Root
            Location = Some workspace.Location
        }

        FakeRepository(descriptor, workspace) :> Repository

    /// Runs a function on the fake workspace of the repository. A repository that another provider
    /// opened gets the failure of the interface.
    let private forWorkspace
        (call: FakeWorkspace -> OperationContext -> Async<OperationResult<'T>>)
        (repository: Repository)
        (context: OperationContext)
        : Async<OperationResult<'T>> =
        match repository with
        | :? FakeRepository as fake -> call fake.Workspace context
        | _ -> async { return OperationResult.failed (Repository.ofAnotherProvider repository) }

    /// The same for a function that takes a request.
    let private forWorkspaceWith
        (call: FakeWorkspace -> 'Request -> OperationContext -> Async<OperationResult<'T>>)
        (repository: Repository)
        (request: 'Request)
        (context: OperationContext)
        : Async<OperationResult<'T>> =
        match repository with
        | :? FakeRepository as fake -> call fake.Workspace request context
        | _ -> async { return OperationResult.failed (Repository.ofAnotherProvider repository) }

    let private newTarget (locked: bool) : FakeTarget = {
        Revisions = Map.empty
        Refs = Map.empty
        PublishBroken = false
        RaceMutations = None
        SlowTransferArmed = false
        Locked = locked
    }

    let private addRevision (target: FakeTarget) (parents: string list) (files: Map<string, string>) (message: string) =
        globalRevisionCounter <- globalRevisionCounter + 1
        let id = $"rev-{globalRevisionCounter}"

        target.Revisions <-
            target.Revisions
            |> Map.add id {
                Id = id
                Parents = parents
                Files = files
                Message = message
            }

        id

    let private revisionFiles (target: FakeTarget) (revisionId: string) =
        match target.Revisions.TryFind revisionId with
        | Some revision -> revision.Files
        | None -> Map.empty

    let private headRevisionId (workspace: FakeWorkspace) =
        workspace.LocalRefs.TryFind workspace.CurrentRef
        |> Option.defaultValue workspace.BaseRevisionId

    let private versionToken (workspace: FakeWorkspace) =
        let conflictPart =
            match workspace.ActiveConflict with
            | Some session -> $"c{session.HandleVersion}"
            | None -> "none"

        $"fake:{workspace.CurrentRef}:{headRevisionId workspace}:{workspace.MutationCounter}:{conflictPart}"

    let private bump (workspace: FakeWorkspace) =
        workspace.MutationCounter <- workspace.MutationCounter + 1

    let private staleFailure () =
        OperationFailure.create Concurrency ConformanceCodes.PreconditionFailed "The workspace version is stale."

    let private pathValue = RepositoryPath.value

    /// Case folding plus NFC normalization: how an aliasing local filesystem sees names.
    let private aliasKey (path: string) = (normalizeNfc path).ToLowerInvariant()

    let private windowsReservedNames =
        set [
            "con"
            "prn"
            "aux"
            "nul"
            "com1"
            "com2"
            "com3"
            "lpt1"
            "lpt2"
            "lpt3"
        ]

    let private isWindowsInvalidName (path: string) =
        path.Split('/')
        |> Array.exists (fun segment ->
            let baseName = (segment.Split '.').[0].ToLowerInvariant()

            windowsReservedNames.Contains baseName
            || segment.EndsWith "."
            || segment.EndsWith " "
            || segment |> Seq.exists (fun c -> "<>:\"|?*".Contains(string c)))

    let private changesAgainstHead (workspace: FakeWorkspace) =
        let headFiles = revisionFiles workspace.Target (headRevisionId workspace)

        let modifiedOrAdded =
            workspace.LocalFiles
            |> Map.toList
            |> List.choose (fun (path, content) ->
                match headFiles.TryFind path with
                | Some headContent when headContent = content -> None
                | Some _ -> Some(path, ModifiedChange)
                | None -> Some(path, AddedChange))

        let deleted =
            headFiles
            |> Map.toList
            |> List.choose (fun (path, _) ->
                if workspace.LocalFiles.ContainsKey path then
                    None
                else
                    Some(path, DeletedChange))

        modifiedOrAdded @ deleted

    let private conflictSummary (session: FakeConflictSession) : ConflictSessionSummary =
        let unresolved = session.ConflictItems |> List.filter (fun item -> item.ResolvedContent.IsNone)

        {
            Handle = {
                SessionId = session.SessionId
                Version = string session.HandleVersion
            }
            Items =
                unresolved
                |> List.map (fun item -> {
                    Path = mkPath item.ItemPath
                    Candidates = [|
                        {
                            CandidateId = "workspace"
                            Label = "Workspace version"
                            Revision = None
                            Preview = item.OursContent |> Option.map TextPreview
                            Object = None
                        }
                        {
                            CandidateId = "target"
                            Label = "Target version"
                            Revision = Some(mkRevisionId session.TheirRevisionId)
                            Preview = item.TheirsContent |> Option.map TextPreview
                            Object = None
                        }
                        yield!
                            match item.BaseContent with
                            | Some baseContent ->
                                [|
                                    {
                                        CandidateId = "base"
                                        Label = "Base version"
                                        Revision = None
                                        Preview = Some(TextPreview baseContent)
                                        Object = None
                                    }
                                |]
                            | None -> [||]
                    |]
                    CombinedPreview = None
                    SupportsResolvedContent = true
                })
                |> List.toArray
        }

    let private synchronizationState (workspace: FakeWorkspace) : SynchronizationState =
        let targetRevision = workspace.Target.Refs.TryFind workspace.CurrentRef
        let localHead = headRevisionId workspace

        let relationship =
            match targetRevision with
            | None -> NoTarget
            | Some target when target = localHead -> UpToDate
            | Some target when target = workspace.BaseRevisionId -> LocalAhead
            | Some _ when localHead = workspace.BaseRevisionId -> TargetAhead
            | Some _ -> Diverged

        let remoteChanged =
            match targetRevision with
            | Some target when target <> workspace.BaseRevisionId ->
                let baseFiles = revisionFiles workspace.Target workspace.BaseRevisionId
                let targetFiles = revisionFiles workspace.Target target

                let changed =
                    Set.union (Set.ofSeq (Map.keys baseFiles)) (Set.ofSeq (Map.keys targetFiles))
                    |> Set.filter (fun path -> baseFiles.TryFind path <> targetFiles.TryFind path)
                    |> Set.toArray
                    |> Array.map mkPath

                Some changed
            | _ -> None

        {
            BaseRevision = Some(mkRevisionId workspace.BaseRevisionId)
            LocalRevision = Some(mkRevisionId localHead)
            TargetRevision = targetRevision |> Option.map mkRevisionId
            TargetRef = None
            LocalRevisionCount = None
            TargetRevisionCount = None
            RemoteChangedPaths = remoteChanged
            Relationship = relationship
        }

    /// Deterministic pause used by cancellation tests: reports progress until canceled.
    let private slowTransferGate (workspace: FakeWorkspace) (context: OperationContext) : Async<bool> =
        async {
            if workspace.Target.SlowTransferArmed then
                workspace.Target.SlowTransferArmed <- false
                let mutable canceled = false
                let mutable step = 0

                if context.OperationId = "large-byte-progress" then
                    context.ReportProgress {
                        PhaseCode = "transfer"
                        Item = None
                        Completed = Some(3.0 * 1024.0 * 1024.0 * 1024.0)
                        Total = Some(4.0 * 1024.0 * 1024.0 * 1024.0)
                        DisplayMessage = Some "Transferring large objects"
                    }

                while not canceled && step < 200 do
                    do! Async.Sleep 2
                    step <- step + 1

                    context.ReportProgress {
                        PhaseCode = "transfer"
                        Item = None
                        Completed = Some(float step)
                        Total = Some 200.0
                        DisplayMessage = Some "Transferring objects"
                    }

                    if context.Cancellation.IsCancellationRequested() then
                        canceled <- true

                return canceled
            else
                return false
        }

    let private raceEvidence (expected: string) (observed: string) = [|
        "expected_destination", mkRevisionId expected
        "observed_destination", mkRevisionId observed
    |]

    /// Applies armed race mutations after a pre-check, simulating another client
    /// advancing the destination inside the check-then-act-then-verify window.
    let private applyArmedRace (workspace: FakeWorkspace) : string option =
        match workspace.Target.RaceMutations with
        | Some mutations ->
            workspace.Target.RaceMutations <- None
            let targetHead = workspace.Target.Refs.TryFind workspace.CurrentRef

            let baseFiles =
                targetHead
                |> Option.map (revisionFiles workspace.Target)
                |> Option.defaultValue Map.empty

            let raceFiles =
                mutations
                |> Array.fold
                    (fun files mutation ->
                        match mutation.Content with
                        | Some content -> Map.add mutation.Path content files
                        | None -> Map.remove mutation.Path files)
                    baseFiles

            let raceRevision =
                addRevision workspace.Target (targetHead |> Option.toList) raceFiles "race: concurrent advance"

            workspace.Target.Refs <- workspace.Target.Refs |> Map.add workspace.CurrentRef raceRevision
            Some raceRevision
        | None -> None

    let private statusOf (workspace: FakeWorkspace) : RepositoryStatus =
        let changes =
            changesAgainstHead workspace
            |> List.map (fun (path, kind) -> {
                Path = mkPath path
                OldPath = None
                Kind = kind
            })
            |> List.toArray

        {
            CurrentRef =
                Some {
                    Name = workspace.CurrentRef
                    ProviderRef = mkProviderRef $"fake:{workspace.CurrentRef}"
                    Kind = LocalRef
                    IsCurrent = true
                }
            RepositoryVersion = versionToken workspace
            Changes = changes
            ActiveConflictSession = workspace.ActiveConflict |> Option.map conflictSummary
            Synchronization = Some(synchronizationState workspace)
        }

    let private getStatus (workspace: FakeWorkspace) (_: OperationContext) = async { return OperationResult.succeeded (statusOf workspace) }

    let private listRefs (workspace: FakeWorkspace) (_: OperationContext) = async {
        let refs =
            workspace.LocalRefs
            |> Map.toArray
            |> Array.map (fun (name, _) -> {
                Name = name
                ProviderRef = mkProviderRef $"fake:{name}"
                Kind = LocalRef
                IsCurrent = name = workspace.CurrentRef
            })

        return OperationResult.succeeded refs
    }

    let private createRef (workspace: FakeWorkspace) (request: CreateRefRequest) (_: OperationContext) = async {
        if request.ExpectedRepositoryVersion <> versionToken workspace then
            return OperationResult.failed (staleFailure ())
        elif workspace.LocalRefs.ContainsKey request.Name then
            return
                OperationResult.failed (
                    OperationFailure.create Validation "ref_exists" $"Ref '{request.Name}' already exists."
                )
        else
            let baseRevision =
                match request.BaseRef with
                | Some baseRef ->
                    let name = (ProviderRef.value baseRef).Replace("fake:", "")

                    workspace.LocalRefs.TryFind name
                    |> Option.defaultValue (headRevisionId workspace)
                | None -> headRevisionId workspace

            workspace.LocalRefs <- workspace.LocalRefs |> Map.add request.Name baseRevision

            if request.SwitchTo then
                workspace.CurrentRef <- request.Name

            bump workspace

            return
                OperationResult.succeeded {
                    Name = request.Name
                    ProviderRef = mkProviderRef $"fake:{request.Name}"
                    Kind = LocalRef
                    IsCurrent = request.SwitchTo
                }
    }

    let private preflightSwitchRef (workspace: FakeWorkspace) (request: SwitchRefRequest) (_: OperationContext) = async {
        let refName = (ProviderRef.value request.TargetRef).Replace("fake:", "")

        match workspace.LocalRefs.TryFind refName with
        | None ->
            return
                OperationResult.failed (
                    OperationFailure.create NotFound "ref_not_found" $"Ref '{refName}' does not exist."
                )
        | Some targetRevision ->
            let targetFiles = revisionFiles workspace.Target targetRevision
            let headFiles = revisionFiles workspace.Target (headRevisionId workspace)

            let dirtyPaths =
                changesAgainstHead workspace |> List.map fst |> Set.ofList

            let atRisk =
                dirtyPaths
                |> Set.filter (fun path -> headFiles.TryFind path <> targetFiles.TryFind path)
                |> Set.toArray
                |> Array.map mkPath

            return
                OperationResult.succeeded {
                    PathsAtRisk = atRisk
                    IsSafe = atRisk.Length = 0
                }
    }

    let private switchRef (workspace: FakeWorkspace) (request: SwitchRefRequest) (_: OperationContext) = async {
        if request.ExpectedRepositoryVersion <> versionToken workspace then
            return OperationResult.failed (staleFailure ())
        else
            let refName = (ProviderRef.value request.TargetRef).Replace("fake:", "")

            match workspace.LocalRefs.TryFind refName with
            | None ->
                return
                    OperationResult.failed (
                        OperationFailure.create NotFound "ref_not_found" $"Ref '{refName}' does not exist."
                    )
            | Some targetRevision ->
                let targetFiles = revisionFiles workspace.Target targetRevision

                // Materialization guards: aliasing collisions and Windows-invalid names.
                let paths = targetFiles |> Map.toList |> List.map fst

                let collisions =
                    paths
                    |> List.groupBy aliasKey
                    |> List.filter (fun (_, group) -> group.Length > 1)
                    |> List.collect snd

                let invalidNames = paths |> List.filter isWindowsInvalidName

                if collisions.Length > 0 then
                    return
                        OperationResult.failed {
                            OperationFailure.create
                                Validation
                                ConformanceCodes.PathCollision
                                "Distinct repository paths alias to one local file." with
                                AffectedPaths = collisions |> List.toArray
                        }
                elif invalidNames.Length > 0 then
                    return
                        OperationResult.failed {
                            OperationFailure.create
                                Validation
                                ConformanceCodes.UnrepresentablePath
                                "A repository path cannot be represented on the local filesystem." with
                                AffectedPaths = invalidNames |> List.toArray
                        }
                else
                    workspace.CurrentRef <- refName
                    workspace.LocalFiles <- targetFiles
                    workspace.BaseRevisionId <- targetRevision
                    bump workspace
                    return OperationResult.succeeded (statusOf workspace)
    }

    let private createRevision (workspace: FakeWorkspace) (request: CreateRevisionRequest) (context: OperationContext) = async {
        let! canceled = slowTransferGate workspace context

        if canceled then
            return OperationResult.canceled "The revision transfer was canceled."
        elif request.ExpectedRepositoryVersion <> versionToken workspace then
            return OperationResult.failed (staleFailure ())
        elif request.Paths.Length = 0 then
            return
                OperationResult.failed (
                    OperationFailure.create Validation "no_paths_selected" "Select at least one path."
                )
        else
            let headId = headRevisionId workspace
            let headFiles = revisionFiles workspace.Target headId

            let missing =
                request.Paths
                |> Array.map pathValue
                |> Array.filter (fun path ->
                    not (workspace.LocalFiles.ContainsKey path) && not (headFiles.ContainsKey path))

            if missing.Length > 0 then
                return
                    OperationResult.failed {
                        OperationFailure.create
                            NotFound
                            "path_not_found"
                            "A selected path does not exist in the workspace." with
                            AffectedPaths = missing
                    }
            else
                let selectedChanges =
                    request.Paths
                    |> Array.map pathValue
                    |> Array.choose (fun path ->
                        let localContent = workspace.LocalFiles.TryFind path
                        let headContent = headFiles.TryFind path

                        if localContent = headContent then
                            None
                        else
                            Some(path, localContent))

                if selectedChanges.Length = 0 then
                    return OperationResult.unchanged (Some "The selected paths are unchanged.") (mkRevisionId headId)
                else
                    let newFiles =
                        selectedChanges
                        |> Array.fold
                            (fun files (path, content) ->
                                match content with
                                | Some value -> Map.add path value files
                                | None -> Map.remove path files)
                            headFiles

                    let revisionId =
                        addRevision workspace.Target [ headId ] newFiles request.Message

                    workspace.LocalRefs <- workspace.LocalRefs |> Map.add workspace.CurrentRef revisionId
                    bump workspace

                    return
                        Succeeded {
                            OperationOutcome.performed (mkRevisionId revisionId) with
                                AffectedPaths = selectedChanges |> Array.map fst
                                ResultingRevision = Some(mkRevisionId revisionId)
                                ResultingRepositoryVersion = Some(versionToken workspace)
                                Publication = LocalOnly
                        }
    }

    let private restorePaths (workspace: FakeWorkspace) (request: RestoreRequest) (_: OperationContext) = async {
        if request.ExpectedRepositoryVersion <> versionToken workspace then
            return OperationResult.failed (staleFailure ())
        else
            let headFiles = revisionFiles workspace.Target (headRevisionId workspace)

            for path in request.Paths |> Array.map pathValue do
                match headFiles.TryFind path with
                | Some content -> workspace.LocalFiles <- workspace.LocalFiles |> Map.add path content
                | None -> workspace.LocalFiles <- workspace.LocalFiles |> Map.remove path

            bump workspace
            return OperationResult.succeeded ()
    }

    let private getDiffSummary (workspace: FakeWorkspace) (_: OperationContext) = async {
        let entries =
            changesAgainstHead workspace
            |> List.map (fun (path, kind) -> {
                Path = mkPath path
                OldPath = None
                Kind = kind
                LineInsertions = None
                LineDeletions = None
            })
            |> List.toArray

        return OperationResult.succeeded { Entries = entries }
    }

    let private localDirtyPaths (workspace: FakeWorkspace) =
        changesAgainstHead workspace |> List.map fst |> Set.ofList

    let private targetChanges (workspace: FakeWorkspace) (targetRevision: RevisionId option) =
        match targetRevision |> Option.map RevisionId.value with
        | Some targetRevision when targetRevision <> workspace.BaseRevisionId ->
            let baseFiles = revisionFiles workspace.Target workspace.BaseRevisionId
            let targetFiles = revisionFiles workspace.Target targetRevision

            Set.union (Set.ofSeq (Map.keys baseFiles)) (Set.ofSeq (Map.keys targetFiles))
            |> Set.filter (fun path -> baseFiles.TryFind path <> targetFiles.TryFind path)
        | _ -> Set.empty

    let private refresh (workspace: FakeWorkspace) (_: OperationContext) =
        async { return OperationResult.succeeded (synchronizationState workspace) }

    let private previewAgainst (workspace: FakeWorkspace) (target: RevisionId option) (_: OperationContext) = async {
        let changed = targetChanges workspace target
        let localChanged = localDirtyPaths workspace

        // Paths the local side changed in commits since base also count as local.
        let localCommitted =
            let baseFiles = revisionFiles workspace.Target workspace.BaseRevisionId
            let headFiles = revisionFiles workspace.Target (headRevisionId workspace)

            Set.union (Set.ofSeq (Map.keys baseFiles)) (Set.ofSeq (Map.keys headFiles))
            |> Set.filter (fun path -> baseFiles.TryFind path <> headFiles.TryFind path)

        let overlapping = Set.intersect changed (Set.union localChanged localCommitted)

        return
            OperationResult.succeeded {
                ChangedPaths = changed |> Set.toArray |> Array.map mkPath
                OverlappingPaths = overlapping |> Set.toArray |> Array.map mkPath
                PredictedConflictPaths = None
                HasDataLossRisk = not (Set.isEmpty (Set.intersect changed localChanged))
                WouldCreateConflictSession = not (Set.isEmpty overlapping)
            }
    }

    let private previewUpdate (workspace: FakeWorkspace) (context: OperationContext) =
        previewAgainst
            workspace
            (workspace.Target.Refs.TryFind workspace.CurrentRef |> Option.map mkRevisionId)
            context

    let private updateAgainst (workspace: FakeWorkspace) (target: RevisionId option) (request: UpdateRequest) (context: OperationContext) = async {
        let! canceled = slowTransferGate workspace context

        if canceled then
            return OperationResult.canceled "The update transfer was canceled."
        elif request.ExpectedRepositoryVersion <> versionToken workspace then
            return OperationResult.failed (staleFailure ())
        elif workspace.ActiveConflict.IsSome then
            return
                OperationResult.failed (
                    OperationFailure.create
                        Conflict
                        "conflict_session_active"
                        "Resolve or cancel the active conflict session first."
                )
        else
            applyArmedRace workspace |> ignore

            match target |> Option.map RevisionId.value with
            | None ->
                return
                    OperationResult.unchanged (Some "No target is configured.") (synchronizationState workspace)
            | Some targetRevision when targetRevision = workspace.BaseRevisionId ->
                return
                    OperationResult.unchanged
                        (Some "The workspace is already up to date.")
                        (synchronizationState workspace)
            | Some targetRevision ->
                let baseFiles = revisionFiles workspace.Target workspace.BaseRevisionId
                let targetFiles = revisionFiles workspace.Target targetRevision
                let headFiles = revisionFiles workspace.Target (headRevisionId workspace)

                let changedOnTarget =
                    Set.union (Set.ofSeq (Map.keys baseFiles)) (Set.ofSeq (Map.keys targetFiles))
                    |> Set.filter (fun path -> baseFiles.TryFind path <> targetFiles.TryFind path)

                let oursContent path =
                    workspace.LocalFiles.TryFind path
                    |> Option.orElse (headFiles.TryFind path)

                let conflicts =
                    changedOnTarget
                    |> Set.filter (fun path ->
                        let ours = oursContent path
                        let baseContent = baseFiles.TryFind path
                        let theirs = targetFiles.TryFind path
                        ours <> baseContent && ours <> theirs)
                    |> Set.toList

                let preUpdateFiles = workspace.LocalFiles
                let preUpdateBase = workspace.BaseRevisionId

                // Apply non-conflicting target changes.
                for path in Set.toList changedOnTarget do
                    if not (List.contains path conflicts) then
                        match targetFiles.TryFind path with
                        | Some content ->
                            workspace.LocalFiles <- workspace.LocalFiles |> Map.add path content
                        | None -> workspace.LocalFiles <- workspace.LocalFiles |> Map.remove path

                if conflicts.IsEmpty then
                    // Fast-forward or clean merge.
                    let mergedFiles =
                        workspace.LocalFiles

                    let localHead = headRevisionId workspace

                    let newHead =
                        if localHead = workspace.BaseRevisionId then
                            targetRevision
                        else
                            addRevision
                                workspace.Target
                                [ localHead; targetRevision ]
                                mergedFiles
                                "merge: update from target"

                    workspace.LocalRefs <- workspace.LocalRefs |> Map.add workspace.CurrentRef newHead
                    workspace.BaseRevisionId <- targetRevision
                    bump workspace
                    return OperationResult.succeeded (synchronizationState workspace)
                else
                    workspace.ActiveConflict <-
                        Some {
                            SessionId = $"conflict-{workspace.MutationCounter}-{targetRevision}"
                            HandleVersion = 1
                            ConflictItems =
                                conflicts
                                |> List.map (fun path -> {
                                    ItemPath = path
                                    BaseContent = baseFiles.TryFind path
                                    OursContent = oursContent path
                                    TheirsContent = targetFiles.TryFind path
                                    ResolvedContent = None
                                })
                            TheirRevisionId = targetRevision
                            PreUpdateFiles = preUpdateFiles
                            PreUpdateBase = preUpdateBase
                        }

                    bump workspace

                    return
                        OperationResult.partiallySucceeded
                            {
                                OperationOutcome.performed (synchronizationState workspace) with
                                    AffectedPaths = conflicts |> List.toArray
                                    ResultingRepositoryVersion = Some(versionToken workspace)
                            }
                            (OperationFailure.create
                                Conflict
                                ConformanceCodes.ConflictsDetected
                                "The update produced conflicts that need resolution.")
                            {
                                Code = "resolve_conflict_session"
                                Instructions = Some "Resolve every conflict item, then finalize."
                            }
    }

    let private update (workspace: FakeWorkspace) (request: UpdateRequest) (context: OperationContext) =
        updateAgainst
            workspace
            (workspace.Target.Refs.TryFind workspace.CurrentRef |> Option.map mkRevisionId)
            request
            context

    let private publish (workspace: FakeWorkspace) (request: PublishRequest) (context: OperationContext) = async {
        let! canceled = slowTransferGate workspace context

        if canceled then
            return OperationResult.canceled "The publish transfer was canceled."
        elif request.ExpectedRepositoryVersion <> versionToken workspace then
            return OperationResult.failed (staleFailure ())
        elif workspace.Target.PublishBroken then
            return
                OperationResult.failed {
                    OperationFailure.create
                        Network
                        "target_unreachable"
                        "The publication target is unreachable; local revisions are preserved for retry." with
                        Retryable = true
                }
        else
            let localHead = headRevisionId workspace
            let observedTarget = workspace.Target.Refs.TryFind workspace.CurrentRef

            // Client-side pre-check against the consumer-observed target revision.
            let expectedMatchesObserved =
                match request.ExpectedTargetRevision, observedTarget with
                | None, _ -> true
                | Some expected, Some observed -> RevisionId.value expected = observed
                | Some _, None -> false

            if not expectedMatchesObserved then
                return
                    OperationResult.failed {
                        staleFailure () with
                            RevisionEvidence = [|
                                yield!
                                    request.ExpectedTargetRevision
                                    |> Option.map (fun r -> "expected_target", r)
                                    |> Option.toList
                                yield!
                                    observedTarget
                                    |> Option.map (fun o -> "observed_target", mkRevisionId o)
                                    |> Option.toList
                            |]
                    }
            else
                match observedTarget with
                | Some target when target = localHead ->
                    return
                        OperationResult.unchanged
                            (Some "The target already has every local revision.")
                            (synchronizationState workspace)
                | _ ->
                    // Simulated race window between pre-check and verification.
                    match applyArmedRace workspace with
                    | Some raceRevision ->
                        return
                            OperationResult.partiallySucceeded
                                (OperationOutcome.performed (synchronizationState workspace))
                                {
                                    OperationFailure.create
                                        Concurrency
                                        ConformanceCodes.PreconditionFailed
                                        "The target advanced during publish verification." with
                                        RevisionEvidence =
                                            raceEvidence
                                                (observedTarget |> Option.defaultValue localHead)
                                                raceRevision
                                }
                                {
                                    Code = "review_and_retry"
                                    Instructions =
                                        Some "Review the observed target revision, refresh, and retry."
                                }
                    | None ->
                        workspace.BaseRevisionId <- localHead
                        workspace.Target.Refs <- workspace.Target.Refs |> Map.add workspace.CurrentRef localHead
                        bump workspace

                        return
                            Succeeded {
                                OperationOutcome.performed (synchronizationState workspace) with
                                    Publication = Published
                                    ResultingRevision = Some(mkRevisionId localHead)
                                    ResultingRepositoryVersion = Some(versionToken workspace)
                            }
    }

    let private synchronize (workspace: FakeWorkspace) (request: SynchronizeRequest) (context: OperationContext) = async {
        if request.ExpectedRepositoryVersion <> versionToken workspace then
            return OperationResult.failed (staleFailure ())
        else
            return!
                Synchronization.compose
                    {
                        HasActiveConflictSession =
                            fun _ -> async { return OperationResult.succeeded workspace.ActiveConflict.IsSome }
                        Refresh = refresh workspace
                        PreviewUpdate = fun syncState context -> previewAgainst workspace syncState.TargetRevision context
                        Update =
                            fun syncState context ->
                                updateAgainst
                                    workspace
                                    syncState.TargetRevision
                                    { ExpectedRepositoryVersion = versionToken workspace }
                                    context
                        Publish =
                            fun syncState context ->
                                publish
                                    workspace
                                    {
                                        ExpectedRepositoryVersion = versionToken workspace
                                        ExpectedTargetRevision = syncState.TargetRevision
                                    }
                                    context
                    }
                    request
                    context
    }

    let private handleRejection () =
        OperationResult.failed {
            OperationFailure.create
                Concurrency
                ConformanceCodes.PreconditionFailed
                "The conflict-session handle is stale, foreign, or closed." with
                RecoveryAction =
                    Some {
                        Code = ConflictRecovery.RefreshConflictSession
                        Instructions = Some "Refresh the conflict session and retry with the live handle."
                    }
        }

    let private validateHandle (workspace: FakeWorkspace) (handle: ConflictSessionHandle) (expectedVersion: string) =
        match workspace.ActiveConflict with
        | Some session when
            session.SessionId = handle.SessionId
            && string session.HandleVersion = handle.Version
            && expectedVersion = versionToken workspace
            ->
            Ok session
        | _ -> Error()

    let private getActiveSession (workspace: FakeWorkspace) (_: OperationContext) = async {
        return OperationResult.succeeded (workspace.ActiveConflict |> Option.map conflictSummary)
    }

    let private resolve (workspace: FakeWorkspace) (request: ResolveConflictRequest) (_: OperationContext) = async {
        match validateHandle workspace request.Handle request.ExpectedRepositoryVersion with
        | Error() -> return handleRejection ()
        | Ok session ->
            let pathText = pathValue request.Path

            match
                session.ConflictItems
                |> List.tryFind (fun item -> item.ItemPath = pathText && item.ResolvedContent.IsNone)
            with
            | None ->
                return
                    OperationResult.failed (
                        OperationFailure.create
                            NotFound
                            "conflict_item_not_found"
                            $"No unresolved conflict exists for '{pathText}'."
                    )
            | Some item ->
                let resolvedContent =
                    match request.Resolution with
                    | PickCandidate "workspace" -> Some item.OursContent
                    | PickCandidate "target" -> Some item.TheirsContent
                    | PickCandidate "base" -> item.BaseContent |> Option.map Some
                    | PickCandidate _ -> None
                    | SupplyResolvedContent content -> Some(Some content)

                match resolvedContent with
                | None ->
                    return
                        OperationResult.failed (
                            OperationFailure.create
                                Validation
                                "unknown_candidate"
                                "The candidate ID is not part of this conflict item."
                        )
                | Some content ->
                    item.ResolvedContent <- Some content
                    session.HandleVersion <- session.HandleVersion + 1
                    bump workspace

                    let remaining =
                        session.ConflictItems
                        |> List.filter (fun candidate -> candidate.ResolvedContent.IsNone)

                    return
                        OperationResult.succeeded {
                            RefreshedHandle = {
                                SessionId = session.SessionId
                                Version = string session.HandleVersion
                            }
                            RemainingItems = (conflictSummary session).Items
                        }
    }

    let private finalizeConflict (workspace: FakeWorkspace) (request: FinalizeConflictRequest) (_: OperationContext) = async {
        match validateHandle workspace request.Handle request.ExpectedRepositoryVersion with
        | Error() -> return handleRejection ()
        | Ok session ->
            if session.ConflictItems |> List.exists (fun item -> item.ResolvedContent.IsNone) then
                return
                    OperationResult.failed (
                        OperationFailure.create
                            Validation
                            "conflicts_unresolved"
                            "Every conflict item must be resolved before finalizing."
                    )
            else
                // Pre-check the destination, then detect an armed concurrent advance.
                let observedBefore = workspace.Target.Refs.TryFind workspace.CurrentRef

                match applyArmedRace workspace with
                | Some raceRevision ->
                    return
                        OperationResult.failed {
                            OperationFailure.create
                                Concurrency
                                ConformanceCodes.PreconditionFailed
                                "The destination advanced between the finalize pre-check and verification." with
                                RevisionEvidence =
                                    raceEvidence
                                        (observedBefore |> Option.defaultValue session.TheirRevisionId)
                                        raceRevision
                                RecoveryAction =
                                    Some {
                                        Code = ConflictRecovery.RefreshConflictSession
                                        Instructions =
                                            Some "Refresh the conflict session and deliberately retry."
                                    }
                        }
                | None ->
                    for item in session.ConflictItems do
                        match item.ResolvedContent with
                        | Some(Some content) ->
                            workspace.LocalFiles <- workspace.LocalFiles |> Map.add item.ItemPath content
                        | Some None ->
                            workspace.LocalFiles <- workspace.LocalFiles |> Map.remove item.ItemPath
                        | None -> ()

                    let localHead = headRevisionId workspace

                    let mergedRevision =
                        addRevision
                            workspace.Target
                            [ localHead; session.TheirRevisionId ]
                            workspace.LocalFiles
                            (request.Message |> Option.defaultValue "Merge online changes")

                    workspace.LocalRefs <- workspace.LocalRefs |> Map.add workspace.CurrentRef mergedRevision
                    workspace.BaseRevisionId <- session.TheirRevisionId
                    workspace.ActiveConflict <- None
                    bump workspace
                    return OperationResult.succeeded (Some(mkRevisionId mergedRevision))
    }

    let private cancelConflict (workspace: FakeWorkspace) (request: CancelConflictRequest) (_: OperationContext) = async {
        match validateHandle workspace request.Handle request.ExpectedRepositoryVersion with
        | Error() -> return handleRejection ()
        | Ok session ->
            workspace.LocalFiles <- session.PreUpdateFiles
            workspace.BaseRevisionId <- session.PreUpdateBase
            workspace.ActiveConflict <- None
            bump workspace
            return OperationResult.succeeded ()
    }

    let private getRepositoryWebUrl (_: FakeWorkspace) (_: OperationContext) = async {
        return OperationResult.succeeded (Some "https://fake.example/repo")
    }

    let private close (repository: Repository) : Async<unit> = async {
        match repository with
        | :? FakeRepository -> ()
        | _ -> invalidArg "repository" "The repository belongs to another provider."
    }

    let private noInstall = "The fake provider has no component to install."

    let private noTextDiff: TextDiffFunctions = {
        Open = Missing "The fake provider has no text diff."
        ReadPage = Missing "The fake provider has no text diff."
        ReplayPage = Missing "The fake provider has no text diff."
        Expand = Missing "The fake provider has no text diff."
        ReadLine = Missing "The fake provider has no text diff."
        GetSourceInfo = Missing "The fake provider has no text diff."
        Close = Missing "The fake provider has no text diff."
    }

    let private noHistory: HistoryFunctions = {
        ListRevisions = Missing "The fake provider has no history."
        GetRevisionChanges = Missing "The fake provider has no history."
    }

    let private noLargeObjects = "The fake provider has no large objects to manage."

    let private noObjectMaterialization: ObjectMaterializationFunctions = {
        ListObjects = Missing noLargeObjects
        Materialize = Missing noLargeObjects
        Dematerialize = Missing noLargeObjects
    }

    let private noStoragePolicy: StoragePolicyFunctions = {
        SetPathPolicy = Missing noLargeObjects
        GetSettings = Missing noLargeObjects
        SetSettings = Missing noLargeObjects
    }

    let private noMaintenance: MaintenanceFunctions = {
        Prune = Missing noLargeObjects
        Deduplicate = Missing noLargeObjects
        CanDeduplicate = Missing noLargeObjects
    }

    /// Builds the complete fake harness with its own isolated state.
    let create () : ProviderTestHarness =
        let mutable locationCounter = 0
        let mutable workspaceCounter = 0
        let targetsByLocation = System.Collections.Generic.Dictionary<string, FakeTarget>()
        let workspacesByRoot = System.Collections.Generic.Dictionary<string, FakeWorkspace>()

        let newLocation (locked: bool) =
            locationCounter <- locationCounter + 1
            let providerLocation = $"fake://repository-{locationCounter}"
            targetsByLocation[providerLocation] <- newTarget locked

            {
                ProviderId = fakeId
                DisplayName = None
                ProviderLocation = providerLocation
                ConnectionProfileId = Some "default-profile"
            }

        let getTarget (location: RepositoryLocation) =
            match targetsByLocation.TryGetValue location.ProviderLocation with
            | true, target -> target
            | false, _ ->
                let target = newTarget false
                targetsByLocation[location.ProviderLocation] <- target
                target

        /// What a clone, initialize, bind or open of a folder comes down to: the workspace of the folder
        /// is found or created over the target of the location, and a new repository object is handed out over it.
        let openAt (root: string) (location: RepositoryLocation) : Repository =
            let target = getTarget location

            let workspace =
                match workspacesByRoot.TryGetValue root with
                | true, existing ->
                    // Bind re-targets an existing workspace: opening it with a new
                    // location switches its target while keeping local state.
                    if not (System.Object.ReferenceEquals(existing.Target, target)) then
                        existing.Target <- target

                    existing.Location <- location
                    existing
                | false, _ ->
                    let targetHead = target.Refs.TryFind "main"

                    let baseRevision =
                        match targetHead with
                        | Some head -> head
                        | None ->
                            // A fresh workspace starts from an empty base revision.
                            addRevision target [] Map.empty "init: empty base"

                    let created = {
                        Target = target
                        Root = root
                        Profile = location.ConnectionProfileId
                        Location = location
                        LocalFiles = revisionFiles target baseRevision
                        BaseRevisionId = baseRevision
                        CurrentRef = "main"
                        LocalRefs = Map.ofList [ "main", baseRevision ]
                        MutationCounter = 0
                        ActiveConflict = None
                    }

                    workspacesByRoot[root] <- created
                    created

            repositoryOf workspace

        let functions: ProviderFunctions = {
            VerifyLocation =
                Implemented(fun request _ -> async {
                    let target = getTarget request.Location

                    if target.Locked then
                        let denied =
                            request.Intents
                            |> Array.filter (fun intent -> intent <> ReadIntent)

                        return
                            OperationResult.succeeded {
                                Location = request.Location
                                GrantedIntents = request.Intents |> Array.filter (fun intent -> intent = ReadIntent)
                                DeniedIntents = denied
                            }
                    else
                        return
                            OperationResult.succeeded {
                                Location = request.Location
                                GrantedIntents = request.Intents
                                DeniedIntents = [||]
                            }
                })
            Initialize =
                Implemented(fun request _ -> async {
                    let location = request.Location |> Option.defaultValue (newLocation false)
                    return OperationResult.succeeded (openAt request.TargetPath location)
                })
            Clone =
                Implemented(fun request context -> async {
                    if context.Cancellation.IsCancellationRequested() then
                        return OperationResult.canceled "The clone was canceled before it started."
                    elif workspacesByRoot.ContainsKey request.TargetPath then
                        return
                            OperationResult.failed (
                                OperationFailure.create
                                    Validation
                                    ConformanceCodes.TargetNotEmpty
                                    "The clone target directory is not empty."
                            )
                    else
                        return OperationResult.succeeded (openAt request.TargetPath request.Location)
                })
            Bind =
                Implemented(fun request _ -> async {
                    return OperationResult.succeeded (openAt request.RepositoryRoot request.Location)
                })
            Open =
                Implemented(fun request _ -> async {
                    match workspacesByRoot.TryGetValue request.RepositoryRoot with
                    | true, existing -> return OperationResult.succeeded (repositoryOf existing)
                    | false, _ ->
                        return
                            OperationResult.failed (
                                OperationFailure.create
                                    NotFound
                                    "not_a_repository"
                                    "The folder is not a repository of this provider."
                            )
                })
            CheckDependencies =
                Implemented(fun _ -> async {
                    return
                        OperationResult.succeeded [|
                            {
                                Component = "fake-engine"
                                Installed = true
                                Version = Some "1.0"
                                Compatible = true
                                Remediation = None
                                ProbeExecutablePath = None
                            }
                        |]
                })
            InstallDependency = Missing noInstall
            GetStatus = Implemented(forWorkspace getStatus)
            ListRefs = Implemented(forWorkspace listRefs)
            CreateRef = Implemented(forWorkspaceWith createRef)
            PreflightSwitchRef = Implemented(forWorkspaceWith preflightSwitchRef)
            SwitchRef = Implemented(forWorkspaceWith switchRef)
            CreateRevision = Implemented(forWorkspaceWith createRevision)
            RestorePaths = Implemented(forWorkspaceWith restorePaths)
            GetDiffSummary = Implemented(forWorkspace getDiffSummary)
            Synchronization = {
                Refresh = Implemented(forWorkspace refresh)
                PreviewUpdate = Implemented(forWorkspace previewUpdate)
                Update = Implemented(forWorkspaceWith update)
                Publish = Implemented(forWorkspaceWith publish)
                Synchronize = Implemented(forWorkspaceWith synchronize)
            }
            ConflictResolution = {
                GetActiveSession = Implemented(forWorkspace getActiveSession)
                Resolve = Implemented(forWorkspaceWith resolve)
                Finalize = Implemented(forWorkspaceWith finalizeConflict)
                Cancel = Implemented(forWorkspaceWith cancelConflict)
            }
            TextDiff = noTextDiff
            History = noHistory
            RepositoryBrowser = { GetRepositoryWebUrl = Implemented(forWorkspace getRepositoryWebUrl) }
            ObjectMaterialization = noObjectMaterialization
            StoragePolicy = noStoragePolicy
            Maintenance = noMaintenance
        }

        let provider: IVersionControl =
            ListedProvider((fun _ -> async { return NotDetected }), close, functions) :> IVersionControl

        let getWorkspaceState (harnessWorkspace: HarnessWorkspace) =
            workspacesByRoot[harnessWorkspace.Repository.Descriptor.RepositoryRoot]

        let createHarnessWorkspace (repository: Repository) : HarnessWorkspace =
            let state = workspacesByRoot[repository.Descriptor.RepositoryRoot]

            {
                Provider = provider
                Repository = repository
                WriteFile =
                    fun path content -> promise {
                        state.LocalFiles <- state.LocalFiles |> Map.add path content
                        state.MutationCounter <- state.MutationCounter + 1
                        return ()
                    }
                ReadFile = fun path -> promise { return state.LocalFiles.TryFind path }
                RemoveFile =
                    fun path -> promise {
                        state.LocalFiles <- state.LocalFiles |> Map.remove path
                        state.MutationCounter <- state.MutationCounter + 1
                        return ()
                    }
                LocalFileSystemAliasesCase = true
                LocalFileSystemAliasesNormalization = true
                LocalFileSystemWindowsRules = true
            }

        let openWorkspace (root: string) (location: RepositoryLocation) = promise {
            let! bindResult =
                Async.StartAsPromise(
                    SuiteHelpers.executed (
                        provider.Bind
                            {
                                RepositoryRoot = root
                                Location = location
                            }
                            (OperationContext.detached "harness-open")
                    )
                )

            match bindResult with
            | Succeeded outcome -> return createHarnessWorkspace outcome.Value
            | PartiallySucceeded _
            | Failed _ -> return failwith "The fake provider failed to open a repository."
        }

        let createWorkspaceWithBase () = promise {
            workspaceCounter <- workspaceCounter + 1
            let location = newLocation false
            let target = targetsByLocation[location.ProviderLocation]

            // Base revision published on the target so every workspace starts synchronized.
            let baseRevision =
                addRevision target [] (Map.ofList [ "base.txt", "base content\n" ]) "init: base"

            target.Refs <- target.Refs |> Map.add "main" baseRevision

            return! openWorkspace $"/fake-workspace-{workspaceCounter}" location
        }

        {
            Name = "fake"
            Provider = provider
            ExpectedServices = [ "synchronization"; "conflicts"; "browser" ]
            CreateLocation = fun () -> promise { return newLocation false }
            CreateUnauthorizedLocation = fun () -> promise { return newLocation true }
            CreateLocalPath =
                fun () -> promise {
                    workspaceCounter <- workspaceCounter + 1
                    return $"/fake-fresh-{workspaceCounter}"
                }
            CreateWorkspace = createWorkspaceWithBase
            CreateLinkedWorkspace =
                fun anchor -> promise {
                    workspaceCounter <- workspaceCounter + 1

                    let linkedLocation = {
                        SuiteHelpers.locationOf anchor with
                            ConnectionProfileId = Some $"linked-profile-{workspaceCounter}"
                    }

                    return! openWorkspace $"/fake-linked-{workspaceCounter}" linkedLocation
                }
            OpenSecondRepository =
                fun anchor -> promise {
                    let! openResult =
                        Async.StartAsPromise(
                            SuiteHelpers.executed (
                                provider.Open
                                    {
                                        RepositoryRoot = anchor.Repository.Descriptor.RepositoryRoot
                                        ConnectionProfileId = (SuiteHelpers.locationOf anchor).ConnectionProfileId
                                    }
                                    (OperationContext.detached "harness-second-repository")
                            )
                        )

                    match openResult with
                    | Succeeded outcome -> return outcome.Value
                    | PartiallySucceeded _
                    | Failed _ -> return failwith "The fake provider failed to open a second repository."
                }
            AdvanceTarget =
                fun harnessWorkspace mutations -> promise {
                    let state = getWorkspaceState harnessWorkspace
                    let target = state.Target
                    let targetHead = target.Refs.TryFind state.CurrentRef

                    let baseFiles =
                        targetHead
                        |> Option.map (revisionFiles target)
                        |> Option.defaultValue Map.empty

                    let newFiles =
                        mutations
                        |> Array.fold
                            (fun files mutation ->
                                match mutation.Content with
                                | Some content -> Map.add mutation.Path content files
                                | None -> Map.remove mutation.Path files)
                            baseFiles

                    let revision =
                        addRevision target (targetHead |> Option.toList) newFiles "external: target advance"

                    target.Refs <- target.Refs |> Map.add state.CurrentRef revision
                    return ()
                }
            SeedRevisionOnNewRef =
                fun harnessWorkspace refName files -> promise {
                    let state = getWorkspaceState harnessWorkspace
                    let baseFiles = revisionFiles state.Target state.BaseRevisionId

                    let seededFiles =
                        files
                        |> Array.fold (fun map (path, content) -> Map.add path content map) baseFiles

                    let revision =
                        addRevision state.Target [ state.BaseRevisionId ] seededFiles $"seed: {refName}"

                    state.LocalRefs <- state.LocalRefs |> Map.add refName revision
                    return mkProviderRef $"fake:{refName}"
                }
            BreakPublish =
                fun harnessWorkspace -> promise {
                    (getWorkspaceState harnessWorkspace).Target.PublishBroken <- true
                    return ()
                }
            RestorePublish =
                fun harnessWorkspace -> promise {
                    (getWorkspaceState harnessWorkspace).Target.PublishBroken <- false
                    return ()
                }
            ArmDestinationRace =
                fun harnessWorkspace mutations -> promise {
                    (getWorkspaceState harnessWorkspace).Target.RaceMutations <- Some mutations
                    return ()
                }
            ArmSlowTransfer =
                fun harnessWorkspace -> promise {
                    (getWorkspaceState harnessWorkspace).Target.SlowTransferArmed <- true
                    return ()
                }
            Cleanup =
                fun () -> promise {
                    targetsByLocation.Clear()
                    workspacesByRoot.Clear()
                    return ()
                }
        }
