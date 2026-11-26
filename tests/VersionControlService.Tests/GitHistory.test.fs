module VersionControlService.Tests.GitHistoryTests

open System
open Fable.Core
open VersionControlService.Abstractions
open VersionControlService.Git
open Vitest
open VersionControlService.Tests.Contracts.ProviderHarness.SuiteHelpers

module NodeFileSystem = VersionControlService.Runtime.Node.FileSystem
module NodePath = VersionControlService.Runtime.Node.Path
module NodePositionalFile = VersionControlService.Runtime.Node.PositionalFile
module NodeProcess = VersionControlService.Runtime.Node.Process

[<Import("mkdtemp", "node:fs/promises")>]
let private mkdtemp (prefix: string) : JS.Promise<string> = jsNative

[<Import("tmpdir", "node:os")>]
let private systemTempDirectory () : string = jsNative

type private Person = { Name: string; Email: string; At: int64 }

let private roots = ResizeArray<string>()

/// Every git call of the fixtures gets its own time, so the commit order never depends on the clock.
let mutable private clock = 1700000000L

let private nextPerson () =
    clock <- clock + 60L
    { Name = "Fixture Author"; Email = "fixture@example.invalid"; At = clock }

let private environmentFor (author: Person) (committer: Person) = [|
    "GIT_AUTHOR_NAME", author.Name
    "GIT_AUTHOR_EMAIL", author.Email
    "GIT_AUTHOR_DATE", $"{author.At} +0000"
    "GIT_COMMITTER_NAME", committer.Name
    "GIT_COMMITTER_EMAIL", committer.Email
    "GIT_COMMITTER_DATE", $"{committer.At} +0000"
|]

let private runGit (cwd: string) (environment: (string * string)[]) (stdin: string option) (arguments: string[]) =
    promise {
        let request = {
            NodeProcess.ProcessRequest.create
                "git"
                [| "-c"; "commit.gpgsign=false"; "-c"; "core.autocrlf=false"; yield! arguments |] with
                WorkingDirectory = Some cwd
                StdinData = stdin
                Environment = environment
        }

        let! result =
            NodeProcess.run request (OperationContext.detached "git-history-fixture") |> Async.StartAsPromise

        let command = String.concat " " arguments

        match result with
        | Succeeded outcome when outcome.Value.ExitCode = 0 -> return outcome.Value.StdOut
        | Succeeded outcome -> return failwith $"git {command} failed: {outcome.Value.StdErr}"
        | PartiallySucceeded _
        | Failed _ -> return failwith $"git {command} did not run."
    }

let private git (cwd: string) (arguments: string[]) =
    let person = nextPerson ()
    runGit cwd (environmentFor person person) None arguments

/// A file URL, because git ignores a filter for a clone from a plain local path.
let private fileUrl (path: string) =
    let normalized = path.Replace('\\', '/')
    if normalized.StartsWith "/" then "file://" + normalized else "file:///" + normalized

let private newDirectory () = promise {
    let! root = mkdtemp (NodePath.join [| systemTempDirectory (); "vcs-git-history-" |])
    roots.Add root
    return root
}

let private initRepository (directory: string) = promise {
    do! NodePositionalFile.mkdirRecursive directory
    let! _ = git directory [| "init"; "-q"; "-b"; "main" |]
    return directory
}

/// A repository in a new directory.
let private newRepository () = promise {
    let! root = newDirectory ()
    return! initRepository (NodePath.join [| root; "work" |])
}

/// A repository whose remote "origin" is a bare repository in the same temporary directory.
let private newRepositoryWithOrigin () = promise {
    let! root = newDirectory ()
    let origin = NodePath.join [| root; "origin.git" |]
    do! NodePositionalFile.mkdirRecursive origin
    let! _ = git origin [| "init"; "-q"; "--bare"; "-b"; "main" |]
    let! work = initRepository (NodePath.join [| root; "work" |])
    let! _ = git work [| "remote"; "add"; "origin"; origin |]
    return work
}

