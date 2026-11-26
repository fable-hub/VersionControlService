module VersionControlService.Tests.Contracts.OptionalServiceProviderSuites

open Fable.Core
open VersionControlService.Abstractions
open VersionControlService.Tests.Contracts.ProviderHarness
open VersionControlService.Tests.Contracts.ProviderHarness.SuiteHelpers
open Vitest

/// Optional function suites: the tests of a group run only when `harness.ExpectedServices` names the group.
/// The first test checks that the groups found in the function list of the provider match that list.
let register (harness: ProviderTestHarness) : string * (unit -> int) =
    let profileName = $"{harness.Name} / optional service suites"
    let mutable testCount = 0

    Vitest.describe (
        profileName,
        fun () ->
            let profileTest (name: string) (body: unit -> JS.Promise<unit>) =
                testCount <- testCount + 1
                Vitest.test (name, suiteTestOptions, body)

            profileTest "advertised optional services match the harness expectation"
            <| fun () -> promise {
                let! workspace = harness.CreateWorkspace()
                let discovered = discoverFeatures workspace.Provider
                Vitest.expect(List.toArray discovered).toEqual (List.toArray (List.sort harness.ExpectedServices))
            }

            if harness.ExpectedServices |> List.contains "materialization" then
                profileTest "object materialization lists and hydrates objects"
                <| fun () -> promise {
                    let! workspace = harness.CreateWorkspace()

                    let materialization = workspace.Provider.ObjectMaterialization

                    let! listResult = run (executed (materialization.ListObjects workspace.Repository (ctx "list-objects")))
                    expectValue "list objects" listResult |> ignore
                }

                profileTest "object materialization of no paths changes nothing"
                <| fun () -> promise {
                    let! workspace = harness.CreateWorkspace()

                    let materialization = workspace.Provider.ObjectMaterialization

                    let! materializeResult = run (executed (materialization.Materialize workspace.Repository [||] (ctx "materialize-none")))
                    expectUnchanged "materialize of no paths" materializeResult |> ignore

                    let! dematerializeResult = run (executed (materialization.Dematerialize workspace.Repository [||] (ctx "dematerialize-none")))
                    expectUnchanged "dematerialize of no paths" dematerializeResult |> ignore
                }

            if harness.ExpectedServices |> List.contains "storage-policy" then
                profileTest "storage policy settings round-trip"
                <| fun () -> promise {
                    let! workspace = harness.CreateWorkspace()

                    let storagePolicy = workspace.Provider.StoragePolicy

                    let! settingsResult = run (executed (storagePolicy.GetSettings workspace.Repository (ctx "get-settings")))
                    expectValue "get storage settings" settingsResult |> ignore
                }

            if harness.ExpectedServices |> List.contains "maintenance" then
                profileTest "maintenance operations return structured results"
                <| fun () -> promise {
                    let! workspace = harness.CreateWorkspace()

                    let maintenance = workspace.Provider.Maintenance

                    let! pruneResult = run (executed (maintenance.Prune workspace.Repository (ctx "prune")))

                    // Success or a classified failure are both fine; hidden errors are not.
                    match pruneResult with
                    | Succeeded _
                    | PartiallySucceeded _ -> ()
                    | Failed failure -> Vitest.expect(failure.Code.Length > 0).toBe (true)
                }

                profileTest "maintenance reports progress and honors cancellation"
                <| fun () -> promise {
                    let! workspace = harness.CreateWorkspace()

                    let maintenance = workspace.Provider.Maintenance

                    let progressReports = ResizeArray<OperationProgress>()

                    let progressContext =
                        OperationContext.create
                            "maintenance-progress"
                            OperationCancellation.none
                            progressReports.Add

                    let! _ = run (executed (maintenance.Deduplicate workspace.Repository progressContext))
                    Vitest.expect(progressReports.Count > 0).toBe (true)

                    let cancellation = OperationCancellation.Source()
                    cancellation.Cancel()

                    let canceledContext =
                        OperationContext.create "maintenance-canceled" cancellation.Cancellation ignore

                    let! canceledResult = run (executed (maintenance.Prune workspace.Repository canceledContext))
                    let failure = expectFailure "canceled maintenance" canceledResult
                    Vitest.expect(failure.Category).toEqual (Canceled)
                    Vitest.expect(failure.StateChanged).toBe (false)
                }

            if harness.ExpectedServices |> List.contains "browser" then
                profileTest "repository browser URLs are data, never exceptions"
                <| fun () -> promise {
                    let! workspace = harness.CreateWorkspace()

                    let browser = workspace.Provider.RepositoryBrowser

                    let! urlResult = run (executed (browser.GetRepositoryWebUrl workspace.Repository (ctx "web-url")))
                    expectValue "repository web url" urlResult |> ignore
                }

            if harness.ExpectedServices |> List.contains "history" then
                profileTest "history starts at the current revision and lists its changes"
                <| fun () -> promise {
                    let! workspace = harness.CreateWorkspace()

                    let history = workspace.Provider.History

                    let! status = getStatus workspace

                    let current =
                        match status.Synchronization |> Option.bind _.LocalRevision with
                        | Some revision -> revision
                        | None -> failwith "Expected the workspace to report its current revision."

                    let! pageResult = run (executed (history.ListRevisions workspace.Repository { Cursor = None; Limit = 10 } (ctx "list-revisions")))
                    let page = expectValue "list revisions" pageResult
                    Vitest.expect(page.Revisions.Length > 0).toBe (true)
                    Vitest.expect(page.Revisions[0].Id = current).toBe (true)

                    let! changesResult = run (executed (history.GetRevisionChanges workspace.Repository current (ctx "revision-changes")))
                    let changes = expectValue "get revision changes" changesResult
                    Vitest.expect(changes.Entries.Length > 0).toBe (true)
                }
    )

    // Vitest defers describe callbacks to the collection phase, so expose the
    // registered-test count as a getter evaluated at test time.
    profileName, (fun () -> testCount)
