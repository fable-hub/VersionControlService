/// The history service of the Git provider: pages of the commit log and the files of one commit.
module internal VersionControlService.Git.GitHistory

open System
open VersionControlService.Abstractions

module NodeProcess = VersionControlService.Runtime.Node.Process
module GitInternals = VersionControlService.Git.GitInternals
module Supervisor = VersionControlService.Git.TextDiff.TextDiffSupervisor
module Resolver = VersionControlService.Git.TextDiff.TextDiffSourceResolver

/// Runs git in the workspace with extra environment variables.
type GitRunner =
    string[] -> (string * string)[] -> OperationContext -> Async<Result<NodeProcess.ProcessOutput, OperationFailure>>

/// Turns a nonzero git exit into a failure.
type GitCheck =
    string[] -> Result<NodeProcess.ProcessOutput, OperationFailure> -> Result<NodeProcess.ProcessOutput, OperationFailure>

/// Git before 2.45 ignores GIT_NO_LAZY_FETCH. The empty protocol list keeps it from fetching in that case, as in
/// the text diff supervisor. A partial clone must not download blobs while the history is read.
let private environment = [| "GIT_NO_LAZY_FETCH", "1"; "GIT_ALLOW_PROTOCOL", "" |]

let private tokensPerEntry = 11

let private invalidCursor =
    OperationFailure.create Validation "invalid_cursor" "The cursor does not belong to this history."

let private invalidOutput =
    OperationFailure.create ProviderError "invalid_git_output" "git log returned output of an unexpected shape."

