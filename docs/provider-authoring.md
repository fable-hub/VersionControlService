# Provider authoring

An external provider needs one project reference or package reference: `VersionControlService.Abstractions`. It should not depend on the Node runtime, Git, lakeFS, or the umbrella unless it uses those implementations directly. The sample in [`samples/ExternalProvider`](../samples/ExternalProvider/Provider.fs) is a compiling read-only provider built from a function list.

Hosts that spawn git beside the library can use `GitExecution.resolvedEnvironment` for a complete child-process environment or `GitExecution.environmentOverrides` for request records. Both use the library's Git tool-path resolution.

## Provider lifecycle

`IVersionControl` is the provider entry point. A host holds one provider per `ProviderId`. An author writes the list of functions, a `ProviderFunctions` record, and builds the provider from it with `ListedProvider(probe, close, functions)`. `probe` is the `Probe` function and `close` is the `Close` function. Every provider has both, so the list holds an `Entry` for every other function.

- `Probe` is read-only detection. It must not throw or claim a root it does not own.
- `VerifyLocation` checks explicit access intents against a repository location.
- `Initialize` creates provider state in place and preserves existing user files. It returns the opened `Repository`, as do `Clone`, `Bind` and `Open`.
- `Clone` requires a missing or empty destination.
- A provider returns an `Unsupported` failure for a ref it cannot check out. lakeFS returns a
  `Validation` failure (`target_ref_mismatch`) for a ref that contradicts the location. It never
  ignores the ref. Use `target_ref_unsupported` for the unsupported case.
- A clone canceled before it completes rolls back and returns `Canceled`, and a pre-existing empty target stays empty. A clone that finished before the cancellation took effect succeeds. A download failure that is not a cancellation returns `PartiallySucceeded` with `retry_materialization`.
- A clone whose rollback cannot remove the target returns `Failed` with `StateChanged = true` and `remove_clone_target` (category `Canceled` when the user canceled).
- `Bind` attaches or retargets an existing local workspace without cloning.
- `Open` takes an `OpenRequest`, the folder and an optional connection profile. It finds the repository root from the folder and returns one opened `Repository`.
- `CheckDependencies` reports installed, missing, and incompatible components.
- `InstallDependency` performs only remediation the provider genuinely supports. Unsupported installation is a structured failure.
- A function the provider does not have is `Missing` with a reason of one plain sentence. A call of it through the interface answers `NoOp` with that reason and runs nothing.

A provider receives credentials and provider settings through the options record of its create function, as `GitProvider.create` takes a `GitProviderOptions` and `LakeFsProvider.create` a `LakeFsProviderOptions`. Do not use global mutable registries. A provider can open multiple independent repositories, including repositories with different connection profiles.

The Git provider also accepts a `GitIdentityStrategy` extension point (`GitProviderOptions.RevisionIdentity`) for host-supplied revision attribution. Its `ResolveIdentity` function receives a `RevisionIdentityRequest` and is called for each operation that may create a revision. The result is not cached when a repository opens. The request carries the repository root and the connection profile of the opened repository. Its `TargetHost` is the host of the effective push URL of the current branch's publish remote (pushurl over url, with `insteadOf` rewriting applied), or of the bound location when no remote is configured or HEAD is detached. It is `None` when the upstream configuration is invalid or when no host can be read from the value, which includes local paths and file URLs. The provider lowercases hosts and drops IPv6 brackets. It passes percent-encoded and internationalized names through as written. Credentials follow the URL git connects to for each command. Fetch, ls-remote, LFS downloads and the first fetch after Initialize use the remote's effective fetch URL. The push and LFS uploads use its effective push URL. Clone and location verification use the `insteadOf`-expanded location. The push credential and the identity therefore resolve against the same host, so an application that keeps accounts per host and connection profile can return the identity that matches where the revision will be published. Credential and identity strategies must return `None` when they have nothing to offer. An exception thrown by a strategy escapes the operation that called it. Returning `Some` supplies the author and committer name and email for that operation. Returning `None` uses the repository's configured `user.name` and `user.email`. If neither a strategy identity nor a complete repository identity is available, the operation fails before mutation with the stable validation code `identity_missing` and a recovery action describing how to configure Git or supply the strategy.

