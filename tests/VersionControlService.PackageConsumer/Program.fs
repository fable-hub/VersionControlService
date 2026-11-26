module VersionControlService.PackageConsumer.Program

open VersionControlService.Abstractions
open Fable.Core
open Fable.Core.JsInterop

module GitProvider = VersionControlService.Git.GitProvider
module GitProviderOptions = VersionControlService.Git.GitProviderOptions
module LakeFsCredentials = VersionControlService.LakeFs.LakeFsCredentials
module LakeFsProvider = VersionControlService.LakeFs.LakeFsProvider
module LakeFsProviderOptions = VersionControlService.LakeFs.LakeFsProviderOptions
module NodeFileSystem = VersionControlService.Runtime.Node.FileSystem
module NodePath = VersionControlService.Runtime.Node.Path

[<Import("tmpdir", "node:os")>]
let private temporaryDirectory () : string = jsNative

[<Import("mkdtempSync", "node:fs")>]
let private makeTemporaryDirectory (_prefix: string) : string = jsNative

[<Import("rmSync", "node:fs")>]
let private removeTemporaryDirectory (_path: string) (_options: NodeFileSystem.RmOptions) : unit = jsNative

let private runGitOperation (provider: IVersionControl) =
    async {
        let targetPath =
            NodePath.join [| temporaryDirectory (); "version-control-service-consumer-" |]
            |> makeTemporaryDirectory

        let context = OperationContext.detached "package-consumer-git-operation"

        try
            let! initialized =
                provider.Initialize
                    { TargetPath = targetPath; Location = None }
                    context

            match initialized with
            | Executed(Succeeded initialization) ->
                let repository = initialization.Value
                let! status = Async.Catch(provider.GetStatus repository context)
                let! closed = Async.Catch(provider.Close repository)

                return
                    match status, closed with
                    | Choice1Of2(Executed(Succeeded _)), Choice1Of2() -> true
                    | _ -> false
            | _ -> return false
        finally
            removeTemporaryDirectory targetPath (NodeFileSystem.RmOptions(recursive = true, force = true))
    }

[<EntryPoint>]
let main _ =
    let gitProvider = GitProvider.create GitProviderOptions.defaults

    let lakeFsOptions: LakeFsProviderOptions.LakeFsProviderOptions = {
        StateRoot = ".version-control-service-package-consumer-state"
        PathCaseSensitivity = CaseInsensitive
        Hooks = LakeFsProviderOptions.LakeFsSessionHooks.none
        Credentials = LakeFsCredentials.unconfigured
    }

    let lakeFsProvider = LakeFsProvider.create lakeFsOptions

    // lakeFS has no history, so asking for it needs no server and shows that the package loads.
    let operation =
        match Subscribe.single (fun functions -> functions.History.ListRevisions) lakeFsProvider with
        | Error _ -> runGitOperation gitProvider
        | Ok _ -> async.Return false

#if FABLE_COMPILER
    Async.StartImmediate(async {
        let! result = Async.Catch operation

        let code =
            match result with
            | Choice1Of2 true -> 0
            | _ -> 1

        Fable.Core.JsInterop.emitJsStatement code "process.exitCode = $0"
    })
    0
#else
    try
        if Async.RunSynchronously operation then 0 else 1
    with _ ->
        1
#endif
