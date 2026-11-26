namespace VersionControlService.Abstractions

/// Provider-issued opaque conflict-session handle. Its Version rotates after every
/// successful nonterminal resolution and is distinct from RepositoryVersion; the two
/// tokens have different lifetimes and are never collapsed into one field.
type ConflictSessionHandle = {
    SessionId: string
    Version: string
}

type ConflictPreview =
    | TextPreview of content: string
    | UnsupportedPreview of reason: string option

/// The stored object behind a conflict candidate, for content a provider keeps outside the ordinary file history (a Git LFS object, say). Consumers render it without a download.
type ConflictCandidateObject = {
    SizeBytes: float option
    /// Content identity, such as an LFS object id. Not a revision.
    ObjectId: string option
    /// Whether the object's bytes are in provider-local storage, observed when the summary was read.
    IsLocallyAvailable: bool
}

/// A candidate with Preview None and Object None stands for the side that deleted the file, and picking it resolves the item to the deletion.
type ConflictCandidate = {
    CandidateId: string
    Label: string
    Revision: RevisionId option
    Preview: ConflictPreview option
    /// Some when the candidate's content is a separately stored object. None when the provider advertises no such object for it.
    Object: ConflictCandidateObject option
}

type ConflictItem = {
    Path: RepositoryPath
    Candidates: ConflictCandidate[]
    /// Provider-owned combined representation, e.g. Git conflict-marker text.
    CombinedPreview: ConflictPreview option
    /// True when the provider accepts caller-supplied resolved content. A consumer resolves binary items, oversized items and items whose candidates include a stored object by picking a candidate.
    SupportsResolvedContent: bool
}

type ConflictSessionSummary = {
    Handle: ConflictSessionHandle
    Items: ConflictItem[]
}

type ConflictResolution =
    | PickCandidate of candidateId: string
    | SupplyResolvedContent of content: string

type ResolveConflictRequest = {
    Handle: ConflictSessionHandle
    /// Independently observed workspace version; separate lifetime from the handle.
    ExpectedRepositoryVersion: string
    Path: RepositoryPath
    Resolution: ConflictResolution
}

/// Every successful nonterminal resolution returns the refreshed handle so replaying
/// an earlier choice is deterministically stale.
type ConflictResolutionOutcome = {
    RefreshedHandle: ConflictSessionHandle
    RemainingItems: ConflictItem[]
}

type FinalizeConflictRequest = {
    Handle: ConflictSessionHandle
    ExpectedRepositoryVersion: string
    Message: string option
}

type CancelConflictRequest = {
    Handle: ConflictSessionHandle
    ExpectedRepositoryVersion: string
}

/// Stable recovery action code for stale/foreign/closed conflict-session handles.
module ConflictRecovery =

    [<Literal>]
    let RefreshConflictSession = "refresh_conflict_session"

/// The conflict resolution functions of a provider, one entry for each.
type ConflictResolutionFunctions = {
    GetActiveSession: Entry<Repository -> OperationContext -> Async<OperationResult<ConflictSessionSummary option>>>
    Resolve: Entry<Repository -> ResolveConflictRequest -> OperationContext -> Async<OperationResult<ConflictResolutionOutcome>>>
    Finalize: Entry<Repository -> FinalizeConflictRequest -> OperationContext -> Async<OperationResult<RevisionId option>>>
    Cancel: Entry<Repository -> CancelConflictRequest -> OperationContext -> Async<OperationResult<unit>>>
}

/// Provider-managed conflict sessions. Stale, foreign, or closed handles are rejected
/// before provider state changes with category Concurrency, code "precondition_failed",
/// and recovery "refresh_conflict_session". Finalize/cancel closes the handle.
type IConflictResolution =
    abstract GetActiveSession:
        repository: Repository -> context: OperationContext -> Async<CallResult<ConflictSessionSummary option>>

    abstract Resolve:
        repository: Repository ->
        request: ResolveConflictRequest ->
        context: OperationContext ->
            Async<CallResult<ConflictResolutionOutcome>>

    /// Finalizes only when all conflicts are resolved; verifies the provider
    /// destination after its own pre-check and closes the handle.
    abstract Finalize:
        repository: Repository ->
        request: FinalizeConflictRequest ->
        context: OperationContext ->
            Async<CallResult<RevisionId option>>

    abstract Cancel:
        repository: Repository -> request: CancelConflictRequest -> context: OperationContext -> Async<CallResult<unit>>
