namespace Autopilot.Core.Workspace;

/// <summary>What <see cref="LegacyWorkspaceMigration.Apply"/> did.</summary>
public enum LegacyMigrationOutcome
{
    /// <summary>There was no old folder, or the new folder is not the default one. Nothing was done.</summary>
    NotNeeded,

    /// <summary>The old folder was moved to the new name.</summary>
    Moved,

    /// <summary>Both folders exist. The new one is used and the old one is left untouched.</summary>
    BothExist,

    /// <summary>The move failed (for example a file is open elsewhere). Nothing was changed; the old folder is used.</summary>
    Failed,
}

public sealed record LegacyMigrationResult(LegacyMigrationOutcome Outcome, string Root, string? Message);

/// <summary>
/// The product used to be called Dev Autopilot and kept its workspace in <c>.dev-autopilot</c> of the user profile.
/// On the first start with the default location, that folder is moved to <c>.autopilot</c>. A folder is never
/// overwritten and nothing is deleted: if both exist the new one wins and the old one stays where it is.
/// </summary>
public static class LegacyWorkspaceMigration
{
    public const string LegacyFolderName = ".dev-autopilot";
    public const string NoteFileName = "MIGRATED-FROM-dev-autopilot.txt";

    public static LegacyMigrationResult Apply(string userProfile)
    {
        var current = Path.Combine(userProfile, Workspace.DefaultFolderName);
        var legacy = Path.Combine(userProfile, LegacyFolderName);

        if (!Directory.Exists(legacy))
        {
            return new LegacyMigrationResult(LegacyMigrationOutcome.NotNeeded, current, null);
        }

        if (Directory.Exists(current))
        {
            return new LegacyMigrationResult(
                LegacyMigrationOutcome.BothExist,
                current,
                $"Autopilot uses {current}. The old folder {legacy} still exists and is not used; copy anything you need from it, then delete it.");
        }

        try
        {
            Directory.Move(legacy, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LegacyMigrationResult(
                LegacyMigrationOutcome.Failed,
                legacy,
                $"Could not move {legacy} to {current} ({ex.Message}). Autopilot keeps using the old folder for now; close other Autopilot processes and start again to move it.");
        }

        var note = $"This folder was {legacy} before Autopilot was renamed from Dev Autopilot.{Environment.NewLine}"
            + $"It was moved here on {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC. Nothing in it was changed.{Environment.NewLine}"
            + $"A project whose settings name the old solution or paths needs those edited by hand.{Environment.NewLine}";
        try
        {
            File.WriteAllText(Path.Combine(current, NoteFileName), note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The note is a courtesy; the data is already in place.
        }

        return new LegacyMigrationResult(LegacyMigrationOutcome.Moved, current, $"Moved the workspace {legacy} to {current}. See {NoteFileName} in it.");
    }
}