let private toOperationResult (result: Result<'T, OperationFailure>) : OperationResult<'T> =
    match result with
    | Ok value -> OperationResult.succeeded value
    | Error failure -> Failed failure

/// Git prints no time for a commit whose date it cannot read. The epoch stands in, so the page stays listed.
let private parseTime (text: string) =
    match Int64.TryParse text with
    | true, value -> value
    | _ -> 0L

let private signature (name: string) (email: string) (time: string) : RevisionSignature = {
    Name = name
    Email = if email = "" then None else Some email
    At = parseTime time
}

/// Reads `HEAD -> refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1`. It keeps only branches.
let private parseDecorations (decorations: string) : LogicalRef[] =
    decorations.Split([| ", " |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun item ->
        let isCurrent = item.StartsWith "HEAD -> "
        let reference = if isCurrent then item.Substring "HEAD -> ".Length else item

        if reference.StartsWith "refs/heads/" then
            let name = reference.Substring "refs/heads/".Length

            Some {
                Name = name
                ProviderRef = GitInternals.mkProviderRef $"git-local:{name}"
                Kind = LocalRef
                IsCurrent = isCurrent
            }
        elif reference.StartsWith "refs/remotes/" && not (reference.EndsWith "/HEAD") then
            let name = reference.Substring "refs/remotes/".Length

            Some {
                Name = name
                ProviderRef = GitInternals.mkProviderRef $"git-remote:{name}"
                Kind = RemoteRef
                IsCurrent = false
            }
        else
            None)

/// Reads the 11 tokens of one log record: id, parents, author name, email and time, committer name, email and
/// time, decorations, subject, message.
let private parseEntry (localOnly: Set<string>) (tokens: string[]) : RevisionEntry = {
    Id = GitInternals.mkRevisionId tokens[0]
    ParentIds = tokens[1].Split ' ' |> Array.filter (fun parent -> parent <> "") |> Array.map GitInternals.mkRevisionId
    Summary = tokens[9]
    Message = if tokens[10].EndsWith "\n" then tokens[10].Substring(0, tokens[10].Length - 1) else tokens[10]
    Committer = signature tokens[5] tokens[6] tokens[7]
    Author = Some(signature tokens[2] tokens[3] tokens[4])
    Refs = parseDecorations tokens[8]
    Publication = if localOnly.Contains tokens[0] then LocalOnly else Published
}

/// The cursor has the form `<tipCommitId>:<skip>`.
let private parseCursor (cursor: string) : Result<string * int, OperationFailure> =
    match cursor.Split ':' with
    | [| tip; skip |] when Supervisor.isObjectId tip ->
        match Int32.TryParse skip with
        | true, value when value >= 0 -> Ok(tip, value)
        | _ -> Error invalidCursor
    | _ -> Error invalidCursor

/// The commit that HEAD points at, or None while the branch has no commit.
let private resolveTip (run: GitRunner) (check: GitCheck) context : Async<Result<string option, OperationFailure>> =
    async {
        let arguments = [| "rev-parse"; "--verify"; "--quiet"; "--end-of-options"; "HEAD^{commit}" |]
        let! result = run arguments environment context

        match result with
        | Ok output when output.ExitCode = 1 && output.StdOut.Trim() = "" -> return Ok None
        | _ -> return check arguments result |> Result.map (fun output -> Some(output.StdOut.Trim()))
    }

/// The commits among the first `count` of the walk from the tip that no remote-tracking branch of origin reaches.
let private listLocalOnly
    (run: GitRunner)
    (check: GitCheck)
    (tip: string)
    (count: int)
    context
    : Async<Result<Set<string>, OperationFailure>> =
    async {
        let arguments = [| "rev-list"; $"--max-count={count}"; tip; "--not"; "--remotes=origin" |]
        let! result = run arguments environment context

        return
            check arguments result
            |> Result.map (fun output ->
                output.StdOut.Split '\n'
                |> Array.map (fun line -> line.Trim())
                |> Array.filter (fun line -> line <> "")
                |> Set.ofArray)
    }

let private readPage
    (run: GitRunner)
    (check: GitCheck)
    (tip: string)
    (limit: int)
    (skip: int)
    context
    : Async<Result<RevisionPage, OperationFailure>> =
    async {
        let arguments = [|
            "log"
            "-z"
            "--decorate=full"
            "--no-show-signature"
            "--encoding=UTF-8"
            $"--max-count={limit + 1}"
            $"--skip={skip}"
            "--format=%H%x00%P%x00%an%x00%ae%x00%at%x00%cn%x00%ce%x00%ct%x00%D%x00%s%x00%B"
            "--end-of-options"
            tip
        |]

        let! result = run arguments environment context

        match check arguments result with
        | Error failure -> return Error failure
        | Ok output ->
            let split = output.StdOut.Split '\000'
            let tokens = Array.truncate (split.Length - 1) split

            if tokens.Length % tokensPerEntry <> 0 then
                return Error invalidOutput
            else
                let records = Array.chunkBySize tokensPerEntry tokens
                let shown = Array.truncate limit records

                let! localOnly = listLocalOnly run check tip (skip + limit + 1) context

                return
                    localOnly
                    |> Result.map (fun localOnly -> {
                        Revisions = shown |> Array.map (parseEntry localOnly)
                        NextCursor = if records.Length > limit then Some $"{tip}:{skip + limit}" else None
                    })
    }

let private listRevisions
    (run: GitRunner)
    (check: GitCheck)
    (request: ListRevisionsRequest)
    context
    : Async<OperationResult<RevisionPage>> =
    async {
        let limit = max 1 (min 200 request.Limit)

        let! start =
            match request.Cursor with
            | Some cursor -> async { return parseCursor cursor |> Result.map Some }
            | None ->
                async {
                    let! tip = resolveTip run check context
                    return tip |> Result.map (Option.map (fun id -> id, 0))
                }

        match start with
        | Error failure -> return Failed failure
        | Ok None -> return OperationResult.succeeded { Revisions = [||]; NextCursor = None }
        | Ok(Some(tip, skip)) ->
            let! page = readPage run check tip limit skip context
            return toOperationResult page
    }

/// The log settings of the user must not change this output.
let private nameStatusArguments (renames: string) (id: string) =
    [|
        "log"
        "-1"
        "-z"
        "--diff-merges=first-parent"
        "--root"
        "--no-show-signature"
        "--format="
        "--name-status"
        renames
        "--end-of-options"
        id
    |]

let private getRevisionChanges
    (run: GitRunner)
    (check: GitCheck)
    (parseNameStatus: string -> DiffEntry[])
    (revision: RevisionId)
    context
    : Async<OperationResult<DiffSummary>> =
    async {
        let id = RevisionId.value revision

        if not (Supervisor.isObjectId id) then
            return
                Failed(OperationFailure.create Validation "invalid_revision" $"'{id}' is not a full object id.")
        else
            let! verify = run [| "rev-parse"; "--verify"; "--quiet"; "--end-of-options"; $"{id}^{{commit}}" |] environment context

            match verify with
            | Error failure -> return Failed failure
            | Ok output when output.ExitCode <> 0 ->
                return Failed(OperationFailure.create NotFound "revision_not_found" $"Revision '{id}' does not exist.")
            | Ok _ ->
                let summarize arguments result =
                    check arguments result
                    |> Result.map (fun output -> { Entries = parseNameStatus output.StdOut })
                    |> toOperationResult

                let arguments = nameStatusArguments "-M" id
                let! first = run arguments environment context

                match first with
                // Rename detection reads blobs, and a partial clone does not have them.
                | Ok output when output.ExitCode <> 0 && Resolver.stderrReportsMissingObject output.StdErr ->
                    let withoutRenames = nameStatusArguments "--no-renames" id
                    let! second = run withoutRenames environment context
                    return summarize withoutRenames second
                | _ -> return summarize arguments first
    }

/// The history service over a workspace. `parseNameStatus` reads `git log --name-status -z` output.
let create (run: GitRunner) (check: GitCheck) (parseNameStatus: string -> DiffEntry[]) : HistoryService = {
    ListRevisions = listRevisions run check
    GetRevisionChanges = getRevisionChanges run check parseNameStatus
}