A canceled Git `Update` reports what a killed `git merge` left behind and mutates nothing on its own except one case: when the merge wrote MERGE_HEAD, the provider runs `git merge --abort`, which is git's own rollback. The recovery runs the abort only when MERGE_HEAD still names the pinned target. A different merge state returns `inspect_workspace` and remains for review. Everything else comes back as a `Canceled` failure with a `RecoveryAction`. `remove_index_lock` means an `index.lock` is present (with `StateChanged` false when it already existed before the update). `abort_merge` means `git merge --abort` failed. `refresh_workspace` means the merge finished before the cancellation landed. `restore_workspace` means a fast-forward may have rewritten the paths in `AffectedPaths`, which the application reviews and restores or keeps. `inspect_workspace` means the workspace differs from its pre-merge state in a way the provider could not attribute, or git could not report it. On Windows a killed git process does not remove its lock, so `remove_index_lock` is the usual outcome of a mid-merge cancellation and the application needs a path for it.

Git reports a held index lock as `index_locked` (`Concurrency`) with `remove_index_lock` and the lock's age in `Details`, and it never deletes the lock itself.

## Revision path policy

`RevisionPolicyStrategy` is immutable, provider-neutral configuration that the host hands to the provider at creation. The default is `RevisionPolicyStrategy.automatic`, which preserves the provider's automatic behavior. `ResolvePathPolicy` must be pure, deterministic, fast, and free of side effects. It must not access the file system or the network.

The function receives one `RevisionPathPolicyRequest` for each selected regular file. `Path` is the selected `RepositoryPath`. `SizeInBytes` is the size of the content that would be committed. For a recognized large-object reference, it is the declared payload size, not the length of the reference. In Git, this means the payload size declared by a Git LFS pointer. Deletions and symbolic links do not receive a request.

Git implements the strategy when `CreateRevision` creates a revision from selected paths. The policies have these effects:

| Policy | Git behavior |
|---|---|
| `Automatic` | Git uses its configured automatic LFS threshold and effective filter attributes. An oversized selected regular file becomes a pointer when its effective filter is not `unset`. Git adds a literal tracking rule when the effective filter is not `lfs`. |
| `Inline` | Git commits a plain blob at any size. The policy takes precedence over the threshold and an effective `filter=lfs` rule. Git adds a literal untracking rule when the effective filter is `lfs`. A working-tree LFS pointer is rejected until its content is materialized. |
| `LargeObject` | Git stores the selected regular file as an LFS pointer even below the threshold. The policy takes precedence over the threshold. Git adds a literal tracking rule when the effective filter is not `lfs`. Existing recognized pointer content stays as the committed pointer. |

Git owns the generated `.gitattributes` content and its literal tracking and untracking rules. When generated rules require a metadata change, a dirty unrelated `.gitattributes` file prevents that change and returns `precondition_failed` with `.gitattributes` in `AffectedPaths`. Hosts provide the strategy and do not implement Git attribute logic.

An `Inline` request whose working-tree content is a recognized LFS pointer fails before mutation with category `Validation` and code `inline_content_not_materialized`. The failure has `StateChanged` set to `false`, `Retryable` set to `false`, the affected paths, and a recovery action with code `retry_materialization`. Its instructions are `Materialize the affected files, refresh the status and create the revision again.`

If `ResolvePathPolicy` throws, Git fails before mutation with category `ProviderError` and code `revision_policy_failed`. The message is redacted, the affected path is reported, and Git does not fall back to `Automatic`. A change to selected content during revision planning returns `selected_content_changed`.

lakeFS has no large-object representation and takes no revision policy. Its revision behavior remains unchanged. The policy applies only to `CreateRevision`. Clone, update, merge, and conflict finalization are outside its scope.

The `RevisionPolicy` field of `GitProviderOptions` takes the strategy. `GitProviderOptions.defaults` uses `RevisionPolicyStrategy.automatic`.

## Function list

`ProviderFunctions` has one entry for each function of the interface except `Probe` and `Close`. An entry is `Implemented` with the function or `Missing` with a reason of one plain sentence for the user. The function in an entry has the signature of the interface member and returns `Async<OperationResult<'T>>`. A function that works on a repository takes the `Repository` as its first argument. `ListedProvider` wraps each result in `Executed` and answers `NoOp` with the reason for a `Missing` entry. A provider writes every entry, so the compiler reports an entry that a newer version of the interface adds.

