namespace VersionControlService.Abstractions

/// Reads the functions of a provider directly, without the wrapper of the interface.
/// Subscribing only looks into the provider's list. It runs no check and calls no provider function.
module Subscribe =

    let private functionsOf (provider: IVersionControl) : Result<ProviderFunctions, string> =
        match provider with
        | :? ListedProvider as listed -> Ok listed.Functions
        | _ -> Error "This provider was not built from a function list."

    /// One function of the provider with its own signature and its direct result. Fails with the reason of
    /// the entry when the provider does not have it.
    let single (select: ProviderFunctions -> Entry<'call>) (provider: IVersionControl) : Result<'call, string> =
        match functionsOf provider with
        | Ok functions ->
            match select functions with
            | Implemented call -> Ok call
            | Missing reason -> Error reason
        | Error message -> Error message

    /// Everything the provider has. The entries say which functions are there.
    let all (provider: IVersionControl) : Result<ProviderFunctions, string> = functionsOf provider
