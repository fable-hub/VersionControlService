namespace VersionControlService.Abstractions

/// Opaque handle for a diff session pinned to its source identities.
type DiffHandle = { Id: string; Version: string }

/// A zero-based range of source lines.
type LineRange = { Start: int64; Count: int64 }

/// The line terminator found after a line.
[<RequireQualifiedAccess>]
type LineEnding =
    | NoEnding
    | LF
    | CRLF
    | CR

/// Describes whether a highlighted span is unchanged or changed text.
[<RequireQualifiedAccess>]
type HighlightKind =
    | UnchangedText
    | ChangedText

/// A UTF-16 span inside the text of a line slice.
type Highlight = { Start: int; Length: int; Kind: HighlightKind }

/// A UTF-16 slice of one line. TotalUtf16 remains absent until the line length is known.
type LineSlice = { OffsetUtf16: int64; TotalUtf16: int64 option; Text: string; Highlights: Highlight[] }

/// A whole source line with the currently available slice of its text.
type DiffLine = { Number: int64; Ending: LineEnding; Slice: LineSlice }

/// Describes the relationship represented by a diff row.
[<RequireQualifiedAccess>]
type DiffRowKind =
    | Context
    | Added
    | Removed
    | Replaced
    | EndingChanged

/// One diff row with a stable identifier and optional lines from each side.
type DiffRow = { Id: string; Kind: DiffRowKind; Previous: DiffLine option; Current: DiffLine option }

/// Aligned rows or separate line sequences when alignment is unavailable.
[<RequireQualifiedAccess>]
type HunkBody =
    | AlignedRows of DiffRow[]
    | UnalignedSides of previous: DiffLine[] * current: DiffLine[]

/// A hunk fragment with source ranges and markers for its boundaries.
type HunkFragment = {
    HunkId: string
    PreviousRange: LineRange
    CurrentRange: LineRange
    StartsHunk: bool
    EndsHunk: bool
    Body: HunkBody
}

/// A hidden range whose lines and endings have been verified equal.
type EqualGap = { GapId: string; PreviousRange: LineRange; CurrentRange: LineRange }

/// One visible component of a diff page, including verified equal context.
[<RequireQualifiedAccess>]
type DiffPart =
    | Hunk of HunkFragment
    | HiddenEqual of EqualGap
    | ExpandedContext of gapId: string * DiffRow[]

/// Unique validated byte coverage across both sources and the scan completion state.
type ScanProgress = { ValidatedBytes: int64; TotalBytes: int64; ScanComplete: bool }

/// Describes what follows the final character in a pending snippet.
[<RequireQualifiedAccess>]
type SnippetEnd =
    | Truncated
    | MoreTextPending
    | LineEnd
    | EndOfFile

/// The available portion of one line while scanning continues.
type PendingSnippet = { Line: int64; OffsetUtf16: int64; Text: string; End: SnippetEnd }

/// State for one side at the pending scan position, including proven exhaustion.
[<RequireQualifiedAccess>]
type PendingSide =
    | NoActiveLine
    | Snippet of PendingSnippet
    | Exhausted of lineCount: int64

/// UTF-16 offsets of an advisory mismatch between the two sources.
type MismatchMarker = { PreviousOffsetUtf16: int64; CurrentOffsetUtf16: int64 }

/// Advisory snippets for lines still being scanned. They never form a diff row.
type PendingPreview = { Previous: PendingSide; Current: PendingSide; Mismatch: MismatchMarker option }

/// A result that is ready or has a continuation while scanning proceeds.
[<RequireQualifiedAccess>]
type Resumable<'T> =
    | Ready of 'T
    | Scanning of ScanProgress * continuation: string * pending: PendingPreview option

/// A page of diff parts with its cursor and progress. A pending preview appears only when another page is available.
type DiffPage = {
    PageId: string
    NextCursor: string option
    Parts: DiffPart[]
    Progress: ScanProgress
    OutputComplete: bool
    Pending: PendingPreview option
}

/// Text metadata for one source in an opened diff.
type DiffSourceInfo = {
    Path: RepositoryPath
    /// The revision the side was read from, also when its content is a large object. A working file or an absent side has none.
    Revision: RevisionId option
    IsAbsent: bool
    ByteLength: int64
    LineCount: int64 option
    Encoding: string option
    EncodingWasChosen: bool
    HasBom: bool
}

