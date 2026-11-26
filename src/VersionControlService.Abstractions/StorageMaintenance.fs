namespace VersionControlService.Abstractions

/// Optional local storage maintenance service.
type StorageMaintenanceService = {
    Prune: OperationContext -> Async<OperationResult<string>>
    Deduplicate: OperationContext -> Async<OperationResult<string>>
    /// Whether Deduplicate can work for this workspace, as the provider's own tooling reports it. True does
    /// not promise that a later Deduplicate succeeds, because that call checks its own preconditions. False
    /// means the tooling ruled deduplication out and carries no reason. A failed result means the check
    /// itself could not run.
    CanDeduplicate: OperationContext -> Async<OperationResult<bool>>
}
