module VersionControlService.Tests.GitProviderContractTests

open System
open Fable.Core
open Fable.Core.JsInterop
open VersionControlService.Abstractions
open VersionControlService.Tests.Contracts
open VersionControlService.Tests.Contracts.ProviderHarness
open VersionControlService.Tests.Contracts.ProviderHarness.SuiteHelpers
open VersionControlService.Tests.NodePath
open Vitest

module GitWorkspaceSession = VersionControlService.Git.GitWorkspaceSession
module GitCredentialStrategy = VersionControlService.Git.GitCredentialStrategy
module GitEngineTypes = VersionControlService.Git.GitEngineTypes
module GitProvider = VersionControlService.Git.GitProvider
module GitProviderOptions = VersionControlService.Git.GitProviderOptions
module GitService = VersionControlService.Git.GitService
module NodeFileSystem = VersionControlService.Runtime.Node.FileSystem
module NodeProcess = VersionControlService.Runtime.Node.Process

let private fsPromisesDynamic: obj = importAll "fs/promises"
let private osDynamic: obj = importAll "os"

let private createTempDirectoryAsync () : JS.Promise<string> = promise {
    let prefix = join [| osDynamic?tmpdir () |> unbox<string>; "vcs-git-harness-" |]
    let! created = fsPromisesDynamic?mkdtemp (prefix) |> unbox<JS.Promise<string>>
    // GitHub's Windows runners hand out TEMP as an 8.3 short path (RUNNER~1). Git reports the
    // long form, so resolve the directory once here and every path comparison agrees.
    return! fsPromisesDynamic?realpath (created) |> unbox<JS.Promise<string>>
}

let private removeDirectoryAsync (path: string) : JS.Promise<unit> = promise {
    // Windows keeps a canceled Git child's working directory busy for a few milliseconds after the
    // child has exited and closed its pipes, so the removal retries on EBUSY.
    let! _ =
        fsPromisesDynamic?rm (
            path,
            createObj [ "recursive" ==> true; "force" ==> true; "maxRetries" ==> 10; "retryDelay" ==> 50 ]
        )
        |> unbox<JS.Promise<obj>>

    return ()
}

let private ensureDirectoryAsync (path: string) : JS.Promise<unit> = promise {
    let! _ =
        fsPromisesDynamic?mkdir (path, createObj [ "recursive" ==> true ])
        |> unbox<JS.Promise<obj>>

    return ()
}

let private writeUtf8FileAsync (path: string) (content: string) : JS.Promise<unit> = promise {
    let parent = dirname path
    do! ensureDirectoryAsync parent
    let! _ = fsPromisesDynamic?writeFile (path, content, "utf8") |> unbox<JS.Promise<obj>>
    return ()
}

[<Emit("Buffer.from($0)")>]
let private bufferFromBytes (_bytes: int[]) : obj = jsNative

let private writeBinaryFileAsync (path: string) (bytes: int[]) : JS.Promise<unit> = promise {
    let parent = dirname path
    do! ensureDirectoryAsync parent
    let! _ = fsPromisesDynamic?writeFile (path, bufferFromBytes bytes) |> unbox<JS.Promise<obj>>
    return ()
}

let private tryReadUtf8FileAsync (path: string) : JS.Promise<string option> = promise {
    try
        let! content = fsPromisesDynamic?readFile (path, "utf8") |> unbox<JS.Promise<string>>
        return Some content
    with _ ->
        return None
}

/// Captures raw bytes as base64 so open tests can assert Git configuration
/// is untouched, rather than merely textually equivalent.
let private readFileBase64Async (path: string) : JS.Promise<string> = promise {
    let! bytes = fsPromisesDynamic?readFile (path) |> unbox<JS.Promise<obj>>
    return bytes?toString("base64") |> unbox<string>
}

[<Emit("Buffer.from($0, 'utf8').toString('base64')")>]
let private utf8Base64 (_value: string) : string = jsNative

let private removeFileAsync (path: string) : JS.Promise<unit> = promise {
    let! _ =
        fsPromisesDynamic?rm (path, createObj [ "force" ==> true ])
        |> unbox<JS.Promise<obj>>

    return ()
}

let private renameAsync (fromPath: string) (toPath: string) : JS.Promise<unit> = promise {
    let! _ = fsPromisesDynamic?rename (fromPath, toPath) |> unbox<JS.Promise<obj>>
    return ()
}

let private createDirectoryJunctionAsync (targetPath: string) (linkPath: string) : JS.Promise<unit> = promise {
    let! _ = fsPromisesDynamic?symlink (targetPath, linkPath, "junction") |> unbox<JS.Promise<obj>>
    return ()
}

[<Emit("(() => { const fs = require('node:fs'); const moduleApi = require('node:module'); const original = fs.renameSync; fs.renameSync = () => { throw new Error('injected atomic rename failure'); }; moduleApi.syncBuiltinESMExports(); return () => { fs.renameSync = original; moduleApi.syncBuiltinESMExports(); }; })()")>]
let private injectRenameSyncFailure () : (unit -> unit) = jsNative

[<Emit("process.execPath")>]
let private nodeExecutablePath: string = jsNative

[<Emit("process.platform === 'win32'")>]
let private isWindowsProcess () : bool = jsNative

[<Emit("process.platform === 'win32' || process.platform === 'darwin'")>]
let private localFileSystemAliasesCase () : bool = jsNative

[<Emit("process.env.PATH || ''")>]
let private currentProcessPath () : string = jsNative

[<Emit("process.env.PATH = $0")>]
let private setCurrentProcessPath (_value: string) : unit = jsNative

[<Emit("process.cwd()")>]
let private currentProcessWorkingDirectory () : string = jsNative

[<Emit("process.chdir($0)")>]
let private setCurrentProcessWorkingDirectory (_value: string) : unit = jsNative

[<Emit("require('node:path').delimiter")>]
let private pathDelimiter: string = jsNative

[<Emit("Object.prototype.hasOwnProperty.call(process.env, $0) ? process.env[$0] : null")>]
let private tryGetProcessEnvironment (_name: string) : string = jsNative

[<Emit("process.env[$0] = $1")>]
let private setProcessEnvironment (_name: string) (_value: string) : unit = jsNative

[<Emit("delete process.env[$0]")>]
let private clearProcessEnvironment (_name: string) : unit = jsNative

[<Emit("""
(() => {
    const childProcess = require('node:child_process');
    const moduleApi = require('node:module');
    const originalSpawn = childProcess.spawn;
    childProcess.spawn = function(command, args, options) {
        if (String(command).toLowerCase() === 'taskkill') {
            setTimeout(() => originalSpawn.call(childProcess, command, args, options), 1500);
            return { on() { return this; }, unref() {} };
        }
        return originalSpawn.call(childProcess, command, args, options);
    };
    moduleApi.syncBuiltinESMExports();
    return () => {
        childProcess.spawn = originalSpawn;
        moduleApi.syncBuiltinESMExports();
    };
})()
""")>]
let private injectDelayedTaskkillSpawn () : (unit -> unit) = jsNative

/// Direct git invocation for harness fixture work (never through the provider).
let private runGitIn
    (cwd: string)
    (environment: (string * string)[])
    (arguments: string[])
    (stdinData: string option)
    : JS.Promise<string> =
    promise {
        let request = {
            NodeProcess.ProcessRequest.create "git" arguments with
                WorkingDirectory = Some cwd
                StdinData = stdinData
                Environment = environment
        }

        let! result = Async.StartAsPromise(NodeProcess.run request (OperationContext.detached "git-harness"))

        match result with
        | Succeeded outcome when outcome.Value.ExitCode = 0 -> return outcome.Value.StdOut
        | Succeeded outcome ->
            let command = String.concat " " arguments
            return failwith $"harness git {command} failed: {outcome.Value.StdErr}"
        | PartiallySucceeded _
        | Failed _ -> return failwith "harness git invocation failed"
    }

let private runGitResultIn (cwd: string) (arguments: string[]) : JS.Promise<NodeProcess.ProcessOutput> = promise {
    let request = {
        NodeProcess.ProcessRequest.create "git" arguments with
            WorkingDirectory = Some cwd
    }

    let! result = Async.StartAsPromise(NodeProcess.run request (OperationContext.detached "git-harness"))

    match result with
    | Succeeded outcome -> return outcome.Value
    | PartiallySucceeded _
    | Failed _ -> return failwith "harness git invocation failed"
}

let private configureUser (repoPath: string) = promise {
    let! _ = runGitIn repoPath [||] [| "config"; "user.name"; "VCS Harness" |] None
    let! _ = runGitIn repoPath [||] [| "config"; "user.email"; "harness@example.org" |] None
    let! _ = runGitIn repoPath [||] [| "config"; "core.autocrlf"; "false" |] None
    return ()
}

let private mkRepositoryPath (value: string) =
    match RepositoryPath.tryCreate value with
    | Ok path -> path
    | Error message -> failwith message

let private gitProviderId =
    match ProviderId.tryCreate "git" with
    | Ok providerId -> providerId
    | Error message -> failwith message

/// Opens the folder through the provider and returns the repository.
let private openFolder
    (provider: IVersionControl)
    (root: string)
    (profile: string option)
    (name: string)
    : JS.Promise<Repository> =
    promise {
        let! result =
            run (executed (provider.Open { RepositoryRoot = root; ConnectionProfileId = profile } (ctx name)))

        return expectValue name result
    }

type private WorkspaceBarriers = {
    BarePath: string
    mutable RaceMutations: TargetMutation[] option
    mutable SlowTransfer: bool
}

/// Builds the real Git harness over isolated temp repositories with a local bare
/// target per workspace.
let createGitHarness () : ProviderTestHarness =
    let tempRoots = ResizeArray<string>()
    let barriersByRoot = System.Collections.Generic.Dictionary<string, WorkspaceBarriers>()
    let mutable counter = 0

    let nextId () =
        counter <- counter + 1
        counter

    /// Applies mutations to a bare target through a scratch clone, like another client.
    let advanceBare (barePath: string) (mutations: TargetMutation[]) = promise {
        let! scratchRoot = createTempDirectoryAsync ()
        tempRoots.Add scratchRoot
        let clonePath = join [| scratchRoot; "advance" |]
        let! _ = runGitIn scratchRoot [||] [| "clone"; barePath; clonePath |] None
        do! configureUser clonePath

        for mutation in mutations do
            match mutation.Content with
            | Some content -> do! writeUtf8FileAsync (join [| clonePath; mutation.Path |]) content
            | None -> do! removeFileAsync (join [| clonePath; mutation.Path |])

        let! _ = runGitIn clonePath [||] [| "add"; "-A" |] None
        let! _ = runGitIn clonePath [||] [| "commit"; "-m"; "external: target advance" |] None
        let! _ = runGitIn clonePath [||] [| "push"; "origin"; "HEAD" |] None
        return ()
    }

    /// Moves the local current branch by one plumbing commit, like another process.
    let advanceLocalBranch (workspaceRoot: string) = promise {
        let! branchRef = runGitIn workspaceRoot [||] [| "symbolic-ref"; "-q"; "HEAD" |] None
        let! tree = runGitIn workspaceRoot [||] [| "rev-parse"; "HEAD^{tree}" |] None
        let! head = runGitIn workspaceRoot [||] [| "rev-parse"; "HEAD" |] None

        let! dummy =
            runGitIn
                workspaceRoot
                [||]
                [|
                    "commit-tree"
                    tree.Trim()
                    "-p"
                    head.Trim()
                    "-m"
                    "race: concurrent local advance"
                |]
                None

        let! _ =
            runGitIn workspaceRoot [||] [| "update-ref"; branchRef.Trim(); dummy.Trim() |] None

        return ()
    }

    let hooks: GitWorkspaceSession.GitSessionHooks = {
        RunBytesProcess = None
        RunProcess = None
        Barrier =
            Some(fun root point context ->
                async {
                    match barriersByRoot.TryGetValue root with
                    | true, barriers ->
                        if point = "publish-precheck-done" then
                            match barriers.RaceMutations with
                            | Some mutations ->
                                barriers.RaceMutations <- None
                                do! Async.AwaitPromise(advanceBare barriers.BarePath mutations)
                            | None -> ()
                        elif point = "finalize-precheck-done" then
                            // Git's finalize destination is the local branch, so the
                            // armed race moves the local ref, not the bare target.
                            match barriers.RaceMutations with
                            | Some _ ->
                                barriers.RaceMutations <- None
                                do! Async.AwaitPromise(advanceLocalBranch root)
                            | None -> ()
                        elif point = "transfer-start" && barriers.SlowTransfer then
                            barriers.SlowTransfer <- false
                            let mutable step = 0

                            context.ReportProgress {
                                PhaseCode = "transfer-bytes"
                                Item = Some "literal[object]*?.bin"
                                Completed = Some(3.0 * 1024.0 * 1024.0 * 1024.0)
                                Total = Some(4.0 * 1024.0 * 1024.0 * 1024.0)
                                DisplayMessage = Some "Transferring large object"
                            }

                            while step < 200 && not (context.Cancellation.IsCancellationRequested()) do
                                do! Async.Sleep 2
                                step <- step + 1

                                context.ReportProgress {
                                    PhaseCode = "transfer"
                                    Item = None
                                    Completed = Some(float step)
                                    Total = Some 200.0
                                    DisplayMessage = Some "Transferring objects"
                                }
                    | false, _ -> ()
                })
    }

    let provider = GitProvider.create { GitProviderOptions.defaults with Hooks = hooks }

    let locationFor (barePath: string) (profile: string option) : RepositoryLocation = {
        ProviderId = gitProviderId
        DisplayName = None
        ProviderLocation = barePath
        ConnectionProfileId = profile
    }

    let openHarnessWorkspace (workspaceRoot: string) (profile: string option) (barePath: string) = promise {
        let! repository = openFolder provider workspaceRoot profile "git-harness-open"

        // The hooks see the root the repository reports, so the barriers are keyed by that root.
        barriersByRoot[repository.Descriptor.RepositoryRoot] <- {
            BarePath = barePath
            RaceMutations = None
            SlowTransfer = false
        }

        return {
            Provider = provider
            Repository = repository
            WriteFile = fun path content -> writeUtf8FileAsync (join [| workspaceRoot; path |]) content
            ReadFile = fun path -> tryReadUtf8FileAsync (join [| workspaceRoot; path |])
            RemoveFile = fun path -> removeFileAsync (join [| workspaceRoot; path |])
            // Windows and macOS default filesystems alias case, and only Windows enforces its
            // reserved names. Linux runners do neither, so the flags follow the platform.
            LocalFileSystemAliasesCase = localFileSystemAliasesCase ()
            LocalFileSystemAliasesNormalization = false
            LocalFileSystemWindowsRules = isWindowsProcess ()
        }
    }

    let createWorkspace () = promise {
        let! root = createTempDirectoryAsync ()
        tempRoots.Add root
        let barePath = join [| root; "origin.git" |]
        let workPath = join [| root; "work" |]

        let! _ = runGitIn root [||] [| "init"; "--bare"; "-b"; "main"; barePath |] None
        let! _ = runGitIn root [||] [| "init"; "-b"; "main"; workPath |] None
        do! configureUser workPath
        do! writeUtf8FileAsync (join [| workPath; "base.txt" |]) "base content\n"
        let! _ = runGitIn workPath [||] [| "add"; "-A" |] None
        let! _ = runGitIn workPath [||] [| "commit"; "-m"; "init: base" |] None
        let! _ = runGitIn workPath [||] [| "remote"; "add"; "origin"; barePath |] None
        let! _ = runGitIn workPath [||] [| "push"; "-u"; "origin"; "main" |] None

        return! openHarnessWorkspace workPath (Some "default-profile") barePath
    }

    {
        Name = "Git"
        Provider = provider
        ExpectedServices = [
            "synchronization"
            "text-diff"
            "conflicts"
            "materialization"
            "storage-policy"
            "maintenance"
            "browser"
            "history"
        ]
        CreateLocation =
            fun () -> promise {
                let! root = createTempDirectoryAsync ()
                tempRoots.Add root
                let barePath = join [| root; $"location-{nextId ()}.git" |]
                let! _ = runGitIn root [||] [| "init"; "--bare"; "-b"; "main"; barePath |] None
                return locationFor barePath None
            }
        CreateUnauthorizedLocation =
            fun () -> promise {
                let! root = createTempDirectoryAsync ()
                tempRoots.Add root
                return locationFor (join [| root; "missing-remote.git" |]) None
            }
        CreateLocalPath =
            fun () -> promise {
                let! root = createTempDirectoryAsync ()
                tempRoots.Add root
                return join [| root; "fresh" |]
            }
        CreateWorkspace = createWorkspace
        CreateLinkedWorkspace =
            fun anchor -> promise {
                let! root = createTempDirectoryAsync ()
                tempRoots.Add root
                let linkedPath = join [| root; "linked" |]
                let barePath = (locationOf anchor).ProviderLocation

                let! _ =
                    runGitIn
                        root
                        [||]
                        [|
                            "clone"
                            "-c"
                            "core.autocrlf=false"
                            barePath
                            linkedPath
                        |]
                        None
                do! configureUser linkedPath

                return! openHarnessWorkspace linkedPath (Some $"linked-profile-{nextId ()}") barePath
            }
        OpenSecondRepository =
            fun anchor ->
                openFolder
                    provider
                    anchor.Repository.Descriptor.RepositoryRoot
                    (locationOf anchor).ConnectionProfileId
                    "git-second-repository"
        AdvanceTarget =
            fun workspace mutations -> advanceBare (locationOf workspace).ProviderLocation mutations
        SeedRevisionOnNewRef =
            fun workspace refName files -> promise {
                let workPath = workspace.Repository.Descriptor.RepositoryRoot
                let! root = createTempDirectoryAsync ()
                tempRoots.Add root
                let indexPath = join [| root; "seed-index" |]
                let environment = [| "GIT_INDEX_FILE", indexPath |]

                let! _ = runGitIn workPath environment [| "read-tree"; "HEAD" |] None

                for path, content in files do
                    let! objectId = runGitIn workPath [||] [| "hash-object"; "-w"; "--stdin" |] (Some content)

                    let! _ =
                        runGitIn
                            workPath
                            environment
                            [|
                                "-c"
                                "core.protectNTFS=false"
                                "update-index"
                                "--add"
                                "--cacheinfo"
                                $"100644,{objectId.Trim()},{path}"
                            |]
                            None

                    ()

                let! treeId = runGitIn workPath environment [| "write-tree" |] None

                let! commitId =
                    runGitIn
                        workPath
                        [||]
                        [|
                            "commit-tree"
                            treeId.Trim()
                            "-p"
                            "HEAD"
                            "-m"
                            $"seed: {refName}"
                        |]
                        None

                let! _ = runGitIn workPath [||] [| "branch"; refName; commitId.Trim() |] None

                match ProviderRef.tryCreate $"git-local:{refName}" with
                | Ok reference -> return reference
                | Error message -> return failwith message
            }
        BreakPublish =
            fun workspace -> promise {
                let barePath = (locationOf workspace).ProviderLocation
                do! renameAsync barePath (barePath + ".offline")
                return ()
            }
        RestorePublish =
            fun workspace -> promise {
                let barePath = (locationOf workspace).ProviderLocation
                do! renameAsync (barePath + ".offline") barePath
                return ()
            }
        ArmDestinationRace =
            fun workspace mutations -> promise {
                barriersByRoot[workspace.Repository.Descriptor.RepositoryRoot].RaceMutations <- Some mutations
                return ()
            }
        ArmSlowTransfer =
            fun workspace -> promise {
                barriersByRoot[workspace.Repository.Descriptor.RepositoryRoot].SlowTransfer <- true
                return ()
            }
        Cleanup =
            fun () -> promise {
                for root in tempRoots do
                    do! removeDirectoryAsync root

                tempRoots.Clear()
                barriersByRoot.Clear()
                return ()
            }
    }

// Register the Git harness with all seven shared conformance profiles under the
// exact reserved describe names ("Git / core profile", ...). The initially failing
// tests are the recorded Task 8 profile baseline, burned down by cycles 8.1-8.6
// and Tasks 9-10.
let private gitHarness = createGitHarness ()

let private registrations = [|
    CoreProviderSuite.register gitHarness
    SynchronizationProviderSuite.register gitHarness
    ConflictProviderSuite.register gitHarness
    ProvisioningProviderSuite.register gitHarness
    OperationalProviderSuite.register gitHarness
    OptionalServiceProviderSuites.register gitHarness
    ConsumerWorkflowSuite.register gitHarness
|]

