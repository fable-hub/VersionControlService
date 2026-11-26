module VersionControlService.Tests.Contracts.ProvisioningProviderSuite

open Fable.Core
open VersionControlService.Abstractions
open VersionControlService.Tests.Contracts.ProviderHarness
open VersionControlService.Tests.Contracts.ProviderHarness.SuiteHelpers
open Vitest

module NodeFileSystem = VersionControlService.Runtime.Node.FileSystem

/// Provisioning profile: initialize, clone, bind/re-target, nonempty-target behavior,
/// access-intent verification, and dependency diagnostics.
let register (harness: ProviderTestHarness) : string * (unit -> int) =
    let profileName = $"{harness.Name} / provisioning profile"
    let mutable testCount = 0

    Vitest.describe (
        profileName,
        fun () ->
            let profileTest (name: string) (body: unit -> JS.Promise<unit>) =
                testCount <- testCount + 1
                Vitest.test (name, suiteTestOptions, body)

            profileTest "initializes a new workspace and opens a session"
            <| fun () -> promise {
                let! targetPath = harness.CreateLocalPath()
                let! location = harness.CreateLocation()

                let! initializeResult =
                    run (
                        executed (
                            harness.Provider.Initialize
                                {
                                    TargetPath = targetPath
                                    Location = Some location
                                }
                                (ctx "initialize")
                        )
                    )

                let repository = expectValue "initialize" initializeResult

                let! status = repositoryStatus harness.Provider repository
                Vitest.expect(status.RepositoryVersion.Length > 0).toBe (true)
            }

            profileTest "clones an existing repository location"
            <| fun () -> promise {
                let! anchor = harness.CreateWorkspace()
                let! clonePath = harness.CreateLocalPath()

                let! cloneResult =
                    run (
                        executed (
                            harness.Provider.Clone
                                {
                                    Location = locationOf anchor
                                    TargetPath = clonePath
                                    TargetRef = None
                                    MaterializeAllObjects = true
                                }
                                (ctx "clone")
                        )
                    )

                let repository = expectValue "clone" cloneResult

                let! status = repositoryStatus harness.Provider repository
                Vitest.expect(status.Changes.Length).toBe (0)

                match status.Synchronization with
                | Some sync -> Vitest.expect(sync.Relationship).toEqual (UpToDate)
                | None -> failwith "Expected synchronization state in a cloned workspace."
            }

            profileTest "a pre-canceled clone leaves its fresh target unchanged"
            <| fun () -> promise {
                let! anchor = harness.CreateWorkspace()
                let! targetPath = harness.CreateLocalPath()
                let targetExistedBefore = NodeFileSystem.existsSync targetPath
                let entriesBefore =
                    if targetExistedBefore then
                        NodeFileSystem.readdirSync targetPath
                    else
                        [||]

                if targetExistedBefore then
                    Vitest.expect(entriesBefore).toEqual [||]

                let cancellation = OperationCancellation.Source()
                cancellation.Cancel()
                let cloneContext =
                    OperationContext.create "pre-canceled-clone" cancellation.Cancellation ignore

                let! cloneResult =
                    run (
                        executed (
                            harness.Provider.Clone
                                {
                                    Location = locationOf anchor
                                    TargetPath = targetPath
                                    TargetRef = None
                                    MaterializeAllObjects = true
                                }
                                cloneContext
                        )
                    )

                let failure = expectFailure "pre-canceled clone" cloneResult
                Vitest.expect(failure.Category).toEqual (Canceled)
                Vitest.expect(failure.Code).toBe "operation_canceled"
                Vitest.expect(failure.StateChanged).toBe false
                Vitest.expect(NodeFileSystem.existsSync targetPath).toBe(targetExistedBefore)

                if targetExistedBefore then
                    Vitest.expect(NodeFileSystem.readdirSync targetPath).toEqual entriesBefore
            }

            profileTest "bind re-targets an existing workspace without cloning"
            <| fun () -> promise {
                let! workspace = harness.CreateWorkspace()
                let! otherWorkspace = harness.CreateWorkspace()

                // Give the other target a distinguishable head.
                do!
                    harness.AdvanceTarget otherWorkspace [|
                        {
                            Path = "other-target.txt"
                            Content = Some "other target content\n"
                        }
                    |]

                let! bindResult =
                    run (
                        executed (
                            harness.Provider.Bind
                                {
                                    RepositoryRoot = workspace.Repository.Descriptor.RepositoryRoot
                                    Location = locationOf otherWorkspace
                                }
                                (ctx "bind")
                        )
                    )

                let rebound = expectValue "bind" bindResult

                Vitest
                    .expect(rebound.Descriptor.Location |> Option.map _.ProviderLocation)
                    .toEqual (Some((locationOf otherWorkspace).ProviderLocation))

                let! refreshResult =
                    run (executed (harness.Provider.Synchronization.Refresh rebound (ctx "rebound-refresh")))

                let state = expectValue "rebound refresh" refreshResult

                // The rebound repository now observes the other target's live head.
                let! otherRefreshResult =
                    run (
                        executed (
                            otherWorkspace.Provider.Synchronization.Refresh
                                otherWorkspace.Repository
                                (ctx "other-refresh")
                        )
                    )

                let otherState = expectValue "other workspace refresh" otherRefreshResult

                Vitest.expect(state.TargetRevision = otherState.TargetRevision).toBe (true)
            }

            profileTest "cloning into a nonempty target fails structurally"
            <| fun () -> promise {
                let! anchor = harness.CreateWorkspace()

                let! cloneResult =
                    run (
                        executed (
                            harness.Provider.Clone
                                {
                                    Location = locationOf anchor
                                    TargetPath = anchor.Repository.Descriptor.RepositoryRoot
                                    TargetRef = None
                                    MaterializeAllObjects = true
                                }
                                (ctx "clone-nonempty")
                        )
                    )

                let failure = expectFailure "clone into nonempty target" cloneResult
                Vitest.expect(failure.Category).toEqual (Validation)
                Vitest.expect(failure.Code).toBe (ConformanceCodes.TargetNotEmpty)
            }

            profileTest "access intents are verified against locations"
            <| fun () -> promise {
                let! goodLocation = harness.CreateLocation()

                let intents = [|
                    ReadIntent
                    WriteObjectIntent
                    CreateRevisionIntent
                    PublishIntent
                |]

                let! goodResult =
                    run (
                        executed (
                            harness.Provider.VerifyLocation
                                {
                                    Location = goodLocation
                                    Intents = intents
                                }
                                (ctx "verify-good")
                        )
                    )

                let goodReport = expectValue "verify authorized location" goodResult
                Vitest.expect(goodReport.DeniedIntents.Length).toBe (0)
                Vitest.expect(goodReport.GrantedIntents.Length).toBe (intents.Length)

                let! unauthorizedLocation = harness.CreateUnauthorizedLocation()

                let! unauthorizedResult =
                    run (
                        executed (
                            harness.Provider.VerifyLocation
                                {
                                    Location = unauthorizedLocation
                                    Intents = intents
                                }
                                (ctx "verify-unauthorized")
                        )
                    )

                // Either a structured failure or a report with denied intents is valid;
                // silently granting everything is not. Providers whose unauthorized
                // fixture is unreachable may classify it as NotFound/Network.
                match unauthorizedResult with
                | Succeeded outcome -> Vitest.expect(outcome.Value.DeniedIntents.Length > 0).toBe (true)
                | PartiallySucceeded(_, failure)
                | Failed failure ->
                    let structurallyDenied =
                        match failure.Category with
                        | Authorization
                        | Authentication
                        | NotFound
                        | Network -> true
                        | _ -> false

                    Vitest.expect(structurallyDenied).toBe (true)
            }

            profileTest "dependency diagnostics report components with remediation"
            <| fun () -> promise {
                let! dependenciesResult = run (executed (harness.Provider.CheckDependencies(ctx "dependencies")))
                let dependencies = expectValue "check dependencies" dependenciesResult

                for dependency in dependencies do
                    Vitest.expect(dependency.Component.Length > 0).toBe (true)

                    if not dependency.Installed || not dependency.Compatible then
                        Vitest.expect(dependency.Remediation.IsSome).toBe (true)
            }
    )

    // Vitest defers describe callbacks to the collection phase, so expose the
    // registered-test count as a getter evaluated at test time.
    profileName, (fun () -> testCount)
