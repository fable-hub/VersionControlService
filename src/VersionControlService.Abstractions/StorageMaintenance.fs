namespace VersionControlService.Abstractions

/// The local storage maintenance functions of a provider, one entry for each.
type MaintenanceFunctions = {
    Prune: Entry<Repository -> OperationContext -> Async<OperationResult<string>>>
    Deduplicate: Entry<Repository -> OperationContext -> Async<OperationResult<string>>>
    CanDeduplicate: Entry<Repository -> OperationContext -> Async<OperationResult<bool>>>
}

/// Prunes and deduplicates the local storage of the repository, and reports whether deduplication can work.
type IMaintenance =
    abstract Prune: repository: Repository -> context: OperationContext -> Async<CallResult<string>>
    abstract Deduplicate: repository: Repository -> context: OperationContext -> Async<CallResult<string>>

    /// Whether Deduplicate can work for this workspace, as the provider's own tooling reports it. True does
    /// not promise that a later Deduplicate succeeds, because that call checks its own preconditions. False
    /// means the tooling ruled deduplication out and carries no reason. A failed result means the check
    /// itself could not run.
    abstract CanDeduplicate: repository: Repository -> context: OperationContext -> Async<CallResult<bool>>