// The step events and output profiles register for the real providers only. They stay outside the seven shared
// profiles, which the in-memory fake registers too, because the fake reports no progress and no output. The Git
// harness configures no credentials, so the output profile has no secret values to look for.
let private realProviderRegistrations = [|
    StepEventsProviderSuite.register gitHarness
    OutputProviderSuite.register gitHarness [||]
|]

let private expectProviderValue (operationName: string) (result: OperationResult<'T>) : 'T =
    match result with
    | Succeeded outcome -> outcome.Value
    | PartiallySucceeded(_, failure) ->
        failwith $"{operationName} unexpectedly returned partial success ({failure.Code})."
    | Failed failure ->
        failwith $"{operationName} failed ({failure.Category}/{failure.Code}): {failure.Message}"

let private expectProviderFailure (operationName: string) (result: OperationResult<'T>) : OperationFailure =
    match result with
    | Failed failure -> failure
    | Succeeded _
    | PartiallySucceeded _ -> failwith $"Expected {operationName} to fail."

let private expectGitBranches (repositoryPath: string) = promise {
    let! result = GitService.getBranches repositoryPath

    match result with
    | Ok branches -> return branches
    | Error failure -> return failwith $"Listing Git branches failed: {failure.Message}"
}

Vitest.describe (
    "Git branch refs and LFS failure classification",
    fun () ->
        Vitest.test (
            "classifies Git LFS filter failures by startup evidence",
            fun () ->
                let accessDenied =
                    GitService.classifyFailureKind
                        "fatal: x.txt: clean filter 'lfs' failed\nAccess is denied"

                let missingFilterProcess =
                    GitService.classifyFailureKind
                        "git-lfs filter-process: git-lfs: command not found\nexternal filter 'git-lfs filter-process' failed"

                let missingGitLfsCommand = GitService.classifyFailureKind "git: 'lfs' is not a git command"

                Vitest.expect(accessDenied).toEqual (GitEngineTypes.GitFailureKind.Unknown)
                Vitest.expect(missingFilterProcess).toEqual (GitEngineTypes.GitFailureKind.LfsInstallRequired)
                Vitest.expect(missingGitLfsCommand).toEqual (GitEngineTypes.GitFailureKind.LfsInstallRequired)
        )

        Vitest.test (
            "classifies connection failure texts as network or timeout",
            fun () ->
                let refused =
                    GitService.classifyFailureKind
                        "dial tcp 127.0.0.1:1: connectex: No connection could be made because the target machine actively refused it."

                let unknownHost = GitService.classifyFailureKind "dial tcp: lookup nohost.invalid: no such host"

                let unanswered =
                    GitService.classifyFailureKind
                        "connectex: A connection attempt failed because the connected party did not properly respond after a period of time, or established connection failed because connected host has failed to respond."

                let reset =
                    GitService.classifyFailureKind
                        "read tcp 127.0.0.1:50000->127.0.0.1:1: wsarecv: An existing connection was forcibly closed by the remote host."

                let closedByServer =
                    GitService.classifyFailureKind
                        "Fetching reference refs/heads/main\nbatch response: Post \"http://127.0.0.1:1/origin.git/info/lfs/objects/batch\": EOF\nerror: failed to fetch some objects from 'http://127.0.0.1:1/origin.git/info/lfs'"

                let bareEof = GitService.classifyFailureKind "error: unexpected EOF while reading the file"

                Vitest.expect(refused).toEqual (GitEngineTypes.GitFailureKind.Network)
                Vitest.expect(unknownHost).toEqual (GitEngineTypes.GitFailureKind.Network)
                Vitest.expect(unanswered).toEqual (GitEngineTypes.GitFailureKind.Timeout)
                Vitest.expect(reset).toEqual (GitEngineTypes.GitFailureKind.Network)
                Vitest.expect(closedByServer).toEqual (GitEngineTypes.GitFailureKind.Network)
                Vitest.expect(bareEof).toEqual (GitEngineTypes.GitFailureKind.Unknown)
        )

        Vitest.test (
            "preserves current and tracking flags across branch states",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()

                    let! trackedBranches = expectGitBranches workspace.Repository.Descriptor.RepositoryRoot
                    let trackedLocal = trackedBranches |> Array.find (fun branch -> branch.RefName = "main")
                    let trackedRemote = trackedBranches |> Array.find (fun branch -> branch.RefName = "origin/main")

                    Vitest.expect(trackedLocal.IsCurrent).toBe true
                    Vitest.expect(trackedLocal.IsTracking).toBe true
                    Vitest.expect(trackedRemote.IsCurrent).toBe false
                    Vitest.expect(trackedRemote.IsTracking).toBe true

                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "branch"; "--unset-upstream" |]
                            None

                    let! untrackedBranches = expectGitBranches workspace.Repository.Descriptor.RepositoryRoot
                    let untrackedLocal = untrackedBranches |> Array.find (fun branch -> branch.RefName = "main")
                    let untrackedRemote = untrackedBranches |> Array.find (fun branch -> branch.RefName = "origin/main")

                    Vitest.expect(untrackedLocal.IsCurrent).toBe true
                    Vitest.expect(untrackedLocal.IsTracking).toBe false
                    Vitest.expect(untrackedRemote.IsCurrent).toBe false
                    Vitest.expect(untrackedRemote.IsTracking).toBe false

                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "checkout"; "--detach" |]
                            None

                    let! detachedBranches = expectGitBranches workspace.Repository.Descriptor.RepositoryRoot
                    let detachedLocal = detachedBranches |> Array.find (fun branch -> branch.RefName = "main")
                    let detachedRemote = detachedBranches |> Array.find (fun branch -> branch.RefName = "origin/main")

                    Vitest.expect(detachedLocal.IsCurrent).toBe false
                    Vitest.expect(detachedLocal.IsTracking).toBe false
                    Vitest.expect(detachedRemote.IsCurrent).toBe false
                    Vitest.expect(detachedRemote.IsTracking).toBe false
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )
)

Vitest.describe (
    "Git / consumer workflow profile",
    fun () ->
        Vitest.test (
            "configured upstream target identity is exposed through the neutral synchronization state",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "configured-target-ref")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "configured target status" statusResult

                    let target =
                        status.Synchronization
                        |> Option.bind _.TargetRef
                        |> Option.defaultWith (fun () -> failwith "Expected the configured Git upstream target.")

                    Vitest.expect(target.Name).toBe "origin/main"
                    Vitest.expect(ProviderRef.value target.ProviderRef).toBe "git-remote:origin/main"
                    Vitest.expect(target.Kind).toEqual RemoteRef
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "refresh fetches the remote named by the configured upstream",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "remote"; "rename"; "origin"; "backup" |]
                            None

                    do!
                        harness.AdvanceTarget workspace [|
                            {
                                Path = "backup-target.txt"
                                Content = Some "advanced through backup\n"
                            }
                        |]

                    let synchronization =
                        workspace.Provider.Synchronization

                    let! refreshResult =
                        synchronization.Refresh workspace.Repository (OperationContext.detached "refresh-configured-remote")
                        |> executed
                        |> Async.StartAsPromise

                    let refreshed = expectProviderValue "refresh configured upstream remote" refreshResult
                    let target =
                        refreshed.TargetRef
                        |> Option.defaultWith (fun () -> failwith "Expected configured backup target.")

                    Vitest.expect(target.Name).toBe "backup/main"
                    Vitest.expect(refreshed.TargetRevision.IsSome).toBe true

                    let changedPaths =
                        refreshed.RemoteChangedPaths
                        |> Option.defaultWith (fun () -> failwith "Expected refreshed target changes.")
                        |> Array.map RepositoryPath.value

                    Vitest.expect(changedPaths |> Array.contains "backup-target.txt").toBe true
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "a local upstream remains a local target and refresh does not require a remote",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let! _ =
                        harness.SeedRevisionOnNewRef
                            workspace
                            "integration"
                            [| "local-target.txt", "advanced through a local upstream\n" |]

                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "branch"; "--set-upstream-to=integration"; "main" |]
                            None

                    let! expectedTargetRevision =
                        runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "rev-parse"; "refs/heads/integration" |] None

                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "local-upstream-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "local upstream status" statusResult
                    let synchronization =
                        status.Synchronization
                        |> Option.defaultWith (fun () -> failwith "Expected local-upstream synchronization state.")

                    let target =
                        synchronization.TargetRef
                        |> Option.defaultWith (fun () -> failwith "Expected configured local upstream target.")

                    Vitest.expect(target.Name).toBe "integration"
                    Vitest.expect(ProviderRef.value target.ProviderRef).toBe "git-local:integration"
                    Vitest.expect(target.Kind).toEqual LocalRef
                    Vitest.expect(synchronization.TargetRevision |> Option.map RevisionId.value).toEqual (Some(expectedTargetRevision.Trim()))
                    Vitest.expect(synchronization.Relationship).toEqual TargetAhead

                    let refresh =
                        workspace.Provider.Synchronization

                    let! refreshResult =
                        refresh.Refresh workspace.Repository (OperationContext.detached "refresh-local-upstream")
                        |> executed
                        |> Async.StartAsPromise

                    let refreshed = expectProviderValue "refresh local upstream" refreshResult
                    Vitest.expect(refreshed.TargetRef).toEqual (Some target)
                    Vitest.expect(refreshed.TargetRevision |> Option.map RevisionId.value).toEqual (Some(expectedTargetRevision.Trim()))
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "a branch without an upstream exposes no neutral synchronization target",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "branch"; "--unset-upstream" |] None

                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "missing-target-ref")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "missing target status" statusResult
                    let synchronization =
                        status.Synchronization
                        |> Option.defaultWith (fun () -> failwith "Expected Git synchronization state.")

                    Vitest.expect(synchronization.TargetRef).toEqual None
                    Vitest.expect(synchronization.TargetRevision).toEqual None
                    Vitest.expect(synchronization.LocalRevisionCount).toEqual None
                    Vitest.expect(synchronization.TargetRevisionCount).toEqual None
                    Vitest.expect(synchronization.Relationship).toEqual NoTarget
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "a detached workspace exposes no neutral synchronization target",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "checkout"; "--detach" |] None

                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "detached-target-ref")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "detached target status" statusResult
                    let synchronization =
                        status.Synchronization
                        |> Option.defaultWith (fun () -> failwith "Expected Git synchronization state.")

                    Vitest.expect(status.CurrentRef).toEqual None
                    Vitest.expect(synchronization.LocalRevision.IsSome).toBe true
                    Vitest.expect(synchronization.TargetRef).toEqual None
                    Vitest.expect(synchronization.TargetRevision).toEqual None
                    Vitest.expect(synchronization.LocalRevisionCount).toEqual None
                    Vitest.expect(synchronization.TargetRevisionCount).toEqual None
                    Vitest.expect(synchronization.Relationship).toEqual NoTarget
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )
)

Vitest.describe (
    "Git / revision identity",
    fun () ->
        Vitest.test (
            "CreateRevision uses the injected revision identity",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()

                    let identity: GitCredentialStrategy.GitIdentityStrategy = {
                        ResolveIdentity = fun _request ->
                            async {
                                return Some { Name = "Test Author"; Email = "author@example.org" }
                            }
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with RevisionIdentity = identity }

                    let! repository =
                        openFolder
                            provider
                            workspace.Repository.Descriptor.RepositoryRoot
                            (locationOf workspace).ConnectionProfileId
                            "revision-identity-open"

                    do! workspace.WriteFile "revision-identity.txt" "identity content\n"
                    let! status =
                        provider.GetStatus repository (OperationContext.detached "revision-identity-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "revision identity status" status

                    let! revisionResult =
                        provider.CreateRevision repository
                            {
                                Message = "test: revision identity"
                                Paths = [| mkRepositoryPath "revision-identity.txt" |]
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "revision-identity-create")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "revision identity create" revisionResult |> ignore

                    let! commitIdentity =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "log"; "-1"; "--format=%an;%ae;%cn;%ce" |]
                            None

                    Vitest.expect(commitIdentity.Trim()).toBe "Test Author;author@example.org;Test Author;author@example.org"
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "revision identity requests carry the publish remote host and connection profile",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let requests = ResizeArray<GitCredentialStrategy.RevisionIdentityRequest>()

                    let identity: GitCredentialStrategy.GitIdentityStrategy = {
                        ResolveIdentity = fun request ->
                            async {
                                requests.Add request
                                return Some { Name = "Host Author"; Email = "host@example.org" }
                            }
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with RevisionIdentity = identity }

                    let createRevisionIn (repository: Repository) (name: string) = promise {
                        do! workspace.WriteFile $"{name}.txt" $"{name} content\n"

                        let! status =
                            provider.GetStatus repository (OperationContext.detached $"{name}-status")
                            |> executed
                            |> Async.StartAsPromise

                        let status = expectProviderValue $"{name} status" status

                        let! revisionResult =
                            provider.CreateRevision repository
                                {
                                    Message = $"test: {name}"
                                    Paths = [| mkRepositoryPath $"{name}.txt" |]
                                    ExpectedRepositoryVersion = status.RepositoryVersion
                                }
                                (OperationContext.detached $"{name}-create")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue $"{name} create" revisionResult |> ignore
                    }

                    let createRevision (profile: string option) (name: string) = promise {
                        let! repository =
                            openFolder provider workspace.Repository.Descriptor.RepositoryRoot profile $"{name}-open"

                        do! createRevisionIn repository name
                    }

                    let workspaceRoot = workspace.Repository.Descriptor.RepositoryRoot
                    let setOriginUrl (url: string) = runGitIn workspaceRoot [||] [| "remote"; "set-url"; "origin"; url |] None

                    // The harness clone publishes to a local bare repository, which has no host.
                    let defaultProfile = (locationOf workspace).ConnectionProfileId
                    do! createRevision defaultProfile "identity-local"

                    // The configured publish remote decides the host, the same way publish
                    // credentials do.
                    let! _ = setOriginUrl "https://Hub.Example.org/group/project.git"

                    do! createRevision (Some "profile-a") "identity-remote"

                    // scp-style SSH remotes name their host too.
                    let! _ = setOriginUrl "git@scp.example.org:group/project.git"
                    do! createRevision defaultProfile "identity-scp"

                    // A push URL wins over the fetch URL, because that is where git push goes.
                    let! _ = setOriginUrl "https://Hub.Example.org/group/project.git"

                    let! _ =
                        runGitIn
                            workspaceRoot
                            [||]
                            [| "remote"; "set-url"; "--push"; "origin"; "https://push.example.org/group/project.git" |]
                            None

                    do! createRevision defaultProfile "identity-pushurl"

                    // Two distinct push URLs would publish to two hosts, which one identity
                    // cannot serve, so the target counts as invalid and there is no host.
                    let! _ =
                        runGitIn
                            workspaceRoot
                            [||]
                            [| "config"; "--add"; "remote.origin.pushurl"; "https://second.example.org/group/project.git" |]
                            None

                    do! createRevision defaultProfile "identity-two-pushurls"
                    let! _ = runGitIn workspaceRoot [||] [| "config"; "--unset-all"; "remote.origin.pushurl" |] None

                    // insteadOf rewriting changes where git actually pushes, so the host
                    // comes from the rewritten URL.
                    let! _ = setOriginUrl "git@scp.example.org:group/project.git"

                    let! _ =
                        runGitIn
                            workspaceRoot
                            [||]
                            [| "config"; "url.https://rewritten.example.org/.insteadOf"; "git@scp.example.org:" |]
                            None

                    do! createRevision defaultProfile "identity-insteadof"
                    let! _ = runGitIn workspaceRoot [||] [| "config"; "--remove-section"; "url.https://rewritten.example.org/" |] None

                    // An upstream that names no remote is an invalid target: no host at all,
                    // rather than a host the revision will never be published to.
                    let! _ = runGitIn workspaceRoot [||] [| "config"; "branch.main.remote"; "." |] None
                    do! createRevision defaultProfile "identity-invalid-upstream"
                    let! _ = runGitIn workspaceRoot [||] [| "config"; "branch.main.remote"; "origin" |] None

                    // Without any publish remote the location read at open is what remains. The
                    // repository is opened while origin still names the fallback host.
                    let! _ = setOriginUrl "https://fallback.example.org/group/project.git"
                    let! fallbackRepository = openFolder provider workspaceRoot defaultProfile "identity-fallback-open"
                    let! _ = runGitIn workspaceRoot [||] [| "remote"; "remove"; "origin" |] None

                    do! createRevisionIn fallbackRepository "identity-fallback"

                    Vitest.expect(requests.Count).toBe 8

                    for request in requests do
                        Vitest.expect(request.RepositoryRoot).toBe workspaceRoot

                    Vitest.expect(requests.[0].TargetHost).toEqual None
                    Vitest.expect(requests.[0].ConnectionProfileId).toEqual defaultProfile
                    Vitest.expect(requests.[1].TargetHost).toEqual (Some "hub.example.org")
                    Vitest.expect(requests.[1].ConnectionProfileId).toEqual (Some "profile-a")
                    Vitest.expect(requests.[2].TargetHost).toEqual (Some "scp.example.org")
                    Vitest.expect(requests.[3].TargetHost).toEqual (Some "push.example.org")
                    Vitest.expect(requests.[4].TargetHost).toEqual None
                    Vitest.expect(requests.[5].TargetHost).toEqual (Some "rewritten.example.org")
                    Vitest.expect(requests.[6].TargetHost).toEqual None
                    Vitest.expect(requests.[7].TargetHost).toEqual (Some "fallback.example.org")
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "revision identity None falls back to repository configuration",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()

                    let identity: GitCredentialStrategy.GitIdentityStrategy = {
                        ResolveIdentity = fun _request -> async { return None }
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with RevisionIdentity = identity }

                    let! repository =
                        openFolder
                            provider
                            workspace.Repository.Descriptor.RepositoryRoot
                            (locationOf workspace).ConnectionProfileId
                            "configured-identity-open"

                    do! workspace.WriteFile "configured-identity.txt" "configured identity\n"
                    let! status =
                        provider.GetStatus repository (OperationContext.detached "configured-identity-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "configured identity status" status

                    let! revisionResult =
                        provider.CreateRevision repository
                            {
                                Message = "test: configured identity"
                                Paths = [| mkRepositoryPath "configured-identity.txt" |]
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "configured-identity-create")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "configured identity create" revisionResult |> ignore

                    let! commitIdentity =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "log"; "-1"; "--format=%an;%ae;%cn;%ce" |]
                            None

                    Vitest.expect(commitIdentity.Trim()).toBe "VCS Harness;harness@example.org;VCS Harness;harness@example.org"
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "incomplete revision identity fails before git commit",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let mutable currentIdentity: GitCredentialStrategy.RevisionIdentity = {
                        Name = " "
                        Email = "author@example.org"
                    }

                    let identity: GitCredentialStrategy.GitIdentityStrategy = {
                        ResolveIdentity = fun _request -> async { return Some currentIdentity }
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with RevisionIdentity = identity }

                    let! repository =
                        openFolder
                            provider
                            workspace.Repository.Descriptor.RepositoryRoot
                            (locationOf workspace).ConnectionProfileId
                            "incomplete-identity-open"

                    let reject path content contextName = promise {
                        do! workspace.WriteFile path content
                        let! statusResult =
                            provider.GetStatus repository (OperationContext.detached $"{contextName}-status")
                            |> executed
                            |> Async.StartAsPromise

                        let status = expectProviderValue $"{contextName} status" statusResult
                        let! revisionResult =
                            provider.CreateRevision repository
                                {
                                    Message = "test: incomplete revision identity"
                                    Paths = [| mkRepositoryPath path |]
                                    ExpectedRepositoryVersion = status.RepositoryVersion
                                }
                                (OperationContext.detached contextName)
                            |> executed
                            |> Async.StartAsPromise

                        return expectProviderFailure contextName revisionResult
                    }

                    let! blankNameFailure = reject "blank-name.txt" "blank name\n" "blank-name-revision-identity"
                    Vitest.expect(blankNameFailure.Category).toEqual Validation
                    Vitest.expect(blankNameFailure.Code).toBe "identity_missing"
                    Vitest.expect(blankNameFailure.StateChanged).toBe false

                    currentIdentity <- { Name = "Test Author"; Email = "\t" }
                    let! blankEmailFailure = reject "blank-email.txt" "blank email\n" "blank-email-revision-identity"
                    Vitest.expect(blankEmailFailure.Category).toEqual Validation
                    Vitest.expect(blankEmailFailure.Code).toBe "identity_missing"
                    Vitest.expect(blankEmailFailure.StateChanged).toBe false
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "revision identity resolves once per operation instead of at repository open",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let mutable currentIdentity: GitCredentialStrategy.RevisionIdentity = {
                        Name = "First Author"
                        Email = "first@example.org"
                    }

                    let identity: GitCredentialStrategy.GitIdentityStrategy = {
                        ResolveIdentity = fun _request -> async { return Some currentIdentity }
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with RevisionIdentity = identity }

                    let! repository =
                        openFolder
                            provider
                            workspace.Repository.Descriptor.RepositoryRoot
                            (locationOf workspace).ConnectionProfileId
                            "per-operation-identity-open"

                    let create path content message contextName = promise {
                        do! workspace.WriteFile path content
                        let! status =
                            provider.GetStatus repository (OperationContext.detached $"{contextName}-status")
                            |> executed
                            |> Async.StartAsPromise

                        let status = expectProviderValue $"{contextName} status" status

                        let! result =
                            provider.CreateRevision repository
                                {
                                    Message = message
                                    Paths = [| mkRepositoryPath path |]
                                    ExpectedRepositoryVersion = status.RepositoryVersion
                                }
                                (OperationContext.detached contextName)
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue $"{contextName} create" result |> ignore
                    }

                    do! create "first-identity.txt" "first\n" "test: first identity" "first-revision-identity"
                    currentIdentity <- { Name = "Second Author"; Email = "second@example.org" }
                    do! create "second-identity.txt" "second\n" "test: second identity" "second-revision-identity"

                    let! commitIdentities =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "log"; "-2"; "--format=%an;%ae;%cn;%ce" |]
                            None

                    let lines =
                        commitIdentities.Replace("\r\n", "\n").Trim().Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)

                    Vitest.expect(lines).toEqual ([|
                        "Second Author;second@example.org;Second Author;second@example.org"
                        "First Author;first@example.org;First Author;first@example.org"
                    |])
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "missing revision identity fails before ref movement",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let emptyGlobal = join [| workspace.Repository.Descriptor.RepositoryRoot; "empty-global.gitconfig" |]
                    let emptySystem = join [| workspace.Repository.Descriptor.RepositoryRoot; "empty-system.gitconfig" |]
                    do! writeUtf8FileAsync emptyGlobal ""
                    do! writeUtf8FileAsync emptySystem ""

                    let environment = [| "GIT_CONFIG_GLOBAL", emptyGlobal; "GIT_CONFIG_SYSTEM", emptySystem |]
                    let hooks = {
                        GitWorkspaceSession.GitSessionHooks.none with
                            RunProcess =
                                Some(fun request processContext ->
                                    NodeProcess.run
                                        { request with
                                            Environment = Array.append environment request.Environment }
                                        processContext)
                    }

                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot environment [| "config"; "--local"; "--unset-all"; "user.name" |] None
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot environment [| "config"; "--local"; "--unset-all"; "user.email" |] None
                    let identity: GitCredentialStrategy.GitIdentityStrategy = {
                        ResolveIdentity = fun _request -> async { return None }
                    }

                    let provider =
                        GitProvider.create {
                            GitProviderOptions.defaults with
                                Hooks = hooks
                                RevisionIdentity = identity
                        }

                    let! repository =
                        openFolder
                            provider
                            workspace.Repository.Descriptor.RepositoryRoot
                            (locationOf workspace).ConnectionProfileId
                            "missing-identity-open"

                    let! headBefore = runGitIn workspace.Repository.Descriptor.RepositoryRoot environment [| "rev-parse"; "HEAD" |] None
                    do! workspace.WriteFile "missing-identity.txt" "missing identity\n"
                    let! status =
                        provider.GetStatus repository (OperationContext.detached "missing-identity-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "missing identity status" status

                    let! revisionResult =
                        provider.CreateRevision repository
                            {
                                Message = "test: missing identity"
                                Paths = [| mkRepositoryPath "missing-identity.txt" |]
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "missing-identity-create")
                        |> executed
                        |> Async.StartAsPromise

                    let failure = expectProviderFailure "missing revision identity" revisionResult
                    Vitest.expect(failure.Category).toEqual Validation
                    Vitest.expect(failure.Code).toBe "identity_missing"
                    Vitest.expect(failure.RecoveryAction.IsSome).toBe true
                    Vitest.expect(failure.StateChanged).toBe false

                    let! headAfter = runGitIn workspace.Repository.Descriptor.RepositoryRoot environment [| "rev-parse"; "HEAD" |] None
                    Vitest.expect(headAfter.Trim()).toBe (headBefore.Trim())
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )
)

