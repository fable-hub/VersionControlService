/// Decides which local content each side of a diff compares. The working file comparison reads the previous side
/// from the pinned HEAD commit and the current side from the working file. The revision comparison reads the
/// current side from the revision and the previous side from its first parent, and it does not touch the working
/// tree. Every lookup is local. Git runs through the injected runner, which the worker routes to the supervisor,
/// so no transfer, LFS command or status scan happens here.
module VersionControlService.Git.TextDiff.TextDiffSourceResolver

open System
open VersionControlService.Abstractions
open VersionControlService.Git.TextDiff.TextDiffLfsPointer

module NodePath = VersionControlService.Runtime.Node.Path
module Supervisor = VersionControlService.Git.TextDiff.TextDiffSupervisor

type ResolverInput = {
    RepositoryRoot: string
    LfsMediaDirectory: string
    /// Repository-relative with forward slashes.
    Path: string
    PreviousPath: string option
    Comparison: DiffComparison
}

type GitShort = Supervisor.ShortResult

type SourceStat = {
    Size: int64
    IsFile: bool
    IsSymbolicLink: bool
    IsDirectory: bool
    MtimeNs: string
    Ino: string
    Dev: string
}

[<RequireQualifiedAccess>]
type StatResult =
    | Missing
    | Stat of SourceStat

type IResolverHost =
    abstract member RunGit: string[] -> Async<GitShort>
    abstract member Lstat: string -> Async<StatResult>
    abstract member IsCanceled: unit -> bool
    /// Reads at most the given number of bytes from the start of a file.
    abstract member ReadPrefix: string -> int -> Async<byte[]>
    /// Resolves symbolic links in the existing part of a path. A missing tail stays as written.
    abstract member Realpath: string -> Async<string>

/// The lstat values that tell an edited file apart from the one that was resolved.
type FileIdentity = {
    Size: int64
    MtimeNs: string
    Ino: string
    Dev: string
}

[<RequireQualifiedAccess>]
type ResolvedSide =
    | Absent
    | GitBlob of oid: string * size: int64
    | WorkingFile of path: string * identity: FileIdentity
    /// The object identity is the lstat of the object file. A pointer-backed working file also carries its own identity.
    | LfsObject of objectPath: string * size: int64 * objectIdentity: FileIdentity * pointerFileIdentity: FileIdentity option

type ResolvedSources = {
    /// The commit the previous side was read from. None when the previous side has no commit.
    PreviousCommitId: string option
    /// The commit the current side was read from. None when the current side is the working file.
    CurrentCommitId: string option
    Previous: ResolvedSide
    Current: ResolvedSide
}

[<RequireQualifiedAccess>]
type ResolveOutcome =
    | Resolved of ResolvedSources
    | Blocked of DiffBlocker
    | ReadError of message: string
    | Canceled

type private Step<'T> =
    | Continue of 'T
    | Stop of ResolveOutcome

exception private ResolveCanceled