/// An encoding candidate with a preview of at most 2 KiB of decoded text.
type EncodingCandidate = { Encoding: string; Preview: string }

/// Binds the selected path, optional previous path, pinned commits, and source identities.
type PreparationToken = { Id: string }

/// How a diff session keeps its temporary data.
[<RequireQualifiedAccess>]
type DiffStoragePolicy =
    /// The session uses temporary files unless the free space of the temp drive is below `minimumFreeBytes`.
    /// In that case it keeps its data in memory within `memoryBudgetBytes`.
    | PreferDisk of minimumFreeBytes: int64 * memoryBudgetBytes: int64
    /// The session always keeps its data in memory within `memoryBudgetBytes`. The budget must be positive. A budget
    /// below 576 KiB (a page envelope plus 64 KiB) blocks every committed Git blob.
    | MemoryOnly of memoryBudgetBytes: int64

/// What a diff compares.
[<RequireQualifiedAccess>]
type DiffComparison =
    /// The working file against its version in the last revision.
    | WorkingFile
    /// The version in a revision against the version in its first parent.
    | Revision of RevisionId

/// Input for opening a pinned diff session. `Comparison` names what the diff compares, and a revision
/// comparison does not read the working tree.
type OpenDiffRequest = {
    Path: RepositoryPath
    PreviousPath: RepositoryPath option
    Comparison: DiffComparison
    Preparation: PreparationToken option
    PreviousEncoding: string option
    CurrentEncoding: string option
    ContextLines: int
    Continuation: string option
    Storage: DiffStoragePolicy
}

/// Reason the provider cannot produce a diff for the requested sources.
[<RequireQualifiedAccess>]
type DiffBlocker =
    | Binary of DiffSide * evidence: string
    | LocalContentUnavailable of DiffSide * objectId: string option
    | EncodingRequired of DiffSide * PreparationToken * EncodingCandidate[]
    | NotRegularFile of DiffSide
    /// The session keeps its data in memory and the committed Git blob of this side is larger than the session
    /// may read into memory.
    | BlobTooLargeForMemory of DiffSide * blobBytes: int64 * limitBytes: int64

/// Where an opened session keeps its temporary data.
[<RequireQualifiedAccess>]
type DiffStorage =
    /// The session keeps its data in temporary files.
    | OnDisk
    /// The session keeps its data in memory within `budgetBytes`.
    | InMemory of budgetBytes: int64

/// The result of opening a diff, with either a blocker or a new handle, the storage of the session and an initial page.
[<RequireQualifiedAccess>]
type OpenDiffResult =
    | NotDiffable of DiffBlocker
    | Opened of
        DiffHandle *
        previous: DiffSourceInfo *
        current: DiffSourceInfo *
        first: Resumable<DiffPage> *
        storage: DiffStorage

/// Request the page that follows a cursor in an opened diff.
/// `Background` is true for a read the host makes ahead of the user.
/// A memory-mode session stops such reads earlier than user reads.
type ReadPageRequest = {
    Handle: DiffHandle
    Cursor: string
    Background: bool
}

/// Request a previously returned page again by its identifier.
type ReplayPageRequest = { Handle: DiffHandle; PageId: string }

/// Request part of a verified equal range. The service clamps Count to 100 lines.
type ExpandRequest = {
    Handle: DiffHandle
    GapId: string
    FromStart: bool
    Count: int
    Continuation: string option
}

/// Request a UTF-16 slice of one whole source line. The service clamps MaxUtf16 to 8,192.
type ReadLineRequest = {
    Handle: DiffHandle
    Side: DiffSide
    Line: int64
    OffsetUtf16: int64
    MaxUtf16: int
    Continuation: string option
}

/// Request the pinned metadata for both sources in an opened diff.
type SourceInfoRequest = { Handle: DiffHandle }

