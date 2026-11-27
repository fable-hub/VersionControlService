module VersionControlService.Tests.ScaffoldTests

open Fable.Core
open Fable.Core.JsInterop
open VersionControlService.Abstractions
open VersionControlService.Git
open Vitest
open VersionControlService.Tests.NodePath
open VersionControlService.Tests.VitestBindings

module LakeFsCredentials = VersionControlService.LakeFs.LakeFsCredentials
module LakeFsProviderOptions = VersionControlService.LakeFs.LakeFsProviderOptions
module LakeFsProvider = VersionControlService.LakeFs.LakeFsProvider

let private osModule: obj = importAll "os"

/// A repository that no provider opened.
type private ForeignRepository(descriptor: RepositoryDescriptor) =
    inherit Repository(descriptor)

describe("VersionControlService provider scaffold", fun () ->
    test("keeps repository path validation in the provider-neutral abstraction", fun () ->
        toEqual (
            expect (RepositoryPath.tryCreate "folder/file.txt" |> Result.map RepositoryPath.value),
            Ok "folder/file.txt"
        )

        toBe (expect (RepositoryPath.tryCreate "../outside.txt" |> Result.isError), true)
    )

    Vitest.test (
        "a provider refuses a repository that another provider opened",
        fun () -> promise {
            let foreign =
                ForeignRepository(
                    {
                        ProviderId = ProviderId.tryCreate "test.foreign" |> Result.defaultWith failwith
                        RepositoryRoot = "/foreign/workspace"
                        Location = None
                    }
                )
                :> Repository

            let providers = [
                "Git", GitProvider.create GitProviderOptions.defaults
                "lakeFS",
                LakeFsProvider.create {
                    StateRoot = join [| osModule?tmpdir () |> unbox<string>; "vcs-foreign-repository-state" |]
                    PathCaseSensitivity = CaseInsensitive
                    Hooks = LakeFsProviderOptions.LakeFsSessionHooks.none
                    Credentials = LakeFsCredentials.unconfigured
                }
            ]

            for name, provider in providers do
                let! result =
                    provider.GetStatus foreign (OperationContext.detached "foreign-repository")
                    |> Async.StartAsPromise

                match result with
                | Executed(Failed failure) ->
                    Vitest.expect(failure.Code).toBe "repository_of_another_provider"
                    Vitest.expect(failure.Category).toBe Validation
                | _ -> failwith $"The {name} provider did not refuse the repository of another provider."
        }
    )
)
