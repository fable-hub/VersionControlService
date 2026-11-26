/// The lakeFS provider as one version control interface.
module VersionControlService.LakeFs.LakeFsProvider

open VersionControlService.Abstractions

module LakeFsProviderFactory = VersionControlService.LakeFs.LakeFsProviderFactory
module LakeFsProviderOptions = VersionControlService.LakeFs.LakeFsProviderOptions
module LakeFsWorkspaceSession = VersionControlService.LakeFs.LakeFsWorkspaceSession

/// Creates the lakeFS provider. Every repository it opens keeps its own state under the state
/// root of the options.
let create (options: LakeFsProviderOptions.LakeFsProviderOptions) : IVersionControl =
    ListedProvider(
        LakeFsProviderFactory.probe options,
        LakeFsWorkspaceSession.close,
        LakeFsWorkspaceSession.functions options
    )
    :> IVersionControl
