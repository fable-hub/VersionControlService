namespace VersionControlService.Git

open VersionControlService.Abstractions

module GitTextDiffService = VersionControlService.Git.TextDiff.GitTextDiffService

/// The settings of the Git provider.
type GitProviderOptions = {
    /// Test seams for child processes and transaction points.
    Hooks: GitWorkspaceSession.GitSessionHooks
    /// Resolves the credential for a host and connection profile.
    Credentials: GitCredentialStrategy.GitCredentialStrategy
    /// Resolves the identity that revisions are attributed to.
    RevisionIdentity: GitCredentialStrategy.GitIdentityStrategy
    /// Decides for each selected file whether a revision stores it inline or as a large object.
    RevisionPolicy: RevisionPolicyStrategy
    /// The worker pool for the text diff. Without it, opening a diff fails with diff_worker_failed.
    TextDiff: GitTextDiffService.GitTextDiffOptions option
}

module GitProviderOptions =

    /// No hooks, anonymous credentials and identity, the automatic revision policy, no text diff worker.
    let defaults: GitProviderOptions = {
        Hooks = GitWorkspaceSession.GitSessionHooks.none
        Credentials = GitCredentialStrategy.anonymous
        RevisionIdentity = GitCredentialStrategy.anonymousIdentity
        RevisionPolicy = RevisionPolicyStrategy.automatic
        TextDiff = None
    }

module GitProvider =

    /// The Git provider. Every function of the interface is there, and `Open`, `Clone`, `Initialize` and `Bind`
    /// hand out the opened repository that the other functions take.
    let create (options: GitProviderOptions) : IVersionControl =
        GitWorkspaceSession.createProvider
            options.Hooks
            options.Credentials
            options.RevisionIdentity
            options.RevisionPolicy
            options.TextDiff
