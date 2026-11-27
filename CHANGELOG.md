# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Types of changes**

-   `Added` for new features.
-   `Changed` for changes in existing functionality.
-   `Deprecated` for soon-to-be removed features.
-   `Removed` for now removed features.
-   `Fixed` for any bug fixes.
-   `Security` in case of vulnerabilities.

## [Unreleased]

### Added

-   `DiffComparison.Revision` compares the version in a revision with the version in its first parent. The comparison does not read the working tree. The Git provider accepts only a full object id as the revision.
-   `IHistory` is a new group of functions. `ListRevisions` returns the revisions that lead to the current one, newest first, in pages, and `GetRevisionChanges` returns the objects that a revision changed against its first parent. The Git provider has both and the lakeFS provider answers `NoOp`. The new public types of the history are `ListRevisionsRequest`, `RevisionPage`, `RevisionEntry` and `RevisionSignature`. `ListRevisions` fails with the new code `invalid_cursor` for a malformed cursor, and `GetRevisionChanges` fails with the new code `revision_not_found` for a full object id that names no commit.
-   `IVersionControl` is the one interface of a provider and replaces `ProviderFactory` and `WorkspaceSession`. It has `Probe`, `VerifyLocation`, `Initialize`, `Clone`, `Bind`, `Open`, `CheckDependencies`, `InstallDependency`, the eight functions of the former core (`GetStatus`, `ListRefs`, `CreateRef`, `PreflightSwitchRef`, `SwitchRef`, `CreateRevision`, `RestorePaths` and `GetDiffSummary`) and `Close`.
-   The group interfaces `ISynchronization`, `IConflictResolution`, `ITextDiff`, `IHistory`, `IRepositoryBrowser`, `IObjectMaterialization`, `IStoragePolicy` and `IMaintenance` are members of `IVersionControl` and replace the optional services. Each function keeps the name it had and takes the `Repository` as its first argument.
-   `Repository` is an opened repository. `Open`, `Clone`, `Initialize` and `Bind` return it and `Close` releases it. Every other function takes it as its first argument. `Repository.Descriptor` holds the `RepositoryDescriptor`. A provider that receives a repository that another provider opened answers with the failure of `Repository.ofAnotherProvider`, which has the code `repository_of_another_provider`.
-   Every call through the interface returns `CallResult<'T>`. `Executed` holds the `OperationResult<'T>` of a provider that ran the function, and `NoOp` holds the reason of a provider that does not have it. A call to a function that is missing never throws.
-   `ProviderFunctions` lists every function of a provider except `Probe` and `Close`. Each function is an `Entry`, either `Implemented` with the function or `Missing` with the reason. The groups have their own records, `SynchronizationFunctions`, `ConflictResolutionFunctions`, `TextDiffFunctions`, `HistoryFunctions`, `RepositoryBrowserFunctions`, `ObjectMaterializationFunctions`, `StoragePolicyFunctions` and `MaintenanceFunctions`.
-   `ListedProvider(probe, close, functions)` builds an `IVersionControl` from a `ProviderFunctions` list. It is the only implementation of the interface in the library, and an external provider uses it too.
-   `Subscribe.single` returns one function of a provider with its own signature and its direct result, or the reason when the provider does not have it. `Subscribe.all` returns the whole `ProviderFunctions` list. Both fail for an `IVersionControl` that is not a `ListedProvider`. Subscribing runs no check and calls no function.
-   `GitProvider.create` takes a `GitProviderOptions` record (`Hooks`, `Credentials`, `RevisionIdentity`, `RevisionPolicy` and `TextDiff`) and returns the Git provider. `GitProviderOptions.defaults` has no hooks, anonymous credentials and identity, the automatic revision policy and no text diff pool. The Git provider has every function.
-   `LakeFsProvider.create` takes a `LakeFsProviderOptions` record and returns the lakeFS provider. The record has the new fields `Hooks` and `Credentials` beside `StateRoot` and `PathCaseSensitivity`. The lakeFS provider has no `InstallDependency`, text diff, history, `GetRepositoryWebUrl`, object materialization, storage policy or maintenance, and a call to one of them answers `NoOp`.
-   `OpenRequest` (`RepositoryRoot` and an optional `ConnectionProfileId`) replaces `AdoptRequest`. `Open` takes it, finds the repository root and returns the opened `Repository`.
-   `Clone`, `Initialize` and `Bind` return the opened `Repository`. The lakeFS provider runs the first open of the folder as part of each of them.
-   The lakeFS provider finds the state of a folder by the folder, so `Open` needs no stored reference. When the first open of a folder did not finish, `Open` fails with the new code `first_open_incomplete`, and the folder has to be initialized or bound again, or deleted and cloned again.

### Changed