let private processOutput exitCode stdout stderr : NodeProcess.ProcessOutput = {
    ExitCode = exitCode
    StdOut = stdout
    StdErr = stderr
}

Vitest.describe (
    "Git provider validation",
    fun () ->
        Vitest.test (
            "a checkout that git aborts for local changes is not reported as canceled",
            TestOptions(timeout = 120000),
            fun () -> promise {
                // git refuses the switch and ends its error text with "Aborting". The
                // failure classifier must not read that word as a user cancellation.
                let! root = createTempDirectoryAsync ()

                try
                    let repoPath = join [| root; "work" |]
                    let! _ = runGitIn root [||] [| "init"; "-b"; "main"; repoPath |] None
                    do! configureUser repoPath
                    do! writeUtf8FileAsync (join [| repoPath; "base.txt" |]) "main content\n"
                    let! _ = runGitIn repoPath [||] [| "add"; "-A" |] None
                    let! _ = runGitIn repoPath [||] [| "commit"; "-m"; "test: main" |] None
                    let! _ = runGitIn repoPath [||] [| "checkout"; "-b"; "other" |] None
                    do! writeUtf8FileAsync (join [| repoPath; "base.txt" |]) "other content\n"
                    let! _ = runGitIn repoPath [||] [| "commit"; "-am"; "test: other" |] None
                    let! _ = runGitIn repoPath [||] [| "checkout"; "main" |] None
                    do! writeUtf8FileAsync (join [| repoPath; "base.txt" |]) "local edit\n"

                    let provider = GitProvider.create GitProviderOptions.defaults
                    let! repository = openFolder provider repoPath None "aborting-open"

                    let! statusResult =
                        provider.GetStatus repository (OperationContext.detached "aborting-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "aborting status" statusResult

                    let otherRef =
                        match ProviderRef.tryCreate "git-local:other" with
                        | Ok reference -> reference
                        | Error message -> failwith message

                    let! createResult =
                        provider.CreateRef repository
                            {
                                Name = "from-other"
                                BaseRef = Some otherRef
                                SwitchTo = true
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "aborting-create")
                        |> executed
                        |> Async.StartAsPromise

                    let failure = expectProviderFailure "aborted checkout" createResult
                    Vitest.expect(failure.Message.ToLowerInvariant().Contains "aborting").toBe true
                    Vitest.expect(failure.Category).toEqual ProviderError
                    Vitest.expect(failure.Code).toBe "git_failure"
                    Vitest.expect(failure.StateChanged).toBe false

                    let! branch = runGitIn repoPath [||] [| "rev-parse"; "--abbrev-ref"; "HEAD" |] None
                    Vitest.expect(branch.Trim()).toBe "main"
                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )

        Vitest.test (
            "rejects invalid ref names through the provider contract",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()

                    for invalidName in [| ""; "-force"; ".foo"; "foo/.bar"; "foo//bar"; "name\000suffix" |] do
                        let! statusResult =
                            workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "invalid-ref-status")
                            |> executed
                            |> Async.StartAsPromise

                        let status = expectProviderValue "invalid ref status" statusResult

                        let! createResult =
                            workspace.Provider.CreateRef workspace.Repository
                                {
                                    Name = invalidName
                                    BaseRef = None
                                    SwitchTo = false
                                    ExpectedRepositoryVersion = status.RepositoryVersion
                                }
                                (OperationContext.detached "invalid-ref-create")
                            |> executed
                            |> Async.StartAsPromise

                        let failure = expectProviderFailure $"create invalid ref '{invalidName}'" createResult
                        Vitest.expect(failure.Category).toEqual Validation
                        Vitest.expect(failure.Code).toBe "invalid_ref_name"
                        Vitest.expect(failure.StateChanged).toBe false

                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "passes an option-like remote as one literal location and reports rejection structurally",
            fun () -> promise {
                let mutable observedArguments: string[] = [||]
                let providerLocation = "https://example.invalid/repository.git -c protocol.file.allow=always"

                let hooks = {
                    GitWorkspaceSession.GitSessionHooks.none with
                        RunProcess =
                            Some(fun request _ ->
                                async {
                                    observedArguments <- request.Arguments
                                    return OperationResult.succeeded (processOutput 128 "" "fatal: repository rejected")
                                })
                }

                let provider = GitProvider.create { GitProviderOptions.defaults with Hooks = hooks }

                let! result =
                    provider.VerifyLocation
                        {
                            Location = {
                                ProviderId = gitProviderId
                                DisplayName = None
                                ProviderLocation = providerLocation
                                ConnectionProfileId = None
                            }
                            Intents = [| ReadIntent |]
                        }
                        (OperationContext.detached "literal-remote-rejection")
                    |> executed
                    |> Async.StartAsPromise

                let failure = expectProviderFailure "option-like remote rejection" result
                Vitest.expect(failure.Category).toEqual (Validation)
                Vitest.expect(failure.Code).toBe "location_not_allowed"
                Vitest.expect(failure.StateChanged).toBe false
                Vitest.expect(observedArguments.Length).toBe 0
            }
        )

        Vitest.test (
            "provider locations are validated before VerifyLocation, Clone, and Bind run Git",
            fun () -> promise {
                for providerLocation in [|
                    "--upload-pack=doesnotexist"
                    "ext::sh -c whatever"
                    "file:///some/path"
                |] do
                    let observed = ResizeArray<NodeProcess.ProcessRequest>()

                    let hooks = {
                        GitWorkspaceSession.GitSessionHooks.none with
                            RunProcess =
                                Some(fun request _ ->
                                    async {
                                        observed.Add request
                                        return OperationResult.succeeded (processOutput 0 "" "")
                                    })
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with Hooks = hooks }
                    let location = {
                        ProviderId = gitProviderId
                        DisplayName = None
                        ProviderLocation = providerLocation
                        ConnectionProfileId = None
                    }

                    let! verifyResult =
                        Async.StartAsPromise(
                            provider.VerifyLocation
                                {
                                    Location = location
                                    Intents = [| ReadIntent |]
                                }
                                (OperationContext.detached "validate-provider-location")
                            |> executed
                        )

                    let verifyFailure = expectProviderFailure "validated VerifyLocation" verifyResult

                    let! cloneResult =
                        Async.StartAsPromise(
                            provider.Clone
                                {
                                    Location = location
                                    TargetPath = "C:/provider-location-clone-target"
                                    TargetRef = None
                                    MaterializeAllObjects = false
                                }
                                (OperationContext.detached "validate-provider-location-clone")
                            |> executed
                        )

                    let cloneFailure = expectProviderFailure "validated Clone" cloneResult

                    let! bindResult =
                        Async.StartAsPromise(
                            provider.Bind
                                {
                                    RepositoryRoot = "C:/provider-location-bind-workspace"
                                    Location = location
                                }
                                (OperationContext.detached "validate-provider-location-bind")
                            |> executed
                        )

                    let bindFailure = expectProviderFailure "validated Bind" bindResult

                    for failure in [| verifyFailure; cloneFailure; bindFailure |] do
                        Vitest.expect(failure.Category).toEqual (Validation)
                        Vitest.expect(failure.Code).toBe ("location_not_allowed")

                    Vitest.expect(observed.Count).toBe 0
            }
        )

        Vitest.test (
            "provider locations are validated with exact remote-name matching during Bind",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! root = createTempDirectoryAsync ()

                try
                    let originalPath = join [| root; "myorigin.git" |]
                    let boundPath = join [| root; "bound.git" |]
                    let workspacePath = join [| root; "workspace" |]

                    let! _ = runGitIn root [||] [| "init"; "--bare"; "-b"; "main"; originalPath |] None
                    let! _ = runGitIn root [||] [| "init"; "--bare"; "-b"; "main"; boundPath |] None
                    let! _ = runGitIn root [||] [| "init"; "-b"; "main"; workspacePath |] None
                    let! _ = runGitIn workspacePath [||] [| "remote"; "add"; "myorigin"; originalPath |] None
                    let! originalLocation =
                        runGitIn workspacePath [||] [| "remote"; "get-url"; "myorigin" |] None

                    let location = {
                        ProviderId = gitProviderId
                        DisplayName = None
                        ProviderLocation = boundPath
                        ConnectionProfileId = None
                    }

                    let provider = GitProvider.create GitProviderOptions.defaults

                    let! bindResult =
                        Async.StartAsPromise(
                            provider.Bind
                                {
                                    RepositoryRoot = workspacePath
                                    Location = location
                                }
                                (OperationContext.detached "bind-exact-remote-name")
                            |> executed
                        )

                    expectProviderValue "bind with myorigin" bindResult |> ignore

                    let! originLocation =
                        runGitIn workspacePath [||] [| "remote"; "get-url"; "origin" |] None

                    let! unchangedLocation =
                        runGitIn workspacePath [||] [| "remote"; "get-url"; "myorigin" |] None

                    Vitest.expect(originLocation.Trim()).toBe (boundPath.Trim())
                    Vitest.expect(unchangedLocation.Trim()).toBe (originalLocation.Trim())

                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )

        Vitest.test (
            "provider locations are validated when Initialize configures and omits remotes",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! root = createTempDirectoryAsync ()

                try
                    let initializedPath = join [| root; "initialized" |]
                    let localOnlyPath = join [| root; "local-only" |]
                    let localLocationPath = join [| root; "local-location" |]
                    let remoteLocation = {
                        ProviderId = gitProviderId
                        DisplayName = None
                        ProviderLocation = "https://example.invalid/repository.git"
                        ConnectionProfileId = None
                    }

                    let provider = GitProvider.create GitProviderOptions.defaults

                    let! initializedResult =
                        Async.StartAsPromise(
                            provider.Initialize
                                {
                                    TargetPath = initializedPath
                                    Location = Some remoteLocation
                                }
                                (OperationContext.detached "initialize-with-location")
                            |> executed
                        )

                    let initializedRepository = expectProviderValue "initialize with remote location" initializedResult
                    let! configuredRemotes = runGitIn initializedPath [||] [| "remote" |] None
                    let! configuredOrigin =
                        runGitIn initializedPath [||] [| "config"; "--get"; "remote.origin.url" |] None

                    Vitest.expect(initializedRepository.Descriptor.Location).toEqual (Some remoteLocation)
                    Vitest.expect(configuredRemotes.Trim()).toBe ("origin")
                    Vitest.expect(configuredOrigin.Trim()).toBe (remoteLocation.ProviderLocation)

                    let! localOnlyResult =
                        Async.StartAsPromise(
                            provider.Initialize
                                {
                                    TargetPath = localOnlyPath
                                    Location = None
                                }
                                (OperationContext.detached "initialize-without-location")
                            |> executed
                        )

                    let localOnlyRepository = expectProviderValue "initialize without remote location" localOnlyResult
                    let! configuredRemotes =
                        runGitIn localOnlyPath [||] [| "remote" |] None

                    Vitest.expect(configuredRemotes.Trim()).toBe ""

                    let! statusResult =
                        Async.StartAsPromise(
                            provider.GetStatus localOnlyRepository (OperationContext.detached "local-only-status")
                            |> executed
                        )

                    let status = expectProviderValue "local-only initialized status" statusResult
                    let synchronization =
                        status.Synchronization
                        |> Option.defaultWith (fun () -> failwith "Expected local-only synchronization state.")

                    Vitest.expect(synchronization.Relationship).toEqual (NoTarget)

                    let localLocation = {
                        ProviderId = gitProviderId
                        DisplayName = None
                        ProviderLocation = localLocationPath
                        ConnectionProfileId = None
                    }

                    let! localLocationResult =
                        Async.StartAsPromise(
                            provider.Initialize
                                {
                                    TargetPath = localLocationPath
                                    Location = Some localLocation
                                }
                                (OperationContext.detached "initialize-with-local-location")
                            |> executed
                        )

                    let localLocationRepository =
                        expectProviderValue "initialize with local self location" localLocationResult

                    let! localLocationRemotes = runGitIn localLocationPath [||] [| "remote" |] None
                    Vitest.expect(localLocationRemotes.Trim()).toBe ""

                    let! localLocationStatusResult =
                        Async.StartAsPromise(
                            provider.GetStatus
                                localLocationRepository
                                (OperationContext.detached "local-location-initialize-status")
                            |> executed
                        )

                    let localLocationStatus =
                        expectProviderValue "local self initialized status" localLocationStatusResult

                    let localLocationSynchronization =
                        localLocationStatus.Synchronization
                        |> Option.defaultWith (fun () -> failwith "Expected local-location synchronization state.")

                    Vitest.expect(localLocationSynchronization.Relationship).toEqual (NoTarget)

                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )

        Vitest.test (
            "provider locations are validated when Initialize reports repository creation after remote failure",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! root = createTempDirectoryAsync ()

                try
                    let targetPath = join [| root; "remote-add-failure" |]
                    let hooks = {
                        GitWorkspaceSession.GitSessionHooks.none with
                            RunProcess =
                                Some(fun request processContext ->
                                    if request.Arguments |> Array.contains "remote" then
                                        async {
                                            return
                                                OperationResult.succeeded(
                                                    processOutput 128 "" "fatal: injected remote add failure"
                                                )
                                        }
                                    else
                                        NodeProcess.run request processContext)
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with Hooks = hooks }

                    let! result =
                        Async.StartAsPromise(
                            provider.Initialize
                                {
                                    TargetPath = targetPath
                                    Location =
                                        Some {
                                            ProviderId = gitProviderId
                                            DisplayName = None
                                            ProviderLocation = "https://example.invalid/repository.git"
                                            ConnectionProfileId = None
                                        }
                                }
                                (OperationContext.detached "initialize-remote-add-failure")
                            |> executed
                        )

                    let failure = expectProviderFailure "initialize remote add failure" result
                    Vitest.expect(failure.StateChanged).toBe true
                    let! gitDirectory = runGitIn targetPath [||] [| "rev-parse"; "--git-dir" |] None
                    Vitest.expect(gitDirectory.Trim()).toBe ".git"

                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )

        Vitest.test (
            "converts supported origin forms into credential-free repository browser URLs",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let browser =
                        workspace.Provider.RepositoryBrowser

                    let cases = [|
                        "https://github.com/example/version-control-service.git",
                        "https://github.com/example/version-control-service"
                        "https://gitlab.example/group/project",
                        "https://gitlab.example/group/project"
                        "ssh://git@gitlab.example/group/project.git",
                        "https://gitlab.example/group/project"
                        "https://oauth2:secret@gitlab.example/group/project.git",
                        "https://gitlab.example/group/project"
                        "https://localhost:3443/a/b.git", "https://localhost:3443/a/b"
                        "https://host:443/a/b", "https://host/a/b"
                        "ssh://git@host:2222/a/b.git", "https://host/a/b"
                    |]

                    for remoteUrl, expectedUrl in cases do
                        let! _ =
                            runGitIn
                                workspace.Repository.Descriptor.RepositoryRoot
                                [||]
                                [| "remote"; "set-url"; "origin"; remoteUrl |]
                                None

                        let! result =
                            browser.GetRepositoryWebUrl workspace.Repository (OperationContext.detached "repository-browser-url")
                            |> executed
                            |> Async.StartAsPromise

                        Vitest.expect(expectProviderValue "repository browser URL" result).toEqual (Some expectedUrl)

                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "rejects submodule-internal and gitlink selections without moving the ref",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let repoPath = workspace.Repository.Descriptor.RepositoryRoot
                    let childPath = join [| dirname repoPath; "child-repository" |]

                    let! _ = runGitIn (dirname repoPath) [||] [| "init"; "-b"; "main"; childPath |] None
                    do! writeUtf8FileAsync (join [| childPath; "inner.txt" |]) "inner base\n"
                    do! configureUser childPath
                    let! _ = runGitIn childPath [||] [| "add"; "-A" |] None
                    let! _ = runGitIn childPath [||] [| "commit"; "-m"; "test: child base" |] None

                    let! _ =
                        runGitIn
                            repoPath
                            [||]
                            [|
                                "-c"
                                "protocol.file.allow=always"
                                "submodule"
                                "add"
                                childPath.Replace("\\", "/")
                                "sub"
                            |]
                            None

                    let! _ = runGitIn repoPath [||] [| "commit"; "-m"; "test: add submodule" |] None
                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "submodule-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "submodule status" statusResult

                    let! innerResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: submodule-internal selection"
                                Paths = [| RepositoryPath.tryCreate "sub/inner.txt" |> Result.defaultWith failwith |]
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "submodule-internal-selection")
                        |> executed
                        |> Async.StartAsPromise

                    let innerFailure = expectProviderFailure "submodule-internal selection" innerResult
                    Vitest.expect(innerFailure.Category).toEqual Validation
                    Vitest.expect(innerFailure.Code).toBe "submodule_internal_path"

                    let checkedOutChild = join [| repoPath; "sub" |]
                    do! configureUser checkedOutChild
                    do! writeUtf8FileAsync (join [| checkedOutChild; "inner.txt" |]) "inner changed\n"
                    let! _ = runGitIn checkedOutChild [||] [| "add"; "-A" |] None
                    let! _ = runGitIn checkedOutChild [||] [| "commit"; "-m"; "test: advance submodule" |] None
                    let! headBefore = runGitIn repoPath [||] [| "rev-parse"; "HEAD" |] None
                    let! freshStatusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "gitlink-status")
                        |> executed
                        |> Async.StartAsPromise

                    let freshStatus = expectProviderValue "gitlink status" freshStatusResult

                    let! gitlinkResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: gitlink selection"
                                Paths = [| RepositoryPath.tryCreate "sub" |> Result.defaultWith failwith |]
                                ExpectedRepositoryVersion = freshStatus.RepositoryVersion
                            }
                            (OperationContext.detached "gitlink-selection")
                        |> executed
                        |> Async.StartAsPromise

                    let gitlinkFailure = expectProviderFailure "gitlink selection" gitlinkResult
                    Vitest.expect(gitlinkFailure.Code).toBe "submodule_internal_path"
                    let! headAfter = runGitIn repoPath [||] [| "rev-parse"; "HEAD" |] None
                    Vitest.expect(headAfter.Trim()).toBe (headBefore.Trim())
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )
)