let private commitEmpty (work: string) (message: string) = promise {
    let! _ = git work [| "commit"; "-q"; "--allow-empty"; "--allow-empty-message"; "-m"; message |]
    return ()
}

let private commitEmptyAs (work: string) (author: Person) (committer: Person) (message: string) = promise {
    let! _ =
        runGit
            work
            (environmentFor author committer)
            None
            [| "commit"; "-q"; "--allow-empty"; "--allow-empty-message"; "-m"; message |]

    return ()
}

let private writeFile (work: string) (relativePath: string) (content: string) = promise {
    let filePath = NodePath.join [| work; relativePath |]
    do! NodePositionalFile.mkdirRecursive (NodePath.dirname filePath)
    NodeFileSystem.writeFileSync filePath content NodeFileSystem.TextEncoding.Utf8
}

let private commitFile (work: string) (relativePath: string) (content: string) = promise {
    do! writeFile work relativePath content
    let! _ = git work [| "add"; "-A" |]
    let! _ = git work [| "commit"; "-q"; "-m"; $"update {relativePath}" |]
    return ()
}

let private commitAll (work: string) (message: string) = promise {
    let! _ = git work [| "add"; "-A" |]
    let! _ = git work [| "commit"; "-q"; "-m"; message |]
    return ()
}

let private head (work: string) = promise {
    let! output = git work [| "rev-parse"; "HEAD" |]
    return output.Trim()
}

/// The ids of `git rev-list HEAD`, newest first.
let private revList (work: string) = promise {
    let! output = git work [| "rev-list"; "HEAD" |]
    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun line -> line.Trim())
}

let private context name = OperationContext.detached name

let private operationValue operationName result =
    match result with
    | Succeeded outcome -> outcome.Value
    | PartiallySucceeded(_, failure) ->
        failwith $"{operationName} returned partial success: {failure.Code} {failure.Message}"
    | Failed failure -> failwith $"{operationName} failed: {failure.Code} {failure.Message}"

let private operationFailure operationName result =
    match result with
    | Failed failure -> failure
    | Succeeded _
    | PartiallySucceeded _ -> failwith $"{operationName} was expected to fail."

/// The provider and the repository whose history a test reads.
type private OpenedHistory = { Provider: IVersionControl; Repository: Repository }

let private historyOf (work: string) : JS.Promise<OpenedHistory> = promise {
    let provider = GitProvider.create GitProviderOptions.defaults

    let! result =
        executed (provider.Open { RepositoryRoot = work; ConnectionProfileId = None } (context "open-history-fixture"))
        |> Async.StartAsPromise

    return { Provider = provider; Repository = operationValue "Open" result }
}

let private listPageResult (history: OpenedHistory) (cursor: string option) (limit: int) =
    executed (
        history.Provider.History.ListRevisions
            history.Repository
            { Cursor = cursor; Limit = limit }
            (context "list-revisions")
    )
    |> Async.StartAsPromise

let private listPage history cursor limit = promise {
    let! result = listPageResult history cursor limit
    return operationValue "ListRevisions" result
}

let private revisionId (value: string) =
    RevisionId.tryCreate value |> Result.defaultWith failwith

let private changesResult (history: OpenedHistory) (id: string) =
    executed (
        history.Provider.History.GetRevisionChanges history.Repository (revisionId id) (context "revision-changes")
    )
    |> Async.StartAsPromise

let private idsOf (page: RevisionPage) =
    page.Revisions |> Array.map (fun entry -> RevisionId.value entry.Id)

/// The first page of the whole history, with room for every commit of a fixture.
let private listAll history = promise {
    let! page = listPage history None 100
    return page.Revisions
}

let private publicationOf (entry: RevisionEntry) =
    match entry.Publication with
    | LocalOnly -> "local"
    | Published -> "published"
    | PublicationNotApplicable -> "not applicable"

let private describeChange (entry: DiffEntry) =
    let letter =
        match entry.Kind with
        | AddedChange -> "A"
        | ModifiedChange -> "M"
        | DeletedChange -> "D"
        | RenamedChange -> "R"
        | ConflictedChange -> "U"

    match entry.OldPath with
    | Some oldPath -> $"{letter} {RepositoryPath.value oldPath} -> {RepositoryPath.value entry.Path}"
    | None -> $"{letter} {RepositoryPath.value entry.Path}"

