namespace VersionControlService.Abstractions

/// Who made or committed a revision, and when. `At` is in seconds since the Unix epoch.
type RevisionSignature = { Name: string; Email: string option; At: int64 }

/// One revision of the history.
type RevisionEntry = {
    Id: RevisionId
    /// The first parent comes first. Empty for a root revision.
    ParentIds: RevisionId[]
    /// The message on one line. The Git provider joins the lines of its first paragraph.
    Summary: string
    Message: string
    Committer: RevisionSignature
    /// Absent when the provider records no author apart from the committer.
    Author: RevisionSignature option
    /// The refs that point at this revision.
    Refs: LogicalRef[]
    /// Whether the revision is visible on the publication target or exists only locally. The Git provider asks the
    /// remote-tracking branches of origin, so the answer is as old as the last contact with it.
    Publication: PublicationState
}

type ListRevisionsRequest = {
    /// The cursor of the page before. None starts at the current revision.
    Cursor: string option
    /// The provider clamps Limit to 1..200.
    Limit: int
}

type RevisionPage = { Revisions: RevisionEntry[]; NextCursor: string option }

/// The history functions of a provider, one entry for each.
type HistoryFunctions = {
    ListRevisions: Entry<Repository -> ListRevisionsRequest -> OperationContext -> Async<OperationResult<RevisionPage>>>
    GetRevisionChanges: Entry<Repository -> RevisionId -> OperationContext -> Async<OperationResult<DiffSummary>>>
}

/// History: the revisions that lead to the current one, newest first.
type IHistory =
    abstract ListRevisions:
        repository: Repository ->
        request: ListRevisionsRequest ->
        context: OperationContext ->
            Async<CallResult<RevisionPage>>

    /// The objects a revision changed against its first parent. A root revision reports every object as added.
    abstract GetRevisionChanges:
        repository: Repository -> revision: RevisionId -> context: OperationContext -> Async<CallResult<DiffSummary>>