-   The library is built and tested with Fable 5.26.0, and the package check no longer compiles the consumer with Fable 5.0.0-alpha.21.
-   `OperationEffect.NoOp` is renamed to `OperationEffect.Unchanged`. Its `reason` field stays, and a match on `OperationEffect` has to use the new case name.
-   `OperationOutcome.noOp` is renamed to `OperationOutcome.unchanged`. Its signature and behavior stay.
-   `OperationResult.noOp` is renamed to `OperationResult.unchanged`. Its signature and behavior stay.
-   `OpenDiffRequest` has a new required field `Comparison`, so record literals have to set it. `DiffComparison.WorkingFile` keeps the earlier behavior.
-   A committed side of a text diff reports its revision in `DiffSourceInfo.Revision`, also when its content is a Git LFS object. Such a side used to report none.
-   `WorkspaceDescriptor` is renamed to `RepositoryDescriptor`. Its fields and behavior stay, and the `WorkspaceRoot` field is renamed as described below.
-   `WorkspaceStatus` is renamed to `RepositoryStatus`. `GetStatus` and `SwitchRef` return the new type name.
-   The field `WorkspaceRoot` is renamed to `RepositoryRoot` in `RepositoryDescriptor`, `BindRequest` and the `RevisionIdentityRequest` of the Git package.
-   The field `WorkspaceVersion` of `RepositoryStatus` is renamed to `RepositoryVersion`.
-   The field `ExpectedWorkspaceVersion` is renamed to `ExpectedRepositoryVersion` in `CreateRefRequest`, `SwitchRefRequest`, `CreateRevisionRequest`, `RestoreRequest`, `UpdateRequest`, `PublishRequest`, `SynchronizeRequest`, `ResolveConflictRequest`, `FinalizeConflictRequest` and `CancelConflictRequest`.
-   The field `ResultingWorkspaceVersion` of `OperationOutcome` is renamed to `ResultingRepositoryVersion`.
-   The field `WorkspaceRevision` of `SynchronizationState` is renamed to `LocalRevision`.
-   The Git provider always has a text diff. Without a pool in `GitProviderOptions.TextDiff`, `ITextDiff.Open` fails with `diff_worker_failed`. The creation functions that took no `GitSessionOptions` used to leave the text diff absent.
-   `LakeFsSessionHooks` moved from `LakeFsWorkspaceSession` to the module `LakeFsProviderOptions`.
-   `LakeFsProviderFactory.probe` takes the `LakeFsProviderOptions` and the folder. It answers `Detected` when the provider state records the folder and no longer looks for `.lakefs_ref.yaml`.
-   `TextDiffPool.Service` and `GitTextDiffService.create` return the new record `TextDiffPool.TextDiffCalls`, which holds the seven text diff functions without a repository argument. They returned a `TextDiffService` before.

### Removed

-   `ProviderFactory`, `WorkspaceSession` with `WorkspaceSession.createCoreOnly` and `WorkspaceSession.availability`, `ServiceAvailability` and `CoreVersionControl` are removed. `IVersionControl` replaces them.
-   `ProviderFactory.Id` is removed, and `IVersionControl` has no member for it. A host takes the provider id from `Descriptor.ProviderId` of an opened `Repository`. Before it has opened one, it knows the id because it chose the provider, and `WellKnownProviderIds` holds the ids of the two built-in providers.
-   The service records `SynchronizationService`, `ConflictResolutionService`, `TextDiffService`, `ObjectMaterializationService`, `StoragePolicyService`, `StorageMaintenanceService` and `RepositoryBrowserService` are removed, together with the optional services as `option` values on the session. The group interfaces replace them.
-   `WorkspaceBinding`, `WorkspaceBinding.CurrentSchemaVersion` and the `ProviderStateRef` field are removed. A host stores no binding. It gives the folder to `Open`, and the provider finds its own state.
-   `AdoptRequest` and the `Adopt` function of the factory are removed. `Open` with an `OpenRequest` does what adopt and open did together.
-   The failure code `operation_not_supported` is removed. A function that a provider does not have answers `NoOp`, and a refusal for one input is a failure with a code of the provider.
-   The ten creation functions `GitWorkspaceSession.createFactory`, `createFactoryWithCredentials`, `createFactoryWithCredentialsAndIdentity`, `createFactoryWithCredentialsIdentityAndPolicy`, `createFactoryWithOptions`, `createSession`, `createSessionWithCredentials`, `createSessionWithCredentialsAndIdentity`, `createSessionWithCredentialsIdentityAndPolicy` and `createSessionWithOptions` are removed, together with `GitSessionOptions`. `GitProvider.create` replaces them, and `GitSessionHooks` stays.
-   `LakeFsWorkspaceSession.createFactory`, `createFactoryWithPolicy`, `createFactoryWithHooks`, `createFactoryWithHooksAndPolicy`, `openSession` and `LakeFsProviderFactory.createFactory` are removed. `LakeFsProvider.create` replaces them. lakeFS ignored the revision policy that some of them accepted, so it is gone with them. `LakeFsWorkspaceSession.cleanupOwnedWorkspaceBranch` is no longer public.
-   The stand-in services that filled absent optional services are removed, together with `WorkspaceSession.withFallbackServices` and `ProviderFactory.withFallbackServices`. A call to a function that a provider does not have answers `NoOp`.
-   `FallbackServiceCodes` and its `FallbackServiceCodes.ServiceUnavailable` code (`service_unavailable`) are removed, because only the stand-in services reported it.
-   `DiffBlocker.ProviderUnsupported` is removed, because only the stand-in text diff service returned it. A match on `DiffBlocker` has to drop the case.
-   `ProviderResolver.ProviderCatalog`, `ProviderResolver.tryCreateCatalog`, `ProviderResolver.factories` and `ProviderResolver.tryGetFactory` are removed. A host keeps its providers itself.
-   `ProviderResolver.resolve` is removed, together with `ProviderResolver.ResolutionRequest`, `ProviderResolver.ResolutionReport`, `ProviderResolver.WorkspaceResolution` with its cases `Bound`, `ProbedWorkspace`, `AmbiguousWorkspace` and `UnmanagedWorkspace`, and `ProviderResolver.DetectionCandidate`. A host asks the `Probe` of each provider it has and decides itself which provider owns a folder. `ProviderResolver.normalizePath` and `PathCaseSensitivity` stay.
-   The lakeFS failure code `provider_state_ref_missing` is removed. `Open` no longer fails with `provider_state_mismatch` for a location that differs from the stored index, because a host passes no reference and no location to `Open`. The code stays for a state entry that belongs to another folder.

