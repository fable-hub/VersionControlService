# VersionControlService

VersionControlService defines portable version-control workspace contracts for Fable and .NET. The built-in Git and lakeFS providers target Fable/Node and implement the same `IVersionControl` interface. Hosts compose the providers they need and keep application concerns such as settings, UI state, and credential storage outside the library.

## Install

The packages are on nuget.org:

```console
dotnet add package VersionControlService
```

The umbrella package is dependency-only and carries the public abstractions, the text diff engine, the Node runtime, the Git provider, and the lakeFS provider at one coordinated version. An external provider that does not need the built-in implementations can reference `VersionControlService.Abstractions` alone.

The Git and lakeFS providers run on Fable and Node. They need the `simple-git` npm package, and the Git provider needs git 2.42 or newer on the path. Git LFS is optional and only the large-object functions use it. lakeFS needs no local tool. [Consuming the library](docs/consuming.md) lists what to install.

## Quick start

This example clones a Git repository, commits the changed files, pulls and pushes. Git calls those steps clone, commit, pull and push. The abstraction calls them `Clone`, `CreateRevision`, `Update` and `Publish`.

Every operation except `Probe` and `Close` takes an `OperationContext` and returns `Async<CallResult<'T>>`. A `CallResult` is `Executed` with the `OperationResult<'T>` of the provider, or `NoOp` with a reason when the provider does not have the function. The `valueOf` helper below unwraps a call and throws on anything other than a `Succeeded` result, which is enough for a first run. A real host reads the failure category and recovery action, as [reading a result](docs/consuming.md#reading-a-result) shows. Save the example as `Program.fs` in a console project named `QuickStart.fsproj` that references the package.

```fsharp
module QuickStart

open VersionControlService.Abstractions

module GitProvider = VersionControlService.Git.GitProvider
module GitProviderOptions = VersionControlService.Git.GitProviderOptions

/// Returns the payload and throws on a failure, a partial success or a missing function.
let valueOf (call: CallResult<'T>) : 'T =
    match call with
    | Executed(Succeeded outcome) -> outcome.Value
    | Executed(PartiallySucceeded(_, failure))
    | Executed(Failed failure) -> failwith $"{failure.Code}: {failure.Message}"
    | NoOp reason -> failwith reason

let provider = GitProvider.create GitProviderOptions.defaults

let location: RepositoryLocation = {
    ProviderId = ProviderId.tryCreate WellKnownProviderIds.Git |> Result.defaultWith invalidOp
    DisplayName = None
    ProviderLocation = "https://github.com/example/study-archive.git"
    ConnectionProfileId = None
}

let quickStart (targetPath: string) =
    async {
        let context = OperationContext.detached "quick-start"

        // Clone into a missing or empty directory. The result is the opened repository,
        // which every later call takes as its first argument.
        let! cloned =
            provider.Clone
                {
                    Location = location
                    TargetPath = targetPath
                    TargetRef = None
                    MaterializeAllObjects = false
                }
                context

        let repository = valueOf cloned

        let readVersion () =
            async {
                let! status = provider.GetStatus repository context
                return (valueOf status).RepositoryVersion
            }

        // Edit files under targetPath, then read the status.
        let! status = provider.GetStatus repository context
        let status = valueOf status

        // Commit exactly the changed paths. The version guards against a workspace
        // that changed since the status was read.
        if status.Changes.Length > 0 then
            let! revision =
                provider.CreateRevision
                    repository
                    {
                        Message = "Update results"
                        Paths = status.Changes |> Array.map (fun change -> change.Path)
                        ExpectedRepositoryVersion = status.RepositoryVersion
                    }
                    context

            printfn "Created revision %s" (RevisionId.value (valueOf revision))

        // Pull. Every mutation moves the version, so read it again each time.
        let! version = readVersion ()

        let! updated =
            provider.Synchronization.Update repository { ExpectedRepositoryVersion = version } context

        match updated with
        | NoOp reason -> printfn "This provider does not update: %s" reason
        | Executed _ ->
            printfn "After update: %A" (valueOf updated).Relationship

            // Push.
            let! version = readVersion ()

            let! published =
                provider.Synchronization.Publish
                    repository
                    {
                        ExpectedRepositoryVersion = version
                        ExpectedTargetRevision = None
                    }
                    context

            printfn "After publish: %A" (valueOf published).Relationship

        do! provider.Close repository
    }

[<EntryPoint>]
let main _ =
    quickStart "study-archive" |> Async.StartImmediate
    0
```

Each mutation carries the `RepositoryVersion` of the status the caller last read. A workspace that changed in between rejects the call with a `Concurrency` failure. The Git provider does not return the new version after a mutation, so the example reads the status again before each one. The example matches the update on `NoOp` to show the answer of a provider that does not have the function. The Git provider has every function.

`Synchronization.Synchronize` runs the pull and the push as one operation. It refuses when the pull would overwrite local changes or open a conflict session, and [synchronizing](docs/consuming.md#synchronizing) shows the refusals a host handles.

`GitProviderOptions.defaults` uses anonymous credentials. Public HTTPS remotes and local paths work with them, and SSH works through the user's SSH agent. `CreateRevision` takes the author from the repository's `user.name` and `user.email` and fails with `identity_missing` when git has none. The `Credentials` field of `GitProviderOptions` takes a credential strategy for tokens, and `RevisionIdentity` takes an identity strategy. [Credentials](docs/consuming.md#credentials) describes both.

### Run it

Fable compiles the program to JavaScript modules that Node can run directly. Start the workflow with `Async.StartImmediate`, as the entry point above does, or with `Async.StartAsPromise` from Fable.Core when the caller wants a promise. If Fable does not write a package file, add `output/package.json` containing `{"type":"module"}` so Node treats the output as ES modules:

```console
npm install simple-git
dotnet new tool-manifest
dotnet tool install fable --version 5.26.0
dotnet tool run fable QuickStart.fsproj --outDir output
node output/Program.js
```

Fable names each output file after its source file, so `Program.fs` becomes `output/Program.js`.
Bundling remains an option for a host that needs one file. Install Rollup with `npm install --save-dev rollup`, then run `npx rollup output/Program.js --file output/app.cjs --format cjs` and `node output/app.cjs`.

### Git terms and their names in the abstraction

| Git | VersionControlService |
|---|---|
| `git clone` | `Clone` |
| `git init` | `Initialize` |
| `git remote add origin` | `Bind` |
| `git status` | `GetStatus` |
| `git add` and `git commit` | `CreateRevision` |
| `git branch` | `ListRefs` and `CreateRef` |
| `git checkout <branch>` | `PreflightSwitchRef`, then `SwitchRef` |
| `git restore` | `RestorePaths` |
| `git diff --stat` | `GetDiffSummary` |
| `git fetch` | `Synchronization.Refresh` |
| `git pull` | `Synchronization.Update` |
| `git push` | `Synchronization.Publish` |
| `git pull`, then `git push` | `Synchronization.Synchronize` |

### lakeFS

A lakeFS provider answers the same calls where it has them, and answers `NoOp` for the rest. Only the provider and the location change. `LakeFsProvider.create` takes a `LakeFsProviderOptions` record. `StateRoot` is a directory outside every workspace where the provider keeps its state, `PathCaseSensitivity` matches the host's filesystem, `Hooks` sets test seams and `Credentials` resolves a connection profile to a lakeFS connection.

```fsharp
module LakeFsCredentials = VersionControlService.LakeFs.LakeFsCredentials
module LakeFsProvider = VersionControlService.LakeFs.LakeFsProvider
module LakeFsProviderOptions = VersionControlService.LakeFs.LakeFsProviderOptions

let lakeFsProvider =
    LakeFsProvider.create
        {
            StateRoot = "/var/lib/my-app/lakefs-state"
            PathCaseSensitivity = CaseSensitive
            Hooks = LakeFsProviderOptions.LakeFsSessionHooks.none
            Credentials =
                LakeFsCredentials.fixedConnection {
                    Endpoint = "http://localhost:8000"
                    AccessKeyId = accessKeyId
                    SecretAccessKey = secretAccessKey
                }
        }
```

The host supplies `accessKeyId` and `secretAccessKey`. The location then uses `WellKnownProviderIds.LakeFs` and a `ProviderLocation` such as `lakefs://study-archive/main`.

## Compose providers

Providers are ordinary values. Create them with host-owned configuration at the application composition root.

```fsharp
open VersionControlService.Abstractions

module GitProvider = VersionControlService.Git.GitProvider
module GitProviderOptions = VersionControlService.Git.GitProviderOptions
module LakeFsCredentials = VersionControlService.LakeFs.LakeFsCredentials
module LakeFsOptions = VersionControlService.LakeFs.LakeFsProviderOptions
module LakeFsProvider = VersionControlService.LakeFs.LakeFsProvider

let gitProvider = GitProvider.create GitProviderOptions.defaults

let lakeFsOptions: LakeFsOptions.LakeFsProviderOptions = {
    StateRoot = applicationStateDirectory
    PathCaseSensitivity = CaseInsensitive
    Hooks = LakeFsOptions.LakeFsSessionHooks.none
    Credentials = LakeFsCredentials.unconfigured
}

let lakeFsProvider = LakeFsProvider.create lakeFsOptions
```

The host asks the `Probe` of each provider it has and decides itself which provider owns a folder. It then gives the folder to `Open` of that provider.

## Public contract

A provider is an `IVersionControl`. Its functions are members of the interface or of one of eight group interfaces, and every call returns a `CallResult`. A provider that does not have a function answers `NoOp` with the reason, so a call never throws for that. A host that wants to know before it calls subscribes with `Subscribe.single` for one function or `Subscribe.all` for the list of everything the provider has. Operations return `Succeeded`, `PartiallySucceeded`, or `Failed` with structured state-change, recovery, affected-path, and revision evidence.

`Open`, `Clone`, `Initialize` and `Bind` return the opened `Repository`, and every function on a repository takes it as its first argument. `Probe`, `VerifyLocation`, `Initialize`, `Clone`, `Open`, `Bind`, `CheckDependencies` and `InstallDependency` need no opened repository. Repository locations and credentials stay provider-specific at the composition root.

See:

- [Consuming the library](docs/consuming.md)
- [Provider authoring](docs/provider-authoring.md)
- [Conformance profiles](docs/conformance-profiles.md)
- [Verification matrix](docs/verification/version-control-matrix.md)

## Build and test

```console
dotnet tool restore
dotnet restore VersionControlService.slnx
npm ci
dotnet build VersionControlService.slnx --no-restore -c Release
dotnet test tests/VersionControlService.Abstractions.Tests/VersionControlService.Abstractions.Tests.fsproj --no-restore
.\build.cmd test run
```

`build.cmd` and `build.sh` both forward their arguments to the build project, so on non-Windows systems the last command becomes:

```console
./build.sh test run
```

The ordinary suite skips live lakeFS tests. Run the pinned Docker matrix explicitly when Docker is available:

```console
.\build.cmd test lakefs
```

CI runs that row on Linux as `./build.sh test lakefs`.

Run `.\build.cmd` without a target to list every target, including the focused test run and the package graph checks.

## Pack a local feed

Contributors who want to try unreleased changes in a host can pack the six coordinated packages into a local feed. Run this from the repository root:

```console
dotnet restore VersionControlService.slnx
dotnet run --project build/Build.fsproj -- pack --version=0.0.1-local --output=<feed-dir>
dotnet nuget add source <feed-dir> --name vcs-local
dotnet add package VersionControlService --version 0.0.1-local
```

`pack` packs with `--no-restore`, so a fresh clone needs the restore first. It empties the output directory, so pick a directory outside the clone.

## NuGet release

Add the next version and its release notes to `CHANGELOG.md`, then run the release target:

```console
NUGET_KEY=<key> dotnet run --project build/Build.fsproj -- release nuget
```

The target packs into `nupkgs/` and verifies the package graph before it pushes to NuGet.
Pass `--dry-run` to pack and verify without publishing.