Vitest.describe (
    "Git / optional service suites",
    fun () ->
        Vitest.test (
            "storage policy treats repository paths with glob metacharacters literally",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()

                    let storagePolicy =
                        workspace.Provider.StoragePolicy

                    let literalPaths = [|
                        "space name.bin", "spaceXname.bin", "nested/space name.bin"
                        "literal[meta].bin", "literalm.bin", "nested/literal[meta].bin"
                        "literal*.star", "literalX.star", "nested/literal*.star"
                        "literal?.question", "literalX.question", "nested/literal?.question"
                        "tab\tname.bin", "tab name.bin", "nested/tab\tname.bin"
                        "line\nname.bin", "lineXname.bin", "nested/line\nname.bin"
                        "quote\"name.bin", "quoteXname.bin", "nested/quote\"name.bin"
                        "bell\u0007name.bin", "bellXname.bin", "nested/bell\u0007name.bin"
                    |]

                    let repositoryPath value =
                        RepositoryPath.tryCreate value
                        |> Result.defaultWith failwith

                    // RepositoryPath normalizes provider paths to forward slashes;
                    // a backslash therefore never reaches SetPathPolicy.
                    Vitest.expect(RepositoryPath.tryCreate "literal\\path.bin" |> Result.isError).toBe true

                    for literalPath, _, _ in literalPaths do
                        let! result =
                            storagePolicy.SetPathPolicy workspace.Repository
                                (repositoryPath literalPath)
                                true
                                (OperationContext.detached $"track-{literalPath}")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue $"track literal path {literalPath}" result |> ignore

                    for literalPath, decoyPath, nestedDecoyPath in literalPaths do
                        let! attributes =
                            runGitIn
                                workspace.Repository.Descriptor.RepositoryRoot
                                [||]
                                [| "check-attr"; "-z"; "filter"; "--"; literalPath; decoyPath; nestedDecoyPath |]
                                None

                        Vitest.expect(attributes.Contains($"{literalPath}\000filter\000lfs\000")).toBe true
                        Vitest.expect(attributes.Contains($"{decoyPath}\000filter\000unspecified\000")).toBe true
                        Vitest.expect(attributes.Contains($"{nestedDecoyPath}\000filter\000unspecified\000")).toBe true

                    let! attributeFile =
                        tryReadUtf8FileAsync (join [| workspace.Repository.Descriptor.RepositoryRoot; ".gitattributes" |])

                    let attributeFile = attributeFile |> Option.defaultValue ""
                    Vitest.expect(attributeFile.Contains("\"/space name.bin\" filter=lfs")).toBe true
                    Vitest.expect(attributeFile.Contains("\"/literal\\\\[meta\\\\].bin\" filter=lfs")).toBe true
                    Vitest.expect(attributeFile.Contains("\"/literal\\\\*.star\" filter=lfs")).toBe true
                    Vitest.expect(attributeFile.Contains("\"/literal\\\\?.question\" filter=lfs")).toBe true
                    Vitest.expect(attributeFile.Contains("\"/tab\\tname.bin\" filter=lfs")).toBe true
                    Vitest.expect(attributeFile.Contains("\"/line\\nname.bin\" filter=lfs")).toBe true
                    Vitest.expect(attributeFile.Contains("\"/quote\\\"name.bin\" filter=lfs")).toBe true
                    Vitest.expect(attributeFile.Contains("\"/bell\\aname.bin\" filter=lfs")).toBe true

                    for literalPath, _, _ in literalPaths do
                        let! result =
                            storagePolicy.SetPathPolicy workspace.Repository
                                (repositoryPath literalPath)
                                false
                                (OperationContext.detached $"untrack-{literalPath}")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue $"untrack literal path {literalPath}" result |> ignore

                    for literalPath, _, nestedDecoyPath in literalPaths do
                        let! attributes =
                            runGitIn
                                workspace.Repository.Descriptor.RepositoryRoot
                                [||]
                                [| "check-attr"; "-z"; "filter"; "--"; literalPath; nestedDecoyPath |]
                                None

                        Vitest.expect(attributes.Contains($"{literalPath}\000filter\000unspecified\000")).toBe true
                        Vitest.expect(attributes.Contains($"{nestedDecoyPath}\000filter\000unspecified\000")).toBe true

                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "storage policy rejects an attributes parent junction without mutating its outside target",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let attributesPath = join [| workspace.Repository.Descriptor.RepositoryRoot; ".gitattributes" |]
                    let outsideContent = "outside.bin filter=lfs diff=lfs merge=lfs -text\n"
                    do! writeUtf8FileAsync attributesPath outsideContent
                    let! outsideBefore = readFileBase64Async attributesPath
                    let linkedRepo = workspace.Repository.Descriptor.RepositoryRoot + "-junction"
                    do! createDirectoryJunctionAsync workspace.Repository.Descriptor.RepositoryRoot linkedRepo
                    let provider = GitProvider.create GitProviderOptions.defaults

                    // Open takes the root from Git, which resolves the junction. Bind keeps the root as given.
                    let! bindResult =
                        provider.Bind
                            { RepositoryRoot = linkedRepo; Location = locationOf workspace }
                            (OperationContext.detached "bind-junction-workspace")
                        |> executed
                        |> Async.StartAsPromise

                    let linkedRepository = expectProviderValue "bind junction workspace" bindResult

                    let! result =
                        provider.StoragePolicy.SetPathPolicy
                            linkedRepository
                            (RepositoryPath.tryCreate "selected.bin" |> Result.defaultWith failwith)
                            true
                            (OperationContext.detached "track-through-junction")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderFailure "track through junction" result |> ignore
                    let! outsideAfter = readFileBase64Async attributesPath
                    Vitest.expect(outsideAfter).toBe outsideBefore
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "storage policy leaves previous attributes byte-identical when the atomic replacement fails",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let attributesPath = join [| workspace.Repository.Descriptor.RepositoryRoot; ".gitattributes" |]
                    let original = "# preserved bytes\r\nlegacy.bin filter=lfs diff=lfs merge=lfs -text\r\n"
                    do! writeUtf8FileAsync attributesPath original
                    let before = readFileBase64Async attributesPath
                    let restoreRename = injectRenameSyncFailure ()

                    let storagePolicy =
                        workspace.Provider.StoragePolicy

                    let selectedPath = RepositoryPath.tryCreate "selected.bin" |> Result.defaultWith failwith

                    let! result =
                        promise {
                            try
                                return!
                                    storagePolicy.SetPathPolicy workspace.Repository
                                        selectedPath
                                        true
                                        (OperationContext.detached "atomic-attributes-failure")
                                    |> executed
                                    |> Async.StartAsPromise
                            finally
                                restoreRename ()
                        }

                    let failure = expectProviderFailure "atomic attributes replacement" result
                    Vitest.expect(failure.Category).toEqual ProviderError
                    Vitest.expect(failure.StateChanged).toBe false
                    let! before = before
                    let! after = readFileBase64Async attributesPath
                    Vitest.expect(after).toBe before
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "untracking a path preserves legacy unanchored and glob rules byte-for-byte while appending literal unsets",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let attributesPath = join [| workspace.Repository.Descriptor.RepositoryRoot; ".gitattributes" |]
                    let legacyRules =
                        "file.bin filter=lfs diff=lfs merge=lfs -text\r\n*.dat filter=lfs diff=lfs merge=lfs -text\r\n"

                    do! writeUtf8FileAsync attributesPath legacyRules
                    let storagePolicy =
                        workspace.Provider.StoragePolicy

                    for value in [| "nested/file.bin"; "nested/report.dat" |] do
                        let path = RepositoryPath.tryCreate value |> Result.defaultWith failwith
                        let! result =
                            storagePolicy.SetPathPolicy workspace.Repository
                                path
                                false
                                (OperationContext.detached $"preserve-legacy-{value}")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue $"untrack {value}" result |> ignore

                    let expectedAfter =
                        legacyRules
                        + "\"/nested/file.bin\" -filter -diff -merge\r\n"
                        + "\"/nested/report.dat\" -filter -diff -merge\r\n"

                    let! afterBase64 = readFileBase64Async attributesPath
                    Vitest.expect(afterBase64).toBe (utf8Base64 expectedAfter)
                    let! after = tryReadUtf8FileAsync attributesPath
                    let after = after |> Option.defaultValue ""
                    Vitest.expect(after.StartsWith legacyRules).toBe true
                    Vitest.expect(after.Contains("\"/nested/file.bin\" -filter -diff -merge")).toBe true
                    Vitest.expect(after.Contains("\"/nested/report.dat\" -filter -diff -merge")).toBe true

                    let! attributeOutput =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "check-attr"; "-z"; "filter"; "--"; "nested/file.bin"; "nested/report.dat" |]
                            None

                    Vitest.expect(attributeOutput.Contains("nested/file.bin\000filter\000lfs\000")).toBe false
                    Vitest.expect(attributeOutput.Contains("nested/report.dat\000filter\000lfs\000")).toBe false
                    Vitest.expect(attributeOutput.Contains("nested/file.bin\000filter\000unset\000")).toBe true
                    Vitest.expect(attributeOutput.Contains("nested/report.dat\000filter\000unset\000")).toBe true

                    for value in [| "nested/file.bin"; "nested/report.dat" |] do
                        let path = RepositoryPath.tryCreate value |> Result.defaultWith failwith
                        let! enableResult =
                            storagePolicy.SetPathPolicy workspace.Repository
                                path
                                true
                                (OperationContext.detached $"cycle-enable-{value}")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue $"cycle enable {value}" enableResult |> ignore
                        let! disableResult =
                            storagePolicy.SetPathPolicy workspace.Repository
                                path
                                false
                                (OperationContext.detached $"cycle-disable-{value}")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue $"cycle disable {value}" disableResult |> ignore

                    let! cycledAttributes = tryReadUtf8FileAsync attributesPath
                    let cycledAttributes = cycledAttributes |> Option.defaultValue ""

                    for value in [| "nested/file.bin"; "nested/report.dat" |] do
                        let escaped = $"\"/{value}\""
                        let literalLines =
                            cycledAttributes.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            |> Array.filter _.StartsWith(escaped)

                        Vitest.expect(literalLines).toEqual [| $"{escaped} -filter -diff -merge" |]

                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "maintenance honors cancellation requested by its first progress report",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()

                    let maintenance =
                        workspace.Provider.Maintenance

                    let cancellation = OperationCancellation.Source()
                    let mutable progressObserved = false

                    let context =
                        OperationContext.create
                            "cancel-running-maintenance"
                            cancellation.Cancellation
                            (fun progress ->
                                if not progressObserved then
                                    progressObserved <- true
                                    cancellation.Cancel())

                    let! result = maintenance.Deduplicate workspace.Repository context |> executed |> Async.StartAsPromise

                    Vitest.expect(progressObserved).toBe true
                    let failure = expectProviderFailure "canceled running maintenance" result
                    Vitest.expect(failure.Category).toEqual Canceled
                    Vitest.expect(failure.Code).toBe "operation_canceled"
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "maintenance streams process meter percentages",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()
                let! fakeRoot = createTempDirectoryAsync ()
                let completedCount = "3"
                let totalCount = "4"

                try
                    let! workspace = harness.CreateWorkspace()
                    let fakeGitPath = join [| fakeRoot; if isWindowsProcess () then "git.exe" else "git" |]

                    if isWindowsProcess () then
                        let! _ = fsPromisesDynamic?copyFile (nodeExecutablePath, fakeGitPath) |> unbox<JS.Promise<obj>>
                        do!
                            writeUtf8FileAsync
                                (join [| workspace.Repository.Descriptor.RepositoryRoot; "rev-parse" |])
                                "console.log('true');\n"

                        do! writeUtf8FileAsync (join [| workspace.Repository.Descriptor.RepositoryRoot; "status" |]) ""

                        do!
                            writeUtf8FileAsync
                                (join [| workspace.Repository.Descriptor.RepositoryRoot; "lfs" |])
                                ("console.log('deduplicate: 75% ("
                                 + completedCount
                                 + "/"
                                 + totalCount
                                 + ")');\n")
                    else
                        let dispatcher = join [| fakeRoot; "fake-git.js" |]

                        // Same contract as the Windows fake above: answer the repository probe with
                        // "true" and stay silent for status, so the maintenance path reaches dedup.
                        do!
                            writeUtf8FileAsync
                                dispatcher
                                ("if (process.argv.includes('rev-parse')) console.log('true');\nelse if (process.argv.includes('dedup')) console.log('deduplicate: 75% ("
                                 + completedCount
                                 + "/"
                                 + totalCount
                                 + ")');\n")

                        do!
                            writeUtf8FileAsync
                                fakeGitPath
                                $"#!/bin/sh\nexec '{nodeExecutablePath}' '{dispatcher}' \"$@\"\n"

                        let! _ = fsPromisesDynamic?chmod (fakeGitPath, 493) |> unbox<JS.Promise<obj>>
                        ()

                    let reports = ResizeArray<OperationProgress>()
                    let context =
                        OperationContext.create "large-maintenance-progress" OperationCancellation.none reports.Add

                    let maintenance =
                        workspace.Provider.Maintenance

                    let originalPath = currentProcessPath ()
                    let! result =
                        promise {
                            try
                                setCurrentProcessPath (fakeRoot + pathDelimiter + originalPath)
                                return! maintenance.Deduplicate workspace.Repository context |> executed |> Async.StartAsPromise
                            finally
                                setCurrentProcessPath originalPath
                        }

                    expectProviderValue "large maintenance progress" result |> ignore

                    let largeReport =
                        reports
                        |> Seq.find (fun report -> report.Completed.IsSome)

                    Vitest.expect(largeReport.PhaseCode).toBe "maintenance-deduplicate"
                    Vitest.expect(largeReport.Completed).toEqual (Some 75.0)
                    Vitest.expect(largeReport.Total).toEqual (Some 100.0)
                    do! harness.Cleanup()
                    do! removeDirectoryAsync fakeRoot
                with error ->
                    do! harness.Cleanup()
                    do! removeDirectoryAsync fakeRoot
                    return raise error
            }
        )

        Vitest.test (
            "cached LFS object availability is independent from literal-path materialization",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let literalPath = "literal[meta].bin"
                    let attributes = "/literal[[]meta].bin filter=lfs diff=lfs merge=lfs -text\n"
                    do! workspace.WriteFile ".gitattributes" attributes
                    do! workspace.WriteFile literalPath "cached object content\n"

                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "--literal-pathspecs"; "add"; "--"; ".gitattributes"; literalPath |]
                            None

                    let! _ =
                        runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "commit"; "-m"; "test: cached lfs object" |] None

                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "push"; "origin"; "main" |] None

                    let! pointer =
                        runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "show"; $"HEAD:{literalPath}" |] None

                    do! workspace.WriteFile literalPath pointer

                    let materialization =
                        workspace.Provider.ObjectMaterialization

                    let! listResult =
                        materialization.ListObjects workspace.Repository (OperationContext.detached "cached-literal-list")
                        |> executed
                        |> Async.StartAsPromise

                    let objectState =
                        expectProviderValue "list cached literal LFS object" listResult
                        |> Array.find (fun item -> RepositoryPath.value item.Path = literalPath)

                    Vitest.expect(RepositoryPath.value objectState.Path).toBe literalPath
                    Vitest.expect(objectState.IsMaterialized).toBe false
                    Vitest.expect(objectState.IsLocallyAvailable).toBe true
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "preview classification failures are retryable indeterminate results",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    do! workspace.WriteFile "local.txt" "local revision\n"

                    let! beforeRevisionResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "preview-local-status")
                        |> executed
                        |> Async.StartAsPromise

                    let beforeRevision = expectProviderValue "preview local status" beforeRevisionResult
                    let localPath =
                        RepositoryPath.tryCreate "local.txt"
                        |> Result.defaultWith failwith

                    let! localRevisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "local preview revision"
                                Paths = [| localPath |]
                                ExpectedRepositoryVersion = beforeRevision.RepositoryVersion
                            }
                            (OperationContext.detached "preview-local-revision")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "create local preview revision" localRevisionResult |> ignore

                    do!
                        harness.AdvanceTarget workspace [|
                            {
                                Path = "target.txt"
                                Content = Some "target revision\n"
                            }
                        |]

                    let hooks: GitWorkspaceSession.GitSessionHooks = {
                        RunBytesProcess = None
                        RunProcess =
                            Some(fun request context ->
                                if request.Arguments |> Array.contains "merge-tree" then
                                    async {
                                        return
                                            OperationResult.failed(
                                                OperationFailure.create
                                                    ProviderError
                                                    "merge_tree_transport_failed"
                                                    "The merge-tree provider call failed."
                                            )
                                    }
                                else
                                    NodeProcess.run request context)
                        Barrier = None
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with Hooks = hooks }

                    let! repository =
                        openFolder
                            provider
                            workspace.Repository.Descriptor.RepositoryRoot
                            (locationOf workspace).ConnectionProfileId
                            "preview-indeterminate-open"

                    let! previewResult =
                        provider.Synchronization.PreviewUpdate repository (OperationContext.detached "preview-indeterminate")
                        |> executed
                        |> Async.StartAsPromise

                    let failure = expectProviderFailure "indeterminate update preview" previewResult
                    Vitest.expect(failure.Category).toEqual ProviderError
                    Vitest.expect(failure.Code).toBe "preview_indeterminate"
                    Vitest.expect(failure.Retryable).toBe true
                    Vitest.expect(failure.StateChanged).toBe false

                    let changedPathHooks: GitWorkspaceSession.GitSessionHooks = {
                        RunBytesProcess = None
                        RunProcess =
                            Some(fun request context ->
                                if
                                    request.Arguments |> Array.contains "diff"
                                    && request.Arguments |> Array.contains "--name-only"
                                then
                                    async {
                                        return
                                            OperationResult.failed(
                                                OperationFailure.create
                                                    ProviderError
                                                    "changed_path_transport_failed"
                                                    "The changed-path provider call failed."
                                            )
                                    }
                                else
                                    NodeProcess.run request context)
                        Barrier = None
                    }

                    let changedPathProvider =
                        GitProvider.create { GitProviderOptions.defaults with Hooks = changedPathHooks }

                    let! changedPathRepository =
                        openFolder
                            changedPathProvider
                            workspace.Repository.Descriptor.RepositoryRoot
                            (locationOf workspace).ConnectionProfileId
                            "preview-indeterminate-changed-paths-open"

                    let! changedPathPreview =
                        changedPathProvider.Synchronization.PreviewUpdate
                            changedPathRepository
                            (OperationContext.detached "preview-indeterminate-changed-paths")
                        |> executed
                        |> Async.StartAsPromise

                    let changedPathFailure =
                        expectProviderFailure "indeterminate changed-path preview" changedPathPreview

                    Vitest.expect(changedPathFailure.Category).toEqual ProviderError
                    Vitest.expect(changedPathFailure.Code).toBe "preview_indeterminate"
                    Vitest.expect(changedPathFailure.Retryable).toBe true
                    Vitest.expect(changedPathFailure.StateChanged).toBe false
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )
)