let private changesOf history (id: string) = promise {
    let! result = changesResult history id
    return operationValue "GetRevisionChanges" result |> fun summary -> summary.Entries |> Array.map describeChange
}

let private describeRef (reference: LogicalRef) =
    let kind =
        match reference.Kind with
        | LocalRef -> "local"
        | RemoteRef -> "remote"

    $"{reference.Name} {kind} current={reference.IsCurrent} {ProviderRef.value reference.ProviderRef}"

/// Creates `count` commits on main in one git call.
let private fastImportCommits (work: string) (count: int) = promise {
    let records =
        [|
            for index in 1..count do
                let message = $"commit {index}"

                $"commit refs/heads/main\ncommitter Fast Import <fast@example.invalid> {1700000000 + index} +0000\ndata {message.Length}\n{message}\n"
        |]

    let person = nextPerson ()
    let! _ = runGit work (environmentFor person person) (Some(String.concat "" records)) [| "fast-import"; "--quiet" |]
    return ()
}

Vitest.describe (
    "Git history",
    fun () ->
        Vitest.afterAll (
            (fun () -> promise {
                for root in roots do
                    try
                        do!
                            NodeFileSystem.rmAsync
                                root
                                (NodeFileSystem.RmOptions(recursive = true, force = true, maxRetries = 5, retryDelay = 100))
                    with _ ->
                        ()
            }),
            120000
        )

        Vitest.test (
            "a page lists the commits newest first with author, committer, times and message",
            fun () -> promise {
                let! work = newRepository ()
                let author = { Name = "Ada Author"; Email = "ada@example.invalid"; At = 1700000100L }
                let committer = { Name = "Cora Committer"; Email = "cora@example.invalid"; At = 1700000200L }
                do! commitEmpty work "First"
                do! commitEmptyAs work author committer "Second subject\n\nBody line one.\nBody line two."
                let! ids = revList work

                let! history = historyOf work
                let! page = listPage history None 10

                Vitest.expect(idsOf page).toEqual ids
                let newest = page.Revisions[0]
                let oldest = page.Revisions[1]
                Vitest.expect(newest.Summary).toBe "Second subject"
                Vitest.expect(newest.Message).toBe "Second subject\n\nBody line one.\nBody line two."
                Vitest.expect(newest.Author).toEqual (Some { Name = "Ada Author"; Email = Some "ada@example.invalid"; At = 1700000100L })
                Vitest.expect(newest.Committer).toEqual { Name = "Cora Committer"; Email = Some "cora@example.invalid"; At = 1700000200L }
                Vitest.expect(newest.ParentIds |> Array.map RevisionId.value).toEqual [| ids[1] |]
                Vitest.expect(oldest.ParentIds.Length).toBe 0
                Vitest.expect(oldest.Message).toBe "First"
                Vitest.expect(page.NextCursor).toEqual None
            }
        )

        Vitest.test (
            "multi-line, empty and non-ASCII messages and an author name with U+001F come back intact",
            fun () -> promise {
                let! work = newRepository ()
                let odd = { Name = "Un\u001Fit Author"; Email = "odd@example.invalid"; At = 1700000300L }
                do! commitEmpty work "Subject\n\nSecond paragraph\nthird line"
                do! commitEmpty work ""
                do! commitEmpty work "Grüße, 日本語"
                do! commitEmptyAs work odd odd "Odd author"

                let! history = historyOf work
                let! entries = listAll history

                Vitest.expect(entries |> Array.map (fun entry -> entry.Message)).toEqual
                    [| "Odd author"; "Grüße, 日本語"; ""; "Subject\n\nSecond paragraph\nthird line" |]

                Vitest.expect(entries[0].Author |> Option.map (fun author -> author.Name)).toEqual (Some "Un\u001Fit Author")
                Vitest.expect(entries[0].Committer.Name).toBe "Un\u001Fit Author"
                Vitest.expect(entries[2].Summary).toBe ""
            }
        )

        Vitest.test (
            "pages of two over five commits follow git rev-list and the last page has no cursor",
            fun () -> promise {
                let! work = newRepository ()

                for index in 1..5 do
                    do! commitEmpty work $"commit {index}"

                let! ids = revList work
                let! history = historyOf work

                let! first = listPage history None 2
                let! second = listPage history first.NextCursor 2
                let! third = listPage history second.NextCursor 2

                Vitest.expect(Array.concat [ idsOf first; idsOf second; idsOf third ]).toEqual ids
                Vitest.expect(first.NextCursor.IsSome).toBe true
                Vitest.expect(second.NextCursor.IsSome).toBe true
                Vitest.expect(third.NextCursor).toEqual None
            }
        )

        Vitest.test (
            "a commit made between two pages does not change the second page",
            fun () -> promise {
                let! work = newRepository ()

                for index in 1..5 do
                    do! commitEmpty work $"commit {index}"

                let! ids = revList work
                let! history = historyOf work
                let! first = listPage history None 2
                do! commitEmpty work "arrives between the pages"

                let! second = listPage history first.NextCursor 2

                Vitest.expect(idsOf second).toEqual [| ids[2]; ids[3] |]
            }
        )

        Vitest.test (
            "the limit is clamped to 1 and 200",
            fun () -> promise {
                let! work = newRepository ()
                do! fastImportCommits work 201
                let! history = historyOf work

                let! none = listPage history None 0
                let! negative = listPage history None -5
                let! many = listPage history None 1000
                let! rest = listPage history many.NextCursor 1000

                Vitest.expect(none.Revisions.Length).toBe 1
                Vitest.expect(negative.Revisions.Length).toBe 1
                Vitest.expect(many.Revisions.Length).toBe 200
                Vitest.expect(rest.Revisions.Length).toBe 1
                Vitest.expect(rest.NextCursor).toEqual None
            }
        )

        Vitest.test (
            "a branch without a commit gives an empty page",
            fun () -> promise {
                let! work = newRepository ()

                let! history = historyOf work
                let! page = listPage history None 10

                Vitest.expect(page.Revisions.Length).toBe 0
                Vitest.expect(page.NextCursor).toEqual None
            }
        )

        Vitest.test (
            "a detached HEAD lists its history",
            fun () -> promise {
                let! work = newRepository ()
                do! commitEmpty work "one"
                do! commitEmpty work "two"
                let! ids = revList work
                let! _ = git work [| "checkout"; "-q"; "--detach" |]

                let! history = historyOf work
                let! page = listPage history None 10

                Vitest.expect(idsOf page).toEqual ids
                Vitest.expect(page.Revisions[0].Refs |> Array.map describeRef).toEqual [| "main local current=false git-local:main" |]
            }
        )

        Vitest.test (
            "refs list branches with their kinds and leave out tags and origin/HEAD",
            fun () -> promise {
                let! work = newRepository ()
                do! commitEmpty work "one"
                do! commitEmpty work "two"
                let! _ = git work [| "branch"; "feature" |]
                let! _ = git work [| "branch"; "a,b" |]
                let! _ = git work [| "tag"; "v1" |]
                let! _ = git work [| "update-ref"; "refs/remotes/origin/main"; "HEAD" |]
                let! _ = git work [| "symbolic-ref"; "refs/remotes/origin/HEAD"; "refs/remotes/origin/main" |]

                let! history = historyOf work
                let! entries = listAll history

                Vitest.expect(entries[0].Refs |> Array.map describeRef |> Array.sort).toEqual
                    [|
                        "a,b local current=false git-local:a,b"
                        "feature local current=false git-local:feature"
                        "main local current=true git-local:main"
                        "origin/main remote current=false git-remote:origin/main"
                    |]

                Vitest.expect(entries[1].Refs.Length).toBe 0
            }
        )

        Vitest.test (
            "after a push every commit is published and later commits exist only locally",
            fun () -> promise {
                let! work = newRepositoryWithOrigin ()

                for index in 1..3 do
                    do! commitEmpty work $"commit {index}"

                let! _ = git work [| "push"; "-q"; "origin"; "main" |]
                let! history = historyOf work
                let! pushed = listAll history

                do! commitEmpty work "local one"
                do! commitEmpty work "local two"
                let! all = listAll history

                Vitest.expect(pushed |> Array.map publicationOf).toEqual [| "published"; "published"; "published" |]
                Vitest.expect(all |> Array.map publicationOf).toEqual
                    [| "local"; "local"; "published"; "published"; "published" |]
            }
        )

        Vitest.test (
            "a page after the first marks its only-local commits too",
            fun () -> promise {
                let! work = newRepositoryWithOrigin ()
                do! commitEmpty work "pushed one"
                do! commitEmpty work "pushed two"
                let! _ = git work [| "push"; "-q"; "origin"; "main" |]

                for index in 1..5 do
                    do! commitEmpty work $"local {index}"

                let! history = historyOf work
                let! first = listPage history None 2
                let! second = listPage history first.NextCursor 2
                let! third = listPage history second.NextCursor 2

                Vitest.expect(second.Revisions |> Array.map publicationOf).toEqual [| "local"; "local" |]
                Vitest.expect(third.Revisions |> Array.map publicationOf).toEqual [| "local"; "published" |]
            }
        )

        Vitest.test (
            "a local merge of an unpushed branch marks the merge and the commits of the branch",
            fun () -> promise {
                let! work = newRepositoryWithOrigin ()
                do! commitEmpty work "pushed one"
                do! commitEmpty work "pushed two"
                let! _ = git work [| "push"; "-q"; "origin"; "main" |]
                let! _ = git work [| "checkout"; "-q"; "-b"; "topic" |]
                do! commitEmpty work "topic one"
                let! _ = git work [| "checkout"; "-q"; "main" |]
                do! commitEmpty work "main three"
                let! _ = git work [| "merge"; "-q"; "--no-ff"; "-m"; "merge topic"; "topic" |]

                let! history = historyOf work
                let! entries = listAll history

                let described =
                    entries |> Array.map (fun entry -> $"{entry.Summary} {publicationOf entry}") |> Array.sort

                Vitest.expect(described).toEqual
                    (Array.sort
                        [|
                            "merge topic local"
                            "main three local"
                            "topic one local"
                            "pushed two published"
                            "pushed one published"
                        |])
            }
        )

        Vitest.test (
            "a repository without a remote marks every commit as only local",
            fun () -> promise {
                let! work = newRepository ()
                do! commitEmpty work "one"
                do! commitEmpty work "two"

                let! history = historyOf work
                let! entries = listAll history

                Vitest.expect(entries |> Array.map publicationOf).toEqual [| "local"; "local" |]
            }
        )

        Vitest.test (
            "a malformed cursor fails with invalid_cursor",
            fun () -> promise {
                let! work = newRepository ()
                do! commitEmpty work "one"
                let! tip = head work
                let! history = historyOf work

                for cursor in [ "garbage"; "abc:1"; $"{tip}:-1"; $"{tip}:x"; $"{tip}:1:2" ] do
                    let! result = listPageResult history (Some cursor) 10
                    let failure = operationFailure "ListRevisions" result
                    Vitest.expect(failure.Code).toBe "invalid_cursor"
                    Vitest.expect(failure.Category).toEqual Validation
            }
        )

        Vitest.test (
            "the root commit lists every file as added",
            fun () -> promise {
                let! work = newRepository ()
                do! writeFile work "a.txt" "a\n"
                do! writeFile work "dir/b.txt" "b\n"
                do! commitAll work "root"
                let! root = head work

                let! history = historyOf work
                let! changes = changesOf history root

                Vitest.expect(changes).toEqual [| "A a.txt"; "A dir/b.txt" |]
            }
        )

        Vitest.test (
            "a merge commit lists its changes against the first parent",
            fun () -> promise {
                let! work = newRepository ()
                do! commitFile work "a.txt" "a\n"
                let! _ = git work [| "checkout"; "-q"; "-b"; "topic" |]
                do! commitFile work "b.txt" "b\n"
                let! _ = git work [| "checkout"; "-q"; "main" |]
                do! commitFile work "c.txt" "c\n"
                let! _ = git work [| "merge"; "-q"; "--no-ff"; "-m"; "merge topic"; "topic" |]
                let! merge = head work

                let! history = historyOf work
                let! changes = changesOf history merge

                Vitest.expect(changes).toEqual [| "A b.txt" |]
            }
        )

        Vitest.test (
            "an exact and an inexact rename report the old and the new path",
            fun () -> promise {
                let lines = [| for index in 1..20 -> $"line {index}\n" |]
                let! work = newRepository ()
                do! commitFile work "r.txt" (String.concat "" lines)
                let! _ = git work [| "mv"; "r.txt"; "exact.txt" |]
                do! commitAll work "exact rename"
                let! exact = head work
                let! _ = git work [| "mv"; "exact.txt"; "inexact.txt" |]
                do! writeFile work "inexact.txt" (String.concat "" (Array.append [| "changed 1\n"; "changed 2\n" |] lines[2..]))
                do! commitAll work "inexact rename"
                let! inexact = head work
                let! history = historyOf work

                let! exactChanges = changesOf history exact
                let! inexactChanges = changesOf history inexact

                Vitest.expect(exactChanges).toEqual [| "R r.txt -> exact.txt" |]
                Vitest.expect(inexactChanges).toEqual [| "R exact.txt -> inexact.txt" |]
            }
        )

        Vitest.test (
            "a deleted file is reported as deleted",
            fun () -> promise {
                let! work = newRepository ()
                do! commitFile work "a.txt" "a\n"
                let! _ = git work [| "rm"; "-q"; "a.txt" |]
                do! commitAll work "delete"
                let! deletion = head work

                let! history = historyOf work
                let! changes = changesOf history deletion

                Vitest.expect(changes).toEqual [| "D a.txt" |]
            }
        )

        Vitest.test (
            "file names with a space and with non-ASCII characters come back unquoted",
            fun () -> promise {
                let! work = newRepository ()
                do! writeFile work "my file.txt" "a\n"
                do! writeFile work "grüße/日本語.txt" "b\n"
                do! commitAll work "names"
                let! tip = head work

                let! history = historyOf work
                let! changes = changesOf history tip

                Vitest.expect(Array.sort changes).toEqual
                    (Array.sort [| "A my file.txt"; "A grüße/日本語.txt" |])
            }
        )

        Vitest.test (
            "an empty commit has no changes",
            fun () -> promise {
                let! work = newRepository ()
                do! commitFile work "a.txt" "a\n"
                do! commitEmpty work "nothing"
                let! tip = head work

                let! history = historyOf work
                let! changes = changesOf history tip

                Vitest.expect(changes.Length).toBe 0
            }
        )

        Vitest.test (
            "a partial clone lists the files of a commit without downloading anything",
            fun () -> promise {
                let lines = String.concat "" [| for index in 1..20 -> $"line {index}\n" |]
                let! source = newRepository ()
                do! commitFile source "r.txt" lines
                let! _ = git source [| "mv"; "r.txt"; "inexact.txt" |]
                do! writeFile source "inexact.txt" ("changed\n" + lines)
                do! commitAll source "inexact rename"
                let! _ = git source [| "config"; "uploadpack.allowFilter"; "true" |]
                let clone = NodePath.join [| NodePath.dirname source; "clone" |]

                let! _ =
                    git
                        (NodePath.dirname source)
                        [|
                            "-c"
                            "protocol.file.allow=always"
                            "clone"
                            "-q"
                            "--filter=blob:none"
                            "--no-checkout"
                            fileUrl source
                            clone
                        |]

                let countMissing () = promise {
                    let! output =
                        runGit clone [| "GIT_NO_LAZY_FETCH", "1" |] None [| "rev-list"; "--objects"; "--all"; "--missing=print" |]

                    return output.Split('\n') |> Array.filter (fun line -> line.StartsWith "?") |> Array.length
                }

                let! tip = head clone
                let! missingBefore = countMissing ()

                let! history = historyOf clone
                let! changes = changesOf history tip

                let! missingAfter = countMissing ()
                Vitest.expect(missingBefore > 0).toBe true
                Vitest.expect(missingAfter).toBe missingBefore
                Vitest.expect(Array.sort changes).toEqual [| "A inexact.txt"; "D r.txt" |]
            }
        )

        Vitest.test (
            "the changed files and the list do not depend on the log settings of the user",
            fun () -> promise {
                let! work = newRepository ()
                do! commitFile work "a.txt" "a\n"
                let! _ = git work [| "checkout"; "-q"; "-b"; "topic" |]
                do! commitFile work "b.txt" "b\n"
                let! _ = git work [| "checkout"; "-q"; "main" |]
                do! commitFile work "c.txt" "c\n"
                let! _ = git work [| "merge"; "-q"; "--no-ff"; "-m"; "merge topic"; "topic" |]
                let! merge = head work
                let! ids = revList work
                let! _ = git work [| "config"; "log.diffMerges"; "off" |]
                let! _ = git work [| "config"; "log.showRoot"; "false" |]
                let! history = historyOf work

                let! mergeChanges = changesOf history merge
                let! rootChanges = changesOf history (Array.last ids)

                Vitest.expect(mergeChanges).toEqual [| "A b.txt" |]
                Vitest.expect(rootChanges).toEqual [| "A a.txt" |]

                // A commit with a signature header needs no key. With log.showSignature git would print its
                // verification text in front of the first status line.
                do! commitFile work "d.txt" "d\n"
                let! commit = git work [| "cat-file"; "commit"; "HEAD" |]
                let signature = "gpgsig -----BEGIN SSH SIGNATURE-----\n AAAA\n -----END SSH SIGNATURE-----\n"
                let split = commit.IndexOf "\n\n"
                let signed = commit.Substring(0, split + 1) + signature + commit.Substring(split + 1)

                let! written =
                    runGit work [||] (Some signed) [| "hash-object"; "-t"; "commit"; "-w"; "--stdin" |]

                let! _ = git work [| "config"; "log.showSignature"; "true" |]

                let! signedChanges = changesOf history (written.Trim())

                Vitest.expect(signedChanges).toEqual [| "A d.txt" |]

                // The list reads the same log with the same settings in place.
                let! _ = git work [| "update-ref"; "HEAD"; written.Trim() |]
                let! signedIds = revList work
                let! signedEntries = listAll history
                Vitest.expect(signedEntries |> Array.map (fun entry -> RevisionId.value entry.Id)).toEqual signedIds

                let! _ = git work [| "config"; "i18n.logOutputEncoding"; "ISO-8859-1" |]
                do! commitEmpty work "Größe geändert"
                let! allIds = revList work
                let! allEntries = listAll history
                Vitest.expect(allEntries |> Array.map (fun entry -> RevisionId.value entry.Id)).toEqual allIds
                Vitest.expect(allEntries[0].Message).toBe "Größe geändert"
            }
        )

        Vitest.test (
            "a revision that is not a full id fails with invalid_revision",
            fun () -> promise {
                let! work = newRepository ()
                do! commitEmpty work "one"
                let! tip = head work
                let! history = historyOf work

                for revision in [ tip.Substring(0, 10); "HEAD"; tip.ToUpperInvariant() ] do
                    let! result = changesResult history revision
                    Vitest.expect((operationFailure "GetRevisionChanges" result).Code).toBe "invalid_revision"
            }
        )

        Vitest.test (
            "an unknown id fails with revision_not_found",
            fun () -> promise {
                let! work = newRepository ()
                do! commitEmpty work "one"

                let! history = historyOf work
                let! result = changesResult history (String.replicate 40 "a")

                let failure = operationFailure "GetRevisionChanges" result
                Vitest.expect(failure.Code).toBe "revision_not_found"
                Vitest.expect(failure.Category).toEqual NotFound
            }
        )
)
