namespace VersionControlService.Abstractions

/// What a call through the interface returns.
type CallResult<'T> =
    /// The provider ran the function. The result is a success, a partial success or its own failure.
    | Executed of OperationResult<'T>
    /// The provider does not have this function, or does not need it. The reason says which.
    | NoOp of reason: string

/// One entry of a provider's list of functions.
type Entry<'call> =
    /// The provider has the function.
    | Implemented of 'call
    /// The provider does not have the function. The reason is one plain sentence for the user.
    | Missing of reason: string