Vitest.describe (
    "Git / materialization honors context",
    fun () ->
        Vitest.test (
            "materialization honors context for ListObjects",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    do! workspace.WriteFile ".gitattributes" "materialized.bin filter=lfs diff=lfs merge=lfs -text\n"
                    do! workspace.WriteFile "materialized.bin" "materialization context content\n"
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "add"; "-A" |] None
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "commit"; "-m"; "test: materialization context" |] None
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "push"; "origin"; "main" |] None

                    let materialization =
                        workspace.Provider.ObjectMaterialization

                    let cancellation = OperationCancellation.Source()
                    let mutable transferStarted = false
                    let context =
                        OperationContext.create
                            "materialization-list-cancel"
                            cancellation.Cancellation
                            (fun progress ->
                                if progress.PhaseCode = "lfs-list-transfer" then
                                    transferStarted <- true
                                    cancellation.Cancel())

                    let! result = materialization.ListObjects workspace.Repository context |> executed |> Async.StartAsPromise
                    Vitest.expect(transferStarted).toBe true
                    let failure = expectProviderFailure "canceled LFS listing" result
                    Vitest.expect(failure.Category).toEqual Canceled
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "materialization honors context after a spawned transfer and immediate retry rejects delayed taskkill lock races",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()
                let! fakeRoot = createTempDirectoryAsync ()

                try
                    let! workspace = harness.CreateWorkspace()
                    do! workspace.WriteFile ".gitattributes" "materialized.bin filter=lfs diff=lfs merge=lfs -text\n"
                    do! workspace.WriteFile "materialized.bin" "materialization context content\n"
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "add"; "-A" |] None
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "commit"; "-m"; "test: materialization context" |] None
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "push"; "origin"; "main" |] None

                    let materialization =
                        workspace.Provider.ObjectMaterialization

                    let path = RepositoryPath.tryCreate "materialized.bin" |> Result.defaultWith failwith

                    let! initialDematerialize =
                        materialization.Dematerialize workspace.Repository [| path |] (OperationContext.detached "materialization-prepare")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "prepare materialization context" initialDematerialize |> ignore

                    let materializeCancellation = OperationCancellation.Source()
                    let mutable materializeTransferStarted = false
                    let materializeContext =
                        OperationContext.create
                            "materialization-cancel"
                            materializeCancellation.Cancellation
                            (fun progress ->
                                if progress.PhaseCode = "lfs-materialize-transfer" then
                                    materializeTransferStarted <- true
                                    materializeCancellation.Cancel())

                    let! materializeResult =
                        materialization.Materialize workspace.Repository [| path |] materializeContext |> executed |> Async.StartAsPromise

                    Vitest.expect(materializeTransferStarted).toBe true
                    let materializeFailure = expectProviderFailure "canceled materialization" materializeResult
                    Vitest.expect(materializeFailure.Category).toEqual Canceled

                    let! restoredMaterialization =
                        materialization.Materialize workspace.Repository [| path |] (OperationContext.detached "materialization-restore")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "restore materialization" restoredMaterialization |> ignore

                    let! listingJson =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "lfs"; "ls-files"; "-j" |]
                            None

                    let! pointerText =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [|
                                "lfs"
                                "pointer"
                                "--file"
                                join [| workspace.Repository.Descriptor.RepositoryRoot; "materialized.bin" |]
                            |]
                            None

                    let fakeGitPath = join [| fakeRoot; if isWindowsProcess () then "git.exe" else "git" |]
                    let listingPath = join [| fakeRoot; "listing.json" |]
                    let pointerPath = join [| fakeRoot; "pointer.txt" |]
                    let markerPath = join [| fakeRoot; "slow-transfer" |]

                    do! writeUtf8FileAsync listingPath listingJson
                    do! writeUtf8FileAsync pointerPath pointerText

                    if isWindowsProcess () then
                        let! _ = fsPromisesDynamic?copyFile (nodeExecutablePath, fakeGitPath) |> unbox<JS.Promise<obj>>
                        ()
                    else
                        do!
                            writeUtf8FileAsync
                                fakeGitPath
                                $"#!/bin/sh\nscript=\"$1\"\nshift\nexec '{nodeExecutablePath}' \"$VCS_TEST_LFS_DISPATCH_ROOT/$script\" \"$@\"\n"

                        let! _ = fsPromisesDynamic?chmod (fakeGitPath, 493) |> unbox<JS.Promise<obj>>
                        ()

                    do! writeUtf8FileAsync (join [| workspace.Repository.Descriptor.RepositoryRoot; "status" |]) ""
                    // Like `git checkout HEAD -- <path>` with smudge skipped, write the committed pointer.
                    do!
                        writeUtf8FileAsync
                            (join [| workspace.Repository.Descriptor.RepositoryRoot; "checkout" |])
                            "const fs = require('node:fs');\nconst target = process.argv[process.argv.length - 1].replace(/^:\\([^)]*\\)/, '');\nfs.writeFileSync(target, fs.readFileSync(process.env.VCS_TEST_LFS_POINTER, 'utf8'));\n"
                    do! writeUtf8FileAsync (join [| workspace.Repository.Descriptor.RepositoryRoot; "rev-parse" |]) "console.log('true');\n"

                    do!
                        writeUtf8FileAsync
                            (join [| workspace.Repository.Descriptor.RepositoryRoot; "config" |])
                            "process.stdout.write(process.env.VCS_TEST_LFS_REMOTE + '\\n');\n"

                    // The LFS functions read the effective remote URL with `git remote get-url`.
                    do!
                        writeUtf8FileAsync
                            (join [| workspace.Repository.Descriptor.RepositoryRoot; "remote" |])
                            "process.stdout.write(process.env.VCS_TEST_LFS_REMOTE + '\\n');\n"

                    do!
                        writeUtf8FileAsync
                            (join [| workspace.Repository.Descriptor.RepositoryRoot; "check-attr" |])
                            "process.stdout.write('materialized.bin\\0filter\\0lfs\\0');\n"

                    // Free reads the committed pointer with `git cat-file blob HEAD:<path>`.
                    do!
                        writeUtf8FileAsync
                            (join [| workspace.Repository.Descriptor.RepositoryRoot; "cat-file" |])
                            "process.stdout.write(require('node:fs').readFileSync(process.env.VCS_TEST_LFS_POINTER, 'utf8'));\n"

                    do!
                        writeUtf8FileAsync
                            (join [| workspace.Repository.Descriptor.RepositoryRoot; "lfs" |])
                            """
const fs = require('node:fs');
const net = require('node:net');
const command = process.argv[2] || '';
const markerPath = process.env.VCS_TEST_LFS_MARKER;
const port = Number(process.env.VCS_TEST_LFS_LOCK_PORT);
// Like git-lfs, a dry-run fetch names each object it covers on stderr.
const writeDryRunLine = () => {
    if (command === 'fetch' && process.argv.includes('--dry-run')) {
        const match = fs.readFileSync(process.env.VCS_TEST_LFS_POINTER, 'utf8').match(/oid sha256:([0-9a-f]+)/);
        if (match) process.stderr.write('fetch ' + match[1] + ' => materialized.bin\n');
    }
};
if (command === 'ls-files') {
    process.stdout.write(fs.readFileSync(process.env.VCS_TEST_LFS_LISTING, 'utf8'));
} else if (command === 'pointer') {
    process.stdout.write(fs.readFileSync(process.env.VCS_TEST_LFS_POINTER, 'utf8'));
} else if (command === 'fetch' || command === 'smudge') {
    if (fs.existsSync(markerPath) && fs.readFileSync(markerPath, 'utf8').trim() === command) {
        fs.rmSync(markerPath, { force: true });
        const lock = net.createServer();
        lock.listen(port, '127.0.0.1', () => {
            process.stderr.write('controlled LFS transfer running\n');
            setTimeout(() => lock.close(() => process.exit(0)), 10000);
        });
    } else {
        const probe = net.createServer();
        probe.once('error', error => {
            if (error.code === 'EADDRINUSE') {
                process.stderr.write("fatal: Unable to create '.git/index.lock': File exists.\n");
                process.exit(2);
            } else {
                throw error;
            }
        });
        probe.listen(port, '127.0.0.1', () => probe.close(() => {
            writeDryRunLine();
            process.exit(0);
        }));
    }
} else {
    process.exit(0);
}
"""

                    do! writeUtf8FileAsync markerPath "fetch"

                    let originalPath = currentProcessPath ()
                    let originalWorkingDirectory = currentProcessWorkingDirectory ()
                    let environmentNames = [|
                        "VCS_TEST_LFS_LISTING"
                        "VCS_TEST_LFS_POINTER"
                        "VCS_TEST_LFS_MARKER"
                        "VCS_TEST_LFS_LOCK_PORT"
                        "VCS_TEST_LFS_REMOTE"
                        "VCS_TEST_LFS_DISPATCH_ROOT"
                    |]
                    let originalEnvironment =
                        environmentNames
                        |> Array.map (fun name -> name, (tryGetProcessEnvironment name |> Option.ofObj))

                    let restoreTaskkill =
                        if isWindowsProcess () then Some(injectDelayedTaskkillSpawn ()) else None

                    let! dematerializeResult, retryResult, dematerializeTransferStarted =
                        promise {
                            try
                                setCurrentProcessPath (fakeRoot + pathDelimiter + originalPath)
                                setCurrentProcessWorkingDirectory workspace.Repository.Descriptor.RepositoryRoot
                                setProcessEnvironment "VCS_TEST_LFS_LISTING" listingPath
                                setProcessEnvironment "VCS_TEST_LFS_POINTER" pointerPath
                                setProcessEnvironment "VCS_TEST_LFS_MARKER" markerPath
                                setProcessEnvironment "VCS_TEST_LFS_LOCK_PORT" "43871"
                                setProcessEnvironment "VCS_TEST_LFS_REMOTE" (locationOf workspace).ProviderLocation
                                setProcessEnvironment "VCS_TEST_LFS_DISPATCH_ROOT" workspace.Repository.Descriptor.RepositoryRoot

                                let dematerializeCancellation = OperationCancellation.Source()
                                let mutable transferStarted = false
                                let dematerializeContext =
                                    OperationContext.create
                                        "dematerialization-cancel"
                                        dematerializeCancellation.Cancellation
                                        (fun progress ->
                                            if progress.PhaseCode = "lfs-dematerialize-transfer" && not transferStarted then
                                                transferStarted <- true

                                                Fable.Core.JS.setTimeout
                                                    (fun () -> dematerializeCancellation.Cancel())
                                                    50
                                                |> ignore)

                                let! canceledResult =
                                    materialization.Dematerialize workspace.Repository [| path |] dematerializeContext
                                    |> executed
                                    |> Async.StartAsPromise

                                let! immediateRetry =
                                    materialization.Dematerialize workspace.Repository
                                        [| path |]
                                        (OperationContext.detached "dematerialization-immediate-retry")
                                    |> executed
                                    |> Async.StartAsPromise

                                if isWindowsProcess () then
                                    do! Async.Sleep 750 |> Async.StartAsPromise

                                return canceledResult, immediateRetry, transferStarted
                            finally
                                setCurrentProcessWorkingDirectory originalWorkingDirectory
                                setCurrentProcessPath originalPath
                                restoreTaskkill |> Option.iter (fun restore -> restore ())

                                for name, originalValue in originalEnvironment do
                                    match originalValue with
                                    | Some value -> setProcessEnvironment name value
                                    | None -> clearProcessEnvironment name
                        }

                    Vitest.expect(dematerializeTransferStarted).toBe true
                    let dematerializeFailure = expectProviderFailure "canceled dematerialization" dematerializeResult
                    Vitest.expect(dematerializeFailure.Category).toEqual Canceled
                    expectProviderValue "dematerialization immediate retry" retryResult |> ignore
                    do! harness.Cleanup()
                    do! removeDirectoryAsync fakeRoot
                with error ->
                    do! harness.Cleanup()
                    do! removeDirectoryAsync fakeRoot
                    return raise error
            }
        )

        Vitest.test (
            "materialization honors context with byte progress",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let content = "materialization progress content\n"
                    let expectedSize = float content.Length
                    do! workspace.WriteFile ".gitattributes" "materialized.bin filter=lfs diff=lfs merge=lfs -text\n"
                    do! workspace.WriteFile "materialized.bin" content
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "add"; "-A" |] None
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "commit"; "-m"; "test: materialization progress" |] None
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "push"; "origin"; "main" |] None

                    let materialization =
                        workspace.Provider.ObjectMaterialization

                    let path = RepositoryPath.tryCreate "materialized.bin" |> Result.defaultWith failwith
                    let listReports = ResizeArray<OperationProgress>()
                    let listContext =
                        OperationContext.create "listing-progress" OperationCancellation.none listReports.Add

                    let! listResult = materialization.ListObjects workspace.Repository listContext |> executed |> Async.StartAsPromise
                    expectProviderValue "listing progress" listResult |> ignore

                    let! _ =
                        materialization.Dematerialize workspace.Repository [| path |] (OperationContext.detached "progress-prepare")
                        |> executed
                        |> Async.StartAsPromise

                    let materializeReports = ResizeArray<OperationProgress>()
                    let materializeContext =
                        OperationContext.create
                            "materialization-progress"
                            OperationCancellation.none
                            materializeReports.Add

                    let! materializeResult =
                        materialization.Materialize workspace.Repository [| path |] materializeContext |> executed |> Async.StartAsPromise
                    expectProviderValue "materialization progress" materializeResult |> ignore

                    let! _ =
                        materialization.Dematerialize workspace.Repository [| path |] (OperationContext.detached "progress-reset")
                        |> executed
                        |> Async.StartAsPromise
                    let dematerializeReports = ResizeArray<OperationProgress>()
                    let dematerializeContext =
                        OperationContext.create
                            "dematerialization-progress"
                            OperationCancellation.none
                            dematerializeReports.Add

                    let! dematerializeResult =
                        materialization.Dematerialize workspace.Repository [| path |] dematerializeContext |> executed |> Async.StartAsPromise

                    expectProviderValue "dematerialization progress" dematerializeResult |> ignore

                    let hasByteProgress expectedPhase (reports: ResizeArray<OperationProgress>) =
                        reports
                        |> Seq.exists (fun report ->
                            report.PhaseCode = expectedPhase
                            && report.Completed.IsSome
                            && report.Total = Some expectedSize)

                    Vitest.expect(hasByteProgress "lfs-list" listReports).toBe true
                    Vitest.expect(hasByteProgress "lfs-materialize" materializeReports).toBe true
                    Vitest.expect(hasByteProgress "lfs-dematerialize" dematerializeReports).toBe true
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )
)