### Fixed

-   With `versioncontrolservice.lfs.materializelargeobjects` on, a Git `Update` or `Synchronize` that has nothing to merge but turns pointer files into their large content now reports the effect `Performed` and lists the downloaded paths in `AffectedPaths`. Before, it returned the effect `NoOp` (now `Unchanged`) and no path. This holds when the download fails after it wrote some files. An update that has no pointer left to download stays `Unchanged`.

## 0.5.0 - 2026-11-19

### Added

-   `StorageMaintenanceService.CanDeduplicate` reports whether `Deduplicate` can work for the workspace. The Git provider asks `git lfs dedup --test`, and the fallback service answers `false`.

### Changed

-   `StorageMaintenanceService` has a new `CanDeduplicate` field, so record literals have to set it.

## 0.4.0 - 2026-11-05

### Added

-   `GitExecution.setGitExecutableProvider` lets a host choose the git executable for every Git operation in the process. The selection has to be an absolute path, and `None` uses the git on the PATH. `GitExecution.resetGitExecutableProvider` goes back to automatic discovery.
-   `DependencyStatus.ProbeExecutablePath` names the git executable that `CheckDependencies` ran.
-   The lakeFS provider reports its HTTP requests on `OperationContext.ReportOutput`, with the credentials of the connection removed.
-   `CommandOutput.ObserveNotice` adds a `Notice` line to a command. `OutputReporter.knownSecrets` and `beginCommandWithSecrets` start a command that hides credentials the caller knows as values.
-   `ObjectMaterializationCodes` holds the failure and recovery codes of object materialization. The Git provider reports the same strings as before.
-   `ProgressPhaseCodes` holds the phase codes of steps that more than one provider reports. Both providers report `fetch`, `merge` and `clone`. Only lakeFS reports `download-objects`, `upload-objects` and `publish`, because Git reports those steps under its own codes.
-   The lakeFS provider reports an event when a step starts, with the codes of `ProgressPhaseCodes`. A step that does no work reports nothing.

### Changed

-   `DependencyStatus` has a new `ProbeExecutablePath` field, so record literals have to set it.
-   With an explicit selection, the Git provider's `CheckDependencies` checks that git and does not fall back to the one on the PATH. An unusable selection marks every component as not installed, and the `Remediation` names the path.
-   lakeFS requests that time out fail with the category `Timeout` and the code `timeout`, as in the Git provider. They used to fail as `Network` with `network_failure`.
-   The lakeFS provider removes the credentials of the connection from failure messages built from an error response.
-   `ObjectMaterializationService.Materialize` and `Dematerialize` take an array of paths, so one call covers a single file or a whole folder. A call with one path still fails with that path's own code and message.

### Removed

-   `ObjectBatchMaterializationService`, `WorkspaceSession.ObjectBatchMaterialization` and `ServiceAvailability.ObjectBatchMaterialization` are removed, because `ObjectMaterialization` takes several paths now. Record literals have to drop the field.
-   `TextDiffSupervisorOptions.GitExecutable` is removed, so record literals have to drop it. The text diff supervisor uses the `GitExecution` selection.

## 0.3.0 - 2026-11-01

### Added

-   `WorkspaceSession.ObjectBatchMaterialization` is a new optional `ObjectBatchMaterializationService`. Its `Materialize` and `Dematerialize` take an array of paths and download or free their large objects in one operation, for providers that do this faster than one call per path. Paths are exact keys, and a duplicate counts once. An empty array, or paths already in the target state, give `Succeeded` with a `NoOp` effect. When paths fail, the result is `PartiallySucceeded` if others changed and `Failed` if none did. The failure lists the failed paths in `AffectedPaths` and one `<path>: <message>` line per failed path in `Details` (for example a retained backup), and the outcome's `AffectedPaths` lists the changed paths. `ServiceAvailability.ObjectBatchMaterialization` reports whether the provider supplies the service, and `WorkspaceSession.withFallbackServices` fills a missing one with operations that succeed with a `NoOp` effect and a `service_unavailable` warning. The Git provider supplies it, with the failure codes `batch_materialization_incomplete` and `batch_dematerialization_incomplete` and the partial-success recoveries `retry_materialization` and `retry_dematerialization`. lakeFS does not, because it has no large-object representation.
-   `OperationContext.ReportOutput` is a new opt-in channel for the raw output of an operation's subprocesses, for a host that shows it in a folded "Show logs" section. `OperationContext.createWithOutput` builds a context with it, and contexts from `create` or `detached` have none. The callback receives batches of `OperationOutput` entries of the kinds `Command`, `StdOut`, `StdErr` and `Notice`. `CommandId` groups the entries of one subprocess. `IsPartial` marks a snapshot of a line that git rewrites with carriage returns, and the next entry with the same `CommandId` and `Kind` replaces it. The Git provider reports a header and the stderr lines of the git and git-lfs processes of its synchronization, clone, publish and large-object operations, and stdout only for the large-object download and upload calls. It redacts every line and replaces the call's known credentials. The channel limits line length, batch size, batch rate and queue size. The rate applies within each subprocess, and a subprocess that ends delivers its pending entries at once. Progress is unchanged and still carries no raw lines. The lakeFS provider reports no output.