let private awaitHost (host: IResolverHost) (work: Async<'T>) : Async<'T> = async {
    if host.IsCanceled() then
        return raise ResolveCanceled

    let! result = work

    if host.IsCanceled() then
        return raise ResolveCanceled

    return result
}

let private identityOf (stat: SourceStat) : FileIdentity = {
    Size = stat.Size
    MtimeNs = stat.MtimeNs
    Ino = stat.Ino
    Dev = stat.Dev
}

let private utf8 (bytes: byte[]) = Text.Encoding.UTF8.GetString bytes

let private succeeded (result: GitShort) = result.ExitCode = Some 0 && result.Error.IsNone

let private commandFailure (command: string) (result: GitShort) =
    let detail =
        match result.Error with
        | Some error -> error
        | None -> result.Stderr.Trim()

    ResolveOutcome.ReadError $"git {command} failed: {detail}"

/// Git reports a locally missing object this way when lazy fetching is off, and older versions report the
/// refused promisor fetch. Both sides are lower-cased because Fable compiles an ignore-case Contains to a
/// case-sensitive search.
let stderrReportsMissingObject (stderr: string) =
    let lowered = stderr.ToLowerInvariant()

    [ "could not get object info"; "promisor remote"; "not a valid object name" ]
    |> List.exists (fun text -> lowered.Contains(text.ToLowerInvariant()))

let private isMissingObject (result: GitShort) =
    result.Error.IsNone && stderrReportsMissingObject result.Stderr

let private isDrivePath (path: string) = path.Length >= 2 && path[1] = ':'

/// Whether a resolved folder is the workspace root or lies below it. Drive letter paths compare without case.
let private isInsideRoot (root: string) (folder: string) =
    let normalize (path: string) = path.Replace('\\', '/').TrimEnd('/')
    let comparison = if isDrivePath root then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
    let root = normalize root
    let folder = normalize folder
    folder.Equals(root, comparison) || folder.StartsWith(root + "/", comparison)

let private outputLines (bytes: byte[]) =
    (utf8 bytes).Split('\n') |> Array.map (fun line -> line.TrimEnd '\r')

/// Pins HEAD to a commit id. None means HEAD is unborn.
let private resolveHead (host: IResolverHost) : Async<Step<string option>> = async {
    let! revParse = awaitHost host (host.RunGit [| "rev-parse"; "--verify"; "--quiet"; "HEAD^{commit}" |])
    let commitId = (utf8 revParse.Stdout).Trim()

    if succeeded revParse && Supervisor.isObjectId commitId then
        return Continue(Some commitId)
    elif revParse.Error.IsSome then
        return Stop(commandFailure "rev-parse" revParse)
    else
        let! symbolicRef = awaitHost host (host.RunGit [| "symbolic-ref"; "-q"; "HEAD" |])
        let refName = (utf8 symbolicRef.Stdout).Trim()

        if symbolicRef.Error.IsSome then
            return Stop(commandFailure "symbolic-ref" symbolicRef)
        elif not (succeeded symbolicRef) || String.IsNullOrEmpty refName then
            return Stop(ResolveOutcome.ReadError "HEAD does not resolve to a commit and is not a symbolic reference.")
        else
            let! forEachRef = awaitHost host (host.RunGit [| "for-each-ref"; "--format=%(refname)"; "--"; refName |])

            if not (succeeded forEachRef) || forEachRef.Stderr.Length > 0 then
                return Stop(commandFailure "for-each-ref" forEachRef)
            elif outputLines forEachRef.Stdout |> Array.contains refName then
                return Stop(ResolveOutcome.ReadError $"HEAD names {refName}, which exists but does not resolve to a commit.")
            else
                return Continue None
}

type private TreeEntry = { Mode: string; Kind: string; Oid: string }

/// Parses `<mode> <type> <oid>\t<path>\0` entries and returns the one whose path equals the request.
let private findTreeEntry (output: byte[]) (path: string) : Result<TreeEntry option, string> =
    let entries = (utf8 output).Split('\000') |> Array.filter (fun entry -> entry.Length > 0)
    let mutable found = None
    let mutable failure = None

    for entry in entries do
        let tab = entry.IndexOf '\t'
        let header = if tab > 0 then entry.Substring(0, tab).Split(' ') else [||]

        if header.Length <> 3 then
            failure <- Some $"git ls-tree returned an unreadable entry: {entry}"
        elif entry.Substring(tab + 1) = path then
            found <- Some { Mode = header[0]; Kind = header[1]; Oid = header[2] }

    match failure with
    | Some message -> Error message
    | None -> Ok found

let private objectPath (mediaDirectory: string) (oid: string) =
    NodePath.join [| mediaDirectory; oid.Substring(0, 2); oid.Substring(2, 2); oid |]

/// The local LFS object of the pointer, when it is a regular file of exactly the pointer's size.
let private resolveLfsObject
    (host: IResolverHost)
    (input: ResolverInput)
    (side: DiffSide)
    (pointer: LfsPointer)
    (pointerIdentity: FileIdentity option)
    : Async<Step<ResolvedSide>> = async {
    let path = objectPath input.LfsMediaDirectory pointer.Oid
    let! stat = awaitHost host (host.Lstat path)

    match stat with
    | StatResult.Stat stat when stat.IsFile && not stat.IsSymbolicLink && stat.Size = pointer.Size ->
        return Continue(ResolvedSide.LfsObject(path, pointer.Size, identityOf stat, pointerIdentity))
    | _ -> return Stop(ResolveOutcome.Blocked(DiffBlocker.LocalContentUnavailable(side, Some pointer.Oid)))
}

/// Pins a revision of the request to a commit and finds its first parent. The revision must be a full object id,
/// so no ref name or revision expression reaches Git. A root commit has no parent.
let private resolveRevision (host: IResolverHost) (revision: RevisionId) : Async<Step<string * string option>> = async {
    let requested = RevisionId.value revision

    if not (Supervisor.isObjectId requested) then
        return Stop(ResolveOutcome.ReadError $"The revision {requested} is not a full lower-case object id.")
    else
        let! commitResult = awaitHost host (host.RunGit [| "rev-parse"; "--verify"; "--quiet"; requested + "^{commit}" |])
        let commitId = (utf8 commitResult.Stdout).Trim()

        if succeeded commitResult && Supervisor.isObjectId commitId then
            let! parentResult = awaitHost host (host.RunGit [| "rev-parse"; "--verify"; "--quiet"; commitId + "^1^{commit}" |])
            let parentId = (utf8 parentResult.Stdout).Trim()

            if succeeded parentResult && Supervisor.isObjectId parentId then
                return Continue(commitId, Some parentId)
            elif parentResult.Error.IsNone && parentResult.Stderr.Trim().Length = 0 then
                // --verify --quiet exits 1 without a message when the commit has no first parent.
                return Continue(commitId, None)
            else
                return Stop(commandFailure "rev-parse" parentResult)
        elif commitResult.Error.IsSome then
            return Stop(commandFailure "rev-parse" commitResult)
        else
            return Stop(ResolveOutcome.ReadError $"The revision {requested} is not a commit in the local repository.")
}

/// Resolves one side from a commit tree. No commit means the side is absent.
let private resolveCommitted
    (host: IResolverHost)
    (input: ResolverInput)
    (side: DiffSide)
    (commitId: string option)
    (path: string)
    : Async<Step<ResolvedSide>> = async {
    match commitId with
    | None -> return Continue ResolvedSide.Absent
    | Some commit ->
        let! listing = awaitHost host (host.RunGit [| "ls-tree"; "-z"; "--full-tree"; commit; "--"; path |])

        if not (succeeded listing) then
            return Stop(commandFailure "ls-tree" listing)
        else
            match findTreeEntry listing.Stdout path with
            | Error message -> return Stop(ResolveOutcome.ReadError message)
            | Ok None -> return Continue ResolvedSide.Absent
            | Ok(Some entry) when entry.Mode = "040000" || entry.Mode = "120000" || entry.Mode = "160000" ->
                return Stop(ResolveOutcome.Blocked(DiffBlocker.NotRegularFile side))
            | Ok(Some entry) when (entry.Mode = "100644" || entry.Mode = "100755") && entry.Kind = "blob" && Supervisor.isObjectId entry.Oid ->
                let! sizeResult = awaitHost host (host.RunGit [| "cat-file"; "-s"; entry.Oid |])

                if not (succeeded sizeResult) then
                    if isMissingObject sizeResult then
                        return Stop(ResolveOutcome.Blocked(DiffBlocker.LocalContentUnavailable(side, Some entry.Oid)))
                    else
                        return Stop(commandFailure "cat-file -s" sizeResult)
                else
                    match Int64.TryParse((utf8 sizeResult.Stdout).Trim()) with
                    | false, _ -> return Stop(ResolveOutcome.ReadError "git cat-file -s returned an unreadable size.")
                    | true, size when size <= int64 MaximumPointerBytes ->
                        let! blob = awaitHost host (host.RunGit [| "cat-file"; "blob"; entry.Oid |])

                        if not (succeeded blob) then
                            if isMissingObject blob then
                                return Stop(ResolveOutcome.Blocked(DiffBlocker.LocalContentUnavailable(side, Some entry.Oid)))
                            else
                                return Stop(commandFailure "cat-file blob" blob)
                        else
                            match tryParseStrict blob.Stdout with
                            | Some pointer -> return! resolveLfsObject host input side pointer None
                            | None -> return Continue(ResolvedSide.GitBlob(entry.Oid, size))
                    | true, size -> return Continue(ResolvedSide.GitBlob(entry.Oid, size))
            | Ok(Some entry) -> return Stop(ResolveOutcome.ReadError $"The committed tree entry has an unexpected mode {entry.Mode} {entry.Kind}.")
}

let private resolveCurrentFile (host: IResolverHost) (input: ResolverInput) (path: string) : Async<Step<ResolvedSide>> = async {
    let! stat = awaitHost host (host.Lstat path)

    match stat with
    | StatResult.Missing -> return Continue ResolvedSide.Absent
    | StatResult.Stat stat when stat.IsSymbolicLink || not stat.IsFile ->
        return Stop(ResolveOutcome.Blocked(DiffBlocker.NotRegularFile DiffSide.Current))
    | StatResult.Stat stat ->
        let identity = identityOf stat

        if stat.Size <= int64 MaximumPointerBytes then
            // Reading one byte past the limit lets the strict parser reject a file that grew since lstat.
            let! prefix = awaitHost host (host.ReadPrefix path (MaximumPointerBytes + 1))

            match tryParseStrict prefix with
            | Some pointer -> return! resolveLfsObject host input DiffSide.Current pointer (Some identity)
            | None -> return Continue(ResolvedSide.WorkingFile(path, identity))
        else
            return Continue(ResolvedSide.WorkingFile(path, identity))
}

let private resolveCurrent (host: IResolverHost) (input: ResolverInput) : Async<Step<ResolvedSide>> = async {
    let path = NodePath.join [| input.RepositoryRoot; input.Path |]

    let insideGitFolder =
        input.Path.Split('/') |> Array.exists (fun segment -> segment.Equals(".git", StringComparison.OrdinalIgnoreCase))

    if insideGitFolder then
        return Stop(ResolveOutcome.ReadError $"The path {input.Path} is inside the .git folder and cannot be diffed.")
    else
        // A symlinked folder on the way can point outside the workspace, and lstat only inspects the last segment.
        let! root = awaitHost host (host.Realpath input.RepositoryRoot)
        let! parent = awaitHost host (host.Realpath(NodePath.dirname path))

        if not (isInsideRoot root parent) then
            return Stop(ResolveOutcome.ReadError $"The path {input.Path} resolves to a folder outside the workspace.")
        else
            return! resolveCurrentFile host input path
}

/// The source of the current side, fixed before any side is read.
type private CurrentSource =
    | WorkingFileSource
    | CommitSource of commitId: string

/// Resolves both sides. The previous side is decided first, so its blocker wins when both sides are blocked.
let resolve (host: IResolverHost) (input: ResolverInput) : Async<ResolveOutcome> = async {
    try
        let! pinned = async {
            match input.Comparison with
            | DiffComparison.WorkingFile ->
                match! resolveHead host with
                | Stop outcome -> return Stop outcome
                | Continue commitId -> return Continue(commitId, WorkingFileSource)
            | DiffComparison.Revision revision ->
                match! resolveRevision host revision with
                | Stop outcome -> return Stop outcome
                | Continue(commitId, parentId) -> return Continue(parentId, CommitSource commitId)
        }

        match pinned with
        | Stop outcome -> return outcome
        | Continue(previousCommitId, currentSource) ->
            let previousPath = input.PreviousPath |> Option.defaultValue input.Path
            let! previous = resolveCommitted host input DiffSide.Previous previousCommitId previousPath

            match previous with
            | Stop outcome -> return outcome
            | Continue previous ->
                let! current =
                    match currentSource with
                    | WorkingFileSource -> resolveCurrent host input
                    | CommitSource commitId -> resolveCommitted host input DiffSide.Current (Some commitId) input.Path

                match current with
                | Stop outcome -> return outcome
                | Continue current ->
                    return
                        ResolveOutcome.Resolved {
                            PreviousCommitId = previousCommitId
                            CurrentCommitId =
                                match currentSource with
                                | WorkingFileSource -> None
                                | CommitSource commitId -> Some commitId
                            Previous = previous
                            Current = current
                        }
    with
    | ResolveCanceled -> return ResolveOutcome.Canceled
    | error ->
        return ResolveOutcome.ReadError error.Message
}