The entries `GetStatus`, `ListRefs`, `CreateRef`, `PreflightSwitchRef`, `SwitchRef`, `CreateRevision`, `RestorePaths` and `GetDiffSummary` cover status, refs, switching, selected-path revisions, restoration, and diff summaries. The other functions sit in eight groups. Each group is a record of entries in `ProviderFunctions` and a member of the interface with the same name:

| Group | Purpose |
|---|---|
| `Synchronization` | refresh, preview update, update, publish, and synchronize |
| `TextDiff` | resumable pages for a working file against its version in the last revision, or for a revision against its first parent |
| `ConflictResolution` | provider-owned conflict sessions and stale-handle checks |
| `ObjectMaterialization` | list large or lazy objects, and hydrate or dematerialize many of them in one call |
| `StoragePolicy` | literal-path policy and host-facing settings |
| `Maintenance` | prune and deduplicate local storage, and report whether deduplication can work |
| `RepositoryBrowser` | a credential-free repository URL |
| `History` | the revisions that lead to the current one, newest first, and the objects a revision changed against its first parent |

A provider marks a function it does not have as `Missing`. It does not write an entry whose function always returns `Unsupported`. A refusal for one input stays a `Failed` result inside `Implemented`.

Each provider has a private subclass of `Repository` that holds the state of one open. `Open`, `Clone`, `Initialize` and `Bind` create one object of it for each call, so two opens of one folder are two objects with their own state. Every function that works on a repository starts by testing the repository against that subclass. A repository that another provider opened gets `Repository.ofAnotherProvider`, which is a `Validation` failure with the code `repository_of_another_provider`. Nothing routes the repository to another provider. `close` releases what the repository holds. A repository of another kind makes it raise `invalidArg`, because its result has no failure to carry.

```fsharp
open VersionControlService.Abstractions

type private SampleRepository(descriptor: RepositoryDescriptor) =
    inherit Repository(descriptor)

let private ownRepository (repository: Repository) (result: OperationResult<'T>) : Async<OperationResult<'T>> =
    async {
        match repository with
        | :? SampleRepository -> return result
        | _ -> return OperationResult.failed (Repository.ofAnotherProvider repository)
    }

let private openRepository (request: OpenRequest) (_context: OperationContext) =
    async {
        let descriptor: RepositoryDescriptor = {
            ProviderId = providerId
            RepositoryRoot = request.RepositoryRoot
            Location = None
        }

        return OperationResult.succeeded (SampleRepository descriptor :> Repository)
    }

let private getDiffSummary (repository: Repository) (_context: OperationContext) =
    ownRepository repository (OperationResult.succeeded { Entries = [||] })

// functions is the ProviderFunctions record with an entry for every function:
// Open = Implemented openRepository, GetDiffSummary = Implemented getDiffSummary,
// and Missing "<reason>" for the rest.
let provider: IVersionControl =
    ListedProvider((fun _ -> async { return NotDetected }), (fun _ -> async.Return()), functions)
```

[`samples/ExternalProvider/Provider.fs`](../samples/ExternalProvider/Provider.fs) holds the complete record.

A text diff request names what it compares in `OpenDiffRequest.Comparison`. `DiffComparison.WorkingFile` compares the working file with its version in the last revision. `DiffComparison.Revision` compares the version in a revision with the version in its first parent and never reads the working tree. The Git provider accepts only a full object id as the revision.

A `TextDiff` provider returns `Resumable` while it scans and binds each continuation to the
same request fields. It journals produced pages so `ReplayPage` can return a page after its
in-memory representation is evicted. `Open` pins both source identities for the handle
lifetime. The provider binds each handle and continuation to the owner that opened it, then
checks that owner before serving a request. A provider reports `source_changed` when either
pinned source changes.

Everything that needs no opened repository (`Probe`, `VerifyLocation`, `Initialize`, `Clone`, `Open`, `Bind`, `CheckDependencies`, `InstallDependency`), repository locations and credentials stay provider-specific at the composition root.

## State outside the workspace