Vitest.describe (
    "Git / automatic Git LFS policy",
    fun () ->
        Vitest.test (
            "tracks selected files at or above the default one MiB threshold",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let oneMiB = 1024 * 1024
                    do! workspace.WriteFile "below.bin" (String.replicate (oneMiB - 1) "b")
                    do! workspace.WriteFile "exact.bin" (String.replicate oneMiB "e")
                    do! workspace.WriteFile "above.bin" (String.replicate (oneMiB + 1) "a")

                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "automatic-lfs-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "automatic LFS status" statusResult

                    let paths =
                        [| "below.bin"; "exact.bin"; "above.bin" |]
                        |> Array.map (RepositoryPath.tryCreate >> Result.defaultWith failwith)

                    let! revisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: automatic LFS threshold"
                                Paths = paths
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "automatic-lfs-revision")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic LFS revision" revisionResult |> ignore

                    let! below = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "show"; "HEAD:below.bin" |] None
                    let! exact = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "show"; "HEAD:exact.bin" |] None
                    let! above = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "show"; "HEAD:above.bin" |] None

                    Vitest.expect(below.Contains "git-lfs").toBe false
                    Vitest.expect(exact.Contains "git-lfs").toBe true
                    Vitest.expect(above.Contains "git-lfs").toBe true

                    let! attributes = workspace.ReadFile ".gitattributes"
                    let attributes = attributes |> Option.defaultValue ""
                    Vitest.expect(attributes.Contains "\"/exact.bin\" filter=lfs").toBe true
                    Vitest.expect(attributes.Contains "\"/above.bin\" filter=lfs").toBe true
                    Vitest.expect(attributes.Contains "\"/below.bin\" filter=lfs").toBe false

                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "validates and persists only library-owned automatic LFS settings",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let storagePolicy =
                        workspace.Provider.StoragePolicy

                    let! setResult =
                        storagePolicy.SetSettings workspace.Repository
                            {
                                AutoPolicyThresholdMb = Some 4
                                MaterializeLargeObjects = true
                            }
                            (OperationContext.detached "set-library-lfs-settings")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "set library LFS settings" setResult |> ignore

                    let! threshold =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "config"; "--local"; "--get"; "versioncontrolservice.lfs.autotrackthresholdmb" |]
                            None

                    let! materialize =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "config"; "--local"; "--get"; "versioncontrolservice.lfs.materializelargeobjects" |]
                            None

                    let! localConfig =
                        runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "config"; "--local"; "--list" |] None

                    Vitest.expect(threshold.Trim()).toBe "4"
                    Vitest.expect(materialize.Trim()).toBe "true"
                    let providerSettings =
                        localConfig.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        |> Array.filter (fun entry -> entry.StartsWith "versioncontrolservice.lfs.")

                    Vitest.expect(providerSettings.Length).toBe 2

                    let fourMiB = 4 * 1024 * 1024
                    do! workspace.WriteFile "configured-below.bin" (String.replicate (fourMiB - 1) "b")
                    do! workspace.WriteFile "configured exact [file].bin" (String.replicate fourMiB "e")

                    let! configuredStatusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "configured-lfs-status")
                        |> executed
                        |> Async.StartAsPromise

                    let configuredStatus = expectProviderValue "configured LFS status" configuredStatusResult

                    let configuredPaths =
                        [| "configured-below.bin"; "configured exact [file].bin" |]
                        |> Array.map (RepositoryPath.tryCreate >> Result.defaultWith failwith)

                    let! configuredRevisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: configured automatic LFS threshold"
                                Paths = configuredPaths
                                ExpectedRepositoryVersion = configuredStatus.RepositoryVersion
                            }
                            (OperationContext.detached "configured-lfs-revision")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "configured automatic LFS revision" configuredRevisionResult |> ignore

                    let! configuredBelow =
                        runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "show"; "HEAD:configured-below.bin" |] None

                    let! configuredExact =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "show"; "HEAD:configured exact [file].bin" |]
                            None

                    Vitest.expect(configuredBelow.Contains "git-lfs").toBe false
                    Vitest.expect(configuredExact.Contains "git-lfs").toBe true

                    let! configuredAttributes = workspace.ReadFile ".gitattributes"
                    let configuredAttributes = configuredAttributes |> Option.defaultValue ""
                    Vitest.expect(configuredAttributes.Contains "\"/configured exact \\\\[file\\\\].bin\" filter=lfs").toBe true

                    let! literalRuleResult =
                        storagePolicy.SetPathPolicy workspace.Repository
                            (RepositoryPath.tryCreate "literal[auto]* name.bin" |> Result.defaultWith failwith)
                            true
                            (OperationContext.detached "literal-rule-policy")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "literal rule policy" literalRuleResult |> ignore
                    let! literalAttributes = workspace.ReadFile ".gitattributes"

                    Vitest.expect(
                        literalAttributes
                        |> Option.exists _.Contains("\"/literal\\\\[auto\\\\]\\\\* name.bin\" filter=lfs diff=lfs merge=lfs -text")
                    ).toBe true

                    Vitest.expect(RepositoryPath.tryCreate "literal\\path.bin" |> Result.isError).toBe true

                    for invalidThreshold in [| 0; -1 |] do
                        let! invalidResult =
                            storagePolicy.SetSettings workspace.Repository
                                {
                                    AutoPolicyThresholdMb = Some invalidThreshold
                                    MaterializeLargeObjects = false
                                }
                                (OperationContext.detached $"invalid-lfs-threshold-{invalidThreshold}")
                            |> executed
                            |> Async.StartAsPromise

                        let failure = expectProviderFailure "invalid LFS threshold" invalidResult
                        Vitest.expect(failure.Category).toEqual Validation
                        Vitest.expect(failure.StateChanged).toBe false

                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [|
                                "config"
                                "--local"
                                "versioncontrolservice.lfs.autotrackthresholdmb"
                                "1.5"
                            |]
                            None

                    do! workspace.WriteFile "fractional.bin" (String.replicate (1024 * 1024) "f")

                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "fractional-threshold-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "fractional threshold status" statusResult
                    let fractionalPath =
                        RepositoryPath.tryCreate "fractional.bin"
                        |> Result.defaultWith failwith

                    let! revisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: reject fractional threshold"
                                Paths = [| fractionalPath |]
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "fractional-threshold-revision")
                        |> executed
                        |> Async.StartAsPromise

                    let fractionalFailure = expectProviderFailure "fractional LFS threshold" revisionResult
                    Vitest.expect(fractionalFailure.Category).toEqual Validation
                    Vitest.expect(fractionalFailure.StateChanged).toBe false
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "automatic Git LFS policy reuses committed wildcard attributes without appending rules",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let attributesContent = "# committed policy\n*.bin filter=lfs diff=lfs merge=lfs -text\n"
                    let attributesPath = join [| workspace.Repository.Descriptor.RepositoryRoot; ".gitattributes" |]
                    do! workspace.WriteFile ".gitattributes" attributesContent
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "add"; ".gitattributes" |] None
                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "commit"; "-m"; "test: commit wildcard LFS policy" |]
                            None

                    let! attributesBefore = readFileBase64Async attributesPath
                    let oneMiB = 1024 * 1024

                    for index, marker in [| 1, "a"; 2, "b"; 3, "c" |] do
                        do! workspace.WriteFile "data/big.bin" (String.replicate oneMiB marker)

                        let! statusResult =
                            workspace.Provider.GetStatus workspace.Repository (OperationContext.detached $"automatic-policy-covered-status-{index}")
                            |> executed
                            |> Async.StartAsPromise

                        let status = expectProviderValue "automatic policy covered status" statusResult
                        let! revisionResult =
                            workspace.Provider.CreateRevision workspace.Repository
                                {
                                    Message = $"test: automatic policy covered commit {index}"
                                    Paths = [| mkRepositoryPath "data/big.bin" |]
                                    ExpectedRepositoryVersion = status.RepositoryVersion
                                }
                                (OperationContext.detached $"automatic-policy-covered-revision-{index}")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue "automatic policy covered revision" revisionResult |> ignore
                        let! committed = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "show"; "HEAD:data/big.bin" |] None
                        let! attributesAfter = readFileBase64Async attributesPath
                        Vitest.expect(committed.Contains "git-lfs").toBe true
                        Vitest.expect(attributesAfter).toBe attributesBefore

                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "automatic Git LFS policy ignores dirty irrelevant attributes",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let committedAttributes = "*.bin filter=lfs diff=lfs merge=lfs -text\n"
                    let dirtyAttributes = committedAttributes + "# unrelated consumer edit\n"
                    do! workspace.WriteFile ".gitattributes" committedAttributes
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "add"; ".gitattributes" |] None
                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "commit"; "-m"; "test: commit wildcard policy" |]
                            None

                    do! workspace.WriteFile ".gitattributes" dirtyAttributes
                    do! workspace.WriteFile "data/big.bin" (String.replicate (1024 * 1024) "d")

                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "automatic-policy-dirty-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "automatic policy dirty status" statusResult
                    let! revisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: automatic policy ignores dirty attributes"
                                Paths = [| mkRepositoryPath "data/big.bin" |]
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "automatic-policy-dirty-revision")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy dirty revision" revisionResult |> ignore
                    let! committed = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "show"; "HEAD:data/big.bin" |] None
                    let! attributesAfter = tryReadUtf8FileAsync (join [| workspace.Repository.Descriptor.RepositoryRoot; ".gitattributes" |])
                    Vitest.expect(committed.Contains "git-lfs").toBe true
                    Vitest.expect(attributesAfter).toEqual (Some dirtyAttributes)
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "automatic Git LFS policy disables a wildcard-covered path with a literal unset",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    do! workspace.WriteFile ".gitattributes" "*.bin filter=lfs diff=lfs merge=lfs -text\n"
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "add"; ".gitattributes" |] None
                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "commit"; "-m"; "test: commit wildcard disable policy" |]
                            None

                    let storagePolicy =
                        workspace.Provider.StoragePolicy

                    let! result =
                        storagePolicy.SetPathPolicy workspace.Repository
                            (mkRepositoryPath "data/big.bin")
                            false
                            (OperationContext.detached "automatic-policy-disable-wildcard")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy disable wildcard" result |> ignore
                    let! attributes =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "check-attr"; "-z"; "filter"; "--"; "data/big.bin" |]
                            None

                    Vitest.expect(attributes.Contains("data/big.bin\000filter\000lfs\000")).toBe false
                    Vitest.expect(attributes.Contains("data/big.bin\000filter\000unset\000")).toBe true
                    let! attributeFile = tryReadUtf8FileAsync (join [| workspace.Repository.Descriptor.RepositoryRoot; ".gitattributes" |])
                    Vitest.expect(attributeFile |> Option.exists _.Contains("\"/data/big.bin\" -filter -diff -merge")).toBe true
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "automatic Git LFS policy preserves an explicit unset for an oversized path",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let attributesPath = join [| workspace.Repository.Descriptor.RepositoryRoot; ".gitattributes" |]
                    do! workspace.WriteFile ".gitattributes" "*.bin filter=lfs diff=lfs merge=lfs -text\n"
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "add"; ".gitattributes" |] None
                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "commit"; "-m"; "test: commit wildcard opt-out policy" |]
                            None

                    let storagePolicy =
                        workspace.Provider.StoragePolicy

                    let! unsetResult =
                        storagePolicy.SetPathPolicy workspace.Repository
                            (mkRepositoryPath "data/big.bin")
                            false
                            (OperationContext.detached "automatic-policy-explicit-unset")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy explicit unset" unsetResult |> ignore
                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "add"; ".gitattributes" |] None
                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "commit"; "-m"; "test: commit explicit LFS opt-out" |]
                            None

                    let! attributesBefore = readFileBase64Async attributesPath
                    let content = String.replicate (1024 * 1024) "n"
                    do! workspace.WriteFile "data/big.bin" content
                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "automatic-policy-opt-out-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "automatic policy opt-out status" statusResult
                    let! revisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: preserve explicit automatic LFS opt-out"
                                Paths = [| mkRepositoryPath "data/big.bin" |]
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "automatic-policy-opt-out-revision")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy opt-out revision" revisionResult |> ignore
                    let! committed = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "show"; "HEAD:data/big.bin" |] None
                    let! attributesAfter = readFileBase64Async attributesPath
                    let! filter =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "check-attr"; "-z"; "filter"; "--"; "data/big.bin" |]
                            None

                    Vitest.expect(committed.Contains "git-lfs").toBe false
                    Vitest.expect(committed.Length).toBe content.Length
                    Vitest.expect(attributesAfter).toBe attributesBefore
                    Vitest.expect(filter.Contains("data/big.bin\000filter\000unset\000")).toBe true
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "automatic Git LFS policy probes plans and disables a literal metacharacter path",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let relativePath =
                        if isWindowsProcess () then
                            "data/space [literal].bin"
                        else
                            "data/space [literal]*.bin"

                    do! workspace.WriteFile relativePath (String.replicate (1024 * 1024) "p")
                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "automatic-policy-literal-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "automatic policy literal status" statusResult
                    let! revisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: automatic LFS literal metacharacter path"
                                Paths = [| mkRepositoryPath relativePath |]
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "automatic-policy-literal-revision")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy literal revision" revisionResult |> ignore
                    let attributesPath = join [| workspace.Repository.Descriptor.RepositoryRoot; ".gitattributes" |]
                    let! generatedAttributes = tryReadUtf8FileAsync attributesPath
                    let generatedAttributes = generatedAttributes |> Option.defaultValue ""
                    let escapedPattern =
                        if isWindowsProcess () then
                            "\"/data/space \\\\[literal\\\\].bin\""
                        else
                            "\"/data/space \\\\[literal\\\\]\\\\*.bin\""

                    if not (generatedAttributes.Contains($"{escapedPattern} filter=lfs")) then
                        failwith $"Expected generated literal tracking rule; attributes were: {generatedAttributes}"

                    do!
                        workspace.WriteFile
                            ".gitattributes"
                            (generatedAttributes + "*.bin filter=lfs diff=lfs merge=lfs -text\n")

                    let! _ = runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "add"; ".gitattributes" |] None
                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "commit"; "-m"; "test: add wildcard for literal disable" |]
                            None

                    let storagePolicy =
                        workspace.Provider.StoragePolicy

                    let! disableResult =
                        storagePolicy.SetPathPolicy workspace.Repository
                            (mkRepositoryPath relativePath)
                            false
                            (OperationContext.detached "automatic-policy-literal-disable")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy literal disable" disableResult |> ignore
                    let! filter =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "check-attr"; "-z"; "filter"; "--"; relativePath |]
                            None

                    let! disabledAttributes = tryReadUtf8FileAsync attributesPath
                    let disabledAttributes = disabledAttributes |> Option.defaultValue ""
                    let literalLines =
                        disabledAttributes.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        |> Array.filter _.StartsWith(escapedPattern)

                    if not (filter.Contains($"{relativePath}\000filter\000unset\000")) then
                        failwith $"Expected literal path to resolve unset; check-attr returned: {filter}"

                    let expectedLiteralLines = [| $"{escapedPattern} -filter -diff -merge" |]

                    if literalLines <> expectedLiteralLines then
                        let literalSummary = String.concat " | " literalLines
                        failwith $"Expected one literal unset line; found: {literalSummary}"

                    let asteriskPath = "data/policy*.bin"
                    let! enableAsterisk =
                        storagePolicy.SetPathPolicy workspace.Repository
                            (mkRepositoryPath asteriskPath)
                            true
                            (OperationContext.detached "automatic-policy-asterisk-enable")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy asterisk enable" enableAsterisk |> ignore
                    let! disableAsterisk =
                        storagePolicy.SetPathPolicy workspace.Repository
                            (mkRepositoryPath asteriskPath)
                            false
                            (OperationContext.detached "automatic-policy-asterisk-disable")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy asterisk disable" disableAsterisk |> ignore
                    let! asteriskFilter =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "check-attr"; "-z"; "filter"; "--"; asteriskPath |]
                            None

                    if asteriskFilter.Contains($"{asteriskPath}\000filter\000lfs\000") then
                        let! finalAttributes = tryReadUtf8FileAsync attributesPath
                        failwith
                            $"Expected literal asterisk path to be disabled; check-attr returned {asteriskFilter}; attributes were {finalAttributes}."
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "automatic Git LFS policy reports local settings and commit threshold honestly",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let storagePolicy =
                        workspace.Provider.StoragePolicy

                    let! setFourResult =
                        storagePolicy.SetSettings workspace.Repository
                            {
                                AutoPolicyThresholdMb = Some 4
                                MaterializeLargeObjects = true
                            }
                            (OperationContext.detached "automatic-policy-set-four")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy set four" setFourResult |> ignore
                    let! settingsFourResult =
                        storagePolicy.GetSettings workspace.Repository (OperationContext.detached "automatic-policy-get-four")
                        |> executed
                        |> Async.StartAsPromise

                    let settingsFour = expectProviderValue "automatic policy get four" settingsFourResult
                    Vitest.expect(settingsFour.AutoPolicyThresholdMb).toEqual (Some 4)
                    Vitest.expect(settingsFour.MaterializeLargeObjects).toBe true
                    do! workspace.WriteFile "four-megabyte-threshold.bin" (String.replicate (1024 * 1024) "f")

                    let! firstStatusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "automatic-policy-four-status")
                        |> executed
                        |> Async.StartAsPromise

                    let firstStatus = expectProviderValue "automatic policy four status" firstStatusResult
                    let! firstRevisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: automatic policy four MiB threshold"
                                Paths = [| mkRepositoryPath "four-megabyte-threshold.bin" |]
                                ExpectedRepositoryVersion = firstStatus.RepositoryVersion
                            }
                            (OperationContext.detached "automatic-policy-four-revision")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy four revision" firstRevisionResult |> ignore
                    let! firstCommitted =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "show"; "HEAD:four-megabyte-threshold.bin" |]
                            None

                    Vitest.expect(firstCommitted.Contains "git-lfs").toBe false

                    let! setOneResult =
                        storagePolicy.SetSettings workspace.Repository
                            {
                                AutoPolicyThresholdMb = Some 1
                                MaterializeLargeObjects = false
                            }
                            (OperationContext.detached "automatic-policy-set-one")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy set one" setOneResult |> ignore
                    let! settingsOneResult =
                        storagePolicy.GetSettings workspace.Repository (OperationContext.detached "automatic-policy-get-one")
                        |> executed
                        |> Async.StartAsPromise

                    let settingsOne = expectProviderValue "automatic policy get one" settingsOneResult
                    Vitest.expect(settingsOne.AutoPolicyThresholdMb).toEqual (Some 1)
                    Vitest.expect(settingsOne.MaterializeLargeObjects).toBe false
                    do! workspace.WriteFile "one-megabyte-threshold.bin" (String.replicate (1024 * 1024) "o")

                    let! secondStatusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "automatic-policy-one-status")
                        |> executed
                        |> Async.StartAsPromise

                    let secondStatus = expectProviderValue "automatic policy one status" secondStatusResult
                    let! secondRevisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: automatic policy one MiB threshold"
                                Paths = [| mkRepositoryPath "one-megabyte-threshold.bin" |]
                                ExpectedRepositoryVersion = secondStatus.RepositoryVersion
                            }
                            (OperationContext.detached "automatic-policy-one-revision")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy one revision" secondRevisionResult |> ignore
                    let! secondCommitted =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "show"; "HEAD:one-megabyte-threshold.bin" |]
                            None

                    Vitest.expect(secondCommitted.Contains "git-lfs").toBe true
                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "config"; "--local"; "versioncontrolservice.lfs.autotrackthresholdmb"; "garbage" |]
                            None

                    let! corruptSettingsResult =
                        storagePolicy.GetSettings workspace.Repository (OperationContext.detached "automatic-policy-corrupt-settings")
                        |> executed
                        |> Async.StartAsPromise

                    let corruptSettingsFailure = expectProviderFailure "automatic policy corrupt settings" corruptSettingsResult
                    Vitest.expect(corruptSettingsFailure.Code).toBe "invalid_lfs_threshold"

                    do! workspace.WriteFile "corrupt-threshold.bin" (String.replicate (1024 * 1024) "c")
                    let! corruptStatusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "automatic-policy-corrupt-status")
                        |> executed
                        |> Async.StartAsPromise

                    let corruptStatus = expectProviderValue "automatic policy corrupt status" corruptStatusResult
                    let! corruptRevisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: reject corrupt automatic threshold"
                                Paths = [| mkRepositoryPath "corrupt-threshold.bin" |]
                                ExpectedRepositoryVersion = corruptStatus.RepositoryVersion
                            }
                            (OperationContext.detached "automatic-policy-corrupt-revision")
                        |> executed
                        |> Async.StartAsPromise

                    let corruptRevisionFailure = expectProviderFailure "automatic policy corrupt revision" corruptRevisionResult
                    Vitest.expect(corruptRevisionFailure.Code).toBe "invalid_lfs_threshold"
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "automatic Git LFS policy ignores global settings while local settings win",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()
                let originalGlobal = tryGetProcessEnvironment "GIT_CONFIG_GLOBAL" |> Option.ofObj

                try
                    let! workspace = harness.CreateWorkspace()
                    let globalConfig = join [| dirname workspace.Repository.Descriptor.RepositoryRoot; "automatic-policy-global.config" |]
                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "config"; "--file"; globalConfig; "versioncontrolservice.lfs.autotrackthresholdmb"; "4" |]
                            None

                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "config"; "--file"; globalConfig; "versioncontrolservice.lfs.materializelargeobjects"; "true" |]
                            None

                    setProcessEnvironment "GIT_CONFIG_GLOBAL" globalConfig
                    let storagePolicy =
                        workspace.Provider.StoragePolicy

                    let! defaultSettingsResult =
                        storagePolicy.GetSettings workspace.Repository (OperationContext.detached "automatic-policy-global-default-settings")
                        |> executed
                        |> Async.StartAsPromise

                    let defaultSettings = expectProviderValue "automatic policy global default settings" defaultSettingsResult
                    Vitest.expect(defaultSettings.AutoPolicyThresholdMb).toEqual (Some 1)
                    Vitest.expect(defaultSettings.MaterializeLargeObjects).toBe false
                    do! workspace.WriteFile "global-only.bin" (String.replicate (1024 * 1024) "g")
                    let! firstStatusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "automatic-policy-global-status")
                        |> executed
                        |> Async.StartAsPromise

                    let firstStatus = expectProviderValue "automatic policy global status" firstStatusResult
                    let! firstRevisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: ignore global automatic LFS settings"
                                Paths = [| mkRepositoryPath "global-only.bin" |]
                                ExpectedRepositoryVersion = firstStatus.RepositoryVersion
                            }
                            (OperationContext.detached "automatic-policy-global-revision")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy global revision" firstRevisionResult |> ignore
                    let! firstCommitted =
                        runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "show"; "HEAD:global-only.bin" |] None

                    Vitest.expect(firstCommitted.Contains "git-lfs").toBe true
                    let! _ =
                        runGitIn
                            workspace.Repository.Descriptor.RepositoryRoot
                            [||]
                            [| "config"; "--file"; globalConfig; "versioncontrolservice.lfs.autotrackthresholdmb"; "1" |]
                            None

                    let! localSettingsResult =
                        storagePolicy.SetSettings workspace.Repository
                            {
                                AutoPolicyThresholdMb = Some 4
                                MaterializeLargeObjects = true
                            }
                            (OperationContext.detached "automatic-policy-local-wins-set")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy local wins set" localSettingsResult |> ignore
                    let! localReportedResult =
                        storagePolicy.GetSettings workspace.Repository (OperationContext.detached "automatic-policy-local-wins-get")
                        |> executed
                        |> Async.StartAsPromise

                    let localReported = expectProviderValue "automatic policy local wins get" localReportedResult
                    Vitest.expect(localReported.AutoPolicyThresholdMb).toEqual (Some 4)
                    Vitest.expect(localReported.MaterializeLargeObjects).toBe true
                    do! workspace.WriteFile "local-wins.bin" (String.replicate (1024 * 1024) "l")
                    let! secondStatusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "automatic-policy-local-wins-status")
                        |> executed
                        |> Async.StartAsPromise

                    let secondStatus = expectProviderValue "automatic policy local wins status" secondStatusResult
                    let! secondRevisionResult =
                        workspace.Provider.CreateRevision workspace.Repository
                            {
                                Message = "test: local automatic LFS settings win"
                                Paths = [| mkRepositoryPath "local-wins.bin" |]
                                ExpectedRepositoryVersion = secondStatus.RepositoryVersion
                            }
                            (OperationContext.detached "automatic-policy-local-wins-revision")
                        |> executed
                        |> Async.StartAsPromise

                    expectProviderValue "automatic policy local wins revision" secondRevisionResult |> ignore
                    let! secondCommitted =
                        runGitIn workspace.Repository.Descriptor.RepositoryRoot [||] [| "show"; "HEAD:local-wins.bin" |] None

                    Vitest.expect(secondCommitted.Contains "git-lfs").toBe false
                    match originalGlobal with
                    | Some value -> setProcessEnvironment "GIT_CONFIG_GLOBAL" value
                    | None -> clearProcessEnvironment "GIT_CONFIG_GLOBAL"

                    do! harness.Cleanup()
                with error ->
                    match originalGlobal with
                    | Some value -> setProcessEnvironment "GIT_CONFIG_GLOBAL" value
                    | None -> clearProcessEnvironment "GIT_CONFIG_GLOBAL"

                    do! harness.Cleanup()
                    return raise error
            }
        )
)

let private adoptionHooks (originResult: OperationResult<NodeProcess.ProcessOutput>) : GitWorkspaceSession.GitSessionHooks = {
    RunBytesProcess = None
    RunProcess =
        Some(fun request _ ->
            async {
                match request.Arguments with
                | [| "rev-parse"; "--show-toplevel" |] ->
                    return OperationResult.succeeded (processOutput 0 "C:/adoption-fixture\n" "")
                | [| "config"; "--get"; "remote.origin.url" |] -> return originResult
                | _ ->
                    return
                        OperationResult.failed(
                            OperationFailure.create ProviderError "unexpected_command" "Unexpected Git command."
                        )
            })
    Barrier = None
}

let private lfsPointerPrefix = "version https://git-lfs.github.com/spec/v1"

