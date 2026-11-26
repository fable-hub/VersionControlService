/// lakeFS provider helpers: probing by the provider state that records a folder and
/// access-intent verification against the API.
module VersionControlService.LakeFs.LakeFsProviderFactory

open VersionControlService.Abstractions
open VersionControlService.LakeFs.LakeFsTypes

module LakeFsApi = VersionControlService.LakeFs.LakeFsApi
module LakeFsCredentials = VersionControlService.LakeFs.LakeFsCredentials
module LakeFsProviderOptions = VersionControlService.LakeFs.LakeFsProviderOptions
module LakeFsStateStore = VersionControlService.LakeFs.LakeFsStateStore
module LakeFsWorkspaceIndex = VersionControlService.LakeFs.LakeFsWorkspaceIndex

let lakeFsProviderId =
    match ProviderId.tryCreate "lakefs" with
    | Ok providerId -> providerId
    | Error message -> failwith message

let private connectionFailure (message: string) =
    OperationFailure.createRedacted Authentication "connection_profile_unresolved" message

let internal resolveLocation
    (credentials: LakeFsCredentials.LakeFsCredentialStrategy)
    (location: RepositoryLocation)
    : Async<Result<LakeFsConnection * LakeFsLocation, OperationFailure>> =
    async {
        match LakeFsLocation.tryParse location.ProviderLocation with
        | Error message -> return Error(OperationFailure.create Validation "invalid_location" message)
        | Ok parsed ->
            let! connection = credentials.ResolveConnection location.ConnectionProfileId

            match connection with
            | Error message -> return Error(connectionFailure message)
            | Ok resolved -> return Ok(resolved, parsed)
    }

/// Verifies requested intents against the server: reads through repository
/// lookup, writes through a transient probe branch (created and deleted).
let verifyAccess
    (credentials: LakeFsCredentials.LakeFsCredentialStrategy)
    (request: VerifyLocationRequest)
    (context: OperationContext)
    : Async<OperationResult<AccessReport>> =
    async {
        let! resolved = resolveLocation credentials request.Location

        match resolved with
        | Error failure -> return Failed failure
        | Ok(connection, location) ->
            let granted = ResizeArray<AccessIntent>()
            let denied = ResizeArray<AccessIntent>()

            let readIntents =
                request.Intents |> Array.filter (fun intent -> intent = ReadIntent)

            let writeIntents =
                request.Intents |> Array.filter (fun intent -> intent <> ReadIntent)

            if readIntents.Length > 0 then
                let! repository = LakeFsApi.getRepository connection location.Repository context

                match repository with
                | Ok _ -> granted.AddRange readIntents
                | Error _ -> denied.AddRange readIntents

            if writeIntents.Length > 0 then
                let probeBranch =
                    $"vcs-access-probe-{LakeFsWorkspaceIndex.createOwnershipToken ()}"

                let! created =
                    LakeFsApi.createBranch connection location.Repository probeBranch location.TargetRef context

                match created with
                | Ok() ->
                    granted.AddRange writeIntents
                    let! _ = LakeFsApi.deleteBranch connection location.Repository probeBranch context
                    ()
                | Error _ -> denied.AddRange writeIntents

            return
                OperationResult.succeeded {
                    Location = request.Location
                    GrantedIntents = granted.ToArray()
                    DeniedIntents = denied.ToArray()
                }
    }

/// Probes a folder for lakeFS provider state: the provider detects a folder when a state entry records it.
let probe (options: LakeFsProviderOptions.LakeFsProviderOptions) (folder: string) : Async<ProbeResult> =
    async {
        try
            match LakeFsStateStore.findByFolder options folder with
            | Ok _ -> return Detected(folder, 80, None)
            | Error _ -> return NotDetected
        with error ->
            return ProbeFailed(OperationFailure.createRedacted ProviderError "probe_exception" error.Message)
    }