A provider that needs mutable indexes, transactions, or recovery data keeps them outside the managed workspace. The lakeFS provider takes a host-selected `StateRoot` and keeps one entry for each workspace folder beneath it, outside every workspace. `Open` finds the entry of a folder again by the folder, so the host stores nothing. State inside a workspace, missing state and corrupt state fail with a code (`provider_state_inside_workspace`, `provider_state_missing` and `provider_state_corrupt`). An entry whose first open did not finish fails with `first_open_incomplete`, and a ready entry that lost its index fails with `provider_state_index_missing`. The provider does not recreate state silently.

Keeping this boundary matters for two reasons: provider metadata cannot collide with repository objects, and a failed materialization can recover without treating transaction files as user content.

## Conflict sessions

The conflict functions keep handle validation and each mutation under the lock of the repository. Read operations leave the lock available for status and session inspection. A successful resolution writes the worktree content and stages it before the handle rotates. The provider reads the refreshed summary after rotation. A failure after the worktree write reports `StateChanged = true` with `refresh_conflict_session`. A write failure reports `file_write_failed` with `StateChanged = true` and `refresh_conflict_session` because the write can truncate the file before it fails.

Candidates carry `Object` when their content is a separately stored object. Git fills `Object` for LFS pointers with their size and object id. It also reports whether each object is in local LFS storage. Git marks those items pick-only with `SupportsResolvedContent = false`. The pointer text is available for comparison and cannot be supplied back as edited content. A pick stages the exact candidate blob and materializes a stored object from local storage when available. Git does not download during that pick. lakeFS returns `Object = None` and resolves binary items by picking as before.

For both providers, a candidate with `Preview = None` and `Object = None` represents a side that deleted the file. Picking it resolves the conflict to that deletion. lakeFS records a picked missing side as a resolved deletion, which `Finalize` applies.

Codes the conflict functions report, shared by the providers unless a provider is named:

| Code | Category | Recovery | Meaning |
| --- | --- | --- | --- |
| `conflict_item_not_found` | `NotFound` | None | The path has no unresolved item (Git also reports it when no session is open). |
| `unknown_candidate` | `Validation` | None | The candidate id is not one the item advertises. |
| `manual_resolution_required` | `Unsupported` | None | The item holds binary or non-text content, and the resolution supplies text. Pick an original candidate instead. |
| `candidate_content_unavailable` | `Validation` | None | The item advertises the candidate, but its content cannot be read for the path (Git). |
| `workspace_file_missing` | `NotFound` | None | The workspace candidate was picked but its file is gone (lakeFS). |
| `file_write_failed` | `ProviderError` | `refresh_conflict_session` | The resolved content could not be written. The file may be truncated (Git). |
| `symlink_not_supported` | `Validation` | None | A candidate or workspace file is not a regular file, or the path changed while it was being read (lakeFS). |
| `conflicts_unresolved` | `Validation` | None | Finalize was called while items remain unresolved. The affected paths name them. |
| `detached_head` | `Validation` | None | Finalize needs a current branch (Git). |
| `object_not_materialized` | (warning) | None | A successful Git pick left the file as a pointer because its object is not in local storage. |
| `object_materialization_failed` | `ProviderError` | `retry_materialization` | Git staged the pick, then failed to materialize its object. The operation returns partial success. |
| `file_delete_failed` | `ProviderError` | None | Git could not delete the worktree file. Nothing changed. |
| `deletion_blocked_by_directory` | `Validation` | None | Git found a directory at the path it needed to delete. |
| `restore_unmerged_paths` | `Validation` | None | Git RestorePaths was asked to discard a conflicted path. |

Finalize checks whether HEAD already has the recorded merge target as a parent and whether the index tree equals the tree of HEAD. When both hold, it cleans up the Git merge state and closes the session without a second commit. A re-resolution after the commit changes the index, so Finalize commits it. A ref-update failure is inspected on the branch ref before the provider reports it. A cleanup failure keeps the session open and returns `inspect_workspace` with the state-file path.

Cancel inspects `MERGE_HEAD` after a failed abort. A missing file means the abort completed and the session closes. A remaining file returns `abort_merge` with instructions to run `git merge --abort` and refresh.

## Results and concurrency

Return `Async<OperationResult<'T>>` from every function of the list:

- `Succeeded` includes the value, whether work was performed, affected paths, resulting revision and repository version, warnings, and publication state.
- `PartiallySucceeded` means state changed before a later step failed. It must include a recovery action.
- `Failed` carries a stable category and provider-extensible code. Set `StateChanged` when mutation may have happened, even if verification was inconclusive.

