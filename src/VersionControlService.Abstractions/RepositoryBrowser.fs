namespace VersionControlService.Abstractions

/// Optional repository browser service.
type RepositoryBrowserService = {
    GetRepositoryWebUrl: OperationContext -> Async<OperationResult<string option>>
}