/// The text diff functions of a provider, one entry for each.
type TextDiffFunctions = {
    Open: Entry<Repository -> OpenDiffRequest -> OperationContext -> Async<OperationResult<Resumable<OpenDiffResult>>>>
    ReadPage: Entry<Repository -> ReadPageRequest -> OperationContext -> Async<OperationResult<Resumable<DiffPage>>>>
    ReplayPage: Entry<Repository -> ReplayPageRequest -> OperationContext -> Async<OperationResult<DiffPage>>>
    Expand: Entry<Repository -> ExpandRequest -> OperationContext -> Async<OperationResult<Resumable<DiffPart[]>>>>
    ReadLine: Entry<Repository -> ReadLineRequest -> OperationContext -> Async<OperationResult<Resumable<DiffLine>>>>
    GetSourceInfo:
        Entry<Repository -> SourceInfoRequest -> OperationContext -> Async<OperationResult<DiffSourceInfo * DiffSourceInfo>>>
    Close: Entry<Repository -> DiffHandle -> OperationContext -> Async<OperationResult<unit>>>
}

/// Reads and expands paged diffs with resumable work.
/// A handle belongs to the workspace and to the owner that the hosting provider derives from the calling
/// operation (for example, the window that made the call). A call with a handle of another owner answers
/// `TextDiffFailureCodes.SessionClosed` ("diff_session_closed"), and `Close` of another owner's handle changes nothing.
type ITextDiff =
    abstract Open:
        repository: Repository ->
        request: OpenDiffRequest ->
        context: OperationContext ->
            Async<CallResult<Resumable<OpenDiffResult>>>

    abstract ReadPage:
        repository: Repository ->
        request: ReadPageRequest ->
        context: OperationContext ->
            Async<CallResult<Resumable<DiffPage>>>

    abstract ReplayPage:
        repository: Repository -> request: ReplayPageRequest -> context: OperationContext -> Async<CallResult<DiffPage>>

    abstract Expand:
        repository: Repository ->
        request: ExpandRequest ->
        context: OperationContext ->
            Async<CallResult<Resumable<DiffPart[]>>>

    abstract ReadLine:
        repository: Repository ->
        request: ReadLineRequest ->
        context: OperationContext ->
            Async<CallResult<Resumable<DiffLine>>>

    abstract GetSourceInfo:
        repository: Repository ->
        request: SourceInfoRequest ->
        context: OperationContext ->
            Async<CallResult<DiffSourceInfo * DiffSourceInfo>>

    abstract Close: repository: Repository -> handle: DiffHandle -> context: OperationContext -> Async<CallResult<unit>>

/// Stable failure codes returned by paged text diff operations.
module TextDiffFailureCodes =

    /// The selected source content could not be read as text.
    [<Literal>]
    let ContentNotText = "diff_content_not_text"

    /// The bytes of a side that classification read as UTF-8 contain a sequence that is invalid in UTF-8 but
    /// valid in Windows-1252, and the caller did not choose the encoding. `OperationFailure.DiffDetail` names
    /// the side and the evidence. When `Open` finds the sequence before the first page, it returns
    /// `DiffBlocker.EncodingRequired` for that side.
    [<Literal>]
    let EncodingMismatch = "diff_encoding_mismatch"

    /// A source could not be read because of a file system or process error, for example a working file that
    /// cannot be opened or a Git child that cannot be started. Any call that reads a source can return it.
    [<Literal>]
    let ReadFailed = "diff_read_failed"

    /// `Open` received an encoding name in `PreviousEncoding` or `CurrentEncoding` that the engine does not know.
    [<Literal>]
    let UnsupportedEncoding = "unsupported_encoding"

    /// A pinned source identity changed while its diff was open.
    [<Literal>]
    let SourceChanged = "source_changed"

    /// The diff handle has already been closed.
    [<Literal>]
    let SessionClosed = "diff_session_closed"

    /// The worker could not complete a diff operation.
    [<Literal>]
    let WorkerFailed = "diff_worker_failed"

    /// The preparation token does not match the requested sources.
    [<Literal>]
    let PreparationMismatch = "preparation_mismatch"

    /// The continuation was produced for a request with different fields.
    [<Literal>]
    let ContinuationMismatch = "continuation_mismatch"

    /// The free space of the temp drive is below the minimum that the request named, so the session refused work
    /// that would write. The session stays usable and recorded answers keep working. A later request can succeed.
    [<Literal>]
    let TempSpaceLow = "diff_temp_space_low"

    /// The memory-mode session reached its budget, or 3/4 of it for a background read. The session stays usable
    /// and recorded answers keep working.
    [<Literal>]
    let MemoryBudgetReached = "diff_memory_budget_reached"