Repository versions are opaque optimistic-concurrency tokens. Conflict handles have a separate lifetime and rotate after each successful nonterminal resolution. Validate both tokens where the request carries both. A stale request must fail before further mutation.

## Synchronize

`Synchronization.compose` implements the shared synchronize operation. It refreshes the target and pins the observed state. When the workspace is behind, it previews the pinned target before applying an update. It can then publish local revisions.

Providers call `Synchronization.compose` inside their mutation lock. The provider steps consume the refreshed state, so the update applies the revision that the preview described. The provider validates the repository version once in its mutation wrapper.

The consumer can accept an update that opens a conflict session. It has no acceptance choice for local changes on affected paths. The user must save or discard those changes first. When the provider cannot compute the preview, the consumer retries the synchronization later.

The synchronize result uses these decision codes and evidence:

| Code | Category | Recovery | Paths and evidence |
| --- | --- | --- | --- |
| `update_would_overwrite_local_changes` | `Conflict` | `resolve_local_changes` | Overlapping paths and `observed_target` |
| `update_would_create_conflict_session` | `Conflict` | `accept_update_risks` | Overlapping paths when any exist and `observed_target` |
| `preview_indeterminate` | Provider-defined | Provider-defined | `observed_target` is added by the composition |
| `acceptance_target_required` | `Validation` | None | The consumer must supply `ExpectedTargetRevision` |
| `conflict_session_active` | `Conflict` | None | Resolve or cancel the active session |
| `operation_in_progress` | `Conflict` | None | Finish or abort the Git operation in progress |
| `publish_rejected` | `ProviderError` | None | The remote refused the push. The message carries the reason the remote printed. |
| `precondition_failed` | `Concurrency` | `refresh_workspace` or None | Git carries `refresh_workspace` in three publish cases. A stale expected target has `expected_target` and `observed_target`. A remote commit missing from the local store has `observed_target`. An adopted untracked remote branch has `observed_target` and `StateChanged = true`. A push the remote rejects as out of date, and a push that raced another, carry no recovery. |
| `upstream_config_failed` | `ProviderError` | `retry_publish` | Git reports `StateChanged = true` when the tracking config could not be written after publication. |
| `publish_target_untracked` | `Validation` | None | Git reports that the remote branch exists, but the fetch refspec does not map it. |

The `operation_in_progress` and `publish_rejected` codes pass through from the provider. Another provider may classify them differently.

For `update_would_overwrite_local_changes`, `AcceptUpdateRisks` does not bypass the failure. The consumer retries with `AcceptUpdateRisks` and the observed target revision only for `update_would_create_conflict_session`. The composition checks that revision against the fresh target before it mutates anything.

A publish failure after an applied update becomes `PartiallySucceeded` when the failure reports no state change. The composition keeps a provider recovery action when one exists and supplies `retry_publish` otherwise. `Publication` describes the synchronization target: `LocalOnly` when the updated workspace is ahead of or diverged from it, `PublicationNotApplicable` after a fast-forward. The retry stays in both cases, because the publication target can differ from the synchronization target and still lack revisions. A publish failure that reports `StateChanged = true` after an applied update keeps its failure classification and carries `retry_publish` when it has no recovery action of its own.

When an update applied successfully but its post-merge inspection failed, the provider returns the `refresh_workspace` recovery. The provider keeps a failure's own recovery action when it has one. A deadline trip reports `inspection_timeout` when the merge applied cleanly. When the merge outcome is already certain, a conflict session or a rejection, the failure keeps `conflicts_detected` or `update_rejected` and carries the deadline as a `state_inspection_failed` warning or in its details.

Cancellation that reaches the runner after the merge finishes has two truthful outcomes. When the runner observed the cancellation, it returns `Canceled` with `operation_canceled`. The failure has `StateChanged = true` and a `refresh_workspace` recovery. When the runner did not observe the cancellation, it returns `Succeeded`. The workspace state is correct in both cases.

## Cancellation, progress, and redaction

Use the supplied `OperationContext` throughout the operation. Check cancellation between bounded units of work and pass it into process, network, and streaming helpers. Report progress with stable phase codes. Byte totals use `float` so values above two GiB remain exact across .NET and JavaScript.

