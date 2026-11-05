#if TOOLS
namespace Enaweg.Plugin.Internal;

/// <summary>
/// Why a plugin state attempt did not complete, as recorded in the <c>.user</c> state file (<c>reason</c>) and explained
/// by the ePlugin Manager's Needs attention dialog (<see cref="Manager.AttentionItem"/>). The values are stored, so they
/// never change; a new one needs its explanation there.
/// </summary>
internal static class AttemptReasons
{
    /// <summary>The attempt began and has not completed yet, or the editor stopped during it.</summary>
    public const string InProgress = "attempt_in_progress";
    public const string CannotRecordParticipant = "cannot_record_participant";
    public const string DeactivationFailed = "deactivation_failed";
    public const string DependencyDisabled = "dependency_disabled";
    public const string DependencyResolutionFailed = "dependency_resolution_failed";
    public const string DependencyTimedOut = "dependency_timed_out";
    public const string DependentRecipeCreationFailed = "dependent_recipe_creation_failed";
    public const string DotnetUnavailable = "dotnet_unavailable";
    public const string InvalidPluginVersion = "invalid_plugin_version";
    public const string OperationTimedOut = "operation_timed_out";
    public const string OptionalRecipeFailed = "optional_recipe_failed";
    public const string ParticipantBlocked = "participant_blocked";
    public const string PluginEnableRefused = "plugin_enable_refused";
    public const string RecipeCreationFailed = "recipe_creation_failed";
    public const string RecipeOperationFailed = "recipe_operation_failed";
    public const string RetryNotContinued = "retry_not_continued";
    public const string SolutionBuildFailed = "solution_build_failed";
    public const string StateCommitFailed = "state_commit_failed";

    /// <summary>The start of every reason an update records.</summary>
    public const string UpdatePrefix = "update_";
    public const string UpdateCommitFailed = "update_commit_failed";
    public const string UpdateKeptBuildFailed = "update_kept_build_failed";
    public const string UpdateRollbackFailed = "update_rollback_failed";

    /// <summary>The reasons the plugin lifecycle records (not updates), each of which the Needs attention dialog explains.</summary>
    public static readonly string[] Lifecycle =
    [
        InProgress, DependencyResolutionFailed, RecipeCreationFailed, DependentRecipeCreationFailed, RecipeOperationFailed,
        SolutionBuildFailed, OptionalRecipeFailed, InvalidPluginVersion, ParticipantBlocked, CannotRecordParticipant,
        StateCommitFailed, DeactivationFailed, PluginEnableRefused, RetryNotContinued, DotnetUnavailable, DependencyDisabled,
        DependencyTimedOut, OperationTimedOut
    ];
}
#endif
