namespace VersionControlService.Abstractions

/// The host-selected comparison semantics for local workspace paths.
type PathCaseSensitivity =
    | CaseSensitive
    | CaseInsensitive

/// Path helpers for hosts that compare workspace roots.
module ProviderResolver =

    /// Normalizes a local path for root comparison only: forward slashes and no
    /// redundant trailing separator. It deliberately preserves case and Unicode.
    let normalizePath (path: string) : string =
        let forward = (path |> Option.ofObj |> Option.defaultValue "").Replace('\\', '/')
        let trimmed = forward.TrimEnd '/'
        if trimmed.Length = 0 then "/" else trimmed