### Changed

-   The Git provider's `Materialize` and `Dematerialize`, in both `ObjectMaterialization` and `ObjectBatchMaterialization`, fail with the code `local_changes` when a path has local changes such as an edited file, so a host can decide by the code instead of the message. A single-path failure used to have `lfs_operation_failed`. A batch returns `local_changes` when every failed path has local changes, as `Failed` if no path changed and as `PartiallySucceeded` otherwise. With any other failed path it keeps `batch_materialization_incomplete` or `batch_dematerialization_incomplete`. `AffectedPaths` and `Details` still list the failed paths, and the category and message are unchanged.
-   `OperationContext` has a new `ReportOutput` field, so code that builds the record as a literal has to set it to `None`. Code that uses `OperationContext.create` or `detached` needs no change. `NodeProcess.ProcessRequest` has a new `ReportStdoutOutput` field, which `ProcessRequest.create` sets to `false`.
-   `WorkspaceSession` and `ServiceAvailability` have a new `ObjectBatchMaterialization` field that code building these records has to set. `WorkspaceSession.createCoreOnly` sets it to `None`.
-   The Git provider reports live progress of its long transfers through `OperationContext.ReportProgress`. `git fetch` and `git clone` run with `--progress`, and the Git LFS transfers of an update, a synchronization, a clone and `Materialize` run with `GIT_LFS_FORCE_PROGRESS=1`. Their meters arrive as events with the phase codes `fetch`, `clone`, `clone-lfs-pull`, `lfs-pull`, `lfs-materialize-transfer` and `lfs-materialize-checkout`, with `Completed` and `Total` taken from the meter. `Synchronize` also reports events without numbers before its fetch (`fetch`), its merge (`merge`) and its large-file download (`lfs-pull`). `Clone` reports events with a `DisplayMessage` and no numbers before `git clone` (`clone`) and before its large-file download (`clone-lfs-pull`). A batch `Materialize` or `Dematerialize` of more than one path counts the finished files under `lfs-materialize-files` or `lfs-dematerialize-files`, with `Total` set to the number of paths. A single-path call reports no count events. Failure messages of these calls leave out the meter lines. `NodeProcess.ProcessRequest` has a new `ObserveStdoutProgress` field, which `ProcessRequest.create` sets to `false`.
-   The Git provider's `Materialize` and `Dematerialize` read the Git LFS listing once per call and check `git status` only for the requested path. A download used to list every Git LFS file of the repository four times and read the status of the whole working tree twice, which made single-file actions slow in large repositories. A file deleted while its download runs now fails with the local-changes message and stays deleted.

### Fixed

