namespace VersionControlService.Abstractions

/// One opened repository. A provider hands it out from Clone, Initialize, Bind and Open and takes it
/// back as the first argument of every function that works on a repository. Each provider has its
/// own private subclass that holds its state.
[<AbstractClass>]
type Repository(descriptor: RepositoryDescriptor) =
    /// Identity of the opened repository.
    member _.Descriptor = descriptor

module Repository =

    /// The failure a provider answers with when it is handed a repository that another provider opened.
    let ofAnotherProvider (repository: Repository) : OperationFailure =
        OperationFailure.create
            Validation
            "repository_of_another_provider"
            $"The repository was opened by the provider '{ProviderId.value repository.Descriptor.ProviderId}', so this provider cannot use it."