let private lfsOidFromPointer (pointer: string) =
    pointer.Replace("\r\n", "\n").Split('\n')
    |> Array.tryPick (fun line ->
        let prefix = "oid sha256:"

        if line.StartsWith(prefix, StringComparison.Ordinal) then
            Some(line.Substring(prefix.Length).Trim())
        else
            None)
    |> Option.defaultWith (fun () -> failwith "Expected an LFS pointer object id.")

let private lfsMediaDirectoryFromEnvironment (lfsEnvironment: string) =
    lfsEnvironment.Replace("\r\n", "\n").Split('\n')
    |> Array.tryPick (fun line ->
        let prefix = "LocalMediaDir="
        let line = line.TrimEnd('\r')

        if line.StartsWith(prefix, StringComparison.Ordinal) then
            Some(line.Substring(prefix.Length).Trim())
        else
            None)
    |> Option.defaultWith (fun () -> failwith "Expected Git LFS to report its local media directory.")

let private lfsObjectPath (mediaDirectory: string) (oid: string) =
    join [| mediaDirectory; oid.Substring(0, 2); oid.Substring(2, 2); oid |]

let private createDownloadedLfsRestoreFixtureWithHooks (hooks: GitWorkspaceSession.GitSessionHooks) = promise {
    let! root = createTempDirectoryAsync ()
    let barePath = join [| root; "origin.git" |]
    let sourcePath = join [| root; "source" |]
    let repoPath = join [| root; "downloaded" |]

    let! _ = runGitIn root [||] [| "init"; "--bare"; "-b"; "main"; barePath |] None
    let! _ = runGitIn root [||] [| "init"; "-b"; "main"; sourcePath |] None
    do! configureUser sourcePath
    let! _ = runGitIn sourcePath [||] [| "lfs"; "install"; "--local" |] None
    let! _ = runGitIn sourcePath [||] [| "lfs"; "track"; "*.bin" |] None
    do! writeUtf8FileAsync (join [| sourcePath; "large.bin" |]) "large binary payload\n"
    let! _ = runGitIn sourcePath [||] [| "add"; "-A" |] None
    let! _ = runGitIn sourcePath [||] [| "commit"; "-m"; "test: downloaded LFS object" |] None
    let! _ = runGitIn sourcePath [||] [| "remote"; "add"; "origin"; barePath |] None
    let! _ = runGitIn sourcePath [||] [| "push"; "-u"; "origin"; "main" |] None
    let! _ = runGitIn root [||] [| "clone"; barePath; repoPath |] None
    do! configureUser repoPath
    let! _ = runGitIn repoPath [||] [| "lfs"; "install"; "--local" |] None
    let! _ = runGitIn repoPath [||] [| "lfs"; "pull"; "origin" |] None

    let provider = GitProvider.create { GitProviderOptions.defaults with Hooks = hooks }
    let! repository = openFolder provider repoPath None "downloaded-lfs-open"
    return root, repoPath, provider, repository
}

let private createDownloadedLfsRestoreFixture () =
    createDownloadedLfsRestoreFixtureWithHooks GitWorkspaceSession.GitSessionHooks.none

let private repositoryPath (value: string) =
    match RepositoryPath.tryCreate value with
    | Ok path -> path
    | Error message -> failwith message

/// Restores a locally edited LFS file while every `git lfs checkout` ends with the text that git-lfs 3.7.0
/// prints when the index lock is held, and exits with 0. With `createLock`, the checkout also creates the
/// lock file, which the text does not name. Returns the result of the restore and whether the lock exists.
let private restoreLfsFileWithIndexError (createLock: bool) (operationName: string) = promise {
    let hooks: GitWorkspaceSession.GitSessionHooks = {
        GitWorkspaceSession.GitSessionHooks.none with
            RunProcess =
                Some(fun request processContext ->
                    async {
                        if request.Arguments.Length >= 2 && request.Arguments[0] = "lfs" && request.Arguments[1] = "checkout" then
                            if createLock then
                                let workingDirectory = request.WorkingDirectory |> Option.defaultValue "."

                                do!
                                    writeUtf8FileAsync
                                        (join [| workingDirectory; ".git"; "index.lock" |])
                                        "held by another process\n"
                                    |> Async.AwaitPromise

                            return
                                OperationResult.succeeded {
                                    NodeProcess.ExitCode = 0
                                    StdOut = ""
                                    StdErr = "Error updating the Git index:\n\n\nErrors logged to 'x.log'."
                                }
                        else
                            return! NodeProcess.run request processContext
                    })
    }

    let! root, repoPath, provider, repository = createDownloadedLfsRestoreFixtureWithHooks hooks

    try
        do! writeUtf8FileAsync (join [| repoPath; "large.bin" |]) "local edit\n"

        let! statusResult =
            provider.GetStatus repository (OperationContext.detached $"{operationName}-status")
            |> executed
            |> Async.StartAsPromise

        let status = expectProviderValue "status before restoring the LFS file" statusResult

        let! restoreResult =
            provider.RestorePaths repository
                {
                    Paths = [| repositoryPath "large.bin" |]
                    ExpectedRepositoryVersion = status.RepositoryVersion
                }
                (OperationContext.detached operationName)
            |> executed
            |> Async.StartAsPromise

        let lockExists = NodeFileSystem.existsSync (join [| repoPath; ".git"; "index.lock" |])
        do! removeDirectoryAsync root
        return restoreResult, lockExists
    with error ->
        do! removeDirectoryAsync root
        return raise error
}

Vitest.describe (
    "Git clone target refs",
    fun () ->
        Vitest.test (
            "Clone rejects target refs before creating the target",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! root = createTempDirectoryAsync ()

                try
                    let targetPath = join [| root; "target-ref-clone" |]
                    let location = {
                        ProviderId = gitProviderId
                        DisplayName = None
                        ProviderLocation = join [| root; "origin.git" |]
                        ConnectionProfileId = None
                    }

                    let provider = GitProvider.create GitProviderOptions.defaults
                    let! result =
                        provider.Clone
                            {
                                Location = location
                                TargetPath = targetPath
                                TargetRef = Some(ProviderRef.tryCreate "git-local:main" |> Result.defaultWith failwith)
                                MaterializeAllObjects = false
                            }
                            (OperationContext.detached "clone-unsupported-target-ref")
                        |> executed
                        |> Async.StartAsPromise

                    match result with
                    | Failed failure ->
                        Vitest.expect(failure.Category).toEqual Unsupported
                        Vitest.expect(failure.Code).toBe "target_ref_unsupported"
                    | Succeeded _
                    | PartiallySucceeded _ -> failwith "A clone with a target ref unexpectedly succeeded."

                    Vitest.expect(NodeFileSystem.existsSync targetPath).toBe(false)
                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )
)

Vitest.describe (
    "Git open of an existing folder",
    fun () ->
        Vitest.test (
            "Git opens an existing repository",
            fun () -> promise {
                let! root = createTempDirectoryAsync ()

                try
                    let remotePath = join [| root; "origin.git" |]
                    let repoPath = join [| root; "existing" |]
                    let localOnlyPath = join [| root; "local-only" |]

                    let! _ = runGitIn root [||] [| "init"; "--bare"; "-b"; "main"; remotePath |] None
                    let! _ = runGitIn root [||] [| "init"; "-b"; "main"; repoPath |] None
                    do! configureUser repoPath
                    do! writeUtf8FileAsync (join [| repoPath; "base.txt" |]) "base\n"
                    let! _ = runGitIn repoPath [||] [| "add"; "-A" |] None
                    let! _ = runGitIn repoPath [||] [| "commit"; "-m"; "test: base" |] None
                    let! _ = runGitIn repoPath [||] [| "remote"; "add"; "origin"; remotePath |] None
                    let! expectedRemoteLocation =
                        runGitIn repoPath [||] [| "config"; "--get"; "remote.origin.url" |] None

                    let configPath = join [| repoPath; ".git"; "config" |]
                    let! configBefore = readFileBase64Async configPath
                    let provider = GitProvider.create GitProviderOptions.defaults

                    let! openResult =
                        Async.StartAsPromise(
                            provider.Open
                                {
                                    RepositoryRoot = repoPath
                                    ConnectionProfileId = Some "account-a"
                                }
                                (OperationContext.detached "open-remote")
                            |> executed
                        )

                    let repository = expectProviderValue "open existing Git repository" openResult
                    Vitest.expect(repository.Descriptor.RepositoryRoot).toBe (repoPath.Replace("\\", "/"))

                    Vitest.expect(repository.Descriptor.Location |> Option.map _.ProviderLocation).toEqual (
                        Some(expectedRemoteLocation.Trim())
                    )

                    Vitest.expect(repository.Descriptor.Location |> Option.bind _.ConnectionProfileId).toEqual (
                        Some "account-a"
                    )

                    let! configAfter = readFileBase64Async configPath
                    Vitest.expect(configAfter).toBe (configBefore)

                    let! _ = runGitIn root [||] [| "init"; "-b"; "main"; localOnlyPath |] None
                    do! writeUtf8FileAsync (join [| localOnlyPath; "uncommitted.txt" |]) "local change\n"

                    let! localOpenResult =
                        Async.StartAsPromise(
                            provider.Open
                                {
                                    RepositoryRoot = localOnlyPath
                                    ConnectionProfileId = None
                                }
                                (OperationContext.detached "open-local")
                            |> executed
                        )

                    let localRepository = expectProviderValue "open remote-less Git repository" localOpenResult
                    Vitest.expect(localRepository.Descriptor.RepositoryRoot).toBe (localOnlyPath.Replace("\\", "/"))

                    Vitest.expect(localRepository.Descriptor.Location |> Option.map _.ProviderLocation).toEqual (
                        Some(localOnlyPath.Replace("\\", "/"))
                    )

                    let! statusResult =
                        Async.StartAsPromise(
                            provider.GetStatus localRepository (OperationContext.detached "adopted-local-status")
                            |> executed
                        )

                    let status = expectProviderValue "adopted local status" statusResult
                    Vitest.expect(status.Changes |> Array.map (fun change -> RepositoryPath.value change.Path)).toEqual ([| "uncommitted.txt" |])

                    match status.Synchronization with
                    | Some synchronization -> Vitest.expect(synchronization.Relationship).toEqual (NoTarget)
                    | None -> failwith "Expected synchronization information for the adopted local repository."

                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )

        Vitest.test (
            "Git open strips origin userinfo from the repository location and keeps the remote unchanged",
            fun () -> promise {
                let! root = createTempDirectoryAsync ()

                try
                    let repoPath = join [| root; "credential-origin" |]
                    let credentialUrl = "https://user:secret@example.invalid/org/repo.git"

                    let! _ = runGitIn root [||] [| "init"; "-b"; "main"; repoPath |] None
                    let! _ = runGitIn repoPath [||] [| "remote"; "add"; "origin"; credentialUrl |] None

                    let provider = GitProvider.create GitProviderOptions.defaults

                    let! openResult =
                        Async.StartAsPromise(
                            provider.Open
                                {
                                    RepositoryRoot = repoPath
                                    ConnectionProfileId = None
                                }
                                (OperationContext.detached "open-credential-origin")
                            |> executed
                        )

                    let repository = expectProviderValue "open credential-bearing Git origin" openResult

                    Vitest.expect(repository.Descriptor.Location |> Option.map _.ProviderLocation).toEqual (
                        Some "https://example.invalid/org/repo.git"
                    )

                    let! configuredRemote =
                        runGitIn repoPath [||] [| "config"; "--get"; "remote.origin.url" |] None

                    Vitest.expect(configuredRemote.Trim()).toBe credentialUrl

                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )

        Vitest.test (
            "Git Bind strips userinfo from the repository location when fetch fails",
            fun () -> promise {
                let! root = createTempDirectoryAsync ()

                try
                    let repoPath = join [| root; "bind-credential-origin" |]
                    let credentialUrl = "https://user:secret@example.invalid/org/repo.git"
                    let mutable fetchObserved = false

                    let! _ = runGitIn root [||] [| "init"; "-b"; "main"; repoPath |] None

                    let hooks = {
                        GitWorkspaceSession.GitSessionHooks.none with
                            RunProcess =
                                Some(fun request context ->
                                    if request.Arguments |> Array.contains "fetch" then
                                        async {
                                            fetchObserved <- true
                                            return
                                                OperationResult.succeeded(
                                                    processOutput 128 "" "fatal: injected fetch failure"
                                                )
                                        }
                                    else
                                        NodeProcess.run request context)
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with Hooks = hooks }
                    let location = {
                        ProviderId = gitProviderId
                        DisplayName = None
                        ProviderLocation = credentialUrl
                        ConnectionProfileId = None
                    }

                    let! bindResult =
                        Async.StartAsPromise(
                            provider.Bind
                                {
                                    RepositoryRoot = repoPath
                                    Location = location
                                }
                                (OperationContext.detached "bind-credential-origin")
                            |> executed
                        )

                    let repository = expectProviderValue "bind credential-bearing Git location" bindResult
                    Vitest.expect(fetchObserved).toBe true

                    Vitest.expect(repository.Descriptor.Location |> Option.map _.ProviderLocation).toEqual (
                        Some "https://example.invalid/org/repo.git"
                    )

                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )

        Vitest.test (
            "Git initialization preserves an existing non-Git directory and reports an already initialized repository",
            fun () -> promise {
                let! root = createTempDirectoryAsync ()

                try
                    let nonGitPath = join [| root; "non-git" |]
                    let existingGitPath = join [| root; "already-git" |]
                    let filePath = join [| nonGitPath; "uncommitted.txt" |]
                    do! writeUtf8FileAsync filePath "uncommitted bytes\n"
                    let! bytesBefore = readFileBase64Async filePath
                    let provider = GitProvider.create GitProviderOptions.defaults

                    let! initializationResult =
                        Async.StartAsPromise(
                            provider.Initialize
                                {
                                    TargetPath = nonGitPath
                                    Location = None
                                }
                                (OperationContext.detached "initialize-non-git")
                            |> executed
                        )

                    let repository = expectProviderValue "initialize non-Git directory" initializationResult
                    let! bytesAfter = readFileBase64Async filePath
                    Vitest.expect(bytesAfter).toBe (bytesBefore)

                    let! statusResult =
                        Async.StartAsPromise(provider.GetStatus repository (OperationContext.detached "initialized-non-git-status") |> executed)

                    let status = expectProviderValue "initialized non-Git status" statusResult
                    Vitest.expect(status.Changes |> Array.map (fun change -> RepositoryPath.value change.Path)).toEqual ([| "uncommitted.txt" |])

                    let! _ = runGitIn root [||] [| "init"; "-b"; "main"; existingGitPath |] None

                    let! existingResult =
                        Async.StartAsPromise(
                            provider.Initialize
                                {
                                    TargetPath = existingGitPath
                                    Location = None
                                }
                                (OperationContext.detached "initialize-existing-git")
                            |> executed
                        )

                    let existingFailure = expectProviderFailure "initialize existing Git repository" existingResult
                    Vitest.expect(existingFailure.Category).toEqual (Validation)
                    Vitest.expect(existingFailure.Code).toBe ("already_initialized")

                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )

        Vitest.test (
            "Git open preserves origin lookup failure semantics",
            fun () -> promise {
                let request = {
                    RepositoryRoot = "C:/adoption-fixture"
                    ConnectionProfileId = None
                }

                let missingOriginProvider =
                    GitProvider.create {
                        GitProviderOptions.defaults with
                            Hooks = adoptionHooks (OperationResult.succeeded (processOutput 1 "" ""))
                    }

                let! missingOriginResult =
                    Async.StartAsPromise(
                        missingOriginProvider.Open request (OperationContext.detached "origin-missing")
                        |> executed
                    )

                let missingOrigin = expectProviderValue "open without origin" missingOriginResult

                Vitest.expect(missingOrigin.Descriptor.Location |> Option.map _.ProviderLocation).toEqual (
                    Some "C:/adoption-fixture"
                )

                let canceledProvider =
                    GitProvider.create {
                        GitProviderOptions.defaults with
                            Hooks = adoptionHooks (OperationResult.canceled "The origin lookup was canceled.")
                    }

                let! canceledResult =
                    Async.StartAsPromise(
                        canceledProvider.Open request (OperationContext.detached "origin-canceled") |> executed
                    )

                let canceledFailure = expectProviderFailure "canceled origin lookup" canceledResult
                Vitest.expect(canceledFailure.Category).toEqual (Canceled)
                Vitest.expect(canceledFailure.Code).toBe ("operation_canceled")

                let processFailureProvider =
                    GitProvider.create {
                        GitProviderOptions.defaults with
                            Hooks =
                                adoptionHooks (
                                    OperationResult.failed(
                                        OperationFailure.create
                                            Network
                                            "origin_process_failed"
                                            "The origin lookup process failed."
                                    )
                                )
                    }

                let! processFailureResult =
                    Async.StartAsPromise(
                        processFailureProvider.Open request (OperationContext.detached "origin-process-failed")
                        |> executed
                    )

                let processFailure = expectProviderFailure "failed origin lookup process" processFailureResult
                Vitest.expect(processFailure.Category).toEqual (Network)
                Vitest.expect(processFailure.Code).toBe ("origin_process_failed")

                let unexpectedOutputProvider =
                    GitProvider.create {
                        GitProviderOptions.defaults with
                            Hooks = adoptionHooks (OperationResult.succeeded (processOutput 2 "" "fatal: config is unreadable"))
                    }

                let! unexpectedOutputResult =
                    Async.StartAsPromise(
                        unexpectedOutputProvider.Open request (OperationContext.detached "origin-unexpected-output")
                        |> executed
                    )

                let unexpectedOutputFailure =
                    expectProviderFailure "unexpected origin lookup output" unexpectedOutputResult

                Vitest.expect(unexpectedOutputFailure.Category).toEqual (ProviderError)
                Vitest.expect(unexpectedOutputFailure.Code).toBe ("origin_lookup_failed")
            }
        )

        Vitest.test (
            "provider locations are validated before the open of a corrupt folder guesses a repository",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! root = createTempDirectoryAsync ()

                try
                    let workspacePath = join [| root; "corrupt-repository" |]
                    let gitPath = join [| workspacePath; ".git" |]
                    do! ensureDirectoryAsync gitPath
                    do! writeBinaryFileAsync (join [| gitPath; "HEAD" |]) [| 0; 255; 1; 254 |]
                    do! writeBinaryFileAsync (join [| gitPath; "objects" |]) [| 9; 8; 7; 6 |]
                    let mutable revParseDetail = ""

                    let hooks = {
                        GitWorkspaceSession.GitSessionHooks.none with
                            RunProcess =
                                Some(fun request processContext ->
                                    async {
                                        let! result =
                                            NodeProcess.run
                                                {
                                                    request with
                                                        Environment =
                                                            Array.append
                                                                request.Environment
                                                                [| "GIT_CEILING_DIRECTORIES", root |]
                                                }
                                                processContext

                                        match request.Arguments, result with
                                        | [| "rev-parse"; "--show-toplevel" |], Succeeded outcome ->
                                            revParseDetail <- outcome.Value.StdErr + outcome.Value.StdOut
                                        | _ -> ()

                                        return result
                                    })
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with Hooks = hooks }

                    let! openResult =
                        Async.StartAsPromise(
                            provider.Open
                                {
                                    RepositoryRoot = workspacePath
                                    ConnectionProfileId = None
                                }
                                (OperationContext.detached "open-corrupt-repository")
                            |> executed
                        )

                    let failure = expectProviderFailure "open corrupt repository" openResult
                    Vitest.expect(failure.Category).toEqual (Unsupported)
                    Vitest.expect(failure.Code).toBe ("adoption_unsupported")
                    Vitest.expect(failure.Message.Contains("without guessing")).toBe true
                    Vitest.expect(String.IsNullOrWhiteSpace revParseDetail).toBe false
                    Vitest.expect(failure.Message.Contains(revParseDetail.Trim())).toBe true

                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )

        Vitest.test (
            "provider locations are validated without hiding the dubious-ownership remediation of an open",
            fun () -> promise {
                let! root = createTempDirectoryAsync ()

                try
                    let workspacePath = join [| root; "dubious-repository" |]
                    do! ensureDirectoryAsync (join [| workspacePath; ".git" |])
                    let detail =
                        "fatal: detected dubious ownership in repository at 'dubious-repository'\nTo add an exception, call git config --global --add safe.directory dubious-repository"

                    let hooks = {
                        GitWorkspaceSession.GitSessionHooks.none with
                            RunProcess =
                                Some(fun request _ ->
                                    async {
                                        match request.Arguments with
                                        | [| "rev-parse"; "--show-toplevel" |] ->
                                            return OperationResult.succeeded (processOutput 128 "" detail)
                                        | _ ->
                                            return
                                                OperationResult.failed(
                                                    OperationFailure.create
                                                        ProviderError
                                                        "unexpected_command"
                                                        "Unexpected Git command."
                                                )
                                    })
                    }

                    let provider = GitProvider.create { GitProviderOptions.defaults with Hooks = hooks }

                    let! result =
                        Async.StartAsPromise(
                            provider.Open
                                {
                                    RepositoryRoot = workspacePath
                                    ConnectionProfileId = None
                                }
                                (OperationContext.detached "open-dubious-repository")
                            |> executed
                        )

                    let failure = expectProviderFailure "open dubious repository" result
                    Vitest.expect(failure.Category).toEqual (ProviderError)
                    Vitest.expect(failure.Code).toBe ("git_failure")
                    Vitest.expect(failure.Message.Contains("dubious ownership")).toBe true
                    Vitest.expect(failure.Message.Contains("safe.directory")).toBe true

                    do! removeDirectoryAsync root
                with error ->
                    do! removeDirectoryAsync root
                    return raise error
            }
        )
)

Vitest.describe (
    "Git restore paths",
    fun () ->
        Vitest.test (
            "RestorePaths materializes a downloaded LFS object from the local cache",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! lfsProbe = runGitResultIn "." [| "lfs"; "version" |]

                if lfsProbe.ExitCode <> 0 then
                    Vitest.expect(true).toBe true
                else
                    let! root, repoPath, provider, repository = createDownloadedLfsRestoreFixture ()

                    try
                        do! writeUtf8FileAsync (join [| repoPath; "large.bin" |]) "local edit\n"
                        let! statusResult =
                            provider.GetStatus repository (OperationContext.detached "restore-downloaded-lfs-status")
                            |> executed
                            |> Async.StartAsPromise

                        let status = expectProviderValue "status before restoring the LFS file" statusResult

                        let! restoreResult =
                            provider.RestorePaths repository
                                {
                                    Paths = [| repositoryPath "large.bin" |]
                                    ExpectedRepositoryVersion = status.RepositoryVersion
                                }
                                (OperationContext.detached "restore-downloaded-lfs")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue "restore downloaded LFS file" restoreResult |> ignore
                        let! restored = tryReadUtf8FileAsync (join [| repoPath; "large.bin" |])
                        Vitest.expect(restored).toEqual (Some "large binary payload\n")

                        let! status = runGitIn repoPath [||] [| "status"; "--porcelain" |] None
                        Vitest.expect(status.Trim()).toBe ""
                        do! removeDirectoryAsync root
                    with error ->
                        do! removeDirectoryAsync root
                        return raise error
            }
        )

        Vitest.test (
            "RestorePaths reports the lock recovery when the checkout ends with success but could not update the index and a lock exists",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! lfsProbe = runGitResultIn "." [| "lfs"; "version" |]

                if lfsProbe.ExitCode <> 0 then
                    Vitest.expect(true).toBe true
                else
                    let! result, lockExists = restoreLfsFileWithIndexError true "restore-lfs-index-error-lock"

                    match result with
                    | PartiallySucceeded(_, failure) ->
                        Vitest.expect(failure.Code).toBe "object_materialization_failed"
                        Vitest.expect(failure.StateChanged).toBe true
                        Vitest.expect(failure.AffectedPaths).toEqual [| "large.bin" |]
                        Vitest.expect(failure.RecoveryAction |> Option.map _.Code).toEqual (Some "remove_index_lock")
                    | Failed failure -> failwith $"Expected a partial result, received {failure.Category}/{failure.Code}."
                    | Succeeded _ -> failwith "Expected the checkout that could not update the index to be reported."

                    Vitest.expect(lockExists).toBe true
            }
        )

        Vitest.test (
            "RestorePaths keeps the plain failure when the checkout ends with success but could not update the index and no lock exists",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! lfsProbe = runGitResultIn "." [| "lfs"; "version" |]

                if lfsProbe.ExitCode <> 0 then
                    Vitest.expect(true).toBe true
                else
                    let! result, lockExists = restoreLfsFileWithIndexError false "restore-lfs-index-error-no-lock"

                    match result with
                    | PartiallySucceeded(_, failure) ->
                        Vitest.expect(failure.Code).toBe "object_materialization_failed"
                        Vitest.expect(failure.StateChanged).toBe true
                        Vitest.expect(failure.AffectedPaths).toEqual [| "large.bin" |]
                        Vitest.expect(failure.Message.Contains "Error updating the Git index").toBe true
                        Vitest.expect(failure.RecoveryAction |> Option.map _.Code).toEqual (Some "retry_materialization")
                    | Failed failure -> failwith $"Expected a partial result, received {failure.Category}/{failure.Code}."
                    | Succeeded _ -> failwith "Expected the checkout that could not update the index to be reported."

                    Vitest.expect(lockExists).toBe false
            }
        )

        Vitest.test (
            "RestorePaths leaves a pointer when its LFS object is absent locally",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! lfsProbe = runGitResultIn "." [| "lfs"; "version" |]

                if lfsProbe.ExitCode <> 0 then
                    Vitest.expect(true).toBe true
                else
                    let! root, repoPath, provider, repository = createDownloadedLfsRestoreFixture ()

                    try
                        let! pointer = runGitIn repoPath [||] [| "show"; "HEAD:large.bin" |] None
                        let! lfsEnvironment = runGitIn repoPath [||] [| "lfs"; "env" |] None
                        let mediaDirectory = lfsMediaDirectoryFromEnvironment lfsEnvironment
                        let objectId = lfsOidFromPointer pointer
                        do! removeFileAsync (lfsObjectPath mediaDirectory objectId)
                        do! writeUtf8FileAsync (join [| repoPath; "large.bin" |]) "local edit\n"

                        let! statusResult =
                            provider.GetStatus repository (OperationContext.detached "restore-lfs-without-local-object-status")
                            |> executed
                            |> Async.StartAsPromise

                        let status = expectProviderValue "status before restoring the LFS file" statusResult

                        let! restoreResult =
                            provider.RestorePaths repository
                                {
                                    Paths = [| repositoryPath "large.bin" |]
                                    ExpectedRepositoryVersion = status.RepositoryVersion
                                }
                                (OperationContext.detached "restore-lfs-without-local-object")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue "restore LFS path without a local object" restoreResult |> ignore
                        let! restored = tryReadUtf8FileAsync (join [| repoPath; "large.bin" |])
                        Vitest.expect(restored |> Option.exists (fun content -> content.StartsWith lfsPointerPrefix)).toBe true
                        do! removeDirectoryAsync root
                    with error ->
                        do! removeDirectoryAsync root
                        return raise error
            }
        )

        Vitest.test (
            "RestorePaths restores an LFS directory",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! lfsProbe = runGitResultIn "." [| "lfs"; "version" |]

                if lfsProbe.ExitCode <> 0 then
                    Vitest.expect(true).toBe true
                else
                    let! root, repoPath, provider, repository = createDownloadedLfsRestoreFixture ()

                    try
                        let firstPath = "data/first.bin"
                        let secondPath = "data/second.bin"
                        let firstContent = "first committed content\n"
                        let secondContent = "second committed content\n"

                        do! writeUtf8FileAsync (join [| repoPath; firstPath |]) firstContent
                        do! writeUtf8FileAsync (join [| repoPath; secondPath |]) secondContent
                        let! _ = runGitIn repoPath [||] [| "add"; "-A" |] None
                        let! _ = runGitIn repoPath [||] [| "commit"; "-m"; "test: restore an LFS directory" |] None

                        do! writeUtf8FileAsync (join [| repoPath; firstPath |]) "first local edit\n"
                        do! writeUtf8FileAsync (join [| repoPath; secondPath |]) "second local edit\n"

                        let! statusResult =
                            provider.GetStatus repository (OperationContext.detached "restore-LFS-directory-status")
                            |> executed
                            |> Async.StartAsPromise

                        let status = expectProviderValue "status before restoring the LFS directory" statusResult

                        let! restoreResult =
                            provider.RestorePaths repository
                                {
                                    Paths = [| repositoryPath "data" |]
                                    ExpectedRepositoryVersion = status.RepositoryVersion
                                }
                                (OperationContext.detached "restore-LFS-directory")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue "restore the LFS directory" restoreResult |> ignore
                        let! restoredFirst = tryReadUtf8FileAsync (join [| repoPath; firstPath |])
                        let! restoredSecond = tryReadUtf8FileAsync (join [| repoPath; secondPath |])

                        Vitest.expect(restoredFirst).toEqual (Some firstContent)
                        Vitest.expect(restoredSecond).toEqual (Some secondContent)
                        do! removeDirectoryAsync root
                    with error ->
                        do! removeDirectoryAsync root
                        return raise error
            }
        )

        Vitest.test (
            "RestorePaths restores a non-ASCII tracked path",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let! lfsProbe = runGitResultIn "." [| "lfs"; "version" |]

                if lfsProbe.ExitCode <> 0 then
                    Vitest.expect(true).toBe true
                else
                    let! root, repoPath, provider, repository = createDownloadedLfsRestoreFixture ()

                    try
                        let relativePath = "data/Messung_ä.csv"
                        let! _ = runGitIn repoPath [||] [| "config"; "core.autocrlf"; "false" |] None
                        let committedContent = "Messung;Wert\nTemperatur;21.5\n"
                        let path = join [| repoPath; relativePath |]

                        do! writeUtf8FileAsync path committedContent
                        let! _ = runGitIn repoPath [||] [| "add"; "-A" |] None
                        let! _ = runGitIn repoPath [||] [| "commit"; "-m"; "test: restore a non-ASCII path" |] None
                        do! writeUtf8FileAsync path "edited;value\n"

                        let! statusResult =
                            provider.GetStatus repository (OperationContext.detached "restore-non-ASCII-status")
                            |> executed
                            |> Async.StartAsPromise

                        let status = expectProviderValue "status before restoring the non-ASCII path" statusResult

                        let! restoreResult =
                            provider.RestorePaths repository
                                {
                                    Paths = [| repositoryPath relativePath |]
                                    ExpectedRepositoryVersion = status.RepositoryVersion
                                }
                                (OperationContext.detached "restore-non-ASCII-path")
                            |> executed
                            |> Async.StartAsPromise

                        expectProviderValue "restore the non-ASCII path" restoreResult |> ignore
                        let! restored = tryReadUtf8FileAsync path
                        Vitest.expect(restored).toEqual (Some committedContent)
                        let! status = runGitIn repoPath [||] [| "status"; "--porcelain" |] None
                        Vitest.expect(status.Trim()).toBe ""
                        do! removeDirectoryAsync root
                    with error ->
                        do! removeDirectoryAsync root
                        return raise error
            }
        )
)

