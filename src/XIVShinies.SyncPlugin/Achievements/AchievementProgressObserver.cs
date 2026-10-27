using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Diagnostics;

namespace XIVShinies.SyncPlugin.Achievements;

/// <summary>
/// Takes the progress the game receives when the player opens an achievement in the Achievements
/// window, and holds it for the next sync.
/// </summary>
/// <remarks>
/// The game asks its server for one achievement's progress when the player selects it, and keeps the
/// answer in four fields this reads each frame (see <see cref="AchievementProgressCapture"/>). Read
/// only: asking for progress would send server traffic the player did not cause. Readings belong to
/// the character who opened them, so both session edges drop them; losing consent drops them too.
/// </remarks>
public sealed unsafe class AchievementProgressObserver : IDisposable
{
    /// <summary>The Achievements window, whose details panel shows an achievement's progress.</summary>
    private const string AchievementsWindowName = "Achievement";

    /// <summary>
    /// The Party Finder's windows. The Party Finder asks for two Pilgrim's Traverse achievements on its
    /// own, so no answer is taken while one is open.
    /// </summary>
    private static readonly string[] PartyFinderWindowNames =
        ["LookingForGroup", "LookingForGroupCondition", "LookingForGroupDetail"];

    private readonly AchievementProgressCapture capture = new();

    // Built once, so a frame with no answer allocates nothing.
    private readonly Func<bool> answerCounts;

    // Read faults, each reported in full once; a failing read would otherwise report every frame.
    private readonly RepeatedFailures readFailures = new();

    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly Func<string, bool> isCategoryEnabled;
    private readonly IPluginLog log;
    private readonly TimeProvider timeProvider;

    /// <summary>Wires the listeners. Takes nothing until the player opens an achievement.</summary>
    /// <param name="isCategoryEnabled">
    /// Whether the user has the given category switched on. Passed in so this class holds no opinion
    /// on where consent lives.
    /// </param>
    /// <param name="timeProvider">The clock readings are stamped with; the system clock when omitted.</param>
    public AchievementProgressObserver(
        IClientState clientState,
        IFramework framework,
        IGameGui gameGui,
        Func<string, bool> isCategoryEnabled,
        IPluginLog log,
        TimeProvider? timeProvider = null)
    {
        this.clientState = clientState;
        this.framework = framework;
        this.gameGui = gameGui;
        this.isCategoryEnabled = isCategoryEnabled;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        answerCounts = AnswerCounts;

        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;

        // Subscribed last: a constructor that fails part-way returns nothing to dispose, so a frame
        // handler subscribed earlier would run for the rest of the session.
        framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// Raised when a reading is taken, with the category key whose facts it holds for the next upload.
    /// </summary>
    /// <remarks>The key travels with the event, so the scheduler never names this collection.</remarks>
    public event Action<string>? ReadingTaken;

    /// <summary>The readings held this session, the newest per achievement.</summary>
    /// <remarks>Framework thread only, where the readings are taken.</remarks>
    public IReadOnlyList<AchievementProgressReading> Readings() => capture.Readings();

    /// <summary>Unregisters everything the constructor wired.</summary>
    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
    }

    private void OnLogin() => capture.Forget();

    private void OnLogout(int type, int code) => capture.Forget();

    /// <summary>Reads the game's progress fields and takes an answer that just landed.</summary>
    /// <remarks>Four field reads a frame.</remarks>
    private void OnFrameworkUpdate(IFramework _)
    {
        var taken = false;

        try
        {
            // While nothing may be taken, every frame is a fresh baseline, so switching the collection
            // on or logging in never takes an answer that landed before.
            if (!clientState.IsLoggedIn || !isCategoryEnabled(CategoryKeys.AchievementProgress))
            {
                capture.Forget();
                return;
            }

            var achievement = Achievement.Instance();
            if (achievement == null)
                return;

            var fields = new ProgressFields(
                (ProgressRequestState)achievement->ProgressRequestState,
                achievement->ProgressAchievementId,
                achievement->ProgressCurrent,
                achievement->ProgressMax);

            var (look, answer) = capture.Look(fields, answerCounts, timeProvider.GetUtcNow());
            if (look == ProgressLook.NoAnswer)
                return;

            var shown = $"achievement {answer.AchievementId} ({answer.Current}/{answer.Max})";
            switch (look)
            {
                case ProgressLook.Taken:
                    log.Debug($"Progress read for {shown}.");
                    taken = true;
                    break;

                case ProgressLook.NotCounted:
                    log.Debug(
                        $"Progress for {shown} arrived with the Achievements window closed or the Party " +
                        "Finder open; not taken.");
                    break;

                case ProgressLook.OutsideContract:
                    log.Debug($"Progress for {shown} is outside what the website accepts; skipped.");
                    break;
            }
        }
        catch (Exception ex)
        {
            // A frame callback that throws would escape into Dalamud's frame dispatch.
            if (readFailures.IsFirstSighting(ex))
                log.Error(ex, "Could not read achievement progress.");
            else
                log.Debug(ex, "Could not read achievement progress again.");
        }

        // Outside the catch, so a fault in upload scheduling reports as itself.
        if (taken)
            ReadingTaken?.Invoke(CategoryKeys.AchievementProgress);
    }

    /// <summary>Whether an answer landing now is one the player asked for.</summary>
    private bool AnswerCounts() =>
        IsOpen(AchievementsWindowName) && !IsAnyOpen(PartyFinderWindowNames);

    private bool IsOpen(string windowName)
    {
        var window = gameGui.GetAddonByName(windowName);
        return !window.IsNull && window.IsVisible;
    }

    private bool IsAnyOpen(string[] windowNames)
    {
        foreach (var windowName in windowNames)
        {
            if (IsOpen(windowName))
                return true;
        }

        return false;
    }
}
