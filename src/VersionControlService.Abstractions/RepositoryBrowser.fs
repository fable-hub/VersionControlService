namespace VersionControlService.Abstractions

/// The repository browser functions of a provider, one entry for each.
type RepositoryBrowserFunctions = {
    GetRepositoryWebUrl: Entry<Repository -> OperationContext -> Async<OperationResult<string option>>>
}

/// Reports the web address of the repository, if the provider knows one.
type IRepositoryBrowser =
    abstract GetRepositoryWebUrl:
        repository: Repository -> context: OperationContext -> Async<CallResult<string option>>