-   The Git provider gives a failure the category `Network` when its text says "actively refused" (a refused connection on Windows), "no such host" or "forcibly closed" (a reset connection on Windows), or has a Git LFS "batch response:" line ending in ": EOF" (a server that closes the connection). It gives the category `Timeout` for "did not properly respond" (a Windows connection attempt that gets no answer). These failures used to have the category `ProviderError`, so a caller could not tell an unreachable remote from a rejected request. The texts of other platforms, such as "connection refused" and "i/o timeout", already had these categories.
-   Canceling a large-object download during `Synchronize`, `Update` or `Clone` no longer leaves `.git/index.lock` behind. The Git provider ran `git lfs pull`, which a cancel could kill while it held the index lock. It now runs `git lfs fetch`, which a cancel stops, and then `git lfs checkout`, which runs to its end. A cancel during the checkout takes effect afterwards, and the call reports the work that finished. Every call that downloads large objects this way reports a step event with the code `lfs-pull-checkout` (`clone-lfs-pull-checkout` after a clone) before the checkout starts, and the checkout reports its progress under that code. The checkout also runs after a failed fetch, for example when the server lost one object, so the objects that arrived reach the working tree, and the result stays the fetch failure. When the checkout fails and an `index.lock` exists, or Git names the lock in its failure text, the partial result has the recovery `remove_index_lock` instead of `retry_materialization`. A failed fetch keeps `retry_materialization`, because the fetch takes no lock. The provider never deletes the lock. A batch `Materialize` or `Dematerialize` that hit an index lock on one path also reports `remove_index_lock`, whether or not other paths finished.
-   A large-object checkout that git-lfs ends with success although it could not update the index (because of a held `index.lock`) is now a failure with the lock recovery. Before, git-lfs wrote the files, printed "Error updating the Git index:" and exited with 0, so the download reported success, the lock stayed and the files read as modified. The Git provider now reads that text from `git lfs checkout`. In `Update`, `Synchronize` and `Clone` the result is `PartiallySucceeded` with the code `hydration_failed`, and the recovery is `remove_index_lock` when the lock file exists. A batch `Materialize` looks for the lock file after such a checkout. If it exists, the recovery is `remove_index_lock`. If not, a partial success keeps `retry_materialization`, and a run in which no path changed is `Failed` without a recovery action. The unmark of a path in `StoragePolicy.SetPathPolicy`, the local materialization of a picked conflict object and the checkout of restored large files in `RestorePaths` also count this checkout as failed. The unmark fails with `index_locked` when the lock file exists, and a restore reports `PartiallySucceeded` with the recovery `remove_index_lock`. The failures of `Materialize`, the unmark, the pick and the restore set `StateChanged` to `true`, because git-lfs wrote the files. Every `git lfs checkout` of the provider runs to its end, including those of the unmark, the pick and the restore, so a cancel cannot leave `.git/index.lock` behind.
-   The Git provider's `Materialize` and batch `Materialize` repair the index entries that a lock-blocked `git lfs checkout` leaves stale. After such a checkout the files hold their content, but the index keeps the stat data of the pointers, so `git status` lists them as modified. A download refused them with "has local changes", or found them materialized and left them modified. A path whose only change is that stale entry is now repaired without a transfer if the file is a regular file with the size and SHA-256 of its Git LFS object, the index entry equals the committed pointer, and the current attributes still route the path through the lfs filter (otherwise `git add` would stage the file as a plain blob). The provider runs `git add` on the repaired paths, which stages nothing because the clean filter reproduces the committed pointer for that content. The repair changes the index, so the batch `Materialize` lists a repaired path in `AffectedPaths` with the effect `Performed`, and the path's progress report says the file is materialized. A lock still held at that point is reported with the `remove_index_lock` recovery. A modified file with other content, or a file with a staged change, still fails with the local-changes message.
-   `Redaction.redact` removes the whole userinfo of a URL whose user name contains an `@`, such as `https://me@mail.com:secret@host/repo.git`. It used to stop at the first `@` and leave the rest of the userinfo in the text.
-   Git LFS transfers of a workspace whose remote URL has no `.git` suffix no longer fail on GitLab with "Unprocessable entity" (nfdi4plants/ARCitISA#1471). The provider passed the remote URL plus `/info/lfs` as `lfs.url`, which git-lfs uses literally. It now builds the endpoint the way git-lfs server discovery does: a remote that ends in `.git` gets `/info/lfs`, and any other gets `.git/info/lfs`. The check is case-sensitive like the one in git-lfs, so `proj.GIT` becomes `proj.GIT.git/info/lfs`.

## 0.2.2 - 2026-10-26

### Fixed

-   The Git provider no longer stops `git lfs ls-files -j` after 15 seconds without output. The command writes its JSON at the end, so large repositories failed with `lfs_listing_failed`. The listing has no time limit now in `ObjectMaterialization.ListObjects`, `Materialize` and `Dematerialize`, including the download that `Materialize` runs. Each of these takes an `OperationContext`, and canceling it ends the git-lfs process and returns a `Canceled` failure.

## 0.2.1 - 2026-10-09

### Fixed

-   With `versioncontrolservice.lfs.materializelargeobjects` on, the Git provider's `Update` and `Synchronize` (without `PublishLocalRevisions`) also run `git lfs pull` when there is nothing to merge, that is when the workspace is up to date or only ahead. Before, the large objects were only downloaded after a merge, so a workspace cloned or switched without them stayed with pointers. A failed download is a partial success with the `retry_materialization` recovery, as after a merge.

## 0.2.0 - 2026-10-08

### Added

-   `VersionControlService.TextDiff` is a new package with a streaming diff engine for large files. The Git provider and the umbrella package depend on it at the same version. The engine bounds the work per request and its read buffers. Its scanner checkpoints, one per 64 MiB of each side, stay in memory, so memory use grows slowly with file size. It targets .NET and Fable.
-   `AsyncTrampoline.switchToSetImmediate` makes Fable's async trampoline hop through `setImmediate` on the calling thread, which saves a timer tick of 11 to 15 ms per hop on Windows. Call it only on a thread that runs nothing but the engine, such as a diff worker thread. The Git worker calls it already. On .NET it does nothing.
-   The Git provider can run text diffs in a worker pool supervised by `TextDiffSupervisor`. Hosts create a `TextDiffPool` with `TextDiffPoolOptions.create` and `TextDiffPool.create`, and use `TextDiffTransport.WorkerThreadTransport.create` to start a worker thread from their own worker file.
-   `DiffSide` and `DiffContentBlocked` describe which side of a diff could not be read as text and the evidence found. `DiffContentBlocked.InvalidSequenceOffset` holds the byte offset of a sequence that is invalid in the encoding of that side, and is `None` for other evidence. `OperationFailure.DiffDetail` carries a `DiffContentBlocked` for a `diff_content_not_text` or `diff_encoding_mismatch` failure.
-   `TextDiffFailureCodes.EncodingMismatch` (`diff_encoding_mismatch`) reports a source that classification read as UTF-8 and that later contains a byte sequence valid in Windows-1252 only. `Open` returns `DiffBlocker.EncodingRequired` for that side when it reaches the sequence before the first page, and the caller can choose Windows-1252.
-   `TextDiffFailureCodes.ReadFailed` (`diff_read_failed`) names the code that any text diff call returns when a source cannot be read because of a file system or process error.
-   `TextDiffFailureCodes.UnsupportedEncoding` (`unsupported_encoding`) names the code that `Open` returns for an encoding name in `PreviousEncoding` or `CurrentEncoding` that the engine does not know.
-   `VersionControlService.Runtime.Node` adds the `PositionalFile` module, which reads and writes files at a byte position through descriptors, and the `WorkerThreads` module, which binds `node:worker_threads`. `Process` gains `processExistence`, `killProcessTreeAsync`, `runBounded`, `spawnToFileTracked` and `runBoundedWithLifecycle`. The last two call `onStarted` with the pid of the child and a promise for its exit.
-   `OpenDiffRequest.Storage` takes a `DiffStoragePolicy`. `PreferDisk of minimumFreeBytes * memoryBudgetBytes` keeps the session in temp files unless the free space of the temp drive is low, and `MemoryOnly of memoryBudgetBytes` always keeps it in memory within the budget. `OpenDiffResult.Opened` reports the choice as `storage: DiffStorage`, either `OnDisk` or `InMemory budget`. `DiffBlocker.BlobTooLargeForMemory` reports a committed Git blob that a memory session cannot read into memory.
-   `ReadPageRequest.Background` marks a read that the host makes ahead of the user. A memory session refuses a background read at 3/4 of its budget and keeps the last quarter for reads that a user waits for.
-   `TextDiffFailureCodes.TempSpaceLow` (`diff_temp_space_low`) and `TextDiffFailureCodes.MemoryBudgetReached` (`diff_memory_budget_reached`) name the failures of a request that would write when the temp drive is short of space or the memory budget is used up. Neither closes or poisons the session. Recorded answers and replays keep working, and a later request can succeed.
-   `MemoryStoreGroup` in `VersionControlService.TextDiff` creates temp stores that keep their bytes in 64 KiB chunks and share one byte counter. Its cap, at most 1 GiB, throws an `InvalidOperationException` when a write would pass it, so it detects bugs in the host's own check. `EngineHost.CheckWrite` runs right before a request does work that can write. It returns `None` to go ahead or `Some(code, message)` to refuse with that failure, and a refusal leaves the session usable. The engine never checks answers that come from the journal.
-   `PositionalFile.tryFreeBytes` in `VersionControlService.Runtime.Node` answers the bytes that a non-privileged user can write on the volume of a folder (`bavail * bsize` of `fs.statfs`). It answers `None` when the call fails or the volume reports no blocks.

### Changed

-   Version 0.2.0 replaces the whole-file contract of `TextDiffService`, which existed before, with resumable paged operations. Its operations are `Open`, `ReadPage`, `ReplayPage`, `Expand`, `ReadLine`, `GetSourceInfo` and `Close`. `Open`, `ReadPage`, `Expand` and `ReadLine` return resumable work while a scan continues. `DiffPart` represents hunk fragments or hidden equal gaps. Each `LineSlice` can carry `Highlight` spans for changed or unchanged text. Code that builds or calls `TextDiffService` has to change.
-   `Expand` returns an `ExpandedContext` part and, when lines of the gap remain hidden, a `HiddenEqual` part with a new gap id for the rest of the gap. `ReplayPage` returns a page that was already read, by its `PageId`. The replayed page is identical to the first one except that `Pending` is `None`, so a caller can drop pages it no longer shows and read them again later.
-   `HunkBody.UnalignedSides` marks lines whose alignment could not be established. A gap can exceed the Myers step budget inside a window and then shows as unaligned lines. In a window that cannot grow, the engine pairs the gap line by line if both sides of the gap have the same line count and at most one line in eight differs at the same offset. Repetitive files with many distinct edits then show replaced rows instead of an unaligned block. In repetitive text, a block inserted or deleted inside such a gap can show as replaced rows, with the matching repeated lines added or removed further on. A forward search can reach the default limit of 1,000,000 lines or 256 MiB per side, configured by `ResyncScanLines` and `ResyncScanBytes`. The search continues after it reaches a limit, from the same line offset between the sides, so after an insertion or deletion larger than the limit the rest of the file shows as unaligned regions, like a rewrite, with every line present. A full rewrite also produces unaligned regions.
-   The host writes its own worker file that calls `TextDiffWorker.bootstrap`, passes a function that returns its pool through `GitSessionOptions.TextDiff`, calls `Prewarm` when it starts and disposes the pool when it shuts down. The Git session calls `GitTextDiffOptions.Pool` on each diff `Open` until it returns a pool, so an `Open` that finds no pool fails with `diff_worker_failed` and the next `Open` asks again. Closing a Git session closes the diff sessions of its workspace in the pool. The package ships no worker script, and an Electron app keeps that file outside the asar archive.
-   The Git factories and sessions created without options no longer supply a text diff service, so `session.TextDiff` is `None` for `createFactory`, the credential factories and the `createSession*` functions that take no `GitSessionOptions`. Only `createFactoryWithOptions` and `createSessionWithOptions` with `GitTextDiffOptions` in `GitSessionOptions.TextDiff` supply a working one. Options without `TextDiff` supply a service whose `Open` fails with `diff_worker_failed`.
-   `TextDiffPoolOptions.create` makes options for a pool with three workers. Each worker runs one session, so no two diffs share a worker's scratch memory. A fourth `Open` closes the least recently used idle session, or waits when every session is busy.
-   `Open` pins source identities and checks binary content, including HDF5 signatures. A recognized BOM selects the source encoding. `DiffBlocker.EncodingRequired` with a `PreparationToken` and `EncodingCandidate` values asks for a choice when a source that classification read as UTF-8 holds a sequence that only Windows-1252 decodes, and `Open` reaches it before the first page. A source that is not text fails with `TextDiffFailureCodes.ContentNotText` and structured `OperationFailure.DiffDetail`. The evidence is a binary signature, a NUL byte, a control-character ratio above 1 percent in a 64 KiB window, or an invalid byte sequence. The scan judges every window. Classification reads samples and judges a window only when they cover at least 4 KiB of it or the window is shorter than that. A BMP signature needs zero reserved bytes and a known DIB header size, so a text file that starts with `BM` is text. Bytes that are invalid as UTF-8 open as Windows-1252 without asking.
-   The supervisor creates its scratch folders with mode 0700 and its files with mode 0600, and it refuses an existing `text-diff` folder in the temp root that belongs to another user. Its Git children run with `LC_ALL=C` and `LANG=C`, so their messages are not localized.
-   A working-tree path that runs through a symlinked folder leaving the workspace, or through a `.git` folder, fails with `diff_read_failed`.
-   `Open` answers `diff_session_closed` for a continuation whose preparation session the worker has closed or evicted. The caller opens the diff again.
-   Pages contain at most 1,000 rows and 32 fragments. A page response, including the first page from `Open`, has a limit of 512 KiB, and a `ReadLine` response has a limit of 64 KiB. The service clamps `ReadLineRequest.MaxUtf16` to 8,192 UTF-16 code units and `ExpandRequest.Count` to 100 lines.
-   The Git worker pool answers cancellation immediately. The worker closes an idle diff session after 15 minutes, and the pool closes an idle session earlier when an `Open` needs its slot. Both cases fail later requests with `diff_session_closed`, and the caller opens a new diff to continue.
-   `OperationFailure` has a new `DiffDetail` field, so code that builds the record has to set it. `OperationFailure.create` sets it to `None`.
-   The text diff fallback of `WorkspaceSession.withFallbackServices` used to return `UnsupportedContent` from its three reads. It now returns `NotDiffable ProviderUnsupported` from `Open`. `ReadPage`, `ReplayPage`, `Expand`, `ReadLine` and `GetSourceInfo` fail with `service_unavailable`, and `Close` succeeds with that warning.
-   The Git provider requires Git 2.42 or newer, and `CheckDependencies` reports older Git as incompatible. Git LFS 3.7 reads the index in `git lfs checkout` only with Git 2.42 or newer. With Git 2.38 to 2.41 it reads the HEAD tree, so a conflict pick of an LFS candidate whose object is in the local cache left a pointer and the warning `object_not_materialized`.
-   A session keeps at most eight suspended `ReadLine` reads. A continuation for a read that the session dropped answers `continuation_mismatch`, and the caller starts the read again without a continuation.
-   The Git worker chooses the storage of a session at its first `Open`. `MemoryOnly` gives memory. `PreferDisk` gives memory when the known free space of the worker's temp folder is below the minimum plus one page envelope (512 KiB) plus 64 KiB, or below that sum plus the size of the committed blobs. Unknown free space gives disk. A continuation keeps the choice of the first `Open`, so a changed `Storage` in it does not cause `continuation_mismatch`.
-   A memory session reads a committed Git blob whole into memory, at most `min(16 MiB, budget / 2, budget - 576 KiB)`. `Open` returns `BlobTooLargeForMemory` for a larger blob. The worker reads Git LFS objects and the working copy in place.
-   The worker checks the session before a request that can write. A memory session fails with `diff_memory_budget_reached` when its stored bytes, its memory blobs, the length of its journal index and room for one more page (512 KiB plus 32 KiB) pass the budget, or 3/4 of it for a background read. A disk session fails with `diff_temp_space_low` when the free space of the temp drive, less the index length and the room for one more page, is below the minimum. The check also subtracts the bytes that a growing blob spool has yet to receive. Unknown free space never refuses.

### Removed

-   `TextDiffService.GetDiff`, `TextDiffService.GetWordDiff`, `TextDiffService.GetBaseContent` and the `ContentView` type with its `TextContent` and `UnsupportedContent` cases no longer exist. They belonged to the whole-file contract. Consumers use `TextDiffService` pages, replay, gap expansion and line slices instead.
-   `VersionControlService.Runtime.Node` no longer exports `Process.isProcessAlive`. `Process.processExistence` replaces it and tells a process that is gone apart from one the caller cannot signal.

### Fixed

-   `Publish` and `Synchronize` no longer fail on Git 2.38 to 2.41 when the remote already has branches. The Git LFS upload planning wrote a `--not` line to `git rev-list --stdin`, and Git accepts option lines on standard input only from 2.42 on, so every such publish failed with `fatal: options not supported in --stdin mode`. The planning now excludes each remote tip with a `^<oid>` line. A rev-list that still rejects its input with that message makes the planning fall back to its slower path.
-   When `git --version` or `git lfs version` cannot start or exits with an error, the `CheckDependencies` remediation for `git` or `git-lfs` starts with the start error, or with the exit code and stderr. It used to give the install advice alone, so a Git that could not start looked like a missing one.

## 0.1.2 - 2026-09-28

### Fixed

-   The Git provider passes repository paths to git as literal pathspecs. Git used to read `[`, `]`, `*` and `?` in a file name as a pattern, so `Dematerialize` or `RestorePaths` on `runs/sample[1].csv` also reset uncommitted edits of `runs/sample1.csv`.
-   When a file changes while `Dematerialize` frees it, the Git provider keeps the new content and the backup and fails with a message that names the backup path. The rollback used to delete whatever was at the path and put the older backup back. The failure has code `lfs_backup_retained` and `StateChanged = true`.
-   `Materialize` downloads the file when `GIT_LFS_SKIP_SMUDGE=1` is set in the environment. The download of a single object used to get the pointer back and fail.
-   After `Dematerialize`, the Git provider keeps the cached object when only an unpushed merge commit adds it, also when `log.diffMerges` is set in the git config. The check used to skip merge commits.

## 0.1.1 - 2026-09-28

### Changed

-   `Dematerialize` in the Git provider also removes the object from the local Git LFS cache, so freeing a file frees its disk space. It first checks that the remote has that exact object, and it keeps the object when the repository uses a custom `lfs.storage` or has unpushed commits that add the object. `Materialize` downloads it again.
-   For an HTTP(S) Git LFS server, `Dematerialize` checks the object with a dry run and downloads nothing. Any other endpoint, or a repository with a standalone transfer agent, still copies the object once, because only a real fetch checks it there.
-   `Materialize` and `Dematerialize` ignore `lfs.fetchexclude` for the object they fetch or check.
-   `GetStatus` runs `git status` once. It used to run it a second time for the workspace version. The Git provider now parses `git status --porcelain=v2` output itself and derives the status and the workspace version from the same output.
-   When the remote lacks a file's object, `Dematerialize` says "The remote does not have '<path>' yet. Push it before freeing its local copy." and `Materialize` says the remote does not have the file's content, instead of showing git-lfs output. A server's per-object 404 answer is recognized as a missing object too.
-   `Dematerialize` keeps the file with "Could not check whether the remote has '<path>', so its local copy stays." when the remote check does not list the exact object.
-   `Materialize` fails with "Git LFS did not download '<path>'." when Git LFS still lists the file as not downloaded after the transfer.
-   `Materialize` downloads a file's object with one `git lfs fetch` for that path and then checks the file out. When another file's name differs from the requested one only at special characters, it downloads only the requested object with `git lfs smudge`, so the download adds no object for the other file. The checkout afterwards still runs `git lfs checkout` with the path pattern, so it also replaces the other file's pointer when that file's object is already in the local cache.

### Fixed

-   The Node runtime and lakeFS provider import Node modules as ES modules, so compiled output runs with plain `node` without a CommonJS bundle.
-   `GetStatus`, the word diff and the checks before `Materialize`, `Dematerialize`, `RestorePaths`, `Prune` and `Deduplicate` no longer stop after 30 or 120 seconds without output. Git prints nothing while it re-hashes a changed Git LFS file, so these failed for large files.
-   `Dematerialize` checks a file's hash without a time limit and reports the real reason when the check fails. A slow check of a large file used to fail as a mismatch.
-   `Materialize` runs `git lfs checkout` to completion. For a large file it used to hit a time limit and report a failure, while on Windows git-lfs kept running and held `.git/index.lock`.
-   `Materialize`, `Dematerialize` and picking a large-object conflict candidate work for file names with brackets, commas or other glob characters. git-lfs read such a name as a pattern that matched no file.
-   `Materialize`, `Dematerialize` and `StoragePolicy.SetSettings` report success when they finished, also when a cancel request arrived during their last step.
-   A `GetStatus` canceled during its repository check returns a canceled result.
-   `Materialize`, `Dematerialize` and cleaning the Git LFS cache pass the account credential to git-lfs, so they work on private repositories. The server used to answer 401.
-   `Dematerialize` keeps the file in place while it checks the file's hash, and then moves it into a backup inside the Git directory (`.git/vcs-lfs-backup/`). When the Git directory is on another volume, the backup is a file next to the original, named `<file>.vcs-lfs-backup-<32 hex digits>`. If the file's size or modification time changes between the check and the move, `Dematerialize` restores it and fails. The file used to leave the working tree for the whole check, and git status listed the backup as a new file.
-   `Materialize` and `Dematerialize` wait for other workspace changes, and other workspace changes wait for them. A save during a free could commit the file while it was missing from the working tree.
-   The credential Git LFS URL replaces a user name or password in the remote URL. A remote URL with its own user name used to produce a URL with two of them, and authentication failed.

## 0.1.0 - 2026-09-24

### Added

-   Added provider-neutral abstractions for workspace sessions and core version control. Optional services can be absent, with fallback wrappers for consumers that need a complete session.
-   Added a Git provider that runs Git and Git LFS through the git CLI.
-   Added a lakeFS provider.
-   Added the Node runtime and dependency-only umbrella package for coordinated provider installation.
-   Added Fable and .NET targets.