Use the codes of `ProgressPhaseCodes` for the steps that other providers report too. A Refresh reports `ProgressPhaseCodes.Refresh` (`fetch`) when it starts. An Update reports `ProgressPhaseCodes.Update` (`merge`) before its merge, and a Clone reports `ProgressPhaseCodes.Clone` (`clone`) when it starts. Announce the start with an event that has no numbers (`Item`, `Completed` and `Total` are `None`) and a `DisplayMessage` that names the step, so a host can show the step before the first meter of a transfer arrives. The module also has optional codes for the transfer of objects (`DownloadObjects` and `UploadObjects`) and for publishing (`Publish`). Both shipped providers report the three codes above. The lakeFS provider reports the optional codes today. The Git provider runs the same steps and reports them under its own codes. The [consuming guide](consuming.md#progress-of-git-transfers) lists the codes of its downloads. Report an optional code only when your engine really runs that step. Announce a batch of objects once before its first object transfers, and report nothing for a batch that transfers nothing. Never invent an event for a step the engine lacks. A host can read a missing event of a common code as a step that did not run. It cannot read a missing optional code that way, because an engine may report that step under a code of its own. Keep the codes for the details of your engine, such as the phases of a transfer, under names of your own.

The start of a step names an activity, and a meter measures a transfer. Meters keep the codes of their provider, for example `lakefs-download` and `lakefs-upload` for lakeFS, and their messages describe the measurement. `Synchronization.compose` reports no events of its own. Each provider reports its steps from its own Refresh, Update and Publish, and a Synchronize carries the events of the steps it runs.

Messages, details, progress text, and repository URLs must not contain secrets. Resolve credentials from the `ConnectionProfileId`, scope them to the operation, and pass provider output through the redaction guard before returning it.

## Storage settings reseed

A host that already stores a large-object threshold and materialization preference can hand them to a provider once through `StoragePolicy.SetSettings`. Read the host-owned values, write both in one settings record, and mark the host migration complete only after success:

```fsharp
let reseedStorageSettings
    (provider: IVersionControl)
    (repository: Repository)
    (savedThresholdMb: int option)
    (savedMaterializeLargeObjects: bool)
    (context: OperationContext)
    = async {
        let! call =
            provider.StoragePolicy.SetSettings
                repository
                {
                    AutoPolicyThresholdMb = savedThresholdMb
                    MaterializeLargeObjects = savedMaterializeLargeObjects
                }
                context

        match call with
        | NoOp reason -> return OperationResult.unchanged (Some reason) ()
        | Executed result -> return result
    }
```

The provider owns its storage keys and escaping rules. Hosts should not read or write provider configuration directly.

Git's `SetPathPolicy` reports the path and `.gitattributes` in `AffectedPaths`, and the next revision that includes the path stores it under the new policy. Git refuses to unmark a pointer whose object is not local (`object_not_local`), because the pointer text would be committed as content.

## Compatibility

The published records are intentionally small. Add new capability as a new function with its own entry in the list instead of adding required members to an existing request or result record. A provider written against the list then fails to compile until it answers `Implemented` or `Missing` for the function, so every provider states for each function whether it has it. Provider-specific mechanics stay behind the SPI. A host should not need provider-specific parsing to probe, open, or operate a workspace.

`OperationContext` is an exception to that rule. It is an observation record that the host builds, and it has the field `ReportOutput`. Code that builds the record as a literal has to set `ReportOutput = None`. Code that calls `OperationContext.create`, `createWithOutput` or `detached`, and a provider that only reads the context, compile as before. A provider that makes external calls, such as subprocesses or HTTP requests, reports their output only when `context.ReportOutput` is `Some`, and it redacts every line first, as the [consuming guide](consuming.md#raw-output-of-subprocesses) describes. A provider that continues work after the caller canceled keeps the output callback of the original context and replaces only its cancellation, so the follow-up calls reach the same log.

A call that knows its credentials as plain values, such as the access key and the secret of an HTTP client, gets the list of secrets to replace from `OutputReporter.knownSecrets` and starts its command with `OutputReporter.beginCommandWithSecrets`. `CommandOutput.ObserveNotice` adds a `Notice` line of the library to that command, for example the status of an HTTP response.

Run the shared conformance profiles before publishing an external provider. The harness contract and profile expectations are described in [conformance-profiles.md](conformance-profiles.md).