Vitest.describe (
    "Git index lock recovery",
    fun () ->
        Vitest.test (
            "Materialize reports a held index lock before running Git LFS",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    let materialization =
                        workspace.Provider.ObjectMaterialization

                    do!
                        writeUtf8FileAsync
                            (join [| workspace.Repository.Descriptor.RepositoryRoot; ".git"; "index.lock" |])
                            "manual lock\n"

                    let! result =
                        materialization.Materialize workspace.Repository
                            [| mkRepositoryPath "base.txt" |]
                            (OperationContext.detached "materialize-index-lock")
                        |> executed
                        |> Async.StartAsPromise

                    let failure = expectProviderFailure "locked Materialize" result
                    Vitest.expect(failure.Category).toEqual Concurrency
                    Vitest.expect(failure.Code).toBe "index_locked"
                    Vitest.expect(failure.StateChanged).toBe false
                    Vitest.expect(failure.RecoveryAction |> Option.map _.Code).toEqual (Some "remove_index_lock")
                    Vitest.expect(
                        failure.Details |> Array.exists (fun detail -> detail.StartsWith "Lock file age:")
                    ).toBe true
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )

        Vitest.test (
            "RestorePaths reports a held index lock with recovery details",
            TestOptions(timeout = 120000),
            fun () -> promise {
                let harness = createGitHarness ()

                try
                    let! workspace = harness.CreateWorkspace()
                    do! workspace.WriteFile "base.txt" "changed for restore\n"
                    let! statusResult =
                        workspace.Provider.GetStatus workspace.Repository (OperationContext.detached "restore-index-lock-status")
                        |> executed
                        |> Async.StartAsPromise

                    let status = expectProviderValue "status before locked restore" statusResult
                    do!
                        writeUtf8FileAsync
                            (join [| workspace.Repository.Descriptor.RepositoryRoot; ".git"; "index.lock" |])
                            "manual lock\n"

                    let! restoreResult =
                        workspace.Provider.RestorePaths workspace.Repository
                            {
                                Paths = [| mkRepositoryPath "base.txt" |]
                                ExpectedRepositoryVersion = status.RepositoryVersion
                            }
                            (OperationContext.detached "restore-index-lock")
                        |> executed
                        |> Async.StartAsPromise

                    let failure = expectProviderFailure "locked RestorePaths" restoreResult
                    Vitest.expect(failure.Category).toEqual Concurrency
                    Vitest.expect(failure.Code).toBe "index_locked"
                    Vitest.expect(failure.StateChanged).toBe false
                    Vitest.expect(failure.RecoveryAction |> Option.map _.Code).toEqual (Some "remove_index_lock")
                    Vitest.expect(
                        failure.Details |> Array.exists (fun detail -> detail.StartsWith "Lock file age:")
                    ).toBe true
                    do! harness.Cleanup()
                with error ->
                    do! harness.Cleanup()
                    return raise error
            }
        )
)

Vitest.describe (
    "Git / LFS listing has no idle time limit",
    fun () ->
        let sleepMs (ms: int) =
            Promise.create (fun resolve _ -> Fable.Core.JS.setTimeout (fun () -> resolve ()) ms |> ignore)

        // A fake `git lfs ls-files -j` that writes a "started" marker, stays silent for VCS_TEST_LS_DELAY_MS,
        // then writes a "finished" marker and prints an empty listing. With cancelOnStart the test cancels
        // the context once the started marker exists, then waits past the delay to see whether the process
        // survived. The result carries whether the started and finished markers exist.
        let listWithSilentFakeGit (delayMs: int) (cancelOnStart: bool) = promise {
            let harness = createGitHarness ()
            let! fakeRoot = createTempDirectoryAsync ()

            try
                let! workspace = harness.CreateWorkspace()
                let fakeGitPath = join [| fakeRoot; if isWindowsProcess () then "git.exe" else "git" |]

                if isWindowsProcess () then
                    let! _ = fsPromisesDynamic?copyFile (nodeExecutablePath, fakeGitPath) |> unbox<JS.Promise<obj>>
                    ()
                else
                    do!
                        writeUtf8FileAsync
                            fakeGitPath
                            $"#!/bin/sh\nscript=\"$1\"\nshift\nexec '{nodeExecutablePath}' \"$VCS_TEST_LS_DISPATCH_ROOT/$script\" \"$@\"\n"

                    let! _ = fsPromisesDynamic?chmod (fakeGitPath, 493) |> unbox<JS.Promise<obj>>
                    ()

                // The fake runs as `git lfs ls-files -j`, so node loads the script named "lfs" from the workspace.
                // On Linux and macOS each spawn first runs `git lfs --version` synchronously to check PATH,
                // so the fake answers that at once and only the listing writes markers and stays silent.
                do!
                    writeUtf8FileAsync
                        (join [| workspace.Repository.Descriptor.RepositoryRoot; "lfs" |])
                        ("if (process.argv.includes('--version')) {\n"
                         + "  process.stdout.write('git-lfs/3.0.0 (fake)\\n');\n"
                         + "  process.exit(0);\n"
                         + "}\n"
                         + "const fs = require('fs');\n"
                         + "const dir = process.env.VCS_TEST_LS_MARKER_DIR;\n"
                         + "fs.writeFileSync(dir + '/started', 'x');\n"
                         + "setTimeout(() => {\n"
                         + "  fs.writeFileSync(dir + '/finished', 'x');\n"
                         + "  process.stdout.write('{\"files\":[]}');\n"
                         + "}, Number(process.env.VCS_TEST_LS_DELAY_MS));\n")

                let materialization =
                    workspace.Provider.ObjectMaterialization

                let cancellation = OperationCancellation.Source()
                let context = OperationContext.create "lfs-list-silent" cancellation.Cancellation ignore
                let originalPath = currentProcessPath ()
                let originalWorkingDirectory = currentProcessWorkingDirectory ()
                let originalDelay = tryGetProcessEnvironment "VCS_TEST_LS_DELAY_MS" |> Option.ofObj
                let originalRoot = tryGetProcessEnvironment "VCS_TEST_LS_DISPATCH_ROOT" |> Option.ofObj
                let originalMarkerDir = tryGetProcessEnvironment "VCS_TEST_LS_MARKER_DIR" |> Option.ofObj
                let startedMarker = join [| fakeRoot; "started" |]
                let finishedMarker = join [| fakeRoot; "finished" |]
                let startedAt = DateTime.UtcNow

                let! result =
                    promise {
                        try
                            setCurrentProcessPath (fakeRoot + pathDelimiter + originalPath)
                            setCurrentProcessWorkingDirectory workspace.Repository.Descriptor.RepositoryRoot
                            setProcessEnvironment "VCS_TEST_LS_DELAY_MS" (string delayMs)
                            setProcessEnvironment "VCS_TEST_LS_DISPATCH_ROOT" workspace.Repository.Descriptor.RepositoryRoot
                            setProcessEnvironment "VCS_TEST_LS_MARKER_DIR" fakeRoot

                            if cancelOnStart then
                                let rec cancelWhenStarted (attempt: int) : JS.Promise<unit> = promise {
                                    if NodeFileSystem.existsSync startedMarker || attempt >= 400 then
                                        cancellation.Cancel()
                                    else
                                        do! sleepMs 25
                                        return! cancelWhenStarted (attempt + 1)
                                }

                                cancelWhenStarted 0 |> ignore

                            return! materialization.ListObjects workspace.Repository context |> executed |> Async.StartAsPromise
                        finally
                            setCurrentProcessWorkingDirectory originalWorkingDirectory
                            setCurrentProcessPath originalPath

                            match originalDelay with
                            | Some value -> setProcessEnvironment "VCS_TEST_LS_DELAY_MS" value
                            | None -> clearProcessEnvironment "VCS_TEST_LS_DELAY_MS"

                            match originalRoot with
                            | Some value -> setProcessEnvironment "VCS_TEST_LS_DISPATCH_ROOT" value
                            | None -> clearProcessEnvironment "VCS_TEST_LS_DISPATCH_ROOT"

                            match originalMarkerDir with
                            | Some value -> setProcessEnvironment "VCS_TEST_LS_MARKER_DIR" value
                            | None -> clearProcessEnvironment "VCS_TEST_LS_MARKER_DIR"
                    }

                let elapsedMs = (DateTime.UtcNow - startedAt).TotalMilliseconds
                let startedSeen = NodeFileSystem.existsSync startedMarker

                if cancelOnStart then
                    // A surviving process writes the finished marker when its delay ends.
                    do! sleepMs (delayMs + 1500)

                let finished = NodeFileSystem.existsSync finishedMarker
                do! harness.Cleanup()
                do! removeDirectoryAsync fakeRoot
                return result, elapsedMs, startedSeen, finished
            with error ->
                do! harness.Cleanup()
                do! removeDirectoryAsync fakeRoot
                return raise error
        }

        Vitest.test (
            "a listing that is silent for longer than 15 seconds still succeeds",
            TestOptions(timeout = 90000),
            fun () -> promise {
                let! result, _, _, finished = listWithSilentFakeGit 17000 false
                Vitest.expect(expectProviderValue "silent LFS listing" result).toEqual [||]
                Vitest.expect(finished).toBe true
            }
        )

        Vitest.test (
            "canceling the context ends the listing and its git-lfs process",
            TestOptions(timeout = 90000),
            fun () -> promise {
                let! result, elapsedMs, startedSeen, finished = listWithSilentFakeGit 4000 true
                let failure = expectProviderFailure "canceled silent LFS listing" result
                Vitest.expect(failure.Category).toEqual Canceled
                // The fake had started, so the cancellation hit a live process.
                Vitest.expect(startedSeen).toBe true
                // The listing returns before the fake's 4 s delay ends.
                Vitest.expect(elapsedMs < 3500.0).toBe true
                // A process that survived the cancellation would have written this marker by now.
                Vitest.expect(finished).toBe false
            }
        )
)
