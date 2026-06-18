using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Assets.Code.Actor;
using Assets.Code.Actor.Queries;
using Assets.Code.Audio.Narration;
using Assets.Code.Boss;
using Assets.Code.Buff;
using Assets.Code.Campaign;
using Assets.Code.Combat;
using Assets.Code.Combat.BattleConfiguration;
using Assets.Code.Combat.BattleModifier;
using Assets.Code.Condition;
using Assets.Code.Data;
using Assets.Code.Dot;
using Assets.Code.Effect;
using Assets.Code.Game;
using Assets.Code.Game.StageCoach;
using Assets.Code.Item;
using Assets.Code.Library;
using Assets.Code.Loading;
using Assets.Code.Locale;
using Assets.Code.Math;
using Assets.Code.Map;
using Assets.Code.Map.Generation;
using Assets.Code.Map.Generation.Route;
using Assets.Code.Map.Minimap;
using Assets.Code.Quirk;
using Assets.Code.Run;
using Assets.Code.Roster;
using Assets.Code.Skill;
using Assets.Code.Source;
using Assets.Code.Token;
using Assets.Code.Torch;
using Assets.Code.UI;
using Assets.Code.UI.Data;
using Assets.Code.UI.Managers;
using Assets.Code.UI.RunLog;
using Assets.Code.Unlock;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Adapter;
using DD2SteamMultiplayerHost.Protocol;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace DD2SteamMultiplayerHost
{
    public sealed class DD2SteamMultiplayerRunner : MonoBehaviour
    {
        private const float PanelMinWidth = 520f;
        private const float PanelMinHeight = 360f;
        private const float PanelResizeHandleSize = 22f;
        private const float MirrorHudMinWidth = 900f;
        private const float MirrorHudMinHeight = 540f;
        private const int MaxSnapshotPollsPerFrame = 2;
        private const float ActiveSnapshotPollInterval = 0.5f;
        private const float HeavyActiveSnapshotPollInterval = 1f;
        private const float InactiveSnapshotPollInterval = 2.5f;
        private const float ActiveSnapshotForcedSendInterval = 3f;
        private const float InactiveSnapshotForcedSendInterval = 30f;
        private const float RunStateSnapshotPollInterval = 1f;
        private const float RunStateSnapshotForcedSendInterval = 10f;
        private const float OverviewSnapshotPollInterval = 6f;
        private const float OverviewSnapshotForcedSendInterval = 30f;
        private const float DamageMeterActiveSnapshotPollInterval = 1f;
        private const float DamageMeterInactiveSnapshotPollInterval = 10f;
        private const float DamageMeterActiveSnapshotForcedSendInterval = 10f;
        private const float DamageMeterInactiveSnapshotForcedSendInterval = 30f;
        private const string DefaultArenaBattleConfigId = "mountain_boss_arms";
        private const string DefaultArenaCombatArenaId = "combat_arena_valley_gaunt";
        private const double SnapshotPerfSlowThresholdMs = 4.0;
        private const float SnapshotPerfSummaryInterval = 60f;
        private const float MirrorHudMapRowGap = 124f;
        private const float MirrorHudMapTopPadding = 50f;
        private const float MirrorHudMapNodeSize = 46f;
        private const float MirrorHudMapRouteIconSize = 30f;
        private static readonly bool SnapshotPerfLoggingEnabled = false;

        private static readonly Color PanelTextColor = new Color(0.92f, 0.95f, 0.98f, 1f);
        private static readonly Color PanelMutedTextColor = new Color(0.66f, 0.72f, 0.78f, 1f);
        private static readonly Color HudBackgroundColor = new Color(0.03f, 0.035f, 0.04f, 0.96f);
        private static readonly Color HudPanelColor = new Color(0.10f, 0.12f, 0.14f, 0.92f);
        private static readonly Color HudCardColor = new Color(0.15f, 0.17f, 0.19f, 0.95f);
        private static readonly Color HudCurrentCardColor = new Color(0.20f, 0.24f, 0.27f, 0.98f);
        private static readonly Color HudTargetCardColor = new Color(0.20f, 0.27f, 0.22f, 0.98f);
        private static readonly Color HudHostOnlyCardColor = new Color(0.13f, 0.13f, 0.15f, 0.92f);
        private static readonly Color HudHealthColor = new Color(0.75f, 0.16f, 0.13f, 1f);
        private static readonly Color HudStressColor = new Color(0.55f, 0.30f, 0.82f, 1f);
        private static readonly Color HudBarBackColor = new Color(0.02f, 0.025f, 0.03f, 1f);
        private static readonly Color HudTileColor = new Color(0.16f, 0.18f, 0.20f, 0.96f);
        private static readonly Regex RichTextTagRegex = new Regex("<.*?>");
        private static readonly Regex SpriteTagRegex = new Regex(
            "<sprite\\b(?<attrs>[^>]*)>",
            RegexOptions.IgnoreCase);
        private static readonly Regex SpriteTagAttributeRegex = new Regex(
            "\\b(?<key>name|index|sprite)\\s*=\\s*(?:\"(?<value>[^\"]+)\"|'(?<value>[^']+)'|\\{q\\}(?<value>.*?)\\{q\\}|(?<value>[^\\s>]+))",
            RegexOptions.IgnoreCase);
        private static readonly Regex SpriteTagShorthandRegex = new Regex(
            "^\\s*=\\s*(?:\"(?<value>[^\"]+)\"|'(?<value>[^']+)'|\\{q\\}(?<value>.*?)\\{q\\}|(?<value>[^\\s>]+))",
            RegexOptions.IgnoreCase);
        private static readonly Regex TooltipWordRegex = new Regex("\\s+|\\S+");
        private static readonly object ArenaBattleModifierPatchLock = new object();
        private static readonly ArenaTorchConfessionProfile[] ArenaTorchConfessionProfiles =
        {
            new ArenaTorchConfessionProfile("brain", "Denial", "否认"),
            new ArenaTorchConfessionProfile("lungs", "Resentment", "怨怼"),
            new ArenaTorchConfessionProfile("eyes", "Obsession", "执迷"),
            new ArenaTorchConfessionProfile("arms", "Ambition", "野心"),
            new ArenaTorchConfessionProfile("body", "Cowardice", "怯懦"),
        };
        private static DD2SteamMultiplayerRunner _activeArenaRunner;
        private static bool _arenaBattleModifierPatchInstalled;
        private static bool _arenaBattleModifierPatchFailedLogged;
        private static object _arenaBattleModifierHarmony;

        private bool _steamIdentityLogged;
        private bool _compatibilityAssembliesLogged;
        private bool _hotkeysUnavailableLogged;
        private int _snapshotPollsThisFrame;
        private float _nextSnapshotPerfSummaryTime;
        private DateTime _lastCommandWriteTimeUtc = DateTime.MinValue;
        private float _nextCommandPollTime;
        private float _nextPvpControllerPollTime;
        private float _nextAutoTurnPollTime;
        private float _nextCombatSnapshotPollTime;
        private float _nextCombatSnapshotForcedSendTime;
        private float _nextLootWindowSnapshotPollTime;
        private float _nextLootWindowSnapshotForcedSendTime;
        private float _nextGameResultsSnapshotPollTime;
        private float _nextGameResultsSnapshotForcedSendTime;
        private float _nextRouteChoiceSnapshotPollTime;
        private float _nextRouteChoiceSnapshotForcedSendTime;
        private float _nextHeroSelectSnapshotPollTime;
        private float _nextHeroSelectSnapshotForcedSendTime;
        private float _nextHeroLoadoutSnapshotPollTime;
        private float _nextHeroLoadoutSnapshotForcedSendTime;
        private float _nextRunStateSnapshotPollTime;
        private float _nextRunStateSnapshotForcedSendTime;
        private float _nextExpeditionOverviewSnapshotPollTime;
        private float _nextExpeditionOverviewSnapshotForcedSendTime;
        private float _nextMainMenuSnapshotPollTime;
        private float _nextMainMenuSnapshotForcedSendTime;
        private float _nextStoryChoiceSnapshotPollTime;
        private float _nextStoryChoiceSnapshotForcedSendTime;
        private float _nextInnSnapshotPollTime;
        private float _nextInnSnapshotForcedSendTime;
        private float _nextEmbarkSnapshotPollTime;
        private float _nextEmbarkSnapshotForcedSendTime;
        private float _nextAltarSnapshotPollTime;
        private float _nextAltarSnapshotForcedSendTime;
        private float _nextConfessionChoiceSnapshotPollTime;
        private float _nextConfessionChoiceSnapshotForcedSendTime;
        private float _nextLairDecisionSnapshotPollTime;
        private float _nextLairDecisionSnapshotForcedSendTime;
        private float _nextConfirmationDialogSnapshotPollTime;
        private float _nextConfirmationDialogSnapshotForcedSendTime;
        private float _nextStoreSnapshotPollTime;
        private float _nextStoreSnapshotForcedSendTime;
        private float _nextStagecoachSnapshotPollTime;
        private float _nextStagecoachSnapshotForcedSendTime;
        private float _nextDamageMeterSnapshotPollTime;
        private float _nextDamageMeterSnapshotForcedSendTime;
        private float _nextSteamIdentityAttempt;
        private bool _autoTurnPromptsEnabled = true;
        private bool _arenaPendingLaunch;
        private bool _arenaDebugControlsSuppressed;
        private bool _arenaDebugControlsEnteredCombat;
        private int _lastAutoTurnRound = -1;
        private int _lastAutoTurnTurn = -1;
        private int _lastAutoTurnSlot = -1;
        private uint _lastAutoTurnActorGuid;
        private string _lastAutoTurnSkipKey;
        private bool _pvpEnemyInputControllersActive;
        private string _lastPvpEnemyControllerDigest;
        private string _lastCombatSnapshotDigest;
        private string _lastLootWindowSnapshotDigest;
        private string _lastGameResultsSnapshotDigest;
        private string _lastRouteChoiceSnapshotDigest;
        private string _lastHeroSelectSnapshotDigest;
        private string _lastHeroLoadoutSnapshotDigest;
        private string _lastRunStateSnapshotDigest;
        private string _lastExpeditionOverviewSnapshotDigest;
        private string _lastMainMenuSnapshotDigest;
        private string _lastStoryChoiceSnapshotDigest;
        private string _lastInnSnapshotDigest;
        private string _lastEmbarkSnapshotDigest;
        private string _lastAltarSnapshotDigest;
        private string _lastConfessionChoiceSnapshotDigest;
        private string _lastLairDecisionSnapshotDigest;
        private string _lastConfirmationDialogSnapshotDigest;
        private string _lastStoreSnapshotDigest;
        private string _lastStagecoachSnapshotDigest;
        private string _lastDamageMeterSnapshotDigest;
        private string _lastAppliedLocalDamageMeterDigest;
        private bool _damageMeterRemoteApplyChecked;
        private Type _damageMeterRemoteApiType;
        private MethodInfo _damageMeterRemoteApplyMethod;
        private readonly HashSet<int> _lootVoteSelectedIndexes = new HashSet<int>();
        private string _lootVoteSelectionDigest;
        private readonly Dictionary<string, HashSet<string>> _restItemTargetSelections =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private string _restItemSelectionDigest;
        private bool _panelVisible;
        private bool _panelErrorLogged;
        private bool _mirrorHudVisible;
        private bool _mirrorHudMapExpanded = true;
        private bool _mirrorHudErrorLogged;
        private bool _hostVoteUiErrorLogged;
        private UiLanguage _uiLanguage = IsChineseCultureDefault() ? UiLanguage.Chinese : UiLanguage.English;
        private bool _arenaBattlePresetBrowserVisible;
        private bool _arenaHeroSetupVisible;
        private bool _panelResizing;
        private bool _panelResizeChangedThisFrame;
        private Rect _panelRect = new Rect(32f, 80f, 820f, 720f);
        private Rect _arenaBattlePresetBrowserRect = new Rect(72f, 64f, 1240f, 820f);
        private Rect _arenaHeroSetupRect = new Rect(96f, 88f, 1160f, 760f);
        private Vector2 _panelScroll;
        private Vector2 _mirrorHudScroll;
        private Vector2 _mirrorHudMapScroll;
        private string _panelJoinLobbyId = string.Empty;
        private string _arenaBattleConfigId = DefaultArenaBattleConfigId;
        private string _arenaBattleConfigSearch = string.Empty;
        private string _arenaCombatArenaId = string.Empty;
        private string _arenaHeroDraftSearch = string.Empty;
        private string _arenaHeroStartEffectSearch = string.Empty;
        private string _arenaBossModifierSearch = string.Empty;
        private string _arenaBattleModifierSearch = string.Empty;
        private string _arenaTorchSearch = string.Empty;
        private string _arenaPositiveQuirkSearch = string.Empty;
        private string _arenaNegativeQuirkSearch = string.Empty;
        private string _arenaDiseaseQuirkSearch = string.Empty;
        private string _arenaBossModifierId = string.Empty;
        private string _arenaBattleModifierId = string.Empty;
        private string _arenaTorchConfessionGroupId = string.Empty;
        private string _arenaTorchFlameItemId = string.Empty;
        private float _arenaTorchValue = 100f;
        private string _arenaStatus = "Idle";
        private Vector2 _arenaBattleConfigBrowserScroll;
        private Vector2 _arenaBattlePresetDetailScroll;
        private Vector2 _arenaBattleSequenceScroll;
        private Vector2 _arenaHeroCatalogScroll;
        private Vector2 _arenaHeroDetailScroll;
        private string[] _arenaPendingNativeLaunchPrefsLines;
        private float _arenaLaunchDeadline;
        private float _nextArenaLaunchLogTime;
        private float _nextArenaDebugControlSuppressTime;
        private bool _arenaResultBypassArmed;
        private bool _arenaResultBypassLogged;
        private bool _arenaResultBypassCombatEntered;
        private bool _arenaWaitingForNextBattle;
        private bool _arenaBattleModifierOverrideArmed;
        private string _arenaBattleModifierOverrideLogKey;
        private bool _arenaTorchOverrideArmed;
        private string _arenaTorchOverrideLogKey;
        private string _arenaTorchAttachedItemId;
        private DataContainer _arenaTorchAttachedDataContainer;
        private bool _arenaTorchAttachedToRunData;
        private bool _arenaTorchAttachedToPartyData;
        private bool _arenaBattlePresetBrowserResizing;
        private bool _arenaBattlePresetBrowserResizeChangedThisFrame;
        private bool _arenaHeroSetupResizing;
        private bool _arenaHeroSetupResizeChangedThisFrame;
        private bool _arenaPostBattleMainMenuReturnPending;
        private bool _arenaPostBattleMainMenuReturnRequested;
        private bool _arenaPendingDraftSkillApply;
        private bool _arenaPendingDraftQuirkApply;
        private bool _arenaPendingEnemyDraftApply;
        private float _arenaResultBypassDeadline;
        private float _nextArenaResultBypassAttemptTime;
        private float _arenaResultsModeFirstSeenTime;
        private float _arenaPostBattleMainMenuReturnTime;
        private float _nextArenaPostBattleMainMenuLogTime;
        private float _arenaNextBattleWaitStartTime;
        private float _nextArenaNextBattleWaitLogTime;
        private float _arenaDraftSkillApplyDeadline;
        private float _nextArenaDraftSkillApplyAttemptTime;
        private float _arenaDraftQuirkApplyDeadline;
        private float _nextArenaDraftQuirkApplyAttemptTime;
        private float _arenaEnemyDraftApplyDeadline;
        private float _nextArenaEnemyDraftApplyAttemptTime;
        private GameModeType _arenaReturnMode = GameModeType.DRIVING;
        private string _arenaLastLaunchBattleConfigId = string.Empty;
        private bool _arenaBattlePresetCacheBuilt;
        private int _arenaBattlePresetTotalCount;
        private int _arenaBattlePresetMergedChildCount;
        private string _arenaBattlePresetSearchApplied = null;
        private bool _arenaHeroCatalogBuilt;
        private bool _arenaHeroDraftInitialized;
        private bool _arenaHeroItemCatalogBuilt;
        private bool _arenaHeroStartEffectCatalogBuilt;
        private bool _arenaBossModifierCatalogBuilt;
        private bool _arenaBattleModifierCatalogBuilt;
        private bool _arenaTorchCatalogBuilt;
        private bool _arenaTorchCatalogBuilding;
        private bool _arenaHeroQuirkCatalogBuilt;
        private int _arenaHeroCatalogTotalCount;
        private int _arenaHeroItemCatalogTotalCount;
        private int _arenaHeroStartEffectCatalogTotalCount;
        private int _arenaBossModifierCatalogTotalCount;
        private int _arenaHeroQuirkCatalogTotalCount;
        private int _arenaHeroDraftSelectedSlot;
        private int _arenaHeroSetupTeamIndex;
        private string _arenaHeroCatalogSearchApplied = null;
        private string _arenaHeroCombatItemSearch = string.Empty;
        private string _arenaHeroTrinketSearch = string.Empty;
        private string _arenaHeroCombatItemSearchApplied = null;
        private string _arenaHeroTrinketSearchApplied = null;
        private string _arenaHeroStartEffectSearchApplied = null;
        private string _arenaBossModifierSearchApplied = null;
        private string _arenaBattleModifierSearchApplied = null;
        private string _arenaTorchSearchApplied = null;
        private string _arenaPositiveQuirkSearchApplied = null;
        private string _arenaNegativeQuirkSearchApplied = null;
        private string _arenaDiseaseQuirkSearchApplied = null;
        private float _arenaResultLoadingFirstSeenTime;
        private float _nextArenaResultLoadingLogTime;
        private string _panelSkillId = string.Empty;
        private string _panelTargetGuid = string.Empty;
        private string _panelPendingKey;
        private string _hoverTooltipTitle;
        private string _hoverTooltipBody;
        private Vector2 _hoverTooltipScreenPosition;
        private bool _hoverTooltipHasScreenPosition;
        private PanelTab _panelTab = PanelTab.Home;
        private ArenaHeroDetailTab _arenaHeroDetailTab = ArenaHeroDetailTab.Skills;
        private ArenaBattleAdvantageMode _arenaBattleAdvantageMode = ArenaBattleAdvantageMode.None;
        private bool _panelStylesReady;
        private Texture2D _panelWindowTexture;
        private Texture2D _panelBodyTexture;
        private Texture2D _panelHeaderTexture;
        private Texture2D _panelTabTexture;
        private Texture2D _panelTabActiveTexture;
        private Texture2D _panelSeparatorTexture;
        private GUIStyle _panelWindowStyle;
        private GUIStyle _panelBodyStyle;
        private GUIStyle _panelHeaderStyle;
        private GUIStyle _panelTitleStyle;
        private GUIStyle _panelStatusStyle;
        private GUIStyle _panelTabStyle;
        private GUIStyle _panelTabActiveStyle;
        private GUIStyle _panelContentStyle;
        private GUIStyle _panelResizeHandleStyle;
        private GUIStyle _panelSeparatorStyle;
        private SteamLobbyClient _lobbyClient;
        private SteamMessageTransport _messageTransport;
        private MultiplayerSession _session;
        private DD2CombatCommandAdapter _combatAdapter;
        private DD2ResultSyncAdapter _resultSyncAdapter;
        private DD2RouteSyncAdapter _routeSyncAdapter;
        private DD2HeroSelectSyncAdapter _heroSelectSyncAdapter;
        private DD2HeroLoadoutSyncAdapter _heroLoadoutSyncAdapter;
        private DD2RunStateSyncAdapter _runStateSyncAdapter;
        private DD2ExpeditionOverviewSyncAdapter _expeditionOverviewSyncAdapter;
        private DD2MainMenuSyncAdapter _mainMenuSyncAdapter;
        private DD2StoryChoiceSyncAdapter _storyChoiceSyncAdapter;
        private DD2InnSyncAdapter _innSyncAdapter;
        private DD2EmbarkSyncAdapter _embarkSyncAdapter;
        private DD2AltarSyncAdapter _altarSyncAdapter;
        private DD2ConfessionChoiceSyncAdapter _confessionChoiceSyncAdapter;
        private DD2LairDecisionSyncAdapter _lairDecisionSyncAdapter;
        private DD2ConfirmationDialogSyncAdapter _confirmationDialogSyncAdapter;
        private DD2StoreSyncAdapter _storeSyncAdapter;
        private DD2StagecoachSyncAdapter _stagecoachSyncAdapter;
        private IDamageMeterSnapshotAdapter _damageMeterSnapshotAdapter;
        private readonly HostVoteUiCoordinator _hostVoteUiCoordinator = new HostVoteUiCoordinator();
        private readonly Dictionary<string, Sprite> _itemSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _skillSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _actorPortraitSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _tokenSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _dotSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _buffSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _quirkSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _battleModifierSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _arenaTorchSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _mapNodeSprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Sprite> _mapRouteSprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _missingTokenSpriteRetryAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _missingDotSpriteRetryAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _missingBuffSpriteRetryAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, TooltipIconData> _inlineSpriteIcons = new Dictionary<string, TooltipIconData>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _missingInlineSpriteRetryAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, IList<TooltipSegment>> _tooltipSegmentCache =
            new Dictionary<string, IList<TooltipSegment>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _arenaSkillDisplayNameCache =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _arenaSkillDescriptionCache =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _expandedLoadoutActorGuids = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<ArenaBattlePresetEntry> _arenaBattlePresetCache = new List<ArenaBattlePresetEntry>();
        private readonly List<ArenaBattlePresetEntry> _arenaBattlePresetMatches = new List<ArenaBattlePresetEntry>();
        private readonly List<string> _arenaBattleSequenceIds = new List<string>();
        private readonly ArenaHeroDraftSlot[] _arenaHeroDraftSlots = CreateArenaHeroDraftSlots();
        private readonly ArenaHeroDraftSlot[] _arenaEnemyHeroDraftSlots = CreateArenaHeroDraftSlots();
        private readonly List<ArenaHeroCatalogEntry> _arenaHeroCatalog = new List<ArenaHeroCatalogEntry>();
        private readonly List<ArenaHeroCatalogEntry> _arenaHeroCatalogMatches = new List<ArenaHeroCatalogEntry>();
        private readonly List<ArenaItemCatalogEntry> _arenaCombatItemCatalog = new List<ArenaItemCatalogEntry>();
        private readonly List<ArenaItemCatalogEntry> _arenaTrinketCatalog = new List<ArenaItemCatalogEntry>();
        private readonly List<ArenaItemCatalogEntry> _arenaCombatItemMatches = new List<ArenaItemCatalogEntry>();
        private readonly List<ArenaItemCatalogEntry> _arenaTrinketMatches = new List<ArenaItemCatalogEntry>();
        private readonly List<ArenaEffectCatalogEntry> _arenaHeroStartEffectCatalog = new List<ArenaEffectCatalogEntry>();
        private readonly List<ArenaEffectCatalogEntry> _arenaHeroStartEffectMatches = new List<ArenaEffectCatalogEntry>();
        private readonly List<string> _arenaHeroStartEffectIds = new List<string>();
        private readonly List<ArenaBossModifierCatalogEntry> _arenaBossModifierCatalog = new List<ArenaBossModifierCatalogEntry>();
        private readonly List<ArenaBossModifierCatalogEntry> _arenaBossModifierMatches = new List<ArenaBossModifierCatalogEntry>();
        private readonly List<ArenaBattleModifierCatalogEntry> _arenaBattleModifierCatalog = new List<ArenaBattleModifierCatalogEntry>();
        private readonly List<ArenaBattleModifierCatalogEntry> _arenaBattleModifierMatches = new List<ArenaBattleModifierCatalogEntry>();
        private readonly List<ArenaTorchCatalogEntry> _arenaTorchCatalog = new List<ArenaTorchCatalogEntry>();
        private readonly List<ArenaTorchCatalogEntry> _arenaTorchMatches = new List<ArenaTorchCatalogEntry>();
        private readonly HashSet<string> _arenaKnownTorchItemIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _arenaKnownTorchItemTags = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<ArenaQuirkCatalogEntry> _arenaPositiveQuirkCatalog = new List<ArenaQuirkCatalogEntry>();
        private readonly List<ArenaQuirkCatalogEntry> _arenaNegativeQuirkCatalog = new List<ArenaQuirkCatalogEntry>();
        private readonly List<ArenaQuirkCatalogEntry> _arenaDiseaseQuirkCatalog = new List<ArenaQuirkCatalogEntry>();
        private readonly List<ArenaQuirkCatalogEntry> _arenaPositiveQuirkMatches = new List<ArenaQuirkCatalogEntry>();
        private readonly List<ArenaQuirkCatalogEntry> _arenaNegativeQuirkMatches = new List<ArenaQuirkCatalogEntry>();
        private readonly List<ArenaQuirkCatalogEntry> _arenaDiseaseQuirkMatches = new List<ArenaQuirkCatalogEntry>();
        private readonly Dictionary<string, List<string>> _arenaBaseSkillIdsByActorId =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _arenaAvailableSkillIdsByActorPath =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, SnapshotPerfStats> _snapshotPerfStats =
            new Dictionary<string, SnapshotPerfStats>(StringComparer.Ordinal);

        private enum PanelTab
        {
            Home,
            Mirror,
            Lobby,
            Arena,
            Run,
            Loadout,
            Combat,
            Rewards,
            Coach,
            Decisions,
            Tools,
        }

        private enum UiLanguage
        {
            English,
            Chinese,
        }

        private enum ArenaHeroDetailTab
        {
            Skills,
            CombatItem,
            Trinkets,
            Quirks,
            StartBuffs,
            Ordainment,
        }

        private enum ArenaQuirkKind
        {
            Positive,
            Negative,
            Disease,
        }

        private enum ArenaBattleAdvantageMode
        {
            None,
            Random,
            Specific,
        }

        private enum ArenaTorchCatalogKind
        {
            None,
            Confession,
            FlameItem,
        }

        private enum ArenaTorchSelectionSection
        {
            Confession,
            Flame,
        }

        private sealed class SnapshotPerfStats
        {
            public int Calls;
            public int SlowCalls;
            public float NextSlowLogTime;
            public double TotalMs;
            public double MaxMs;
        }

        private sealed class ArenaBattlePresetEntry
        {
            public BattleConfigurationDefinition Config;
            public string Id;
            public string Summary;
            public string BackgroundScene;
            public string ChainSummary;
            public string RiskSummary;
            public bool IsLaunchRecommended;
            public bool IsReferencedChainChild;
            public string SearchText;
            public string Tooltip;
            public IReadOnlyList<string> EnemyActorIds;
            public IReadOnlyList<string> EnemyNames;
            public IReadOnlyList<string> ChainBattleIds;
            public IReadOnlyList<ArenaBattleWavePreview> Waves;
        }

        private sealed class ArenaBattleWavePreview
        {
            public string Label;
            public string BattleConfigId;
            public string Summary;
            public bool IsTableOption;
            public IReadOnlyList<string> EnemyActorIds;
            public IReadOnlyList<string> EnemyNames;
        }

        private sealed class ArenaHeroCatalogEntry
        {
            public string ActorId;
            public string DisplayName;
            public string SearchText;
        }

        private sealed class ArenaItemCatalogEntry
        {
            public string ItemId;
            public string DisplayName;
            public string Description;
            public string PreviewDescription;
            public string SearchText;
            public ItemDefinition Definition;
        }

        private sealed class ArenaEffectCatalogEntry
        {
            public string EffectId;
            public string DisplayName;
            public string Description;
            public string SearchText;
            public EffectDefinition Definition;
        }

        private sealed class ArenaBossModifierCatalogEntry
        {
            public string ModifierId;
            public string DisplayName;
            public string Description;
            public string SearchText;
            public BossModifierDefinition Definition;
        }

        private sealed class ArenaBattleModifierCatalogEntry
        {
            public string ModifierId;
            public string DisplayName;
            public string Description;
            public string SearchText;
            public BattleModifierDefinition Definition;
        }

        private sealed class ArenaTorchCatalogEntry
        {
            public string ProfileId;
            public ArenaTorchCatalogKind Kind;
            public string GroupId;
            public string ItemId;
            public string DisplayName;
            public string Description;
            public string SearchText;
            public TorchLevelGroupDefinition Group;
            public ItemDefinition ItemDefinition;
        }

        private sealed class ArenaTorchConfessionProfile
        {
            public ArenaTorchConfessionProfile(string groupId, string englishName, string chineseName)
            {
                GroupId = groupId;
                EnglishName = englishName;
                ChineseName = chineseName;
            }

            public string GroupId { get; private set; }
            public string EnglishName { get; private set; }
            public string ChineseName { get; private set; }
        }

        private sealed class ArenaQuirkCatalogEntry
        {
            public string QuirkId;
            public string DisplayName;
            public string Description;
            public string SearchText;
            public ArenaQuirkKind Kind;
            public QuirkDefinition Definition;
        }

        private sealed class ArenaHeroDraftSlot
        {
            public string ActorId = string.Empty;
            public string PathId = string.Empty;
            public string CombatItemId = string.Empty;
            public string DiseaseQuirkId = string.Empty;
            public readonly List<string> SkillIds = new List<string>();
            public readonly List<string> TrinketIds = new List<string>();
            public readonly List<string> PositiveQuirkIds = new List<string>();
            public readonly List<string> NegativeQuirkIds = new List<string>();
        }

        private static ArenaHeroDraftSlot[] CreateArenaHeroDraftSlots()
        {
            ArenaHeroDraftSlot[] slots = new ArenaHeroDraftSlot[4];
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = new ArenaHeroDraftSlot();
            }

            return slots;
        }

        private ArenaHeroDraftSlot[] GetArenaHeroDraftSlots(int teamIndex)
        {
            return teamIndex == 1 ? _arenaEnemyHeroDraftSlots : _arenaHeroDraftSlots;
        }

        private ArenaHeroDraftSlot[] GetActiveArenaHeroDraftSlots()
        {
            return GetArenaHeroDraftSlots(_arenaHeroSetupTeamIndex);
        }

        private string GetArenaDraftTeamLabel(int teamIndex)
        {
            return teamIndex == 1
                ? Ui("Enemy Heroes", "敌方英雄")
                : Ui("Party Heroes", "我方英雄");
        }

        private struct TooltipIconData
        {
            public readonly Sprite Sprite;
            public readonly Texture Texture;
            public readonly Rect TextureRect;

            public TooltipIconData(Sprite sprite)
            {
                Sprite = sprite;
                Texture = null;
                TextureRect = default(Rect);
            }

            public TooltipIconData(Texture texture, Rect textureRect)
            {
                Sprite = null;
                Texture = texture;
                TextureRect = textureRect;
            }

            public bool IsValid
            {
                get { return Sprite != null || Texture != null; }
            }
        }

        private enum TooltipSegmentKind
        {
            Text,
            Icon,
        }

        private sealed class TooltipSegment
        {
            public TooltipSegment(string text)
            {
                Kind = TooltipSegmentKind.Text;
                Text = text;
            }

            public TooltipSegment(TooltipIconData icon)
            {
                Kind = TooltipSegmentKind.Icon;
                Icon = icon;
            }

            public TooltipSegmentKind Kind { get; private set; }

            public string Text { get; private set; }

            public TooltipIconData Icon { get; private set; }
        }

        private sealed class HudStatusIcon
        {
            public string Id;
            public string Kind;
            public string Label;
            public string DisplayName;
            public string Description;
            public int Count;
            public int Duration;
            public Sprite Sprite;
            public Color Color;
        }

        private void Awake()
        {
            Debug.Log("[DD2SteamMP] Host runner started.");
            HostLog.Write("Host runner started.");
            _activeArenaRunner = this;
            EnsureArenaBattleModifierPatchInstalled();
            TryLogSteamIdentity();
            _messageTransport = new SteamMessageTransport();
            _lobbyClient = new SteamLobbyClient();
            _combatAdapter = new DD2CombatCommandAdapter();
            _resultSyncAdapter = new DD2ResultSyncAdapter();
            _resultSyncAdapter.BattleResultReady += OnBattleResultReady;
            _routeSyncAdapter = new DD2RouteSyncAdapter();
            _heroSelectSyncAdapter = new DD2HeroSelectSyncAdapter();
            _heroLoadoutSyncAdapter = new DD2HeroLoadoutSyncAdapter();
            _runStateSyncAdapter = new DD2RunStateSyncAdapter();
            _expeditionOverviewSyncAdapter = new DD2ExpeditionOverviewSyncAdapter();
            _mainMenuSyncAdapter = new DD2MainMenuSyncAdapter();
            _storyChoiceSyncAdapter = new DD2StoryChoiceSyncAdapter();
            _innSyncAdapter = new DD2InnSyncAdapter();
            _embarkSyncAdapter = new DD2EmbarkSyncAdapter();
            _altarSyncAdapter = new DD2AltarSyncAdapter();
            _confessionChoiceSyncAdapter = new DD2ConfessionChoiceSyncAdapter();
            _lairDecisionSyncAdapter = new DD2LairDecisionSyncAdapter();
            _confirmationDialogSyncAdapter = new DD2ConfirmationDialogSyncAdapter();
            _storeSyncAdapter = new DD2StoreSyncAdapter();
            _stagecoachSyncAdapter = new DD2StagecoachSyncAdapter();
            _damageMeterSnapshotAdapter = new DD2DamageMeterBridgeAdapter();
            _session = new MultiplayerSession(
                _lobbyClient,
                _messageTransport,
                _combatAdapter,
                _resultSyncAdapter,
                _resultSyncAdapter,
                _routeSyncAdapter,
                _heroSelectSyncAdapter,
                _heroLoadoutSyncAdapter,
                _mainMenuSyncAdapter,
                _storyChoiceSyncAdapter,
                _innSyncAdapter,
                _embarkSyncAdapter,
                _altarSyncAdapter,
                _confessionChoiceSyncAdapter,
                _lairDecisionSyncAdapter,
                _confirmationDialogSyncAdapter,
                _storeSyncAdapter,
                _stagecoachSyncAdapter);
            InitializeCommandFileCursor();
            LogControls();
            HostLog.Write("Command file: " + HostPaths.CommandPath);
        }

        private void Update()
        {
            HandleHotkeys();
            PollCommandFile();
            PollPendingArenaLaunch();
            PollArenaDebugControlSuppression();
            PollArenaResultBypass();
            PollArenaDraftSkillApply();
            PollArenaDraftQuirkApply();
            PollArenaEnemyDraftApply();
            PollPvpEnemyControllerRuntime();
            PollAutoTurnPrompts();
            _snapshotPollsThisFrame = 0;
            MeasureSnapshotPoll("combat", PollCombatSnapshots);
            MeasureSnapshotPoll("loot", PollLootWindowSnapshots);
            MeasureSnapshotPoll("results", PollGameResultsSnapshots);
            MeasureSnapshotPoll("route", PollRouteChoiceSnapshots);
            MeasureSnapshotPoll("hero_select", PollHeroSelectSnapshots);
            MeasureSnapshotPoll("hero_loadout", PollHeroLoadoutSnapshots);
            MeasureSnapshotPoll("run_state", PollRunStateSnapshots);
            MeasureSnapshotPoll("overview", PollExpeditionOverviewSnapshots);
            MeasureSnapshotPoll("main_menu", PollMainMenuSnapshots);
            MeasureSnapshotPoll("story", PollStoryChoiceSnapshots);
            MeasureSnapshotPoll("inn", PollInnSnapshots);
            MeasureSnapshotPoll("embark", PollEmbarkSnapshots);
            MeasureSnapshotPoll("altar", PollAltarSnapshots);
            MeasureSnapshotPoll("confession", PollConfessionChoiceSnapshots);
            MeasureSnapshotPoll("lair", PollLairDecisionSnapshots);
            MeasureSnapshotPoll("dialog", PollConfirmationDialogSnapshots);
            MeasureSnapshotPoll("store", PollStoreSnapshots);
            MeasureSnapshotPoll("stagecoach", PollStagecoachSnapshots);
            MeasureSnapshotPoll("damage_meter", PollDamageMeterSnapshots);
            LogSnapshotPerfSummaryIfDue();
            if (_resultSyncAdapter != null)
            {
                _resultSyncAdapter.TryEnsureListeners();
            }

            if (_routeSyncAdapter != null)
            {
                _routeSyncAdapter.TryEnsureListeners();
            }

            if (_heroSelectSyncAdapter != null)
            {
                _heroSelectSyncAdapter.TryEnsureListeners();
            }

            if (_heroLoadoutSyncAdapter != null)
            {
                _heroLoadoutSyncAdapter.TryEnsureListeners();
            }

            if (_runStateSyncAdapter != null)
            {
                _runStateSyncAdapter.TryEnsureListeners();
            }

            if (_storyChoiceSyncAdapter != null)
            {
                _storyChoiceSyncAdapter.TryEnsureListeners();
            }

            if (_storeSyncAdapter != null)
            {
                _storeSyncAdapter.TryEnsureListeners();
            }

            if (_stagecoachSyncAdapter != null)
            {
                _stagecoachSyncAdapter.TryEnsureListeners();
            }

            if (_messageTransport != null)
            {
                _messageTransport.PollIncoming();
            }

            ApplyRemoteDamageMeterSnapshotToLocalPlugin();

            if (_session != null)
            {
                _session.PollAutoResync();
            }

            if (!_steamIdentityLogged && Time.unscaledTime >= _nextSteamIdentityAttempt)
            {
                TryLogSteamIdentity();
            }

            if (!_compatibilityAssembliesLogged && Time.unscaledTime >= 5f)
            {
                LogCompatibilityAssemblies();
            }
        }

        private void OnDestroy()
        {
            Debug.Log("[DD2SteamMP] Host runner destroyed.");
            HostLog.Write("Host runner destroyed.");
            if (ReferenceEquals(_activeArenaRunner, this))
            {
                _activeArenaRunner = null;
            }

            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }

            _combatAdapter = null;

            if (_resultSyncAdapter != null)
            {
                _resultSyncAdapter.BattleResultReady -= OnBattleResultReady;
                _resultSyncAdapter.Dispose();
                _resultSyncAdapter = null;
            }

            if (_routeSyncAdapter != null)
            {
                _routeSyncAdapter.Dispose();
                _routeSyncAdapter = null;
            }

            if (_heroSelectSyncAdapter != null)
            {
                _heroSelectSyncAdapter.Dispose();
                _heroSelectSyncAdapter = null;
            }

            if (_heroLoadoutSyncAdapter != null)
            {
                _heroLoadoutSyncAdapter.Dispose();
                _heroLoadoutSyncAdapter = null;
            }

            if (_runStateSyncAdapter != null)
            {
                _runStateSyncAdapter.Dispose();
                _runStateSyncAdapter = null;
            }

            _expeditionOverviewSyncAdapter = null;

            if (_storyChoiceSyncAdapter != null)
            {
                _storyChoiceSyncAdapter.Dispose();
                _storyChoiceSyncAdapter = null;
            }

            _innSyncAdapter = null;
            _embarkSyncAdapter = null;
            _altarSyncAdapter = null;
            _confessionChoiceSyncAdapter = null;
            _lairDecisionSyncAdapter = null;
            _confirmationDialogSyncAdapter = null;
            if (_storeSyncAdapter != null)
            {
                _storeSyncAdapter.Dispose();
                _storeSyncAdapter = null;
            }

            if (_stagecoachSyncAdapter != null)
            {
                _stagecoachSyncAdapter.Dispose();
                _stagecoachSyncAdapter = null;
            }

            _damageMeterSnapshotAdapter = null;

            if (_lobbyClient != null)
            {
                _lobbyClient.LeaveLobby(_messageTransport);
                _lobbyClient.Dispose();
                _lobbyClient = null;
            }

            if (_messageTransport != null)
            {
                _messageTransport.Dispose();
                _messageTransport = null;
            }

            DestroyPanelTextures();
        }

        private void TryLogSteamIdentity()
        {
            try
            {
                CSteamID steamId = SteamUser.GetSteamID();
                bool loggedOn = SteamUser.BLoggedOn();
                Debug.Log($"[DD2SteamMP] Steam user detected: steamId={steamId.m_SteamID}, loggedOn={loggedOn}.");
                HostLog.Write($"Steam user detected: steamId={steamId.m_SteamID}, loggedOn={loggedOn}.");
                _steamIdentityLogged = steamId.m_SteamID != 0UL;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DD2SteamMP] Steam identity is not available yet: " + ex.Message);
                HostLog.Write("Steam identity is not available yet: " + ex.Message);
                _nextSteamIdentityAttempt = Time.unscaledTime + 2f;
            }
        }

        private void LogCompatibilityAssemblies()
        {
            _compatibilityAssembliesLogged = true;

            try
            {
                string[] assemblyNames = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetName().Name)
                    .Where(name =>
                        name.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("DD2DamageMeter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("DebugActorPlus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("ItemSpawnerPlus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("PartyBuilderPlus", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct()
                    .OrderBy(name => name)
                    .ToArray();

                string message = "Compatibility assemblies loaded: " + (assemblyNames.Length == 0 ? "<none>" : string.Join(", ", assemblyNames));
                Debug.Log("[DD2SteamMP] " + message);
                HostLog.Write(message);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DD2SteamMP] Failed to log compatibility assemblies: " + ex.Message);
                HostLog.Write("Failed to log compatibility assemblies: " + ex.Message);
            }
        }

        private void HandleHotkeys()
        {
            if (_lobbyClient == null)
            {
                return;
            }

            try
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    if (!_hotkeysUnavailableLogged)
                    {
                        _hotkeysUnavailableLogged = true;
                        HostLog.Write("Keyboard.current is not available; lobby hotkeys are disabled until a keyboard is detected.");
                    }

                    return;
                }

                if (keyboard[Key.F6].wasPressedThisFrame)
                {
                    _mirrorHudVisible = !_mirrorHudVisible;
                    if (_mirrorHudVisible)
                    {
                        _panelVisible = false;
                    }

                    HostLog.Write("Mirror HUD visible=" + _mirrorHudVisible + ".");
                }
                else if (keyboard[Key.F8].wasPressedThisFrame)
                {
                    _lobbyClient.DumpLobby();
                }
                else if (keyboard[Key.F7].wasPressedThisFrame)
                {
                    _panelVisible = !_panelVisible;
                    HostLog.Write("In-game control panel visible=" + _panelVisible + ".");
                }
                else if (keyboard[Key.F9].wasPressedThisFrame)
                {
                    _lobbyClient.CreateLobby(4);
                }
                else if (keyboard[Key.F10].wasPressedThisFrame)
                {
                    _lobbyClient.OpenInviteDialog();
                }
                else if (keyboard[Key.F11].wasPressedThisFrame)
                {
                    _lobbyClient.LeaveLobby(_messageTransport);
                }
            }
            catch (Exception ex)
            {
                if (!_hotkeysUnavailableLogged)
                {
                    _hotkeysUnavailableLogged = true;
                    Debug.LogWarning("[DD2SteamMP] Hotkey handling failed: " + ex.Message);
                    HostLog.Write("Hotkey handling failed: " + ex.Message);
                }
            }
        }

        private static void LogControls()
        {
            const string message = "Lobby controls: F6=toggle fullscreen Mirror HUD, F7=toggle compact control panel, F8=dump lobby, F9=create friends-only lobby, F10=open Steam invite overlay, F11=leave lobby. Command file is a debug fallback and supports: host, invite, leave, dump, join <lobbyId>, say <text>, slot, slotauto, pvp, turn, turncurrent, autoturn, skill, target, pass, heroassign, heroclear, heropath, heroready, heroconfirm, storychoice, innbiome, innembark, embarkapply, embarkcontinue, altarcontinue, altarspend, altarreward, confessionchoice, resultscontinue, laircontinue, lairretreat, dialogconfirm, dialogdecline, resync, fullstate, nativeprobe, state, digest, combat. Run/game/map/loadout/story/inn/embark/altar/confession/lair/dialog/store/stagecoach state is mirrored in the F6 HUD; F7 keeps lobby, slot, Arena, and diagnostics controls.";
            Debug.Log("[DD2SteamMP] " + message);
            HostLog.Write(message);
        }

        private static void EnsureArenaBattleModifierPatchInstalled()
        {
            if (_arenaBattleModifierPatchInstalled)
            {
                return;
            }

            lock (ArenaBattleModifierPatchLock)
            {
                if (_arenaBattleModifierPatchInstalled)
                {
                    return;
                }

                try
                {
                    MethodInfo battleModifierOriginal = typeof(BattleModifierCalculation).GetMethod(
                        nameof(BattleModifierCalculation.RollBattleModifier),
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(BattleConfigurationDefinition) },
                        null);
                    MethodInfo battleModifierPrefix = typeof(DD2SteamMultiplayerRunner).GetMethod(
                        nameof(ArenaBattleModifierRollPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo torchGroupOriginal = typeof(TorchManager).GetMethod(
                        nameof(TorchManager.GetActiveTorchLevelGroup),
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    MethodInfo torchGroupPrefix = typeof(DD2SteamMultiplayerRunner).GetMethod(
                        nameof(ArenaTorchLevelGroupPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo runValueGetOriginal = typeof(RunValues).GetMethod(
                        nameof(RunValues.GetValue),
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(RunValueType) },
                        null);
                    MethodInfo runValueGetPrefix = typeof(DD2SteamMultiplayerRunner).GetMethod(
                        nameof(ArenaRunValueGetPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo runValueSetOriginal = typeof(RunValues).GetMethod(
                        nameof(RunValues.SetValue),
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(RunValueType), typeof(float) },
                        null);
                    MethodInfo runValueSetPrefix = typeof(DD2SteamMultiplayerRunner).GetMethod(
                        nameof(ArenaRunValueSetPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo runValueChangeOriginal = typeof(RunValues).GetMethod(
                        nameof(RunValues.ChangeValue),
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(RunValueType), typeof(float), typeof(SourceType) },
                        null);
                    MethodInfo runValueChangePrefix = typeof(DD2SteamMultiplayerRunner).GetMethod(
                        nameof(ArenaRunValueChangePrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo stageCoachItemQtyOriginal = typeof(StageCoach).GetMethod(
                        nameof(StageCoach.GetStageCoachUpgradeItemQty),
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(string) },
                        null);
                    MethodInfo stageCoachItemQtyPrefix = typeof(DD2SteamMultiplayerRunner).GetMethod(
                        nameof(ArenaStageCoachUpgradeItemQtyPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo stageCoachItemTagQtyOriginal = typeof(StageCoach).GetMethod(
                        nameof(StageCoach.GetStageCoachUpgradeItemTagQty),
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(string) },
                        null);
                    MethodInfo stageCoachItemTagQtyPrefix = typeof(DD2SteamMultiplayerRunner).GetMethod(
                        nameof(ArenaStageCoachUpgradeItemTagQtyPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);

                    if (battleModifierOriginal == null || battleModifierPrefix == null ||
                        torchGroupOriginal == null || torchGroupPrefix == null ||
                        runValueGetOriginal == null || runValueGetPrefix == null ||
                        runValueSetOriginal == null || runValueSetPrefix == null ||
                        runValueChangeOriginal == null || runValueChangePrefix == null ||
                        stageCoachItemQtyOriginal == null || stageCoachItemQtyPrefix == null ||
                        stageCoachItemTagQtyOriginal == null || stageCoachItemTagQtyPrefix == null)
                    {
                        throw new MissingMethodException("Could not find one or more arena runtime override patch methods.");
                    }

                    PatchWithHarmonyPrefix(battleModifierOriginal, battleModifierPrefix);
                    PatchWithHarmonyPrefix(torchGroupOriginal, torchGroupPrefix);
                    PatchWithHarmonyPrefix(runValueGetOriginal, runValueGetPrefix);
                    PatchWithHarmonyPrefix(runValueSetOriginal, runValueSetPrefix);
                    PatchWithHarmonyPrefix(runValueChangeOriginal, runValueChangePrefix);
                    PatchWithHarmonyPrefix(stageCoachItemQtyOriginal, stageCoachItemQtyPrefix);
                    PatchWithHarmonyPrefix(stageCoachItemTagQtyOriginal, stageCoachItemTagQtyPrefix);
                    _arenaBattleModifierPatchInstalled = true;
                    HostLog.Write("[arena] Arena runtime override patches installed.");
                }
                catch (Exception ex)
                {
                    if (!_arenaBattleModifierPatchFailedLogged)
                    {
                        _arenaBattleModifierPatchFailedLogged = true;
                        HostLog.Write("[arena] Failed to install arena runtime override patches: " + ex);
                    }
                }
            }
        }

        private static void PatchWithHarmonyPrefix(MethodInfo original, MethodInfo prefix)
        {
            Assembly harmonyAssembly = LoadHarmonyAssemblyForArenaPatch();
            Type harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", throwOnError: true);
            Type harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", throwOnError: true);

            ConstructorInfo harmonyCtor = harmonyType.GetConstructor(new[] { typeof(string) });
            ConstructorInfo harmonyMethodCtor = harmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) });
            if (harmonyCtor == null || harmonyMethodCtor == null)
            {
                throw new MissingMethodException("Could not find required Harmony constructors.");
            }

            _arenaBattleModifierHarmony = harmonyCtor.Invoke(new object[] { "com.superexboom.dd2steammultiplayer.host.arena-battle-modifier" });
            object prefixHarmonyMethod = harmonyMethodCtor.Invoke(new object[] { prefix });

            MethodInfo patchMethod = FindHarmonyPatchMethod(harmonyType, harmonyMethodType, "prefix");
            ParameterInfo[] parameters = patchMethod.GetParameters();
            object[] args = new object[parameters.Length];
            args[0] = original;

            for (int i = 1; i < parameters.Length; i++)
            {
                if (string.Equals(parameters[i].Name, "prefix", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = prefixHarmonyMethod;
                }
                else
                {
                    args[i] = null;
                }
            }

            patchMethod.Invoke(_arenaBattleModifierHarmony, args);
        }

        private static Assembly LoadHarmonyAssemblyForArenaPatch()
        {
            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
            {
                return loaded;
            }

            string harmonyPath = Path.Combine(HostPaths.GameRoot, "BepInEx", "core", "0Harmony.dll");
            if (!File.Exists(harmonyPath))
            {
                throw new FileNotFoundException("0Harmony.dll was not found in BepInEx core.", harmonyPath);
            }

            return Assembly.LoadFrom(harmonyPath);
        }

        private static MethodInfo FindHarmonyPatchMethod(Type harmonyType, Type harmonyMethodType, string patchParameterName)
        {
            foreach (MethodInfo method in harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!string.Equals(method.Name, "Patch", StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2 || parameters[0].ParameterType != typeof(MethodBase))
                {
                    continue;
                }

                bool hasPatchParameter = parameters.Any(parameter =>
                    string.Equals(parameter.Name, patchParameterName, StringComparison.OrdinalIgnoreCase) &&
                    parameter.ParameterType == harmonyMethodType);
                if (hasPatchParameter)
                {
                    return method;
                }
            }

            throw new MissingMethodException("Could not find Harmony.Patch MethodBase overload with " + patchParameterName + ".");
        }

        private static bool ArenaBattleModifierRollPrefix(
            BattleConfigurationDefinition battleConfiguration,
            ref BattleModifierDefinition __result)
        {
            DD2SteamMultiplayerRunner runner = _activeArenaRunner;
            if (runner == null || !runner.ShouldOverrideArenaBattleModifierRoll())
            {
                return true;
            }

            __result = runner.ResolveArenaBattleModifierOverride(battleConfiguration);
            runner.LogArenaBattleModifierOverride(battleConfiguration, __result);
            return false;
        }

        private bool ShouldOverrideArenaBattleModifierRoll()
        {
            return _arenaBattleModifierOverrideArmed &&
                (_arenaPendingLaunch ||
                 _arenaDebugControlsSuppressed ||
                 _arenaResultBypassArmed ||
                 _arenaWaitingForNextBattle ||
                 _arenaPostBattleMainMenuReturnPending ||
                 _arenaPostBattleMainMenuReturnRequested);
        }

        private static bool ArenaTorchLevelGroupPrefix(ref TorchLevelGroupDefinition __result)
        {
            DD2SteamMultiplayerRunner runner = _activeArenaRunner;
            if (runner == null || !runner.ShouldOverrideArenaTorchRuntime())
            {
                return true;
            }

            __result = runner.ResolveArenaTorchLevelGroupOverride();
            runner.LogArenaTorchOverride(__result);
            return false;
        }

        private static bool ArenaRunValueGetPrefix(RunValueType runValueType, ref float __result)
        {
            DD2SteamMultiplayerRunner runner = _activeArenaRunner;
            if (runner == null ||
                runValueType != RunValueType.TORCH ||
                !runner.ShouldOverrideArenaTorchRuntime())
            {
                return true;
            }

            __result = runner.GetEffectiveArenaTorchValue();
            return false;
        }

        private static bool ArenaRunValueSetPrefix(RunValueType runValueType, float value)
        {
            DD2SteamMultiplayerRunner runner = _activeArenaRunner;
            if (runner == null ||
                runValueType != RunValueType.TORCH ||
                !runner.ShouldOverrideArenaTorchRuntime())
            {
                return true;
            }

            return false;
        }

        private static bool ArenaRunValueChangePrefix(RunValueType runValueType, float changeValue, SourceType sourceType)
        {
            DD2SteamMultiplayerRunner runner = _activeArenaRunner;
            if (runner == null ||
                runValueType != RunValueType.TORCH ||
                !runner.ShouldOverrideArenaTorchRuntime())
            {
                return true;
            }

            return false;
        }

        private static bool ArenaStageCoachUpgradeItemQtyPrefix(string itemId, ref int __result)
        {
            DD2SteamMultiplayerRunner runner = _activeArenaRunner;
            if (runner == null || !runner.ShouldOverrideArenaTorchRuntime())
            {
                return true;
            }

            string id = (itemId ?? string.Empty).Trim();
            if (!runner.IsKnownArenaTorchItemId(id))
            {
                return true;
            }

            __result = runner.IsSelectedArenaTorchItemId(id) ? 1 : 0;
            return false;
        }

        private static bool ArenaStageCoachUpgradeItemTagQtyPrefix(string itemTag, ref int __result)
        {
            DD2SteamMultiplayerRunner runner = _activeArenaRunner;
            if (runner == null || !runner.ShouldOverrideArenaTorchRuntime())
            {
                return true;
            }

            string tag = (itemTag ?? string.Empty).Trim();
            if (!runner.IsKnownArenaTorchItemTag(tag))
            {
                return true;
            }

            __result = runner.SelectedArenaTorchHasTag(tag) ? 1 : 0;
            return false;
        }

        private bool ShouldOverrideArenaTorchRuntime()
        {
            return _arenaTorchOverrideArmed &&
                !HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots) &&
                (_arenaPendingLaunch ||
                 _arenaDebugControlsSuppressed ||
                 _arenaResultBypassArmed ||
                 _arenaWaitingForNextBattle ||
                 _arenaPostBattleMainMenuReturnPending ||
                 _arenaPostBattleMainMenuReturnRequested);
        }

        private void ReleaseArenaBattleModifierOverride(string reason)
        {
            if (_arenaBattleModifierOverrideArmed)
            {
                HostLog.Write("[arena] Battle advantage override released: " + (reason ?? "[none]") + ".");
            }

            _arenaBattleModifierOverrideArmed = false;
            _arenaBattleModifierOverrideLogKey = null;
            ClearArenaBattleModifierEditorPrefs();
            ReleaseArenaTorchOverride(reason);
        }

        private void ArmArenaTorchOverrideForLaunch()
        {
            bool heroVsHero = HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots);
            _arenaTorchOverrideArmed = !heroVsHero;
            _arenaTorchOverrideLogKey = null;
            if (!_arenaTorchOverrideArmed)
            {
                DetachArenaTorchItemData("hero-vs-hero");
                RefreshArenaTorchManagerRuntime("hero-vs-hero disabled");
                return;
            }

            ApplyArenaTorchRuntimeOverride("launch");
        }

        private void ReleaseArenaTorchOverride(string reason)
        {
            bool wasArmed = _arenaTorchOverrideArmed;
            _arenaTorchOverrideArmed = false;
            _arenaTorchOverrideLogKey = null;
            DetachArenaTorchItemData(reason ?? "release");
            if (wasArmed)
            {
                RefreshArenaTorchManagerRuntime("release");
                HostLog.Write("[arena] Torch override released: " + (reason ?? "[none]") + ".");
            }
        }

        private void ApplyArenaTorchRuntimeOverride(string reason)
        {
            if (!_arenaTorchOverrideArmed)
            {
                return;
            }

            AttachArenaTorchItemData(reason);
            RefreshArenaTorchManagerRuntime(reason);
        }

        private void AttachArenaTorchItemData(string reason)
        {
            ItemDefinition item = GetSelectedArenaTorchItemDefinition();
            DataContainer dataContainer = item == null ? null : item.GetDataContainer();
            string itemId = item == null ? string.Empty : item.m_id;
            if (dataContainer == null || string.IsNullOrWhiteSpace(itemId))
            {
                DetachArenaTorchItemData(reason ?? "no selected flame item");
                return;
            }

            if (_arenaTorchAttachedDataContainer == dataContainer &&
                string.Equals(_arenaTorchAttachedItemId, itemId, StringComparison.Ordinal))
            {
                return;
            }

            DetachArenaTorchItemData("switch flame item");
            try
            {
                if (Singleton<GameTypeMgr>.HasInstance())
                {
                    GameTypeMgr gameTypeMgr = Singleton<GameTypeMgr>.Instance;
                    if (gameTypeMgr.RunDataManager != null)
                    {
                        gameTypeMgr.RunDataManager.RunData.AddChild(dataContainer);
                        _arenaTorchAttachedToRunData = true;
                    }

                    if (gameTypeMgr.RosterManager != null &&
                        gameTypeMgr.RosterManager.PartyPerActorDataContainer != null)
                    {
                        gameTypeMgr.RosterManager.PartyPerActorDataContainer.AddChild(dataContainer);
                        _arenaTorchAttachedToPartyData = true;
                    }
                }

                _arenaTorchAttachedDataContainer = dataContainer;
                _arenaTorchAttachedItemId = itemId;
                HostLog.Write("[arena] Torch item data attached: item=" + itemId +
                    ", runData=" + _arenaTorchAttachedToRunData +
                    ", partyData=" + _arenaTorchAttachedToPartyData +
                    ", reason=" + (reason ?? "[none]") + ".");
            }
            catch (Exception ex)
            {
                HostLog.Write("[arena] Failed to attach torch item data " + itemId + ": " + ex.Message);
            }
        }

        private void DetachArenaTorchItemData(string reason)
        {
            if (_arenaTorchAttachedDataContainer == null)
            {
                _arenaTorchAttachedItemId = null;
                _arenaTorchAttachedToRunData = false;
                _arenaTorchAttachedToPartyData = false;
                return;
            }

            DataContainer dataContainer = _arenaTorchAttachedDataContainer;
            string itemId = _arenaTorchAttachedItemId;
            try
            {
                if (Singleton<GameTypeMgr>.HasInstance())
                {
                    GameTypeMgr gameTypeMgr = Singleton<GameTypeMgr>.Instance;
                    if (_arenaTorchAttachedToRunData && gameTypeMgr.RunDataManager != null)
                    {
                        gameTypeMgr.RunDataManager.RunData.RemoveChild(dataContainer);
                    }

                    if (_arenaTorchAttachedToPartyData &&
                        gameTypeMgr.RosterManager != null &&
                        gameTypeMgr.RosterManager.PartyPerActorDataContainer != null)
                    {
                        gameTypeMgr.RosterManager.PartyPerActorDataContainer.RemoveChild(dataContainer);
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("[arena] Failed to detach torch item data " + (itemId ?? "[none]") + ": " + ex.Message);
            }
            finally
            {
                _arenaTorchAttachedDataContainer = null;
                _arenaTorchAttachedItemId = null;
                _arenaTorchAttachedToRunData = false;
                _arenaTorchAttachedToPartyData = false;
            }

            HostLog.Write("[arena] Torch item data detached: item=" + (itemId ?? "[none]") +
                ", reason=" + (reason ?? "[none]") + ".");
        }

        private void RefreshArenaTorchManagerRuntime(string reason)
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.TorchManager == null)
                {
                    return;
                }

                float torchValue = _arenaTorchOverrideArmed
                    ? GetEffectiveArenaTorchValue()
                    : (Singleton<GameTypeMgr>.Instance.RunValues == null
                        ? 100f
                        : Singleton<GameTypeMgr>.Instance.RunValues.GetValue(RunValueType.TORCH));
                MethodInfo updateTorch = typeof(TorchManager).GetMethod(
                    "UpdateTorch",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (updateTorch == null)
                {
                    return;
                }

                updateTorch.Invoke(
                    Singleton<GameTypeMgr>.Instance.TorchManager,
                    new object[] { torchValue, torchValue });
                HostLog.Write("[arena] Torch manager refreshed: value=" +
                    torchValue.ToString("0", CultureInfo.InvariantCulture) +
                    ", profile=" + GetArenaTorchDisplaySummary() +
                    ", reason=" + (reason ?? "[none]") + ".");
            }
            catch (Exception ex)
            {
                HostLog.Write("[arena] Failed to refresh torch manager: " + ex.Message);
            }
        }

        private static void ClearArenaBattleModifierEditorPrefs()
        {
            try
            {
                TextBasedEditorPrefsBaseType.BATTLE_TEST_BATTLE_MODIFIER.ClearValue();
                TextBasedEditorPrefsBaseType.BATTLE_TEST_ROLL_BATTLE_MODIFIER.ClearValue();
            }
            catch
            {
            }
        }

        private BattleModifierDefinition ResolveArenaBattleModifierOverride(BattleConfigurationDefinition battleConfiguration)
        {
            if (HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots))
            {
                return null;
            }

            ArenaBattleAdvantageMode mode = GetEffectiveArenaBattleAdvantageMode();
            if (mode == ArenaBattleAdvantageMode.Specific)
            {
                return TryGetArenaBattleModifierDefinition(_arenaBattleModifierId);
            }

            if (mode == ArenaBattleAdvantageMode.Random)
            {
                return RollArenaBattleModifierIndependentOfRun(battleConfiguration);
            }

            return null;
        }

        private void LogArenaBattleModifierOverride(
            BattleConfigurationDefinition battleConfiguration,
            BattleModifierDefinition result)
        {
            string key =
                (battleConfiguration == null ? "[battle]" : battleConfiguration.m_Id ?? "[battle]") + "|" +
                GetArenaBattleAdvantageModeName(GetEffectiveArenaBattleAdvantageMode()) + "|" +
                (result == null ? "[none]" : result.m_Id ?? "[modifier]");
            if (string.Equals(_arenaBattleModifierOverrideLogKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _arenaBattleModifierOverrideLogKey = key;
            HostLog.Write("[arena] Battle advantage override: battle=" +
                (battleConfiguration == null ? "[battle]" : battleConfiguration.m_Id ?? "[battle]") +
                ", mode=" + GetArenaBattleAdvantageModeName(GetEffectiveArenaBattleAdvantageMode()) +
                ", result=" + (result == null ? "[none]" : result.m_Id ?? "[modifier]") + ".");
        }

        private static void LogNativeSceneProbe(string reason)
        {
            try
            {
                List<string> scenes = new List<string>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    scenes.Add(scene.name +
                        "(loaded=" + scene.isLoaded +
                        ", active=" + (scene == SceneManager.GetActiveScene()) + ")");
                }

                string currentMode = GameModeMgr.CurrentMode == null
                    ? "[null]"
                    : GameModeMgr.CurrentMode.GetName();
                bool hasGameModeMgr = Singleton<GameModeMgr>.HasInstance();
                bool hasCombat = SingletonMonoBehaviour<CombatBhv>.HasInstance(false);
                bool hasCombatUi = SingletonMonoBehaviour<CombatUiBhv>.HasInstance(false);
                bool hasArena = SingletonMonoBehaviour<ArenaBhv>.HasInstance(false);
                string inferredMode = "[unavailable]";
                try
                {
                    GameModeType inferred = GameModeType.DetermineCurrentModeFromLoadedScenes();
                    inferredMode = inferred == null ? "[null]" : inferred.GetName();
                }
                catch (Exception ex)
                {
                    inferredMode = "error:" + ex.Message;
                }

                HostLog.Write("[native-probe] reason=" + (reason ?? "[none]") +
                    ", currentMode=" + currentMode +
                    ", inferredMode=" + inferredMode +
                    ", hasGameModeMgr=" + hasGameModeMgr +
                    ", hasCombat=" + hasCombat +
                    ", hasCombatUi=" + hasCombatUi +
                    ", hasArena=" + hasArena +
                    ", activeScene=" + SceneManager.GetActiveScene().name +
                    ", scenes=" + string.Join(" | ", scenes.ToArray()));
            }
            catch (Exception ex)
            {
                HostLog.Write("[native-probe] failed: " + ex.Message);
            }
        }

        private void OnGUI()
        {
            bool drawHostVoteUi =
                !_panelVisible &&
                !_mirrorHudVisible &&
                !_arenaBattlePresetBrowserVisible &&
                !_arenaHeroSetupVisible &&
                _hostVoteUiCoordinator.HasDrawableVote(_session, _lobbyClient);

            if (!_panelVisible &&
                !_mirrorHudVisible &&
                !_arenaBattlePresetBrowserVisible &&
                !_arenaHeroSetupVisible &&
                !drawHostVoteUi)
            {
                return;
            }

            if (drawHostVoteUi)
            {
                try
                {
                    _hostVoteUiCoordinator.Draw(_session, _lobbyClient, IsChineseUi);
                }
                catch (Exception ex)
                {
                    if (!_hostVoteUiErrorLogged)
                    {
                        _hostVoteUiErrorLogged = true;
                        Debug.LogWarning("[DD2SteamMP] Host vote UI rendering failed: " + ex.Message);
                        HostLog.Write("Host vote UI rendering failed: " + ex.Message);
                    }
                }
            }

            if (_mirrorHudVisible)
            {
                try
                {
                    DrawMirrorHudOverlay();
                }
                catch (Exception ex)
                {
                    if (!_mirrorHudErrorLogged)
                    {
                        _mirrorHudErrorLogged = true;
                        Debug.LogWarning("[DD2SteamMP] Mirror HUD rendering failed: " + ex.Message);
                        HostLog.Write("Mirror HUD rendering failed: " + ex.Message);
                    }
                }
            }

            if (_panelVisible)
            {
                try
                {
                    EnsurePanelStyles();
                    _hoverTooltipTitle = null;
                    _hoverTooltipBody = null;
                    _hoverTooltipHasScreenPosition = false;
                    ClampPanelRectToScreen();
                    _panelResizeChangedThisFrame = false;
                    Rect returnedRect = GUI.Window(GetInstanceID(), _panelRect, DrawControlPanel, string.Empty, _panelWindowStyle);
                    if (_panelResizing || _panelResizeChangedThisFrame)
                    {
                        _panelRect.x = returnedRect.x;
                        _panelRect.y = returnedRect.y;
                    }
                    else
                    {
                        _panelRect = returnedRect;
                    }

                    DrawFloatingTooltip();
                }
                catch (Exception ex)
                {
                    if (!_panelErrorLogged)
                    {
                        _panelErrorLogged = true;
                        Debug.LogWarning("[DD2SteamMP] Control panel rendering failed: " + ex.Message);
                        HostLog.Write("Control panel rendering failed: " + ex.Message);
                    }
                }
            }

            if (_arenaBattlePresetBrowserVisible)
            {
                try
                {
                    EnsurePanelStyles();
                    _hoverTooltipTitle = null;
                    _hoverTooltipBody = null;
                    _hoverTooltipHasScreenPosition = false;
                    ClampArenaBattlePresetBrowserRectToScreen();
                    _arenaBattlePresetBrowserResizeChangedThisFrame = false;
                    Rect returnedRect = GUI.Window(
                        GetInstanceID() + 2048,
                        _arenaBattlePresetBrowserRect,
                        DrawArenaBattlePresetBrowserWindow,
                        string.Empty,
                        _panelWindowStyle);
                    if (_arenaBattlePresetBrowserResizing || _arenaBattlePresetBrowserResizeChangedThisFrame)
                    {
                        _arenaBattlePresetBrowserRect.x = returnedRect.x;
                        _arenaBattlePresetBrowserRect.y = returnedRect.y;
                    }
                    else
                    {
                        _arenaBattlePresetBrowserRect = returnedRect;
                    }

                    DrawFloatingTooltip();
                }
                catch (Exception ex)
                {
                    _arenaBattlePresetBrowserVisible = false;
                    HostLog.Write("[arena] Battle preset browser rendering failed: " + ex.Message);
                }
            }

            if (_arenaHeroSetupVisible)
            {
                try
                {
                    EnsurePanelStyles();
                    _hoverTooltipTitle = null;
                    _hoverTooltipBody = null;
                    _hoverTooltipHasScreenPosition = false;
                    ClampArenaHeroSetupRectToScreen();
                    _arenaHeroSetupResizeChangedThisFrame = false;
                    Rect returnedRect = GUI.Window(
                        GetInstanceID() + 3072,
                        _arenaHeroSetupRect,
                        DrawArenaHeroSetupWindow,
                        string.Empty,
                        _panelWindowStyle);
                    if (_arenaHeroSetupResizing || _arenaHeroSetupResizeChangedThisFrame)
                    {
                        _arenaHeroSetupRect.x = returnedRect.x;
                        _arenaHeroSetupRect.y = returnedRect.y;
                    }
                    else
                    {
                        _arenaHeroSetupRect = returnedRect;
                    }

                    DrawFloatingTooltip();
                }
                catch (Exception ex)
                {
                    _arenaHeroSetupVisible = false;
                    HostLog.Write("[arena] Hero setup window rendering failed: " + ex.Message);
                }
            }
        }

        private void DrawMirrorHudOverlay()
        {
            EnsurePanelStyles();

            Color oldColor = GUI.color;
            Color oldContentColor = GUI.contentColor;
            Color oldBackgroundColor = GUI.backgroundColor;
            int oldLabelFontSize = GUI.skin.label.fontSize;
            int oldButtonFontSize = GUI.skin.button.fontSize;
            int oldBoxFontSize = GUI.skin.box.fontSize;

            try
            {
                GUI.color = Color.white;
                GUI.contentColor = PanelTextColor;
                GUI.backgroundColor = Color.white;
                GUI.skin.label.fontSize = 15;
                GUI.skin.button.fontSize = 14;
                GUI.skin.box.fontSize = 14;
                _hoverTooltipTitle = null;
                _hoverTooltipBody = null;
                _hoverTooltipHasScreenPosition = false;

                Rect screen = new Rect(0f, 0f, Screen.width, Screen.height);
                DrawSolidRect(screen, HudBackgroundColor);
                DrawMirrorHudTopBar(screen);

                CombatSnapshotPayload combat;
                if (_session != null &&
                    _session.TryGetLatestCombatSnapshot(out combat) &&
                    combat != null &&
                    (combat.PartyInBattle || (combat.Actors != null && combat.Actors.Count > 0)))
                {
                    DrawMirrorHudCombat(screen, combat);
                }
                else
                {
                    DrawMirrorHudNonCombat(screen);
                }

                DrawFloatingTooltip();
            }
            finally
            {
                GUI.color = oldColor;
                GUI.contentColor = oldContentColor;
                GUI.backgroundColor = oldBackgroundColor;
                GUI.skin.label.fontSize = oldLabelFontSize;
                GUI.skin.button.fontSize = oldButtonFontSize;
                GUI.skin.box.fontSize = oldBoxFontSize;
            }
        }

        private void DrawMirrorHudTopBar(Rect screen)
        {
            Rect top = new Rect(18f, 14f, screen.width - 36f, 54f);
            DrawSolidRect(top, HudPanelColor);

            GUIStyle titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
            };
            titleStyle.normal.textColor = Color.white;

            GUIStyle statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
            };
            statusStyle.normal.textColor = PanelMutedTextColor;

            GUI.Label(new Rect(top.x + 16f, top.y + 4f, 360f, 26f), Ui("DD2 Steam MP Mirror", "DD2 Steam MP 镜像"), titleStyle);
            GUI.Label(new Rect(top.x + 16f, top.y + 30f, Mathf.Max(280f, top.width - 552f), 20f), GetLobbyPanelStatus() + " | " + GetPvpPanelStatus() + " | " + GetVersionPanelStatus(), statusStyle);

            float buttonY = top.y + 12f;
            if (GUI.Button(new Rect(top.xMax - 618f, buttonY, 82f, 30f), IsChineseUi ? "EN" : "中文"))
            {
                ToggleUiLanguage();
            }

            if (GUI.Button(new Rect(top.xMax - 528f, buttonY, 104f, 30f), _mirrorHudMapExpanded ? Ui("Hide Map", "隐藏地图") : Ui("Show Map", "显示地图")))
            {
                _mirrorHudMapExpanded = !_mirrorHudMapExpanded;
            }

            if (GUI.Button(new Rect(top.xMax - 416f, buttonY, 128f, 30f), Ui("Native Probe", "原生探测")))
            {
                LogNativeSceneProbe("hud");
            }

            if (GUI.Button(new Rect(top.xMax - 280f, buttonY, 132f, 30f), Ui("F7 Controls", "F7 控制")))
            {
                _mirrorHudVisible = false;
                _panelVisible = true;
                _panelTab = PanelTab.Home;
            }

            if (GUI.Button(new Rect(top.xMax - 140f, buttonY, 118f, 30f), Ui("Close F6", "关闭 F6")))
            {
                _mirrorHudVisible = false;
            }
        }

        private void DrawMirrorHudCombat(Rect screen, CombatSnapshotPayload snapshot)
        {
            _mirrorHudMapExpanded = false;
            IList<ActorSnapshotPayload> actors = snapshot.Actors ?? Array.Empty<ActorSnapshotPayload>();
            TurnPromptPayload prompt = null;
            HeroSlotAssignmentPayload owner = null;
            string skillId = null;
            string targetGuid = null;
            bool isPass = false;
            bool hasPendingTurn = _session != null &&
                _session.TryGetPendingTurn(out prompt, out owner, out skillId, out targetGuid, out isPass) &&
                prompt != null;
            if (hasPendingTurn)
            {
                SyncPanelPendingKey(prompt);
            }

            TurnSkillOptionPayload selectedPromptSkill = prompt == null
                ? null
                : FindTurnSkillOption(prompt, !string.IsNullOrWhiteSpace(_panelSkillId) ? _panelSkillId : skillId);
            bool canChooseTurnInput = IsLocalTurnOwner(owner);
            bool canChooseTarget = canChooseTurnInput && prompt != null && selectedPromptSkill != null;
            IList<string> validTargetGuids = BuildMirrorValidTargetGuids(snapshot.SelectedSkill, selectedPromptSkill);
            DamageMeterSnapshotPayload damageMeterSnapshot = null;
            if (_session != null)
            {
                _session.TryGetLatestDamageMeterSnapshot(out damageMeterSnapshot);
            }

            float width = Mathf.Max(MirrorHudMinWidth, screen.width);
            float height = Mathf.Max(MirrorHudMinHeight, screen.height);
            float sideWidth = Mathf.Max(260f, (width - 420f) * 0.36f);
            Rect skillRect = new Rect(24f, height - 140f, width - 48f, 112f);
            float mainHeight = Mathf.Max(250f, skillRect.y - 104f);
            Rect partyRect = new Rect(24f, 88f, sideWidth, mainHeight);
            Rect enemyRect = new Rect(width - sideWidth - 24f, 88f, sideWidth, mainHeight);
            Rect centerRect = new Rect(partyRect.xMax + 18f, 88f, enemyRect.x - partyRect.xMax - 36f, height - 228f);
            centerRect.height = mainHeight;

            DrawMirrorHudActorSide(
                partyRect,
                "Party",
                actors.Where(actor => actor != null && actor.TeamIndex == 0)
                    .OrderBy(actor => actor.TeamPosition)
                    .ThenBy(actor => actor.ActorGuid)
                    .ToList(),
                snapshot.CurrentActorGuid,
                validTargetGuids,
                targetGuid,
                canChooseTarget,
                prompt);

            DrawMirrorHudCenter(centerRect, snapshot, prompt, owner, skillId, targetGuid, isPass, damageMeterSnapshot);

            DrawMirrorHudActorSide(
                enemyRect,
                "Enemies",
                actors.Where(actor => actor != null && actor.TeamIndex != 0)
                    .OrderBy(actor => actor.TeamIndex)
                    .ThenBy(actor => actor.TeamPosition)
                    .ThenBy(actor => actor.ActorGuid)
                    .ToList(),
                snapshot.CurrentActorGuid,
                validTargetGuids,
                targetGuid,
                canChooseTarget,
                prompt);

            DrawMirrorHudSkillBar(skillRect, prompt, owner, skillId, selectedPromptSkill, canChooseTurnInput);
        }

        private void DrawMirrorHudCenter(
            Rect area,
            CombatSnapshotPayload snapshot,
            TurnPromptPayload prompt,
            HeroSlotAssignmentPayload owner,
            string skillId,
            string targetGuid,
            bool isPass,
            DamageMeterSnapshotPayload damageMeterSnapshot)
        {
            DrawSolidRect(area, HudPanelColor);
            GUIStyle heading = CreateHudLabelStyle(18, FontStyle.Bold, Color.white, TextAnchor.UpperCenter);
            GUIStyle body = CreateHudLabelStyle(13, FontStyle.Normal, PanelTextColor, TextAnchor.UpperCenter);
            GUIStyle muted = CreateHudLabelStyle(12, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperCenter);

            GUI.Label(new Rect(area.x + 12f, area.y + 12f, area.width - 24f, 26f), "Combat", heading);
            GUI.Label(new Rect(area.x + 12f, area.y + 42f, area.width - 24f, 22f),
                "state=" + (snapshot.BattleState ?? "[none]") +
                " | r" + snapshot.Round + "/t" + snapshot.Turn,
                body);
            GUI.Label(new Rect(area.x + 12f, area.y + 66f, area.width - 24f, 22f),
                "current=" + (snapshot.CurrentActorName ?? snapshot.CurrentActorGuid ?? "[none]"),
                body);

            string selectedSkill = snapshot.SelectedSkill == null
                ? "none"
                : CleanInline(snapshot.SelectedSkill.DisplayName ?? snapshot.SelectedSkill.SkillId ?? "[skill]") +
                    " targets=" + (snapshot.SelectedSkill.ValidTargets == null ? 0 : snapshot.SelectedSkill.ValidTargets.Count);
            GUI.Label(new Rect(area.x + 12f, area.y + 92f, area.width - 24f, 22f), "selected skill: " + selectedSkill, muted);

            string ownerText = owner == null ? "unassigned" : owner.Name ?? "[owner]";
            string inputText = prompt == null
                ? "pending input: none"
                : "pending: role=" + (prompt.ControlRole ?? "hero") +
                    " team=" + prompt.TeamIndex + ":" + prompt.TeamPosition +
                    " slot=" + prompt.HeroSlot +
                    " owner=" + ownerText +
                    " skill=" + (skillId ?? "[none]") +
                    " target=" + (targetGuid ?? "[none]") +
                    " pass=" + isPass;
            GUI.Label(new Rect(area.x + 12f, area.y + 116f, area.width - 24f, 38f), inputText, body);

            float turnOrderHeight = Mathf.Clamp(area.height * 0.22f, 78f, 116f);
            Rect logRect = new Rect(area.x + 12f, area.y + 164f, area.width - 24f, area.height - turnOrderHeight - 176f);
            DrawDamageMeterCombatLog(logRect, damageMeterSnapshot);

            IList<CombatTurnOrderEntryPayload> turnOrder = snapshot.TurnOrder ?? Array.Empty<CombatTurnOrderEntryPayload>();
            Rect turnRect = new Rect(area.x + 12f, area.yMax - turnOrderHeight - 8f, area.width - 24f, turnOrderHeight);
            GUI.Label(new Rect(turnRect.x, turnRect.y, turnRect.width, 22f), "Turn Order", CreateHudLabelStyle(15, FontStyle.Bold, Color.white, TextAnchor.UpperCenter));
            float y = turnRect.y + 24f;
            foreach (CombatTurnOrderEntryPayload entry in turnOrder.OrderBy(entry => entry.Index).Take(4))
            {
                GUI.Label(new Rect(turnRect.x + 6f, y, turnRect.width - 12f, 18f), FormatCombatTurnOrderEntry(entry), muted);
                y += 19f;
            }
        }

        private void DrawDamageMeterCombatLog(Rect area, DamageMeterSnapshotPayload snapshot)
        {
            if (area.height < 64f || area.width < 120f)
            {
                return;
            }

            DrawSolidRect(area, new Color(0.075f, 0.085f, 0.095f, 0.96f));
            GUIStyle title = CreateHudLabelStyle(15, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            GUI.Label(new Rect(area.x + 10f, area.y + 6f, area.width - 20f, 22f), "Combat Log", title);

            Rect body = new Rect(area.x + 8f, area.y + 34f, area.width - 16f, area.height - 42f);
            if (snapshot == null)
            {
                GUI.Label(body, "DamageMeter snapshot not received yet.", CreateHudLabelStyle(12, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperCenter));
                return;
            }

            if (!snapshot.IsAvailable)
            {
                GUI.Label(body,
                    "DamageMeter unavailable: " + (snapshot.UnavailableReason ?? "[unknown]"),
                    CreateHudLabelStyle(12, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperCenter));
                return;
            }

            IList<DamageMeterCombatLogEntryPayload> entries = snapshot.CombatLogEntries ?? Array.Empty<DamageMeterCombatLogEntryPayload>();
            const float rowHeight = 38f;
            const float rowGap = 4f;
            int maxRows = Mathf.Max(1, Mathf.FloorToInt(body.height / (rowHeight + rowGap)));
            List<DamageMeterCombatLogEntryPayload> ordered = entries
                .Where(entry => entry != null)
                .OrderBy(entry => entry.Index)
                .ToList();
            IList<DamageMeterCombatLogEntryPayload> visible = ordered
                .Skip(Math.Max(0, ordered.Count - maxRows))
                .ToList();
            if (visible.Count == 0)
            {
                GUI.Label(body, "No combat log entries yet.", CreateHudLabelStyle(12, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperCenter));
                return;
            }

            float y = body.y;
            foreach (DamageMeterCombatLogEntryPayload entry in visible)
            {
                Rect row = new Rect(body.x, y, body.width, rowHeight);
                DrawDamageMeterCombatLogRow(row, entry);
                y += rowHeight + rowGap;
                if (y > body.yMax - rowHeight)
                {
                    break;
                }
            }
        }

        private void DrawDamageMeterCombatLogRow(Rect row, DamageMeterCombatLogEntryPayload entry)
        {
            if (entry == null)
            {
                return;
            }

            if (string.Equals(entry.EntryType, "round", StringComparison.OrdinalIgnoreCase))
            {
                DrawSolidRect(row, new Color(0.12f, 0.13f, 0.15f, 0.96f));
                GUI.Label(row, "Round " + entry.Round, CreateHudLabelStyle(12, FontStyle.Bold, PanelMutedTextColor, TextAnchor.MiddleCenter));
                return;
            }

            DrawSolidRect(row, new Color(0.11f, 0.12f, 0.135f, 0.92f));
            Rect chip = new Rect(row.x + 5f, row.y + 7f, 62f, 24f);
            DrawSolidRect(chip, GetDamageMeterActionColor(entry.ActionType));
            GUI.Label(chip, FormatDamageMeterAction(entry), CreateHudLabelStyle(10, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter));

            GUIStyle main = CreateHudLabelStyle(11, FontStyle.Bold, PanelTextColor, TextAnchor.UpperLeft);
            main.wordWrap = false;
            main.clipping = TextClipping.Clip;
            GUIStyle sub = CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperLeft);
            sub.wordWrap = false;
            sub.clipping = TextClipping.Clip;

            float textX = chip.xMax + 7f;
            float textWidth = row.xMax - textX - 5f;
            string source = CleanInline(entry.SourceName);
            string target = CleanInline(entry.TargetName);
            string mainText = string.IsNullOrWhiteSpace(source)
                ? target
                : source + " -> " + (string.IsNullOrWhiteSpace(target) ? "?" : target);
            GUI.Label(new Rect(textX, row.y + 5f, textWidth, 17f), mainText, main);

            string info = BuildDamageMeterLogInfo(entry);
            if (!string.IsNullOrWhiteSpace(info))
            {
                GUI.Label(new Rect(textX, row.y + 21f, textWidth, 15f), info, sub);
            }

            RegisterTooltip(row, FormatDamageMeterAction(entry), BuildDamageMeterLogTooltip(entry));
        }

        private static Color GetDamageMeterActionColor(string actionType)
        {
            switch ((actionType ?? string.Empty).ToUpperInvariant())
            {
                case "CRIT":
                    return new Color(0.75f, 0.42f, 0.12f, 1f);
                case "DMG":
                    return new Color(0.62f, 0.16f, 0.13f, 1f);
                case "DOT":
                    return new Color(0.48f, 0.18f, 0.20f, 1f);
                case "HEAL":
                    return new Color(0.18f, 0.46f, 0.26f, 1f);
                case "STRESS":
                    return new Color(0.48f, 0.27f, 0.68f, 1f);
                case "KILL":
                case "DEATH":
                    return new Color(0.18f, 0.18f, 0.20f, 1f);
                default:
                    return new Color(0.23f, 0.27f, 0.30f, 1f);
            }
        }

        private static string FormatDamageMeterAction(DamageMeterCombatLogEntryPayload entry)
        {
            string action = (entry == null ? null : entry.ActionType) ?? string.Empty;
            switch (action.ToUpperInvariant())
            {
                case "CRIT":
                    return "CRIT " + entry.Value.ToString("0", CultureInfo.InvariantCulture);
                case "DMG":
                    return "-" + entry.Value.ToString("0", CultureInfo.InvariantCulture);
                case "DOT":
                    return (string.IsNullOrWhiteSpace(entry.DotType) ? "DOT" : CleanInline(entry.DotType)) +
                        " " + entry.Value.ToString("0", CultureInfo.InvariantCulture);
                case "HEAL":
                    return "+" + entry.Value.ToString("0", CultureInfo.InvariantCulture);
                case "STRESS":
                    return "ST " + entry.Value.ToString("0.#", CultureInfo.InvariantCulture);
                case "KILL":
                case "DEATH":
                    return action.ToUpperInvariant();
                default:
                    return string.IsNullOrWhiteSpace(action) ? "LOG" : TrimPanelText(action.ToUpperInvariant(), 8);
            }
        }

        private static string BuildDamageMeterLogInfo(DamageMeterCombatLogEntryPayload entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            List<string> parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(entry.SkillId))
            {
                parts.Add(TrimPanelText(CleanInline(entry.SkillId), 24));
            }

            if (!string.IsNullOrWhiteSpace(entry.Extra))
            {
                parts.Add(CleanInline(entry.Extra));
            }

            if (entry.OverkillDamage > 0.5f)
            {
                parts.Add("OVK " + entry.OverkillDamage.ToString("0", CultureInfo.InvariantCulture));
            }

            return string.Join("  ", parts.ToArray());
        }

        private static string BuildDamageMeterLogTooltip(DamageMeterCombatLogEntryPayload entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>
            {
                "Round: " + entry.Round,
                "Source: " + (CleanInline(entry.SourceName) ?? string.Empty),
                "Target: " + (CleanInline(entry.TargetName) ?? string.Empty),
            };

            if (!string.IsNullOrWhiteSpace(entry.SkillId))
            {
                lines.Add("Skill: " + entry.SkillId);
            }

            if (!string.IsNullOrWhiteSpace(entry.Extra))
            {
                lines.Add("Extra: " + CleanInline(entry.Extra));
            }

            return string.Join("\n", lines.ToArray());
        }

        private void DrawMirrorHudActorSide(
            Rect area,
            string title,
            IList<ActorSnapshotPayload> actors,
            string currentActorGuid,
            IList<string> validTargetGuids,
            string selectedTargetGuid,
            bool canChooseTarget,
            TurnPromptPayload prompt)
        {
            DrawSolidRect(area, HudPanelColor);
            GUI.Label(new Rect(area.x + 12f, area.y + 10f, area.width - 24f, 28f), title, CreateHudLabelStyle(18, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft));

            if (actors == null || actors.Count == 0)
            {
                GUI.Label(new Rect(area.x + 12f, area.y + 46f, area.width - 24f, 28f), "none");
                return;
            }

            float y = area.y + 48f;
            float cardHeight = Mathf.Min(126f, Mathf.Max(94f, (area.height - 58f) / Mathf.Max(actors.Count, 1) - 8f));
            foreach (ActorSnapshotPayload actor in actors)
            {
                Rect card = new Rect(area.x + 10f, y, area.width - 20f, cardHeight);
                DrawMirrorHudActorCard(card, actor, currentActorGuid, validTargetGuids, selectedTargetGuid, canChooseTarget, prompt);
                y += cardHeight + 8f;
            }
        }

        private void DrawMirrorHudActorCard(
            Rect card,
            ActorSnapshotPayload actor,
            string currentActorGuid,
            IList<string> validTargetGuids,
            string selectedTargetGuid,
            bool canChooseTarget,
            TurnPromptPayload prompt)
        {
            bool current = !string.IsNullOrWhiteSpace(currentActorGuid) &&
                string.Equals(actor.ActorGuid, currentActorGuid, StringComparison.Ordinal);
            bool validTarget = validTargetGuids != null &&
                validTargetGuids.Any(guid => string.Equals(guid, actor.ActorGuid, StringComparison.Ordinal));
            bool selectedTarget = !string.IsNullOrWhiteSpace(selectedTargetGuid) &&
                string.Equals(actor.ActorGuid, selectedTargetGuid, StringComparison.Ordinal);

            Color cardColor = current
                ? HudCurrentCardColor
                : validTarget ? HudTargetCardColor : HudCardColor;
            if (!actor.IsLiving)
            {
                cardColor = HudHostOnlyCardColor;
            }

            DrawSolidRect(card, cardColor);
            GUIStyle title = CreateHudLabelStyle(15, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            string markers =
                (current ? "> " : string.Empty) +
                (validTarget ? "[target] " : string.Empty) +
                (selectedTarget ? "[chosen] " : string.Empty);

            Sprite portrait = GetActorPortraitSprite(actor.ActorDataId);
            float contentX = card.x + 10f;
            float contentWidth = card.width - 20f;
            if (portrait != null)
            {
                Rect portraitRect = new Rect(card.x + 10f, card.y + 10f, 54f, 54f);
                DrawSolidRect(portraitRect, new Color(0.04f, 0.045f, 0.05f, 1f));
                DrawPortraitSprite(portraitRect, portrait);
                contentX += 64f;
                contentWidth -= 64f;
            }

            string deathDoor = actor.IsDeathsDoor ? " [Death's Door]" : string.Empty;
            GUI.Label(new Rect(contentX, card.y + 8f, contentWidth, 24f),
                markers + FormatMirrorActorName(actor) + deathDoor + " t" + actor.TeamIndex + "p" + actor.TeamPosition,
                title);

            DrawHudStatBar(new Rect(contentX, card.y + 38f, contentWidth, 14f), actor.Health, actor.MaxHealth, HudHealthColor, "HP " + actor.Health + "/" + actor.MaxHealth);
            DrawHudStatBar(new Rect(contentX, card.y + 58f, contentWidth, 14f), actor.Stress, actor.StressMax, HudStressColor, "ST " + actor.Stress + "/" + actor.StressMax);

            Rect statusRect = new Rect(contentX, card.y + 78f, Math.Max(40f, contentWidth - 110f), 24f);
            DrawActorStatusIconStrip(statusRect, actor);
            RegisterTooltip(card, CleanInline(FormatMirrorActorName(actor)), BuildActorStatusTooltip(actor));

            if (canChooseTarget && validTarget && prompt != null)
            {
                if (GUI.Button(new Rect(card.xMax - 118f, card.yMax - 34f, 106f, 26f), selectedTarget ? "Selected" : "Target"))
                {
                    _panelTargetGuid = actor.ActorGuid ?? string.Empty;
                    _session.ChooseTarget(prompt.HeroSlot, prompt.ActorGuid, _panelTargetGuid);
                }
            }
        }

        private void DrawMirrorHudSkillBar(
            Rect area,
            TurnPromptPayload prompt,
            HeroSlotAssignmentPayload owner,
            string acceptedSkillId,
            TurnSkillOptionPayload selectedPromptSkill,
            bool canChooseTurnInput)
        {
            DrawSolidRect(area, HudPanelColor);
            string turnTitle = prompt == null
                ? "Turn Controls"
                : "Turn Controls - " + (prompt.ControlRole ?? "hero") + " team " + prompt.TeamIndex + ":" + prompt.TeamPosition;
            GUI.Label(new Rect(area.x + 14f, area.y + 8f, area.width - 28f, 24f), turnTitle, CreateHudLabelStyle(17, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft));

            if (prompt == null)
            {
                GUI.Label(new Rect(area.x + 14f, area.y + 40f, area.width - 28f, 28f), "No pending turn input.");
                return;
            }

            string ownerText = owner == null ? "unassigned" : owner.Name ?? "[owner]";
            if (!canChooseTurnInput)
            {
                GUI.Label(new Rect(area.x + 14f, area.y + 40f, area.width - 28f, 28f), "Waiting for " + ownerText + ".");
                return;
            }

            IList<TurnSkillOptionPayload> options = prompt.SkillOptions ?? Array.Empty<TurnSkillOptionPayload>();
            float buttonY = area.y + 38f;
            float passWidth = 84f;
            float spacing = 8f;
            float buttonWidth = options.Count == 0
                ? 140f
                : Mathf.Clamp((area.width - 42f - passWidth - spacing * options.Count) / Math.Max(options.Count, 1), 120f, 220f);
            float x = area.x + 14f;
            GUIStyle skillLabelStyle = CreateHudLabelStyle(12, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            GUIStyle skillIdStyle = CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.MiddleLeft);
            for (int i = 0; i < options.Count; i++)
            {
                TurnSkillOptionPayload option = options[i];
                bool selected = selectedPromptSkill != null && string.Equals(selectedPromptSkill.SkillId, option.SkillId, StringComparison.Ordinal);
                Rect buttonRect = new Rect(x, buttonY, buttonWidth, 48f);
                Color oldBackground = GUI.backgroundColor;
                GUI.backgroundColor = selected ? new Color(0.42f, 0.56f, 0.45f, 1f) : Color.white;
                bool clicked = GUI.Button(buttonRect, GUIContent.none);
                GUI.backgroundColor = oldBackground;

                Sprite skillSprite = GetSkillSprite(option.SkillId);
                float labelX = buttonRect.x + 9f;
                if (skillSprite != null)
                {
                    Rect iconRect = new Rect(buttonRect.x + 8f, buttonRect.y + 7f, 34f, 34f);
                    DrawSprite(iconRect, skillSprite);
                    labelX = iconRect.xMax + 7f;
                }

                string displayName = CleanInline(string.IsNullOrWhiteSpace(option.DisplayName) ? option.SkillId : option.DisplayName);
                GUI.Label(new Rect(labelX, buttonRect.y + 7f, buttonRect.xMax - labelX - 6f, 18f),
                    TrimPanelText((selected ? "> " : string.Empty) + displayName, 24),
                    skillLabelStyle);
                GUI.Label(new Rect(labelX, buttonRect.y + 26f, buttonRect.xMax - labelX - 6f, 14f),
                    "targets=" + (option.Targets == null ? 0 : option.Targets.Count),
                    skillIdStyle);
                RegisterTooltip(buttonRect, displayName, option.Description);

                if (clicked)
                {
                    _panelSkillId = option.SkillId ?? string.Empty;
                    _panelTargetGuid = string.Empty;
                    _session.ChooseSkill(prompt.HeroSlot, prompt.ActorGuid, _panelSkillId);
                }

                x += buttonWidth + spacing;
            }

            if (GUI.Button(new Rect(area.xMax - passWidth - 14f, buttonY, passWidth, 48f), Ui("Pass", "跳过")))
            {
                _session.PassTurn(prompt.HeroSlot, prompt.ActorGuid);
            }
        }

        private void DrawMirrorHudNonCombat(Rect screen)
        {
            Rect content = new Rect(42f, 92f, screen.width - 84f, screen.height - 124f);
            ExpeditionOverviewSnapshotPayload overview = null;
            if (_session != null)
            {
                _session.TryGetLatestExpeditionOverviewSnapshot(out overview);
            }

            if (_mirrorHudMapExpanded)
            {
                float mapWidth = Mathf.Clamp(content.width * 0.34f, 340f, 540f);
                if (content.width < 1020f)
                {
                    mapWidth = Mathf.Min(mapWidth, content.width * 0.42f);
                }

                Rect mapRect = new Rect(content.x, content.y, mapWidth, content.height);
                Rect interactionRect = new Rect(mapRect.xMax + 14f, content.y, content.width - mapWidth - 14f, content.height);
                DrawMirrorHudMapPanel(mapRect, overview);
                DrawMirrorHudInteractionPanel(interactionRect);
                return;
            }

            Rect railRect = new Rect(content.x, content.y, 58f, content.height);
            DrawSolidRect(railRect, HudPanelColor);
            GUI.Label(new Rect(railRect.x + 8f, railRect.y + 12f, railRect.width - 16f, 24f), Ui("Map", "地图"), CreateHudLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter));
            if (GUI.Button(new Rect(railRect.x + 7f, railRect.y + 44f, railRect.width - 14f, 30f), ">>"))
            {
                _mirrorHudMapExpanded = true;
            }

            DrawMirrorHudInteractionPanel(new Rect(railRect.xMax + 14f, content.y, content.width - railRect.width - 14f, content.height));
        }

        private void DrawMirrorHudInteractionPanel(Rect content)
        {
            DrawSolidRect(content, HudPanelColor);

            GUILayout.BeginArea(new Rect(content.x + 18f, content.y + 14f, content.width - 36f, content.height - 28f));
            _mirrorHudScroll = GUILayout.BeginScrollView(_mirrorHudScroll);
            GUILayout.Label(Ui("Current Host Interaction", "当前主机交互"));
            bool drewCurrentAction = TryDrawMirrorCurrentAction();
            bool drewOperationalPanel = DrawMirrorOperationalPanels(drewCurrentAction);
            if (!drewCurrentAction && !drewOperationalPanel)
            {
                CurrentInteractionSnapshotPayload interaction = null;
                if (_session != null &&
                    _session.TryGetLatestCurrentInteractionSnapshot(out interaction) &&
                    interaction != null &&
                    interaction.IsActive)
                {
                    DrawMirrorNonCombat(interaction);
                }
                else
                {
                    DrawWrappedLabel(Ui("No active host interaction snapshot yet.", "尚未收到可显示的主机交互快照。"));
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawMirrorHudMapPanel(Rect area, ExpeditionOverviewSnapshotPayload overview)
        {
            DrawSolidRect(area, HudPanelColor);
            DrawRectBorder(area, new Color(0.23f, 0.27f, 0.30f, 1f), 1f);

            GUIStyle heading = CreateHudLabelStyle(18, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            GUIStyle muted = CreateHudLabelStyle(12, FontStyle.Normal, PanelMutedTextColor, TextAnchor.MiddleLeft);
            GUI.Label(new Rect(area.x + 14f, area.y + 10f, area.width - 96f, 26f), Ui("Route Map", "路线地图"), heading);
            if (GUI.Button(new Rect(area.xMax - 74f, area.y + 10f, 58f, 26f), Ui("Fold", "折叠")))
            {
                _mirrorHudMapExpanded = false;
            }

            ExpeditionMapRoutePayload route = overview == null ? null : overview.MapRoute;
            if (overview == null || route == null || route.Rows == null || route.Rows.Count == 0)
            {
                GUI.Label(new Rect(area.x + 14f, area.y + 48f, area.width - 28f, 60f),
                    Ui("Map snapshot is not available yet. It will appear after the host enters a region map.",
                        "尚未收到地图快照。房主进入大地图区域后会显示。"),
                    muted);
                return;
            }

            bool revealFullMap = ShouldRevealFullMapForChoiceOverrule(overview);
            string biome = string.IsNullOrWhiteSpace(overview.BiomeType) ? Ui("[unknown biome]", "[未知区域]") : overview.BiomeType;
            GUI.Label(new Rect(area.x + 14f, area.y + 40f, area.width - 28f, 20f),
                biome + " | " + FormatMapRouteCompact(route) +
                (revealFullMap ? " | " + Ui("Infighting: full map", "内斗：全图显示") : string.Empty),
                muted);
            if (overview.MapProgress != null && overview.MapProgress.IsValid)
            {
                GUI.Label(new Rect(area.x + 14f, area.y + 60f, area.width - 28f, 18f),
                    Ui("Progress: ", "进度：") + FormatMapProgressCompact(overview.MapProgress),
                    muted);
            }

            Rect viewport = new Rect(area.x + 10f, area.y + 86f, area.width - 20f, area.height - 98f);
            float viewWidth = Mathf.Max(1f, viewport.width - 18f);
            float viewHeight = CalculateMirrorHudMapCanvasHeight(route);
            Rect view = new Rect(0f, 0f, viewWidth, viewHeight);
            _mirrorHudMapScroll = GUI.BeginScrollView(viewport, _mirrorHudMapScroll, view);
            DrawMirrorHudMapCanvas(view, overview, revealFullMap);
            GUI.EndScrollView();
        }

        private static float CalculateMirrorHudMapCanvasHeight(ExpeditionMapRoutePayload route)
        {
            int rowCount = route == null || route.Rows == null ? 0 : route.Rows.Count(row => row != null);
            return Mathf.Max(220f, 86f + Math.Max(1, rowCount) * MirrorHudMapRowGap);
        }

        private bool ShouldRevealFullMapForChoiceOverrule(ExpeditionOverviewSnapshotPayload overview)
        {
            return overview != null &&
                overview.ChoiceOverruleEnabled &&
                _lobbyClient != null &&
                _lobbyClient.IsInLobby &&
                !_lobbyClient.IsHost;
        }

        private void DrawMirrorHudMapCanvas(Rect canvas, ExpeditionOverviewSnapshotPayload overview, bool revealFullMap)
        {
            ExpeditionMapRoutePayload route = overview == null ? null : overview.MapRoute;
            List<ExpeditionMapRouteRowPayload> rows = route == null || route.Rows == null
                ? new List<ExpeditionMapRouteRowPayload>()
                : route.Rows.Where(row => row != null).OrderBy(row => row.RowIndex).ToList();
            if (rows.Count == 0)
            {
                GUI.Label(new Rect(8f, 8f, canvas.width - 16f, 24f), Ui("No route rows.", "没有路线行。"));
                return;
            }

            int maxSlots = Math.Max(2, rows
                .Select(row => row.Nodes == null || row.Nodes.Count == 0 ? 0 : row.Nodes.Max(node => node == null ? 0 : node.NodeIndex + 1))
                .DefaultIfEmpty(2)
                .Max());

            foreach (ExpeditionMapRouteRowPayload row in rows)
            {
                if (row.Links == null || row.Links.Count == 0)
                {
                    continue;
                }

                ExpeditionMapRouteRowPayload nextRow = FindMapRow(rows, row.RowIndex + 1);
                if (nextRow == null)
                {
                    continue;
                }

                foreach (ExpeditionMapRouteLinkPayload link in row.Links.Where(link => link != null))
                {
                    DrawMirrorHudMapLink(canvas, rows, row, nextRow, link, maxSlots, revealFullMap);
                }
            }

            foreach (ExpeditionMapRouteRowPayload row in rows)
            {
                GUI.Label(
                    new Rect(4f, GetMapRowY(rows, row) - 10f, 36f, 20f),
                    row.RowIndex.ToString(CultureInfo.InvariantCulture),
                    CreateHudLabelStyle(10, FontStyle.Bold, PanelMutedTextColor, TextAnchor.MiddleCenter));

                if (row.Nodes == null)
                {
                    continue;
                }

                foreach (ExpeditionMapRouteNodePayload node in row.Nodes.Where(node => node != null).OrderBy(node => node.NodeIndex))
                {
                    DrawMirrorHudMapNode(canvas, rows, row, node, maxSlots, revealFullMap);
                }
            }

            DrawMirrorHudMapCoachMarker(canvas, rows, overview.MapProgress, route, maxSlots);
            DrawMirrorHudMapLegend(new Rect(8f, canvas.height - 30f, canvas.width - 16f, 24f));
        }

        private void DrawMirrorHudMapLink(
            Rect canvas,
            IList<ExpeditionMapRouteRowPayload> rows,
            ExpeditionMapRouteRowPayload fromRow,
            ExpeditionMapRouteRowPayload toRow,
            ExpeditionMapRouteLinkPayload link,
            int maxSlots,
            bool revealFullMap)
        {
            Vector2 from = GetMapNodeCenter(canvas, rows, fromRow, link.FromNodeIndex, maxSlots);
            Vector2 to = GetMapNodeCenter(canvas, rows, toRow, link.ToNodeIndex, maxSlots);
            bool isRevealedForDisplay = revealFullMap || link.IsRevealed;
            Color color = isRevealedForDisplay ? GetMapRouteColor(link.RouteType) : new Color(0.24f, 0.26f, 0.28f, 0.75f);
            if (link.IsChosen)
            {
                DrawHudLine(from, to, new Color(0.95f, 0.78f, 0.32f, 1f), 5f);
            }

            DrawHudLine(from, to, color, isRevealedForDisplay ? 3f : 2f);

            Vector2 middle = Vector2.Lerp(from, to, 0.5f);
            Rect badge = new Rect(
                middle.x - MirrorHudMapRouteIconSize * 0.5f,
                middle.y - MirrorHudMapRouteIconSize * 0.5f,
                MirrorHudMapRouteIconSize,
                MirrorHudMapRouteIconSize);
            DrawSolidRect(badge, new Color(0.05f, 0.055f, 0.06f, 1f));
            DrawRectBorder(badge, isRevealedForDisplay ? color : new Color(0.24f, 0.26f, 0.28f, 1f), 2f);
            Sprite routeSprite = isRevealedForDisplay ? GetMapRouteSprite(link.RouteType) : null;
            if (routeSprite != null)
            {
                DrawSprite(new Rect(badge.x + 4f, badge.y + 4f, badge.width - 8f, badge.height - 8f), routeSprite);
            }
            else
            {
                GUI.Label(badge, GetMapRouteAbbrev(link.RouteType), CreateHudLabelStyle(10, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter));
            }

            RegisterTooltip(badge, FormatMapRouteDisplayName(link.RouteType), BuildMapLinkTooltip(link, revealFullMap));
        }

        private void DrawMirrorHudMapNode(
            Rect canvas,
            IList<ExpeditionMapRouteRowPayload> rows,
            ExpeditionMapRouteRowPayload row,
            ExpeditionMapRouteNodePayload node,
            int maxSlots,
            bool revealFullMap)
        {
            Vector2 center = GetMapNodeCenter(canvas, rows, row, node.NodeIndex, maxSlots);
            const float nodeSize = MirrorHudMapNodeSize;
            bool isRevealedForDisplay = revealFullMap || node.IsRevealed;
            Rect outer = new Rect(center.x - nodeSize * 0.5f, center.y - nodeSize * 0.5f, nodeSize, nodeSize);
            Color border = node.IsCurrentNode
                ? new Color(0.95f, 0.80f, 0.32f, 1f)
                : node.IsLastCompletedNode
                    ? new Color(0.40f, 0.72f, 0.42f, 1f)
                    : node.IsLastVisitedNode
                        ? new Color(0.45f, 0.58f, 0.82f, 1f)
                        : new Color(0.22f, 0.25f, 0.28f, 1f);
            DrawSolidRect(outer, border);

            Rect inner = new Rect(outer.x + 3f, outer.y + 3f, outer.width - 6f, outer.height - 6f);
            DrawSolidRect(inner, isRevealedForDisplay ? HudTileColor : new Color(0.09f, 0.10f, 0.11f, 1f));

            Sprite sprite = isRevealedForDisplay ? GetMapNodeSprite(node.NodeType) : null;
            if (sprite != null)
            {
                DrawSprite(new Rect(inner.x + 5f, inner.y + 5f, inner.width - 10f, inner.height - 10f), sprite);
            }
            else
            {
                GUI.Label(inner,
                    isRevealedForDisplay ? GetMapNodeAbbrev(node.NodeType) : "?",
                    CreateHudLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter));
            }

            if (node.HasBiomeKillContract)
            {
                Rect contract = new Rect(outer.xMax - 14f, outer.y - 2f, 16f, 16f);
                DrawSolidRect(contract, new Color(0.70f, 0.18f, 0.15f, 1f));
                GUI.Label(contract, "!", CreateHudLabelStyle(10, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter));
            }

            string label = isRevealedForDisplay ? FormatMapNodeDisplayName(node.NodeType) : "?";
            GUI.Label(new Rect(outer.x - 30f, outer.yMax + 4f, outer.width + 60f, 32f),
                TrimPanelText(label, 18),
                CreateHudLabelStyle(10, FontStyle.Normal, PanelTextColor, TextAnchor.UpperCenter));
            RegisterTooltip(outer, FormatMapNodeDisplayName(node.NodeType), BuildMapNodeTooltip(row, node, revealFullMap));
        }

        private void DrawMirrorHudMapCoachMarker(
            Rect canvas,
            IList<ExpeditionMapRouteRowPayload> rows,
            ExpeditionMapProgressPayload progress,
            ExpeditionMapRoutePayload route,
            int maxSlots)
        {
            if (progress == null || !progress.IsValid || route == null || rows == null || rows.Count == 0)
            {
                return;
            }

            Vector2 center;
            ExpeditionMapRouteRowPayload row = FindMapRow(rows, progress.RowIndex);
            if (progress.IsAtNode && row != null)
            {
                center = GetMapNodeCenter(canvas, rows, row, progress.NodeIndex, maxSlots);
            }
            else
            {
                ExpeditionMapRouteRowPayload fromRow = FindMapRow(rows, route.LastVisitedRowIndex);
                ExpeditionMapRouteRowPayload toRow = FindMapRow(rows, route.LastVisitedRowIndex + 1);
                ExpeditionMapRouteLinkPayload chosen = fromRow == null || fromRow.Links == null
                    ? null
                    : fromRow.Links.FirstOrDefault(link => link != null && link.IsChosen);
                if (fromRow == null || toRow == null || chosen == null)
                {
                    return;
                }

                Vector2 from = GetMapNodeCenter(canvas, rows, fromRow, chosen.FromNodeIndex, maxSlots);
                Vector2 to = GetMapNodeCenter(canvas, rows, toRow, chosen.ToNodeIndex, maxSlots);
                center = Vector2.Lerp(from, to, Mathf.Clamp01(progress.BetweenRowsRatio));
            }

            Rect marker = new Rect(center.x - 8f, center.y - 8f, 16f, 16f);
            DrawSolidRect(marker, new Color(0.98f, 0.90f, 0.45f, 1f));
            DrawRectBorder(marker, new Color(0.08f, 0.08f, 0.08f, 1f), 2f);
            RegisterTooltip(marker, Ui("Current Position", "当前位置"), FormatMapProgress(progress));
        }

        private void DrawMirrorHudMapLegend(Rect area)
        {
            DrawSolidRect(area, new Color(0.07f, 0.08f, 0.09f, 0.92f));
            GUI.Label(area,
                Ui("Gold path=current route | C combat | H hazard | R rough | S safe | ? unrevealed",
                    "金线路=当前路线 | C 战斗 | H 危险 | R 粗糙 | S 安全 | ? 未侦察"),
                CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.MiddleCenter));
        }

        private static ExpeditionMapRouteRowPayload FindMapRow(IList<ExpeditionMapRouteRowPayload> rows, int rowIndex)
        {
            return rows == null ? null : rows.FirstOrDefault(row => row != null && row.RowIndex == rowIndex);
        }

        private static float GetMapRowY(IList<ExpeditionMapRouteRowPayload> rows, ExpeditionMapRouteRowPayload row)
        {
            int order = 0;
            if (rows != null && row != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] != null && rows[i].RowIndex == row.RowIndex)
                    {
                        order = i;
                        break;
                    }
                }
            }

            return MirrorHudMapTopPadding + order * MirrorHudMapRowGap;
        }

        private static Vector2 GetMapNodeCenter(
            Rect canvas,
            IList<ExpeditionMapRouteRowPayload> rows,
            ExpeditionMapRouteRowPayload row,
            int nodeIndex,
            int maxSlots)
        {
            float y = GetMapRowY(rows, row);
            float left = 56f;
            float right = 56f;
            float usable = Mathf.Max(1f, canvas.width - left - right);
            float x = maxSlots <= 1
                ? canvas.width * 0.5f
                : left + usable * Mathf.Clamp01((float)Math.Max(0, nodeIndex) / Math.Max(1, maxSlots - 1));
            return new Vector2(x, y);
        }

        private Sprite GetMapNodeSprite(string nodeType)
        {
            if (string.IsNullOrWhiteSpace(nodeType))
            {
                return null;
            }

            Sprite cached;
            if (_mapNodeSprites.TryGetValue(nodeType, out cached))
            {
                return cached;
            }

            Sprite sprite = null;
            try
            {
                NodeType type;
                if (CustomEnum<NodeType>.TryToCast(nodeType, out type) &&
                    type != null &&
                    SingletonMonoBehaviour<MapMgrBhv>.HasInstance(false))
                {
                    sprite = SingletonMonoBehaviour<MapMgrBhv>.Instance.GetNodeIcon(type);
                }
            }
            catch
            {
                sprite = null;
            }

            _mapNodeSprites[nodeType] = sprite;
            return sprite;
        }

        private Sprite GetMapRouteSprite(string routeType)
        {
            if (string.IsNullOrWhiteSpace(routeType))
            {
                return null;
            }

            Sprite cached;
            if (_mapRouteSprites.TryGetValue(routeType, out cached))
            {
                return cached;
            }

            Sprite sprite = null;
            try
            {
                RouteType type;
                if (!CustomEnum<RouteType>.TryToCast(routeType, out type) || type == null)
                {
                    type = RouteType.SIZE;
                }

                if (SingletonMonoBehaviour<MapMgrBhv>.HasInstance(false))
                {
                    MinimapMgrBhv minimap = SingletonMonoBehaviour<MapMgrBhv>.Instance.GetMinimapMgr();
                    Image image = minimap == null ? null : minimap.GetRouteTypeImage(type);
                    sprite = image == null ? null : image.sprite;
                }
            }
            catch
            {
                sprite = null;
            }

            _mapRouteSprites[routeType] = sprite;
            return sprite;
        }

        private static Color GetMapRouteColor(string routeType)
        {
            string text = (routeType ?? string.Empty).ToLowerInvariant();
            if (text.Contains("combat") || text.Contains("battle"))
            {
                return new Color(0.62f, 0.18f, 0.15f, 1f);
            }

            if (text.Contains("hazard") || text.Contains("danger"))
            {
                return new Color(0.74f, 0.45f, 0.12f, 1f);
            }

            if (text.Contains("rough") || text.Contains("wheels") || text.Contains("armor"))
            {
                return new Color(0.50f, 0.42f, 0.31f, 1f);
            }

            if (text.Contains("safe"))
            {
                return new Color(0.22f, 0.50f, 0.30f, 1f);
            }

            if (text.Contains("tear") || text.Contains("oblivion"))
            {
                return new Color(0.45f, 0.24f, 0.62f, 1f);
            }

            if (text.Contains("gang"))
            {
                return new Color(0.50f, 0.28f, 0.56f, 1f);
            }

            return new Color(0.34f, 0.40f, 0.46f, 1f);
        }

        private static string GetMapRouteAbbrev(string routeType)
        {
            string text = (routeType ?? string.Empty).ToLowerInvariant();
            if (text.Contains("combat") || text.Contains("battle"))
            {
                return "C";
            }

            if (text.Contains("hazard") || text.Contains("danger"))
            {
                return "H";
            }

            if (text.Contains("rough") || text.Contains("wheels") || text.Contains("armor"))
            {
                return "R";
            }

            if (text.Contains("safe"))
            {
                return "S";
            }

            if (text.Contains("tear") || text.Contains("oblivion"))
            {
                return "T";
            }

            if (text.Contains("gang"))
            {
                return "G";
            }

            return "?";
        }

        private static string GetMapNodeAbbrev(string nodeType)
        {
            string label = FormatMapNodeDisplayName(nodeType);
            if (string.IsNullOrWhiteSpace(label))
            {
                return "?";
            }

            string[] parts = Regex.Split(label.Trim(), "\\s+");
            if (parts.Length >= 2)
            {
                return (parts[0].Substring(0, 1) + parts[1].Substring(0, 1)).ToUpperInvariant();
            }

            return label.Substring(0, Math.Min(2, label.Length)).ToUpperInvariant();
        }

        private static string FormatMapNodeDisplayName(string nodeType)
        {
            if (string.IsNullOrWhiteSpace(nodeType) || string.Equals(nodeType, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return "Unknown";
            }

            try
            {
                NodeType type;
                if (CustomEnum<NodeType>.TryToCast(nodeType, out type) && type != null)
                {
                    string localized = TryGetLocalizedText(type.m_minimapIconTooltipLocKey, type.m_roadIndicatorNameKey);
                    if (!string.IsNullOrWhiteSpace(localized))
                    {
                        return localized;
                    }
                }
            }
            catch
            {
            }

            return SplitCamelCase(nodeType);
        }

        private static string FormatMapRouteDisplayName(string routeType)
        {
            if (string.IsNullOrWhiteSpace(routeType) || string.Equals(routeType, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return "Unknown Route";
            }

            return SplitCamelCase(routeType);
        }

        private static string SplitCamelCase(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string spaced = Regex.Replace(text.Replace('_', ' '), "([a-z])([A-Z])", "$1 $2");
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
        }

        private static string BuildMapNodeTooltip(ExpeditionMapRouteRowPayload row, ExpeditionMapRouteNodePayload node, bool revealFullMap)
        {
            if (node == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>
            {
                "Row: " + (row == null ? -1 : row.RowIndex) + "  Node: " + node.NodeIndex,
                "Type: " + (node.NodeType ?? "[type]"),
            };
            if (!string.IsNullOrWhiteSpace(node.NodeSubType))
            {
                lines.Add("Subtype: " + node.NodeSubType);
            }

            lines.Add("Revealed: " + node.IsRevealed + (revealFullMap && !node.IsRevealed ? " (shown by infighting)" : string.Empty));
            if (node.IsCurrentNode)
            {
                lines.Add("Current position");
            }

            if (node.IsLastVisitedNode)
            {
                lines.Add("Last visited");
            }

            if (node.IsLastCompletedNode)
            {
                lines.Add("Last completed");
            }

            if (node.HasBiomeKillContract)
            {
                lines.Add("Biome kill contract: " + node.BiomeKillContractGuid);
            }

            return string.Join("\n", lines.ToArray());
        }

        private static string BuildMapLinkTooltip(ExpeditionMapRouteLinkPayload link, bool revealFullMap)
        {
            if (link == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>
            {
                "Route: " + (link.RouteType ?? "[route]"),
                "From: " + link.FromNodeIndex + "  To: " + link.ToNodeIndex,
                "Revealed: " + link.IsRevealed + (revealFullMap && !link.IsRevealed ? " (shown by infighting)" : string.Empty),
                "Chosen: " + link.IsChosen,
            };
            if (!string.IsNullOrWhiteSpace(link.RouteId))
            {
                lines.Add("Id: " + link.RouteId);
            }

            return string.Join("\n", lines.ToArray());
        }

        private static void DrawHudLine(Vector2 from, Vector2 to, Color color, float thickness)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length <= 0.01f)
            {
                return;
            }

            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            GUI.color = color;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(angle, from);
            GUI.DrawTexture(new Rect(from.x, from.y - thickness * 0.5f, length, thickness), Texture2D.whiteTexture);
            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
        }

        private static void DrawRectBorder(Rect rect, Color color, float thickness)
        {
            DrawSolidRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            DrawSolidRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            DrawSolidRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            DrawSolidRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private static GUIStyle CreateHudLabelStyle(int fontSize, FontStyle fontStyle, Color textColor, TextAnchor alignment)
        {
            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                fontStyle = fontStyle,
                alignment = alignment,
                wordWrap = true,
            };
            style.normal.textColor = textColor;
            return style;
        }

        private static void DrawHudStatBar(Rect rect, int value, int max, Color fill, string label)
        {
            DrawSolidRect(rect, HudBarBackColor);
            float ratio = max <= 0 ? 0f : Mathf.Clamp01((float)value / max);
            DrawSolidRect(new Rect(rect.x, rect.y, rect.width * ratio, rect.height), fill);
            GUI.Label(new Rect(rect.x + 4f, rect.y - 2f, rect.width - 8f, rect.height + 4f), label, CreateHudLabelStyle(10, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft));
        }

        private void DrawActorStatusIconStrip(Rect rect, ActorSnapshotPayload actor)
        {
            if (actor == null || rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            List<HudStatusIcon> icons = BuildHudStatusIcons(actor);
            if (icons.Count == 0)
            {
                return;
            }

            const float iconSize = 18f;
            const float gap = 4f;
            int maxIcons = Math.Max(1, Mathf.FloorToInt((rect.width + gap) / (iconSize + gap)));
            int visibleCount = Math.Min(icons.Count, maxIcons);
            float x = rect.x;
            float y = rect.y + Mathf.Max(0f, (rect.height - iconSize) * 0.5f);
            for (int i = 0; i < visibleCount; i++)
            {
                if (i == visibleCount - 1 && icons.Count > maxIcons)
                {
                    DrawStatusOverflowIcon(new Rect(x, y, iconSize, iconSize), icons.Count - maxIcons + 1);
                    break;
                }

                DrawHudStatusIcon(new Rect(x, y, iconSize, iconSize), icons[i]);
                x += iconSize + gap;
            }
        }

        private List<HudStatusIcon> BuildHudStatusIcons(ActorSnapshotPayload actor)
        {
            List<HudStatusIcon> icons = new List<HudStatusIcon>();
            if (actor.IsDeathsDoor)
            {
                icons.Add(new HudStatusIcon
                {
                    Id = "deaths_door",
                    Kind = "status",
                    Label = "DD",
                    DisplayName = "Death's Door",
                    Description = "This actor is at Death's Door.",
                    Count = 1,
                    Color = new Color(0.55f, 0.08f, 0.08f, 1f),
                });
            }

            AddHudStatusIcons(icons, actor.Tokens, "token");
            AddHudStatusIcons(icons, actor.Buffs, "buff");
            AddHudStatusIcons(icons, actor.Dots, "dot");
            return icons;
        }

        private void AddHudStatusIcons(List<HudStatusIcon> icons, IList<StatusSnapshotPayload> statuses, string fallbackKind)
        {
            if (statuses == null || statuses.Count == 0)
            {
                return;
            }

            foreach (StatusSnapshotPayload status in statuses
                .Where(status => status != null && status.Count > 0)
                .OrderBy(status => status.Kind ?? fallbackKind)
                .ThenBy(status => status.Id))
            {
                string kind = string.IsNullOrWhiteSpace(status.Kind) ? fallbackKind : status.Kind;
                Sprite sprite = null;
                string label = "?";
                Color color = new Color(0.25f, 0.28f, 0.31f, 1f);
                if (string.Equals(kind, "token", StringComparison.OrdinalIgnoreCase))
                {
                    sprite = GetTokenSprite(status.Id);
                    label = "T";
                    color = new Color(0.34f, 0.28f, 0.16f, 1f);
                }
                else if (string.Equals(kind, "dot", StringComparison.OrdinalIgnoreCase))
                {
                    sprite = GetDotSprite(status.Id);
                    label = "D";
                    color = new Color(0.42f, 0.13f, 0.13f, 1f);
                }
                else if (string.Equals(kind, "buff", StringComparison.OrdinalIgnoreCase))
                {
                    sprite = GetBuffSprite(status.Id);
                    label = "B";
                    color = new Color(0.14f, 0.30f, 0.27f, 1f);
                }

                icons.Add(new HudStatusIcon
                {
                    Id = status.Id,
                    Kind = kind,
                    Label = label,
                    DisplayName = string.IsNullOrWhiteSpace(status.DisplayName) ? status.Id : status.DisplayName,
                    Description = status.Description,
                    Count = status.Count,
                    Duration = status.Duration,
                    Sprite = sprite,
                    Color = color,
                });
            }
        }

        private void DrawHudStatusIcon(Rect rect, HudStatusIcon icon)
        {
            if (icon == null)
            {
                return;
            }

            DrawSolidRect(rect, icon.Color);
            Rect inner = new Rect(rect.x + 2f, rect.y + 2f, rect.width - 4f, rect.height - 4f);
            if (icon.Sprite != null)
            {
                DrawSprite(inner, icon.Sprite);
            }
            else
            {
                GUI.Label(rect, icon.Label ?? "?", CreateHudLabelStyle(9, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter));
            }

            if (icon.Count > 1)
            {
                GUI.Label(new Rect(rect.x + 1f, rect.yMax - 10f, rect.width - 2f, 10f),
                    icon.Count.ToString(CultureInfo.InvariantCulture),
                    CreateHudLabelStyle(8, FontStyle.Bold, Color.white, TextAnchor.LowerRight));
            }

            RegisterTooltip(rect, CleanInline(icon.DisplayName ?? icon.Id), BuildStatusIconTooltip(icon));
        }

        private void DrawStatusOverflowIcon(Rect rect, int overflowCount)
        {
            DrawSolidRect(rect, new Color(0.18f, 0.20f, 0.23f, 1f));
            GUI.Label(rect, "+" + overflowCount, CreateHudLabelStyle(9, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter));
        }

        private static string BuildStatusIconTooltip(HudStatusIcon icon)
        {
            if (icon == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>();
            if (icon.Count > 1)
            {
                lines.Add("Stacks: " + icon.Count);
            }

            if (icon.Duration >= 0)
            {
                lines.Add("Duration: " + icon.Duration);
            }

            if (!string.IsNullOrWhiteSpace(icon.Description))
            {
                lines.Add(CleanTooltip(icon.Description));
            }

            if (!string.IsNullOrWhiteSpace(icon.Id) &&
                !string.Equals(icon.Id, icon.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add("Id: " + icon.Id);
            }

            return string.Join("\n", lines.ToArray());
        }

        private static void DrawSolidRect(Rect rect, Color color)
        {
            Color oldColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = oldColor;
        }

        private static void DrawSprite(Rect rect, Sprite sprite)
        {
            if (sprite == null || sprite.texture == null)
            {
                return;
            }

            DrawTextureSection(rect, sprite.texture, sprite.textureRect);
        }

        private static void DrawTooltipIcon(Rect rect, TooltipIconData icon)
        {
            if (icon.Sprite != null)
            {
                DrawSprite(rect, icon.Sprite);
                return;
            }

            if (icon.Texture != null)
            {
                DrawTextureSection(rect, icon.Texture, icon.TextureRect);
            }
        }

        private static void DrawTextureSection(Rect rect, Texture texture, Rect textureRect)
        {
            if (texture == null || textureRect.width <= 0f || textureRect.height <= 0f)
            {
                return;
            }

            Rect fittedRect = FitRectToAspect(rect, textureRect.width, textureRect.height);
            Rect texCoords = new Rect(
                textureRect.x / texture.width,
                textureRect.y / texture.height,
                textureRect.width / texture.width,
                textureRect.height / texture.height);
            GUI.DrawTextureWithTexCoords(fittedRect, texture, texCoords, true);
        }

        private static Rect FitRectToAspect(Rect rect, float sourceWidth, float sourceHeight)
        {
            if (sourceWidth > 0f && sourceHeight > 0f && rect.width > 0f && rect.height > 0f)
            {
                float sourceAspect = sourceWidth / sourceHeight;
                float targetAspect = rect.width / rect.height;
                if (sourceAspect > targetAspect)
                {
                    float fittedHeight = rect.width / sourceAspect;
                    return new Rect(rect.x, rect.y + (rect.height - fittedHeight) * 0.5f, rect.width, fittedHeight);
                }

                float fittedWidth = rect.height * sourceAspect;
                return new Rect(rect.x + (rect.width - fittedWidth) * 0.5f, rect.y, fittedWidth, rect.height);
            }

            return rect;
        }

        private static Rect FillRectToAspect(Rect rect, float sourceWidth, float sourceHeight)
        {
            if (sourceWidth > 0f && sourceHeight > 0f && rect.width > 0f && rect.height > 0f)
            {
                float sourceAspect = sourceWidth / sourceHeight;
                float targetAspect = rect.width / rect.height;
                if (sourceAspect > targetAspect)
                {
                    float filledWidth = rect.height * sourceAspect;
                    return new Rect(rect.x + (rect.width - filledWidth) * 0.5f, rect.y, filledWidth, rect.height);
                }

                float filledHeight = rect.width / sourceAspect;
                return new Rect(rect.x, rect.y + (rect.height - filledHeight) * 0.5f, rect.width, filledHeight);
            }

            return rect;
        }

        private static void DrawPortraitSprite(Rect rect, Sprite sprite)
        {
            if (sprite == null || sprite.texture == null)
            {
                return;
            }

            Rect textureRect = sprite.textureRect;
            Rect drawRect = FillRectToAspect(rect, textureRect.width, textureRect.height);
            GUI.BeginGroup(rect);
            DrawTextureSection(new Rect(drawRect.x - rect.x, drawRect.y - rect.y, drawRect.width, drawRect.height), sprite.texture, textureRect);
            GUI.EndGroup();
        }

        private Sprite GetItemSprite(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return null;
            }

            Sprite cached;
            if (_itemSprites.TryGetValue(itemId, out cached))
            {
                return cached;
            }

            Sprite sprite = null;
            try
            {
                if (Singleton<ResourceDatabaseItem>.HasInstance())
                {
                    ResourceItem resource = Singleton<ResourceDatabaseItem>.Instance.GetResource(itemId, true, null);
                    if (resource != null && resource.m_iconPrefab != null && resource.m_iconPrefab.RuntimeKeyIsValid())
                    {
                        AsyncOperationHandle<GameObject> handle = resource.m_iconPrefab.LoadAssetAsync();
                        GameObject prefab = handle.WaitForCompletion();
                        Image image = prefab == null ? null : prefab.GetComponentInChildren<Image>(true);
                        sprite = image == null ? null : image.sprite;
                    }
                }
            }
            catch
            {
                sprite = null;
            }

            _itemSprites[itemId] = sprite;
            return sprite;
        }

        private static string GetLocalizedItemDisplayName(string itemId, string fallback)
        {
            ItemDefinition definition = TryGetItemDefinition(itemId);
            if (definition != null)
            {
                try
                {
                    string displayName = ItemDescription.GetTitle(definition, 0);
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        return CleanInline(displayName);
                    }
                }
                catch
                {
                }
            }

            return CleanInline(string.IsNullOrWhiteSpace(fallback) ? itemId ?? "[item]" : fallback);
        }

        private static string GetLocalizedItemDescription(string itemId)
        {
            ItemDefinition definition = TryGetItemDefinition(itemId);
            if (definition == null)
            {
                return string.Empty;
            }

            string fallbackDescription = BuildArenaItemDefinitionDescription(definition);
            try
            {
                string description = ItemDescription.GetDescription(
                    definition,
                    1,
                    true,
                    0,
                    false,
                    false,
                    false,
                    false,
                    true,
                    null) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return CombineArenaItemDescriptions(description, fallbackDescription);
                }
            }
            catch
            {
            }

            try
            {
                string description = ItemDescription.GetDescription(
                    definition,
                    -1,
                    false,
                    0,
                    true,
                    false,
                    false,
                    true,
                    true,
                    null) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return CombineArenaItemDescriptions(description, fallbackDescription);
                }
            }
            catch
            {
            }

            return fallbackDescription;
        }

        private static string CombineArenaItemDescriptions(string primary, string fallback)
        {
            if (string.IsNullOrWhiteSpace(primary))
            {
                return fallback ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(fallback))
            {
                return primary;
            }

            string cleanPrimary = CleanTooltip(primary);
            string[] fallbackLines = CleanTooltip(fallback)
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length >= 8)
                .ToArray();
            if (fallbackLines.Length > 0 && fallbackLines.All(line => cleanPrimary.Contains(line)))
            {
                return primary;
            }

            return primary + "\n" + fallback;
        }

        private static string BuildArenaItemDefinitionDescription(ItemDefinition definition)
        {
            if (definition == null)
            {
                return string.Empty;
            }

            List<string> blocks = new List<string>();
            ActorDataSkill skill = null;
            try
            {
                skill = definition.GetActorDataSkill();
                if (skill != null)
                {
                    AddArenaDescriptionBlock(blocks, GetArenaSkillDescription(skill.Id, null));
                }
            }
            catch
            {
            }

            try
            {
                DataExternalBuffs dataExternalBuffs = definition.GetDataExternalBuffs();
                string description = dataExternalBuffs == null
                    ? string.Empty
                    : BuffDescription.GetDescription(dataExternalBuffs.GetBuffs(), false);
                AddArenaDescriptionBlock(blocks, description);
            }
            catch
            {
            }

            AddArenaItemEffectDescriptions(blocks, definition.GetEffects(), false);
            AddArenaItemEffectDescriptions(blocks, definition.GetApplyLimitEffects(), false);
            AddArenaItemEffectDescriptions(blocks, definition.GetCombinationEffects(), false);
            AddArenaItemEffectDescriptions(blocks, definition.GetCombinationApplyLimitEffects(), false);

            try
            {
                ActorDataEffects actorDataEffects = definition.GetActorDataSkillEffects();
                string description = actorDataEffects == null
                    ? string.Empty
                    : ActorDataEffectDescription.GetDescription(actorDataEffects, skill, true, true, 0U);
                AddArenaDescriptionBlock(blocks, description);
            }
            catch
            {
            }

            if (blocks.Count == 0)
            {
                List<string> meta = new List<string>();
                try
                {
                    if (definition.m_type != null)
                    {
                        meta.Add("Type: " + definition.m_type.GetName());
                    }

                    if (definition.m_tags != null && definition.m_tags.Count > 0)
                    {
                        meta.Add("Tags: " + string.Join(", ", definition.m_tags.ToArray()));
                    }
                }
                catch
                {
                }

                return string.Join("\n", meta.ToArray());
            }

            return string.Join("\n", blocks.ToArray());
        }

        private static void AddArenaItemEffectDescriptions(
            List<string> blocks,
            IReadOnlyList<SourceDefinition<EffectDefinition>> effects,
            bool isFriendlyTarget)
        {
            if (blocks == null || effects == null)
            {
                return;
            }

            foreach (SourceDefinition<EffectDefinition> sourceDefinition in effects)
            {
                EffectDefinition effect = sourceDefinition == null ? null : sourceDefinition.Definition;
                if (effect == null)
                {
                    continue;
                }

                try
                {
                    AddArenaDescriptionBlock(blocks, EffectDescription.GetDescription(effect, isFriendlyTarget, true));
                }
                catch
                {
                }
            }
        }

        private static ItemDefinition TryGetItemDefinition(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return null;
            }

            try
            {
                if (SingletonMonoBehaviour<Library<string, ItemDefinition>>.HasInstance(false))
                {
                    return SingletonMonoBehaviour<Library<string, ItemDefinition>>.Instance.GetLibraryElement(itemId);
                }
            }
            catch
            {
            }

            return null;
        }

        private Sprite GetSkillSprite(string skillId)
        {
            if (string.IsNullOrWhiteSpace(skillId))
            {
                return null;
            }

            Sprite cached;
            if (_skillSprites.TryGetValue(skillId, out cached))
            {
                return cached;
            }

            Sprite sprite = null;
            try
            {
                if (Singleton<ResourceDatabaseSkills>.HasInstance())
                {
                    ResourceSkillBase resource = Singleton<ResourceDatabaseSkills>.Instance.GetResource(skillId, true, null);
                    sprite = resource == null ? null : resource.m_SkillSprite;
                }
            }
            catch
            {
                sprite = null;
            }

            _skillSprites[skillId] = sprite;
            return sprite;
        }

        private Sprite GetActorPortraitSprite(string actorDataId)
        {
            if (string.IsNullOrWhiteSpace(actorDataId))
            {
                return null;
            }

            Sprite cached;
            if (_actorPortraitSprites.TryGetValue(actorDataId, out cached))
            {
                return cached;
            }

            Sprite sprite = null;
            try
            {
                if (Singleton<ResourceDatabaseActors>.HasInstance())
                {
                    ResourceActor resource = Singleton<ResourceDatabaseActors>.Instance.GetResource(actorDataId, true, null);
                    ResourceActor topMost = resource == null ? null : resource.GetTopMostParent();
                    ResourceActor source = topMost == null ? resource : topMost;
                    if (source != null)
                    {
                        sprite = source.GetPortraitIconByType(ResourceActor.PortraitIconType.TurnOrder, null);
                        if (sprite == null)
                        {
                            sprite = source.GetPortraitIconByType(ResourceActor.PortraitIconType.Map, null);
                        }

                        if (sprite == null)
                        {
                            sprite = source.GetPortraitIconByType(ResourceActor.PortraitIconType.Color, null);
                        }

                        if (sprite == null)
                        {
                            sprite = source.GetPortraitIconByType(ResourceActor.PortraitIconType.CombatBar, null);
                        }
                    }
                }
            }
            catch
            {
                sprite = null;
            }

            _actorPortraitSprites[actorDataId] = sprite;
            return sprite;
        }

        private Sprite GetTokenSprite(string tokenId)
        {
            if (string.IsNullOrWhiteSpace(tokenId))
            {
                return null;
            }

            Sprite cached;
            if (_tokenSprites.TryGetValue(tokenId, out cached))
            {
                return cached;
            }

            float retryAt;
            if (_missingTokenSpriteRetryAt.TryGetValue(tokenId, out retryAt) && Time.unscaledTime < retryAt)
            {
                return null;
            }

            Sprite sprite = null;
            try
            {
                if (Singleton<ResourceDatabaseTokens>.HasInstance())
                {
                    ResourceToken resource = Singleton<ResourceDatabaseTokens>.Instance.GetResource(tokenId, true, null);
                    sprite = resource == null ? null : resource.m_TokenSprite;
                }
            }
            catch
            {
                sprite = null;
            }

            if (sprite != null)
            {
                _tokenSprites[tokenId] = sprite;
                _missingTokenSpriteRetryAt.Remove(tokenId);
            }
            else
            {
                _missingTokenSpriteRetryAt[tokenId] = Time.unscaledTime + 5f;
            }

            return sprite;
        }

        private Sprite GetDotSprite(string dotType)
        {
            if (string.IsNullOrWhiteSpace(dotType))
            {
                return null;
            }

            Sprite cached;
            if (_dotSprites.TryGetValue(dotType, out cached))
            {
                return cached;
            }

            float retryAt;
            if (_missingDotSpriteRetryAt.TryGetValue(dotType, out retryAt) && Time.unscaledTime < retryAt)
            {
                return null;
            }

            Sprite sprite = null;
            try
            {
                if (SingletonMonoBehaviour<Assets.Code.UI.PopTextManager>.HasInstance(false))
                {
                    object manager = SingletonMonoBehaviour<Assets.Code.UI.PopTextManager>.Instance;
                    FieldInfo field = manager.GetType().GetField("m_DotResourceDatabase", BindingFlags.NonPublic | BindingFlags.Instance);
                    ResourceDatabaseDots database = field == null ? null : field.GetValue(manager) as ResourceDatabaseDots;
                    ResourceDot resource = database == null ? null : database.GetResource(dotType, true, true);
                    sprite = resource == null ? null : resource.m_DotSprite;
                }
            }
            catch
            {
                sprite = null;
            }

            try
            {
                if (Singleton<ResourceDatabaseDots>.HasInstance())
                {
                    ResourceDot resource = Singleton<ResourceDatabaseDots>.Instance.GetResource(dotType, true, true);
                    if (sprite == null)
                    {
                        sprite = resource == null ? null : resource.m_DotSprite;
                    }
                }
            }
            catch
            {
            }

            if (sprite != null)
            {
                _dotSprites[dotType] = sprite;
                _missingDotSpriteRetryAt.Remove(dotType);
            }
            else
            {
                _missingDotSpriteRetryAt[dotType] = Time.unscaledTime + 5f;
            }

            return sprite;
        }

        private Sprite GetBuffSprite(string buffId)
        {
            if (string.IsNullOrWhiteSpace(buffId))
            {
                return null;
            }

            Sprite cached;
            if (_buffSprites.TryGetValue(buffId, out cached))
            {
                return cached;
            }

            float retryAt;
            if (_missingBuffSpriteRetryAt.TryGetValue(buffId, out retryAt) && Time.unscaledTime < retryAt)
            {
                return null;
            }

            Sprite sprite = null;
            try
            {
                BuffDefinition definition = SingletonMonoBehaviour<Library<string, BuffDefinition>>.HasInstance(false)
                    ? SingletonMonoBehaviour<Library<string, BuffDefinition>>.Instance.GetLibraryElement(buffId)
                    : null;
                if (definition != null)
                {
                    BuffIconBhv[] icons = Resources.FindObjectsOfTypeAll<BuffIconBhv>();
                    foreach (BuffIconBhv icon in icons)
                    {
                        if (icon == null || !BuffIconMatchesDefinition(icon, definition))
                        {
                            continue;
                        }

                        BuffIconProperties properties = GetBuffIconProperties(icon);
                        if (properties != null && properties.Icon != null)
                        {
                            sprite = properties.Icon;
                            break;
                        }
                    }
                }
            }
            catch
            {
                sprite = null;
            }

            if (sprite != null)
            {
                _buffSprites[buffId] = sprite;
                _missingBuffSpriteRetryAt.Remove(buffId);
            }
            else
            {
                _missingBuffSpriteRetryAt[buffId] = Time.unscaledTime + 5f;
            }

            return sprite;
        }

        private static bool BuffIconMatchesDefinition(BuffIconBhv icon, BuffDefinition definition)
        {
            if (icon == null || definition == null || icon.RequiredTagCount <= 0 || definition.Tags == null || definition.Tags.Count == 0)
            {
                return false;
            }

            int matches = 0;
            foreach (string tag in definition.Tags)
            {
                if (icon.HasTagRequirement(tag))
                {
                    matches++;
                }
            }

            return matches == icon.RequiredTagCount;
        }

        private static BuffIconProperties GetBuffIconProperties(BuffIconBhv icon)
        {
            if (icon == null)
            {
                return null;
            }

            FieldInfo field = typeof(BuffIconBhv).GetField("m_iconProperties", BindingFlags.NonPublic | BindingFlags.Instance);
            return field == null ? null : field.GetValue(icon) as BuffIconProperties;
        }

        private Sprite GetQuirkSprite(ArenaQuirkKind kind)
        {
            string key = kind.ToString();
            Sprite cached;
            if (_quirkSprites.TryGetValue(key, out cached))
            {
                return cached;
            }

            Sprite sprite = null;
            try
            {
                string fieldName = kind == ArenaQuirkKind.Positive
                    ? "m_positiveSprite"
                    : kind == ArenaQuirkKind.Negative
                        ? "m_negativeSprite"
                        : "m_diseaseSprite";
                QuirkLogEntryBhv[] entries = Resources.FindObjectsOfTypeAll<QuirkLogEntryBhv>();
                foreach (QuirkLogEntryBhv entry in entries)
                {
                    sprite = GetMemberValue(entry, fieldName) as Sprite;
                    if (sprite != null)
                    {
                        break;
                    }
                }
            }
            catch
            {
            }

            _quirkSprites[key] = sprite;
            return sprite;
        }

        private void RegisterTooltip(Rect rect, string title, string body)
        {
            Vector2 localMouse = Event.current.mousePosition;
            Rect screenRect = GuiRectToScreenRect(rect);
            bool localHit = rect.Contains(localMouse);
            bool screenHit = screenRect.Contains(localMouse);
            if (!localHit && !screenHit)
            {
                return;
            }

            string cleanTitle = CleanInline(title);
            if (string.IsNullOrWhiteSpace(cleanTitle) && string.IsNullOrWhiteSpace(CleanTooltip(body)))
            {
                return;
            }

            _hoverTooltipTitle = cleanTitle;
            _hoverTooltipBody = body ?? string.Empty;
            _hoverTooltipScreenPosition = localHit
                ? GuiPointToScreenPoint(localMouse)
                : localMouse;
            _hoverTooltipHasScreenPosition = true;
        }

        private void DrawFloatingTooltip()
        {
            Rect bounds = new Rect(0f, 0f, Screen.width, Screen.height);
            Vector2 mouse = _hoverTooltipHasScreenPosition
                ? _hoverTooltipScreenPosition
                : (Event.current == null ? Vector2.zero : Event.current.mousePosition);
            DrawFloatingTooltipAt(mouse, bounds);
        }

        private void DrawFloatingTooltipAt(Vector2 mouse, Rect bounds)
        {
            if (string.IsNullOrWhiteSpace(_hoverTooltipTitle) && string.IsNullOrWhiteSpace(_hoverTooltipBody))
            {
                return;
            }

            GUIStyle titleStyle = CreateHudLabelStyle(14, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUIStyle bodyStyle = CreateHudLabelStyle(12, FontStyle.Normal, PanelTextColor, TextAnchor.UpperLeft);
            bodyStyle.wordWrap = false;

            IList<TooltipSegment> bodySegments = BuildTooltipSegments(_hoverTooltipBody);
            float width = Mathf.Min(Mathf.Min(720f, Mathf.Max(320f, bounds.width - 16f)), Mathf.Max(320f, Screen.width - 32f));
            float innerWidth = width - 24f;
            float titleHeight = string.IsNullOrWhiteSpace(_hoverTooltipTitle)
                ? 0f
                : titleStyle.CalcHeight(new GUIContent(_hoverTooltipTitle), innerWidth);
            float bodyHeight = CalculateTooltipBodyHeight(bodySegments, bodyStyle, innerWidth);
            float maxHeight = Mathf.Max(140f, bounds.height - 16f);
            float height = Mathf.Clamp(28f + titleHeight + bodyHeight, 58f, maxHeight);

            float x = mouse.x + 18f;
            float tooltipY = mouse.y + 18f;
            if (tooltipY + height > bounds.yMax - 6f)
            {
                tooltipY = mouse.y - height - 18f;
            }

            Rect rect = new Rect(x, tooltipY, width, height);
            rect.x = Mathf.Clamp(rect.x, bounds.xMin + 6f, Mathf.Max(bounds.xMin + 6f, bounds.xMax - rect.width - 6f));
            rect.y = Mathf.Clamp(rect.y, bounds.yMin + 6f, Mathf.Max(bounds.yMin + 6f, bounds.yMax - rect.height - 6f));
            DrawSolidRect(rect, new Color(0.04f, 0.045f, 0.05f, 0.98f));

            float contentY = rect.y + 10f;
            if (!string.IsNullOrWhiteSpace(_hoverTooltipTitle))
            {
                GUI.Label(new Rect(rect.x + 12f, contentY, rect.width - 24f, titleHeight), _hoverTooltipTitle, titleStyle);
                contentY += titleHeight + 6f;
            }

            if (bodyHeight > 0f)
            {
                DrawTooltipBody(new Rect(rect.x + 12f, contentY, rect.width - 24f, rect.yMax - contentY - 10f), bodySegments, bodyStyle);
            }
        }

        private static Vector2 GuiPointToScreenPoint(Vector2 point)
        {
            try
            {
                return GUIUtility.GUIToScreenPoint(point);
            }
            catch
            {
                return point;
            }
        }

        private static Rect GuiRectToScreenRect(Rect rect)
        {
            try
            {
                Vector2 min = GUIUtility.GUIToScreenPoint(new Vector2(rect.xMin, rect.yMin));
                Vector2 max = GUIUtility.GUIToScreenPoint(new Vector2(rect.xMax, rect.yMax));
                return Rect.MinMaxRect(
                    Mathf.Min(min.x, max.x),
                    Mathf.Min(min.y, max.y),
                    Mathf.Max(min.x, max.x),
                    Mathf.Max(min.y, max.y));
            }
            catch
            {
                return rect;
            }
        }

        private IList<TooltipSegment> BuildTooltipSegments(string rawText)
        {
            List<TooltipSegment> segments = new List<TooltipSegment>();
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return segments;
            }

            int cursor = 0;
            foreach (Match match in SpriteTagRegex.Matches(rawText))
            {
                if (match.Index > cursor)
                {
                    AddTooltipTextSegment(segments, rawText.Substring(cursor, match.Index - cursor));
                }

                string attrs = match.Groups["attrs"].Value;
                string spriteName = GetSpriteTagAttribute(attrs, "name");
                string spriteAssetName = GetSpriteTagAttribute(attrs, "sprite");
                if (string.IsNullOrWhiteSpace(spriteAssetName))
                {
                    spriteAssetName = GetSpriteTagShorthand(attrs);
                }
                if (string.IsNullOrWhiteSpace(spriteName))
                {
                    spriteName = spriteAssetName;
                    spriteAssetName = string.Empty;
                }

                string spriteIndexText = GetSpriteTagAttribute(attrs, "index");
                bool iconAdded = false;
                int spriteIndex;
                if ((!string.IsNullOrWhiteSpace(spriteIndexText) &&
                        int.TryParse(spriteIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out spriteIndex)) ||
                    (!string.IsNullOrWhiteSpace(spriteName) &&
                        int.TryParse(spriteName, NumberStyles.Integer, CultureInfo.InvariantCulture, out spriteIndex)))
                {
                    TooltipIconData indexIcon = string.IsNullOrWhiteSpace(spriteAssetName)
                        ? GetTmpSpriteIcon(spriteIndex)
                        : GetTmpSpriteIcon(spriteAssetName, spriteIndex);
                    if (indexIcon.IsValid)
                    {
                        segments.Add(new TooltipSegment(indexIcon));
                        iconAdded = true;
                    }
                }

                if (iconAdded)
                {
                    cursor = match.Index + match.Length;
                    continue;
                }

                foreach (string spriteId in GetSpriteIdCandidates(spriteName))
                {
                    TooltipIconData icon = GetInlineSpriteIcon(spriteAssetName, spriteId);
                    if (icon.IsValid)
                    {
                        segments.Add(new TooltipSegment(icon));
                        break;
                    }
                }

                cursor = match.Index + match.Length;
            }

            if (cursor < rawText.Length)
            {
                AddTooltipTextSegment(segments, rawText.Substring(cursor));
            }

            return segments;
        }

        private static string GetSpriteTagAttribute(string attrs, string attributeName)
        {
            if (string.IsNullOrWhiteSpace(attrs) || string.IsNullOrWhiteSpace(attributeName))
            {
                return string.Empty;
            }

            foreach (Match match in SpriteTagAttributeRegex.Matches(attrs))
            {
                if (string.Equals(match.Groups["key"].Value, attributeName, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Groups["value"].Value;
                }
            }

            return string.Empty;
        }

        private static string GetSpriteTagShorthand(string attrs)
        {
            if (string.IsNullOrWhiteSpace(attrs))
            {
                return string.Empty;
            }

            Match match = SpriteTagShorthandRegex.Match(attrs);
            return match.Success ? match.Groups["value"].Value : string.Empty;
        }

        private static void AddTooltipTextSegment(List<TooltipSegment> segments, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string cleaned = RichTextTagRegex.Replace(text, string.Empty).Replace("\r", string.Empty);
            if (!string.IsNullOrEmpty(cleaned))
            {
                segments.Add(new TooltipSegment(cleaned));
            }
        }

        private static float CalculateTooltipBodyHeight(IList<TooltipSegment> segments, GUIStyle style, float width)
        {
            return LayoutTooltipBody(new Rect(0f, 0f, width, 10000f), segments, style, false);
        }

        private static void DrawTooltipBody(Rect rect, IList<TooltipSegment> segments, GUIStyle style)
        {
            LayoutTooltipBody(rect, segments, style, true);
        }

        private static float LayoutTooltipBody(Rect rect, IList<TooltipSegment> segments, GUIStyle style, bool draw)
        {
            if (segments == null || segments.Count == 0 || rect.width <= 0f)
            {
                return 0f;
            }

            GUIStyle inlineStyle = new GUIStyle(style)
            {
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            GUIStyle wrapStyle = new GUIStyle(style)
            {
                wordWrap = true,
                clipping = TextClipping.Clip,
            };

            float lineHeight = Mathf.Max(18f, inlineStyle.CalcHeight(new GUIContent("Mg"), rect.width));
            float iconSize = Mathf.Min(18f, lineHeight);
            float iconAdvance = iconSize + 4f;
            float x = rect.x;
            float y = rect.y;
            bool lineHasContent = false;
            bool anyContent = false;

            for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                TooltipSegment segment = segments[segmentIndex];
                if (segment == null)
                {
                    continue;
                }

                if (segment.Kind == TooltipSegmentKind.Icon)
                {
                    if (x + iconAdvance > rect.xMax && lineHasContent)
                    {
                        y += lineHeight;
                        x = rect.x;
                        lineHasContent = false;
                    }

                    if (draw && y < rect.yMax)
                    {
                        DrawTooltipIcon(new Rect(x, y + (lineHeight - iconSize) * 0.5f, iconSize, iconSize), segment.Icon);
                    }

                    x += iconAdvance;
                    lineHasContent = true;
                    anyContent = true;
                    continue;
                }

                string text = segment.Text ?? string.Empty;
                string[] lines = text.Split('\n');
                for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    if (lineIndex > 0)
                    {
                        y += lineHeight;
                        x = rect.x;
                        lineHasContent = false;
                    }

                    foreach (Match wordMatch in TooltipWordRegex.Matches(lines[lineIndex]))
                    {
                        string token = wordMatch.Value;
                        if (string.IsNullOrEmpty(token))
                        {
                            continue;
                        }

                        bool whitespace = string.IsNullOrWhiteSpace(token);
                        if (whitespace && !lineHasContent)
                        {
                            continue;
                        }

                        float tokenWidth = inlineStyle.CalcSize(new GUIContent(token)).x;
                        if (!whitespace && tokenWidth > rect.width)
                        {
                            if (lineHasContent)
                            {
                                y += lineHeight;
                                x = rect.x;
                                lineHasContent = false;
                            }

                            float wrappedHeight = wrapStyle.CalcHeight(new GUIContent(token), rect.width);
                            if (draw && y < rect.yMax)
                            {
                                GUI.Label(new Rect(rect.x, y, rect.width, Mathf.Min(wrappedHeight, rect.yMax - y)), token, wrapStyle);
                            }

                            y += wrappedHeight;
                            x = rect.x;
                            anyContent = true;
                            continue;
                        }

                        if (x + tokenWidth > rect.xMax && lineHasContent)
                        {
                            y += lineHeight;
                            x = rect.x;
                            lineHasContent = false;
                            if (whitespace)
                            {
                                continue;
                            }
                        }

                        if (!whitespace && draw && y < rect.yMax)
                        {
                            GUI.Label(new Rect(x, y, tokenWidth + 4f, lineHeight), token, inlineStyle);
                        }

                        x += tokenWidth;
                        if (!whitespace)
                        {
                            lineHasContent = true;
                            anyContent = true;
                        }
                    }
                }
            }

            if (!anyContent)
            {
                return 0f;
            }

            return Mathf.Max(0f, (lineHasContent ? y + lineHeight : y) - rect.y);
        }

        private TooltipIconData GetInlineSpriteIcon(string spriteId)
        {
            return GetInlineSpriteIcon(null, spriteId);
        }

        private TooltipIconData GetInlineSpriteIcon(string spriteAssetName, string spriteId)
        {
            if (string.IsNullOrWhiteSpace(spriteId))
            {
                return default(TooltipIconData);
            }

            string cacheKey = BuildInlineSpriteCacheKey(spriteAssetName, spriteId);
            TooltipIconData cached;
            if (_inlineSpriteIcons.TryGetValue(cacheKey, out cached))
            {
                if (cached.IsValid)
                {
                    return cached;
                }

                _inlineSpriteIcons.Remove(cacheKey);
            }

            float retryAt;
            if (_missingInlineSpriteRetryAt.TryGetValue(cacheKey, out retryAt) && Time.unscaledTime < retryAt)
            {
                return default(TooltipIconData);
            }

            Sprite tokenSprite = GetTokenSprite(spriteId);
            Sprite dotSprite = tokenSprite == null ? GetDotSprite(spriteId) : null;
            Sprite buffSprite = tokenSprite == null && dotSprite == null ? GetBuffSprite(spriteId) : null;
            TooltipIconData icon = tokenSprite != null
                ? new TooltipIconData(tokenSprite)
                : dotSprite != null
                    ? new TooltipIconData(dotSprite)
                    : buffSprite != null
                        ? new TooltipIconData(buffSprite)
                        : string.IsNullOrWhiteSpace(spriteAssetName)
                            ? GetTmpSpriteIcon(spriteId)
                            : GetTmpSpriteIcon(spriteAssetName, spriteId);
            if (icon.IsValid)
            {
                _inlineSpriteIcons[cacheKey] = icon;
                _missingInlineSpriteRetryAt.Remove(cacheKey);
            }
            else
            {
                _missingInlineSpriteRetryAt[cacheKey] = Time.unscaledTime + 5f;
            }

            return icon;
        }

        private static string BuildInlineSpriteCacheKey(string spriteAssetName, string spriteId)
        {
            string asset = string.IsNullOrWhiteSpace(spriteAssetName)
                ? string.Empty
                : spriteAssetName.Trim().Trim('"', '\'');
            string id = string.IsNullOrWhiteSpace(spriteId)
                ? string.Empty
                : spriteId.Trim();
            return asset + "::" + id;
        }

        private static IEnumerable<string> GetSpriteIdCandidates(string spriteName)
        {
            if (string.IsNullOrWhiteSpace(spriteName))
            {
                yield break;
            }

            List<string> candidates = new List<string>();
            AddCandidate(candidates, spriteName.Trim().Trim('"', '\''));
            string normalized = candidates[0];
            int slashIndex = normalized.LastIndexOf('/');
            if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
            {
                AddCandidate(candidates, normalized.Substring(slashIndex + 1));
            }

            int colonIndex = normalized.LastIndexOf(':');
            if (colonIndex >= 0 && colonIndex < normalized.Length - 1)
            {
                AddCandidate(candidates, normalized.Substring(colonIndex + 1));
            }

            string lower = normalized.ToLowerInvariant();
            AddCandidate(candidates, lower);
            if (lower.StartsWith("token_"))
            {
                AddCandidate(candidates, normalized.Substring("token_".Length));
            }

            if (lower.StartsWith("icon_token_"))
            {
                AddCandidate(candidates, normalized.Substring("icon_token_".Length));
            }

            if (lower.StartsWith("tokens_"))
            {
                AddCandidate(candidates, normalized.Substring("tokens_".Length));
            }

            if (lower.StartsWith("icon_"))
            {
                AddCandidate(candidates, normalized.Substring("icon_".Length));
            }

            if (lower.StartsWith("sprite_"))
            {
                AddCandidate(candidates, normalized.Substring("sprite_".Length));
            }

            foreach (string candidate in candidates.ToArray())
            {
                string compact = candidate == null ? string.Empty : candidate.Trim();
                if (string.IsNullOrWhiteSpace(compact))
                {
                    continue;
                }

                string compactLower = compact.ToLowerInvariant();
                AddCandidate(candidates, compactLower);
                AddCandidate(candidates, compactLower.Replace('-', '_'));

                string prefixBase = compactLower;
                if (prefixBase.StartsWith("icon_"))
                {
                    prefixBase = prefixBase.Substring("icon_".Length);
                }

                AddCandidate(candidates, "icon_" + prefixBase);
                AddCandidate(candidates, "sprite_" + prefixBase);
                AddCandidate(candidates, "token_" + prefixBase);
                AddCandidate(candidates, "tokens_" + prefixBase);
                AddCandidate(candidates, "icon_token_" + prefixBase);
                AddCandidate(candidates, "buff_" + prefixBase);
                AddCandidate(candidates, "dot_" + prefixBase);
                AddCandidate(candidates, "debuff_" + prefixBase);

                if (prefixBase.EndsWith("_icon", StringComparison.Ordinal) && prefixBase.Length > "_icon".Length)
                {
                    AddCandidate(candidates, prefixBase.Substring(0, prefixBase.Length - "_icon".Length));
                }

                string[] visualSuffixes =
                {
                    "_white",
                    "_black",
                    "_outline",
                    "_filled",
                    "_small",
                    "_large",
                    "_positive",
                    "_negative",
                };
                foreach (string suffix in visualSuffixes)
                {
                    if (!prefixBase.EndsWith(suffix, StringComparison.Ordinal) ||
                        prefixBase.Length <= suffix.Length)
                    {
                        continue;
                    }

                    string stripped = prefixBase.Substring(0, prefixBase.Length - suffix.Length);
                    AddCandidate(candidates, stripped);
                    AddCandidate(candidates, "icon_" + stripped);
                    AddCandidate(candidates, "sprite_" + stripped);
                    AddCandidate(candidates, "token_" + stripped);
                    AddCandidate(candidates, "tokens_" + stripped);
                    AddCandidate(candidates, "icon_token_" + stripped);
                    AddCandidate(candidates, "buff_" + stripped);
                    AddCandidate(candidates, "dot_" + stripped);
                    AddCandidate(candidates, "debuff_" + stripped);
                }
            }

            foreach (string candidate in candidates)
            {
                yield return candidate;
            }
        }

        private static void AddCandidate(List<string> candidates, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidates.Contains(candidate))
            {
                return;
            }

            candidates.Add(candidate);
        }

        private static TooltipIconData GetTmpSpriteIcon(string spriteName)
        {
            try
            {
                Type settingsType = Type.GetType("TMPro.TMP_Settings, Unity.TextMeshPro");
                if (settingsType == null)
                {
                    return default(TooltipIconData);
                }

                PropertyInfo defaultSpriteAssetProperty = settingsType.GetProperty(
                    "defaultSpriteAsset",
                    BindingFlags.Public | BindingFlags.Static);
                object defaultSpriteAsset = defaultSpriteAssetProperty == null
                    ? null
                    : defaultSpriteAssetProperty.GetValue(null, null);
                TooltipIconData icon = FindTmpSpriteIcon(defaultSpriteAsset, spriteName);
                return icon.IsValid ? icon : FindLoadedTmpSpriteIcon(null, spriteName);
            }
            catch
            {
                return default(TooltipIconData);
            }
        }

        private static TooltipIconData GetTmpSpriteIcon(int spriteIndex)
        {
            if (spriteIndex < 0)
            {
                return default(TooltipIconData);
            }

            try
            {
                Type settingsType = Type.GetType("TMPro.TMP_Settings, Unity.TextMeshPro");
                if (settingsType == null)
                {
                    return default(TooltipIconData);
                }

                PropertyInfo defaultSpriteAssetProperty = settingsType.GetProperty(
                    "defaultSpriteAsset",
                    BindingFlags.Public | BindingFlags.Static);
                object defaultSpriteAsset = defaultSpriteAssetProperty == null
                    ? null
                    : defaultSpriteAssetProperty.GetValue(null, null);
                TooltipIconData icon = FindTmpSpriteIcon(defaultSpriteAsset, spriteIndex);
                return icon.IsValid ? icon : FindLoadedTmpSpriteIcon(null, spriteIndex);
            }
            catch
            {
                return default(TooltipIconData);
            }
        }

        private static TooltipIconData GetTmpSpriteIcon(string spriteAssetName, string spriteName)
        {
            if (string.IsNullOrWhiteSpace(spriteName))
            {
                return default(TooltipIconData);
            }

            try
            {
                object spriteAsset = TryLoadTmpSpriteAsset(spriteAssetName);
                TooltipIconData icon = FindTmpSpriteIcon(spriteAsset, spriteName);
                if (icon.IsValid)
                {
                    return icon;
                }

                icon = FindLoadedTmpSpriteIcon(spriteAssetName, spriteName);
                return icon.IsValid ? icon : GetTmpSpriteIcon(spriteName);
            }
            catch
            {
                return default(TooltipIconData);
            }
        }

        private static TooltipIconData GetTmpSpriteIcon(string spriteAssetName, int spriteIndex)
        {
            if (spriteIndex < 0)
            {
                return default(TooltipIconData);
            }

            try
            {
                object spriteAsset = TryLoadTmpSpriteAsset(spriteAssetName);
                TooltipIconData icon = FindTmpSpriteIcon(spriteAsset, spriteIndex);
                if (icon.IsValid)
                {
                    return icon;
                }

                icon = FindLoadedTmpSpriteIcon(spriteAssetName, spriteIndex);
                return icon.IsValid ? icon : GetTmpSpriteIcon(spriteIndex);
            }
            catch
            {
                return default(TooltipIconData);
            }
        }

        private static TooltipIconData FindTmpSpriteIcon(object spriteAsset, string spriteName)
        {
            if (spriteAsset == null || string.IsNullOrWhiteSpace(spriteName))
            {
                return default(TooltipIconData);
            }

            TooltipIconData icon = FindTmpSpriteIconInAsset(spriteAsset, spriteName);
            if (icon.IsValid)
            {
                return icon;
            }

            object fallbackAssets = GetMemberValue(spriteAsset, "fallbackSpriteAssets");
            System.Collections.IEnumerable fallbackEnumerable = fallbackAssets as System.Collections.IEnumerable;
            if (fallbackEnumerable == null)
            {
                return default(TooltipIconData);
            }

            foreach (object fallbackAsset in fallbackEnumerable)
            {
                icon = FindTmpSpriteIconInAsset(fallbackAsset, spriteName);
                if (icon.IsValid)
                {
                    return icon;
                }
            }

            return default(TooltipIconData);
        }

        private static TooltipIconData FindTmpSpriteIcon(object spriteAsset, int spriteIndex)
        {
            if (spriteAsset == null || spriteIndex < 0)
            {
                return default(TooltipIconData);
            }

            TooltipIconData icon = FindTmpSpriteIconInAsset(spriteAsset, spriteIndex);
            if (icon.IsValid)
            {
                return icon;
            }

            object fallbackAssets = GetMemberValue(spriteAsset, "fallbackSpriteAssets");
            System.Collections.IEnumerable fallbackEnumerable = fallbackAssets as System.Collections.IEnumerable;
            if (fallbackEnumerable == null)
            {
                return default(TooltipIconData);
            }

            foreach (object fallbackAsset in fallbackEnumerable)
            {
                icon = FindTmpSpriteIconInAsset(fallbackAsset, spriteIndex);
                if (icon.IsValid)
                {
                    return icon;
                }
            }

            return default(TooltipIconData);
        }

        private static object TryLoadTmpSpriteAsset(string spriteAssetName)
        {
            if (string.IsNullOrWhiteSpace(spriteAssetName))
            {
                return null;
            }

            string normalized = spriteAssetName.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            try
            {
                Type spriteAssetType = Type.GetType("TMPro.TMP_SpriteAsset, Unity.TextMeshPro");
                if (spriteAssetType == null)
                {
                    return null;
                }

                object asset = Resources.Load(normalized, spriteAssetType);
                if (asset != null)
                {
                    return asset;
                }

                if (normalized.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    string withoutExtension = normalized.Substring(0, normalized.Length - ".asset".Length);
                    asset = Resources.Load(withoutExtension, spriteAssetType);
                    if (asset != null)
                    {
                        return asset;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static TooltipIconData FindLoadedTmpSpriteIcon(string spriteAssetName, string spriteName)
        {
            if (string.IsNullOrWhiteSpace(spriteName))
            {
                return default(TooltipIconData);
            }

            try
            {
                Type spriteAssetType = Type.GetType("TMPro.TMP_SpriteAsset, Unity.TextMeshPro");
                if (spriteAssetType == null)
                {
                    return default(TooltipIconData);
                }

                UnityEngine.Object[] assets = Resources.FindObjectsOfTypeAll(spriteAssetType);
                TooltipIconData fallback = default(TooltipIconData);
                foreach (UnityEngine.Object asset in assets)
                {
                    if (asset == null)
                    {
                        continue;
                    }

                    TooltipIconData icon = FindTmpSpriteIconInAsset(asset, spriteName);
                    if (!icon.IsValid)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(spriteAssetName) || TmpSpriteAssetNameMatches(asset, spriteAssetName))
                    {
                        return icon;
                    }

                    if (!fallback.IsValid)
                    {
                        fallback = icon;
                    }
                }

                return fallback;
            }
            catch
            {
                return default(TooltipIconData);
            }
        }

        private static TooltipIconData FindLoadedTmpSpriteIcon(string spriteAssetName, int spriteIndex)
        {
            if (spriteIndex < 0)
            {
                return default(TooltipIconData);
            }

            try
            {
                Type spriteAssetType = Type.GetType("TMPro.TMP_SpriteAsset, Unity.TextMeshPro");
                if (spriteAssetType == null)
                {
                    return default(TooltipIconData);
                }

                UnityEngine.Object[] assets = Resources.FindObjectsOfTypeAll(spriteAssetType);
                TooltipIconData fallback = default(TooltipIconData);
                foreach (UnityEngine.Object asset in assets)
                {
                    if (asset == null)
                    {
                        continue;
                    }

                    TooltipIconData icon = FindTmpSpriteIconInAsset(asset, spriteIndex);
                    if (!icon.IsValid)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(spriteAssetName) || TmpSpriteAssetNameMatches(asset, spriteAssetName))
                    {
                        return icon;
                    }

                    if (!fallback.IsValid)
                    {
                        fallback = icon;
                    }
                }

                return fallback;
            }
            catch
            {
                return default(TooltipIconData);
            }
        }

        private static bool TmpSpriteAssetNameMatches(UnityEngine.Object asset, string spriteAssetName)
        {
            if (asset == null || string.IsNullOrWhiteSpace(spriteAssetName))
            {
                return false;
            }

            string normalized = spriteAssetName.Trim().Trim('"', '\'');
            if (normalized.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - ".asset".Length);
            }

            string assetName = asset.name ?? string.Empty;
            return string.Equals(assetName, normalized, StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith("/" + assetName, StringComparison.OrdinalIgnoreCase) ||
                assetName.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase);
        }

        private static TooltipIconData FindTmpSpriteIconInAsset(object spriteAsset, string spriteName)
        {
            if (spriteAsset == null)
            {
                return default(TooltipIconData);
            }

            Texture texture = GetMemberValue(spriteAsset, "atlasTexture") as Texture;
            if (texture == null)
            {
                texture = GetMemberValue(spriteAsset, "spriteSheet") as Texture;
            }

            if (texture == null)
            {
                return default(TooltipIconData);
            }

            object tableObject = GetMemberValue(spriteAsset, "spriteCharacterTable");
            System.Collections.IEnumerable table = tableObject as System.Collections.IEnumerable;
            if (table == null)
            {
                return default(TooltipIconData);
            }

            foreach (object spriteCharacter in table)
            {
                string name = GetMemberValue(spriteCharacter, "name") as string;
                if (!string.Equals(name, spriteName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return BuildTmpSpriteIcon(texture, spriteCharacter);
            }

            return default(TooltipIconData);
        }

        private static TooltipIconData FindTmpSpriteIconInAsset(object spriteAsset, int spriteIndex)
        {
            if (spriteAsset == null || spriteIndex < 0)
            {
                return default(TooltipIconData);
            }

            Texture texture = GetMemberValue(spriteAsset, "atlasTexture") as Texture;
            if (texture == null)
            {
                texture = GetMemberValue(spriteAsset, "spriteSheet") as Texture;
            }

            if (texture == null)
            {
                return default(TooltipIconData);
            }

            object tableObject = GetMemberValue(spriteAsset, "spriteCharacterTable");
            System.Collections.IEnumerable table = tableObject as System.Collections.IEnumerable;
            if (table == null)
            {
                return default(TooltipIconData);
            }

            System.Collections.IList list = tableObject as System.Collections.IList;
            if (list != null && spriteIndex < list.Count)
            {
                return BuildTmpSpriteIcon(texture, list[spriteIndex]);
            }

            int rowIndex = 0;
            foreach (object spriteCharacter in table)
            {
                int unicode = ToInt(GetMemberValue(spriteCharacter, "unicode"));
                int glyphIndex = ToInt(GetMemberValue(spriteCharacter, "glyphIndex"));
                if (rowIndex == spriteIndex || unicode == spriteIndex || glyphIndex == spriteIndex)
                {
                    return BuildTmpSpriteIcon(texture, spriteCharacter);
                }

                rowIndex++;
            }

            return default(TooltipIconData);
        }

        private static TooltipIconData BuildTmpSpriteIcon(Texture texture, object spriteCharacter)
        {
            if (texture == null || spriteCharacter == null)
            {
                return default(TooltipIconData);
            }

            object glyph = GetMemberValue(spriteCharacter, "glyph");
            object glyphRect = GetMemberValue(glyph, "glyphRect") ?? GetMemberValue(spriteCharacter, "glyphRect");
            float x = ToFloat(GetMemberValue(glyphRect, "x"));
            float y = ToFloat(GetMemberValue(glyphRect, "y"));
            float width = ToFloat(GetMemberValue(glyphRect, "width"));
            float height = ToFloat(GetMemberValue(glyphRect, "height"));
            if (width <= 0f || height <= 0f)
            {
                return default(TooltipIconData);
            }

            return new TooltipIconData(texture, new Rect(x, y, width, height));
        }

        private static object GetMemberValue(object instance, string memberName)
        {
            if (instance == null)
            {
                return null;
            }

            Type type = instance.GetType();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo property = type.GetProperty(memberName, flags);
            if (property != null)
            {
                return property.GetValue(instance, null);
            }

            FieldInfo field = type.GetField(memberName, flags);
            return field == null ? null : field.GetValue(instance);
        }

        private static float ToFloat(object value)
        {
            if (value == null)
            {
                return 0f;
            }

            try
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0f;
            }
        }

        private static int ToInt(object value)
        {
            if (value == null)
            {
                return -1;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return -1;
            }
        }

        private static string CleanInline(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return RichTextTagRegex.Replace(text, string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }

        private static string CleanTooltip(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string cleaned = RichTextTagRegex.Replace(text, string.Empty)
                .Replace("\r", string.Empty)
                .Trim();
            while (cleaned.Contains("\n\n\n"))
            {
                cleaned = cleaned.Replace("\n\n\n", "\n\n");
            }

            return cleaned;
        }

        private static string BuildStoreItemTooltip(StoreItemPayload item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                lines.Add(item.Description);
            }

            lines.Add("Price: " + (string.IsNullOrWhiteSpace(item.PriceText) ? "[none]" : item.PriceText));
            lines.Add("Stock: " + item.Quantity + " | Type: " + (item.ItemType ?? "[unknown]"));
            if (!item.CanAfford)
            {
                lines.Add("Cannot afford.");
            }

            return string.Join("\n", lines.ToArray());
        }

        private static string BuildLootItemTooltip(LootItemSnapshotPayload item, string displayName)
        {
            if (item == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>();
            string description = GetLocalizedItemDescription(item.ItemId);
            if (!string.IsNullOrWhiteSpace(description))
            {
                lines.Add(description);
            }

            lines.Add("Quantity: " + item.Quantity);
            if (!string.IsNullOrWhiteSpace(item.ItemType) || !string.IsNullOrWhiteSpace(item.SlotType))
            {
                lines.Add("Type: " +
                    (string.IsNullOrWhiteSpace(item.ItemType) ? "[unknown]" : item.ItemType) +
                    (string.IsNullOrWhiteSpace(item.SlotType) ? string.Empty : " | Slot: " + item.SlotType));
            }

            if (item.Duration > 0)
            {
                lines.Add("Duration: " + item.Duration);
            }

            if (!string.IsNullOrWhiteSpace(item.ItemId) &&
                !string.Equals(item.ItemId, displayName, StringComparison.Ordinal))
            {
                lines.Add("Id: " + item.ItemId);
            }

            return string.Join("\n", lines.ToArray());
        }

        private static string BuildActorStatusTooltip(ActorSnapshotPayload actor)
        {
            if (actor == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>
            {
                "HP " + actor.Health + "/" + actor.MaxHealth + " | Stress " + actor.Stress + "/" + actor.StressMax,
            };
            if (actor.IsDeathsDoor)
            {
                lines.Add("Death's Door");
            }

            AppendStatusTooltip(lines, "Tokens", actor.Tokens);
            AppendStatusTooltip(lines, "Buffs", actor.Buffs);
            AppendStatusTooltip(lines, "DOT", actor.Dots);
            return string.Join("\n", lines.ToArray());
        }

        private static void AppendStatusTooltip(List<string> lines, string label, IList<StatusSnapshotPayload> statuses)
        {
            IList<StatusSnapshotPayload> valid = (statuses ?? Array.Empty<StatusSnapshotPayload>())
                .Where(status => status != null && status.Count > 0)
                .OrderBy(status => status.Id)
                .ToList();
            if (valid.Count == 0)
            {
                return;
            }

            lines.Add(label + ":");
            foreach (StatusSnapshotPayload status in valid)
            {
                string name = string.IsNullOrWhiteSpace(status.DisplayName) ? status.Id : status.DisplayName;
                string description = string.IsNullOrWhiteSpace(status.Description)
                    ? string.Empty
                    : " - " + status.Description;
                string icon = string.IsNullOrWhiteSpace(status.Id)
                    ? string.Empty
                    : "<sprite name=\"" + status.Id + "\"> ";
                lines.Add("  " + icon + CleanInline(name) + " x" + status.Count + description);
            }
        }

        private void DrawControlPanel(int windowId)
        {
            Color oldContentColor = GUI.contentColor;
            Color oldBackgroundColor = GUI.backgroundColor;
            int oldLabelFontSize = GUI.skin.label.fontSize;
            Color oldLabelTextColor = GUI.skin.label.normal.textColor;
            int oldButtonFontSize = GUI.skin.button.fontSize;
            int oldTextFieldFontSize = GUI.skin.textField.fontSize;

            try
            {
                GUI.contentColor = PanelTextColor;
                GUI.backgroundColor = Color.white;
                GUI.skin.label.fontSize = 13;
                GUI.skin.label.normal.textColor = PanelTextColor;
                GUI.skin.button.fontSize = 13;
                GUI.skin.textField.fontSize = 13;

                GUILayout.BeginVertical(_panelBodyStyle, GUILayout.ExpandHeight(true));

                GUILayout.BeginHorizontal(_panelHeaderStyle, GUILayout.Height(38f));
                GUILayout.Label("DD2 Steam MP", _panelTitleStyle);
                GUILayout.FlexibleSpace();
                GUILayout.Label(GetPanelHeaderStatus(), _panelStatusStyle);
                DrawUiLanguageToggle();
                GUILayout.EndHorizontal();

                DrawPanelTabs();

                _panelScroll = GUILayout.BeginScrollView(_panelScroll, GUILayout.ExpandHeight(true));
                GUILayout.BeginVertical(_panelContentStyle);
                DrawSelectedPanelTab();
                GUILayout.EndVertical();
                GUILayout.EndScrollView();

                GUILayout.EndVertical();
                DrawPanelResizeHandle();
                GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, _panelRect.width - PanelResizeHandleSize), 38f));
            }
            finally
            {
                GUI.contentColor = oldContentColor;
                GUI.backgroundColor = oldBackgroundColor;
                GUI.skin.label.fontSize = oldLabelFontSize;
                GUI.skin.label.normal.textColor = oldLabelTextColor;
                GUI.skin.button.fontSize = oldButtonFontSize;
                GUI.skin.textField.fontSize = oldTextFieldFontSize;
            }
        }

        private void EnsurePanelStyles()
        {
            if (_panelStylesReady)
            {
                return;
            }

            _panelWindowTexture = CreatePanelTexture(new Color(0.07f, 0.08f, 0.10f, 1f));
            _panelBodyTexture = CreatePanelTexture(new Color(0.09f, 0.11f, 0.14f, 1f));
            _panelHeaderTexture = CreatePanelTexture(new Color(0.13f, 0.16f, 0.20f, 1f));
            _panelTabTexture = CreatePanelTexture(new Color(0.16f, 0.19f, 0.23f, 1f));
            _panelTabActiveTexture = CreatePanelTexture(new Color(0.10f, 0.36f, 0.43f, 1f));
            _panelSeparatorTexture = CreatePanelTexture(new Color(0.27f, 0.32f, 0.38f, 1f));

            _panelWindowStyle = new GUIStyle(GUI.skin.window)
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(1, 1, 1, 1),
            };
            _panelWindowStyle.normal.background = _panelWindowTexture;
            _panelWindowStyle.onNormal.background = _panelWindowTexture;

            _panelBodyStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(0, 0, 0, 0),
            };
            _panelBodyStyle.normal.background = _panelBodyTexture;

            _panelHeaderStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(12, 12, 6, 6),
                margin = new RectOffset(0, 0, 0, 8),
                alignment = TextAnchor.MiddleLeft,
            };
            _panelHeaderStyle.normal.background = _panelHeaderTexture;

            _panelTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
            };
            _panelTitleStyle.normal.textColor = PanelTextColor;

            _panelStatusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleRight,
            };
            _panelStatusStyle.normal.textColor = PanelMutedTextColor;

            _panelTabStyle = CreatePanelTabStyle(_panelTabTexture, PanelTextColor);
            _panelTabActiveStyle = CreatePanelTabStyle(_panelTabActiveTexture, Color.white);

            _panelContentStyle = new GUIStyle
            {
                padding = new RectOffset(4, 12, 8, 8),
            };

            _panelResizeHandleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.LowerRight,
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(0, 3, 0, 1),
            };
            _panelResizeHandleStyle.normal.textColor = PanelMutedTextColor;

            _panelSeparatorStyle = new GUIStyle(GUI.skin.box)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
            };
            _panelSeparatorStyle.normal.background = _panelSeparatorTexture;

            _panelStylesReady = true;
        }

        private static GUIStyle CreatePanelTabStyle(Texture2D background, Color textColor)
        {
            GUIStyle style = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                margin = new RectOffset(2, 2, 0, 0),
                padding = new RectOffset(8, 8, 4, 4),
            };
            style.normal.background = background;
            style.hover.background = background;
            style.active.background = background;
            style.focused.background = background;
            style.normal.textColor = textColor;
            style.hover.textColor = Color.white;
            style.active.textColor = Color.white;
            style.focused.textColor = textColor;
            return style;
        }

        private static Texture2D CreatePanelTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private void DestroyPanelTextures()
        {
            DestroyPanelTexture(ref _panelWindowTexture);
            DestroyPanelTexture(ref _panelBodyTexture);
            DestroyPanelTexture(ref _panelHeaderTexture);
            DestroyPanelTexture(ref _panelTabTexture);
            DestroyPanelTexture(ref _panelTabActiveTexture);
            DestroyPanelTexture(ref _panelSeparatorTexture);
            _panelStylesReady = false;
        }

        private static void DestroyPanelTexture(ref Texture2D texture)
        {
            if (texture == null)
            {
                return;
            }

            Destroy(texture);
            texture = null;
        }

        private static bool IsChineseCultureDefault()
        {
            try
            {
                CultureInfo culture = CultureInfo.CurrentUICulture;
                return culture != null &&
                    string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool IsChineseUi
        {
            get { return _uiLanguage == UiLanguage.Chinese; }
        }

        private string Ui(string english, string chinese)
        {
            return IsChineseUi ? chinese : english;
        }

        private void ToggleUiLanguage()
        {
            _uiLanguage = IsChineseUi ? UiLanguage.English : UiLanguage.Chinese;
            ClearArenaTextCaches();
        }

        private void DrawUiLanguageToggle()
        {
            bool oldEnabled = GUI.enabled;
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal(GUILayout.Width(90f));
            GUI.enabled = oldEnabled && _uiLanguage != UiLanguage.English;
            if (GUILayout.Button("EN", GUILayout.Width(40f), GUILayout.Height(24f)))
            {
                _uiLanguage = UiLanguage.English;
                ClearArenaTextCaches();
            }

            GUI.enabled = oldEnabled && _uiLanguage != UiLanguage.Chinese;
            if (GUILayout.Button("中", GUILayout.Width(40f), GUILayout.Height(24f)))
            {
                _uiLanguage = UiLanguage.Chinese;
                ClearArenaTextCaches();
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();
        }

        private void ClearArenaTextCaches()
        {
            _tooltipSegmentCache.Clear();
            _arenaSkillDisplayNameCache.Clear();
            _arenaSkillDescriptionCache.Clear();
            _arenaTorchCatalogBuilt = false;
            _arenaTorchMatches.Clear();
            _arenaTorchSearchApplied = null;
        }

        private void ForceReloadArenaVisualResources(string reason)
        {
            _itemSprites.Clear();
            _skillSprites.Clear();
            _actorPortraitSprites.Clear();
            _tokenSprites.Clear();
            _dotSprites.Clear();
            _buffSprites.Clear();
            _quirkSprites.Clear();
            _battleModifierSprites.Clear();
            _arenaTorchSprites.Clear();
            _mapNodeSprites.Clear();
            _mapRouteSprites.Clear();
            _inlineSpriteIcons.Clear();
            _missingTokenSpriteRetryAt.Clear();
            _missingDotSpriteRetryAt.Clear();
            _missingBuffSpriteRetryAt.Clear();
            _missingInlineSpriteRetryAt.Clear();
            _tooltipSegmentCache.Clear();
            _arenaStatus = Ui("Icon cache reloaded.", "图标缓存已重载。");
            HostLog.Write("[arena] Visual resource caches cleared from " + (reason ?? "manual request") + ".");
        }

        private void DrawPanelTabs()
        {
            GUILayout.BeginHorizontal(GUILayout.Height(38f));
            DrawPanelTabButton(PanelTab.Home, Ui("Home", "主页"), Ui("room, roster, current status", "房间、成员和当前状态"));
            DrawPanelTabButton(PanelTab.Lobby, Ui("Lobby", "房间"), Ui("Steam lobby and hero slots", "Steam 房间和英雄槽位"));
            DrawPanelTabButton(PanelTab.Arena, Ui("Arena", "竞技场"), Ui("custom battle and PVP setup", "自定义战斗和 PVP 设置"));
            DrawPanelTabButton(PanelTab.Tools, Ui("Tools", "工具"), Ui("diagnostics", "诊断工具"));
            GUILayout.EndHorizontal();
        }

        private void DrawPanelTabButton(PanelTab tab, string label, string tooltip)
        {
            GUIStyle style = _panelTab == tab ? _panelTabActiveStyle : _panelTabStyle;
            Rect rect = GUILayoutUtility.GetRect(120f, 34f, style, GUILayout.Height(34f), GUILayout.ExpandWidth(true));
            if (GUI.Button(rect, label, style))
            {
                SetPanelTab(tab);
            }

            RegisterTooltip(rect, label, tooltip);
        }

        private void SetPanelTab(PanelTab tab)
        {
            if (!IsCompactPanelTab(tab))
            {
                tab = PanelTab.Home;
            }

            if (_panelTab == tab)
            {
                return;
            }

            _panelTab = tab;
            _panelScroll = Vector2.zero;
        }

        private static bool IsCompactPanelTab(PanelTab tab)
        {
            return tab == PanelTab.Home ||
                tab == PanelTab.Lobby ||
                tab == PanelTab.Arena ||
                tab == PanelTab.Tools;
        }

        private void OpenPanelTarget(PanelTab tab)
        {
            if (IsCompactPanelTab(tab))
            {
                SetPanelTab(tab);
            }
        }

        private static bool HasCompactPanelTarget(PanelTab tab)
        {
            return IsCompactPanelTab(tab);
        }

        private void DrawSelectedPanelTab()
        {
            switch (_panelTab)
            {
                case PanelTab.Home:
                    DrawHomePanelSection();
                    break;
                case PanelTab.Lobby:
                    DrawLobbyPanelSection();
                    DrawPanelSeparator();
                    DrawSlotPanelSection();
                    break;
                case PanelTab.Arena:
                    DrawArenaPanelSection();
                    break;
                case PanelTab.Tools:
                    DrawDiagnosticsPanelSection();
                    break;
                case PanelTab.Mirror:
                case PanelTab.Run:
                case PanelTab.Loadout:
                case PanelTab.Combat:
                case PanelTab.Rewards:
                case PanelTab.Coach:
                case PanelTab.Decisions:
                    _panelTab = PanelTab.Home;
                    DrawHomePanelSection();
                    break;
            }
        }

        private void DrawPanelSeparator()
        {
            GUILayout.Space(12f);
            GUILayout.Box(GUIContent.none, _panelSeparatorStyle, GUILayout.Height(1f), GUILayout.ExpandWidth(true));
            GUILayout.Space(12f);
        }

        private void DrawPanelResizeHandle()
        {
            Rect handleRect = new Rect(
                Mathf.Max(0f, _panelRect.width - PanelResizeHandleSize - 2f),
                Mathf.Max(0f, _panelRect.height - PanelResizeHandleSize - 2f),
                PanelResizeHandleSize,
                PanelResizeHandleSize);
            GUI.Label(handleRect, "///", _panelResizeHandleStyle);

            Event evt = Event.current;
            if (evt == null)
            {
                return;
            }

            if (evt.type == EventType.MouseDown && handleRect.Contains(evt.mousePosition))
            {
                _panelResizing = true;
                _panelResizeChangedThisFrame = true;
                evt.Use();
                return;
            }

            if (!_panelResizing)
            {
                return;
            }

            if (evt.type == EventType.MouseDrag || evt.type == EventType.Repaint)
            {
                float maxWidth = Mathf.Max(320f, Screen.width - 20f);
                float maxHeight = Mathf.Max(260f, Screen.height - 20f);
                float minWidth = Mathf.Min(PanelMinWidth, maxWidth);
                float minHeight = Mathf.Min(PanelMinHeight, maxHeight);
                _panelRect.width = Mathf.Clamp(evt.mousePosition.x + 8f, minWidth, maxWidth);
                _panelRect.height = Mathf.Clamp(evt.mousePosition.y + 8f, minHeight, maxHeight);
                _panelResizeChangedThisFrame = true;

                if (evt.type == EventType.MouseDrag)
                {
                    evt.Use();
                }
            }

            if (evt.rawType == EventType.MouseUp)
            {
                _panelResizing = false;
                _panelResizeChangedThisFrame = true;
                evt.Use();
            }
        }

        private void DrawWindowResizeHandle(
            ref Rect windowRect,
            ref bool resizing,
            ref bool resizeChangedThisFrame,
            float minWidth,
            float minHeight)
        {
            Rect handleRect = new Rect(
                Mathf.Max(0f, windowRect.width - PanelResizeHandleSize - 2f),
                Mathf.Max(0f, windowRect.height - PanelResizeHandleSize - 2f),
                PanelResizeHandleSize,
                PanelResizeHandleSize);
            GUI.Label(handleRect, "///", _panelResizeHandleStyle);

            Event evt = Event.current;
            if (evt == null)
            {
                return;
            }

            if (evt.type == EventType.MouseDown && handleRect.Contains(evt.mousePosition))
            {
                resizing = true;
                resizeChangedThisFrame = true;
                evt.Use();
                return;
            }

            if (!resizing)
            {
                return;
            }

            if (evt.type == EventType.MouseDrag || evt.type == EventType.Repaint)
            {
                float maxWidth = Mathf.Max(minWidth, Screen.width - 20f);
                float maxHeight = Mathf.Max(minHeight, Screen.height - 20f);
                windowRect.width = Mathf.Clamp(evt.mousePosition.x + 8f, Mathf.Min(minWidth, maxWidth), maxWidth);
                windowRect.height = Mathf.Clamp(evt.mousePosition.y + 8f, Mathf.Min(minHeight, maxHeight), maxHeight);
                resizeChangedThisFrame = true;

                if (evt.type == EventType.MouseDrag)
                {
                    evt.Use();
                }
            }

            if (evt.rawType == EventType.MouseUp)
            {
                resizing = false;
                resizeChangedThisFrame = true;
                evt.Use();
            }
        }

        private void DrawHomePanelSection()
        {
            GUILayout.Label(Ui("Control Center", "控制中心"));
            DrawWrappedLabel(GetLobbyPanelStatus() + " | " + GetPvpPanelStatus() + " | " + GetVersionPanelStatus());
            DrawWrappedLabel(Ui(
                "F7 is kept for lobby, assignment, Arena/PVP setup, and diagnostics. Gameplay mirror and operation panels remain in F6.",
                "F7 只保留房间、分配、竞技场/PVP 设置和诊断信息。战斗、奖励、路线、酒馆、商店、马车等实际镜像操作仍在 F6。"));

            GUILayout.BeginHorizontal();
            bool hasLobby = _lobbyClient != null && _lobbyClient.IsInLobby;
            GUI.enabled = _lobbyClient != null && !hasLobby;
            if (GUILayout.Button(Ui("Host 4", "开 4 人房"), GUILayout.Height(28f)))
            {
                _lobbyClient.CreateLobby(4);
            }

            GUI.enabled = _lobbyClient != null && hasLobby;
            if (GUILayout.Button(Ui("Invite", "邀请"), GUILayout.Height(28f)))
            {
                _lobbyClient.OpenInviteDialog();
            }

            if (GUILayout.Button(Ui("Leave", "离开"), GUILayout.Height(28f)))
            {
                _lobbyClient.LeaveLobby(_messageTransport);
            }

            GUI.enabled = _session != null && hasLobby;
            if (GUILayout.Button(Ui("Resync State", "重新同步"), GUILayout.Height(28f)))
            {
                _session.RequestFullState("panel-home");
            }

            GUI.enabled = true;
            GUILayout.EndHorizontal();

            DrawPanelSeparator();
            DrawChoiceOverrulePanelSection();
            DrawPanelSeparator();
            DrawHomeLobbyRosterSummary();
            DrawPanelSeparator();
            DrawHomeHostSummary();
            DrawPanelSeparator();
            DrawHomeActiveActions();
            DrawPanelSeparator();
            DrawHomeVoteSummary();
        }

        private void DrawChoiceOverrulePanelSection()
        {
            GUILayout.Label(Ui("Infighting Overrides", "内斗强制选择"));
            if (_session == null || _lobbyClient == null || !_lobbyClient.IsInLobby)
            {
                DrawWrappedLabel(Ui("No active lobby.", "当前没有房间。"));
                return;
            }

            DrawWrappedLabel(Ui(
                "Allows clients to spend limited per-map charges to force route and story choices. Host controls both the switch and the per-map charge count.",
                "允许客机消耗每张图有限次数，强制决定岔路口和故事选择。是否开启和每图次数都由房主控制。"));

            DrawWrappedLabel(Ui("State: ", "状态：") +
                (_session.ChoiceOverruleEnabled ? Ui("enabled", "已开启") : Ui("disabled", "已关闭")) +
                " | " + Ui("uses", "次数") + "=" +
                _session.ChoiceOverruleRemaining + "/" + _session.ChoiceOverruleLimitPerMap +
                " | " + Ui("map", "地图") + "=" + (_session.ChoiceOverruleMapKey ?? "[none]"));

            if (!_lobbyClient.IsHost)
            {
                DrawWrappedLabel(Ui("Only host can change these settings.", "只有房主可以修改这些设置。"));
                return;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_session.ChoiceOverruleEnabled ? Ui("Disable", "关闭") : Ui("Enable", "开启"), GUILayout.Width(96f), GUILayout.Height(28f)))
            {
                _session.SetChoiceOverruleOptions(!_session.ChoiceOverruleEnabled, _session.ChoiceOverruleLimitPerMap);
            }

            GUILayout.Label(Ui("Uses per map", "每图次数"), GUILayout.Width(112f));
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && _session.ChoiceOverruleLimitPerMap > 0;
            if (GUILayout.Button("-", GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                _session.SetChoiceOverruleOptions(_session.ChoiceOverruleEnabled, _session.ChoiceOverruleLimitPerMap - 1);
            }

            GUI.enabled = oldEnabled;
            GUILayout.Label(_session.ChoiceOverruleLimitPerMap.ToString(), GUILayout.Width(28f));
            if (GUILayout.Button("+", GUILayout.Width(36f), GUILayout.Height(28f)))
            {
                _session.SetChoiceOverruleOptions(_session.ChoiceOverruleEnabled, _session.ChoiceOverruleLimitPerMap + 1);
            }

            GUI.enabled = oldEnabled;
            if (GUILayout.Button(Ui("Reset Uses", "重置次数"), GUILayout.Width(104f), GUILayout.Height(28f)))
            {
                _session.SetChoiceOverruleOptions(_session.ChoiceOverruleEnabled, _session.ChoiceOverruleLimitPerMap);
            }

            GUILayout.EndHorizontal();
        }

        private void DrawHomeLobbyRosterSummary()
        {
            GUILayout.Label(Ui("Lobby Members", "房间成员"));
            if (_lobbyClient == null || !_lobbyClient.IsInLobby)
            {
                DrawWrappedLabel(Ui(
                    "Not in a Steam lobby. Host creates the lobby with Host 4, then uses Invite from F7 or the Steam overlay.",
                    "当前未加入 Steam 房间。房主先点“开 4 人房”，再用 F7 的“邀请”或 Steam 覆盖层邀请好友。"));
                return;
            }

            IReadOnlyList<CSteamID> members = _lobbyClient.GetMembers();
            DrawWrappedLabel(Ui("Members: ", "成员：") + members.Count + "/4" +
                " | " + (_lobbyClient.IsHost ? Ui("you are host", "你是房主") : Ui("you are client", "你是客机")));

            PvpModeStatePayload pvpState = null;
            bool hasPvp = _session != null && _session.TryGetPvpModeState(out pvpState) && pvpState != null && pvpState.Enabled;
            if (hasPvp)
            {
                DrawWrappedLabel(Ui("PVP enemy pilot: ", "PVP 敌方操作者：") +
                    (string.IsNullOrWhiteSpace(pvpState.EnemyControllerName)
                        ? pvpState.EnemyControllerSteamId.ToString()
                        : pvpState.EnemyControllerName));
            }

            for (int i = 0; i < members.Count; i++)
            {
                CSteamID member = members[i];
                DrawWrappedLabel("- " + _lobbyClient.GetPersonaName(member) +
                    "/" + member.m_SteamID +
                    " : " + FormatHomeLobbyMemberStatus(member, pvpState));
            }

            DrawWrappedLabel(Ui("Hero slots: ", "英雄槽位：") + BuildHomeHeroSlotSummary());
        }

        private string FormatHomeLobbyMemberStatus(CSteamID member, PvpModeStatePayload pvpState)
        {
            List<string> parts = new List<string>();
            if (member == _lobbyClient.Owner)
            {
                parts.Add(Ui("host", "房主"));
            }

            if (member == SteamUser.GetSteamID())
            {
                parts.Add(Ui("me", "我"));
            }

            if (pvpState != null &&
                pvpState.Enabled &&
                pvpState.EnemyControllerSteamId == member.m_SteamID)
            {
                parts.Add(Ui("enemy pilot", "敌方操作者"));
            }

            List<int> slots = GetHeroSlotsForMember(member.m_SteamID);
            if (slots.Count > 0)
            {
                parts.Add(Ui("hero slots ", "英雄槽位 ") + string.Join("/", slots.Select(slot => slot.ToString()).ToArray()));
            }

            if (parts.Count == 0)
            {
                parts.Add(Ui("connected, unassigned", "已连接，未分配"));
            }

            return string.Join(", ", parts.ToArray());
        }

        private List<int> GetHeroSlotsForMember(ulong steamId)
        {
            List<int> slots = new List<int>();
            if (_session == null || steamId == 0UL)
            {
                return slots;
            }

            for (int slot = 1; slot <= 4; slot++)
            {
                HeroSlotAssignmentPayload owner;
                if (_session.TryGetHeroSlotOwner(slot, out owner) && owner != null && owner.SteamId == steamId)
                {
                    slots.Add(slot);
                }
            }

            return slots;
        }

        private string BuildHomeHeroSlotSummary()
        {
            if (_session == null)
            {
                return Ui("[no session]", "[无会话]");
            }

            List<string> slots = new List<string>();
            for (int slot = 1; slot <= 4; slot++)
            {
                HeroSlotAssignmentPayload owner;
                string ownerText = _session.TryGetHeroSlotOwner(slot, out owner) && owner != null
                    ? owner.Name
                    : Ui("unassigned", "未分配");
                slots.Add("S" + slot + "(pos " + (slot - 1) + ")=" + ownerText);
            }

            return string.Join(" | ", slots.ToArray());
        }

        private void DrawHomeHostSummary()
        {
            GUILayout.Label(Ui("Host State", "主机状态"));

            ExpeditionOverviewSnapshotPayload overview;
            if (_session == null || !_session.TryGetLatestExpeditionOverviewSnapshot(out overview))
            {
                DrawWrappedLabel(Ui(
                    "Host overview: none yet. If you just joined, use Resync State.",
                    "尚未收到主机总览。如果刚加入房间，可以点“重新同步”。"));
                return;
            }

            IList<ExpeditionHeroPayload> heroes = overview.Heroes ?? Array.Empty<ExpeditionHeroPayload>();
            IList<ExpeditionRelationshipPayload> relationships = overview.Relationships ?? Array.Empty<ExpeditionRelationshipPayload>();
            DrawWrappedLabel("Run: mode=" + (overview.CurrentGameMode ?? "[none]") +
                ", gameType=" + (overview.CurrentGameType ?? "[none]") +
                ", runStarted=" + overview.IsRunStarted +
                ", map=" + (overview.MapState ?? "[none]") +
                ", biome=" + (overview.BiomeType ?? "[none]") +
                (string.IsNullOrWhiteSpace(overview.BiomeSubType) ? string.Empty : "/" + overview.BiomeSubType));

            DrawWrappedLabel("Resources: relics=" + overview.Relics +
                ", baubles=" + overview.Baubles +
                ", candles=" + overview.Candles +
                ", mastery=" + overview.MasteryPoints +
                ", torch=" + overview.Torch + "/" + overview.TorchMax +
                ", loathing=" + overview.Loathing + "/" + overview.LoathingMax +
                ", armor=" + overview.Armor + "/" + overview.ArmorMax +
                ", wheels=" + overview.Wheels + "/" + overview.WheelsMax +
                ", inventory=" + overview.InventoryFilledSlots + "/" + overview.InventoryTotalSlots);

            if (relationships.Count > 0)
            {
                int currentRelationshipCount = relationships.Count(relationship => relationship != null && relationship.HasCurrentRelationship);
                int pendingRelationshipCount = relationships.Count(relationship => relationship != null && relationship.HasPendingRelationship);
                DrawWrappedLabel("Relationships: pairs=" + relationships.Count +
                    ", current=" + currentRelationshipCount +
                    ", pending=" + pendingRelationshipCount +
                    ", leaning=" + FormatRelationshipLeaningRange(relationships));
            }

            if (overview.BiomeGoal != null || overview.BiomeModifier != null)
            {
                DrawWrappedLabel("Region: " +
                    FormatBiomeGoalCompact(overview.BiomeGoal) +
                    (overview.BiomeModifier == null ? string.Empty : ", modifier=" + FormatBiomeModifierCompact(overview.BiomeModifier)));
            }

            if (overview.CombatScenario != null)
            {
                DrawWrappedLabel("Combat scenario: " + FormatCombatScenarioCompact(overview.CombatScenario));
            }

            if (overview.MapProgress != null && overview.MapProgress.IsValid)
            {
                DrawWrappedLabel("Map progress: " + FormatMapProgressCompact(overview.MapProgress));
            }

            if (overview.MapRoute != null)
            {
                DrawWrappedLabel("Map route: " + FormatMapRouteCompact(overview.MapRoute));
            }

            if (overview.LastVisitedNode != null || overview.LastCompletedNode != null)
            {
                DrawWrappedLabel("Map nodes: visited=" + FormatMapNodeCompact(overview.LastVisitedNode) +
                    ", completed=" + FormatMapNodeCompact(overview.LastCompletedNode));
            }

            if (heroes.Count == 0)
            {
                DrawWrappedLabel(Ui("Party: none", "队伍：无"));
                return;
            }

            DrawWrappedLabel(Ui("Party", "队伍"));
            foreach (ExpeditionHeroPayload hero in heroes
                .Where(hero => hero != null)
                .OrderBy(hero => hero.TeamPosition)
                .ThenBy(hero => hero.ActorGuid))
            {
                string displayName = string.IsNullOrWhiteSpace(hero.ActorName) ? hero.ActorDataId : hero.ActorName;
                int quirkCount = (hero.Quirks == null ? 0 : hero.Quirks.Count) +
                    (hero.Diseases == null ? 0 : hero.Diseases.Count);
                int memoryCount = hero.Memories == null ? 0 : hero.Memories.Count;
                string runGoal = string.IsNullOrWhiteSpace(hero.RunGoalId)
                    ? string.Empty
                    : " goal=" + FormatHeroRunGoalCompact(hero);
                DrawWrappedLabel("  Slot " + hero.HeroSlot +
                    " pos=" + hero.TeamPosition +
                    " " + (displayName ?? "[hero]") +
                    " [" + (hero.ActorDataId ?? "[id]") + "]" +
                    " hp=" + hero.Hp + "/" + hero.HpMax +
                    " stress=" + hero.Stress + "/" + hero.StressMax +
                    " quirks/diseases=" + quirkCount +
                    " memories=" + memoryCount +
                    runGoal);
            }
        }

        private void DrawHomeActiveActions()
        {
            CurrentInteractionSnapshotPayload interaction = null;
            if (_session != null &&
                _session.TryGetLatestCurrentInteractionSnapshot(out interaction) &&
                interaction != null)
            {
                DrawHomeCurrentInteraction(interaction);
                return;
            }

            GUILayout.Label(Ui("Active Host Screens", "当前主机界面"));
            bool any = false;

            TurnPromptPayload prompt = null;
            HeroSlotAssignmentPayload owner = null;
            string skillId = null;
            string targetGuid = null;
            bool isPass = false;
            if (_session != null &&
                _session.TryGetPendingTurn(out prompt, out owner, out skillId, out targetGuid, out isPass) &&
                prompt != null)
            {
                string ownerText = owner == null ? "unassigned" : owner.Name;
                string localText = IsLocalTurnOwner(owner) ? "your input" : "waiting";
                DrawHomeActionRow(
                    "Combat Turn",
                    "r" + prompt.Round + "/t" + prompt.Turn +
                    ", role=" + (prompt.ControlRole ?? "hero") +
                    ", team=" + prompt.TeamIndex + ":" + prompt.TeamPosition +
                    ", slot=" + prompt.HeroSlot +
                    ", actor=" + (prompt.ActorName ?? prompt.ActorGuid ?? "[actor]") +
                    ", owner=" + ownerText +
                    ", state=" + localText +
                    ", skill=" + (skillId ?? "[none]") +
                    ", target=" + (targetGuid ?? "[none]") +
                    ", pass=" + isPass,
                    PanelTab.Combat,
                    null);
                any = true;
            }

            any |= DrawHomeSnapshotRows();

            if (!any)
            {
                DrawWrappedLabel(Ui(
                    "No active host-side action. F7 only shows setup/status; use F6 for the detailed gameplay mirror when needed.",
                    "当前没有需要处理的主机操作。F7 只显示设置和状态，需要详细游戏镜像时使用 F6。"));
            }
        }

        private void DrawHomeCurrentInteraction(CurrentInteractionSnapshotPayload snapshot)
        {
            GUILayout.Label(Ui("Current Interaction", "当前交互"));
            if (snapshot == null || !snapshot.IsActive || snapshot.Items == null || snapshot.Items.Count == 0)
            {
                DrawWrappedLabel(Ui(
                    "No active host-side interaction. F7 only shows setup/status; use F6 for the detailed gameplay mirror when needed.",
                    "当前没有主机交互。F7 只显示设置和状态，需要详细游戏镜像时使用 F6。"));
                return;
            }

            List<CurrentInteractionItemPayload> visibleItems = snapshot.Items
                .Where(ShouldShowCurrentInteractionItem)
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.Label ?? string.Empty)
                .Take(12)
                .ToList();
            if (visibleItems.Count == 0)
            {
                DrawWrappedLabel(Ui("No active host-side interaction for your current role.", "当前身份没有可处理的主机交互。"));
                return;
            }

            CurrentInteractionItemPayload primary = visibleItems.FirstOrDefault(item =>
                string.Equals(item.Kind, snapshot.PrimaryKind, StringComparison.Ordinal) ||
                string.Equals(item.VoteKey, snapshot.PrimaryVoteKey, StringComparison.Ordinal)) ?? visibleItems[0];
            DrawWrappedLabel(Ui("Primary: ", "主要：") +
                (primary.Label ?? primary.Kind ?? "[none]") +
                (string.IsNullOrWhiteSpace(primary.Summary) ? string.Empty : " | " + primary.Summary) +
                FormatVoteInline(primary.VoteKey));

            foreach (CurrentInteractionItemPayload item in visibleItems)
            {
                DrawHomeInteractionRow(item);
            }
        }

        private bool ShouldShowCurrentInteractionItem(CurrentInteractionItemPayload item)
        {
            if (item == null)
            {
                return false;
            }

            if (!IsLocalPvpEnemyController())
            {
                return true;
            }

            return !string.Equals(item.Kind, "loot", StringComparison.Ordinal) &&
                !string.Equals(item.VoteKey, MultiplayerSession.VoteKeyLoot, StringComparison.Ordinal);
        }

        private void DrawHomeInteractionRow(CurrentInteractionItemPayload item)
        {
            if (item == null)
            {
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(item.Label ?? item.Kind ?? "[item]", GUILayout.Width(118f));
            GUILayout.BeginVertical();
            DrawWrappedLabel((item.Summary ?? string.Empty) +
                FormatVoteInline(item.VoteKey) +
                (item.IsActionable ? string.Empty : " | view-only"));
            GUILayout.EndVertical();

            PanelTab targetTab;
            bool hasTarget = TryParsePanelTab(item.TargetTab, out targetTab);
            bool hasCompactTarget = hasTarget && HasCompactPanelTarget(targetTab);
            if (hasCompactTarget)
            {
                if (GUILayout.Button(Ui("Open", "打开"), GUILayout.Width(78f), GUILayout.Height(28f)))
                {
                    OpenPanelTarget(targetTab);
                }
            }
            else
            {
                GUILayout.Space(82f);
            }

            GUILayout.EndHorizontal();
        }

        private static bool TryParsePanelTab(string value, out PanelTab tab)
        {
            tab = PanelTab.Home;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            try
            {
                tab = (PanelTab)Enum.Parse(typeof(PanelTab), value, true);
                return Enum.IsDefined(typeof(PanelTab), tab);
            }
            catch
            {
                return false;
            }
        }

        private void DrawMirrorPanelSection()
        {
            GUILayout.Label("Mirror View");

            CurrentInteractionSnapshotPayload interaction = null;
            if (_session != null &&
                _session.TryGetLatestCurrentInteractionSnapshot(out interaction) &&
                interaction != null &&
                interaction.IsActive)
            {
                DrawWrappedLabel("Now: " +
                    (interaction.PrimaryLabel ?? interaction.PrimaryKind ?? "[none]") +
                    (string.IsNullOrWhiteSpace(interaction.PrimarySummary) ? string.Empty : " | " + interaction.PrimarySummary) +
                    FormatVoteInline(interaction.PrimaryVoteKey));
            }
            else
            {
                DrawWrappedLabel("Now: no active host interaction snapshot yet.");
            }

            CombatSnapshotPayload combat;
            if (_session != null &&
                _session.TryGetLatestCombatSnapshot(out combat) &&
                combat != null &&
                (combat.PartyInBattle || (combat.Actors != null && combat.Actors.Count > 0)))
            {
                DrawPanelSeparator();
                DrawMirrorCombat(combat);
                return;
            }

            DrawPanelSeparator();
            DrawMirrorNonCombat(interaction);
        }

        private void DrawMirrorCombat(CombatSnapshotPayload snapshot)
        {
            IList<ActorSnapshotPayload> actors = snapshot.Actors ?? Array.Empty<ActorSnapshotPayload>();
            DrawWrappedLabel("Combat: state=" + (snapshot.BattleState ?? "[none]") +
                ", r" + snapshot.Round + "/t" + snapshot.Turn +
                ", current=" + (snapshot.CurrentActorName ?? snapshot.CurrentActorGuid ?? "[none]"));

            DrawCombatTurnOrder(snapshot);
            DrawCombatSelectedSkill(snapshot.SelectedSkill);

            TurnPromptPayload prompt = null;
            HeroSlotAssignmentPayload owner = null;
            string skillId = null;
            string targetGuid = null;
            bool isPass = false;
            bool hasPendingTurn = _session != null &&
                _session.TryGetPendingTurn(out prompt, out owner, out skillId, out targetGuid, out isPass) &&
                prompt != null;
            if (hasPendingTurn)
            {
                SyncPanelPendingKey(prompt);
            }
            else
            {
                prompt = null;
                owner = null;
                skillId = null;
                targetGuid = null;
                isPass = false;
            }

            TurnSkillOptionPayload selectedPromptSkill = prompt == null
                ? null
                : FindTurnSkillOption(prompt, !string.IsNullOrWhiteSpace(_panelSkillId) ? _panelSkillId : skillId);
            bool canChooseTurnInput = IsLocalTurnOwner(owner);
            if (hasPendingTurn)
            {
                DrawMirrorTurnControls(prompt, owner, skillId, targetGuid, isPass, selectedPromptSkill, canChooseTurnInput);
            }

            IList<string> validTargetGuids = BuildMirrorValidTargetGuids(snapshot.SelectedSkill, selectedPromptSkill);
            bool canChooseTarget = canChooseTurnInput && prompt != null && selectedPromptSkill != null;

            GUILayout.BeginHorizontal();
            DrawMirrorActorColumn(
                "Party",
                actors.Where(actor => actor != null && actor.TeamIndex == 0)
                    .OrderBy(actor => actor.TeamPosition)
                    .ThenBy(actor => actor.ActorGuid)
                    .ToList(),
                snapshot.CurrentActorGuid,
                validTargetGuids,
                targetGuid,
                canChooseTarget,
                prompt);
            DrawMirrorActorColumn(
                "Enemies",
                actors.Where(actor => actor != null && actor.TeamIndex != 0)
                    .OrderBy(actor => actor.TeamIndex)
                    .ThenBy(actor => actor.TeamPosition)
                    .ThenBy(actor => actor.ActorGuid)
                    .ToList(),
                snapshot.CurrentActorGuid,
                validTargetGuids,
                targetGuid,
                canChooseTarget,
                prompt);
            GUILayout.EndHorizontal();
        }

        private void DrawMirrorTurnControls(
            TurnPromptPayload prompt,
            HeroSlotAssignmentPayload owner,
            string acceptedSkillId,
            string acceptedTargetGuid,
            bool isPass,
            TurnSkillOptionPayload selectedPromptSkill,
            bool canChooseTurnInput)
        {
            string ownerText = owner == null ? "unassigned" : owner.Name ?? "[owner]";
            DrawWrappedLabel("Turn input: slot=" + prompt.HeroSlot +
                ", role=" + (prompt.ControlRole ?? "hero") +
                ", team=" + prompt.TeamIndex + ":" + prompt.TeamPosition +
                ", owner=" + ownerText +
                ", skill=" + (acceptedSkillId ?? "[none]") +
                ", target=" + (acceptedTargetGuid ?? "[none]") +
                ", pass=" + isPass);

            if (!canChooseTurnInput)
            {
                DrawWrappedLabel(owner == null
                    ? "Input: slot is unassigned."
                    : "Input: waiting for " + ownerText + ".");
                return;
            }

            IList<TurnSkillOptionPayload> options = prompt.SkillOptions ?? Array.Empty<TurnSkillOptionPayload>();
            if (options.Count == 0)
            {
                DrawWrappedLabel("Skills: none in prompt.");
            }
            else
            {
                GUILayout.Label("Your skills");
                for (int i = 0; i < options.Count; i++)
                {
                    TurnSkillOptionPayload option = options[i];
                    bool selected = selectedPromptSkill != null &&
                        string.Equals(selectedPromptSkill.SkillId, option.SkillId, StringComparison.Ordinal);
                    string label = (selected ? "> " : string.Empty) + FormatSkillOption(option);
                    if (GUILayout.Button(label, GUILayout.Height(28f)))
                    {
                        _panelSkillId = option.SkillId ?? string.Empty;
                        _panelTargetGuid = string.Empty;
                        _session.ChooseSkill(prompt.HeroSlot, prompt.ActorGuid, _panelSkillId);
                    }
                }
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Ui("Pass", "跳过"), GUILayout.Width(96f), GUILayout.Height(28f)))
            {
                _session.PassTurn(prompt.HeroSlot, prompt.ActorGuid);
            }

            if (GUILayout.Button("Open Combat Controls", GUILayout.Width(168f), GUILayout.Height(28f)))
            {
                SetPanelTab(PanelTab.Combat);
            }

            GUILayout.EndHorizontal();
        }

        private void DrawMirrorActorColumn(
            string title,
            IList<ActorSnapshotPayload> actors,
            string currentActorGuid,
            IList<string> validTargetGuids,
            string selectedTargetGuid,
            bool canChooseTarget,
            TurnPromptPayload prompt)
        {
            GUILayout.BeginVertical(GUILayout.MinWidth(220f), GUILayout.ExpandWidth(true));
            GUILayout.Label(title);
            if (actors == null || actors.Count == 0)
            {
                DrawWrappedLabel("  none");
                GUILayout.EndVertical();
                return;
            }

            foreach (ActorSnapshotPayload actor in actors)
            {
                DrawMirrorActorCard(actor, currentActorGuid, validTargetGuids, selectedTargetGuid, canChooseTarget, prompt);
            }

            GUILayout.EndVertical();
        }

        private void DrawMirrorActorCard(
            ActorSnapshotPayload actor,
            string currentActorGuid,
            IList<string> validTargetGuids,
            string selectedTargetGuid,
            bool canChooseTarget,
            TurnPromptPayload prompt)
        {
            if (actor == null)
            {
                return;
            }

            bool current = !string.IsNullOrWhiteSpace(currentActorGuid) &&
                string.Equals(actor.ActorGuid, currentActorGuid, StringComparison.Ordinal);
            bool validTarget = validTargetGuids != null &&
                validTargetGuids.Any(guid => string.Equals(guid, actor.ActorGuid, StringComparison.Ordinal));
            bool selectedTarget = !string.IsNullOrWhiteSpace(selectedTargetGuid) &&
                string.Equals(actor.ActorGuid, selectedTargetGuid, StringComparison.Ordinal);

            GUILayout.BeginVertical(GUI.skin.box);
            DrawWrappedLabel((current ? "> " : string.Empty) +
                (validTarget ? "[target] " : string.Empty) +
                (selectedTarget ? "[chosen] " : string.Empty) +
                FormatMirrorActorName(actor) +
                " | t" + actor.TeamIndex + "p" + actor.TeamPosition +
                (actor.IsDeathsDoor ? " [Death's Door]" : string.Empty) +
                (actor.IsLiving ? string.Empty : " [down]"));

            DrawWrappedLabel("HP " + actor.Health + "/" + actor.MaxHealth +
                " | Stress " + actor.Stress + "/" + actor.StressMax +
                " | guid=" + (actor.ActorGuid ?? "[none]"));

            string tokens = FormatSnapshotStatuses(actor.Tokens);
            if (tokens != "-")
            {
                DrawWrappedLabel("TOK " + tokens);
            }

            string buffs = FormatSnapshotStatuses(actor.Buffs);
            if (buffs != "-")
            {
                DrawWrappedLabel("BUFF " + buffs);
            }

            string dots = FormatSnapshotStatuses(actor.Dots);
            if (dots != "-")
            {
                DrawWrappedLabel("DOT " + dots);
            }

            if (canChooseTarget && validTarget && prompt != null)
            {
                string buttonLabel = selectedTarget ? "Target Selected" : "Choose Target";
                if (GUILayout.Button(buttonLabel, GUILayout.Height(26f)))
                {
                    _panelTargetGuid = actor.ActorGuid ?? string.Empty;
                    _session.ChooseTarget(prompt.HeroSlot, prompt.ActorGuid, _panelTargetGuid);
                }
            }

            GUILayout.EndVertical();
        }

        private static TurnSkillOptionPayload FindTurnSkillOption(TurnPromptPayload prompt, string skillId)
        {
            if (prompt == null || string.IsNullOrWhiteSpace(skillId) || prompt.SkillOptions == null)
            {
                return null;
            }

            return prompt.SkillOptions.FirstOrDefault(option =>
                option != null &&
                string.Equals(option.SkillId, skillId, StringComparison.Ordinal));
        }

        private static IList<string> BuildMirrorValidTargetGuids(
            CombatSelectedSkillPayload selectedSkill,
            TurnSkillOptionPayload selectedPromptSkill)
        {
            if (selectedPromptSkill != null && selectedPromptSkill.Targets != null)
            {
                return selectedPromptSkill.Targets
                    .Where(target => target != null && !string.IsNullOrWhiteSpace(target.ActorGuid))
                    .Select(target => target.ActorGuid)
                    .ToList();
            }

            if (selectedSkill != null && selectedSkill.ValidTargets != null)
            {
                return selectedSkill.ValidTargets
                    .Where(target => target != null && !string.IsNullOrWhiteSpace(target.ActorGuid))
                    .Select(target => target.ActorGuid)
                    .ToList();
            }

            return Array.Empty<string>();
        }

        private void DrawMirrorNonCombat(CurrentInteractionSnapshotPayload interaction)
        {
            bool drewCurrentAction = TryDrawMirrorCurrentAction();
            bool drewOperationalPanel = DrawMirrorOperationalPanels(drewCurrentAction);
            if (drewCurrentAction || drewOperationalPanel)
            {
                return;
            }

            DrawWrappedLabel("Native DD2 scene mirroring is not enabled. This view is driven by host snapshots.");
            if (interaction == null || interaction.Items == null || interaction.Items.Count == 0)
            {
                DrawWrappedLabel("No active mirror item yet.");
                return;
            }

            foreach (CurrentInteractionItemPayload item in interaction.Items
                .Where(ShouldShowCurrentInteractionItem)
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.Label ?? string.Empty)
                .Take(10))
            {
                DrawHomeInteractionRow(item);
            }
        }

        private bool TryDrawMirrorCurrentAction()
        {
            ConfirmationDialogSnapshotPayload dialog;
            if (_session != null &&
                _session.TryGetLatestConfirmationDialogSnapshot(out dialog) &&
                dialog != null &&
                dialog.IsActive)
            {
                DrawMirrorConfirmationDialog(dialog);
                return true;
            }

            HeroSelectSnapshotPayload heroSelect;
            if (_session != null &&
                _session.TryGetLatestHeroSelectSnapshot(out heroSelect) &&
                heroSelect != null &&
                heroSelect.IsActive)
            {
                DrawMirrorHeroSelect(heroSelect);
                return true;
            }

            LootWindowSnapshotPayload loot;
            if (_session != null &&
                !IsLocalPvpEnemyController() &&
                _session.TryGetLatestLootWindowSnapshot(out loot) &&
                loot != null &&
                loot.IsActive)
            {
                DrawMirrorLoot(loot);
                return true;
            }

            RouteChoiceSnapshotPayload route;
            if (_session != null &&
                _session.TryGetLatestRouteChoiceSnapshot(out route) &&
                route != null &&
                route.IsActive)
            {
                DrawMirrorRouteChoice(route);
                return true;
            }

            StoryChoiceSnapshotPayload story;
            if (_session != null &&
                _session.TryGetLatestStoryChoiceSnapshot(out story) &&
                story != null &&
                story.IsActive)
            {
                DrawMirrorStoryChoice(story);
                return true;
            }

            InnSnapshotPayload inn;
            if (_session != null &&
                _session.TryGetLatestInnSnapshot(out inn) &&
                inn != null &&
                inn.IsActive)
            {
                DrawMirrorInn(inn);
                return true;
            }

            EmbarkSnapshotPayload embark;
            if (_session != null &&
                _session.TryGetLatestEmbarkSnapshot(out embark) &&
                embark != null &&
                embark.IsActive)
            {
                DrawMirrorEmbark(embark);
                return true;
            }

            ConfessionChoiceSnapshotPayload confession;
            if (_session != null &&
                _session.TryGetLatestConfessionChoiceSnapshot(out confession) &&
                confession != null &&
                confession.IsActive)
            {
                DrawMirrorConfessionChoice(confession);
                return true;
            }

            LairDecisionSnapshotPayload lair;
            if (_session != null &&
                _session.TryGetLatestLairDecisionSnapshot(out lair) &&
                lair != null &&
                lair.IsActive)
            {
                DrawMirrorLairDecision(lair);
                return true;
            }

            GameResultsSnapshotPayload results;
            if (_session != null &&
                _session.TryGetLatestGameResultsSnapshot(out results) &&
                results != null &&
                results.IsActive)
            {
                DrawMirrorGameResults(results);
                return true;
            }

            return false;
        }

        private bool DrawMirrorOperationalPanels(bool afterPrimaryAction)
        {
            bool any = false;

            StoreSnapshotPayload store;
            if (_session != null &&
                _session.TryGetLatestStoreSnapshot(out store) &&
                store != null &&
                store.IsActive)
            {
                if (afterPrimaryAction || any)
                {
                    DrawPanelSeparator();
                }

                DrawMirrorStore(store);
                any = true;
            }

            StagecoachSnapshotPayload stagecoach;
            if (_session != null &&
                _session.TryGetLatestStagecoachSnapshot(out stagecoach) &&
                stagecoach != null &&
                stagecoach.IsActive)
            {
                if (afterPrimaryAction || any)
                {
                    DrawPanelSeparator();
                }

                DrawMirrorStagecoach(stagecoach);
                any = true;
            }

            HeroLoadoutSnapshotPayload loadout;
            if (_session != null &&
                _session.TryGetLatestHeroLoadoutSnapshot(out loadout) &&
                loadout != null &&
                loadout.IsActive)
            {
                if (afterPrimaryAction || any)
                {
                    DrawPanelSeparator();
                }

                DrawMirrorLoadout(loadout);
                any = true;
            }

            return any;
        }

        private void DrawMirrorHeader(string title, string summary, string voteKey)
        {
            GUILayout.Label(title);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                DrawWrappedLabel(summary + FormatVoteInline(voteKey));
            }

            DrawVoteStatus(voteKey);
        }

        private void DrawMirrorHeroSelect(HeroSelectSnapshotPayload snapshot)
        {
            IList<HeroSelectSlotPayload> slots = snapshot.Slots ?? Array.Empty<HeroSelectSlotPayload>();
            IList<HeroSelectHeroPayload> heroes = snapshot.Heroes ?? Array.Empty<HeroSelectHeroPayload>();
            List<int> localSlotIndexes = GetLocalOwnedHeroSelectSlotIndexes(slots);

            DrawMirrorHeader(
                "Hero Select",
                "confirmed=" + snapshot.RosterConfirmed +
                ", canConfirm=" + snapshot.CanConfirm +
                ", slots=" + slots.Count +
                ", heroes=" + heroes.Count,
                MultiplayerSession.VoteKeyHeroReady);

            GUILayout.Label("Party");
            GUILayout.BeginHorizontal();
            foreach (HeroSelectSlotPayload slot in slots
                .Where(slot => slot != null)
                .OrderBy(slot => slot.SlotIndex))
            {
                DrawMirrorHeroSelectSlot(slot, heroes, localSlotIndexes.Contains(slot.SlotIndex), snapshot.RosterConfirmed);
            }

            GUILayout.EndHorizontal();

            if (localSlotIndexes.Count == 0)
            {
                DrawWrappedLabel("Input: no local assigned hero slot.");
            }
            else if (snapshot.RosterConfirmed)
            {
                DrawWrappedLabel("Input: roster is already confirmed.");
            }
            else
            {
                DrawMirrorHeroSelectAvailableHeroes(heroes, localSlotIndexes);
            }

            GUILayout.BeginHorizontal();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && snapshot.CanConfirm && !snapshot.RosterConfirmed;
            if (GUILayout.Button("Ready To Start", GUILayout.Height(30f)))
            {
                _session.RequestHeroSelectReady();
            }

            GUI.enabled = previousEnabled && _lobbyClient != null && _lobbyClient.IsHost && snapshot.CanConfirm;
            if (GUILayout.Button("Confirm Party On Host", GUILayout.Height(30f)))
            {
                _session.RequestHeroSelectConfirm();
            }

            GUI.enabled = previousEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawMirrorHeroSelectSlot(
            HeroSelectSlotPayload slot,
            IList<HeroSelectHeroPayload> heroes,
            bool isLocalSlot,
            bool rosterConfirmed)
        {
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(230f), GUILayout.MinHeight(160f));
            GUILayout.Label("Slot " + slot.HeroSlot + (isLocalSlot ? " (you)" : string.Empty));

            GUILayout.BeginHorizontal();
            DrawHeroSelectPortrait(slot.ActorDataId, 58f);
            GUILayout.BeginVertical();
            DrawWrappedLabel(FormatHeroSelectActor(slot.ActorGuid, slot.ActorDataId, slot.ActorName, slot.PathId));
            string ownerText = string.IsNullOrWhiteSpace(slot.OwnerName)
                ? "[unassigned]"
                : slot.OwnerName;
            DrawWrappedLabel("Owner: " + ownerText);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            DrawMirrorHeroSelectPathButtons(slot, heroes, isLocalSlot && !rosterConfirmed);

            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && isLocalSlot && !rosterConfirmed && !string.IsNullOrWhiteSpace(slot.ActorGuid);
            if (GUILayout.Button("Clear Slot", GUILayout.Height(26f)))
            {
                _session.RequestHeroSelectClear(slot.SlotIndex);
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndVertical();
        }

        private void DrawMirrorHeroSelectPathButtons(
            HeroSelectSlotPayload slot,
            IList<HeroSelectHeroPayload> heroes,
            bool canChangePath)
        {
            if (slot == null || string.IsNullOrWhiteSpace(slot.ActorGuid) || heroes == null)
            {
                return;
            }

            HeroSelectHeroPayload hero = heroes.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.ActorGuid, slot.ActorGuid, StringComparison.Ordinal));
            IList<HeroSelectPathPayload> paths = hero == null ? Array.Empty<HeroSelectPathPayload>() : hero.Paths ?? Array.Empty<HeroSelectPathPayload>();
            if (paths.Count == 0)
            {
                return;
            }

            GUILayout.Label("Paths");
            int drawn = 0;
            GUILayout.BeginHorizontal();
            foreach (HeroSelectPathPayload path in paths.Where(path => path != null && !string.IsNullOrWhiteSpace(path.PathId)))
            {
                if (drawn > 0 && drawn % 2 == 0)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }

                string label = TrimPanelText(CleanInline(string.IsNullOrWhiteSpace(path.DisplayName) ? path.PathId : path.DisplayName), 18);
                bool oldEnabled = GUI.enabled;
                GUI.enabled = oldEnabled && canChangePath && !path.IsCurrent;
                if (GUILayout.Button(path.IsCurrent ? label + " *" : label, GUILayout.Height(24f)))
                {
                    _session.RequestHeroSelectPath(slot.SlotIndex, slot.ActorGuid, path.PathId);
                }

                GUI.enabled = oldEnabled;
                drawn++;
            }

            GUILayout.EndHorizontal();
        }

        private void DrawMirrorHeroSelectAvailableHeroes(IList<HeroSelectHeroPayload> heroes, IList<int> localSlotIndexes)
        {
            IList<HeroSelectHeroPayload> availableHeroes = (heroes ?? Array.Empty<HeroSelectHeroPayload>())
                .Where(hero => hero != null && !hero.IsSelected)
                .OrderBy(hero => hero.ActorDataId)
                .ThenBy(hero => hero.ActorGuid)
                .ToList();
            if (availableHeroes.Count == 0)
            {
                DrawWrappedLabel("Available heroes: none");
                return;
            }

            GUILayout.Label("Available Heroes");
            int drawn = 0;
            GUILayout.BeginHorizontal();
            foreach (HeroSelectHeroPayload hero in availableHeroes)
            {
                if (drawn > 0 && drawn % 3 == 0)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }

                DrawMirrorHeroSelectHeroTile(hero, localSlotIndexes);
                drawn++;
            }

            GUILayout.EndHorizontal();
        }

        private void DrawMirrorHeroSelectHeroTile(HeroSelectHeroPayload hero, IList<int> localSlotIndexes)
        {
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(230f), GUILayout.MinHeight(138f));
            GUILayout.BeginHorizontal();
            DrawHeroSelectPortrait(hero.ActorDataId, 58f);
            GUILayout.BeginVertical();
            string displayName = string.IsNullOrWhiteSpace(hero.ActorName) ? hero.ActorDataId ?? "[hero]" : hero.ActorName;
            DrawWrappedLabel(CleanInline(displayName));
            DrawWrappedLabel("Path: " + CleanInline(hero.PathId ?? "[none]") +
                (hero.IsKingdomPreferred ? " | preferred" : string.Empty));
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            for (int i = 0; i < localSlotIndexes.Count; i++)
            {
                int slotIndex = localSlotIndexes[i];
                if (GUILayout.Button("Assign S" + (slotIndex + 1), GUILayout.Height(26f)))
                {
                    _session.RequestHeroSelectAssign(slotIndex, hero.ActorGuid);
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void DrawHeroSelectPortrait(string actorDataId, float size)
        {
            Rect portraitRect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            DrawSolidRect(portraitRect, new Color(0.04f, 0.045f, 0.05f, 1f));
            Sprite portrait = GetActorPortraitSprite(actorDataId);
            if (portrait != null)
            {
                DrawPortraitSprite(portraitRect, portrait);
            }
        }

        private void DrawMirrorLoot(LootWindowSnapshotPayload snapshot)
        {
            IList<LootItemSnapshotPayload> items = snapshot.Items ?? Array.Empty<LootItemSnapshotPayload>();
            SyncLootVoteSelection(snapshot);
            DrawMirrorHeader(
                "Loot",
                "reason=" + (snapshot.Reason ?? "[none]") +
                ", items=" + items.Count +
                ", takeAll=" + snapshot.CanTakeAll,
                MultiplayerSession.VoteKeyLoot);

            if (snapshot.HeroPoints != 0 || snapshot.TorchGain != 0 || snapshot.ArmorGain != 0 || snapshot.WheelGain != 0)
            {
                DrawWrappedLabel("Gains: heroPoints=" + snapshot.HeroPoints +
                    ", torch=" + snapshot.TorchGain +
                    ", armor=" + snapshot.ArmorGain +
                    ", wheels=" + snapshot.WheelGain);
            }

            if (items.Count == 0)
            {
                DrawWrappedLabel("No loot items.");
            }

            foreach (LootItemSnapshotPayload item in items.Where(item => item != null).OrderBy(item => item.InventoryIndex))
            {
                bool canSelect = item.InventoryIndex >= 0;
                bool selected = canSelect && _lootVoteSelectedIndexes.Contains(item.InventoryIndex);
                string displayName = GetLocalizedItemDisplayName(item.ItemId, item.DisplayName);
                string tooltip = BuildLootItemTooltip(item, displayName);
                GUILayout.BeginHorizontal(GUI.skin.box);
                Sprite itemSprite = GetItemSprite(item.ItemId);
                if (itemSprite != null)
                {
                    Rect iconRect = GUILayoutUtility.GetRect(30f, 30f, GUILayout.Width(30f), GUILayout.Height(30f));
                    DrawSprite(iconRect, itemSprite);
                    RegisterTooltip(iconRect, displayName, tooltip);
                }

                string label = (selected ? "[want] " : string.Empty) +
                    "#" + item.InventoryIndex +
                    " " + displayName +
                    " x" + item.Quantity +
                    (string.IsNullOrWhiteSpace(item.SlotType) ? string.Empty : " | " + item.SlotType);
                Rect labelRect = GUILayoutUtility.GetRect(420f, 30f, GUILayout.MinWidth(420f), GUILayout.Height(30f), GUILayout.ExpandWidth(true));
                GUI.Label(labelRect, label);
                RegisterTooltip(labelRect, displayName, tooltip);

                bool oldEnabled = GUI.enabled;
                GUI.enabled = oldEnabled && canSelect;
                if (GUILayout.Button(selected ? "Unwant" : "Want", GUILayout.Width(86f), GUILayout.Height(28f)))
                {
                    if (selected)
                    {
                        _lootVoteSelectedIndexes.Remove(item.InventoryIndex);
                    }
                    else
                    {
                        _lootVoteSelectedIndexes.Add(item.InventoryIndex);
                    }
                }

                GUI.enabled = oldEnabled;
                GUILayout.EndHorizontal();
            }

            List<LootItemSnapshotPayload> selectedItems = items
                .Where(item => item != null && _lootVoteSelectedIndexes.Contains(item.InventoryIndex))
                .ToList();
            DrawWrappedLabel("Selection: " + selectedItems.Count + " item(s).");

            GUILayout.BeginHorizontal();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && selectedItems.Count > 0;
            if (GUILayout.Button("Vote Selected", GUILayout.Height(30f)))
            {
                _session.RequestLootTakeSelected(selectedItems);
            }

            GUI.enabled = previousEnabled && snapshot.CanTakeAll;
            if (GUILayout.Button("Vote Take All", GUILayout.Height(30f)))
            {
                _session.RequestLootTakeAll();
            }

            GUI.enabled = previousEnabled;
            if (GUILayout.Button("Vote Discard All", GUILayout.Height(30f)))
            {
                _session.RequestLootDiscardAll();
            }

            GUI.enabled = previousEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawMirrorRouteChoice(RouteChoiceSnapshotPayload snapshot)
        {
            IList<RouteChoiceOptionPayload> choices = snapshot.Choices ?? Array.Empty<RouteChoiceOptionPayload>();
            DrawMirrorHeader(
                "Route Choice",
                "choices=" + choices.Count + "/" + snapshot.ChoiceCount +
                ", selected=" + snapshot.SelectedOptionIndex +
                FormatChoiceOverruleInline(snapshot.ChoiceOverruleEnabled, snapshot.ChoiceOverruleRemaining, snapshot.ChoiceOverruleLimitPerMap),
                MultiplayerSession.VoteKeyRoute);

            bool any = false;
            foreach (RouteChoiceOptionPayload choice in choices.Where(choice => choice != null).OrderBy(choice => choice.OptionIndex))
            {
                any = true;
                GUILayout.BeginHorizontal(GUI.skin.box);
                string nodeText = choice.IsRevealed ? (choice.NodeType ?? "[unknown]") : "Unknown";
                string subType = string.IsNullOrWhiteSpace(choice.NodeSubType) ? string.Empty : " / " + choice.NodeSubType;
                DrawWrappedLabel("[" + choice.OptionIndex + "] " +
                    (choice.Direction ?? "[direction]") +
                    " -> " + nodeText + subType);

                if (GUILayout.Button("Vote", GUILayout.Width(92f), GUILayout.Height(28f)))
                {
                    _session.RequestRouteChoice(choice.OptionIndex);
                }

                bool oldEnabled = GUI.enabled;
                if (ShouldShowChoiceOverruleButton())
                {
                    GUI.enabled = oldEnabled && CanUseChoiceOverrule(snapshot.ChoiceOverruleEnabled, snapshot.ChoiceOverruleRemaining);
                    if (GUILayout.Button("Force (" + snapshot.ChoiceOverruleRemaining + ")", GUILayout.Width(112f), GUILayout.Height(28f)))
                    {
                        _session.RequestRouteChoice(choice.OptionIndex, true);
                    }
                }

                GUI.enabled = oldEnabled;
                GUILayout.EndHorizontal();
            }

            if (!any)
            {
                DrawWrappedLabel("No choices.");
            }
        }

        private void DrawMirrorStoryChoice(StoryChoiceSnapshotPayload snapshot)
        {
            IList<StoryChoiceOptionPayload> choices = snapshot.Choices ?? Array.Empty<StoryChoiceOptionPayload>();
            DrawMirrorHeader(
                "Story Choice",
                "type=" + (snapshot.StoryType ?? "[none]") +
                ", engage=" + (snapshot.EngageType ?? "[none]") +
                ", choices=" + choices.Count +
                FormatChoiceOverruleInline(snapshot.ChoiceOverruleEnabled, snapshot.ChoiceOverruleRemaining, snapshot.ChoiceOverruleLimitPerMap),
                MultiplayerSession.VoteKeyStory);

            bool any = false;
            foreach (StoryChoiceOptionPayload choice in choices.Where(choice => choice != null).OrderBy(choice => choice.OptionIndex))
            {
                any = true;
                GUILayout.BeginHorizontal(GUI.skin.box);
                string owner = string.IsNullOrWhiteSpace(choice.OwnerName) ? "[unassigned]" : choice.OwnerName;
                string previews = FormatStoryPreviewList(choice.PlayerPreviews, "party");
                if (!string.IsNullOrWhiteSpace(previews))
                {
                    previews = " | " + previews;
                }

                DrawWrappedLabel("[" + choice.OptionIndex + "] slot " + choice.HeroSlot +
                    " | " + FormatStoryChoiceOption(choice) +
                    " | owner=" + owner +
                    previews);

                bool oldEnabled = GUI.enabled;
                GUI.enabled = oldEnabled && choice.CanChoose;
                if (GUILayout.Button("Vote", GUILayout.Width(92f), GUILayout.Height(28f)))
                {
                    _session.RequestStoryChoice(choice);
                }

                if (ShouldShowChoiceOverruleButton())
                {
                    GUI.enabled = oldEnabled && choice.CanChoose && CanUseChoiceOverrule(snapshot.ChoiceOverruleEnabled, snapshot.ChoiceOverruleRemaining);
                    if (GUILayout.Button("Force (" + snapshot.ChoiceOverruleRemaining + ")", GUILayout.Width(112f), GUILayout.Height(28f)))
                    {
                        _session.RequestStoryChoice(choice, true);
                    }
                }

                GUI.enabled = oldEnabled;
                GUILayout.EndHorizontal();
            }

            if (!any)
            {
                DrawWrappedLabel("No choices.");
            }
        }

        private void DrawMirrorInn(InnSnapshotPayload snapshot)
        {
            IList<InnBiomeChoicePayload> choices = snapshot.BiomeChoices ?? Array.Empty<InnBiomeChoicePayload>();
            DrawMirrorHeader(
                "Inn",
                "state=" + (snapshot.InnState ?? "[none]") +
                ", choices=" + choices.Count +
                ", canEmbark=" + snapshot.CanEmbark,
                MultiplayerSession.VoteKeyInnBiome);
            DrawVoteStatus(MultiplayerSession.VoteKeyInnEmbark);

            DrawMirrorChoiceRows(
                choices.Where(choice => choice != null).OrderBy(choice => choice.OptionIndex),
                choice =>
                    "[" + choice.OptionIndex + "] " +
                    CleanInline(choice.BiomeName ?? choice.BiomeType ?? "[biome]") +
                    (choice.IsEndBiome ? " | end" : string.Empty) +
                    (choice.IsSelected ? " | selected" : string.Empty) +
                    (string.IsNullOrWhiteSpace(choice.BiomeGoalName) ? string.Empty : " | goal=" + CleanInline(choice.BiomeGoalName)) +
                    (string.IsNullOrWhiteSpace(choice.BiomeModifierName) ? string.Empty : " | mod=" + CleanInline(choice.BiomeModifierName)) +
                    (string.IsNullOrWhiteSpace(choice.BiomeGoalDescription) ? string.Empty : "\nGoal: " + TrimPanelText(CleanTooltip(choice.BiomeGoalDescription), 160)) +
                    (string.IsNullOrWhiteSpace(choice.BiomeModifierDescription) ? string.Empty : "\nModifier: " + TrimPanelText(CleanTooltip(choice.BiomeModifierDescription), 160)),
                choice => "Vote Biome",
                choice => _session.RequestInnSelectBiome(choice.OptionIndex),
                choice => true);

            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && snapshot.CanEmbark;
            if (GUILayout.Button("Vote Embark", GUILayout.Height(30f)))
            {
                _session.RequestInnEmbark();
            }

            GUI.enabled = oldEnabled;
        }

        private void DrawMirrorEmbark(EmbarkSnapshotPayload snapshot)
        {
            DrawMirrorHeader(
                "Embark",
                "next=" + (snapshot.NextBiomeName ?? snapshot.NextBiomeType ?? "[none]") +
                ", relationships=" + snapshot.RelationshipCount +
                ", applied=" + snapshot.HasRelationshipsApplied +
                ", canContinue=" + snapshot.CanContinue,
                MultiplayerSession.VoteKeyEmbarkContinue);

            GUILayout.BeginHorizontal();
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && snapshot.CanApplyRelationships;
            if (GUILayout.Button("Apply Relationships", GUILayout.Height(30f)))
            {
                _session.RequestEmbarkApplyRelationships();
            }

            GUI.enabled = oldEnabled && snapshot.CanContinue;
            if (GUILayout.Button("Vote Continue", GUILayout.Height(30f)))
            {
                _session.RequestEmbarkContinue();
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawMirrorConfessionChoice(ConfessionChoiceSnapshotPayload snapshot)
        {
            IList<ConfessionChoiceOptionPayload> choices = snapshot.Choices ?? Array.Empty<ConfessionChoiceOptionPayload>();
            DrawMirrorHeader(
                "Confession Choice",
                "choices=" + choices.Count +
                ", selected=" + (snapshot.SelectedBossId ?? "[none]") +
                ", canChoose=" + snapshot.CanChoose,
                MultiplayerSession.VoteKeyConfessionChoice);

            DrawMirrorChoiceRows(
                choices.Where(choice => choice != null).OrderBy(choice => choice.OptionIndex),
                choice =>
                    "[" + choice.OptionIndex + "] " +
                    (choice.Label ?? choice.BossId ?? "[confession]") +
                    (choice.IsSelected ? " | selected" : string.Empty),
                choice => "Vote",
                choice => _session.RequestConfessionChoice(choice),
                choice => snapshot.CanChoose && choice.IsSelectable);
        }

        private void DrawMirrorLairDecision(LairDecisionSnapshotPayload snapshot)
        {
            DrawMirrorHeader(
                "Lair / Next Battle",
                "battle=" + snapshot.CurrentBattleIndex + "->" + snapshot.NextBattleIndex +
                "/" + snapshot.TotalBattles +
                ", rewards=" + snapshot.LootedRewardCount + "+" + snapshot.UpcomingRewardCount,
                MultiplayerSession.VoteKeyLairDecision);

            GUILayout.BeginHorizontal();
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && snapshot.CanContinue;
            if (GUILayout.Button("Vote Continue Battle", GUILayout.Height(30f)))
            {
                _session.RequestLairDecision("continue");
            }

            GUI.enabled = oldEnabled && snapshot.CanRetreat;
            if (GUILayout.Button("Vote Retreat", GUILayout.Height(30f)))
            {
                _session.RequestLairDecision("retreat");
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawMirrorConfirmationDialog(ConfirmationDialogSnapshotPayload snapshot)
        {
            DrawMirrorHeader(
                "Dialog",
                (snapshot.Title ?? snapshot.Kind ?? "[dialog]") +
                (snapshot.IsAllowed ? string.Empty : " | blocked"),
                MultiplayerSession.VoteKeyConfirmationDialog);

            if (!string.IsNullOrWhiteSpace(snapshot.Description))
            {
                DrawWrappedLabel(TrimPanelText(snapshot.Description, 260));
            }

            if (!snapshot.IsAllowed && !string.IsNullOrWhiteSpace(snapshot.BlockReason))
            {
                DrawWrappedLabel("Blocked: " + snapshot.BlockReason);
            }

            if (!snapshot.IsAllowed)
            {
                return;
            }

            GUILayout.BeginHorizontal();
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && snapshot.CanConfirm;
            if (GUILayout.Button(string.IsNullOrWhiteSpace(snapshot.ConfirmLabel) ? "Vote Confirm" : "Vote " + snapshot.ConfirmLabel, GUILayout.Height(30f)))
            {
                _session.RequestConfirmationDialog("confirm");
            }

            GUI.enabled = oldEnabled && snapshot.CanDecline;
            if (GUILayout.Button(string.IsNullOrWhiteSpace(snapshot.DeclineLabel) ? "Vote Decline" : "Vote " + snapshot.DeclineLabel, GUILayout.Height(30f)))
            {
                _session.RequestConfirmationDialog("decline");
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawMirrorGameResults(GameResultsSnapshotPayload snapshot)
        {
            DrawMirrorHeader(
                "Game Results",
                "state=" + (snapshot.ScreenState ?? "[none]") +
                ", reason=" + (snapshot.GameOverReason ?? "[none]") +
                ", canContinue=" + snapshot.CanContinue,
                MultiplayerSession.VoteKeyGameResults);

            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && snapshot.CanContinue;
            if (GUILayout.Button("Vote Continue Results", GUILayout.Height(30f)))
            {
                _session.RequestGameResultsContinue();
            }

            GUI.enabled = oldEnabled;
        }

        private void DrawMirrorStore(StoreSnapshotPayload snapshot)
        {
            IList<StoreItemPayload> items = snapshot.Items ?? Array.Empty<StoreItemPayload>();
            DrawMirrorHeader(
                "Store",
                "kind=" + (snapshot.StoreKind ?? "[none]") +
                ", state=" + (snapshot.ScreenState ?? "[none]") +
                ", items=" + items.Count,
                null);

            IList<StoreItemPayload> visibleItems = items
                .Where(item => item != null)
                .OrderBy(item => item.InventoryIndex)
                .ToList();
            if (visibleItems.Count == 0)
            {
                DrawWrappedLabel("Items: none / out of stock.");
                return;
            }

            bool oldEnabled = GUI.enabled;
            const float tileWidth = 220f;
            const float tileHeight = 118f;
            int columns = Mathf.Max(1, Mathf.FloorToInt((Screen.width - 140f) / (tileWidth + 12f)));
            for (int i = 0; i < visibleItems.Count; i += columns)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < columns && i + column < visibleItems.Count; column++)
                {
                    StoreItemPayload item = visibleItems[i + column];
                    Rect tile = GUILayoutUtility.GetRect(tileWidth, tileHeight, GUILayout.Width(tileWidth), GUILayout.Height(tileHeight));
                    DrawStoreItemTile(tile, item, oldEnabled);
                    GUILayout.Space(10f);
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(8f);
            }
        }

        private void DrawStoreItemTile(Rect tile, StoreItemPayload item, bool oldEnabled)
        {
            DrawSolidRect(tile, HudTileColor);

            Rect iconRect = new Rect(tile.x + 10f, tile.y + 12f, 56f, 56f);
            DrawSolidRect(iconRect, new Color(0.05f, 0.055f, 0.06f, 1f));
            DrawSprite(iconRect, GetItemSprite(item.ItemId));

            string displayName = CleanInline(string.IsNullOrWhiteSpace(item.DisplayName) ? item.ItemId : item.DisplayName);
            GUIStyle title = CreateHudLabelStyle(12, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            title.wordWrap = true;
            GUIStyle meta = CreateHudLabelStyle(11, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperLeft);
            GUI.Label(new Rect(iconRect.xMax + 8f, tile.y + 10f, tile.width - 84f, 40f), displayName, title);
            GUI.Label(new Rect(iconRect.xMax + 8f, tile.y + 52f, tile.width - 84f, 18f),
                "x" + item.Quantity + (string.IsNullOrWhiteSpace(item.ItemType) ? string.Empty : " | " + item.ItemType),
                meta);

            string price = CleanInline(item.PriceText);
            GUI.Label(new Rect(tile.x + 10f, tile.y + 74f, tile.width - 20f, 18f),
                string.IsNullOrWhiteSpace(price) ? "[no price]" : price,
                meta);

            bool enabled = oldEnabled && item.CanAfford && item.Quantity > 0;
            GUI.enabled = enabled;
            if (GUI.Button(new Rect(tile.xMax - 74f, tile.yMax - 32f, 62f, 24f), "Buy"))
            {
                _session.RequestStorePurchase(item.InventoryIndex, item.ItemId);
            }

            GUI.enabled = oldEnabled;
            if (!item.CanAfford)
            {
                GUI.Label(new Rect(tile.x + 10f, tile.yMax - 29f, tile.width - 90f, 18f), "Cannot afford", meta);
            }

            RegisterTooltip(tile, displayName, BuildStoreItemTooltip(item));
        }

        private void DrawMirrorStagecoach(StagecoachSnapshotPayload snapshot)
        {
            IList<StagecoachItemPayload> playerItems = snapshot.PlayerItems ?? Array.Empty<StagecoachItemPayload>();
            IList<StagecoachSlotPayload> slots = snapshot.Slots ?? Array.Empty<StagecoachSlotPayload>();
            DrawMirrorHeader(
                "Stagecoach",
                "armor=" + snapshot.Armor + "/" + snapshot.MaxArmor +
                ", wheels=" + snapshot.Wheels + "/" + snapshot.MaxWheels +
                ", editable=" + snapshot.IsEditable,
                null);

            DrawStagecoachRepairRow(snapshot.ArmorRepair);
            DrawStagecoachRepairRow(snapshot.WheelRepair);

            IList<StagecoachSlotPayload> generalSlots = slots
                .Where(slot => slot != null && IsMirrorStagecoachActionSlot(slot.SlotType))
                .OrderBy(slot => slot.SlotIndex)
                .ToList();
            if (generalSlots.Count > 0)
            {
                GUILayout.Label("General Slots");
                foreach (StagecoachSlotPayload slot in generalSlots)
                {
                    DrawMirrorStagecoachSlot(snapshot, playerItems, slot, true);
                }
            }

            IList<StagecoachSlotPayload> hostOnlySlots = slots
                .Where(slot => slot != null && !IsMirrorStagecoachActionSlot(slot.SlotType))
                .OrderBy(slot => StagecoachSlotOrder(slot.SlotType))
                .ThenBy(slot => slot.SlotIndex)
                .ToList();
            if (hostOnlySlots.Count > 0)
            {
                GUILayout.Label("Host-Only Slots");
                foreach (StagecoachSlotPayload slot in hostOnlySlots)
                {
                    DrawMirrorStagecoachSlot(snapshot, playerItems, slot, false);
                }
            }
        }

        private void DrawMirrorStagecoachSlot(
            StagecoachSnapshotPayload snapshot,
            IList<StagecoachItemPayload> playerItems,
            StagecoachSlotPayload slot,
            bool allowActions)
        {
            GUILayout.BeginVertical(GUI.skin.box);

            StagecoachItemPayload equipped = slot.Item;
            string currentItemText = equipped == null
                ? "[empty]"
                : (equipped.DisplayName ?? equipped.ItemId ?? "[item]") +
                    " x" + equipped.Quantity +
                    (equipped.IsUnequipInvalid ? " | locked" : string.Empty);

            GUILayout.BeginHorizontal();
            GUILayout.Label((slot.SlotType ?? "[slot]") +
                " #" + slot.SlotIndex +
                ": " + currentItemText +
                (allowActions ? string.Empty : " | host-only"),
                GUILayout.MinWidth(420f));

            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && allowActions && snapshot.IsEditable && equipped != null && slot.CanUnequip;
            if (GUILayout.Button("Unequip", GUILayout.Width(90f), GUILayout.Height(28f)))
            {
                _session.RequestStagecoachUnequip(slot.SlotType, slot.SlotIndex, equipped.ItemId);
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();

            if (allowActions)
            {
                IList<StagecoachItemPayload> compatibleItems = (playerItems ?? Array.Empty<StagecoachItemPayload>())
                    .Where(item => item != null &&
                        item.CanEquip &&
                        string.Equals(item.SlotType, slot.SlotType, StringComparison.Ordinal))
                    .OrderBy(item => item.InventoryIndex)
                    .ToList();
                foreach (StagecoachItemPayload item in compatibleItems)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(18f);
                    GUILayout.Label("Bag #" + item.InventoryIndex +
                        " " + (item.DisplayName ?? item.ItemId ?? "[item]") +
                        " x" + item.Quantity,
                        GUILayout.MinWidth(360f));

                    GUI.enabled = oldEnabled && snapshot.IsEditable && slot.CanAcceptItems;
                    if (GUILayout.Button("Equip Here", GUILayout.Width(110f), GUILayout.Height(28f)))
                    {
                        _session.RequestStagecoachEquip(
                            item.InventoryIndex,
                            item.ItemId,
                            slot.SlotType,
                            slot.SlotIndex);
                    }

                    GUI.enabled = oldEnabled;
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.EndVertical();
        }

        private static bool IsMirrorStagecoachActionSlot(string slotType)
        {
            return string.Equals(slotType, "general", StringComparison.OrdinalIgnoreCase);
        }

        private void DrawMirrorLoadout(HeroLoadoutSnapshotPayload snapshot)
        {
            IList<HeroLoadoutActorPayload> actors = snapshot.Actors ?? Array.Empty<HeroLoadoutActorPayload>();
            DrawMirrorHeader(
                "Loadout",
                "scope=" + (snapshot.Scope ?? "[none]") +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", mastery=" + snapshot.HeroUpgradePoints +
                ", trainer=" + snapshot.CanMasterSkills,
                null);

            if (actors.Count == 0)
            {
                DrawWrappedLabel("Actors: none.");
                return;
            }

            DrawHeroRestItemPanelSection(snapshot, actors);

            foreach (HeroLoadoutActorPayload actor in actors
                .Where(actor => actor != null)
                .OrderByDescending(IsLocalHeroLoadoutOwner)
                .ThenBy(actor => actor.HeroSlot))
            {
                DrawMirrorLoadoutActor(snapshot, actor);
            }
        }

        private void DrawMirrorLoadoutActor(HeroLoadoutSnapshotPayload snapshot, HeroLoadoutActorPayload actor)
        {
            bool isLocalOwner = IsLocalHeroLoadoutOwner(actor);
            string ownerText = string.IsNullOrWhiteSpace(actor.OwnerName)
                ? "[unassigned]"
                : actor.OwnerName;
            string actorKey = string.IsNullOrWhiteSpace(actor.ActorGuid)
                ? "slot:" + actor.HeroSlot
                : actor.ActorGuid;
            bool expanded = isLocalOwner || _expandedLoadoutActorGuids.Contains(actorKey);

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            if (!isLocalOwner)
            {
                if (GUILayout.Button(expanded ? "-" : "+", GUILayout.Width(28f), GUILayout.Height(24f)))
                {
                    if (expanded)
                    {
                        _expandedLoadoutActorGuids.Remove(actorKey);
                    }
                    else
                    {
                        _expandedLoadoutActorGuids.Add(actorKey);
                    }
                    expanded = !expanded;
                }
            }
            else
            {
                GUILayout.Label("[you]", GUILayout.Width(44f));
            }

            DrawWrappedLabel("Slot " + actor.HeroSlot +
                " " + FormatLoadoutActor(actor) +
                " | owner=" + ownerText +
                " | skills=" + actor.EquippedSkillCount + "/" + actor.EquippedSkillLimit);
            GUILayout.EndHorizontal();

            if (!expanded)
            {
                GUILayout.EndVertical();
                return;
            }

            DrawHeroEquipmentPanelSection(snapshot, actor, isLocalOwner);

            IList<HeroLoadoutSkillPayload> skills = actor.Skills ?? Array.Empty<HeroLoadoutSkillPayload>();
            foreach (HeroLoadoutSkillPayload skill in skills
                .Where(skill => skill != null)
                .OrderByDescending(skill => skill.IsEquipped)
                .ThenBy(skill => skill.DisplayName ?? skill.SkillId ?? string.Empty))
            {
                DrawMirrorLoadoutSkillRow(snapshot, actor, skill, isLocalOwner);
            }

            GUILayout.EndVertical();
        }

        private void DrawMirrorLoadoutSkillRow(
            HeroLoadoutSnapshotPayload snapshot,
            HeroLoadoutActorPayload actor,
            HeroLoadoutSkillPayload skill,
            bool isLocalOwner)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(18f);

            string state = skill.IsEquipped ? "[E]" : "[ ]";
            string flags =
                (skill.IsUpgraded ? " mastered" : string.Empty) +
                (!skill.IsUnlocked ? " locked" : string.Empty) +
                (skill.IsAlwaysEquipped ? " fixed" : string.Empty);
            Sprite skillSprite = GetSkillSprite(skill.SkillId);
            if (skillSprite != null)
            {
                Rect iconRect = GUILayoutUtility.GetRect(26f, 26f, GUILayout.Width(26f), GUILayout.Height(26f));
                DrawSprite(iconRect, skillSprite);
            }

            string displayName = CleanInline(string.IsNullOrWhiteSpace(skill.DisplayName) ? skill.SkillId : skill.DisplayName);
            Rect labelRect = GUILayoutUtility.GetRect(300f, 26f, GUILayout.MinWidth(300f), GUILayout.Height(26f));
            GUI.Label(labelRect, state + " " + displayName + flags);
            RegisterTooltip(labelRect, displayName, skill.Description);

            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && isLocalOwner && actor.CanEditSkills && skill.CanEquip;
            if (GUILayout.Button("Equip", GUILayout.Width(76f), GUILayout.Height(26f)))
            {
                _session.RequestHeroLoadoutSkill(actor.HeroSlot, actor.ActorGuid, skill.SkillId, true);
            }

            GUI.enabled = oldEnabled && isLocalOwner && actor.CanEditSkills && skill.CanUnequip;
            if (GUILayout.Button("Unequip", GUILayout.Width(86f), GUILayout.Height(26f)))
            {
                _session.RequestHeroLoadoutSkill(actor.HeroSlot, actor.ActorGuid, skill.SkillId, false);
            }

            GUI.enabled = oldEnabled && isLocalOwner && snapshot.CanMasterSkills && skill.CanMaster;
            if (GUILayout.Button("Master", GUILayout.Width(82f), GUILayout.Height(26f)))
            {
                _session.RequestHeroMasterSkill(actor.HeroSlot, actor.ActorGuid, skill.SkillId);
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawMirrorChoiceRows<T>(
            IEnumerable<T> choices,
            Func<T, string> labelSelector,
            Func<T, string> buttonLabelSelector,
            Action<T> onClick,
            Func<T, bool> enabledSelector)
        {
            bool any = false;
            foreach (T choice in choices)
            {
                any = true;
                GUILayout.BeginHorizontal(GUI.skin.box);
                DrawWrappedLabel(labelSelector(choice));

                bool oldEnabled = GUI.enabled;
                GUI.enabled = oldEnabled && enabledSelector(choice);
                if (GUILayout.Button(buttonLabelSelector(choice), GUILayout.Width(110f), GUILayout.Height(28f)))
                {
                    onClick(choice);
                }

                GUI.enabled = oldEnabled;
                GUILayout.EndHorizontal();
            }

            if (!any)
            {
                DrawWrappedLabel("No choices.");
            }
        }

        private bool CanUseChoiceOverrule(bool enabled, int remaining)
        {
            return enabled &&
                remaining > 0 &&
                _lobbyClient != null &&
                _lobbyClient.IsInLobby &&
                !_lobbyClient.IsHost;
        }

        private bool ShouldShowChoiceOverruleButton()
        {
            return _lobbyClient != null &&
                _lobbyClient.IsInLobby &&
                !_lobbyClient.IsHost;
        }

        private static string FormatChoiceOverruleInline(bool enabled, int remaining, int limitPerMap)
        {
            return enabled
                ? ", overrule=" + remaining + "/" + limitPerMap
                : ", overrule=off";
        }

        private static string FormatMirrorActorName(ActorSnapshotPayload actor)
        {
            if (actor == null)
            {
                return "[actor]";
            }

            return string.IsNullOrWhiteSpace(actor.ActorDataId)
                ? actor.ActorGuid ?? "[actor]"
                : actor.ActorDataId;
        }

        private bool DrawHomeSnapshotRows()
        {
            bool any = false;

            ConfirmationDialogSnapshotPayload dialog;
            if (_session != null && _session.TryGetLatestConfirmationDialogSnapshot(out dialog) && dialog != null && dialog.IsActive)
            {
                DrawHomeActionRow(
                    "Dialog",
                    (dialog.Kind ?? "[dialog]") +
                    ", type=" + (dialog.DialogType ?? "[none]") +
                    ", confirm=" + dialog.CanConfirm +
                    ", decline=" + dialog.CanDecline,
                    PanelTab.Decisions,
                    MultiplayerSession.VoteKeyConfirmationDialog);
                any = true;
            }

            MainMenuSnapshotPayload mainMenu;
            if (_session != null && _session.TryGetLatestMainMenuSnapshot(out mainMenu) && mainMenu != null && mainMenu.IsActive)
            {
                DrawHomeActionRow(
                    "Main Menu",
                    "save=" + mainMenu.HasExpeditionSave +
                    ", continue=" + mainMenu.CanContinueExpedition +
                    ", new=" + mainMenu.CanStartNewExpedition,
                    PanelTab.Run,
                    MultiplayerSession.VoteKeyMainMenu);
                any = true;
            }

            HeroSelectSnapshotPayload heroSelect;
            if (_session != null && _session.TryGetLatestHeroSelectSnapshot(out heroSelect) && heroSelect != null && heroSelect.IsActive)
            {
                int slotCount = heroSelect.Slots == null ? 0 : heroSelect.Slots.Count;
                int heroCount = heroSelect.Heroes == null ? 0 : heroSelect.Heroes.Count;
                DrawHomeActionRow(
                    "Hero Select",
                    "confirmed=" + heroSelect.RosterConfirmed +
                    ", canConfirm=" + heroSelect.CanConfirm +
                    ", slots=" + slotCount +
                    ", heroes=" + heroCount,
                    PanelTab.Run,
                    MultiplayerSession.VoteKeyHeroReady);
                any = true;
            }

            HeroLoadoutSnapshotPayload loadout;
            if (_session != null && _session.TryGetLatestHeroLoadoutSnapshot(out loadout) && loadout != null && loadout.IsActive)
            {
                int actorCount = loadout.Actors == null ? 0 : loadout.Actors.Count;
                DrawHomeActionRow(
                    "Loadout",
                    "scope=" + (loadout.Scope ?? "[none]") +
                    ", mode=" + (loadout.CurrentGameMode ?? "[none]") +
                    ", mastery=" + loadout.HeroUpgradePoints +
                    ", trainer=" + loadout.CanMasterSkills +
                    ", actors=" + actorCount,
                    PanelTab.Loadout,
                    null);
                any = true;
            }

            CombatSnapshotPayload combat;
            if (_session != null && _session.TryGetLatestCombatSnapshot(out combat) && combat != null && (combat.PartyInBattle || (combat.Actors != null && combat.Actors.Count > 0)))
            {
                int turnOrderCount = combat.TurnOrder == null ? 0 : combat.TurnOrder.Count;
                string selectedSkill = combat.SelectedSkill == null
                    ? string.Empty
                    : ", selected=" + (combat.SelectedSkill.DisplayName ?? combat.SelectedSkill.SkillId ?? "[skill]") +
                        " targets=" + (combat.SelectedSkill.ValidTargets == null ? 0 : combat.SelectedSkill.ValidTargets.Count);
                DrawHomeActionRow(
                    "Combat State",
                    "state=" + (combat.BattleState ?? "[none]") +
                    ", r" + combat.Round + "/t" + combat.Turn +
                    ", current=" + (combat.CurrentActorName ?? combat.CurrentActorGuid ?? "[none]") +
                    ", order=" + turnOrderCount +
                    selectedSkill,
                    PanelTab.Combat,
                    null);
                any = true;
            }

            LootWindowSnapshotPayload loot;
            if (_session != null &&
                !IsLocalPvpEnemyController() &&
                _session.TryGetLatestLootWindowSnapshot(out loot) &&
                loot != null &&
                loot.IsActive)
            {
                int itemCount = loot.Items == null ? 0 : loot.Items.Count;
                DrawHomeActionRow(
                    "Loot",
                    "reason=" + (loot.Reason ?? "[none]") +
                    ", items=" + itemCount +
                    ", takeAll=" + loot.CanTakeAll,
                    PanelTab.Rewards,
                    MultiplayerSession.VoteKeyLoot);
                any = true;
            }

            GameResultsSnapshotPayload results;
            if (_session != null && _session.TryGetLatestGameResultsSnapshot(out results) && results != null && results.IsActive)
            {
                DrawHomeActionRow(
                    "Results",
                    "reason=" + (results.GameOverReason ?? "[none]") +
                    ", hasScore=" + results.HasScore +
                    ", continue=" + results.CanContinue,
                    PanelTab.Rewards,
                    MultiplayerSession.VoteKeyGameResults);
                any = true;
            }

            StoreSnapshotPayload store;
            if (_session != null && _session.TryGetLatestStoreSnapshot(out store) && store != null && store.IsActive)
            {
                int itemCount = store.Items == null ? 0 : store.Items.Count;
                DrawHomeActionRow(
                    "Store",
                    "kind=" + (store.StoreKind ?? "[none]") +
                    ", items=" + itemCount,
                    PanelTab.Rewards,
                    null);
                any = true;
            }

            StagecoachSnapshotPayload stagecoach;
            if (_session != null && _session.TryGetLatestStagecoachSnapshot(out stagecoach) && stagecoach != null && stagecoach.IsActive)
            {
                int slotCount = stagecoach.Slots == null ? 0 : stagecoach.Slots.Count;
                DrawHomeActionRow(
                    "Stagecoach",
                    "editable=" + stagecoach.IsEditable +
                    ", armor=" + stagecoach.Armor + "/" + stagecoach.MaxArmor +
                    ", wheels=" + stagecoach.Wheels + "/" + stagecoach.MaxWheels +
                    ", slots=" + slotCount,
                    PanelTab.Coach,
                    null);
                any = true;
            }

            InnSnapshotPayload inn;
            if (_session != null && _session.TryGetLatestInnSnapshot(out inn) && inn != null && inn.IsActive)
            {
                int choiceCount = inn.BiomeChoices == null ? 0 : inn.BiomeChoices.Count;
                DrawHomeActionRow(
                    "Inn",
                    "state=" + (inn.InnState ?? "[none]") +
                    ", choices=" + choiceCount +
                    ", selected=" + inn.SelectedBiomeChoiceIndex +
                    ", embark=" + inn.CanEmbark,
                    PanelTab.Decisions,
                    MultiplayerSession.VoteKeyInnBiome);
                any = true;
            }

            EmbarkSnapshotPayload embark;
            if (_session != null && _session.TryGetLatestEmbarkSnapshot(out embark) && embark != null && embark.IsActive)
            {
                DrawHomeActionRow(
                    "Embark",
                    "next=" + (embark.NextBiomeName ?? embark.NextBiomeType ?? "[none]") +
                    ", relationships=" + embark.RelationshipCount +
                    ", applied=" + embark.HasRelationshipsApplied +
                    ", continue=" + embark.CanContinue,
                    PanelTab.Decisions,
                    MultiplayerSession.VoteKeyEmbarkContinue);
                any = true;
            }

            AltarSnapshotPayload altar;
            if (_session != null && _session.TryGetLatestAltarSnapshot(out altar) && altar != null && altar.IsActive)
            {
                int trackCount = altar.Tracks == null ? 0 : altar.Tracks.Count;
                int rewardCount = altar.RewardButtons == null ? 0 : altar.RewardButtons.Count;
                DrawHomeActionRow(
                    "Altar",
                    "screen=" + (altar.ActiveSubscreen ?? "[none]") +
                    ", candles=" + altar.CandleCount +
                    ", tracks=" + trackCount +
                    ", rewards=" + rewardCount +
                    ", leave=" + altar.CanEmbark,
                    PanelTab.Decisions,
                    MultiplayerSession.VoteKeyAltarEmbark);
                any = true;
            }

            ConfessionChoiceSnapshotPayload confession;
            if (_session != null && _session.TryGetLatestConfessionChoiceSnapshot(out confession) && confession != null && confession.IsActive)
            {
                int choiceCount = confession.Choices == null ? 0 : confession.Choices.Count;
                DrawHomeActionRow(
                    "Confession",
                    "choices=" + choiceCount +
                    ", selected=" + (confession.SelectedBossId ?? "[none]") +
                    ", canChoose=" + confession.CanChoose,
                    PanelTab.Decisions,
                    MultiplayerSession.VoteKeyConfessionChoice);
                any = true;
            }

            LairDecisionSnapshotPayload lair;
            if (_session != null && _session.TryGetLatestLairDecisionSnapshot(out lair) && lair != null && lair.IsActive)
            {
                DrawHomeActionRow(
                    "Lair",
                    "battle=" + lair.CurrentBattleIndex + "->" + lair.NextBattleIndex +
                    "/" + lair.TotalBattles +
                    ", continue=" + lair.CanContinue +
                    ", retreat=" + lair.CanRetreat,
                    PanelTab.Decisions,
                    MultiplayerSession.VoteKeyLairDecision);
                any = true;
            }

            RouteChoiceSnapshotPayload route;
            if (_session != null && _session.TryGetLatestRouteChoiceSnapshot(out route) && route != null && route.IsActive)
            {
                int choiceCount = route.Choices == null ? 0 : route.Choices.Count;
                DrawHomeActionRow(
                    "Route",
                    "choices=" + choiceCount +
                    ", selected=" + route.SelectedOptionIndex,
                    PanelTab.Decisions,
                    MultiplayerSession.VoteKeyRoute);
                any = true;
            }

            StoryChoiceSnapshotPayload story;
            if (_session != null && _session.TryGetLatestStoryChoiceSnapshot(out story) && story != null && story.IsActive)
            {
                int choiceCount = story.Choices == null ? 0 : story.Choices.Count;
                DrawHomeActionRow(
                    "Story",
                    "type=" + (story.StoryType ?? "[none]") +
                    ", choices=" + choiceCount +
                    ", selected=" + (story.SelectedActorGuid ?? "[none]"),
                    PanelTab.Decisions,
                    MultiplayerSession.VoteKeyStory);
                any = true;
            }

            return any;
        }

        private void DrawHomeActionRow(string label, string summary, PanelTab targetTab, string voteKey)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(118f));
            GUILayout.BeginVertical();
            DrawWrappedLabel((summary ?? string.Empty) + FormatVoteInline(voteKey));
            GUILayout.EndVertical();
            bool hasCompactTarget = HasCompactPanelTarget(targetTab);
            if (hasCompactTarget)
            {
                if (GUILayout.Button(Ui("Open", "打开"), GUILayout.Width(78f), GUILayout.Height(28f)))
                {
                    OpenPanelTarget(targetTab);
                }
            }
            else
            {
                GUILayout.Space(82f);
            }

            GUILayout.EndHorizontal();
        }

        private void DrawHomeVoteSummary()
        {
            GUILayout.Label(Ui("Active Votes", "当前投票"));
            bool any = false;
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyMainMenu, "Main Menu");
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyHeroReady, "Hero Ready");
            if (!IsLocalPvpEnemyController())
            {
                any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyLoot, "Loot");
            }
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyGameResults, "Results");
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyRoute, "Route");
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyStory, "Story");
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyInnBiome, "Inn Biome");
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyInnEmbark, "Inn Embark");
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyEmbarkContinue, "Embark");
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyAltarEmbark, "Altar");
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyConfessionChoice, "Confession");
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyLairDecision, "Lair");
            any |= DrawHomeVoteSummaryLine(MultiplayerSession.VoteKeyConfirmationDialog, "Dialog");

            if (!any)
            {
                DrawWrappedLabel(Ui("No active vote.", "当前没有投票。"));
            }
        }

        private bool DrawHomeVoteSummaryLine(string voteKey, string label)
        {
            VoteStatusPayload status;
            if (_session == null ||
                !_session.TryGetLatestVoteStatus(voteKey, out status) ||
                status == null ||
                !status.IsActive)
            {
                return false;
            }

            DrawWrappedLabel("  " + label +
                ": " + status.VotedCount + "/" + status.RequiredCount +
                ", resolved=" + status.IsResolved +
                (string.IsNullOrWhiteSpace(status.Resolution) ? string.Empty : ", result=" + status.Resolution));
            return true;
        }

        private string FormatVoteInline(string voteKey)
        {
            if (string.IsNullOrWhiteSpace(voteKey) ||
                _session == null ||
                (IsLocalPvpEnemyController() &&
                    string.Equals(voteKey, MultiplayerSession.VoteKeyLoot, StringComparison.Ordinal)))
            {
                return string.Empty;
            }

            VoteStatusPayload status;
            if (!_session.TryGetLatestVoteStatus(voteKey, out status) || status == null || !status.IsActive)
            {
                return string.Empty;
            }

            return " | vote=" + status.VotedCount + "/" + status.RequiredCount +
                (status.IsResolved ? " resolved" : string.Empty);
        }

        private void DrawLobbyPanelSection()
        {
            GUILayout.Label(Ui("Lobby", "房间"));
            GUILayout.Label(GetLobbyPanelStatus());
            GUILayout.Label(GetPvpPanelStatus());
            DrawWrappedLabel(GetVersionPanelStatus());

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Ui("Host 4", "开 4 人房"), GUILayout.Height(28f)))
            {
                _lobbyClient.CreateLobby(4);
            }

            if (GUILayout.Button(Ui("Invite", "邀请"), GUILayout.Height(28f)))
            {
                _lobbyClient.OpenInviteDialog();
            }

            if (GUILayout.Button(Ui("Leave", "离开"), GUILayout.Height(28f)))
            {
                _lobbyClient.LeaveLobby(_messageTransport);
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            _panelJoinLobbyId = GUILayout.TextField(_panelJoinLobbyId ?? string.Empty);
            if (GUILayout.Button(Ui("Join", "加入"), GUILayout.Width(80f)))
            {
                TryJoinFromPanel();
            }

            GUILayout.EndHorizontal();
        }

        private void DrawSlotPanelSection()
        {
            GUILayout.Label(Ui("Hero Slots", "英雄槽位"));

            for (int slot = 1; slot <= 4; slot++)
            {
                HeroSlotAssignmentPayload owner;
                string ownerText = _session.TryGetHeroSlotOwner(slot, out owner)
                    ? owner.Name + "/" + owner.SteamId
                    : Ui("[unassigned]", "[未分配]");
                GUILayout.Label(Ui("Slot ", "槽位 ") + slot + " (hero pos " + (slot - 1) + "): " + ownerText);
            }

            if (!_lobbyClient.IsInLobby)
            {
                GUILayout.Label(Ui("No active lobby.", "当前没有房间。"));
                return;
            }

            if (!_lobbyClient.IsHost)
            {
                GUILayout.Label(Ui("Only host can assign slots.", "只有房主可以分配槽位。"));
                return;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Ui("Auto Fill", "自动填空位"), GUILayout.Height(28f)))
            {
                _session.AutoAssignHeroSlots(false);
            }

            if (GUILayout.Button(Ui("Auto Replace", "重新分配"), GUILayout.Height(28f)))
            {
                _session.AutoAssignHeroSlots(true);
            }

            GUILayout.EndHorizontal();

            IReadOnlyList<CSteamID> members = _lobbyClient.GetMembers();
            for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
            {
                CSteamID member = members[memberIndex];
                GUILayout.BeginHorizontal();
                GUILayout.Label(FormatLobbyMember(member), GUILayout.Width(220f));
                for (int slot = 1; slot <= 4; slot++)
                {
                    if (GUILayout.Button(slot.ToString(), GUILayout.Width(42f)))
                    {
                        _session.AssignHeroSlot(slot, member.m_SteamID.ToString());
                    }
                }

                GUILayout.EndHorizontal();
            }
        }

        private void DrawPvpPanelSection()
        {
            GUILayout.Label(Ui("PVP Enemy Pilot", "PVP 敌方操作者"));
            DrawWrappedLabel(Ui(
                "Host can assign one lobby member to the enemy side. That player waits outside hero/loadout sync and only controls enemy turns during combat.",
                "房主可以指定一名房间成员控制敌方。该玩家不接收英雄和配装同步，只在战斗中的敌方回合操作。"));

            PvpModeStatePayload state = null;
            bool hasState = _session != null && _session.TryGetPvpModeState(out state) && state != null;
            if (hasState && state.Enabled)
            {
                DrawWrappedLabel(Ui("Enabled: ", "已启用：") + (state.Mode ?? "[none]") +
                    " | enemy=" + (state.EnemyControllerName ?? "[none]") +
                    "/" + state.EnemyControllerSteamId +
                    " | runtimeInput=" + state.RuntimeEnemyInput +
                    " | suppressHeroSync=" + state.SuppressHeroSyncForEnemyController);
            }
            else
            {
                DrawWrappedLabel(Ui(
                    "Disabled. Host can assign one lobby member to control enemy turns during combat.",
                    "未启用。房主可以指定一名成员在战斗中控制敌方回合。"));
            }

            if (_session == null || _lobbyClient == null || !_lobbyClient.IsInLobby)
            {
                DrawWrappedLabel(Ui("No active lobby.", "当前没有房间。"));
                return;
            }

            if (!_lobbyClient.IsHost)
            {
                DrawWrappedLabel(Ui("Only host can change PVP mode.", "只有房主可以修改 PVP 模式。"));
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Pick an enemy pilot from the member list below.", "从下方成员列表选择敌方操作者。"));
            if (GUILayout.Button(Ui("Disable", "关闭"), GUILayout.Width(88f), GUILayout.Height(26f)))
            {
                _session.SetPvpEnemyPilot(false, string.Empty);
            }

            GUILayout.EndHorizontal();

            IReadOnlyList<CSteamID> members = _lobbyClient.GetMembers();
            for (int i = 0; i < members.Count; i++)
            {
                CSteamID member = members[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(FormatLobbyMember(member), GUILayout.Width(220f));
                if (GUILayout.Button(Ui("Enemy Pilot", "设为敌方"), GUILayout.Width(112f), GUILayout.Height(24f)))
                {
                    _session.SetPvpEnemyPilot(true, member.m_SteamID.ToString());
                }

                GUILayout.EndHorizontal();
            }
        }

        private void DrawArenaPanelSection()
        {
            GUILayout.Label(Ui("Arena", "竞技场"));
            DrawWrappedLabel(GetLobbyPanelStatus() + " | " + GetPvpPanelStatus());

            DrawPvpPanelSection();
            DrawPanelSeparator();
            DrawArenaSetupPanelSection();
        }

        private void DrawArenaSetupPanelSection()
        {
            GUILayout.Label(Ui("Custom Battle Setup", "自定义战斗设置"));
            DrawWrappedLabel(Ui("Status: ", "状态：") + _arenaStatus);

            DrawWrappedLabel(Ui(
                "Flow: host opens Browse to pick an official enemy preset, optionally queues waves/chains in that window, opens Hero Setup to edit the hero draft, then launches the draft. Enemy-side players are assigned above and should wait for combat turns.",
                "流程：房主点“浏览”选择官方敌人预设，可在浏览窗口里加入单波或官方连战队列；再打开“英雄设置”调整英雄草案，最后启动草案。被分配到敌方的玩家等待战斗中的敌方回合即可。"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Battle", "战斗"), GUILayout.Width(64f));
            GUILayout.Label(TrimPanelText(string.IsNullOrWhiteSpace(_arenaBattleConfigId) ? "[none]" : _arenaBattleConfigId, 60), GUILayout.MinWidth(180f), GUILayout.ExpandWidth(true));
            if (GUILayout.Button(Ui("Browse", "浏览"), GUILayout.Width(82f), GUILayout.Height(24f)))
            {
                OpenArenaBattlePresetBrowser();
            }

            if (GUILayout.Button(Ui("Current", "当前"), GUILayout.Width(88f), GUILayout.Height(24f)))
            {
                TryUseCurrentArenaBattleConfig();
            }

            if (GUILayout.Button(Ui("Reset", "重置"), GUILayout.Width(72f), GUILayout.Height(24f)))
            {
                _arenaBattleConfigId = DefaultArenaBattleConfigId;
                _arenaBattleSequenceIds.Clear();
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Arena", "场景"), GUILayout.Width(64f));
            GUILayout.Label(TrimPanelText(string.IsNullOrWhiteSpace(_arenaCombatArenaId)
                ? Ui("[preset/default]", "[使用预设/默认]")
                : _arenaCombatArenaId, 60), GUILayout.MinWidth(180f), GUILayout.ExpandWidth(true));
            if (GUILayout.Button(Ui("Use Preset", "使用预设"), GUILayout.Width(96f), GUILayout.Height(24f)))
            {
                _arenaCombatArenaId = string.Empty;
            }

            if (GUILayout.Button(Ui("Fallback", "备用场景"), GUILayout.Width(96f), GUILayout.Height(24f)))
            {
                _arenaCombatArenaId = DefaultArenaCombatArenaId;
            }

            GUILayout.EndHorizontal();

            DrawArenaBattleAdvantagePanel();
            DrawArenaTorchPanel();

            DrawArenaBattleConfigSummary(_arenaBattleConfigId);
            DrawArenaBattleSequenceSummaryLight();
            EnsureArenaHeroDraftInitialized();
            DrawArenaHeroDraftSummaryLight();
            DrawArenaLaunchReadiness();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Ui("Hero Setup", "英雄设置"), GUILayout.Height(28f)))
            {
                _arenaHeroSetupVisible = true;
            }

            if (GUILayout.Button(Ui("Reload Icons", "重载图标"), GUILayout.Height(28f)))
            {
                ForceReloadArenaVisualResources("arena panel");
            }

            if (GUILayout.Button(Ui("Validate Draft", "验证草案"), GUILayout.Height(28f)))
            {
                SaveArenaHeroDraftFromPanel();
            }

            if (GUILayout.Button(_arenaPendingLaunch ? Ui("Launch Pending", "启动等待中") : Ui("Launch Draft", "启动草案"), GUILayout.Height(28f)))
            {
                BeginArenaLaunch();
            }

            GUILayout.EndHorizontal();
        }

        private void DrawArenaBattleAdvantagePanel()
        {
            bool heroVsHero = HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots);
            GUILayout.Space(6f);
            GUILayout.Label(Ui("Monster Battle Advantage", "怪物战斗优势"));
            DrawWrappedLabel(Ui(
                "Arena launch ignores the current run's altar and Flame battle-modifier chance. Pick a deterministic setting here instead.",
                "竞技场启动会忽略当前存档的祭坛和烛光战斗调整概率，改由这里的设置决定。"));

            if (heroVsHero)
            {
                DrawWrappedLabel(Ui(
                    "Disabled because enemy heroes are configured. Hero-vs-hero always launches without monster battle advantage.",
                    "已禁用：当前配置了敌方英雄。英雄对战固定不使用怪物战斗优势。"));
            }

            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && !heroVsHero;
            GUILayout.BeginHorizontal();
            DrawArenaBattleAdvantageModeButton(ArenaBattleAdvantageMode.None, Ui("None", "无优势"));
            DrawArenaBattleAdvantageModeButton(ArenaBattleAdvantageMode.Random, Ui("Random", "随机"));
            DrawArenaBattleAdvantageModeButton(ArenaBattleAdvantageMode.Specific, Ui("Specific", "指定"));
            GUILayout.EndHorizontal();
            GUI.enabled = oldEnabled;

            DrawWrappedLabel(Ui("Current: ", "当前：") + GetArenaBattleAdvantageDisplaySummary());
            if (heroVsHero || _arenaBattleAdvantageMode != ArenaBattleAdvantageMode.Specific)
            {
                return;
            }

            EnsureArenaBattleModifierCatalog();
            DrawArenaBattleModifierSearchRow();
            RefreshArenaBattleModifierMatchesIfNeeded(false);
            if (!_arenaBattleModifierCatalogBuilt)
            {
                DrawWrappedLabel(Ui("Battle modifier library is not loaded yet.", "BattleModifier 库尚未加载。"));
                return;
            }

            if (_arenaBattleModifierMatches.Count == 0)
            {
                DrawWrappedLabel(Ui("No matching battle advantage.", "没有匹配的战斗优势。"));
                return;
            }

            foreach (ArenaBattleModifierCatalogEntry entry in _arenaBattleModifierMatches)
            {
                DrawArenaBattleModifierCatalogRow(entry);
            }
        }

        private void DrawArenaBattleAdvantageModeButton(ArenaBattleAdvantageMode mode, string label)
        {
            bool selected = _arenaBattleAdvantageMode == mode;
            string text = selected ? "* " + label : label;
            if (GUILayout.Button(text, GUILayout.Height(24f)))
            {
                _arenaBattleAdvantageMode = mode;
                _arenaHeroDraftInitialized = true;
            }
        }

        private void DrawArenaBattleModifierSearchRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Search", "搜索"), GUILayout.Width(54f));
            string next = GUILayout.TextField(_arenaBattleModifierSearch ?? string.Empty);
            if (!string.Equals(next, _arenaBattleModifierSearch ?? string.Empty, StringComparison.Ordinal))
            {
                _arenaBattleModifierSearch = next;
                RefreshArenaBattleModifierMatchesIfNeeded(true);
            }

            if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(58f), GUILayout.Height(24f)))
            {
                _arenaBattleModifierSearch = string.Empty;
                RefreshArenaBattleModifierMatchesIfNeeded(true);
            }

            GUILayout.EndHorizontal();
        }

        private void DrawArenaBattleModifierCatalogRow(ArenaBattleModifierCatalogEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            bool selected = string.Equals(_arenaBattleModifierId, entry.ModifierId, StringComparison.Ordinal);
            Rect row = GUILayoutUtility.GetRect(0f, 88f, GUILayout.ExpandWidth(true), GUILayout.Height(88f));
            DrawSolidRect(row, selected ? HudCurrentCardColor : HudCardColor);
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                _arenaBattleModifierId = entry.ModifierId;
                _arenaBattleAdvantageMode = ArenaBattleAdvantageMode.Specific;
                _arenaHeroDraftInitialized = true;
            }

            Rect icon = new Rect(row.x + 8f, row.y + 12f, 52f, 52f);
            DrawSolidRect(icon, new Color(0.05f, 0.055f, 0.06f, 1f));
            DrawSprite(icon, GetBattleModifierSprite(entry.ModifierId));

            GUIStyle title = CreateHudLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUI.Label(new Rect(row.x + 70f, row.y + 8f, row.width - 80f, 22f),
                (selected ? "* " : string.Empty) + (entry.DisplayName ?? entry.ModifierId),
                title);
            DrawInlineDescriptionPreview(new Rect(row.x + 70f, row.y + 32f, row.width - 80f, 50f), entry.Description, 11);
        }

        private void DrawArenaTorchPanel()
        {
            bool heroVsHero = HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots);
            GUILayout.Space(6f);
            GUILayout.Label(Ui("Arena Flame / Torch", "竞技场火炬 / 烛光"));
            DrawWrappedLabel(Ui(
                "Only custom monster battles use this override. It isolates the demo from the current run's equipped Flame and torch value. Low torch still will not roll enemy advantage; use Monster Battle Advantage above for that.",
                "只影响自定义打怪战斗。这里会隔离当前存档已装备的火炬和烛光数值；低烛光仍不会自动触发敌方优势，敌方优势请用上面的设置手动控制。"));

            if (heroVsHero)
            {
                DrawWrappedLabel(Ui(
                    "Disabled because enemy heroes are configured. Hero-vs-hero always launches without Flame/Torch override.",
                    "已禁用：当前配置了敌方英雄。英雄对战固定不使用火炬/烛光覆盖。"));
            }

            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && !heroVsHero;

            EnsureArenaTorchCatalog();
            DrawWrappedLabel(Ui("Current: ", "当前：") + GetArenaTorchDisplaySummary());
            if (!_arenaTorchCatalogBuilt)
            {
                DrawWrappedLabel(Ui("Torch libraries are not loaded yet.", "火炬库尚未加载。"));
                GUI.enabled = oldEnabled;
                return;
            }

            GUILayout.Space(4f);
            GUILayout.Label(Ui("Confession Context", "忏悔环境"));
            DrawWrappedLabel(Ui(
                "Pick the confession first. Torch brightness belongs to the selected confession context and is disabled until one is selected.",
                "先选择忏悔环境。烛光数值绑定在所选忏悔上；未选择忏悔时不能调整烛光。"));
            DrawArenaTorchCatalogRow(GetArenaTorchNoneEntry(), ArenaTorchSelectionSection.Confession);
            foreach (ArenaTorchCatalogEntry entry in _arenaTorchCatalog)
            {
                if (entry != null && entry.Kind == ArenaTorchCatalogKind.Confession)
                {
                    DrawArenaTorchCatalogRow(entry, ArenaTorchSelectionSection.Confession);
                }
            }

            bool hasConfession = HasArenaTorchConfessionSelected();
            GUILayout.Space(4f);
            bool oldEnabledForTorchValue = GUI.enabled;
            GUI.enabled = oldEnabledForTorchValue && hasConfession;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Brightness", "烛光"), GUILayout.Width(72f));
            float nextTorch = GUILayout.HorizontalSlider(GetEffectiveArenaTorchValue(), 0f, 100f, GUILayout.MinWidth(160f), GUILayout.ExpandWidth(true));
            nextTorch = Mathf.Round(nextTorch);
            if (!Mathf.Approximately(nextTorch, _arenaTorchValue))
            {
                _arenaTorchValue = Mathf.Clamp(nextTorch, 0f, 100f);
            }

            GUILayout.Label(GetEffectiveArenaTorchValue().ToString("0", CultureInfo.InvariantCulture), GUILayout.Width(36f));
            DrawArenaTorchValueButton(0f);
            DrawArenaTorchValueButton(25f);
            DrawArenaTorchValueButton(50f);
            DrawArenaTorchValueButton(75f);
            DrawArenaTorchValueButton(100f);
            GUILayout.EndHorizontal();
            GUI.enabled = oldEnabledForTorchValue;

            GUILayout.Space(4f);
            GUILayout.Label(Ui("Flame Type", "火炬类型"));
            if (!hasConfession)
            {
                DrawWrappedLabel(Ui(
                    "Select a confession before choosing Radiant/Infernal Flames.",
                    "需要先选择忏悔，之后才能选择 Radiant / Infernal 火炬。"));
                GUI.enabled = oldEnabled;
                return;
            }

            DrawArenaTorchCatalogRow(GetArenaTorchNoneEntry(), ArenaTorchSelectionSection.Flame);
            DrawArenaTorchSearchRow();
            RefreshArenaTorchMatchesIfNeeded(false);

            if (_arenaTorchMatches.Count == 0)
            {
                DrawWrappedLabel(Ui("No matching Flame item.", "没有匹配的火炬物品。"));
                GUI.enabled = oldEnabled;
                return;
            }

            foreach (ArenaTorchCatalogEntry entry in _arenaTorchMatches)
            {
                DrawArenaTorchCatalogRow(entry, ArenaTorchSelectionSection.Flame);
            }

            GUI.enabled = oldEnabled;
        }

        private void DrawArenaTorchValueButton(float value)
        {
            if (GUILayout.Button(value.ToString("0", CultureInfo.InvariantCulture), GUILayout.Width(34f), GUILayout.Height(22f)))
            {
                _arenaTorchValue = Mathf.Clamp(value, 0f, 100f);
            }
        }

        private void DrawArenaTorchSearchRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Search", "搜索"), GUILayout.Width(54f));
            string next = GUILayout.TextField(_arenaTorchSearch ?? string.Empty);
            if (!string.Equals(next, _arenaTorchSearch ?? string.Empty, StringComparison.Ordinal))
            {
                _arenaTorchSearch = next;
                RefreshArenaTorchMatchesIfNeeded(true);
            }

            if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(58f), GUILayout.Height(24f)))
            {
                _arenaTorchSearch = string.Empty;
                RefreshArenaTorchMatchesIfNeeded(true);
            }

            GUILayout.EndHorizontal();
        }

        private void DrawArenaTorchCatalogRow(ArenaTorchCatalogEntry entry, ArenaTorchSelectionSection section)
        {
            if (entry == null)
            {
                return;
            }

            bool selected = IsArenaTorchEntrySelected(entry, section);
            string displayName = GetArenaTorchRowDisplayName(entry, section);
            string description = BuildArenaTorchEntryDescription(entry, section);
            float descriptionWidth = Mathf.Max(240f, _panelRect.width - 160f);
            float descriptionHeight = CalculateInlineDescriptionPreviewHeight(description, 11, descriptionWidth);
            float rowHeight = Mathf.Max(94f, 42f + descriptionHeight);
            Rect row = GUILayoutUtility.GetRect(0f, rowHeight, GUILayout.ExpandWidth(true), GUILayout.Height(rowHeight));
            DrawSolidRect(row, selected ? HudCurrentCardColor : HudCardColor);
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                SelectArenaTorchEntry(entry, section);
                _arenaHeroDraftInitialized = true;
            }

            Rect icon = new Rect(row.x + 8f, row.y + 14f, 54f, 54f);
            DrawSolidRect(icon, new Color(0.05f, 0.055f, 0.06f, 1f));
            Sprite sprite = GetArenaTorchSprite(entry);
            if (sprite != null)
            {
                DrawSprite(icon, sprite);
            }
            else
            {
                string abbrev = entry.Kind == ArenaTorchCatalogKind.None
                    ? "-"
                    : (entry.Kind == ArenaTorchCatalogKind.Confession ? "C" : "F");
                GUI.Label(icon, abbrev, CreateHudLabelStyle(18, FontStyle.Bold, PanelMutedTextColor, TextAnchor.MiddleCenter));
            }

            GUIStyle title = CreateHudLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUI.Label(new Rect(row.x + 72f, row.y + 8f, row.width - 82f, 22f),
                (selected ? "* " : string.Empty) + displayName,
                title);
            DrawInlineDescriptionPreview(new Rect(row.x + 72f, row.y + 32f, row.width - 82f, row.height - 38f), description, 11);
            RegisterTooltip(row, CleanInline(displayName), description);
        }

        private void DrawArenaBattleConfigSummary(string battleConfigId)
        {
            BattleConfigurationDefinition config;
            string error;
            if (!TryGetArenaBattleConfiguration(battleConfigId, out config, out error))
            {
                DrawArenaValidationLine(Ui("Battle config", "战斗配置"), false, error);
                return;
            }

            DrawArenaValidationLine(Ui("Battle config", "战斗配置"), true, config.m_Id);
            ArenaBattlePresetEntry entry = GetArenaBattlePresetEntryFromCache(config.m_Id);
            if (entry == null)
            {
                EnsureArenaBattlePresetCache();
                entry = GetArenaBattlePresetEntryFromCache(config.m_Id);
            }

            DrawWrappedLabel(Ui("Enemies: ", "敌人：") + BuildArenaBattleConfigSummaryText(config));
            if (entry != null)
            {
                DrawWrappedLabel(Ui("Sequence: ", "连战：") + (string.IsNullOrWhiteSpace(entry.ChainSummary) ? Ui("[single]", "[单场]") : entry.ChainSummary));
                DrawArenaValidationLine(
                    Ui("Preset risk", "预设风险"),
                    entry.IsLaunchRecommended,
                    string.IsNullOrWhiteSpace(entry.RiskSummary) ? Ui("recommended", "推荐") : entry.RiskSummary);
            }

            DrawWrappedLabel(Ui(
                "Detailed enemy portraits and localized preset search are in Browse. F7 intentionally keeps this preview lightweight.",
                "敌人头像和本地化搜索在“浏览”窗口中查看。F7 主面板只保留轻量预览。"));

            if (config.m_PlayerActors != null && config.m_PlayerActors.Count > 0)
            {
                DrawArenaValidationLine(
                    Ui("Preset player actors", "预设玩家角色"),
                    false,
                    JoinArenaIds(config.m_PlayerActors));
            }

            string background = entry == null || string.IsNullOrWhiteSpace(entry.BackgroundScene)
                ? GetArenaResolvedBackgroundScene(config)
                : entry.BackgroundScene;
            DrawWrappedLabel(Ui("Background: ", "背景：") + background);

            List<string> flags = new List<string>();
            if (config.HasNextBattle)
            {
                flags.Add("nextBattle");
            }

            if (config.m_IsNextBattleOptional)
            {
                flags.Add("optionalNext");
            }

            if (config.m_IsKeepCombatContainers)
            {
                flags.Add("keepContainers");
            }

            if (config.BattleModifierOverride != null || !string.IsNullOrWhiteSpace(config.m_BattleModifierOverrideId))
            {
                flags.Add("battleModifier");
            }

            if (config.HeroEffects != null && config.HeroEffects.Count > 0)
            {
                flags.Add("heroEffects=" + config.HeroEffects.Count);
            }

            if (config.EnemyEffects != null && config.EnemyEffects.Count > 0)
            {
                flags.Add("enemyEffects=" + config.EnemyEffects.Count);
            }

            if (config.ActorlessEffects != null && config.ActorlessEffects.Count > 0)
            {
                flags.Add("actorlessEffects=" + config.ActorlessEffects.Count);
            }

            DrawWrappedLabel(Ui("Flags: ", "标记：") + (flags.Count == 0 ? "[none]" : string.Join(", ", flags.ToArray())));
        }

        private void DrawArenaBattleSequenceSummaryLight()
        {
            if (_arenaBattleSequenceIds.Count == 0)
            {
                DrawWrappedLabel(Ui("Launch sequence: selected preset / official chain.", "启动序列：使用当前预设或其官方连战。"));
                return;
            }

            DrawArenaValidationLine(Ui("Launch sequence", "启动序列"), true, _arenaBattleSequenceIds.Count + Ui(" wave(s)", " 波"));
            for (int i = 0; i < _arenaBattleSequenceIds.Count; i++)
            {
                string id = _arenaBattleSequenceIds[i];
                ArenaBattlePresetEntry entry = GetArenaBattlePresetEntryFromCache(id);
                DrawWrappedLabel("  #" + (i + 1) + " " + id +
                    (entry == null || string.IsNullOrWhiteSpace(entry.Summary) ? string.Empty : " | " + entry.Summary));
            }
        }

        private string BuildArenaBattleConfigSummaryText(BattleConfigurationDefinition config)
        {
            if (config == null)
            {
                return "[none]";
            }

            ArenaBattlePresetEntry cached = GetArenaBattlePresetEntryFromCache(config.m_Id);
            if (cached != null && !string.IsNullOrWhiteSpace(cached.Summary))
            {
                return cached.Summary;
            }

            return JoinArenaIds(config.m_EnemyActors);
        }

        private void OpenArenaBattlePresetBrowser()
        {
            _arenaBattlePresetBrowserVisible = true;
            EnsureArenaBattlePresetCache();
            RefreshArenaBattlePresetMatchesIfNeeded(true);
        }

        private void DrawArenaBattlePresetBrowserWindow(int id)
        {
            Color oldContentColor = GUI.contentColor;
            Color oldBackgroundColor = GUI.backgroundColor;
            int oldLabelFontSize = GUI.skin.label.fontSize;
            int oldButtonFontSize = GUI.skin.button.fontSize;
            int oldTextFieldFontSize = GUI.skin.textField.fontSize;

            try
            {
                GUI.contentColor = PanelTextColor;
                GUI.backgroundColor = Color.white;
                GUI.skin.label.fontSize = 13;
                GUI.skin.button.fontSize = 13;
                GUI.skin.textField.fontSize = 13;

                GUILayout.BeginVertical(_panelBodyStyle, GUILayout.ExpandHeight(true));
                GUILayout.BeginHorizontal(_panelHeaderStyle, GUILayout.Height(38f));
                GUILayout.Label(Ui("Official Battle Presets", "官方战斗预设"), _panelTitleStyle);
                GUILayout.FlexibleSpace();
                GUILayout.Label(_arenaBattlePresetCacheBuilt
                    ? (_arenaBattlePresetMatches.Count + "/" + _arenaBattlePresetTotalCount +
                       (_arenaBattlePresetMergedChildCount > 0 ? " merged=" + _arenaBattlePresetMergedChildCount : string.Empty))
                    : Ui("not loaded", "未加载"),
                    _panelStatusStyle);
                if (GUILayout.Button(Ui("Refresh", "刷新"), GUILayout.Width(82f), GUILayout.Height(26f)))
                {
                    RebuildArenaBattlePresetCache();
                }

                if (GUILayout.Button(Ui("Reload Icons", "重载图标"), GUILayout.Width(104f), GUILayout.Height(26f)))
                {
                    ForceReloadArenaVisualResources("battle preset browser");
                }

                if (GUILayout.Button("X", GUILayout.Width(34f), GUILayout.Height(26f)))
                {
                    _arenaBattlePresetBrowserVisible = false;
                }

                GUILayout.EndHorizontal();

                if (!EnsureArenaBattlePresetCache())
                {
                    DrawWrappedLabel(Ui(
                        "Battle preset library is not loaded yet. Enter a run or a combat scene first, then reopen this window.",
                        "战斗预设库尚未加载。先进入对局或战斗场景，再重新打开此窗口。"));
                    GUILayout.EndVertical();
                    DrawWindowResizeHandle(
                        ref _arenaBattlePresetBrowserRect,
                        ref _arenaBattlePresetBrowserResizing,
                        ref _arenaBattlePresetBrowserResizeChangedThisFrame,
                        880f,
                        560f);
                    GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, _arenaBattlePresetBrowserRect.width - PanelResizeHandleSize), 38f));
                    return;
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label(Ui("Search", "搜索"), GUILayout.Width(64f));
                string nextSearch = GUILayout.TextField(_arenaBattleConfigSearch ?? string.Empty);
                if (!string.Equals(nextSearch, _arenaBattleConfigSearch, StringComparison.Ordinal))
                {
                    _arenaBattleConfigSearch = nextSearch;
                    RefreshArenaBattlePresetMatchesIfNeeded(true);
                }

                if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(68f), GUILayout.Height(24f)))
                {
                    _arenaBattleConfigSearch = string.Empty;
                    RefreshArenaBattlePresetMatchesIfNeeded(true);
                }

                GUILayout.EndHorizontal();
                DrawWrappedLabel(Ui(
                    "Search supports battle id, tag, enemy internal id, and localized enemy names. Rows use cached text; portraits load only in the selected preset preview.",
                    "搜索支持战斗 id、标签、敌人内部 id 和本地化敌人名称。列表只用缓存文本，头像只在选中预览时加载。"));

                RefreshArenaBattlePresetMatchesIfNeeded(false);

                GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
                DrawArenaBattlePresetListColumn();
                GUILayout.Space(12f);
                DrawArenaBattlePresetDetailColumn();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();

                DrawWindowResizeHandle(
                    ref _arenaBattlePresetBrowserRect,
                    ref _arenaBattlePresetBrowserResizing,
                    ref _arenaBattlePresetBrowserResizeChangedThisFrame,
                    880f,
                    560f);
                GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, _arenaBattlePresetBrowserRect.width - PanelResizeHandleSize), 38f));
            }
            finally
            {
                GUI.contentColor = oldContentColor;
                GUI.backgroundColor = oldBackgroundColor;
                GUI.skin.label.fontSize = oldLabelFontSize;
                GUI.skin.button.fontSize = oldButtonFontSize;
                GUI.skin.textField.fontSize = oldTextFieldFontSize;
            }
        }

        private void DrawArenaHeroSetupWindow(int id)
        {
            Color oldContentColor = GUI.contentColor;
            Color oldBackgroundColor = GUI.backgroundColor;
            int oldLabelFontSize = GUI.skin.label.fontSize;
            int oldButtonFontSize = GUI.skin.button.fontSize;
            int oldTextFieldFontSize = GUI.skin.textField.fontSize;

            try
            {
                GUI.contentColor = PanelTextColor;
                GUI.backgroundColor = Color.white;
                GUI.skin.label.fontSize = 13;
                GUI.skin.button.fontSize = 12;
                GUI.skin.textField.fontSize = 12;

                EnsureArenaHeroDraftInitialized();

                GUILayout.BeginVertical(_panelBodyStyle, GUILayout.ExpandHeight(true));
                GUILayout.BeginHorizontal(_panelHeaderStyle, GUILayout.Height(38f));
                GUILayout.Label(Ui("Arena Hero Setup", "竞技场英雄设置"), _panelTitleStyle);
                GUILayout.Label(GetArenaDraftTeamLabel(_arenaHeroSetupTeamIndex), _panelStatusStyle, GUILayout.Width(128f));
                GUILayout.FlexibleSpace();
                GUILayout.Label(GetArenaHeroDraftValidationSummary(), _panelStatusStyle);
                if (GUILayout.Button(Ui("Import Current", "导入当前"), GUILayout.Width(118f), GUILayout.Height(26f)))
                {
                    ImportArenaHeroDraftFromCurrentParty(true);
                }

                if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(64f), GUILayout.Height(26f)))
                {
                    ClearArenaHeroDraft();
                }

                if (GUILayout.Button(Ui("Reload Icons", "重载图标"), GUILayout.Width(104f), GUILayout.Height(26f)))
                {
                    ForceReloadArenaVisualResources("hero setup");
                }

                if (GUILayout.Button(Ui("Close", "关闭"), GUILayout.Width(76f), GUILayout.Height(26f)))
                {
                    _arenaHeroSetupVisible = false;
                }

                GUILayout.EndHorizontal();

                DrawWrappedLabel(Ui(
                    "Arena keeps only battle/team ids in native launch prefs. Paths, skills, combat items, trinkets, and quirks are applied from the in-memory draft by the module. Use Reload Icons after returning from main menu if portraits/icons were cached before resources loaded.",
                    "竞技场只把战斗和队伍 id 放进原生内存启动 prefs。道途、技能、战斗道具、饰品和怪癖都由模块从内存草案直接应用。回主菜单后重新进图若头像/图标缺失，可点“重载图标”。"));

                DrawArenaHeroSetupTeamSelector();

                GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
                DrawArenaHeroDraftSlotColumn();
                GUILayout.Space(10f);
                DrawArenaHeroCatalogColumn();
                GUILayout.Space(10f);
                DrawArenaHeroDraftDetailColumn();
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
                DrawWindowResizeHandle(
                    ref _arenaHeroSetupRect,
                    ref _arenaHeroSetupResizing,
                    ref _arenaHeroSetupResizeChangedThisFrame,
                    860f,
                    560f);
                GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, _arenaHeroSetupRect.width - PanelResizeHandleSize), 38f));
            }
            finally
            {
                GUI.contentColor = oldContentColor;
                GUI.backgroundColor = oldBackgroundColor;
                GUI.skin.label.fontSize = oldLabelFontSize;
                GUI.skin.button.fontSize = oldButtonFontSize;
                GUI.skin.textField.fontSize = oldTextFieldFontSize;
            }
        }

        private void ClampArenaHeroSetupRectToScreen()
        {
            float maxWidth = Mathf.Max(480f, Screen.width - 20f);
            float maxHeight = Mathf.Max(360f, Screen.height - 20f);
            float minWidth = Mathf.Min(860f, maxWidth);
            float minHeight = Mathf.Min(560f, maxHeight);
            _arenaHeroSetupRect.width = Mathf.Clamp(_arenaHeroSetupRect.width, minWidth, maxWidth);
            _arenaHeroSetupRect.height = Mathf.Clamp(_arenaHeroSetupRect.height, minHeight, maxHeight);
            _arenaHeroSetupRect.x = Mathf.Clamp(_arenaHeroSetupRect.x, 0f, Mathf.Max(0f, Screen.width - _arenaHeroSetupRect.width));
            _arenaHeroSetupRect.y = Mathf.Clamp(_arenaHeroSetupRect.y, 0f, Mathf.Max(0f, Screen.height - _arenaHeroSetupRect.height));
        }

        private void DrawArenaHeroSetupTeamSelector()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Edit side", "编辑队伍"), GUILayout.Width(76f));
            DrawArenaHeroSetupTeamButton(0, GetArenaDraftTeamLabel(0));
            DrawArenaHeroSetupTeamButton(1, GetArenaDraftTeamLabel(1));
            GUILayout.FlexibleSpace();
            if (_arenaHeroSetupTeamIndex == 1 &&
                GUILayout.Button(Ui("Mirror Party", "镜像我方"), GUILayout.Width(108f), GUILayout.Height(26f)))
            {
                CopyArenaHeroDraftSlots(_arenaHeroDraftSlots, _arenaEnemyHeroDraftSlots);
                _arenaHeroDraftSelectedSlot = 0;
                _arenaHeroDetailScroll = Vector2.zero;
                _arenaHeroDraftInitialized = true;
                HostLog.Write("[arena] Copied party hero draft to enemy hero draft for mirror testing.");
            }

            GUILayout.EndHorizontal();
        }

        private void DrawArenaHeroSetupTeamButton(int teamIndex, string label)
        {
            bool selected = _arenaHeroSetupTeamIndex == teamIndex;
            Color oldBackground = GUI.backgroundColor;
            GUI.backgroundColor = selected ? new Color(0.42f, 0.54f, 0.60f, 1f) : Color.white;
            if (GUILayout.Button(selected ? ("* " + label) : label, GUILayout.Width(148f), GUILayout.Height(26f)))
            {
                if (_arenaHeroSetupTeamIndex != teamIndex)
                {
                    _arenaHeroSetupTeamIndex = teamIndex;
                    _arenaHeroDetailScroll = Vector2.zero;
                    if (_arenaHeroDraftSelectedSlot < 0 || _arenaHeroDraftSelectedSlot >= GetActiveArenaHeroDraftSlots().Length)
                    {
                        _arenaHeroDraftSelectedSlot = 0;
                    }
                }
            }

            GUI.backgroundColor = oldBackground;
        }

        private void DrawArenaHeroDraftSlotColumn()
        {
            ArenaHeroDraftSlot[] slots = GetActiveArenaHeroDraftSlots();
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(200f), GUILayout.ExpandHeight(true));
            GUILayout.Label(GetArenaDraftTeamLabel(_arenaHeroSetupTeamIndex));
            DrawWrappedLabel(Ui(
                "Launch uses these slots for the selected side. Import Current copies the active run party into this side.",
                "启动时会把这些槽位用于当前选中的队伍。“导入当前”会把当前对局队伍复制到这一侧。"));

            for (int i = 0; i < slots.Length; i++)
            {
                DrawArenaHeroDraftSlotRow(i);
                GUILayout.Space(6f);
            }

            GUILayout.FlexibleSpace();
            bool ready = TryGetArenaDraftForLaunch(slots, GetArenaDraftTeamLabel(_arenaHeroSetupTeamIndex), out _, out _, out _, out string error);
            DrawArenaValidationLine(Ui("Draft", "草案"), ready, string.IsNullOrWhiteSpace(error) ? Ui("ready", "就绪") : error);
            GUILayout.EndVertical();
        }

        private void DrawArenaHeroDraftSlotRow(int index)
        {
            ArenaHeroDraftSlot[] slots = GetActiveArenaHeroDraftSlots();
            if (index < 0 || index >= slots.Length)
            {
                return;
            }

            ArenaHeroDraftSlot slot = slots[index];
            bool selected = index == _arenaHeroDraftSelectedSlot;
            Rect row = GUILayoutUtility.GetRect(0f, 86f, GUILayout.ExpandWidth(true), GUILayout.Height(86f));
            DrawSolidRect(row, selected ? HudCurrentCardColor : HudCardColor);

            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                _arenaHeroDraftSelectedSlot = index;
            }

            string actorName = string.IsNullOrWhiteSpace(slot.ActorId)
                ? "[empty]"
                : GetArenaActorClassDisplayName(slot.ActorId);
            string pathName = string.IsNullOrWhiteSpace(slot.PathId)
                ? "[path]"
                : GetArenaDraftPathDisplayName(slot.PathId, slot.ActorId);
            GUIStyle title = CreateHudLabelStyle(12, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUIStyle meta = CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperLeft);
            GUI.Label(new Rect(row.x + 12f, row.y + 8f, row.width - 20f, 20f),
                "S" + (index + 1) + " " + actorName,
                title);
            GUI.Label(new Rect(row.x + 12f, row.y + 29f, row.width - 20f, 18f),
                "Path: " + pathName,
                meta);
            GUI.Label(new Rect(row.x + 12f, row.y + 49f, row.width - 20f, 18f),
                "Skills: " + slot.SkillIds.Count + "/5",
                meta);
            string itemSummary = "Combat: " + (string.IsNullOrWhiteSpace(slot.CombatItemId) ? "[none]" : GetLocalizedItemDisplayName(slot.CombatItemId, slot.CombatItemId)) +
                " | Trinkets: " + slot.TrinketIds.Count + "/2";
            GUI.Label(new Rect(row.x + 12f, row.y + 66f, row.width - 20f, 16f),
                TrimPanelText(itemSummary, 44),
                meta);
        }

        private void DrawArenaHeroCatalogColumn()
        {
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(220f), GUILayout.ExpandHeight(true));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Heroes", "英雄"), GUILayout.Width(70f));
            GUILayout.FlexibleSpace();
            GUILayout.Label(_arenaHeroCatalogBuilt
                ? (_arenaHeroCatalogMatches.Count + "/" + _arenaHeroCatalogTotalCount)
                : Ui("not loaded", "未加载"));
            if (GUILayout.Button(Ui("Refresh", "刷新"), GUILayout.Width(76f), GUILayout.Height(24f)))
            {
                RebuildArenaHeroCatalog();
            }

            GUILayout.EndHorizontal();

            if (!EnsureArenaHeroCatalog())
            {
                DrawWrappedLabel(Ui(
                    "Hero data library is not loaded yet. Enter a run or reopen this window after data init.",
                    "英雄数据尚未加载。先进入对局，或等数据初始化后重新打开窗口。"));
                GUILayout.EndVertical();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Search", "搜索"), GUILayout.Width(54f));
            string nextSearch = GUILayout.TextField(_arenaHeroDraftSearch ?? string.Empty);
            if (!string.Equals(nextSearch, _arenaHeroDraftSearch, StringComparison.Ordinal))
            {
                _arenaHeroDraftSearch = nextSearch;
                RefreshArenaHeroCatalogMatchesIfNeeded(true);
            }

            if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(58f), GUILayout.Height(24f)))
            {
                _arenaHeroDraftSearch = string.Empty;
                RefreshArenaHeroCatalogMatchesIfNeeded(true);
            }

            GUILayout.EndHorizontal();

            RefreshArenaHeroCatalogMatchesIfNeeded(false);
            _arenaHeroCatalogScroll = GUILayout.BeginScrollView(_arenaHeroCatalogScroll, GUILayout.ExpandHeight(true));
            try
            {
                if (_arenaHeroCatalogMatches.Count == 0)
                {
                    DrawWrappedLabel(Ui("No matching hero.", "没有匹配的英雄。"));
                }

                foreach (ArenaHeroCatalogEntry entry in _arenaHeroCatalogMatches)
                {
                    DrawArenaHeroCatalogRow(entry);
                }
            }
            finally
            {
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
            }
        }

        private void DrawArenaHeroCatalogRow(ArenaHeroCatalogEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            Rect row = GUILayoutUtility.GetRect(0f, 52f, GUILayout.ExpandWidth(true), GUILayout.Height(52f));
            bool selectedActor = string.Equals(GetSelectedArenaHeroDraftSlot().ActorId, entry.ActorId, StringComparison.Ordinal);
            DrawSolidRect(row, selectedActor ? HudCurrentCardColor : HudCardColor);

            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                SetArenaHeroDraftActor(_arenaHeroDraftSelectedSlot, entry.ActorId);
            }

            GUIStyle title = CreateHudLabelStyle(12, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUIStyle meta = CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperLeft);
            GUI.Label(new Rect(row.x + 10f, row.y + 7f, row.width - 20f, 20f),
                (selectedActor ? "* " : string.Empty) + (entry.DisplayName ?? entry.ActorId),
                title);
            GUI.Label(new Rect(row.x + 10f, row.y + 29f, row.width - 20f, 18f), TrimPanelText(entry.ActorId ?? string.Empty, 42), meta);
        }

        private void DrawArenaHeroDraftDetailColumn()
        {
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandHeight(true));
            ArenaHeroDraftSlot slot = GetSelectedArenaHeroDraftSlot();
            GUILayout.Label(GetArenaDraftTeamLabel(_arenaHeroSetupTeamIndex) + " | " + Ui("Slot ", "槽位 ") + (_arenaHeroDraftSelectedSlot + 1));

            DrawArenaHeroDraftActorHeader(slot);
            GUILayout.Space(8f);
            DrawArenaHeroDraftPathPicker(slot);
            GUILayout.Space(8f);
            DrawArenaHeroDetailTabBar();
            GUILayout.Space(6f);
            _arenaHeroDetailScroll = GUILayout.BeginScrollView(
                _arenaHeroDetailScroll,
                false,
                true,
                GUI.skin.horizontalScrollbar,
                GUI.skin.verticalScrollbar,
                GUILayout.ExpandHeight(true));
            try
            {
                DrawArenaHeroDetailTabContent(slot);
            }
            finally
            {
                GUILayout.EndScrollView();
            }

            GUILayout.EndVertical();
        }

        private void DrawArenaHeroDetailTabBar()
        {
            GUILayout.BeginHorizontal();
            DrawArenaHeroDetailTabButton(ArenaHeroDetailTab.Skills, Ui("Skills", "技能"));
            DrawArenaHeroDetailTabButton(ArenaHeroDetailTab.CombatItem, Ui("Combat Item", "战斗道具"));
            DrawArenaHeroDetailTabButton(ArenaHeroDetailTab.Trinkets, Ui("Trinkets", "饰品"));
            DrawArenaHeroDetailTabButton(ArenaHeroDetailTab.Quirks, Ui("Quirks", "怪癖"));
            DrawArenaHeroDetailTabButton(ArenaHeroDetailTab.StartBuffs, Ui("Start Buffs", "友方 Buff"));
            DrawArenaHeroDetailTabButton(ArenaHeroDetailTab.Ordainment, Ui("Ordainment", "敌方赐福"));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawArenaHeroDetailTabButton(ArenaHeroDetailTab tab, string label)
        {
            bool selected = _arenaHeroDetailTab == tab;
            Color oldBackground = GUI.backgroundColor;
            GUI.backgroundColor = selected ? new Color(0.42f, 0.54f, 0.60f, 1f) : Color.white;
            if (GUILayout.Button(selected ? ("* " + label) : label, GUILayout.Height(26f), GUILayout.MinWidth(104f)))
            {
                if (_arenaHeroDetailTab != tab)
                {
                    _arenaHeroDetailTab = tab;
                    _arenaHeroDetailScroll = Vector2.zero;
                }
            }

            GUI.backgroundColor = oldBackground;
        }

        private void DrawArenaHeroDetailTabContent(ArenaHeroDraftSlot slot)
        {
            switch (_arenaHeroDetailTab)
            {
                case ArenaHeroDetailTab.CombatItem:
                    DrawArenaHeroDraftCombatItemPicker(slot);
                    break;
                case ArenaHeroDetailTab.Trinkets:
                    DrawArenaHeroDraftTrinketPicker(slot);
                    break;
                case ArenaHeroDetailTab.Quirks:
                    DrawArenaHeroDraftQuirkPicker(slot);
                    break;
                case ArenaHeroDetailTab.StartBuffs:
                    DrawArenaHeroStartBuffPicker();
                    break;
                case ArenaHeroDetailTab.Ordainment:
                    DrawArenaEnemyOrdainmentPicker();
                    break;
                default:
                    DrawArenaHeroDraftSkillPicker(slot);
                    break;
            }
        }

        private void DrawArenaHeroDraftActorHeader(ArenaHeroDraftSlot slot)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Actor id", "角色 id"), GUILayout.Width(62f));
            string nextActorId = GUILayout.TextField(slot.ActorId ?? string.Empty);
            if (!string.Equals(nextActorId, slot.ActorId ?? string.Empty, StringComparison.Ordinal))
            {
                SetArenaHeroDraftActor(_arenaHeroDraftSelectedSlot, nextActorId);
                slot = GetSelectedArenaHeroDraftSlot();
            }

            if (GUILayout.Button(Ui("Default Skills", "默认技能"), GUILayout.Width(106f), GUILayout.Height(24f)))
            {
                ResetArenaHeroDraftSkills(_arenaHeroDraftSelectedSlot);
            }

            GUILayout.EndHorizontal();

            if (string.IsNullOrWhiteSpace(slot.ActorId))
            {
                DrawWrappedLabel(Ui("Pick a hero from the list, or type an actor id.", "从左侧列表选择英雄，或输入角色 id。"));
                return;
            }

            Rect tile = GUILayoutUtility.GetRect(0f, 92f, GUILayout.ExpandWidth(true), GUILayout.Height(92f));
            DrawSolidRect(tile, HudCardColor);
            Rect portrait = new Rect(tile.x + 10f, tile.y + 10f, 70f, 70f);
            DrawSolidRect(portrait, new Color(0.04f, 0.045f, 0.05f, 1f));
            DrawPortraitSprite(portrait, GetActorPortraitSprite(slot.ActorId));

            GUIStyle title = CreateHudLabelStyle(14, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUIStyle meta = CreateHudLabelStyle(11, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperLeft);
            GUI.Label(new Rect(tile.x + 90f, tile.y + 10f, tile.width - 100f, 24f),
                GetArenaActorClassDisplayName(slot.ActorId),
                title);
            GUI.Label(new Rect(tile.x + 90f, tile.y + 36f, tile.width - 100f, 20f),
                "Path: " + (string.IsNullOrWhiteSpace(slot.PathId) ? "[path]" : GetArenaDraftPathDisplayName(slot.PathId, slot.ActorId)),
                meta);
            GUI.Label(new Rect(tile.x + 90f, tile.y + 58f, tile.width - 100f, 20f),
                "Skills: " + slot.SkillIds.Count + "/5 | id=" + slot.ActorId,
                meta);
        }

        private void DrawArenaHeroDraftPathPicker(ArenaHeroDraftSlot slot)
        {
            GUILayout.Label(Ui("Path", "道途"));
            if (string.IsNullOrWhiteSpace(slot.ActorId))
            {
                DrawWrappedLabel(Ui("No actor selected.", "未选择角色。"));
                return;
            }

            List<ActorDataPath> paths = GetArenaDraftAvailablePaths(slot.ActorId);
            if (paths.Count == 0)
            {
                DrawWrappedLabel(Ui("No valid path found for ", "没有找到可用道途：") + slot.ActorId + ".");
                return;
            }

            const int columns = 2;
            for (int i = 0; i < paths.Count; i += columns)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < columns && i + column < paths.Count; column++)
                {
                    ActorDataPath path = paths[i + column];
                    bool selected = path != null && string.Equals(path.Id, slot.PathId, StringComparison.Ordinal);
                    string label = path == null ? "[path]" : GetArenaDraftPathDisplayName(path.Id, slot.ActorId);
                    if (GUILayout.Button(selected ? ("* " + label) : label, GUILayout.Height(28f)))
                    {
                        SetArenaHeroDraftPath(_arenaHeroDraftSelectedSlot, path == null ? string.Empty : path.Id);
                    }
                }

                GUILayout.EndHorizontal();
            }
        }

        private void DrawArenaHeroDraftSkillPicker(ArenaHeroDraftSlot slot)
        {
            GUILayout.Label(Ui("Skills", "技能"));
            if (string.IsNullOrWhiteSpace(slot.ActorId))
            {
                DrawWrappedLabel(Ui("No actor selected.", "未选择角色。"));
                return;
            }

            GUILayout.BeginHorizontal();
            for (int i = 0; i < 5; i++)
            {
                string skillId = i < slot.SkillIds.Count ? slot.SkillIds[i] : string.Empty;
                Rect rect = GUILayoutUtility.GetRect(76f, 78f, GUILayout.Width(76f), GUILayout.Height(78f));
                DrawSolidRect(rect, string.IsNullOrWhiteSpace(skillId) ? HudHostOnlyCardColor : HudTileColor);
                if (!string.IsNullOrWhiteSpace(skillId))
                {
                    DrawSprite(new Rect(rect.x + 14f, rect.y + 5f, 48f, 48f), GetSkillSprite(skillId));
                    if (GUI.Button(new Rect(rect.x + rect.width - 20f, rect.y + 2f, 18f, 18f), "x"))
                    {
                        slot.SkillIds.RemoveAt(i);
                        _arenaHeroDraftInitialized = true;
                    }

                    DrawArenaHeroDraftSkillUpgradeButton(slot, i, skillId, new Rect(rect.x + 5f, rect.y + 55f, rect.width - 10f, 19f));
                }
                else
                {
                    GUIStyle meta = CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.MiddleCenter);
                    GUI.Label(rect, "+", meta);
                }

                GUILayout.Space(6f);
            }

            GUILayout.EndHorizontal();

            List<string> availableSkills = GetArenaDraftAvailableSkillIds(slot.ActorId, slot.PathId);
            if (availableSkills.Count == 0)
            {
                DrawWrappedLabel(Ui("Skill resource list is not available for this actor yet.", "该角色的技能资源列表暂不可用。"));
                return;
            }

            DrawWrappedLabel(Ui("Click a skill to add/remove it. Launch requires exactly 5 skills.", "点击技能可添加/移除。启动需要正好 5 个技能。"));
            const int columns = 2;
            for (int i = 0; i < availableSkills.Count; i += columns)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < columns && i + column < availableSkills.Count; column++)
                {
                    DrawArenaHeroDraftSkillButton(slot, availableSkills[i + column]);
                }

                GUILayout.EndHorizontal();
            }
        }

        private void DrawArenaHeroDraftSkillButton(ArenaHeroDraftSlot slot, string skillId)
        {
            int selectedIndex = FindArenaHeroDraftSkillIndex(slot, skillId);
            bool selected = selectedIndex >= 0;
            string selectedSkillId = selected ? slot.SkillIds[selectedIndex] : skillId;
            Rect row = GUILayoutUtility.GetRect(0f, 92f, GUILayout.ExpandWidth(true), GUILayout.Height(92f));
            DrawSolidRect(row, selected ? HudCurrentCardColor : HudCardColor);
            string label = GetCachedArenaSkillDisplayName(selectedSkillId);
            string description = GetCachedArenaSkillDescription(selectedSkillId);
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                ToggleArenaHeroDraftSkill(slot, skillId);
            }

            Rect icon = new Rect(row.x + 8f, row.y + 10f, 52f, 52f);
            DrawSolidRect(icon, new Color(0.05f, 0.055f, 0.06f, 1f));
            DrawSprite(icon, GetSkillSprite(selectedSkillId));

            GUIStyle title = CreateHudLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            string prefix = selected ? (IsArenaSkillUpgrade(selectedSkillId) ? "* " + Ui("Mastered ", "强化 ") : "* ") : string.Empty;
            GUI.Label(new Rect(row.x + 70f, row.y + 8f, row.width - 80f, 22f), prefix + label, title);
            DrawInlineDescriptionPreview(new Rect(row.x + 70f, row.y + 32f, row.width - 80f, 54f), description, 11);
        }

        private void DrawArenaHeroDraftSkillUpgradeButton(ArenaHeroDraftSlot slot, int skillIndex, string skillId, Rect rect)
        {
            string upgradeSkillId = GetArenaSkillUpgradeId(skillId);
            GUIStyle labelStyle = CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.MiddleCenter);
            if (string.IsNullOrWhiteSpace(upgradeSkillId))
            {
                GUI.Label(rect, Ui("No Upg", "无强化"), labelStyle);
                return;
            }

            bool upgraded = IsArenaSkillUpgrade(skillId);
            Color oldBackground = GUI.backgroundColor;
            GUI.backgroundColor = upgraded
                ? new Color(0.95f, 0.74f, 0.28f, 1f)
                : new Color(0.30f, 0.35f, 0.39f, 1f);

            if (GUI.Button(rect, upgraded ? Ui("Mastered", "强化") : Ui("Normal", "普通")))
            {
                if (skillIndex >= 0 && skillIndex < slot.SkillIds.Count)
                {
                    slot.SkillIds[skillIndex] = upgraded ? StripArenaSkillUpgradeSuffix(skillId) : upgradeSkillId;
                    _arenaHeroDraftInitialized = true;
                    NormalizeArenaHeroDraftSkills(slot, false);
                }
            }

            GUI.backgroundColor = oldBackground;
        }

        private void DrawArenaHeroDraftCombatItemPicker(ArenaHeroDraftSlot slot)
        {
            if (slot == null)
            {
                return;
            }

            EnsureArenaHeroItemCatalog();

            GUILayout.Label(Ui("Combat Item", "战斗道具"));
            DrawArenaSelectedCombatItem(slot);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Id", GUILayout.Width(28f));
            string nextCombatItem = GUILayout.TextField(slot.CombatItemId ?? string.Empty);
            if (!string.Equals(nextCombatItem, slot.CombatItemId ?? string.Empty, StringComparison.Ordinal))
            {
                slot.CombatItemId = nextCombatItem.Trim();
                _arenaHeroDraftInitialized = true;
            }

            if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(58f), GUILayout.Height(24f)))
            {
                slot.CombatItemId = string.Empty;
                _arenaHeroDraftInitialized = true;
            }

            GUILayout.EndHorizontal();

            DrawArenaItemSearchRow(Ui("Search", "搜索"), ref _arenaHeroCombatItemSearch, true);
            RefreshArenaHeroItemMatchesIfNeeded(true, false);
            if (_arenaCombatItemMatches.Count == 0)
            {
                DrawWrappedLabel(Ui("No matching combat item.", "没有匹配的战斗道具。"));
            }

            foreach (ArenaItemCatalogEntry entry in _arenaCombatItemMatches)
            {
                DrawArenaItemCatalogRow(
                    entry,
                    string.Equals(slot.CombatItemId, entry.ItemId, StringComparison.Ordinal),
                    () =>
                    {
                        slot.CombatItemId = string.Equals(slot.CombatItemId, entry.ItemId, StringComparison.Ordinal)
                            ? string.Empty
                            : entry.ItemId;
                        _arenaHeroDraftInitialized = true;
                    });
            }

            GUILayout.Space(8f);
            DrawWrappedLabel(BuildArenaHeroDraftItemWriteSummary());
        }

        private void DrawArenaHeroDraftTrinketPicker(ArenaHeroDraftSlot slot)
        {
            if (slot == null)
            {
                return;
            }

            EnsureArenaHeroItemCatalog();

            GUILayout.Label(Ui("Trinkets", "饰品"));
            DrawArenaDraftSelectedItemChips(slot.TrinketIds, 2);
            DrawArenaItemSearchRow(Ui("Search", "搜索"), ref _arenaHeroTrinketSearch, false);
            RefreshArenaHeroItemMatchesIfNeeded(false, false);
            if (_arenaTrinketMatches.Count == 0)
            {
                DrawWrappedLabel(Ui("No matching trinket.", "没有匹配的饰品。"));
            }

            foreach (ArenaItemCatalogEntry entry in _arenaTrinketMatches)
            {
                DrawArenaItemCatalogRow(
                    entry,
                    slot.TrinketIds.Contains(entry.ItemId),
                    () =>
                    {
                        ToggleArenaHeroDraftTrinket(slot, entry.ItemId);
                        _arenaHeroDraftInitialized = true;
                    });
            }

            GUILayout.Space(8f);
            DrawWrappedLabel(BuildArenaHeroDraftItemWriteSummary());
        }

        private void DrawArenaHeroDraftQuirkPicker(ArenaHeroDraftSlot slot)
        {
            if (slot == null)
            {
                return;
            }

            EnsureArenaHeroQuirkCatalog();

            GUILayout.Label(Ui("Quirks", "怪癖"));
            DrawWrappedLabel(Ui(
                "Selected quirks are applied by slot after the custom Arena combat enters. Positive and negative quirks are capped at 3 each; disease/curse is capped at 1.",
                "选中的怪癖会在自定义竞技场战斗进入后按槽位补加。正面和负面怪癖各最多 3 个；疾病/诅咒最多 1 个。"));

            DrawArenaSelectedQuirks(slot);

            DrawArenaQuirkSection(
                Ui("Positive Quirks", "正面怪癖"),
                ArenaQuirkKind.Positive,
                slot.PositiveQuirkIds,
                ref _arenaPositiveQuirkSearch,
                _arenaPositiveQuirkMatches,
                3);

            DrawArenaQuirkSection(
                Ui("Negative Quirks", "负面怪癖"),
                ArenaQuirkKind.Negative,
                slot.NegativeQuirkIds,
                ref _arenaNegativeQuirkSearch,
                _arenaNegativeQuirkMatches,
                3);

            DrawArenaQuirkSection(
                Ui("Disease / Curse", "疾病 / 诅咒"),
                ArenaQuirkKind.Disease,
                null,
                ref _arenaDiseaseQuirkSearch,
                _arenaDiseaseQuirkMatches,
                1,
                slot);

            GUILayout.Space(8f);
            DrawWrappedLabel(BuildArenaHeroDraftQuirkSummary());
        }

        private void DrawArenaSelectedQuirks(ArenaHeroDraftSlot slot)
        {
            GUILayout.Label(Ui("Selected", "已选择"));
            DrawArenaSelectedQuirkGroup(Ui("Positive", "正面"), slot.PositiveQuirkIds, 3, ArenaQuirkKind.Positive);
            DrawArenaSelectedQuirkGroup(Ui("Negative", "负面"), slot.NegativeQuirkIds, 3, ArenaQuirkKind.Negative);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Disease", "疾病"), GUILayout.Width(78f));
            string diseaseId = slot.DiseaseQuirkId ?? string.Empty;
            Rect rect = GUILayoutUtility.GetRect(52f, 52f, GUILayout.Width(52f), GUILayout.Height(52f));
            DrawSolidRect(rect, string.IsNullOrWhiteSpace(diseaseId) ? HudHostOnlyCardColor : HudTileColor);
            if (!string.IsNullOrWhiteSpace(diseaseId))
            {
                ArenaQuirkCatalogEntry entry = GetArenaQuirkEntry(diseaseId);
                DrawArenaQuirkIcon(new Rect(rect.x + 6f, rect.y + 6f, 40f, 40f), entry, ArenaQuirkKind.Disease);
                if (GUI.Button(new Rect(rect.x + 32f, rect.y + 1f, 18f, 18f), "x"))
                {
                    slot.DiseaseQuirkId = string.Empty;
                    _arenaHeroDraftInitialized = true;
                }
            }
            else
            {
                GUI.Label(rect, "+", CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.MiddleCenter));
            }

            GUILayout.Space(8f);
            GUILayout.Label(string.IsNullOrWhiteSpace(diseaseId)
                ? Ui("[none]", "[无]")
                : ((GetArenaQuirkEntry(diseaseId) == null ? diseaseId : GetArenaQuirkEntry(diseaseId).DisplayName)));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);
        }

        private void DrawArenaSelectedQuirkGroup(string label, List<string> quirkIds, int maxCount, ArenaQuirkKind kind)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(78f));
            for (int i = 0; i < maxCount; i++)
            {
                string quirkId = quirkIds != null && i < quirkIds.Count ? quirkIds[i] : string.Empty;
                Rect rect = GUILayoutUtility.GetRect(52f, 52f, GUILayout.Width(52f), GUILayout.Height(52f));
                DrawSolidRect(rect, string.IsNullOrWhiteSpace(quirkId) ? HudHostOnlyCardColor : HudTileColor);
                if (!string.IsNullOrWhiteSpace(quirkId))
                {
                    ArenaQuirkCatalogEntry entry = GetArenaQuirkEntry(quirkId);
                    DrawArenaQuirkIcon(new Rect(rect.x + 6f, rect.y + 6f, 40f, 40f), entry, kind);
                    if (GUI.Button(new Rect(rect.x + 32f, rect.y + 1f, 18f, 18f), "x") && quirkIds != null && i < quirkIds.Count)
                    {
                        quirkIds.RemoveAt(i);
                        _arenaHeroDraftInitialized = true;
                    }
                }
                else
                {
                    GUI.Label(rect, "+", CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.MiddleCenter));
                }

                GUILayout.Space(6f);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        private void DrawArenaQuirkSection(
            string titleText,
            ArenaQuirkKind kind,
            List<string> selectedIds,
            ref string search,
            List<ArenaQuirkCatalogEntry> matches,
            int maxCount,
            ArenaHeroDraftSlot slot = null)
        {
            GUILayout.Space(8f);
            GUILayout.Label(titleText);
            DrawArenaQuirkSearchRow(ref search, kind);
            RefreshArenaQuirkMatchesIfNeeded(kind, false);

            if (!_arenaHeroQuirkCatalogBuilt)
            {
                DrawWrappedLabel(Ui("Quirk library is not loaded yet.", "怪癖库尚未加载。"));
                return;
            }

            if (matches == null || matches.Count == 0)
            {
                DrawWrappedLabel(Ui("No matching quirk.", "没有匹配的怪癖。"));
                return;
            }

            foreach (ArenaQuirkCatalogEntry entry in matches)
            {
                bool selected = kind == ArenaQuirkKind.Disease && slot != null
                    ? string.Equals(slot.DiseaseQuirkId, entry.QuirkId, StringComparison.Ordinal)
                    : selectedIds != null && selectedIds.Contains(entry.QuirkId);
                DrawArenaQuirkCatalogRow(entry, selected, () =>
                {
                    if (kind == ArenaQuirkKind.Disease && slot != null)
                    {
                        slot.DiseaseQuirkId = selected ? string.Empty : entry.QuirkId;
                        _arenaHeroDraftInitialized = true;
                        return;
                    }

                    ToggleArenaHeroDraftQuirk(selectedIds, entry.QuirkId, maxCount, kind);
                });
            }
        }

        private void DrawArenaQuirkSearchRow(ref string search, ArenaQuirkKind kind)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Search", "搜索"), GUILayout.Width(54f));
            string next = GUILayout.TextField(search ?? string.Empty);
            if (!string.Equals(next, search ?? string.Empty, StringComparison.Ordinal))
            {
                search = next;
                RefreshArenaQuirkMatchesIfNeeded(kind, true);
            }

            if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(58f), GUILayout.Height(24f)))
            {
                search = string.Empty;
                RefreshArenaQuirkMatchesIfNeeded(kind, true);
            }

            GUILayout.EndHorizontal();
        }

        private void DrawArenaQuirkCatalogRow(ArenaQuirkCatalogEntry entry, bool selected, Action onClick)
        {
            if (entry == null)
            {
                return;
            }

            Rect row = GUILayoutUtility.GetRect(0f, 86f, GUILayout.ExpandWidth(true), GUILayout.Height(86f));
            DrawSolidRect(row, selected ? HudCurrentCardColor : HudCardColor);
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                onClick?.Invoke();
            }

            Rect icon = new Rect(row.x + 8f, row.y + 10f, 52f, 52f);
            DrawArenaQuirkIcon(icon, entry, entry.Kind);

            GUIStyle title = CreateHudLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUI.Label(new Rect(row.x + 70f, row.y + 8f, row.width - 80f, 22f),
                (selected ? "* " : string.Empty) + (entry.DisplayName ?? entry.QuirkId),
                title);
            DrawInlineDescriptionPreview(new Rect(row.x + 70f, row.y + 32f, row.width - 80f, 48f), entry.Description, 11);
        }

        private void DrawArenaQuirkIcon(Rect rect, ArenaQuirkCatalogEntry entry, ArenaQuirkKind fallbackKind)
        {
            ArenaQuirkKind kind = entry == null ? fallbackKind : entry.Kind;
            DrawSolidRect(rect, GetArenaQuirkKindColor(kind));
            Sprite sprite = GetQuirkSprite(kind);
            if (sprite != null)
            {
                DrawSprite(new Rect(rect.x + 4f, rect.y + 4f, rect.width - 8f, rect.height - 8f), sprite);
                return;
            }

            string label = kind == ArenaQuirkKind.Positive ? "+" : kind == ArenaQuirkKind.Negative ? "-" : "D";
            GUI.Label(rect, label, CreateHudLabelStyle(18, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter));
        }

        private static Color GetArenaQuirkKindColor(ArenaQuirkKind kind)
        {
            switch (kind)
            {
                case ArenaQuirkKind.Positive:
                    return new Color(0.16f, 0.34f, 0.24f, 1f);
                case ArenaQuirkKind.Negative:
                    return new Color(0.36f, 0.17f, 0.17f, 1f);
                default:
                    return new Color(0.24f, 0.20f, 0.38f, 1f);
            }
        }

        private void DrawArenaSelectedCombatItem(ArenaHeroDraftSlot slot)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Equipped", "已装备"), GUILayout.Width(74f));
            string itemId = slot == null ? string.Empty : slot.CombatItemId;
            Rect rect = GUILayoutUtility.GetRect(56f, 56f, GUILayout.Width(56f), GUILayout.Height(56f));
            DrawSolidRect(rect, string.IsNullOrWhiteSpace(itemId) ? HudHostOnlyCardColor : HudTileColor);
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                DrawSprite(new Rect(rect.x + 6f, rect.y + 6f, 44f, 44f), GetItemSprite(itemId));
                if (GUI.Button(new Rect(rect.x + 36f, rect.y + 2f, 18f, 18f), "x"))
                {
                    slot.CombatItemId = string.Empty;
                    _arenaHeroDraftInitialized = true;
                }
            }
            else
            {
                GUI.Label(rect, "+", CreateHudLabelStyle(12, FontStyle.Normal, PanelMutedTextColor, TextAnchor.MiddleCenter));
            }

            GUILayout.Space(8f);
            GUILayout.Label(string.IsNullOrWhiteSpace(itemId)
                ? Ui("[none]", "[无]")
                : GetLocalizedItemDisplayName(itemId, itemId));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        private void DrawArenaItemSearchRow(string label, ref string search, bool combatItem)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(54f));
            string next = GUILayout.TextField(search ?? string.Empty);
            if (!string.Equals(next, search ?? string.Empty, StringComparison.Ordinal))
            {
                search = next;
                RefreshArenaHeroItemMatchesIfNeeded(combatItem, true);
            }

            if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(58f), GUILayout.Height(24f)))
            {
                search = string.Empty;
                RefreshArenaHeroItemMatchesIfNeeded(combatItem, true);
            }

            GUILayout.EndHorizontal();
        }

        private void DrawArenaDraftSelectedItemChips(List<string> itemIds, int maxCount)
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < maxCount; i++)
            {
                string itemId = itemIds != null && i < itemIds.Count ? itemIds[i] : string.Empty;
                Rect rect = GUILayoutUtility.GetRect(50f, 50f, GUILayout.Width(50f), GUILayout.Height(50f));
                DrawSolidRect(rect, string.IsNullOrWhiteSpace(itemId) ? HudHostOnlyCardColor : HudTileColor);
                if (!string.IsNullOrWhiteSpace(itemId))
                {
                    DrawSprite(new Rect(rect.x + 5f, rect.y + 5f, 40f, 40f), GetItemSprite(itemId));
                    if (GUI.Button(new Rect(rect.x + 30f, rect.y + 1f, 18f, 18f), "x") && itemIds != null && i < itemIds.Count)
                    {
                        itemIds.RemoveAt(i);
                        _arenaHeroDraftInitialized = true;
                    }
                }
                else
                {
                    GUI.Label(rect, "+", CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.MiddleCenter));
                }

                GUILayout.Space(6f);
            }

            GUILayout.EndHorizontal();
        }

        private void DrawArenaItemCatalogRow(ArenaItemCatalogEntry entry, bool selected, Action onClick)
        {
            if (entry == null)
            {
                return;
            }

            Rect row = GUILayoutUtility.GetRect(0f, 92f, GUILayout.ExpandWidth(true), GUILayout.Height(92f));
            DrawSolidRect(row, selected ? HudCurrentCardColor : HudCardColor);
            string description = string.IsNullOrWhiteSpace(entry.Description)
                ? GetLocalizedItemDescription(entry.ItemId)
                : entry.Description;
            string previewDescription = entry.PreviewDescription;
            if (string.IsNullOrWhiteSpace(previewDescription))
            {
                previewDescription = description;
            }
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                onClick?.Invoke();
            }

            Rect icon = new Rect(row.x + 8f, row.y + 10f, 52f, 52f);
            DrawSolidRect(icon, new Color(0.05f, 0.055f, 0.06f, 1f));
            DrawSprite(icon, GetItemSprite(entry.ItemId));
            GUIStyle title = CreateHudLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUI.Label(new Rect(row.x + 70f, row.y + 8f, row.width - 80f, 22f),
                (selected ? "* " : string.Empty) + (entry.DisplayName ?? entry.ItemId),
                title);
            DrawInlineDescriptionPreview(new Rect(row.x + 70f, row.y + 32f, row.width - 80f, 54f), previewDescription, 11);
        }

        private void DrawInlineDescriptionPreview(Rect rect, string description, int fontSize)
        {
            string text = string.IsNullOrWhiteSpace(description)
                ? Ui("[no description]", "[无描述]")
                : description;
            GUIStyle style = CreateHudLabelStyle(fontSize, FontStyle.Normal, PanelTextColor, TextAnchor.UpperLeft);
            style.wordWrap = false;
            style.clipping = TextClipping.Clip;
            DrawTooltipBody(rect, GetCachedTooltipSegments(text), style);
        }

        private float CalculateInlineDescriptionPreviewHeight(string description, int fontSize, float width)
        {
            string text = string.IsNullOrWhiteSpace(description)
                ? Ui("[no description]", "[无描述]")
                : description;
            GUIStyle style = CreateHudLabelStyle(fontSize, FontStyle.Normal, PanelTextColor, TextAnchor.UpperLeft);
            style.wordWrap = false;
            style.clipping = TextClipping.Clip;
            return Mathf.Max(22f, CalculateTooltipBodyHeight(GetCachedTooltipSegments(text), style, Mathf.Max(120f, width)));
        }

        private IList<TooltipSegment> GetCachedTooltipSegments(string text)
        {
            string key = text ?? string.Empty;
            IList<TooltipSegment> cached;
            if (_tooltipSegmentCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            if (_tooltipSegmentCache.Count > 512)
            {
                _tooltipSegmentCache.Clear();
            }

            cached = BuildTooltipSegments(key);
            _tooltipSegmentCache[key] = cached;
            return cached;
        }

        private void DrawArenaHeroStartBuffPicker()
        {
            EnsureArenaHeroStartEffectCatalog();

            GUILayout.Label(Ui("Hero Start Buffs", "英雄开局 Buff"));
            DrawWrappedLabel(Ui(
                "Selected effects are written to hero_test_start_effect and applied to all four heroes when the Arena draft is launched.",
                "选中的效果会写入 hero_test_start_effect，在竞技场草案启动时套给四名英雄。"));

            DrawArenaSelectedHeroStartEffects();
            DrawArenaEffectSearchRow();
            RefreshArenaHeroStartEffectMatchesIfNeeded(false);
            if (!_arenaHeroStartEffectCatalogBuilt)
            {
                DrawWrappedLabel(Ui("Effect library is not loaded yet.", "效果库尚未加载。"));
            }
            else if (_arenaHeroStartEffectMatches.Count == 0)
            {
                DrawWrappedLabel(Ui("No matching effect.", "没有匹配的效果。"));
            }

            foreach (ArenaEffectCatalogEntry entry in _arenaHeroStartEffectMatches)
            {
                DrawArenaEffectCatalogRow(entry);
            }
        }

        private void DrawArenaEnemyOrdainmentPicker()
        {
            EnsureArenaBossModifierCatalog();

            GUILayout.Label(Ui("Enemy Ordainment", "敌方赐福"));
            DrawWrappedLabel(Ui(
                "This writes run_test_boss_modifier. It only applies when the selected BossModifier is valid for the spawned enemy class.",
                "这里会写入 run_test_boss_modifier。只有当所选 BossModifier 对生成的敌人类型有效时才会生效。"));

            DrawWrappedLabel(Ui("Selected: ", "当前：") +
                (string.IsNullOrWhiteSpace(_arenaBossModifierId)
                    ? Ui("[none]", "[无]")
                    : GetArenaBossModifierDisplayName(_arenaBossModifierId)));
            DrawArenaBossModifierSearchRow();
            RefreshArenaBossModifierMatchesIfNeeded(false);
            if (!_arenaBossModifierCatalogBuilt)
            {
                DrawWrappedLabel(Ui("Boss modifier library is not loaded yet.", "BossModifier 库尚未加载。"));
            }
            else if (_arenaBossModifierMatches.Count == 0)
            {
                DrawWrappedLabel(Ui("No matching modifier.", "没有匹配的赐福。"));
            }

            DrawArenaBossModifierNoneRow();
            foreach (ArenaBossModifierCatalogEntry entry in _arenaBossModifierMatches)
            {
                DrawArenaBossModifierCatalogRow(entry);
            }
        }

        private void DrawArenaSelectedHeroStartEffects()
        {
            if (_arenaHeroStartEffectIds.Count == 0)
            {
                DrawWrappedLabel(Ui("Selected: [none]", "当前：[无]"));
                return;
            }

            for (int i = 0; i < _arenaHeroStartEffectIds.Count; i++)
            {
                string effectId = _arenaHeroStartEffectIds[i];
                ArenaEffectCatalogEntry entry = GetArenaHeroStartEffectEntry(effectId);
                GUILayout.BeginHorizontal();
                DrawWrappedLabel("#" + (i + 1) + " " +
                    (entry == null ? effectId : entry.DisplayName));
                if (GUILayout.Button("X", GUILayout.Width(28f), GUILayout.Height(22f)))
                {
                    _arenaHeroStartEffectIds.RemoveAt(i);
                    i--;
                }

                GUILayout.EndHorizontal();
            }
        }

        private void DrawArenaEffectSearchRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Effect", "效果"), GUILayout.Width(54f));
            string next = GUILayout.TextField(_arenaHeroStartEffectSearch ?? string.Empty);
            if (!string.Equals(next, _arenaHeroStartEffectSearch ?? string.Empty, StringComparison.Ordinal))
            {
                _arenaHeroStartEffectSearch = next;
                RefreshArenaHeroStartEffectMatchesIfNeeded(true);
            }

            if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(58f), GUILayout.Height(24f)))
            {
                _arenaHeroStartEffectSearch = string.Empty;
                RefreshArenaHeroStartEffectMatchesIfNeeded(true);
            }

            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && _arenaHeroStartEffectIds.Count > 0;
            if (GUILayout.Button(Ui("Clear Selected", "清空已选"), GUILayout.Width(106f), GUILayout.Height(24f)))
            {
                _arenaHeroStartEffectIds.Clear();
                _arenaHeroDraftInitialized = true;
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawArenaEffectCatalogRow(ArenaEffectCatalogEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            bool selected = _arenaHeroStartEffectIds.Contains(entry.EffectId);
            Rect row = GUILayoutUtility.GetRect(0f, 86f, GUILayout.ExpandWidth(true), GUILayout.Height(86f));
            DrawSolidRect(row, selected ? HudCurrentCardColor : HudCardColor);
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                ToggleArenaHeroStartEffect(entry.EffectId);
            }

            GUIStyle title = CreateHudLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUI.Label(new Rect(row.x + 10f, row.y + 8f, row.width - 20f, 22f),
                (selected ? "* " : string.Empty) + (entry.DisplayName ?? entry.EffectId),
                title);
            DrawInlineDescriptionPreview(new Rect(row.x + 10f, row.y + 32f, row.width - 20f, 48f), entry.Description, 11);
        }

        private void DrawArenaBossModifierSearchRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Search", "搜索"), GUILayout.Width(54f));
            string next = GUILayout.TextField(_arenaBossModifierSearch ?? string.Empty);
            if (!string.Equals(next, _arenaBossModifierSearch ?? string.Empty, StringComparison.Ordinal))
            {
                _arenaBossModifierSearch = next;
                RefreshArenaBossModifierMatchesIfNeeded(true);
            }

            if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(58f), GUILayout.Height(24f)))
            {
                _arenaBossModifierSearch = string.Empty;
                RefreshArenaBossModifierMatchesIfNeeded(true);
            }

            GUILayout.EndHorizontal();
        }

        private void DrawArenaBossModifierNoneRow()
        {
            bool selected = string.IsNullOrWhiteSpace(_arenaBossModifierId);
            Rect row = GUILayoutUtility.GetRect(0f, 36f, GUILayout.ExpandWidth(true), GUILayout.Height(36f));
            DrawSolidRect(row, selected ? HudCurrentCardColor : HudCardColor);
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                _arenaBossModifierId = string.Empty;
                _arenaHeroDraftInitialized = true;
            }

            GUI.Label(
                new Rect(row.x + 8f, row.y + 8f, row.width - 16f, 18f),
                selected ? Ui("* No forced ordainment", "* 不强制赐福") : Ui("No forced ordainment", "不强制赐福"),
                CreateHudLabelStyle(11, FontStyle.Bold, Color.white, TextAnchor.UpperLeft));
        }

        private void DrawArenaBossModifierCatalogRow(ArenaBossModifierCatalogEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            bool selected = string.Equals(_arenaBossModifierId, entry.ModifierId, StringComparison.Ordinal);
            Rect row = GUILayoutUtility.GetRect(0f, 86f, GUILayout.ExpandWidth(true), GUILayout.Height(86f));
            DrawSolidRect(row, selected ? HudCurrentCardColor : HudCardColor);
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                _arenaBossModifierId = entry.ModifierId;
                _arenaHeroDraftInitialized = true;
            }

            GUIStyle title = CreateHudLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUI.Label(new Rect(row.x + 10f, row.y + 8f, row.width - 20f, 22f),
                (selected ? "* " : string.Empty) + (entry.DisplayName ?? entry.ModifierId),
                title);
            DrawInlineDescriptionPreview(new Rect(row.x + 10f, row.y + 32f, row.width - 20f, 48f), entry.Description, 11);
        }

        private void ToggleArenaHeroStartEffect(string effectId)
        {
            string id = (effectId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (_arenaHeroStartEffectIds.Contains(id))
            {
                _arenaHeroStartEffectIds.Remove(id);
                _arenaHeroDraftInitialized = true;
                return;
            }

            if (_arenaHeroStartEffectIds.Count >= 12)
            {
                _arenaStatus = "Hero start effect list is capped at 12 effects.";
                return;
            }

            _arenaHeroStartEffectIds.Add(id);
            _arenaHeroDraftInitialized = true;
        }

        private void ToggleArenaHeroDraftQuirk(List<string> selectedIds, string quirkId, int maxCount, ArenaQuirkKind kind)
        {
            if (selectedIds == null)
            {
                return;
            }

            string id = (quirkId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (selectedIds.Contains(id))
            {
                selectedIds.Remove(id);
                _arenaHeroDraftInitialized = true;
                return;
            }

            if (selectedIds.Count >= maxCount)
            {
                _arenaStatus = GetArenaQuirkKindLabel(kind) + " quirk list is capped at " + maxCount + ".";
                return;
            }

            selectedIds.Add(id);
            _arenaHeroDraftInitialized = true;
        }

        private static IEnumerable<string> GetArenaDraftQuirkIds(ArenaHeroDraftSlot slot)
        {
            if (slot == null)
            {
                yield break;
            }

            foreach (string id in slot.PositiveQuirkIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    yield return id.Trim();
                }
            }

            foreach (string id in slot.NegativeQuirkIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    yield return id.Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(slot.DiseaseQuirkId))
            {
                yield return slot.DiseaseQuirkId.Trim();
            }
        }

        private bool EnsureArenaHeroStartEffectCatalog()
        {
            if (_arenaHeroStartEffectCatalogBuilt)
            {
                return true;
            }

            return RebuildArenaHeroStartEffectCatalog();
        }

        private bool RebuildArenaHeroStartEffectCatalog()
        {
            _arenaHeroStartEffectCatalog.Clear();
            _arenaHeroStartEffectMatches.Clear();
            _arenaHeroStartEffectSearchApplied = null;
            _arenaHeroStartEffectCatalogTotalCount = 0;

            try
            {
                if (!SingletonMonoBehaviour<Library<string, EffectDefinition>>.HasInstance(false))
                {
                    _arenaHeroStartEffectCatalogBuilt = false;
                    return false;
                }

                Library<string, EffectDefinition> library = SingletonMonoBehaviour<Library<string, EffectDefinition>>.Instance;
                _arenaHeroStartEffectCatalogTotalCount = library.GetNumberOfLibraryElements();
                for (int i = 0; i < _arenaHeroStartEffectCatalogTotalCount; i++)
                {
                    EffectDefinition definition = library.GetLibraryElementAtIndex(i);
                    if (definition == null || string.IsNullOrWhiteSpace(definition.m_Id))
                    {
                        continue;
                    }

                    string displayName = GetArenaEffectDisplayName(definition);
                    string description = GetArenaEffectDescription(definition);
                    _arenaHeroStartEffectCatalog.Add(new ArenaEffectCatalogEntry
                    {
                        EffectId = definition.m_Id,
                        DisplayName = displayName,
                        Description = description,
                        Definition = definition,
                        SearchText = (definition.m_Id + " " + displayName + " " + description).ToLowerInvariant(),
                    });
                }

                _arenaHeroStartEffectCatalog.Sort(CompareArenaEffectCatalogEntries);
                _arenaHeroStartEffectCatalogBuilt = true;
                RefreshArenaHeroStartEffectMatchesIfNeeded(true);
                HostLog.Write("[arena] Hero start effect catalog built: " +
                    _arenaHeroStartEffectCatalog.Count + "/" + _arenaHeroStartEffectCatalogTotalCount + ".");
                return true;
            }
            catch (Exception ex)
            {
                _arenaHeroStartEffectCatalogBuilt = false;
                HostLog.Write("[arena] Failed to build hero start effect catalog: " + ex.Message);
                return false;
            }
        }

        private static int CompareArenaEffectCatalogEntries(ArenaEffectCatalogEntry left, ArenaEffectCatalogEntry right)
        {
            return string.Compare(
                left == null ? string.Empty : left.DisplayName,
                right == null ? string.Empty : right.DisplayName,
                StringComparison.CurrentCultureIgnoreCase);
        }

        private void RefreshArenaHeroStartEffectMatchesIfNeeded(bool force)
        {
            string query = (_arenaHeroStartEffectSearch ?? string.Empty).Trim().ToLowerInvariant();
            if (!force && string.Equals(_arenaHeroStartEffectSearchApplied, query, StringComparison.Ordinal))
            {
                return;
            }

            _arenaHeroStartEffectMatches.Clear();
            string[] terms = query
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .ToArray();
            foreach (ArenaEffectCatalogEntry entry in _arenaHeroStartEffectCatalog)
            {
                if (terms.Length > 0 && !terms.All(term => (entry.SearchText ?? string.Empty).Contains(term)))
                {
                    continue;
                }

                if (_arenaHeroStartEffectMatches.Count < 120)
                {
                    _arenaHeroStartEffectMatches.Add(entry);
                }
            }

            _arenaHeroStartEffectSearchApplied = query;
        }

        private ArenaEffectCatalogEntry GetArenaHeroStartEffectEntry(string effectId)
        {
            string id = (effectId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            EnsureArenaHeroStartEffectCatalog();
            return _arenaHeroStartEffectCatalog.FirstOrDefault(entry =>
                entry != null && string.Equals(entry.EffectId, id, StringComparison.Ordinal));
        }

        private static EffectDefinition TryGetArenaEffectDefinition(string effectId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(effectId) ||
                    !SingletonMonoBehaviour<Library<string, EffectDefinition>>.HasInstance(false))
                {
                    return null;
                }

                return SingletonMonoBehaviour<Library<string, EffectDefinition>>.Instance.GetLibraryElement(effectId.Trim());
            }
            catch
            {
                return null;
            }
        }

        private static string GetArenaEffectDisplayName(EffectDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.m_Id))
            {
                return "[effect]";
            }

            string localized = TryGetLocalizedText(
                "effect_" + definition.m_Id + "_name",
                "effect_skill_" + definition.m_Id + "_name",
                "effect_name_" + definition.m_Id);
            return string.IsNullOrWhiteSpace(localized)
                ? HumanizeArenaInternalId(definition.m_Id)
                : localized;
        }

        private static string GetArenaEffectDescription(EffectDefinition definition)
        {
            if (definition == null)
            {
                return string.Empty;
            }

            try
            {
                string description = EffectDescription.GetDescription(definition, true, true);
                return string.IsNullOrWhiteSpace(description) ? string.Empty : description;
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool EnsureArenaBossModifierCatalog()
        {
            if (_arenaBossModifierCatalogBuilt)
            {
                return true;
            }

            return RebuildArenaBossModifierCatalog();
        }

        private bool RebuildArenaBossModifierCatalog()
        {
            _arenaBossModifierCatalog.Clear();
            _arenaBossModifierMatches.Clear();
            _arenaBossModifierSearchApplied = null;
            _arenaBossModifierCatalogTotalCount = 0;

            try
            {
                if (!SingletonMonoBehaviour<Library<string, BossModifierDefinition>>.HasInstance(false))
                {
                    _arenaBossModifierCatalogBuilt = false;
                    return false;
                }

                Library<string, BossModifierDefinition> library = SingletonMonoBehaviour<Library<string, BossModifierDefinition>>.Instance;
                _arenaBossModifierCatalogTotalCount = library.GetNumberOfLibraryElements();
                for (int i = 0; i < _arenaBossModifierCatalogTotalCount; i++)
                {
                    BossModifierDefinition definition = library.GetLibraryElementAtIndex(i);
                    if (definition == null || string.IsNullOrWhiteSpace(definition.m_Id))
                    {
                        continue;
                    }

                    string displayName = GetArenaBossModifierDisplayName(definition);
                    string description = GetArenaBossModifierDescription(definition);
                    string tags = definition.ActorDataTags == null
                        ? string.Empty
                        : string.Join(" ", definition.ActorDataTags.ToArray());
                    _arenaBossModifierCatalog.Add(new ArenaBossModifierCatalogEntry
                    {
                        ModifierId = definition.m_Id,
                        DisplayName = displayName,
                        Description = description,
                        Definition = definition,
                        SearchText = (definition.m_Id + " " + displayName + " " + description + " " + tags).ToLowerInvariant(),
                    });
                }

                _arenaBossModifierCatalog.Sort(CompareArenaBossModifierCatalogEntries);
                _arenaBossModifierCatalogBuilt = true;
                RefreshArenaBossModifierMatchesIfNeeded(true);
                HostLog.Write("[arena] Boss modifier catalog built: " +
                    _arenaBossModifierCatalog.Count + "/" + _arenaBossModifierCatalogTotalCount + ".");
                return true;
            }
            catch (Exception ex)
            {
                _arenaBossModifierCatalogBuilt = false;
                HostLog.Write("[arena] Failed to build boss modifier catalog: " + ex.Message);
                return false;
            }
        }

        private static int CompareArenaBossModifierCatalogEntries(ArenaBossModifierCatalogEntry left, ArenaBossModifierCatalogEntry right)
        {
            return string.Compare(
                left == null ? string.Empty : left.DisplayName,
                right == null ? string.Empty : right.DisplayName,
                StringComparison.CurrentCultureIgnoreCase);
        }

        private void RefreshArenaBossModifierMatchesIfNeeded(bool force)
        {
            string query = (_arenaBossModifierSearch ?? string.Empty).Trim().ToLowerInvariant();
            if (!force && string.Equals(_arenaBossModifierSearchApplied, query, StringComparison.Ordinal))
            {
                return;
            }

            _arenaBossModifierMatches.Clear();
            string[] terms = query
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .ToArray();
            foreach (ArenaBossModifierCatalogEntry entry in _arenaBossModifierCatalog)
            {
                if (terms.Length > 0 && !terms.All(term => (entry.SearchText ?? string.Empty).Contains(term)))
                {
                    continue;
                }

                if (_arenaBossModifierMatches.Count < 80)
                {
                    _arenaBossModifierMatches.Add(entry);
                }
            }

            _arenaBossModifierSearchApplied = query;
        }

        private static BossModifierDefinition TryGetArenaBossModifierDefinition(string modifierId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(modifierId) ||
                    !SingletonMonoBehaviour<Library<string, BossModifierDefinition>>.HasInstance(false))
                {
                    return null;
                }

                return SingletonMonoBehaviour<Library<string, BossModifierDefinition>>.Instance.GetLibraryElement(modifierId.Trim());
            }
            catch
            {
                return null;
            }
        }

        private static string GetArenaBossModifierDisplayName(BossModifierDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.m_Id))
            {
                return "[ordainment]";
            }

            string localized = TryGetLocalizedText(
                "boss_modifier_" + definition.m_Id + "_name",
                "boss_modifier_name_" + definition.m_Id,
                "ordainment_" + definition.m_Id + "_name");
            return string.IsNullOrWhiteSpace(localized)
                ? HumanizeArenaInternalId(definition.m_Id)
                : localized;
        }

        private static string GetArenaBossModifierDisplayName(string modifierId)
        {
            BossModifierDefinition definition = TryGetArenaBossModifierDefinition(modifierId);
            return definition == null ? HumanizeArenaInternalId(modifierId) : GetArenaBossModifierDisplayName(definition);
        }

        private static string GetArenaBossModifierDescription(BossModifierDefinition definition)
        {
            if (definition == null)
            {
                return string.Empty;
            }

            try
            {
                string description = BossDescription.GetBossModifierDescription(definition);
                return string.IsNullOrWhiteSpace(description) ? string.Empty : description;
            }
            catch
            {
                return string.Empty;
            }
        }

        private ArenaBattleAdvantageMode GetEffectiveArenaBattleAdvantageMode()
        {
            return HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots)
                ? ArenaBattleAdvantageMode.None
                : _arenaBattleAdvantageMode;
        }

        private string GetArenaBattleAdvantageDisplaySummary()
        {
            if (HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots))
            {
                return Ui("disabled for hero-vs-hero", "英雄对战中禁用");
            }

            switch (_arenaBattleAdvantageMode)
            {
                case ArenaBattleAdvantageMode.Random:
                    return Ui("random monster advantage", "随机怪物优势");
                case ArenaBattleAdvantageMode.Specific:
                    return string.IsNullOrWhiteSpace(_arenaBattleModifierId)
                        ? Ui("specific: [not selected]", "指定：[未选择]")
                        : Ui("specific: ", "指定：") + GetArenaBattleModifierDisplayName(_arenaBattleModifierId);
                default:
                    return Ui("none", "无");
            }
        }

        private static string GetArenaBattleAdvantageModeName(ArenaBattleAdvantageMode mode)
        {
            switch (mode)
            {
                case ArenaBattleAdvantageMode.Random:
                    return "random";
                case ArenaBattleAdvantageMode.Specific:
                    return "specific";
                default:
                    return "none";
            }
        }

        private bool EnsureArenaBattleModifierCatalog()
        {
            if (_arenaBattleModifierCatalogBuilt)
            {
                return true;
            }

            return RebuildArenaBattleModifierCatalog();
        }

        private bool RebuildArenaBattleModifierCatalog()
        {
            _arenaBattleModifierCatalog.Clear();
            _arenaBattleModifierMatches.Clear();
            _arenaBattleModifierSearchApplied = null;

            try
            {
                if (!SingletonMonoBehaviour<Library<string, BattleModifierDefinition>>.HasInstance(false))
                {
                    _arenaBattleModifierCatalogBuilt = false;
                    return false;
                }

                Library<string, BattleModifierDefinition> library =
                    SingletonMonoBehaviour<Library<string, BattleModifierDefinition>>.Instance;
                int total = library.GetNumberOfLibraryElements();
                for (int i = 0; i < total; i++)
                {
                    BattleModifierDefinition definition = library.GetLibraryElementAtIndex(i);
                    if (!IsArenaMonsterBattleModifier(definition))
                    {
                        continue;
                    }

                    string displayName = GetArenaBattleModifierDisplayName(definition);
                    string description = GetArenaBattleModifierDescription(definition);
                    string tags = definition.Tags == null
                        ? string.Empty
                        : string.Join(" ", definition.Tags.ToArray());
                    _arenaBattleModifierCatalog.Add(new ArenaBattleModifierCatalogEntry
                    {
                        ModifierId = definition.m_Id,
                        DisplayName = displayName,
                        Description = description,
                        Definition = definition,
                        SearchText = (definition.m_Id + " " + displayName + " " + description + " " + tags).ToLowerInvariant(),
                    });
                }

                _arenaBattleModifierCatalog.Sort(CompareArenaBattleModifierCatalogEntries);
                _arenaBattleModifierCatalogBuilt = true;
                RefreshArenaBattleModifierMatchesIfNeeded(true);
                HostLog.Write("[arena] Battle modifier catalog built: " +
                    _arenaBattleModifierCatalog.Count + "/" + total + ".");
                return true;
            }
            catch (Exception ex)
            {
                _arenaBattleModifierCatalogBuilt = false;
                HostLog.Write("[arena] Failed to build battle modifier catalog: " + ex.Message);
                return false;
            }
        }

        private static int CompareArenaBattleModifierCatalogEntries(
            ArenaBattleModifierCatalogEntry left,
            ArenaBattleModifierCatalogEntry right)
        {
            return string.Compare(
                left == null ? string.Empty : left.DisplayName,
                right == null ? string.Empty : right.DisplayName,
                StringComparison.CurrentCultureIgnoreCase);
        }

        private void RefreshArenaBattleModifierMatchesIfNeeded(bool force)
        {
            string query = (_arenaBattleModifierSearch ?? string.Empty).Trim().ToLowerInvariant();
            if (!force && string.Equals(_arenaBattleModifierSearchApplied, query, StringComparison.Ordinal))
            {
                return;
            }

            _arenaBattleModifierMatches.Clear();
            string[] terms = query
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .ToArray();
            foreach (ArenaBattleModifierCatalogEntry entry in _arenaBattleModifierCatalog)
            {
                if (terms.Length > 0 && !terms.All(term => (entry.SearchText ?? string.Empty).Contains(term)))
                {
                    continue;
                }

                if (_arenaBattleModifierMatches.Count < 80)
                {
                    _arenaBattleModifierMatches.Add(entry);
                }
            }

            _arenaBattleModifierSearchApplied = query;
        }

        private static BattleModifierDefinition TryGetArenaBattleModifierDefinition(string modifierId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(modifierId) ||
                    !SingletonMonoBehaviour<Library<string, BattleModifierDefinition>>.HasInstance(false))
                {
                    return null;
                }

                BattleModifierDefinition definition =
                    SingletonMonoBehaviour<Library<string, BattleModifierDefinition>>.Instance.GetLibraryElement(modifierId.Trim());
                return IsArenaMonsterBattleModifier(definition) ? definition : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsArenaMonsterBattleModifier(BattleModifierDefinition definition)
        {
            return definition != null &&
                !string.IsNullOrWhiteSpace(definition.m_Id) &&
                definition.m_Chance > 0f &&
                definition.Tags != null &&
                definition.Tags.Contains("monster");
        }

        private BattleModifierDefinition RollArenaBattleModifierIndependentOfRun(BattleConfigurationDefinition battleConfiguration)
        {
            try
            {
                if (!EnsureArenaBattleModifierCatalog() || _arenaBattleModifierCatalog.Count == 0)
                {
                    return null;
                }

                string requiredTag = battleConfiguration == null ? null : battleConfiguration.m_RollBattleModifierTag;
                List<BattleModifierDefinition> candidates = _arenaBattleModifierCatalog
                    .Select(entry => entry == null ? null : entry.Definition)
                    .Where(definition =>
                        IsArenaMonsterBattleModifier(definition) &&
                        (string.IsNullOrWhiteSpace(requiredTag) || definition.Tags.Contains(requiredTag)))
                    .ToList();
                if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(requiredTag))
                {
                    candidates = _arenaBattleModifierCatalog
                        .Select(entry => entry == null ? null : entry.Definition)
                        .Where(IsArenaMonsterBattleModifier)
                        .ToList();
                }

                float totalWeight = candidates.Sum(definition => Mathf.Max(0f, definition.m_Chance));
                if (totalWeight <= 0f)
                {
                    return null;
                }

                float pick = UnityEngine.Random.Range(0f, totalWeight);
                for (int i = 0; i < candidates.Count; i++)
                {
                    BattleModifierDefinition definition = candidates[i];
                    pick -= Mathf.Max(0f, definition.m_Chance);
                    if (pick <= 0f)
                    {
                        return definition;
                    }
                }

                return candidates.LastOrDefault();
            }
            catch (Exception ex)
            {
                HostLog.Write("[arena] Failed to roll independent battle advantage: " + ex.Message);
                return null;
            }
        }

        private static string GetArenaBattleModifierDisplayName(BattleModifierDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.m_Id))
            {
                return "[battle modifier]";
            }

            string localized = TryGetLocalizedText(
                "battle_modifier_title_" + definition.m_Id,
                "battle_modifier_tooltip_title_" + definition.m_Id);
            return string.IsNullOrWhiteSpace(localized)
                ? HumanizeArenaInternalId(definition.m_Id)
                : localized.TrimEnd(':');
        }

        private static string GetArenaBattleModifierDisplayName(string modifierId)
        {
            BattleModifierDefinition definition = TryGetArenaBattleModifierDefinition(modifierId);
            return definition == null ? HumanizeArenaInternalId(modifierId) : GetArenaBattleModifierDisplayName(definition);
        }

        private static string GetArenaBattleModifierDescription(BattleModifierDefinition definition)
        {
            if (definition == null)
            {
                return string.Empty;
            }

            List<string> blocks = new List<string>();
            AddArenaDescriptionBlock(
                blocks,
                TryGetLocalizedText("battle_modifier_tooltip_title_" + definition.m_Id, "battle_modifier_title_" + definition.m_Id));

            try
            {
                ActorDataEffects effects = definition.ActorDataSkillEffects;
                if (effects != null)
                {
                    AddArenaDescriptionBlock(blocks, ActorDataEffectDescription.GetDescription(effects, null, false, true, 0U));
                }
            }
            catch
            {
            }

            try
            {
                DataExternalBuffs buffs = definition.ActorDataExternalBuffs;
                if (buffs != null)
                {
                    foreach (BuffDefinition buff in buffs.GetBuffs())
                    {
                        AddArenaDescriptionBlock(blocks, BuffDescription.GetDescription(buff, false));
                    }
                }
            }
            catch
            {
            }

            return string.Join("\n", blocks.ToArray());
        }

        private Sprite GetBattleModifierSprite(string modifierId)
        {
            if (string.IsNullOrWhiteSpace(modifierId))
            {
                return null;
            }

            modifierId = modifierId.Trim();
            Sprite cached;
            if (_battleModifierSprites.TryGetValue(modifierId, out cached))
            {
                return cached;
            }

            Sprite sprite = null;
            try
            {
                LibraryBattleModifier library = LibraryBattleModifier.LibraryBattleModifierInstance;
                Assets.Code.Combat.ResourceBattleModifier resource =
                    library == null ? null : library.GetBattleModifierResource(modifierId);
                sprite = resource == null ? null : resource.m_modifierIcon;
            }
            catch
            {
                sprite = null;
            }

            _battleModifierSprites[modifierId] = sprite;
            return sprite;
        }

        private bool EnsureArenaTorchCatalog()
        {
            if (_arenaTorchCatalogBuilt)
            {
                return true;
            }

            return RebuildArenaTorchCatalog();
        }

        private bool RebuildArenaTorchCatalog()
        {
            _arenaTorchCatalog.Clear();
            _arenaTorchMatches.Clear();
            _arenaKnownTorchItemIds.Clear();
            _arenaKnownTorchItemTags.Clear();
            _arenaTorchSearchApplied = null;
            _arenaTorchCatalogBuilding = true;

            try
            {
                _arenaTorchCatalog.Add(new ArenaTorchCatalogEntry
                {
                    ProfileId = GetArenaTorchNoneProfileId(),
                    Kind = ArenaTorchCatalogKind.None,
                    DisplayName = Ui("None - isolated demo baseline", "无 - 隔离演示基线"),
                    Description = Ui(
                        "No Flame/Torch level group is applied. The custom battle will not inherit the current run's equipped Flame, confession torch curve, or torch value.",
                        "不应用任何火炬等级组。自定义战斗不会继承当前存档装备的火炬、忏悔烛光曲线或烛光数值。"),
                    SearchText = "none isolated baseline no torch flame " + Ui("none isolated baseline", "无 隔离 基线"),
                });

                if (SingletonMonoBehaviour<Library<string, TorchLevelGroupDefinition>>.HasInstance(false))
                {
                    Library<string, TorchLevelGroupDefinition> groupLibrary =
                        SingletonMonoBehaviour<Library<string, TorchLevelGroupDefinition>>.Instance;
                    foreach (ArenaTorchConfessionProfile profile in ArenaTorchConfessionProfiles)
                    {
                        TorchLevelGroupDefinition group = groupLibrary.GetLibraryElement(profile.GroupId);
                        if (group == null)
                        {
                            continue;
                        }

                        string displayName = Ui(
                            "Confession: " + profile.EnglishName,
                            "忏悔：" + profile.ChineseName);
                        string description = Ui(
                            "Uses the confession torch curve without requiring the current run's selected boss/confession.",
                            "使用该忏悔的烛光曲线，但不要求当前存档真的处于对应忏悔。");
                        _arenaTorchCatalog.Add(new ArenaTorchCatalogEntry
                        {
                            ProfileId = GetArenaTorchGroupProfileId(profile.GroupId),
                            Kind = ArenaTorchCatalogKind.Confession,
                            GroupId = profile.GroupId,
                            DisplayName = displayName,
                            Description = description,
                            SearchText = (profile.GroupId + " " +
                                profile.EnglishName + " " +
                                profile.ChineseName + " " +
                                displayName + " " +
                                description).ToLowerInvariant(),
                            Group = group,
                        });
                    }
                }

                int totalItems = 0;
                if (SingletonMonoBehaviour<Library<string, ItemDefinition>>.HasInstance(false))
                {
                    Library<string, ItemDefinition> itemLibrary = SingletonMonoBehaviour<Library<string, ItemDefinition>>.Instance;
                    totalItems = itemLibrary.GetNumberOfLibraryElements();
                    for (int i = 0; i < totalItems; i++)
                    {
                        ItemDefinition definition = itemLibrary.GetLibraryElementAtIndex(i);
                        if (definition == null || string.IsNullOrWhiteSpace(definition.m_id))
                        {
                            continue;
                        }

                        if (definition.m_slot != ItemSlotType.FLAME && definition.TorchLevelGroup == null)
                        {
                            continue;
                        }

                        _arenaKnownTorchItemIds.Add(definition.m_id);
                        if (definition.m_tags != null)
                        {
                            foreach (string tag in definition.m_tags)
                            {
                                if (!string.IsNullOrWhiteSpace(tag))
                                {
                                    _arenaKnownTorchItemTags.Add(tag.Trim());
                                }
                            }
                        }

                        string displayName = GetLocalizedItemDisplayName(definition.m_id, definition.m_id);
                        string itemDescription = GetLocalizedItemDescription(definition.m_id);
                        string tagText = definition.m_tags == null
                            ? string.Empty
                            : string.Join(" ", definition.m_tags.ToArray());
                        _arenaTorchCatalog.Add(new ArenaTorchCatalogEntry
                        {
                            ProfileId = GetArenaTorchItemProfileId(definition.m_id),
                            Kind = ArenaTorchCatalogKind.FlameItem,
                            GroupId = definition.TorchLevelGroup == null ? string.Empty : definition.TorchLevelGroup.m_Id,
                            ItemId = definition.m_id,
                            DisplayName = displayName,
                            Description = itemDescription,
                            SearchText = (definition.m_id + " " +
                                displayName + " " +
                                itemDescription + " " +
                                tagText + " " +
                                (definition.TorchLevelGroup == null ? string.Empty : definition.TorchLevelGroup.m_Id)).ToLowerInvariant(),
                            Group = definition.TorchLevelGroup,
                            ItemDefinition = definition,
                        });
                    }
                }

                _arenaTorchCatalog.Sort(CompareArenaTorchCatalogEntries);
                _arenaTorchCatalogBuilt = true;
                RefreshArenaTorchMatchesIfNeeded(true);
                HostLog.Write("[arena] Torch catalog built: entries=" + _arenaTorchCatalog.Count +
                    ", flameItems=" + _arenaKnownTorchItemIds.Count +
                    "/" + totalItems +
                    ", knownTags=" + _arenaKnownTorchItemTags.Count + ".");
                return true;
            }
            catch (Exception ex)
            {
                _arenaTorchCatalogBuilt = false;
                HostLog.Write("[arena] Failed to build torch catalog: " + ex.Message);
                return false;
            }
            finally
            {
                _arenaTorchCatalogBuilding = false;
            }
        }

        private static int CompareArenaTorchCatalogEntries(ArenaTorchCatalogEntry left, ArenaTorchCatalogEntry right)
        {
            int leftKind = left == null ? 99 : (int)left.Kind;
            int rightKind = right == null ? 99 : (int)right.Kind;
            int kindCompare = leftKind.CompareTo(rightKind);
            if (kindCompare != 0)
            {
                return kindCompare;
            }

            return string.Compare(
                left == null ? string.Empty : left.DisplayName,
                right == null ? string.Empty : right.DisplayName,
                StringComparison.CurrentCultureIgnoreCase);
        }

        private void RefreshArenaTorchMatchesIfNeeded(bool force)
        {
            string query = (_arenaTorchSearch ?? string.Empty).Trim().ToLowerInvariant();
            if (!force && string.Equals(_arenaTorchSearchApplied, query, StringComparison.Ordinal))
            {
                return;
            }

            _arenaTorchMatches.Clear();
            string[] terms = query
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .ToArray();
            foreach (ArenaTorchCatalogEntry entry in _arenaTorchCatalog)
            {
                if (entry == null)
                {
                    continue;
                }

                if (entry.Kind != ArenaTorchCatalogKind.FlameItem)
                {
                    continue;
                }

                if (terms.Length > 0 && !terms.All(term => (entry.SearchText ?? string.Empty).Contains(term)))
                {
                    continue;
                }

                if (_arenaTorchMatches.Count < 80)
                {
                    _arenaTorchMatches.Add(entry);
                }
            }

            _arenaTorchSearchApplied = query;
        }

        private bool HasArenaTorchConfessionSelected()
        {
            return !string.IsNullOrWhiteSpace(_arenaTorchConfessionGroupId);
        }

        private ArenaTorchCatalogEntry GetArenaTorchNoneEntry()
        {
            EnsureArenaTorchCatalog();
            ArenaTorchCatalogEntry entry = _arenaTorchCatalog.FirstOrDefault(candidate =>
                candidate != null && candidate.Kind == ArenaTorchCatalogKind.None);
            return entry ?? new ArenaTorchCatalogEntry
            {
                ProfileId = GetArenaTorchNoneProfileId(),
                Kind = ArenaTorchCatalogKind.None,
                DisplayName = Ui("None", "无"),
            };
        }

        private string GetArenaTorchRowDisplayName(ArenaTorchCatalogEntry entry, ArenaTorchSelectionSection section)
        {
            if (entry == null)
            {
                return Ui("[missing]", "[缺失]");
            }

            if (entry.Kind == ArenaTorchCatalogKind.None)
            {
                return section == ArenaTorchSelectionSection.Confession
                    ? Ui("No Confession", "无忏悔")
                    : Ui("No Flame", "无火炬");
            }

            return string.IsNullOrWhiteSpace(entry.DisplayName)
                ? (entry.ProfileId ?? entry.GroupId ?? entry.ItemId ?? Ui("[unnamed]", "[未命名]"))
                : entry.DisplayName;
        }

        private bool IsArenaTorchEntrySelected(ArenaTorchCatalogEntry entry, ArenaTorchSelectionSection section)
        {
            if (entry == null)
            {
                return false;
            }

            if (section == ArenaTorchSelectionSection.Confession)
            {
                if (entry.Kind == ArenaTorchCatalogKind.None)
                {
                    return string.IsNullOrWhiteSpace(_arenaTorchConfessionGroupId);
                }

                return entry.Kind == ArenaTorchCatalogKind.Confession &&
                    !string.IsNullOrWhiteSpace(entry.GroupId) &&
                    string.Equals(_arenaTorchConfessionGroupId, entry.GroupId, StringComparison.Ordinal);
            }

            if (entry.Kind == ArenaTorchCatalogKind.None)
            {
                return string.IsNullOrWhiteSpace(_arenaTorchFlameItemId);
            }

            return entry.Kind == ArenaTorchCatalogKind.FlameItem &&
                !string.IsNullOrWhiteSpace(entry.ItemId) &&
                string.Equals(_arenaTorchFlameItemId, entry.ItemId, StringComparison.Ordinal);
        }

        private void SelectArenaTorchEntry(ArenaTorchCatalogEntry entry, ArenaTorchSelectionSection section)
        {
            if (entry == null)
            {
                return;
            }

            if (section == ArenaTorchSelectionSection.Confession)
            {
                if (entry.Kind == ArenaTorchCatalogKind.None)
                {
                    _arenaTorchConfessionGroupId = string.Empty;
                    _arenaTorchFlameItemId = string.Empty;
                    return;
                }

                if (entry.Kind == ArenaTorchCatalogKind.Confession)
                {
                    _arenaTorchConfessionGroupId = entry.GroupId ?? string.Empty;
                }

                return;
            }

            if (!HasArenaTorchConfessionSelected())
            {
                _arenaTorchFlameItemId = string.Empty;
                return;
            }

            if (entry.Kind == ArenaTorchCatalogKind.None)
            {
                _arenaTorchFlameItemId = string.Empty;
                return;
            }

            if (entry.Kind == ArenaTorchCatalogKind.FlameItem)
            {
                _arenaTorchFlameItemId = entry.ItemId ?? string.Empty;
            }
        }

        private static string GetArenaTorchNoneProfileId()
        {
            return "none";
        }

        private static string GetArenaTorchGroupProfileId(string groupId)
        {
            return "group:" + ((groupId ?? string.Empty).Trim());
        }

        private static string GetArenaTorchItemProfileId(string itemId)
        {
            return "item:" + ((itemId ?? string.Empty).Trim());
        }

        private ArenaTorchCatalogEntry GetSelectedArenaTorchConfessionEntry()
        {
            EnsureArenaTorchCatalog();
            if (string.IsNullOrWhiteSpace(_arenaTorchConfessionGroupId))
            {
                return null;
            }

            string groupId = _arenaTorchConfessionGroupId.Trim();
            ArenaTorchCatalogEntry entry = _arenaTorchCatalog.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.Kind == ArenaTorchCatalogKind.Confession &&
                string.Equals(candidate.GroupId, groupId, StringComparison.Ordinal));
            if (entry != null)
            {
                return entry;
            }

            TorchLevelGroupDefinition group = TryGetArenaTorchLevelGroup(groupId);
            if (group != null)
            {
                return new ArenaTorchCatalogEntry
                {
                    ProfileId = GetArenaTorchGroupProfileId(groupId),
                    Kind = ArenaTorchCatalogKind.Confession,
                    GroupId = groupId,
                    DisplayName = HumanizeArenaInternalId(groupId),
                    Group = group,
                };
            }

            return null;
        }

        private ArenaTorchCatalogEntry GetSelectedArenaTorchFlameEntry()
        {
            EnsureArenaTorchCatalog();
            if (!HasArenaTorchConfessionSelected() || string.IsNullOrWhiteSpace(_arenaTorchFlameItemId))
            {
                return null;
            }

            string itemId = _arenaTorchFlameItemId.Trim();
            ArenaTorchCatalogEntry entry = _arenaTorchCatalog.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.Kind == ArenaTorchCatalogKind.FlameItem &&
                string.Equals(candidate.ItemId, itemId, StringComparison.Ordinal));
            if (entry != null)
            {
                return entry;
            }

            ItemDefinition item = TryGetItemDefinition(itemId);
            if (item != null)
            {
                return new ArenaTorchCatalogEntry
                {
                    ProfileId = GetArenaTorchItemProfileId(item.m_id),
                    Kind = ArenaTorchCatalogKind.FlameItem,
                    GroupId = item.TorchLevelGroup == null ? string.Empty : item.TorchLevelGroup.m_Id,
                    ItemId = item.m_id,
                    DisplayName = GetLocalizedItemDisplayName(item.m_id, item.m_id),
                    Description = GetLocalizedItemDescription(item.m_id),
                    Group = item.TorchLevelGroup,
                    ItemDefinition = item,
                };
            }

            return null;
        }

        private static TorchLevelGroupDefinition TryGetArenaTorchLevelGroup(string groupId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(groupId) ||
                    !SingletonMonoBehaviour<Library<string, TorchLevelGroupDefinition>>.HasInstance(false))
                {
                    return null;
                }

                return SingletonMonoBehaviour<Library<string, TorchLevelGroupDefinition>>.Instance.GetLibraryElement(groupId.Trim());
            }
            catch
            {
                return null;
            }
        }

        private TorchLevelGroupDefinition ResolveArenaTorchLevelGroupOverride()
        {
            if (HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots))
            {
                return null;
            }

            ArenaTorchCatalogEntry flame = GetSelectedArenaTorchFlameEntry();
            if (flame != null && flame.Group != null)
            {
                return flame.Group;
            }

            if (flame != null && !string.IsNullOrWhiteSpace(flame.GroupId))
            {
                TorchLevelGroupDefinition flameGroup = TryGetArenaTorchLevelGroup(flame.GroupId);
                if (flameGroup != null)
                {
                    return flameGroup;
                }
            }

            ArenaTorchCatalogEntry confession = GetSelectedArenaTorchConfessionEntry();
            if (confession == null)
            {
                return null;
            }

            if (confession.Group != null)
            {
                return confession.Group;
            }

            if (!string.IsNullOrWhiteSpace(confession.GroupId))
            {
                return TryGetArenaTorchLevelGroup(confession.GroupId);
            }

            return null;
        }

        private ItemDefinition GetSelectedArenaTorchItemDefinition()
        {
            if (HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots))
            {
                return null;
            }

            ArenaTorchCatalogEntry entry = GetSelectedArenaTorchFlameEntry();
            if (entry == null)
            {
                return null;
            }

            if (entry.ItemDefinition != null)
            {
                return entry.ItemDefinition;
            }

            return TryGetItemDefinition(entry.ItemId);
        }

        private float GetEffectiveArenaTorchValue()
        {
            return Mathf.Clamp(Mathf.Round(_arenaTorchValue), 0f, 100f);
        }

        private string GetArenaTorchDisplaySummary()
        {
            if (HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots))
            {
                return Ui("disabled for hero-vs-hero", "英雄对战中禁用");
            }

            ArenaTorchCatalogEntry confession = GetSelectedArenaTorchConfessionEntry();
            if (confession == null)
            {
                return Ui("confession=[none], Flame/Torch disabled", "忏悔=[无]，火炬/烛光禁用");
            }

            ArenaTorchCatalogEntry flame = GetSelectedArenaTorchFlameEntry();
            TorchLevelGroupDefinition activeGroup = ResolveArenaTorchLevelGroupOverride();
            return Ui("confession=", "忏悔=") +
                GetArenaTorchRowDisplayName(confession, ArenaTorchSelectionSection.Confession) +
                ", " + Ui("brightness=", "烛光=") +
                GetEffectiveArenaTorchValue().ToString("0", CultureInfo.InvariantCulture) +
                ", " + Ui("flame=", "火炬=") +
                (flame == null ? Ui("[none]", "[无]") : GetArenaTorchRowDisplayName(flame, ArenaTorchSelectionSection.Flame)) +
                ", group=" + (activeGroup == null ? "[none]" : activeGroup.m_Id);
        }

        private bool IsKnownArenaTorchItemId(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return false;
            }

            if (!_arenaTorchCatalogBuilt && !_arenaTorchCatalogBuilding)
            {
                EnsureArenaTorchCatalog();
            }

            return _arenaKnownTorchItemIds.Contains(itemId.Trim());
        }

        private bool IsSelectedArenaTorchItemId(string itemId)
        {
            ItemDefinition selected = GetSelectedArenaTorchItemDefinition();
            return selected != null &&
                !string.IsNullOrWhiteSpace(itemId) &&
                string.Equals(selected.m_id, itemId.Trim(), StringComparison.Ordinal);
        }

        private bool IsKnownArenaTorchItemTag(string itemTag)
        {
            if (string.IsNullOrWhiteSpace(itemTag))
            {
                return false;
            }

            if (!_arenaTorchCatalogBuilt && !_arenaTorchCatalogBuilding)
            {
                EnsureArenaTorchCatalog();
            }

            return _arenaKnownTorchItemTags.Contains(itemTag.Trim());
        }

        private bool SelectedArenaTorchHasTag(string itemTag)
        {
            ItemDefinition selected = GetSelectedArenaTorchItemDefinition();
            if (selected == null || selected.m_tags == null || string.IsNullOrWhiteSpace(itemTag))
            {
                return false;
            }

            string tag = itemTag.Trim();
            return selected.m_tags.Any(candidate => string.Equals(candidate, tag, StringComparison.Ordinal));
        }

        private string BuildArenaTorchEntryDescription(ArenaTorchCatalogEntry entry, ArenaTorchSelectionSection section)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            List<string> blocks = new List<string>();
            if (entry.Kind == ArenaTorchCatalogKind.None)
            {
                AddArenaDescriptionBlock(blocks, section == ArenaTorchSelectionSection.Confession
                    ? Ui(
                        "No confession context is selected. Brightness and Flame selection are disabled, and the custom battle will not inherit the current run's confession torch curve.",
                        "未选择忏悔环境。烛光和火炬类型会被禁用，自定义战斗不会继承当前存档的忏悔烛光曲线。")
                    : Ui(
                        "No Flame item is virtualized. The selected confession's torch curve remains active by itself.",
                        "不虚拟装备火炬物品，仅使用所选忏悔自身的烛光曲线。"));
            }
            else
            {
                AddArenaDescriptionBlock(blocks, entry.Description);
            }

            TorchLevelGroupDefinition group = entry.Group;
            if (group == null && !string.IsNullOrWhiteSpace(entry.GroupId))
            {
                group = TryGetArenaTorchLevelGroup(entry.GroupId);
            }

            if (section == ArenaTorchSelectionSection.Flame &&
                entry.Kind == ArenaTorchCatalogKind.FlameItem &&
                group != null)
            {
                AddArenaDescriptionBlock(blocks, Ui(
                    "This Flame has its own TorchLevelGroup and will override the selected confession's active torch group during the custom battle, matching the game's Stagecoach Flame priority.",
                    "该火炬拥有自己的 TorchLevelGroup，自定义战斗期间会按游戏原生马车火炬优先级覆盖所选忏悔的活动烛光组。"));
            }

            if (entry.Kind != ArenaTorchCatalogKind.None)
            {
                AddArenaDescriptionBlock(blocks, BuildArenaTorchGroupDescription(group, GetEffectiveArenaTorchValue()));
            }

            try
            {
                if (entry.ItemDefinition != null &&
                    entry.ItemDefinition.m_tags != null &&
                    entry.ItemDefinition.m_tags.Contains("infernal_torch_boss_hidden"))
                {
                    AddArenaDescriptionBlock(blocks, Ui(
                        "Boss-specific Infernal Flame conditions are virtualized during launch, so boss-only modifiers that check the equipped Flame tag can activate.",
                        "启动期间会虚拟化 boss 专属炼狱火炬条件，因此检查已装备火炬标签的 boss 专属调整可以生效。"));
                }
            }
            catch
            {
            }

            return string.Join("\n", blocks.ToArray());
        }

        private static string BuildArenaTorchGroupDescription(TorchLevelGroupDefinition group, float torchValue)
        {
            if (group == null || group.TorchLevels == null)
            {
                return string.Empty;
            }

            List<string> blocks = new List<string>();
            foreach (TorchLevelDefinition level in group.TorchLevels)
            {
                if (level == null)
                {
                    continue;
                }

                if (!torchValue.IsInEpsilonRange(level.m_TorchMin, level.m_TorchMax))
                {
                    continue;
                }

                try
                {
                    string description = TorchDescription.GetDescription(level);
                    AddArenaDescriptionBlock(blocks, description);
                }
                catch
                {
                    List<string> fallback = new List<string>();
                    if (level.m_Tags != null && level.m_Tags.Count > 0)
                    {
                        fallback.Add("Tags: " + string.Join(", ", level.m_Tags.ToArray()));
                    }

                    try
                    {
                        if (level.ActorDataSkillEffects != null)
                        {
                            AddArenaDescriptionBlock(fallback, ActorDataEffectDescription.GetDescription(level.ActorDataSkillEffects, null, false, true, 0U));
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        if (level.ActorDataExternalBuffs != null)
                        {
                            AddArenaDescriptionBlock(fallback, BuffDescription.GetDescription(level.ActorDataExternalBuffs.GetBuffs(), false));
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        if (level.RunDataStats != null)
                        {
                            AddArenaDescriptionBlock(fallback, BuffDescription.GetDescription(null, level.RunDataStats, null));
                        }
                    }
                    catch
                    {
                    }

                    AddArenaDescriptionBlock(blocks, string.Join("\n", fallback.ToArray()));
                }
            }

            return string.Join("\n", blocks.ToArray());
        }

        private Sprite GetArenaTorchSprite(ArenaTorchCatalogEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.ItemId))
            {
                return null;
            }

            Sprite cached;
            if (_arenaTorchSprites.TryGetValue(entry.ItemId, out cached))
            {
                return cached;
            }

            Sprite sprite = GetItemSprite(entry.ItemId);
            _arenaTorchSprites[entry.ItemId] = sprite;
            return sprite;
        }

        private void LogArenaTorchOverride(TorchLevelGroupDefinition group)
        {
            string key = (_arenaTorchConfessionGroupId ?? string.Empty) +
                "|" + (_arenaTorchFlameItemId ?? string.Empty) +
                "|" + GetEffectiveArenaTorchValue().ToString("0", CultureInfo.InvariantCulture) +
                "|" + (group == null ? "[none]" : group.m_Id);
            if (string.Equals(_arenaTorchOverrideLogKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _arenaTorchOverrideLogKey = key;
            ItemDefinition item = GetSelectedArenaTorchItemDefinition();
            HostLog.Write("[arena] Torch override active: profile=" + GetArenaTorchDisplaySummary() +
                ", group=" + (group == null ? "[none]" : group.m_Id) +
                ", item=" + (item == null ? "[none]" : item.m_id) + ".");
        }

        private bool EnsureArenaHeroQuirkCatalog()
        {
            if (_arenaHeroQuirkCatalogBuilt)
            {
                return true;
            }

            return RebuildArenaHeroQuirkCatalog();
        }

        private bool RebuildArenaHeroQuirkCatalog()
        {
            _arenaPositiveQuirkCatalog.Clear();
            _arenaNegativeQuirkCatalog.Clear();
            _arenaDiseaseQuirkCatalog.Clear();
            _arenaPositiveQuirkMatches.Clear();
            _arenaNegativeQuirkMatches.Clear();
            _arenaDiseaseQuirkMatches.Clear();
            _arenaPositiveQuirkSearchApplied = null;
            _arenaNegativeQuirkSearchApplied = null;
            _arenaDiseaseQuirkSearchApplied = null;
            _arenaHeroQuirkCatalogTotalCount = 0;

            try
            {
                if (!SingletonMonoBehaviour<Library<string, QuirkDefinition>>.HasInstance(false))
                {
                    _arenaHeroQuirkCatalogBuilt = false;
                    return false;
                }

                Library<string, QuirkDefinition> library = SingletonMonoBehaviour<Library<string, QuirkDefinition>>.Instance;
                _arenaHeroQuirkCatalogTotalCount = library.GetNumberOfLibraryElements();
                for (int i = 0; i < _arenaHeroQuirkCatalogTotalCount; i++)
                {
                    QuirkDefinition definition = library.GetLibraryElementAtIndex(i);
                    if (definition == null || string.IsNullOrWhiteSpace(definition.m_Id))
                    {
                        continue;
                    }

                    ArenaQuirkKind kind = GetArenaQuirkKind(definition);
                    string displayName = GetArenaQuirkDisplayName(definition);
                    string description = GetArenaQuirkDescription(definition);
                    ArenaQuirkCatalogEntry entry = new ArenaQuirkCatalogEntry
                    {
                        QuirkId = definition.m_Id,
                        DisplayName = displayName,
                        Description = description,
                        Kind = kind,
                        Definition = definition,
                        SearchText = (definition.m_Id + " " + displayName + " " + description + " " + GetArenaQuirkKindLabel(kind)).ToLowerInvariant(),
                    };

                    if (kind == ArenaQuirkKind.Positive)
                    {
                        _arenaPositiveQuirkCatalog.Add(entry);
                    }
                    else if (kind == ArenaQuirkKind.Negative)
                    {
                        _arenaNegativeQuirkCatalog.Add(entry);
                    }
                    else
                    {
                        _arenaDiseaseQuirkCatalog.Add(entry);
                    }
                }

                _arenaPositiveQuirkCatalog.Sort(CompareArenaQuirkCatalogEntries);
                _arenaNegativeQuirkCatalog.Sort(CompareArenaQuirkCatalogEntries);
                _arenaDiseaseQuirkCatalog.Sort(CompareArenaQuirkCatalogEntries);
                _arenaHeroQuirkCatalogBuilt = true;
                RefreshArenaQuirkMatchesIfNeeded(ArenaQuirkKind.Positive, true);
                RefreshArenaQuirkMatchesIfNeeded(ArenaQuirkKind.Negative, true);
                RefreshArenaQuirkMatchesIfNeeded(ArenaQuirkKind.Disease, true);
                HostLog.Write("[arena] Hero quirk catalog built: positive=" + _arenaPositiveQuirkCatalog.Count +
                    ", negative=" + _arenaNegativeQuirkCatalog.Count +
                    ", disease=" + _arenaDiseaseQuirkCatalog.Count +
                    ", total=" + _arenaHeroQuirkCatalogTotalCount + ".");
                return true;
            }
            catch (Exception ex)
            {
                _arenaHeroQuirkCatalogBuilt = false;
                HostLog.Write("[arena] Failed to build hero quirk catalog: " + ex.Message);
                return false;
            }
        }

        private static int CompareArenaQuirkCatalogEntries(ArenaQuirkCatalogEntry left, ArenaQuirkCatalogEntry right)
        {
            return string.Compare(
                left == null ? string.Empty : left.DisplayName,
                right == null ? string.Empty : right.DisplayName,
                StringComparison.CurrentCultureIgnoreCase);
        }

        private void RefreshArenaQuirkMatchesIfNeeded(ArenaQuirkKind kind, bool force)
        {
            List<ArenaQuirkCatalogEntry> source;
            List<ArenaQuirkCatalogEntry> target;
            string query;
            string applied;
            if (kind == ArenaQuirkKind.Positive)
            {
                source = _arenaPositiveQuirkCatalog;
                target = _arenaPositiveQuirkMatches;
                query = (_arenaPositiveQuirkSearch ?? string.Empty).Trim().ToLowerInvariant();
                applied = _arenaPositiveQuirkSearchApplied;
            }
            else if (kind == ArenaQuirkKind.Negative)
            {
                source = _arenaNegativeQuirkCatalog;
                target = _arenaNegativeQuirkMatches;
                query = (_arenaNegativeQuirkSearch ?? string.Empty).Trim().ToLowerInvariant();
                applied = _arenaNegativeQuirkSearchApplied;
            }
            else
            {
                source = _arenaDiseaseQuirkCatalog;
                target = _arenaDiseaseQuirkMatches;
                query = (_arenaDiseaseQuirkSearch ?? string.Empty).Trim().ToLowerInvariant();
                applied = _arenaDiseaseQuirkSearchApplied;
            }

            if (!force && string.Equals(applied, query, StringComparison.Ordinal))
            {
                return;
            }

            target.Clear();
            string[] terms = query
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .ToArray();
            foreach (ArenaQuirkCatalogEntry entry in source)
            {
                if (terms.Length > 0 && !terms.All(term => (entry.SearchText ?? string.Empty).Contains(term)))
                {
                    continue;
                }

                if (target.Count < 48)
                {
                    target.Add(entry);
                }
            }

            if (kind == ArenaQuirkKind.Positive)
            {
                _arenaPositiveQuirkSearchApplied = query;
            }
            else if (kind == ArenaQuirkKind.Negative)
            {
                _arenaNegativeQuirkSearchApplied = query;
            }
            else
            {
                _arenaDiseaseQuirkSearchApplied = query;
            }
        }

        private ArenaQuirkCatalogEntry GetArenaQuirkEntry(string quirkId)
        {
            string id = (quirkId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            EnsureArenaHeroQuirkCatalog();
            return _arenaPositiveQuirkCatalog
                .Concat(_arenaNegativeQuirkCatalog)
                .Concat(_arenaDiseaseQuirkCatalog)
                .FirstOrDefault(entry => entry != null && string.Equals(entry.QuirkId, id, StringComparison.Ordinal));
        }

        private static QuirkDefinition TryGetArenaQuirkDefinition(string quirkId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(quirkId) ||
                    !SingletonMonoBehaviour<Library<string, QuirkDefinition>>.HasInstance(false))
                {
                    return null;
                }

                return SingletonMonoBehaviour<Library<string, QuirkDefinition>>.Instance.GetLibraryElement(quirkId.Trim());
            }
            catch
            {
                return null;
            }
        }

        private static ArenaQuirkKind GetArenaQuirkKind(QuirkDefinition definition)
        {
            if (definition != null && definition.IsPositive)
            {
                return ArenaQuirkKind.Positive;
            }

            if (definition != null && definition.IsNegative)
            {
                return ArenaQuirkKind.Negative;
            }

            return ArenaQuirkKind.Disease;
        }

        private static string GetArenaQuirkDisplayName(QuirkDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.m_Id))
            {
                return "[quirk]";
            }

            try
            {
                string displayName = QuirkDescription.GetNameString(definition, (ActorDataClass)null, true);
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return CleanInline(displayName);
                }
            }
            catch
            {
            }

            return HumanizeArenaInternalId(definition.m_Id);
        }

        private static string GetArenaQuirkDescription(QuirkDefinition definition)
        {
            if (definition == null)
            {
                return string.Empty;
            }

            try
            {
                string description = QuirkDescription.GetDescriptionString(definition, (ActorDataClass)null);
                return string.IsNullOrWhiteSpace(description) ? string.Empty : description;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetArenaQuirkKindLabel(ArenaQuirkKind kind)
        {
            switch (kind)
            {
                case ArenaQuirkKind.Positive:
                    return "positive";
                case ArenaQuirkKind.Negative:
                    return "negative";
                default:
                    return "disease curse";
            }
        }

        private static string HumanizeArenaInternalId(string id)
        {
            string value = (id ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string[] prefixes =
            {
                "item_effect_",
                "combat_start_",
                "battle_advantage_",
                "boss_modifier_",
                "quirk_",
            };
            foreach (string prefix in prefixes)
            {
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    value = value.Substring(prefix.Length);
                    break;
                }
            }

            value = value.Replace("_", " ");
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value);
        }

        private void ToggleArenaHeroDraftTrinket(ArenaHeroDraftSlot slot, string itemId)
        {
            if (slot == null || string.IsNullOrWhiteSpace(itemId))
            {
                return;
            }

            string id = itemId.Trim();
            if (slot.TrinketIds.Contains(id))
            {
                slot.TrinketIds.Remove(id);
                return;
            }

            if (slot.TrinketIds.Count < 2)
            {
                slot.TrinketIds.Add(id);
                return;
            }

            slot.TrinketIds[1] = id;
        }

        private bool EnsureArenaHeroItemCatalog()
        {
            if (_arenaHeroItemCatalogBuilt)
            {
                return true;
            }

            return RebuildArenaHeroItemCatalog();
        }

        private bool RebuildArenaHeroItemCatalog()
        {
            _arenaCombatItemCatalog.Clear();
            _arenaTrinketCatalog.Clear();
            _arenaCombatItemMatches.Clear();
            _arenaTrinketMatches.Clear();
            _arenaHeroCombatItemSearchApplied = null;
            _arenaHeroTrinketSearchApplied = null;
            _arenaHeroItemCatalogTotalCount = 0;

            try
            {
                if (!SingletonMonoBehaviour<Library<string, ItemDefinition>>.HasInstance(false))
                {
                    _arenaHeroItemCatalogBuilt = false;
                    return false;
                }

                Library<string, ItemDefinition> library = SingletonMonoBehaviour<Library<string, ItemDefinition>>.Instance;
                _arenaHeroItemCatalogTotalCount = library.GetNumberOfLibraryElements();
                for (int i = 0; i < _arenaHeroItemCatalogTotalCount; i++)
                {
                    ItemDefinition definition = library.GetLibraryElementAtIndex(i);
                    if (definition == null || string.IsNullOrWhiteSpace(definition.m_id))
                    {
                        continue;
                    }

                    if (definition.m_type != ItemType.COMBAT && definition.m_type != ItemType.TRINKET)
                    {
                        continue;
                    }

                    string displayName = GetLocalizedItemDisplayName(definition.m_id, definition.m_id);
                    string description = GetLocalizedItemDescription(definition.m_id);
                    string previewDescription = BuildArenaItemDefinitionDescription(definition);
                    if (string.IsNullOrWhiteSpace(previewDescription))
                    {
                        previewDescription = description;
                    }
                    ArenaItemCatalogEntry entry = new ArenaItemCatalogEntry
                    {
                        ItemId = definition.m_id,
                        DisplayName = displayName,
                        Description = description,
                        PreviewDescription = previewDescription,
                        Definition = definition,
                        SearchText = (definition.m_id + " " +
                            displayName + " " +
                            description + " " +
                            previewDescription + " " +
                            (definition.m_type == null ? string.Empty : definition.m_type.GetName()) + " " +
                            (definition.SubType == null ? string.Empty : definition.SubType.m_Id) + " " +
                            string.Join(" ", definition.m_tags == null ? Array.Empty<string>() : definition.m_tags.ToArray())).ToLowerInvariant(),
                    };

                    if (definition.m_type == ItemType.COMBAT)
                    {
                        _arenaCombatItemCatalog.Add(entry);
                    }
                    else
                    {
                        _arenaTrinketCatalog.Add(entry);
                    }
                }

                _arenaCombatItemCatalog.Sort(CompareArenaItemCatalogEntries);
                _arenaTrinketCatalog.Sort(CompareArenaItemCatalogEntries);
                _arenaHeroItemCatalogBuilt = true;
                RefreshArenaHeroItemMatchesIfNeeded(true, true);
                RefreshArenaHeroItemMatchesIfNeeded(false, true);
                HostLog.Write("[arena] Hero item catalog built: combat=" + _arenaCombatItemCatalog.Count +
                    ", trinket=" + _arenaTrinketCatalog.Count +
                    ", total=" + _arenaHeroItemCatalogTotalCount + ".");
                return true;
            }
            catch (Exception ex)
            {
                _arenaHeroItemCatalogBuilt = false;
                HostLog.Write("[arena] Failed to build hero item catalog: " + ex.Message);
                return false;
            }
        }

        private static int CompareArenaItemCatalogEntries(ArenaItemCatalogEntry left, ArenaItemCatalogEntry right)
        {
            return string.Compare(
                left == null ? string.Empty : left.DisplayName,
                right == null ? string.Empty : right.DisplayName,
                StringComparison.CurrentCultureIgnoreCase);
        }

        private void RefreshArenaHeroItemMatchesIfNeeded(bool combatItem, bool force)
        {
            List<ArenaItemCatalogEntry> source = combatItem ? _arenaCombatItemCatalog : _arenaTrinketCatalog;
            List<ArenaItemCatalogEntry> target = combatItem ? _arenaCombatItemMatches : _arenaTrinketMatches;
            string query = (combatItem ? _arenaHeroCombatItemSearch : _arenaHeroTrinketSearch ?? string.Empty).Trim().ToLowerInvariant();
            string applied = combatItem ? _arenaHeroCombatItemSearchApplied : _arenaHeroTrinketSearchApplied;
            if (!force && string.Equals(applied, query, StringComparison.Ordinal))
            {
                return;
            }

            target.Clear();
            string[] terms = query
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .ToArray();
            foreach (ArenaItemCatalogEntry entry in source)
            {
                if (terms.Length > 0 && !terms.All(term => (entry.SearchText ?? string.Empty).Contains(term)))
                {
                    continue;
                }

                if (target.Count < 48)
                {
                    target.Add(entry);
                }
            }

            if (combatItem)
            {
                _arenaHeroCombatItemSearchApplied = query;
            }
            else
            {
                _arenaHeroTrinketSearchApplied = query;
            }
        }

        private string BuildArenaHeroDraftItemWriteSummary()
        {
            ArenaHeroDraftSlot[] slots = GetActiveArenaHeroDraftSlots();
            bool nativePartySide = _arenaHeroSetupTeamIndex == 0;
            bool anyCombat = slots.Any(slot => slot != null && !string.IsNullOrWhiteSpace(slot.CombatItemId));
            bool allCombat = slots.All(slot => slot != null && !string.IsNullOrWhiteSpace(slot.CombatItemId));
            int trinketCount = slots.Sum(slot => slot == null ? 0 : slot.TrinketIds.Count);
            bool allTrinkets = slots.All(slot => slot != null && slot.TrinketIds.Count == 2);

            List<string> parts = new List<string>();
            if (nativePartySide)
            {
                parts.Add(!anyCombat ? "combat item prefs: none" : (allCombat ? "combat item prefs: will write" : "combat item prefs: fill all 4 slots to write"));
                parts.Add(trinketCount == 0 ? "trinket prefs: none" : (allTrinkets ? "trinket prefs: will write" : "trinket prefs: fill 2 trinkets on every slot to write"));
            }
            else
            {
                parts.Add(!anyCombat ? "combat item: none" : "combat item: " + slots.Count(slot => slot != null && !string.IsNullOrWhiteSpace(slot.CombatItemId)));
                parts.Add(trinketCount == 0 ? "trinkets: none" : "trinkets: " + trinketCount + " selected");
                parts.Add("enemy items: applied after combat entry");
            }

            parts.Add(_arenaHeroStartEffectIds.Count == 0
                ? "start effects: none"
                : "start effects: " + _arenaHeroStartEffectIds.Count);
            parts.Add(string.IsNullOrWhiteSpace(_arenaBossModifierId)
                ? "ordainment: none"
                : "ordainment: " + _arenaBossModifierId.Trim());
            return string.Join(" | ", parts.ToArray());
        }

        private string BuildArenaHeroDraftQuirkSummary()
        {
            return BuildArenaHeroDraftQuirkSummary(GetActiveArenaHeroDraftSlots());
        }

        private string BuildArenaHeroDraftQuirkSummary(ArenaHeroDraftSlot[] slots)
        {
            int positive = slots.Sum(slot => slot == null ? 0 : slot.PositiveQuirkIds.Count);
            int negative = slots.Sum(slot => slot == null ? 0 : slot.NegativeQuirkIds.Count);
            int disease = slots.Count(slot => slot != null && !string.IsNullOrWhiteSpace(slot.DiseaseQuirkId));
            return "quirks: +" + positive + " / -" + negative + " / disease=" + disease +
                (positive + negative + disease == 0
                    ? " (" + Ui("none selected", "未选择") + ")"
                    : " (" + Ui("applied after combat entry", "战斗进入后应用") + ")");
        }

        private bool HasArenaHeroDraftAnyQuirks()
        {
            return HasArenaHeroDraftAnyQuirks(_arenaHeroDraftSlots);
        }

        private static bool HasArenaHeroDraftAnyQuirks(ArenaHeroDraftSlot[] slots)
        {
            return slots != null && slots.Any(slot => slot != null && GetArenaDraftQuirkIds(slot).Any());
        }

        private bool TryValidateArenaHeroDraftQuirks(out string error)
        {
            return TryValidateArenaHeroDraftQuirks(_arenaHeroDraftSlots, out error);
        }

        private bool TryValidateArenaHeroDraftQuirks(ArenaHeroDraftSlot[] slots, out string error)
        {
            error = string.Empty;
            for (int i = 0; i < slots.Length; i++)
            {
                ArenaHeroDraftSlot slot = slots[i];
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string quirkId in GetArenaDraftQuirkIds(slot))
                {
                    if (!seen.Add(quirkId))
                    {
                        error = "slot " + (i + 1) + " has duplicate quirk: " + quirkId + ".";
                        return false;
                    }

                    QuirkDefinition definition = TryGetArenaQuirkDefinition(quirkId);
                    if (definition == null)
                    {
                        error = "slot " + (i + 1) + " quirk not found: " + quirkId + ".";
                        return false;
                    }
                }
            }

            return true;
        }

        private void EnsureArenaHeroDraftInitialized()
        {
            EnsureArenaHeroCatalog();
            ArenaHeroDraftSlot[] activeSlots = GetActiveArenaHeroDraftSlots();
            if (_arenaHeroDraftSelectedSlot < 0 || _arenaHeroDraftSelectedSlot >= activeSlots.Length)
            {
                _arenaHeroDraftSelectedSlot = 0;
            }

            if (!_arenaHeroDraftInitialized)
            {
                ImportArenaHeroDraftFromCurrentParty(_arenaHeroDraftSlots, false);
            }
        }

        private bool HasArenaHeroDraftAnyActor()
        {
            return HasArenaHeroDraftAnyActor(_arenaHeroDraftSlots);
        }

        private static bool HasArenaHeroDraftAnyActor(ArenaHeroDraftSlot[] slots)
        {
            return slots != null && slots.Any(slot => slot != null && !string.IsNullOrWhiteSpace(slot.ActorId));
        }

        private ArenaHeroDraftSlot GetSelectedArenaHeroDraftSlot()
        {
            ArenaHeroDraftSlot[] slots = GetActiveArenaHeroDraftSlots();
            if (_arenaHeroDraftSelectedSlot < 0 || _arenaHeroDraftSelectedSlot >= slots.Length)
            {
                _arenaHeroDraftSelectedSlot = 0;
            }

            return slots[_arenaHeroDraftSelectedSlot];
        }

        private void ImportArenaHeroDraftFromCurrentParty(bool log)
        {
            ImportArenaHeroDraftFromCurrentParty(GetActiveArenaHeroDraftSlots(), log);
        }

        private void ImportArenaHeroDraftFromCurrentParty(ArenaHeroDraftSlot[] slots, bool log)
        {
            IList<ActorInstance> party = GetArenaCurrentPartyActors();
            for (int i = 0; i < slots.Length; i++)
            {
                ArenaHeroDraftSlot slot = slots[i];
                ClearArenaHeroDraftSlot(slot);
                if (i >= party.Count || party[i] == null)
                {
                    continue;
                }

                ActorInstance actor = party[i];
                slot.ActorId = (actor.ActorDataId ?? string.Empty).Trim();
                slot.PathId = actor.ActorDataPath == null ? string.Empty : (actor.ActorDataPath.Id ?? string.Empty).Trim();
                slot.SkillIds.AddRange(GetArenaEquippedSkillIds(actor).Take(5));
                slot.CombatItemId = GetArenaFirstInventoryItemId(actor.GetCombatSkillInventory(), ItemType.COMBAT);
                slot.TrinketIds.AddRange(GetArenaInventoryItemIds(actor.GetTrinketInventory(), ItemType.TRINKET).Take(2));
                ImportArenaHeroDraftQuirks(slot, actor);
                NormalizeArenaHeroDraftSkills(slot, false);
            }

            if (log)
            {
                HostLog.Write("[arena] Imported " + GetArenaDraftTeamLabel(_arenaHeroSetupTeamIndex) + " draft from current party: " + party.Count + " actor(s).");
            }

            _arenaHeroDraftInitialized = true;
        }

        private static void ImportArenaHeroDraftQuirks(ArenaHeroDraftSlot slot, ActorInstance actor)
        {
            if (slot == null || actor == null || !actor.HasEnabledQuirkContainer || actor.QuirkContainer == null)
            {
                return;
            }

            try
            {
                foreach (QuirkInstance instance in actor.QuirkContainer.GetInstances())
                {
                    QuirkDefinition definition = instance == null ? null : instance.Definition;
                    if (definition == null || string.IsNullOrWhiteSpace(definition.m_Id))
                    {
                        continue;
                    }

                    string id = definition.m_Id.Trim();
                    if ((definition.IsDisease || definition.IsCurse) && string.IsNullOrWhiteSpace(slot.DiseaseQuirkId))
                    {
                        slot.DiseaseQuirkId = id;
                    }
                    else if (definition.IsNegative && slot.NegativeQuirkIds.Count < 3 && !slot.NegativeQuirkIds.Contains(id))
                    {
                        slot.NegativeQuirkIds.Add(id);
                    }
                    else if (definition.IsPositive && slot.PositiveQuirkIds.Count < 3 && !slot.PositiveQuirkIds.Contains(id))
                    {
                        slot.PositiveQuirkIds.Add(id);
                    }
                }
            }
            catch
            {
            }
        }

        private void ClearArenaHeroDraft()
        {
            foreach (ArenaHeroDraftSlot slot in GetActiveArenaHeroDraftSlots())
            {
                ClearArenaHeroDraftSlot(slot);
            }

            _arenaHeroDraftSelectedSlot = 0;
            _arenaHeroDraftInitialized = true;
            HostLog.Write("[arena] Cleared " + GetArenaDraftTeamLabel(_arenaHeroSetupTeamIndex) + " draft.");
        }

        private static void CopyArenaHeroDraftSlots(ArenaHeroDraftSlot[] source, ArenaHeroDraftSlot[] destination)
        {
            if (source == null || destination == null)
            {
                return;
            }

            int count = Math.Min(source.Length, destination.Length);
            for (int i = 0; i < count; i++)
            {
                CopyArenaHeroDraftSlot(source[i], destination[i]);
            }

            for (int i = count; i < destination.Length; i++)
            {
                ClearArenaHeroDraftSlot(destination[i]);
            }
        }

        private static void CopyArenaHeroDraftSlot(ArenaHeroDraftSlot source, ArenaHeroDraftSlot destination)
        {
            if (destination == null)
            {
                return;
            }

            ClearArenaHeroDraftSlot(destination);
            if (source == null)
            {
                return;
            }

            destination.ActorId = source.ActorId ?? string.Empty;
            destination.PathId = source.PathId ?? string.Empty;
            destination.CombatItemId = source.CombatItemId ?? string.Empty;
            destination.DiseaseQuirkId = source.DiseaseQuirkId ?? string.Empty;
            destination.SkillIds.AddRange(source.SkillIds);
            destination.TrinketIds.AddRange(source.TrinketIds);
            destination.PositiveQuirkIds.AddRange(source.PositiveQuirkIds);
            destination.NegativeQuirkIds.AddRange(source.NegativeQuirkIds);
        }

        private static void ClearArenaHeroDraftSlot(ArenaHeroDraftSlot slot)
        {
            if (slot == null)
            {
                return;
            }

            slot.ActorId = string.Empty;
            slot.PathId = string.Empty;
            slot.CombatItemId = string.Empty;
            slot.DiseaseQuirkId = string.Empty;
            slot.SkillIds.Clear();
            slot.TrinketIds.Clear();
            slot.PositiveQuirkIds.Clear();
            slot.NegativeQuirkIds.Clear();
        }

        private void SetArenaHeroDraftActor(int slotIndex, string actorId)
        {
            ArenaHeroDraftSlot[] slots = GetActiveArenaHeroDraftSlots();
            if (slotIndex < 0 || slotIndex >= slots.Length)
            {
                return;
            }

            ArenaHeroDraftSlot slot = slots[slotIndex];
            string nextActorId = (actorId ?? string.Empty).Trim();
            _arenaHeroDraftInitialized = true;
            bool actorChanged = !string.Equals(slot.ActorId, nextActorId, StringComparison.Ordinal);
            slot.ActorId = nextActorId;

            if (string.IsNullOrWhiteSpace(nextActorId))
            {
                slot.PathId = string.Empty;
                slot.CombatItemId = string.Empty;
                slot.SkillIds.Clear();
                slot.TrinketIds.Clear();
                return;
            }

            if (actorChanged || string.IsNullOrWhiteSpace(slot.PathId) || !GetArenaDraftAvailablePaths(nextActorId).Any(path => string.Equals(path.Id, slot.PathId, StringComparison.Ordinal)))
            {
                slot.PathId = GetArenaDefaultPathId(nextActorId);
            }

            if (actorChanged)
            {
                slot.SkillIds.Clear();
            }

            NormalizeArenaHeroDraftSkills(slot, true);
            _arenaHeroDetailScroll = Vector2.zero;
        }

        private void SetArenaHeroDraftPath(int slotIndex, string pathId)
        {
            ArenaHeroDraftSlot[] slots = GetActiveArenaHeroDraftSlots();
            if (slotIndex < 0 || slotIndex >= slots.Length)
            {
                return;
            }

            ArenaHeroDraftSlot slot = slots[slotIndex];
            if (string.IsNullOrWhiteSpace(slot.ActorId))
            {
                return;
            }

            ActorDataPath oldPath = TryGetArenaActorDataPath(slot.PathId);
            ActorDataPath newPath = TryGetArenaActorDataPath(pathId);
            if (newPath == null)
            {
                return;
            }

            _arenaHeroDraftInitialized = true;
            List<string> baseSkillIds = slot.SkillIds
                .Select(skillId =>
                {
                    bool upgraded = IsArenaSkillUpgrade(skillId);
                    string rawSkillId = StripArenaSkillUpgradeSuffix(skillId);
                    string baseSkillId = GetArenaBaseSkillIdForPathSkill(rawSkillId, oldPath);
                    return upgraded ? baseSkillId + "_u" : baseSkillId;
                })
                .Where(skillId => !string.IsNullOrWhiteSpace(skillId))
                .ToList();
            slot.PathId = newPath.Id;
            slot.SkillIds.Clear();
            foreach (string baseSkillId in baseSkillIds)
            {
                bool upgraded = IsArenaSkillUpgrade(baseSkillId);
                string mapped = ApplyArenaSkillReplacementForPath(StripArenaSkillUpgradeSuffix(baseSkillId), newPath);
                if (upgraded && TryGetArenaSkillData(mapped + "_u") != null)
                {
                    mapped += "_u";
                }

                if (!string.IsNullOrWhiteSpace(mapped) && !slot.SkillIds.Contains(mapped))
                {
                    slot.SkillIds.Add(mapped);
                }
            }

            NormalizeArenaHeroDraftSkills(slot, true);
        }

        private void ResetArenaHeroDraftSkills(int slotIndex)
        {
            ArenaHeroDraftSlot[] slots = GetActiveArenaHeroDraftSlots();
            if (slotIndex < 0 || slotIndex >= slots.Length)
            {
                return;
            }

            ArenaHeroDraftSlot slot = slots[slotIndex];
            _arenaHeroDraftInitialized = true;
            slot.SkillIds.Clear();
            NormalizeArenaHeroDraftSkills(slot, true);
        }

        private void ToggleArenaHeroDraftSkill(ArenaHeroDraftSlot slot, string skillId)
        {
            if (slot == null || string.IsNullOrWhiteSpace(skillId))
            {
                return;
            }

            int existingIndex = FindArenaHeroDraftSkillIndex(slot, skillId);
            if (existingIndex >= 0)
            {
                slot.SkillIds.RemoveAt(existingIndex);
                _arenaHeroDraftInitialized = true;
                return;
            }

            if (slot.SkillIds.Count >= 5)
            {
                slot.SkillIds.RemoveAt(slot.SkillIds.Count - 1);
            }

            slot.SkillIds.Add(skillId);
            _arenaHeroDraftInitialized = true;
        }

        private static int FindArenaHeroDraftSkillIndex(ArenaHeroDraftSlot slot, string skillId)
        {
            if (slot == null || string.IsNullOrWhiteSpace(skillId))
            {
                return -1;
            }

            string baseSkillId = StripArenaSkillUpgradeSuffix(skillId);
            for (int i = 0; i < slot.SkillIds.Count; i++)
            {
                string selectedBaseSkillId = StripArenaSkillUpgradeSuffix(slot.SkillIds[i]);
                if (string.Equals(selectedBaseSkillId, baseSkillId, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private void NormalizeArenaHeroDraftSkills(ArenaHeroDraftSlot slot, bool fillMissing)
        {
            if (slot == null || string.IsNullOrWhiteSpace(slot.ActorId))
            {
                return;
            }

            List<string> available = GetArenaDraftAvailableSkillIds(slot.ActorId, slot.PathId);
            HashSet<string> availableSet = new HashSet<string>(available, StringComparer.Ordinal);
            List<string> normalized = new List<string>();
            HashSet<string> normalizedBaseSkillIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string skillId in slot.SkillIds)
            {
                string id = (skillId ?? string.Empty).Trim();
                string baseSkillId = StripArenaSkillUpgradeSuffix(id);
                if (string.IsNullOrWhiteSpace(id) ||
                    string.IsNullOrWhiteSpace(baseSkillId) ||
                    normalizedBaseSkillIds.Contains(baseSkillId) ||
                    (availableSet.Count > 0 && !IsArenaDraftSkillAllowed(id, availableSet)))
                {
                    continue;
                }

                normalized.Add(id);
                normalizedBaseSkillIds.Add(baseSkillId);
                if (normalized.Count >= 5)
                {
                    break;
                }
            }

            if (fillMissing)
            {
                foreach (string skillId in available)
                {
                    if (normalized.Count >= 5)
                    {
                        break;
                    }

                    string baseSkillId = StripArenaSkillUpgradeSuffix(skillId);
                    if (!normalizedBaseSkillIds.Contains(baseSkillId))
                    {
                        normalized.Add(skillId);
                        normalizedBaseSkillIds.Add(baseSkillId);
                    }
                }
            }

            slot.SkillIds.Clear();
            slot.SkillIds.AddRange(normalized.Take(5));
        }

        private bool EnsureArenaHeroCatalog()
        {
            if (_arenaHeroCatalogBuilt)
            {
                return true;
            }

            return RebuildArenaHeroCatalog();
        }

        private bool RebuildArenaHeroCatalog()
        {
            _arenaHeroCatalog.Clear();
            _arenaHeroCatalogMatches.Clear();
            _arenaHeroCatalogSearchApplied = null;
            _arenaHeroCatalogTotalCount = 0;

            try
            {
                if (!SingletonMonoBehaviour<Library<string, ActorDataClass>>.HasInstance(false))
                {
                    _arenaHeroCatalogBuilt = false;
                    return false;
                }

                Library<string, ActorDataClass> library = SingletonMonoBehaviour<Library<string, ActorDataClass>>.Instance;
                _arenaHeroCatalogTotalCount = library.GetNumberOfLibraryElements();
                for (int i = 0; i < _arenaHeroCatalogTotalCount; i++)
                {
                    ActorDataClass actorClass = library.GetLibraryElementAtIndex(i);
                    if (!IsArenaHeroClassCandidate(actorClass))
                    {
                        continue;
                    }

                    string actorId = actorClass.Id;
                    string displayName = GetArenaActorClassDisplayName(actorId);
                    string pathText = string.Join(" ", GetArenaDraftAvailablePaths(actorId)
                        .Select(path => path == null ? string.Empty : path.Id + " " + GetArenaDraftPathDisplayName(path.Id, actorId))
                        .ToArray());
                    _arenaHeroCatalog.Add(new ArenaHeroCatalogEntry
                    {
                        ActorId = actorId,
                        DisplayName = displayName,
                        SearchText = (actorId + " " + displayName + " " + pathText).ToLowerInvariant(),
                    });
                }

                _arenaHeroCatalog.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase));
                _arenaHeroCatalogBuilt = true;
                RefreshArenaHeroCatalogMatchesIfNeeded(true);
                HostLog.Write("[arena] Hero catalog built: " + _arenaHeroCatalog.Count + "/" + _arenaHeroCatalogTotalCount + ".");
                return true;
            }
            catch (Exception ex)
            {
                _arenaHeroCatalogBuilt = false;
                HostLog.Write("[arena] Failed to build hero catalog: " + ex.Message);
                return false;
            }
        }

        private static bool IsArenaHeroClassCandidate(ActorDataClass actorClass)
        {
            if (actorClass == null || string.IsNullOrWhiteSpace(actorClass.Id))
            {
                return false;
            }

            if (IsArenaExcludedActorId(actorClass.Id))
            {
                return false;
            }

            if (actorClass.m_Size != 1)
            {
                return false;
            }

            if (!actorClass.IsPopulateInRoster && !actorClass.IsInHeroSelect && !actorClass.IsHireClass)
            {
                return false;
            }

            return actorClass.DefualtActorDataPath != null || actorClass.ReserveActorDataPath != null;
        }

        private void RefreshArenaHeroCatalogMatchesIfNeeded(bool force)
        {
            string query = (_arenaHeroDraftSearch ?? string.Empty).Trim().ToLowerInvariant();
            if (!force && string.Equals(_arenaHeroCatalogSearchApplied, query, StringComparison.Ordinal))
            {
                return;
            }

            _arenaHeroCatalogMatches.Clear();
            string[] terms = query
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .ToArray();
            foreach (ArenaHeroCatalogEntry entry in _arenaHeroCatalog)
            {
                if (terms.Length > 0 && !terms.All(term => (entry.SearchText ?? string.Empty).Contains(term)))
                {
                    continue;
                }

                if (_arenaHeroCatalogMatches.Count < 80)
                {
                    _arenaHeroCatalogMatches.Add(entry);
                }
            }

            _arenaHeroCatalogSearchApplied = query;
        }

        private static ActorDataPath TryGetArenaActorDataPath(string pathId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pathId) ||
                    !SingletonMonoBehaviour<Library<string, ActorDataPath>>.HasInstance(false))
                {
                    return null;
                }

                return SingletonMonoBehaviour<Library<string, ActorDataPath>>.Instance.GetLibraryElement(pathId.Trim());
            }
            catch
            {
                return null;
            }
        }

        private static string GetArenaDefaultPathId(string actorId)
        {
            ActorDataClass actorClass = TryGetArenaActorDataClass(actorId);
            ActorDataPath path = actorClass == null ? null : actorClass.DefualtActorDataPath ?? actorClass.ReserveActorDataPath;
            return path == null ? string.Empty : path.Id;
        }

        private static List<ActorDataPath> GetArenaDraftAvailablePaths(string actorId)
        {
            List<ActorDataPath> paths = new List<ActorDataPath>();
            ActorDataClass actorClass = TryGetArenaActorDataClass(actorId);
            if (actorClass == null ||
                !SingletonMonoBehaviour<Library<string, ActorDataPath>>.HasInstance(false))
            {
                return paths;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (actorClass.DefualtActorDataPath != null && seen.Add(actorClass.DefualtActorDataPath.Id))
            {
                paths.Add(actorClass.DefualtActorDataPath);
            }

            if (actorClass.ReserveActorDataPath != null && seen.Add(actorClass.ReserveActorDataPath.Id))
            {
                paths.Add(actorClass.ReserveActorDataPath);
            }

            Library<string, ActorDataPath> library = SingletonMonoBehaviour<Library<string, ActorDataPath>>.Instance;
            int count = library.GetNumberOfLibraryElements();
            for (int i = 0; i < count; i++)
            {
                ActorDataPath path = library.GetLibraryElementAtIndex(i);
                if (path == null || string.IsNullOrWhiteSpace(path.Id) || seen.Contains(path.Id))
                {
                    continue;
                }

                bool valid = false;
                try
                {
                    valid = path.GetIsValidForActorDataClass(actorClass);
                }
                catch
                {
                    valid = false;
                }

                if (valid && seen.Add(path.Id))
                {
                    paths.Add(path);
                }
            }

            paths.Sort((left, right) =>
            {
                bool leftDefault = actorClass.DefualtActorDataPath != null && string.Equals(left.Id, actorClass.DefualtActorDataPath.Id, StringComparison.Ordinal);
                bool rightDefault = actorClass.DefualtActorDataPath != null && string.Equals(right.Id, actorClass.DefualtActorDataPath.Id, StringComparison.Ordinal);
                if (leftDefault != rightDefault)
                {
                    return leftDefault ? -1 : 1;
                }

                int order = left.m_OrderPriority.CompareTo(right.m_OrderPriority);
                return order != 0 ? order : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
            });
            return paths;
        }

        private static string GetArenaDraftPathDisplayName(string pathId, string actorId)
        {
            ActorDataPath path = TryGetArenaActorDataPath(pathId);
            if (path == null)
            {
                return string.IsNullOrWhiteSpace(pathId) ? "[path]" : pathId;
            }

            ActorDataClass actorClass = TryGetArenaActorDataClass(actorId);
            try
            {
                string gender = actorClass == null ? string.Empty : actorClass.m_LocalizationGender;
                string displayName = ActorPathDescription.GetNameString(path, gender, false);
                return string.IsNullOrWhiteSpace(displayName) ? path.Id : CleanInline(displayName);
            }
            catch
            {
                return path.Id ?? "[path]";
            }
        }

        private static string GetArenaDraftPathDescription(ActorDataPath path, string actorId)
        {
            if (path == null)
            {
                return string.Empty;
            }

            try
            {
                string text = ActorPathDescription.GetDescriptionString(path, null, true, true);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            catch
            {
            }

            ActorDataClass actorClass = TryGetArenaActorDataClass(actorId);
            return "Id: " + (path.Id ?? "[path]") +
                (actorClass == null ? string.Empty : "\nActor: " + GetArenaActorClassDisplayName(actorId));
        }

        private List<string> GetArenaDraftAvailableSkillIds(string actorId, string pathId)
        {
            string actorKey = (actorId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(actorKey))
            {
                return new List<string>();
            }

            string cacheKey = actorKey + "|" + (pathId ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                List<string> cached;
                if (_arenaAvailableSkillIdsByActorPath.TryGetValue(cacheKey, out cached))
                {
                    return new List<string>(cached);
                }
            }

            List<string> available = new List<string>();
            ActorDataPath path = TryGetArenaActorDataPath(pathId);
            foreach (string baseSkillId in GetArenaBaseSkillIdsForActor(actorKey))
            {
                string id = ApplyArenaSkillReplacementForPath(baseSkillId, path);
                if (string.IsNullOrWhiteSpace(id) || available.Contains(id))
                {
                    continue;
                }

                ActorDataSkill skill = TryGetArenaSkillData(id);
                if (skill == null || skill.IsMoveSkill || skill.IsPassSkill || skill.IsItemSkill || skill.IsTokenSkill)
                {
                    continue;
                }

                available.Add(id);
            }

            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                if (_arenaAvailableSkillIdsByActorPath.Count > 128)
                {
                    _arenaAvailableSkillIdsByActorPath.Clear();
                }

                _arenaAvailableSkillIdsByActorPath[cacheKey] = new List<string>(available);
            }

            return available;
        }

        private List<string> GetArenaBaseSkillIdsForActor(string actorId)
        {
            string key = (actorId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return new List<string>();
            }

            List<string> cached;
            if (_arenaBaseSkillIdsByActorId.TryGetValue(key, out cached))
            {
                return new List<string>(cached);
            }

            List<string> skills = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                if (Singleton<ResourceDatabaseActors>.HasInstance())
                {
                    ResourceActor resource = Singleton<ResourceDatabaseActors>.Instance.GetResource(key, true, null);
                    AddArenaResourceSkills(skills, seen, resource == null ? null : resource.m_StartingCombatSkills);
                    AddArenaResourceSkills(skills, seen, resource == null ? null : resource.m_AdditionalCombatSkills);
                }
            }
            catch
            {
            }

            if (skills.Count == 0)
            {
                ActorDataClass actorClass = TryGetArenaActorDataClass(key);
                if (actorClass != null && actorClass.SkillSets != null)
                {
                    foreach (SkillSetDefinition set in actorClass.SkillSets)
                    {
                        if (set == null || set.Skills == null)
                        {
                            continue;
                        }

                        foreach (ActorDataSkill skill in set.Skills)
                        {
                            AddArenaSkillId(skills, seen, skill == null ? null : skill.Id);
                        }
                    }
                }
            }

            _arenaBaseSkillIdsByActorId[key] = new List<string>(skills);
            return skills;
        }

        private static void AddArenaResourceSkills(List<string> skills, HashSet<string> seen, IReadOnlyList<ResourceSkillBase> resources)
        {
            if (skills == null || seen == null || resources == null)
            {
                return;
            }

            foreach (ResourceSkillBase resource in resources)
            {
                AddArenaSkillId(skills, seen, resource == null ? null : resource.GetSkillId());
            }
        }

        private static void AddArenaSkillId(List<string> skills, HashSet<string> seen, string skillId)
        {
            if (skills == null || seen == null || string.IsNullOrWhiteSpace(skillId))
            {
                return;
            }

            string id = skillId.Trim();
            ActorDataSkill skill = TryGetArenaSkillData(id);
            if (skill == null || skill.IsMoveSkill || skill.IsPassSkill || skill.IsItemSkill || skill.IsTokenSkill)
            {
                return;
            }

            if (seen.Add(id))
            {
                skills.Add(id);
            }
        }

        private static string ApplyArenaSkillReplacementForPath(string baseSkillId, ActorDataPath path)
        {
            string id = (baseSkillId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id) || path == null)
            {
                return id;
            }

            try
            {
                ActorDataSkillReplacement replacement = path.GetDataContainerSum<ActorDataSkillReplacement>(null, null);
                if (replacement == null || replacement.SourceSkillReplacements == null)
                {
                    return id;
                }

                foreach (SourceDefinition<SkillReplacementDefinition> source in replacement.SourceSkillReplacements)
                {
                    SkillReplacementDefinition definition = source == null ? null : source.Definition;
                    if (definition != null && string.Equals(definition.m_FromActorDataSkillId, id, StringComparison.Ordinal))
                    {
                        return string.IsNullOrWhiteSpace(definition.m_ToActorDataSkillId) ? id : definition.m_ToActorDataSkillId;
                    }
                }
            }
            catch
            {
            }

            return id;
        }

        private static string GetArenaBaseSkillIdForPathSkill(string skillId, ActorDataPath path)
        {
            string id = (skillId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id) || path == null)
            {
                return id;
            }

            try
            {
                ActorDataSkillReplacement replacement = path.GetDataContainerSum<ActorDataSkillReplacement>(null, null);
                if (replacement == null || replacement.SourceSkillReplacements == null)
                {
                    return id;
                }

                foreach (SourceDefinition<SkillReplacementDefinition> source in replacement.SourceSkillReplacements)
                {
                    SkillReplacementDefinition definition = source == null ? null : source.Definition;
                    if (definition != null && string.Equals(definition.m_ToActorDataSkillId, id, StringComparison.Ordinal))
                    {
                        return string.IsNullOrWhiteSpace(definition.m_FromActorDataSkillId) ? id : definition.m_FromActorDataSkillId;
                    }
                }
            }
            catch
            {
            }

            return id;
        }

        private static bool IsArenaDraftSkillAllowed(string skillId, HashSet<string> availableSkillIds)
        {
            if (string.IsNullOrWhiteSpace(skillId) || availableSkillIds == null || availableSkillIds.Count == 0)
            {
                return false;
            }

            string id = skillId.Trim();
            if (availableSkillIds.Contains(id))
            {
                return true;
            }

            if (!IsArenaSkillUpgrade(id))
            {
                return false;
            }

            return availableSkillIds.Contains(StripArenaSkillUpgradeSuffix(id)) && TryGetArenaSkillData(id) != null;
        }

        private static bool IsArenaSkillUpgrade(string skillId)
        {
            return !string.IsNullOrWhiteSpace(skillId) &&
                skillId.EndsWith("_u", StringComparison.Ordinal);
        }

        private static string GetArenaSkillUpgradeId(string skillId)
        {
            string baseSkillId = StripArenaSkillUpgradeSuffix(skillId);
            if (string.IsNullOrWhiteSpace(baseSkillId))
            {
                return string.Empty;
            }

            try
            {
                UnlockDefinition unlock = SkillUtils.GetUnlockFromSkillId(baseSkillId);
                if (unlock != null &&
                    !string.IsNullOrWhiteSpace(unlock.m_Id) &&
                    TryGetArenaSkillData(unlock.m_Id) != null)
                {
                    return unlock.m_Id;
                }
            }
            catch
            {
            }

            string conventionalUpgradeId = baseSkillId + "_u";
            return TryGetArenaSkillData(conventionalUpgradeId) == null ? string.Empty : conventionalUpgradeId;
        }

        private static string GetArenaSkillRootId(string skillId)
        {
            string id = (skillId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return string.Empty;
            }

            try
            {
                if (SingletonMonoBehaviour<Library<string, UnlockDefinition>>.HasInstance(false))
                {
                    UnlockDefinition unlock = SingletonMonoBehaviour<Library<string, UnlockDefinition>>.Instance.GetLibraryElement(id);
                    if (unlock != null && unlock.RequirementIds != null && unlock.RequirementIds.Count > 0)
                    {
                        string requirementId = unlock.RequirementIds.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(requirementId))
                        {
                            return StripArenaSkillUpgradeSuffix(requirementId);
                        }
                    }
                }
            }
            catch
            {
            }

            return StripArenaSkillUpgradeSuffix(id);
        }

        private static string StripArenaSkillUpgradeSuffix(string skillId)
        {
            string id = (skillId ?? string.Empty).Trim();
            return IsArenaSkillUpgrade(id) ? id.Substring(0, id.Length - 2) : id;
        }

        private string BuildArenaHeroDraftSlotTooltip(ArenaHeroDraftSlot slot)
        {
            if (slot == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>
            {
                "Actor: " + (slot.ActorId ?? "[actor]"),
                "Path: " + (slot.PathId ?? "[path]"),
            };
            if (slot.SkillIds.Count > 0)
            {
                lines.Add("Skills:");
                foreach (string skillId in slot.SkillIds)
                {
                    lines.Add("- " + GetArenaSkillDisplayName(skillId) + " (" + skillId + ")");
                }
            }

            return string.Join("\n", lines.ToArray());
        }

        private string GetArenaHeroDraftValidationSummary()
        {
            if (!TryGetArenaHeroDraftForLaunch(out _, out _, out _, out string error))
            {
                return string.IsNullOrWhiteSpace(error) ? "party draft incomplete" : "party: " + error;
            }

            if (!TryValidateArenaHeroDraftQuirks(out error))
            {
                return string.IsNullOrWhiteSpace(error) ? "party quirk draft invalid" : "party: " + error;
            }

            bool hasEnemyDraft = HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots);
            if (hasEnemyDraft)
            {
                if (!TryGetArenaEnemyHeroDraftForLaunch(out _, out _, out _, out error))
                {
                    return string.IsNullOrWhiteSpace(error) ? "enemy draft incomplete" : "enemy: " + error;
                }

                if (!TryValidateArenaHeroDraftQuirks(_arenaEnemyHeroDraftSlots, out error))
                {
                    return string.IsNullOrWhiteSpace(error) ? "enemy quirk draft invalid" : "enemy: " + error;
                }

                if (!TryValidateArenaDraftItems(_arenaEnemyHeroDraftSlots, out error))
                {
                    return string.IsNullOrWhiteSpace(error) ? "enemy item draft invalid" : "enemy: " + error;
                }
            }

            List<string> parts = new List<string> { "draft ready" };
            if (hasEnemyDraft)
            {
                parts.Add("enemy=custom");
            }

            if (_arenaHeroStartEffectIds.Count > 0)
            {
                parts.Add("buffs=" + _arenaHeroStartEffectIds.Count);
            }

            int quirkCount = _arenaHeroDraftSlots.Sum(slot => slot == null ? 0 : GetArenaDraftQuirkIds(slot).Count()) +
                _arenaEnemyHeroDraftSlots.Sum(slot => slot == null ? 0 : GetArenaDraftQuirkIds(slot).Count());
            if (quirkCount > 0)
            {
                parts.Add("quirks=" + quirkCount);
            }

            if (!string.IsNullOrWhiteSpace(_arenaBossModifierId))
            {
                parts.Add("ordain");
            }

            return string.Join(" | ", parts.ToArray());
        }

        private bool TryGetArenaHeroDraftForLaunch(
            out List<string> heroIds,
            out List<string> pathIds,
            out List<string> skillIds,
            out string error)
        {
            return TryGetArenaDraftForLaunch(_arenaHeroDraftSlots, "party", out heroIds, out pathIds, out skillIds, out error);
        }

        private bool TryGetArenaEnemyHeroDraftForLaunch(
            out List<string> heroIds,
            out List<string> pathIds,
            out List<string> skillIds,
            out string error)
        {
            return TryGetArenaDraftForLaunch(_arenaEnemyHeroDraftSlots, "enemy", out heroIds, out pathIds, out skillIds, out error);
        }

        private bool TryGetArenaDraftForLaunch(
            ArenaHeroDraftSlot[] slots,
            string draftLabel,
            out List<string> heroIds,
            out List<string> pathIds,
            out List<string> skillIds,
            out string error)
        {
            heroIds = new List<string>();
            pathIds = new List<string>();
            skillIds = new List<string>();
            error = string.Empty;

            if (!HasArenaHeroDraftAnyActor(slots))
            {
                error = "no " + draftLabel + " draft";
                return false;
            }

            bool sawEmptySlot = false;
            for (int i = 0; i < slots.Length; i++)
            {
                ArenaHeroDraftSlot slot = slots[i];
                if (slot == null || string.IsNullOrWhiteSpace(slot.ActorId))
                {
                    sawEmptySlot = true;
                    continue;
                }

                if (sawEmptySlot)
                {
                    error = "slot " + (i + 1) + " is configured after an empty slot; fill draft slots from the front.";
                    return false;
                }

                string actorId = slot.ActorId.Trim();
                ActorDataClass actorClass = TryGetArenaActorDataClass(actorId);
                if (actorClass == null)
                {
                    error = "slot " + (i + 1) + " actor not found: " + actorId + ".";
                    return false;
                }

                string pathId = string.IsNullOrWhiteSpace(slot.PathId) ? GetArenaDefaultPathId(actorId) : slot.PathId.Trim();
                ActorDataPath path = TryGetArenaActorDataPath(pathId);
                if (path == null)
                {
                    error = "slot " + (i + 1) + " path not found: " + pathId + ".";
                    return false;
                }

                try
                {
                    if (!path.GetIsValidForActorDataClass(actorClass) &&
                        (actorClass.DefualtActorDataPath == null || !string.Equals(actorClass.DefualtActorDataPath.Id, path.Id, StringComparison.Ordinal)) &&
                        (actorClass.ReserveActorDataPath == null || !string.Equals(actorClass.ReserveActorDataPath.Id, path.Id, StringComparison.Ordinal)))
                    {
                        error = "slot " + (i + 1) + " path is not valid for actor: " + pathId + ".";
                        return false;
                    }
                }
                catch
                {
                }

                NormalizeArenaHeroDraftSkills(slot, false);
                if (slot.SkillIds.Count < 1 || slot.SkillIds.Count > 5)
                {
                    error = "slot " + (i + 1) + " needs 1-5 skills; found " + slot.SkillIds.Count + ".";
                    return false;
                }

                List<string> available = GetArenaDraftAvailableSkillIds(actorId, pathId);
                HashSet<string> availableSet = new HashSet<string>(available, StringComparer.Ordinal);
                foreach (string skillId in slot.SkillIds)
                {
                    ActorDataSkill skill = TryGetArenaSkillData(skillId);
                    if (skill == null)
                    {
                        error = "slot " + (i + 1) + " skill not found: " + skillId + ".";
                        return false;
                    }

                    if (availableSet.Count > 0 && !IsArenaDraftSkillAllowed(skillId, availableSet))
                    {
                        error = "slot " + (i + 1) + " skill is not valid for actor/path: " + skillId + ".";
                        return false;
                    }
                }

                heroIds.Add(actorId);
                pathIds.Add(pathId);
                skillIds.AddRange(slot.SkillIds);
            }

            return true;
        }

        private static bool TryValidateArenaDraftItems(
            ArenaHeroDraftSlot[] slots,
            out string error)
        {
            error = string.Empty;

            if (slots == null)
            {
                return true;
            }

            for (int i = 0; i < slots.Length; i++)
            {
                ArenaHeroDraftSlot slot = slots[i];
                if (slot == null || string.IsNullOrWhiteSpace(slot.ActorId))
                {
                    continue;
                }

                string combatItemId = (slot.CombatItemId ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(combatItemId))
                {
                    ItemDefinition definition = TryGetItemDefinition(combatItemId);
                    if (definition == null || definition.m_type != ItemType.COMBAT)
                    {
                        error = "slot " + (i + 1) + " combat item is invalid: " + combatItemId + ".";
                        return false;
                    }
                }

                for (int j = 0; j < slot.TrinketIds.Count; j++)
                {
                    string trinketId = (slot.TrinketIds[j] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(trinketId))
                    {
                        continue;
                    }

                    ItemDefinition definition = TryGetItemDefinition(trinketId);
                    if (definition == null || definition.m_type != ItemType.TRINKET)
                    {
                        error = "slot " + (i + 1) + " trinket " + (j + 1) + " is invalid: " + trinketId + ".";
                        return false;
                    }
                }
            }

            return true;
        }

        private void DrawArenaBattlePresetListColumn()
        {
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(430f), GUILayout.ExpandHeight(true));
            GUILayout.Label(Ui("Matching Presets", "匹配预设"));
            DrawWrappedLabel(_arenaBattlePresetMatches.Count + Ui(" shown", " 个显示") +
                (_arenaBattlePresetMatches.Count >= 120 ? Ui(" (refine search for more)", "（缩小搜索以显示更多结果）") : string.Empty));

            _arenaBattleConfigBrowserScroll = GUILayout.BeginScrollView(_arenaBattleConfigBrowserScroll, GUILayout.ExpandHeight(true));
            try
            {
                if (_arenaBattlePresetMatches.Count == 0)
                {
                    DrawWrappedLabel(Ui("No matching official battle preset.", "没有匹配的官方战斗预设。"));
                }

                foreach (ArenaBattlePresetEntry entry in _arenaBattlePresetMatches)
                {
                    DrawArenaBattlePresetRow(entry);
                }
            }
            finally
            {
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
            }
        }

        private void DrawArenaBattlePresetDetailColumn()
        {
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandHeight(true));
            ArenaBattlePresetEntry entry = GetArenaBattlePresetEntry(_arenaBattleConfigId);
            if (entry == null || entry.Config == null)
            {
                DrawWrappedLabel(Ui(
                    "Selected preset is not in the current cache. Use Refresh or pick another preset from the list.",
                    "当前选择的预设不在缓存里。请刷新，或从列表重新选择预设。"));
                GUILayout.EndVertical();
                return;
            }

            _arenaBattlePresetDetailScroll = GUILayout.BeginScrollView(_arenaBattlePresetDetailScroll, GUILayout.ExpandHeight(true));
            GUILayout.Label(Ui("Selected", "当前选择"));
            DrawWrappedLabel(entry.Id);
            DrawWrappedLabel(Ui("Enemies: ", "敌人：") + entry.Summary);
            DrawWrappedLabel(Ui("Background: ", "背景：") + (string.IsNullOrWhiteSpace(entry.BackgroundScene) ? Ui("[default]", "[默认]") : entry.BackgroundScene));
            DrawWrappedLabel(Ui("Sequence: ", "连战：") + (string.IsNullOrWhiteSpace(entry.ChainSummary) ? Ui("[single]", "[单场]") : entry.ChainSummary));
            if (entry.IsReferencedChainChild)
            {
                DrawWrappedLabel(Ui(
                    "This preset is normally folded into an official chain parent. Exact id search can still show it for debugging.",
                    "此预设通常会合并到官方连战的父预设里。精确搜索 id 时仍可显示，用于调试。"));
            }

            DrawArenaValidationLine(
                Ui("Launch", "启动"),
                entry.IsLaunchRecommended,
                string.IsNullOrWhiteSpace(entry.RiskSummary) ? Ui("recommended", "推荐") : entry.RiskSummary);
            if (!string.IsNullOrWhiteSpace(entry.Tooltip))
            {
                DrawWrappedLabel(TrimPanelText(CleanTooltip(entry.Tooltip), 360));
            }

            DrawArenaBattleWavePreview(entry);
            DrawArenaBattleSequenceQueuePanel();
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Ui("Use Single", "使用单场"), GUILayout.Height(28f)))
            {
                _arenaBattleConfigId = entry.Id;
                _arenaBattleSequenceIds.Clear();
                _arenaBattlePresetBrowserVisible = false;
            }

            if (GUILayout.Button(Ui("Queue Wave", "加入单波"), GUILayout.Height(28f)))
            {
                AddArenaBattleSequenceId(entry.Id);
            }

            if (GUILayout.Button(Ui("Queue Chain", "加入连战"), GUILayout.Height(28f)))
            {
                AddArenaBattlePresetChainToSequence(entry);
            }

            if (GUILayout.Button(Ui("Close", "关闭"), GUILayout.Height(28f)))
            {
                _arenaBattlePresetBrowserVisible = false;
            }

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void DrawArenaBattlePresetRow(ArenaBattlePresetEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            bool selected = string.Equals((_arenaBattleConfigId ?? string.Empty).Trim(), entry.Id, StringComparison.Ordinal);
            Rect row = GUILayoutUtility.GetRect(0f, 72f, GUILayout.ExpandWidth(true), GUILayout.Height(72f));
            DrawSolidRect(row, selected ? HudCurrentCardColor : HudCardColor);
            RegisterTooltip(row, entry.Id, entry.Tooltip);
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                _arenaBattleConfigId = entry.Id;
            }

            GUIStyle title = CreateHudLabelStyle(12, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUIStyle meta = CreateHudLabelStyle(11, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperLeft);
            GUI.Label(new Rect(row.x + 10f, row.y + 7f, row.width - 20f, 20f), (selected ? "* " : string.Empty) + (entry.Id ?? "[battle]"), title);
            GUI.Label(new Rect(row.x + 10f, row.y + 28f, row.width - 20f, 18f), TrimPanelText(entry.Summary, 92), meta);
            string lower = string.IsNullOrWhiteSpace(entry.RiskSummary)
                ? (string.IsNullOrWhiteSpace(entry.ChainSummary) ? "single" : entry.ChainSummary)
                : entry.RiskSummary;
            GUI.Label(new Rect(row.x + 10f, row.y + 47f, row.width - 20f, 18f), TrimPanelText(lower, 104), meta);
        }

        private bool EnsureArenaBattlePresetCache()
        {
            if (_arenaBattlePresetCacheBuilt)
            {
                return true;
            }

            return RebuildArenaBattlePresetCache();
        }

        private bool RebuildArenaBattlePresetCache()
        {
            _arenaBattlePresetCache.Clear();
            _arenaBattlePresetMatches.Clear();
            _arenaBattlePresetSearchApplied = null;
            _arenaBattlePresetTotalCount = 0;
            _arenaBattlePresetMergedChildCount = 0;

            try
            {
                if (!SingletonMonoBehaviour<Library<string, BattleConfigurationDefinition>>.HasInstance(false))
                {
                    _arenaBattlePresetCacheBuilt = false;
                    return false;
                }

                Library<string, BattleConfigurationDefinition> library =
                    SingletonMonoBehaviour<Library<string, BattleConfigurationDefinition>>.Instance;
                _arenaBattlePresetTotalCount = library.GetNumberOfLibraryElements();
                List<BattleConfigurationDefinition> configs = new List<BattleConfigurationDefinition>();
                for (int i = 0; i < _arenaBattlePresetTotalCount; i++)
                {
                    BattleConfigurationDefinition config = library.GetLibraryElementAtIndex(i);
                    if (config != null)
                    {
                        configs.Add(config);
                    }
                }

                HashSet<string> referencedChildIds = BuildArenaReferencedBattleConfigIds(configs);
                _arenaBattlePresetMergedChildCount = referencedChildIds.Count;

                foreach (BattleConfigurationDefinition config in configs)
                {
                    ArenaBattlePresetEntry entry = BuildArenaBattlePresetEntry(
                        config,
                        referencedChildIds.Contains(config.m_Id));
                    if (entry != null)
                    {
                        _arenaBattlePresetCache.Add(entry);
                    }
                }

                _arenaBattlePresetCache.Sort((left, right) => string.Compare(left.Id, right.Id, StringComparison.Ordinal));
                _arenaBattlePresetCacheBuilt = true;
                RefreshArenaBattlePresetMatchesIfNeeded(true);
                HostLog.Write("[arena] Battle preset cache built: " + _arenaBattlePresetCache.Count + "/" + _arenaBattlePresetTotalCount +
                    ", mergedChildren=" + _arenaBattlePresetMergedChildCount + ".");
                return true;
            }
            catch (Exception ex)
            {
                _arenaBattlePresetCacheBuilt = false;
                HostLog.Write("[arena] Failed to build battle preset cache: " + ex.Message);
                return false;
            }
        }

        private ArenaBattlePresetEntry BuildArenaBattlePresetEntry(BattleConfigurationDefinition config, bool isReferencedChainChild)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.m_Id))
            {
                return null;
            }

            List<string> enemyIds = (config.m_EnemyActors ?? Array.Empty<string>())
                .Where(actorId => !string.IsNullOrWhiteSpace(actorId))
                .Select(actorId => actorId.Trim())
                .ToList();
            List<string> enemyNames = enemyIds
                .Select(GetArenaActorClassDisplayName)
                .ToList();
            List<string> chainBattleIds = BuildArenaBattleConfigReferenceIds(config, 24);
            List<ArenaBattleWavePreview> waves = BuildArenaBattleWavePreviews(config, 48);
            string backgroundScene = GetArenaResolvedBackgroundScene(config);
            string chainSummary = BuildArenaBattleChainSummary(config);
            string riskSummary = BuildArenaBattlePresetRiskSummary(config, enemyIds, chainBattleIds, backgroundScene, out bool recommended);

            List<string> searchParts = new List<string>
            {
                config.m_Id,
                backgroundScene,
                chainSummary,
                riskSummary,
            };
            if (config.m_Tags != null)
            {
                searchParts.AddRange(config.m_Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)));
            }

            searchParts.AddRange(enemyIds);
            searchParts.AddRange(enemyNames);
            searchParts.AddRange(chainBattleIds);
            foreach (ArenaBattleWavePreview wave in waves)
            {
                if (wave == null)
                {
                    continue;
                }

                searchParts.Add(wave.BattleConfigId);
                searchParts.Add(wave.Summary);
                if (wave.EnemyActorIds != null)
                {
                    searchParts.AddRange(wave.EnemyActorIds);
                }

                if (wave.EnemyNames != null)
                {
                    searchParts.AddRange(wave.EnemyNames);
                }
            }

            foreach (string chainBattleId in chainBattleIds)
            {
                BattleConfigurationDefinition chainConfig = TryGetBattleConfigurationNoError(chainBattleId);
                if (chainConfig == null || chainConfig.m_EnemyActors == null)
                {
                    continue;
                }

                foreach (string actorId in chainConfig.m_EnemyActors)
                {
                    if (string.IsNullOrWhiteSpace(actorId))
                    {
                        continue;
                    }

                    searchParts.Add(actorId);
                    searchParts.Add(GetArenaActorClassDisplayName(actorId));
                }
            }

            return new ArenaBattlePresetEntry
            {
                Config = config,
                Id = config.m_Id,
                EnemyActorIds = enemyIds,
                EnemyNames = enemyNames,
                Summary = BuildArenaEnemyCompositionText(enemyIds, enemyNames, true),
                BackgroundScene = backgroundScene,
                ChainSummary = chainSummary,
                RiskSummary = riskSummary,
                IsLaunchRecommended = recommended,
                IsReferencedChainChild = isReferencedChainChild,
                ChainBattleIds = chainBattleIds,
                Waves = waves,
                SearchText = string.Join(" ", searchParts.ToArray()).ToLowerInvariant(),
                Tooltip = BuildArenaBattleConfigTooltip(config) +
                    "\nBackground: " + backgroundScene +
                    "\nSequence: " + (string.IsNullOrWhiteSpace(chainSummary) ? "[single]" : chainSummary) +
                    "\nLaunch: " + (string.IsNullOrWhiteSpace(riskSummary) ? "recommended" : riskSummary),
            };
        }

        private static HashSet<string> BuildArenaReferencedBattleConfigIds(IEnumerable<BattleConfigurationDefinition> configs)
        {
            HashSet<string> referenced = new HashSet<string>(StringComparer.Ordinal);
            if (configs == null)
            {
                return referenced;
            }

            foreach (BattleConfigurationDefinition config in configs)
            {
                if (config == null || string.IsNullOrWhiteSpace(config.m_Id))
                {
                    continue;
                }

                foreach (ArenaBattleWavePreview wave in BuildArenaBattleWavePreviews(config, 64))
                {
                    if (wave == null ||
                        string.IsNullOrWhiteSpace(wave.BattleConfigId) ||
                        string.Equals(wave.BattleConfigId, config.m_Id, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    referenced.Add(wave.BattleConfigId);
                }
            }

            return referenced;
        }

        private static List<ArenaBattleWavePreview> BuildArenaBattleWavePreviews(BattleConfigurationDefinition config, int maxEntries)
        {
            List<ArenaBattleWavePreview> waves = new List<ArenaBattleWavePreview>();
            if (config == null || maxEntries <= 0)
            {
                return waves;
            }

            HashSet<string> seenExplicit = new HashSet<string>(StringComparer.Ordinal);
            BattleConfigurationDefinition current = config;
            int waveNumber = 1;
            for (int depth = 0; depth < 12 && current != null && waves.Count < maxEntries; depth++)
            {
                if (!string.IsNullOrWhiteSpace(current.m_Id) && !seenExplicit.Add(current.m_Id))
                {
                    AddArenaBattleWavePreview(waves, current, "Wave " + waveNumber + " (loop)", false);
                    break;
                }

                AddArenaBattleWavePreview(waves, current, "Wave " + waveNumber, false);

                string nextTableId = GetArenaPrivateString(current, "m_NextBattleConfigurationTableId");
                if (!string.IsNullOrWhiteSpace(nextTableId))
                {
                    List<string> possible = GetArenaPossibleBattleConfigIdsFromTable(nextTableId, Mathf.Max(0, maxEntries - waves.Count));
                    foreach (string possibleId in possible)
                    {
                        BattleConfigurationDefinition option = TryGetBattleConfigurationNoError(possibleId);
                        AddArenaBattleWavePreview(waves, option, "Wave " + (waveNumber + 1) + " option", true);
                        if (waves.Count >= maxEntries)
                        {
                            break;
                        }
                    }

                    break;
                }

                string nextId = GetArenaPrivateString(current, "m_NextBattleConfigurationId");
                if (string.IsNullOrWhiteSpace(nextId))
                {
                    break;
                }

                current = TryGetBattleConfigurationNoError(nextId);
                waveNumber++;
            }

            string additionalId = GetArenaPrivateString(config, "m_AdditionalBattleConfigurationId");
            if (!string.IsNullOrWhiteSpace(additionalId) && waves.Count < maxEntries)
            {
                AddArenaBattleWavePreview(
                    waves,
                    TryGetBattleConfigurationNoError(additionalId),
                    "Additional",
                    false);
            }

            string additionalTableId = GetArenaPrivateString(config, "m_AdditionalBattleConfigurationTableId");
            if (!string.IsNullOrWhiteSpace(additionalTableId) && waves.Count < maxEntries)
            {
                List<string> possible = GetArenaPossibleBattleConfigIdsFromTable(additionalTableId, Mathf.Max(0, maxEntries - waves.Count));
                foreach (string possibleId in possible)
                {
                    AddArenaBattleWavePreview(
                        waves,
                        TryGetBattleConfigurationNoError(possibleId),
                        "Additional option",
                        true);
                    if (waves.Count >= maxEntries)
                    {
                        break;
                    }
                }
            }

            return waves;
        }

        private static void AddArenaBattleWavePreview(
            List<ArenaBattleWavePreview> waves,
            BattleConfigurationDefinition config,
            string label,
            bool isTableOption)
        {
            if (waves == null || config == null || string.IsNullOrWhiteSpace(config.m_Id))
            {
                return;
            }

            List<string> enemyIds = (config.m_EnemyActors ?? Array.Empty<string>())
                .Where(actorId => !string.IsNullOrWhiteSpace(actorId))
                .Select(actorId => actorId.Trim())
                .ToList();
            List<string> enemyNames = enemyIds.Select(GetArenaActorClassDisplayName).ToList();
            waves.Add(new ArenaBattleWavePreview
            {
                Label = label ?? "Wave",
                BattleConfigId = config.m_Id,
                EnemyActorIds = enemyIds,
                EnemyNames = enemyNames,
                Summary = BuildArenaEnemyCompositionText(enemyIds, enemyNames, true),
                IsTableOption = isTableOption,
            });
        }

        private static string GetArenaResolvedBackgroundScene(BattleConfigurationDefinition config)
        {
            string configBackground = config == null ? null : config.m_BackgroundSceneOverride;
            if (!string.IsNullOrWhiteSpace(configBackground))
            {
                return configBackground.Trim();
            }

            return DefaultArenaCombatArenaId;
        }

        private string GetArenaResolvedBackgroundSceneForLaunch(BattleConfigurationDefinition config)
        {
            string userOverride = (_arenaCombatArenaId ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(userOverride))
            {
                return userOverride;
            }

            return GetArenaResolvedBackgroundScene(config);
        }

        private static string BuildArenaBattleChainSummary(BattleConfigurationDefinition config)
        {
            if (config == null)
            {
                return string.Empty;
            }

            List<string> parts = new List<string>();
            string nextId = GetArenaPrivateString(config, "m_NextBattleConfigurationId");
            string nextTableId = GetArenaPrivateString(config, "m_NextBattleConfigurationTableId");
            string additionalId = GetArenaPrivateString(config, "m_AdditionalBattleConfigurationId");
            string additionalTableId = GetArenaPrivateString(config, "m_AdditionalBattleConfigurationTableId");

            if (!string.IsNullOrWhiteSpace(nextId))
            {
                parts.Add("next: " + BuildArenaExplicitNextBattleChain(nextId, 8));
            }

            if (!string.IsNullOrWhiteSpace(nextTableId))
            {
                List<string> possible = GetArenaPossibleBattleConfigIdsFromTable(nextTableId, 10);
                parts.Add("next table " + nextTableId + ": " + possible.Count + " possible" +
                    (possible.Count == 0 ? string.Empty : " (" + string.Join(", ", possible.Take(6).ToArray()) + (possible.Count > 6 ? ", ..." : string.Empty) + ")"));
            }

            if (!string.IsNullOrWhiteSpace(additionalId))
            {
                parts.Add("additional: " + additionalId);
            }

            if (!string.IsNullOrWhiteSpace(additionalTableId))
            {
                List<string> possible = GetArenaPossibleBattleConfigIdsFromTable(additionalTableId, 10);
                parts.Add("additional table " + additionalTableId + ": " + possible.Count + " possible" +
                    (possible.Count == 0 ? string.Empty : " (" + string.Join(", ", possible.Take(6).ToArray()) + (possible.Count > 6 ? ", ..." : string.Empty) + ")"));
            }

            return string.Join(" | ", parts.ToArray());
        }

        private static string BuildArenaExplicitNextBattleChain(string firstBattleConfigId, int maxDepth)
        {
            List<string> ids = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            string current = firstBattleConfigId;
            for (int i = 0; i < maxDepth && !string.IsNullOrWhiteSpace(current); i++)
            {
                if (!seen.Add(current))
                {
                    ids.Add(current + " (loop)");
                    break;
                }

                ids.Add(current);
                BattleConfigurationDefinition config = TryGetBattleConfigurationNoError(current);
                if (config == null)
                {
                    break;
                }

                string tableId = GetArenaPrivateString(config, "m_NextBattleConfigurationTableId");
                if (!string.IsNullOrWhiteSpace(tableId))
                {
                    List<string> possible = GetArenaPossibleBattleConfigIdsFromTable(tableId, 6);
                    ids.Add("table:" + tableId + "(" + possible.Count + ")");
                    break;
                }

                current = GetArenaPrivateString(config, "m_NextBattleConfigurationId");
            }

            return ids.Count == 0 ? firstBattleConfigId : string.Join(" -> ", ids.ToArray());
        }

        private static List<string> BuildArenaBattleConfigReferenceIds(BattleConfigurationDefinition config, int maxIds)
        {
            List<string> ids = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            AddArenaBattleConfigReferenceId(ids, seen, config == null ? null : config.m_Id, maxIds);
            AddArenaBattleConfigReferenceId(ids, seen, GetArenaPrivateString(config, "m_NextBattleConfigurationId"), maxIds);
            foreach (string id in GetArenaPossibleBattleConfigIdsFromTable(GetArenaPrivateString(config, "m_NextBattleConfigurationTableId"), maxIds))
            {
                AddArenaBattleConfigReferenceId(ids, seen, id, maxIds);
            }

            AddArenaBattleConfigReferenceId(ids, seen, GetArenaPrivateString(config, "m_AdditionalBattleConfigurationId"), maxIds);
            foreach (string id in GetArenaPossibleBattleConfigIdsFromTable(GetArenaPrivateString(config, "m_AdditionalBattleConfigurationTableId"), maxIds))
            {
                AddArenaBattleConfigReferenceId(ids, seen, id, maxIds);
            }

            return ids;
        }

        private static void AddArenaBattleConfigReferenceId(List<string> ids, HashSet<string> seen, string id, int maxIds)
        {
            if (ids == null || seen == null || ids.Count >= maxIds || string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            id = id.Trim();
            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        private static List<string> GetArenaPossibleBattleConfigIdsFromTable(string tableId, int maxIds)
        {
            List<string> ids = new List<string>();
            if (string.IsNullOrWhiteSpace(tableId))
            {
                return ids;
            }

            try
            {
                HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
                LibraryBattleConfigurationTables.CollectAllPossibleConfigurations(tableId.Trim(), false, set);
                ids.AddRange(set.Where(id => !string.IsNullOrWhiteSpace(id)).OrderBy(id => id).Take(maxIds));
            }
            catch
            {
            }

            return ids;
        }

        private static string BuildArenaBattlePresetRiskSummary(
            BattleConfigurationDefinition config,
            IReadOnlyList<string> enemyIds,
            IReadOnlyList<string> referencedBattleIds,
            string backgroundScene,
            out bool recommended)
        {
            recommended = true;
            List<string> warnings = new List<string>();

            if (config == null)
            {
                recommended = false;
                return "missing config";
            }

            if (enemyIds == null || enemyIds.Count == 0)
            {
                recommended = false;
                warnings.Add("no enemy actors");
            }

            if (ArenaPresetContainsExcludedActor(enemyIds) || ArenaPresetReferenceContainsExcludedActor(referencedBattleIds))
            {
                recommended = false;
                warnings.Add("excluded coven sister mechanic");
            }

            if (string.IsNullOrWhiteSpace(config.m_BackgroundSceneOverride) &&
                string.Equals(backgroundScene, DefaultArenaCombatArenaId, StringComparison.Ordinal))
            {
                warnings.Add("uses default arena fallback");
            }

            if (config.m_PlayerActors != null && config.m_PlayerActors.Count > 0)
            {
                warnings.Add("preset player actors ignored");
            }

            if (config.HasNextBattle)
            {
                warnings.Add(config.m_IsNextBattleOptional ? "optional next battle" : "official next battle");
            }

            if (config.HasAdditionalBattle)
            {
                warnings.Add("additional battle package");
            }

            return string.Join(", ", warnings.ToArray());
        }

        private static bool ArenaPresetReferenceContainsExcludedActor(IReadOnlyList<string> battleConfigIds)
        {
            if (battleConfigIds == null)
            {
                return false;
            }

            foreach (string id in battleConfigIds)
            {
                BattleConfigurationDefinition config = TryGetBattleConfigurationNoError(id);
                if (config != null && ArenaPresetContainsExcludedActor(config.m_EnemyActors))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ArenaPresetContainsExcludedActor(IReadOnlyList<string> actorIds)
        {
            if (actorIds == null)
            {
                return false;
            }

            return actorIds.Any(IsArenaExcludedActorId);
        }

        private static bool IsArenaExcludedActorId(string actorId)
        {
            return string.Equals(actorId, "coven_sister_a", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actorId, "coven_sister_b", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetArenaPrivateString(object instance, string fieldName)
        {
            try
            {
                if (instance == null || string.IsNullOrWhiteSpace(fieldName))
                {
                    return null;
                }

                FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(instance) as string;
            }
            catch
            {
                return null;
            }
        }

        private static BattleConfigurationDefinition TryGetBattleConfigurationNoError(string battleConfigId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(battleConfigId) ||
                    !SingletonMonoBehaviour<Library<string, BattleConfigurationDefinition>>.HasInstance(false))
                {
                    return null;
                }

                return SingletonMonoBehaviour<Library<string, BattleConfigurationDefinition>>.Instance.GetLibraryElement(battleConfigId.Trim());
            }
            catch
            {
                return null;
            }
        }

        private void RefreshArenaBattlePresetMatchesIfNeeded(bool force)
        {
            string query = (_arenaBattleConfigSearch ?? string.Empty).Trim().ToLowerInvariant();
            if (!force && string.Equals(_arenaBattlePresetSearchApplied, query, StringComparison.Ordinal))
            {
                return;
            }

            _arenaBattlePresetMatches.Clear();
            string[] terms = query
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .ToArray();
            foreach (ArenaBattlePresetEntry entry in _arenaBattlePresetCache)
            {
                if (entry.IsReferencedChainChild &&
                    !string.Equals(query, entry.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (terms.Length > 0 && !terms.All(term => (entry.SearchText ?? string.Empty).Contains(term)))
                {
                    continue;
                }

                if (_arenaBattlePresetMatches.Count < 120)
                {
                    _arenaBattlePresetMatches.Add(entry);
                }
            }

            _arenaBattlePresetSearchApplied = query;
        }

        private ArenaBattlePresetEntry GetArenaBattlePresetEntry(string battleConfigId)
        {
            if (!EnsureArenaBattlePresetCache())
            {
                return null;
            }

            string id = (battleConfigId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return _arenaBattlePresetCache.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
        }

        private ArenaBattlePresetEntry GetArenaBattlePresetEntryFromCache(string battleConfigId)
        {
            if (!_arenaBattlePresetCacheBuilt)
            {
                return null;
            }

            string id = (battleConfigId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return _arenaBattlePresetCache.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
        }

        private void DrawArenaEnemyPreview(BattleConfigurationDefinition config)
        {
            if (config == null || config.m_EnemyActors == null || config.m_EnemyActors.Count == 0)
            {
                DrawWrappedLabel("Enemy preview: none.");
                return;
            }

            GUILayout.Label("Enemy Preview");
            int columns = GetArenaBattlePreviewColumnCount();
            for (int i = 0; i < config.m_EnemyActors.Count; i += columns)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < columns && i + column < config.m_EnemyActors.Count; column++)
                {
                    DrawArenaActorPreviewTile(config.m_EnemyActors[i + column], i + column + 1, false);
                    GUILayout.Space(8f);
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(6f);
            }
        }

        private void DrawArenaBattleWavePreview(ArenaBattlePresetEntry entry)
        {
            if (entry == null || entry.Waves == null || entry.Waves.Count == 0)
            {
                DrawArenaEnemyPreview(entry == null ? null : entry.Config);
                return;
            }

            GUILayout.Label(entry.Waves.Count <= 1 ? "Enemy Preview" : "Official Wave Preview");
            int columns = GetArenaBattlePreviewColumnCount();
            foreach (ArenaBattleWavePreview wave in entry.Waves)
            {
                if (wave == null)
                {
                    continue;
                }

                DrawWrappedLabel(
                    (wave.Label ?? "Wave") +
                    (wave.IsTableOption ? " (rolled option)" : string.Empty) +
                    ": " + (wave.BattleConfigId ?? "[battle]") +
                    " | " + (string.IsNullOrWhiteSpace(wave.Summary) ? "[none]" : wave.Summary));

                IReadOnlyList<string> actorIds = wave.EnemyActorIds ?? Array.Empty<string>();
                for (int i = 0; i < actorIds.Count; i += columns)
                {
                    GUILayout.BeginHorizontal();
                    for (int column = 0; column < columns && i + column < actorIds.Count; column++)
                    {
                        DrawArenaActorPreviewTile(actorIds[i + column], i + column + 1, false);
                        GUILayout.Space(8f);
                    }

                    GUILayout.EndHorizontal();
                    GUILayout.Space(6f);
                }
            }
        }

        private int GetArenaBattlePreviewColumnCount()
        {
            float width = _arenaBattlePresetBrowserRect.width;
            if (width >= 1180f)
            {
                return 4;
            }

            if (width >= 960f)
            {
                return 3;
            }

            return 2;
        }

        private void DrawArenaBattleSequenceQueuePanel()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("Launch Sequence", "启动序列"));
            GUILayout.FlexibleSpace();
            GUILayout.Label(_arenaBattleSequenceIds.Count == 0 ? Ui("single selected preset", "当前单场预设") : _arenaBattleSequenceIds.Count + Ui(" wave(s)", " 波"));
            GUI.enabled = _arenaBattleSequenceIds.Count > 0;
            if (GUILayout.Button(Ui("Clear", "清空"), GUILayout.Width(64f), GUILayout.Height(24f)))
            {
                _arenaBattleSequenceIds.Clear();
            }

            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_arenaBattleSequenceIds.Count == 0)
            {
                DrawWrappedLabel(Ui(
                    "Empty queue: launch uses the selected preset. If that preset has official next battles, DD2 still rolls that official chain.",
                    "队列为空：启动时使用当前选择的预设。如果该预设自带官方后续战斗，DD2 仍会按官方逻辑处理。"));
                GUILayout.EndVertical();
                return;
            }

            _arenaBattleSequenceScroll = GUILayout.BeginScrollView(_arenaBattleSequenceScroll, GUILayout.Height(112f));
            for (int i = 0; i < _arenaBattleSequenceIds.Count; i++)
            {
                DrawArenaBattleSequenceRow(i);
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawArenaBattleSequenceRow(int index)
        {
            if (index < 0 || index >= _arenaBattleSequenceIds.Count)
            {
                return;
            }

            string battleId = _arenaBattleSequenceIds[index];
            ArenaBattlePresetEntry entry = GetArenaBattlePresetEntryFromCache(battleId);
            string summary = entry == null ? battleId : entry.Summary;

            GUILayout.BeginHorizontal();
            GUILayout.Label("#" + (index + 1), GUILayout.Width(28f));
            GUILayout.Label(TrimPanelText((battleId ?? "[battle]") + " | " + summary, 92));
            GUI.enabled = index > 0;
            if (GUILayout.Button(Ui("Up", "上移"), GUILayout.Width(42f), GUILayout.Height(22f)))
            {
                string tmp = _arenaBattleSequenceIds[index - 1];
                _arenaBattleSequenceIds[index - 1] = _arenaBattleSequenceIds[index];
                _arenaBattleSequenceIds[index] = tmp;
            }

            GUI.enabled = index < _arenaBattleSequenceIds.Count - 1;
            if (GUILayout.Button(Ui("Down", "下移"), GUILayout.Width(54f), GUILayout.Height(22f)))
            {
                string tmp = _arenaBattleSequenceIds[index + 1];
                _arenaBattleSequenceIds[index + 1] = _arenaBattleSequenceIds[index];
                _arenaBattleSequenceIds[index] = tmp;
            }

            GUI.enabled = true;
            if (GUILayout.Button("X", GUILayout.Width(28f), GUILayout.Height(22f)))
            {
                _arenaBattleSequenceIds.RemoveAt(index);
            }

            GUILayout.EndHorizontal();
        }

        private void AddArenaBattleSequenceId(string battleConfigId)
        {
            string id = (battleConfigId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (_arenaBattleSequenceIds.Count >= 12)
            {
                _arenaStatus = "Launch sequence is capped at 12 waves.";
                return;
            }

            _arenaBattleSequenceIds.Add(id);
            _arenaStatus = "Queued arena wave " + _arenaBattleSequenceIds.Count + ": " + id + ".";
        }

        private void AddArenaBattlePresetChainToSequence(ArenaBattlePresetEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            int before = _arenaBattleSequenceIds.Count;
            if (entry.Waves != null)
            {
                foreach (ArenaBattleWavePreview wave in entry.Waves)
                {
                    if (wave == null || wave.IsTableOption)
                    {
                        continue;
                    }

                    AddArenaBattleSequenceId(wave.BattleConfigId);
                }
            }

            if (_arenaBattleSequenceIds.Count == before)
            {
                AddArenaBattleSequenceId(entry.Id);
            }
        }

        private void DrawArenaPartySummary()
        {
            IList<ActorInstance> party = GetArenaCurrentPartyActors();
            DrawArenaValidationLine(
                "Current party",
                party.Count >= 1 && party.Count <= 4,
                party.Count + "/1-4");

            if (party.Count == 0)
            {
                DrawWrappedLabel("Current party: none. Start or continue a run before launching the current-party arena.");
                return;
            }

            const int columns = 2;
            for (int i = 0; i < party.Count; i += columns)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < columns && i + column < party.Count; column++)
                {
                    DrawArenaHeroPreviewTile(party[i + column], i + column + 1);
                    GUILayout.Space(10f);
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(8f);
            }
        }

        private void DrawArenaPartySummaryLight()
        {
            IList<ActorInstance> party = GetArenaCurrentPartyActors();
            DrawArenaValidationLine(
                "Current party",
                party.Count == 4,
                party.Count + "/4");

            if (party.Count == 0)
            {
                DrawWrappedLabel("Current party: none. Start or continue a run before launching the current-party arena.");
                return;
            }

            GUILayout.Label("Current Party");
            for (int i = 0; i < party.Count; i++)
            {
                ActorInstance actor = party[i];
                if (actor == null)
                {
                    continue;
                }

                string path = actor.ActorDataPath == null ? "[path]" : actor.ActorDataPath.Id;
                string actorName = string.IsNullOrWhiteSpace(actor.ActorName)
                    ? actor.ActorDataId ?? "[actor]"
                    : CleanInline(actor.ActorName);
                string[] skills = GetArenaEquippedSkillIds(actor).ToArray();
                DrawWrappedLabel(
                    "S" + (i + 1) + " " + actorName +
                    " | id=" + (actor.ActorDataId ?? "[actor]") +
                    " | path=" + path +
                    " | skills=" + (skills.Length == 0 ? "[none]" : string.Join(", ", skills)));
            }
        }

        private void DrawArenaHeroDraftSummaryLight()
        {
            bool ready = TryGetArenaHeroDraftForLaunch(out _, out _, out _, out string error);
            DrawArenaValidationLine("Party hero draft", ready, ready ? BuildArenaDraftReadySummary(_arenaHeroDraftSlots) : error);
            bool enemyReady = !HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots) ||
                TryGetArenaEnemyHeroDraftForLaunch(out _, out _, out _, out error);
            DrawArenaValidationLine(
                "Enemy hero draft",
                enemyReady,
                !HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots)
                    ? "[battle config enemies]"
                    : (enemyReady ? BuildArenaDraftReadySummary(_arenaEnemyHeroDraftSlots) : error));
            DrawWrappedLabel("  Start effects: " +
                (_arenaHeroStartEffectIds.Count == 0
                    ? "[none]"
                    : string.Join(", ", _arenaHeroStartEffectIds.Take(4).ToArray()) +
                      (_arenaHeroStartEffectIds.Count > 4 ? " +" + (_arenaHeroStartEffectIds.Count - 4) : string.Empty)));
            DrawWrappedLabel("  Enemy ordainment: " +
                (string.IsNullOrWhiteSpace(_arenaBossModifierId) ? "[none]" : _arenaBossModifierId.Trim()));
            DrawWrappedLabel("  Battle advantage: " + GetArenaBattleAdvantageDisplaySummary());
            DrawWrappedLabel("  Party " + BuildArenaHeroDraftQuirkSummary(_arenaHeroDraftSlots));
            if (HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots))
            {
                DrawWrappedLabel("  Enemy " + BuildArenaHeroDraftQuirkSummary(_arenaEnemyHeroDraftSlots));
            }

            DrawArenaHeroDraftSummarySlots(_arenaHeroDraftSlots, "Party");
            if (HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots))
            {
                DrawArenaHeroDraftSummarySlots(_arenaEnemyHeroDraftSlots, "Enemy");
            }
        }

        private void DrawArenaHeroDraftSummarySlots(ArenaHeroDraftSlot[] slots, string label)
        {
            GUILayout.Label(label);
            for (int i = 0; i < slots.Length; i++)
            {
                ArenaHeroDraftSlot slot = slots[i];
                if (slot == null || string.IsNullOrWhiteSpace(slot.ActorId))
                {
                    DrawWrappedLabel("  S" + (i + 1) + ": [empty]");
                    continue;
                }

                DrawWrappedLabel(
                    "  S" + (i + 1) + ": " + GetArenaActorClassDisplayName(slot.ActorId) +
                    " | path=" + (string.IsNullOrWhiteSpace(slot.PathId) ? "[path]" : GetArenaDraftPathDisplayName(slot.PathId, slot.ActorId)) +
                    " | skills=" + slot.SkillIds.Count + "/5");
            }
        }

        private static string BuildArenaDraftReadySummary(ArenaHeroDraftSlot[] slots)
        {
            int heroes = CountConfiguredArenaDraftActors(slots);
            int skills = slots == null
                ? 0
                : slots
                    .Where(slot => slot != null && !string.IsNullOrWhiteSpace(slot.ActorId))
                    .Sum(slot => slot.SkillIds.Count);
            return heroes + " hero(s) / " + skills + " skill(s)";
        }

        private void DrawArenaHeroPreviewTile(ActorInstance actor, int slot)
        {
            if (actor == null)
            {
                return;
            }

            Rect tile = GUILayoutUtility.GetRect(372f, 158f, GUILayout.Width(372f), GUILayout.Height(158f));
            DrawSolidRect(tile, HudCardColor);

            Rect portraitRect = new Rect(tile.x + 10f, tile.y + 12f, 70f, 70f);
            DrawSolidRect(portraitRect, new Color(0.04f, 0.045f, 0.05f, 1f));
            DrawPortraitSprite(portraitRect, GetActorPortraitSprite(actor.ActorDataId));

            GUIStyle title = CreateHudLabelStyle(13, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            GUIStyle meta = CreateHudLabelStyle(11, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperLeft);
            string actorName = GetArenaActorInstanceDisplayName(actor);
            string pathName = GetArenaActorPathDisplayName(actor);
            GUI.Label(new Rect(tile.x + 90f, tile.y + 10f, tile.width - 100f, 24f),
                "S" + slot + " " + actorName,
                title);
            GUI.Label(new Rect(tile.x + 90f, tile.y + 34f, tile.width - 100f, 20f),
                "Path: " + pathName,
                meta);
            GUI.Label(new Rect(tile.x + 90f, tile.y + 54f, tile.width - 100f, 20f),
                "Id: " + (actor.ActorDataId ?? "[actor]") + " | guid=" + actor.ActorGuid,
                meta);

            RegisterTooltip(
                new Rect(tile.x, tile.y, tile.width, 86f),
                actorName,
                new Rect(tile.x, tile.y, tile.width, 86f).Contains(Event.current.mousePosition)
                    ? BuildArenaHeroTooltip(actor)
                    : string.Empty);

            List<string> skills = GetArenaEquippedSkillIds(actor);
            for (int i = 0; i < skills.Count && i < 5; i++)
            {
                string skillId = skills[i];
                Rect skillRect = new Rect(tile.x + 10f + i * 46f, tile.y + 102f, 40f, 40f);
                DrawSolidRect(skillRect, new Color(0.05f, 0.055f, 0.06f, 1f));
                DrawSprite(skillRect, GetSkillSprite(skillId));
                string skillName = CleanInline(GetArenaSkillDisplayName(skillId));
                RegisterTooltip(
                    skillRect,
                    skillName,
                    skillRect.Contains(Event.current.mousePosition)
                        ? GetArenaSkillDescription(skillId, actor)
                        : string.Empty);
            }
        }

        private void DrawArenaActorPreviewTile(string actorDataId, int rank, bool playerSide)
        {
            const float tileWidth = 160f;
            const float tileHeight = 126f;
            Rect tile = GUILayoutUtility.GetRect(tileWidth, tileHeight, GUILayout.Width(tileWidth), GUILayout.Height(tileHeight));
            DrawSolidRect(tile, playerSide ? HudCurrentCardColor : HudTileColor);

            Rect portraitRect = new Rect(tile.x + 10f, tile.y + 10f, 54f, 54f);
            DrawSolidRect(portraitRect, new Color(0.04f, 0.045f, 0.05f, 1f));
            DrawPortraitSprite(portraitRect, GetActorPortraitSprite(actorDataId));

            ActorDataClass actorClass = TryGetArenaActorDataClass(actorDataId);
            string displayName = GetArenaActorClassDisplayName(actorDataId);
            string size = actorClass == null ? "?" : actorClass.m_Size.ToString(CultureInfo.InvariantCulture);
            GUIStyle title = CreateHudLabelStyle(12, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
            title.wordWrap = true;
            GUIStyle meta = CreateHudLabelStyle(10, FontStyle.Normal, PanelMutedTextColor, TextAnchor.UpperLeft);
            GUI.Label(new Rect(tile.x + 72f, tile.y + 9f, tile.width - 82f, 40f), displayName, title);
            GUI.Label(new Rect(tile.x + 72f, tile.y + 52f, tile.width - 82f, 18f), "Rank " + rank + " | size " + size, meta);
            GUI.Label(new Rect(tile.x + 10f, tile.y + 76f, tile.width - 20f, 36f), TrimPanelText(actorDataId ?? "[actor]", 42), meta);
            RegisterTooltip(
                tile,
                displayName,
                tile.Contains(Event.current.mousePosition)
                    ? BuildArenaActorClassTooltip(actorDataId, actorClass)
                    : string.Empty);
        }

        private void DrawArenaLaunchReadiness()
        {
            PvpModeStatePayload state = null;
            bool hasEnemyPilot = _session != null &&
                _session.TryGetPvpModeState(out state) &&
                state != null &&
                state.Enabled &&
                state.EnemyControllerSteamId != 0UL;
            DrawArenaValidationLine(
                "Enemy pilot",
                hasEnemyPilot,
                hasEnemyPilot
                    ? ((state.EnemyControllerName ?? string.Empty) + "/" + state.EnemyControllerSteamId)
                    : "not assigned");

            DrawArenaValidationLine(
                "Launch path",
                IsArenaLaunchReady(out string reason),
                string.IsNullOrWhiteSpace(reason) ? "ready" : reason);
        }

        private void DrawArenaValidationLine(string label, bool ok, string details)
        {
            GUILayout.Label((ok ? "OK " : "TODO ") + label + ": " + (details ?? string.Empty));
        }

        private bool TryGetArenaBattleConfiguration(
            string battleConfigId,
            out BattleConfigurationDefinition config,
            out string error)
        {
            config = null;
            error = string.Empty;

            string id = (battleConfigId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "empty id";
                return false;
            }

            if (!SingletonMonoBehaviour<Library<string, BattleConfigurationDefinition>>.HasInstance(false))
            {
                error = "battle configuration library is not loaded";
                return false;
            }

            config = SingletonMonoBehaviour<Library<string, BattleConfigurationDefinition>>.Instance.GetLibraryElement(id);
            if (config == null)
            {
                error = "not found: " + id;
                return false;
            }

            return true;
        }

        private void TryUseCurrentArenaBattleConfig()
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.CombatScenarioData == null ||
                    Singleton<GameTypeMgr>.Instance.CombatScenarioData.CurrentBattleConfiguration == null)
                {
                    HostLog.Write("[arena] Current battle config is not available.");
                    return;
                }

                _arenaBattleConfigId = Singleton<GameTypeMgr>.Instance
                    .CombatScenarioData
                    .CurrentBattleConfiguration
                    .m_Id;
                HostLog.Write("[arena] Using current battle config: " + _arenaBattleConfigId + ".");
            }
            catch (Exception ex)
            {
                HostLog.Write("[arena] Failed to use current battle config: " + ex.Message);
            }
        }

        private void LogArenaSetupSummary()
        {
            BattleConfigurationDefinition config;
            string error;
            bool valid = TryGetArenaBattleConfiguration(_arenaBattleConfigId, out config, out error);

            PvpModeStatePayload state = null;
            bool hasState = _session != null && _session.TryGetPvpModeState(out state) && state != null;
            HostLog.Write("[arena] setup battle=" + ((_arenaBattleConfigId ?? string.Empty).Trim()) +
                ", valid=" + valid +
                (valid ? string.Empty : ", error=" + error) +
                ", sequence=" + BuildArenaLaunchSequenceSummary() +
                ", arenaOverride=" + (string.IsNullOrWhiteSpace(_arenaCombatArenaId) ? "[none]" : _arenaCombatArenaId.Trim()) +
                ", startEffects=" + (_arenaHeroStartEffectIds.Count == 0 ? "[none]" : string.Join(",", _arenaHeroStartEffectIds.ToArray())) +
                ", quirks=" + BuildArenaHeroDraftQuirkSummary() +
                ", ordainment=" + (string.IsNullOrWhiteSpace(_arenaBossModifierId) ? "[none]" : _arenaBossModifierId.Trim()) +
                ", battleAdvantage=" + GetArenaBattleAdvantageModeName(GetEffectiveArenaBattleAdvantageMode()) +
                "/" + (string.IsNullOrWhiteSpace(_arenaBattleModifierId) ? "[none]" : _arenaBattleModifierId.Trim()) +
                ", torch=" + GetArenaTorchDisplaySummary() +
                ", pvp=" + (hasState && state.Enabled) +
                ", enemy=" + (hasState ? ((state.EnemyControllerName ?? "[none]") + "/" + state.EnemyControllerSteamId) : "[none]") +
                (valid
                    ? ", enemies=" + JoinArenaIds(config.m_EnemyActors) +
                      ", background=" + GetArenaResolvedBackgroundSceneForLaunch(config) +
                      ", nextBattle=" + config.HasNextBattle +
                      ", additionalBattle=" + config.HasAdditionalBattle +
                      ", playerActors=" + JoinArenaIds(config.m_PlayerActors)
                    : string.Empty) +
                ".");
        }

        private void SaveArenaHeroDraftFromPanel()
        {
            EnsureArenaHeroDraftInitialized();
            if (!BuildArenaNativeLaunchPrefsLines(out string[] lines, out string buildError))
            {
                _arenaStatus = "Draft validation blocked: " + buildError;
                HostLog.Write("[arena] Draft validation blocked: " + buildError);
                return;
            }

            LogArenaSetupSummary();

            List<ActorInstance> party = GetArenaCurrentPartyActors();
            if (DoesArenaPartyMatchDraft(party))
            {
                if (!TryApplyArenaDraftSkillsToParty(party, out int changedActors, out string applyError))
                {
                    _arenaStatus = "Draft validated, but current party apply failed: " + applyError;
                    HostLog.Write("[arena] Draft validated, but current party apply failed: " + applyError + ".");
                    return;
                }

                _arenaStatus = "Draft validated and applied to current party.";
                HostLog.Write("[arena] Draft validated and applied to current party; actorsChanged=" + changedActors +
                    ", nativeLaunchPrefs=" + lines.Length + ".");
                return;
            }

            _arenaStatus = "Draft validated in memory. Current party differs, so it will be applied during launch.";
            HostLog.Write("[arena] Draft validated in memory; current party does not match draft, apply deferred to launch; nativeLaunchPrefs=" +
                lines.Length + ".");
        }

        private static int CountConfiguredArenaDraftActors(ArenaHeroDraftSlot[] slots)
        {
            return slots == null ? 0 : slots.Count(slot => slot != null && !string.IsNullOrWhiteSpace(slot.ActorId));
        }

        private void BeginArenaLaunch()
        {
            if (_arenaPendingLaunch)
            {
                _arenaStatus = "Launch is already pending.";
                HostLog.Write("[arena] Launch request ignored; launch is already pending.");
                return;
            }

            if (!BuildArenaNativeLaunchPrefsLines(out string[] lines, out string error))
            {
                _arenaStatus = "Launch blocked: " + error;
                HostLog.Write("[arena] Launch blocked: " + error);
                return;
            }

            _arenaPendingNativeLaunchPrefsLines = lines;
            _arenaBattleModifierOverrideArmed = true;
            _arenaBattleModifierOverrideLogKey = null;
            _arenaPendingLaunch = true;
            _arenaPendingDraftSkillApply = false;
            _arenaPendingDraftQuirkApply = false;
            _arenaPendingEnemyDraftApply = false;
            _arenaBattlePresetBrowserVisible = false;
            _arenaLaunchDeadline = Time.unscaledTime + 90f;
            _nextArenaLaunchLogTime = 0f;
            _arenaStatus = "Launch pending; waiting for safe DD2 runtime state.";
            HostLog.Write("[arena] Launch pending for battle=" + (_arenaBattleConfigId ?? string.Empty).Trim() +
                ", sequence=" + BuildArenaLaunchSequenceSummary() +
                ", battleAdvantage=" + GetArenaBattleAdvantageModeName(GetEffectiveArenaBattleAdvantageMode()) +
                "/" + (string.IsNullOrWhiteSpace(_arenaBattleModifierId) ? "[none]" : _arenaBattleModifierId.Trim()) +
                ", torch=" + GetArenaTorchDisplaySummary() +
                ", nativeLaunchPrefs=" + lines.Length + ".");
        }

        private void PollPendingArenaLaunch()
        {
            if (!_arenaPendingLaunch)
            {
                return;
            }

            try
            {
                if (!IsArenaLaunchReady(out string reason))
                {
                    if (Time.unscaledTime >= _arenaLaunchDeadline)
                    {
                        _arenaPendingLaunch = false;
                        _arenaStatus = "Launch timed out: " + reason;
                        HostLog.Write("[arena] Launch timed out: " + reason + " " + BuildArenaLaunchSnapshot());
                        return;
                    }

                    if (Time.unscaledTime >= _nextArenaLaunchLogTime)
                    {
                        _nextArenaLaunchLogTime = Time.unscaledTime + 3f;
                        HostLog.Write("[arena] Waiting to launch: " + reason + " " + BuildArenaLaunchSnapshot());
                    }

                    return;
                }

                if (_arenaPendingNativeLaunchPrefsLines == null || _arenaPendingNativeLaunchPrefsLines.Length == 0)
                {
                    _arenaPendingLaunch = false;
                    _arenaStatus = "Launch failed: no native launch prefs were prepared.";
                    HostLog.Write("[arena] Launch failed: no native launch prefs were prepared.");
                    return;
                }

                TextBasedEditorPrefs.SetEditorPrefsFromStringArray(_arenaPendingNativeLaunchPrefsLines, false, false);
                ClearArenaNativeLoadoutEditorPrefs();
                if (!TryPreloadArenaCustomEnemyDraft(out string enemyPreloadError))
                {
                    _arenaPendingLaunch = false;
                    _arenaStatus = "Launch failed: failed to preload custom enemy heroes: " + enemyPreloadError;
                    HostLog.Write("[arena] Launch failed: failed to preload custom enemy heroes: " + enemyPreloadError + ".");
                    return;
                }

                if (!TryRebuildArenaPartyFromDraft(out int rebuiltPartyCount, out string partyRebuildError))
                {
                    _arenaPendingLaunch = false;
                    _arenaStatus = "Launch failed: failed to rebuild draft party: " + partyRebuildError;
                    HostLog.Write("[arena] Launch failed: failed to rebuild draft party before combat launch: " + partyRebuildError + ".");
                    return;
                }

                HostLog.Write("[arena] Draft party rebuilt before combat launch; party=" + rebuiltPartyCount + " actor(s).");

                if (!TryGetArenaLaunchBattleSequenceConfigs(out List<BattleConfigurationDefinition> sequenceConfigs, out string sequenceError) ||
                    !TryValidateArenaBattleSequenceForLaunch(sequenceConfigs, out sequenceError))
                {
                    _arenaPendingLaunch = false;
                    _arenaStatus = "Launch failed: " + sequenceError;
                    HostLog.Write("[arena] Launch failed: invalid launch sequence: " + sequenceError + ".");
                    return;
                }

                string resolvedArena = GetArenaResolvedBackgroundSceneForLaunch(sequenceConfigs[0]);
                IReadOnlyList<uint> startingPartyGuids = Singleton<GameTypeMgr>.Instance.RosterManager.GetActorGuids(RosterStatusType.PARTY);
                CombatScenarioData scenarioData = new CombatScenarioData(
                    sequenceConfigs,
                    0,
                    resolvedArena,
                    startingPartyGuids,
                    CombatSource.DEBUG);
                if (scenarioData == null || scenarioData.CurrentBattleConfiguration == null)
                {
                    _arenaPendingLaunch = false;
                    _arenaStatus = "Launch failed: invalid combat scenario.";
                    HostLog.Write("[arena] Launch failed: invalid combat scenario.");
                    return;
                }

                if (_arenaBattleSequenceIds.Count > 0)
                {
                    HostLog.Write("[arena] Custom launch sequence applied: " +
                        string.Join(" -> ", sequenceConfigs.Select(config => config.m_Id).ToArray()) + ".");
                }

                List<ActorInstance> preLaunchParty = GetArenaCurrentPartyActors();
                if (DoesArenaPartyMatchDraft(preLaunchParty))
                {
                    if (!TryApplyArenaDraftSkillsToParty(preLaunchParty, out int changedActors, out string skillApplyError))
                    {
                        _arenaPendingLaunch = false;
                        _arenaStatus = "Launch failed: failed to apply draft skills: " + skillApplyError;
                        HostLog.Write("[arena] Launch failed: failed to apply draft skills before combat launch: " + skillApplyError + ".");
                        return;
                    }

                    HostLog.Write("[arena] Draft skills applied before combat launch; actorsChanged=" + changedActors + ".");
                }
                else
                {
                    HostLog.Write("[arena] Draft skills not applied before combat launch because the current party does not match the draft; will retry after combat entry.");
                }

                ArmArenaTorchOverrideForLaunch();
                _arenaPendingLaunch = false;
                _arenaDebugControlsSuppressed = true;
                _arenaDebugControlsEnteredCombat = false;
                _arenaResultBypassArmed = true;
                _arenaResultBypassLogged = false;
                _arenaResultBypassCombatEntered = false;
                _arenaWaitingForNextBattle = false;
                _arenaPostBattleMainMenuReturnPending = false;
                _arenaPostBattleMainMenuReturnRequested = false;
                _arenaPendingDraftSkillApply = true;
                _arenaPendingDraftQuirkApply = HasArenaHeroDraftAnyQuirks();
                _arenaPendingEnemyDraftApply = HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots);
                _arenaResultBypassDeadline = Time.unscaledTime + 180f;
                _nextArenaResultBypassAttemptTime = 0f;
                _arenaResultsModeFirstSeenTime = 0f;
                _arenaResultLoadingFirstSeenTime = 0f;
                _nextArenaResultLoadingLogTime = 0f;
                _arenaPostBattleMainMenuReturnTime = 0f;
                _nextArenaPostBattleMainMenuLogTime = 0f;
                _arenaNextBattleWaitStartTime = 0f;
                _nextArenaNextBattleWaitLogTime = 0f;
                _arenaDraftSkillApplyDeadline = Time.unscaledTime + 45f;
                _nextArenaDraftSkillApplyAttemptTime = 0f;
                _arenaDraftQuirkApplyDeadline = Time.unscaledTime + 45f;
                _nextArenaDraftQuirkApplyAttemptTime = 0f;
                _arenaEnemyDraftApplyDeadline = Time.unscaledTime + 45f;
                _nextArenaEnemyDraftApplyAttemptTime = 0f;
                _arenaReturnMode = GameModeMgr.CurrentMode == GameModeType.COMBAT || GameModeMgr.CurrentMode == GameModeType.RESULTS
                    ? GameModeType.DRIVING
                    : GameModeMgr.CurrentMode;
                _arenaLastLaunchBattleConfigId = scenarioData.CurrentBattleConfiguration.m_Id;
                _nextArenaDebugControlSuppressTime = 0f;
                _arenaStatus = "Launching combat: " + scenarioData.CurrentBattleConfiguration.m_Id;
                HostLog.Write("[arena] Controlled combat launch: battle=" +
                    scenarioData.CurrentBattleConfiguration.m_Id +
                    ", arena=" + (scenarioData.BackgroundSceneName ?? "[auto]") +
                    ", returnMode=" + (_arenaReturnMode == null ? "[none]" : _arenaReturnMode.GetName()) +
                    ", torch=" + GetArenaTorchDisplaySummary() +
                    ", nativeLaunchPrefs=memory/" + _arenaPendingNativeLaunchPrefsLines.Length +
                    ". Official battle test controls launch-enabled, then suppressed after combat enters; DebugActorPlus edits suppressed during arena combat. " +
                    BuildArenaLaunchSnapshot());

                Singleton<GameTypeMgr>.Instance.SetCombatScenario(scenarioData, true);
                Singleton<GameModeMgr>.Instance.SetMode(GameModeType.COMBAT, false, null, null, false, false);
            }
            catch (Exception ex)
            {
                _arenaPendingLaunch = false;
                _arenaPendingDraftSkillApply = false;
                _arenaPendingEnemyDraftApply = false;
                ReleaseArenaBattleModifierOverride("launch exception");
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                _arenaStatus = "Launch failed: " + inner.GetType().Name + ": " + inner.Message;
                HostLog.Write("[arena] Launch failed for battle=" + ((_arenaBattleConfigId ?? string.Empty).Trim()) +
                    ", arena=" + ((_arenaCombatArenaId ?? string.Empty).Trim()) +
                    ", nativeLaunchPrefs=memory" +
                    ": " + ex);
            }
        }

        private void PollArenaDebugControlSuppression()
        {
            if (!_arenaDebugControlsSuppressed)
            {
                return;
            }

            GameModeType currentMode = Singleton<GameModeMgr>.HasInstance()
                ? GameModeMgr.CurrentMode
                : GameModeType.UNDETERMINED;

            if (!_arenaDebugControlsEnteredCombat)
            {
                if (currentMode == GameModeType.COMBAT)
                {
                    _arenaDebugControlsEnteredCombat = true;
                    HostLog.Write("[arena] Combat entered; suppressing official battle test controls.");
                }
                else
                {
                    return;
                }
            }

            if (currentMode != GameModeType.COMBAT && !_arenaPendingLaunch)
            {
                _arenaDebugControlsSuppressed = false;
                _arenaDebugControlsEnteredCombat = false;
                HostLog.Write("[arena] Debug control suppression released.");
                return;
            }

            if (Time.unscaledTime < _nextArenaDebugControlSuppressTime)
            {
                return;
            }

            _nextArenaDebugControlSuppressTime = Time.unscaledTime + 0.5f;

            try
            {
                TextBasedEditorPrefsBaseType.BATTLE_TEST_CONTROLS.SetValue(false);
            }
            catch
            {
            }

        }

        private bool TryRebuildArenaPartyFromDraft(out int partyCount, out string error)
        {
            partyCount = 0;
            error = string.Empty;

            if (!Singleton<GameTypeMgr>.HasInstance() || Singleton<GameTypeMgr>.Instance.RosterManager == null)
            {
                error = "roster manager is not ready";
                return false;
            }

            if (!TryGetArenaHeroDraftForLaunch(
                    out List<string> heroIds,
                    out _,
                    out _,
                    out error))
            {
                return false;
            }

            RosterManager roster = Singleton<GameTypeMgr>.Instance.RosterManager;
            try
            {
                TryInvokeArenaRosterAddMissingHeroes(roster);
                roster.SetAllStatuses(RosterStatusType.PARTY, RosterStatusType.IDLE, null);

                MethodInfo buildTestParty = typeof(RosterManager).GetMethod(
                    "BuildTestParty",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (buildTestParty == null)
                {
                    error = "RosterManager.BuildTestParty was not found";
                    return false;
                }

                string partyString = string.Join(",", heroIds.ToArray());
                buildTestParty.Invoke(roster, new object[] { partyString });

                List<ActorInstance> party = GetArenaCurrentPartyActors();
                partyCount = party.Count;
                if (party.Count != heroIds.Count)
                {
                    error = "rebuilt party count does not match draft; expected=" +
                        heroIds.Count +
                        ", actual=" + party.Count +
                        ", actors=" + string.Join(",", party.Select(actor => actor == null ? "[null]" : actor.ActorDataId ?? "[actor]").ToArray());
                    return false;
                }

                if (!DoesArenaPartyMatchDraft(party))
                {
                    error = "rebuilt party does not match draft; expected=" +
                        string.Join(",", heroIds.ToArray()) +
                        ", actual=" + string.Join(",", party.Select(actor => actor == null ? "[null]" : actor.ActorDataId ?? "[actor]").ToArray());
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                error = inner.GetType().Name + ": " + inner.Message;
                return false;
            }
        }

        private static void TryInvokeArenaRosterAddMissingHeroes(RosterManager roster)
        {
            if (roster == null)
            {
                return;
            }

            try
            {
                MethodInfo addMissingHeroes = typeof(RosterManager).GetMethod(
                    "AddMissingHeroesToRoster",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (addMissingHeroes != null)
                {
                    addMissingHeroes.Invoke(roster, new object[] { true, true, null });
                }
            }
            catch
            {
            }
        }

        private bool TryPreloadArenaCustomEnemyDraft(out string error)
        {
            error = string.Empty;
            if (!HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots))
            {
                return true;
            }

            if (!TryGetArenaEnemyHeroDraftForLaunch(out List<string> actorIds, out _, out _, out error))
            {
                return false;
            }

            if (!SingletonMonoBehaviour<ActorCreateGameObjectBhv>.HasInstance(false))
            {
                error = "actor factory is not ready";
                return false;
            }

            try
            {
                foreach (string actorId in actorIds.Distinct(StringComparer.Ordinal))
                {
                    SingletonMonoBehaviour<ActorCreateGameObjectBhv>.Instance.AddPreloadForActor(actorId, true);
                }

                HostLog.Write("[arena] Custom enemy hero preloads requested: " + string.Join(",", actorIds.ToArray()) + ".");
                return true;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                error = inner.GetType().Name + ": " + inner.Message;
                return false;
            }
        }

        private void PollArenaDraftSkillApply()
        {
            if (!_arenaPendingDraftSkillApply)
            {
                return;
            }

            if (Time.unscaledTime < _nextArenaDraftSkillApplyAttemptTime)
            {
                return;
            }

            _nextArenaDraftSkillApplyAttemptTime = Time.unscaledTime + 0.5f;

            if (Time.unscaledTime >= _arenaDraftSkillApplyDeadline)
            {
                _arenaPendingDraftSkillApply = false;
                HostLog.Write("[arena] Timed out applying draft skills; party did not match the Arena draft in time.");
                return;
            }

            if (!Singleton<GameModeMgr>.HasInstance() || GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return;
            }

            List<ActorInstance> party = GetArenaCombatTeamActors(0);
            if (!DoesArenaActorListMatchDraft(party, _arenaHeroDraftSlots))
            {
                return;
            }

            if (!TryApplyArenaDraftToActors(party, _arenaHeroDraftSlots, out int changedActors, out string error))
            {
                _arenaPendingDraftSkillApply = false;
                HostLog.Write("[arena] Failed to apply draft skills after combat entry: " + error + ".");
                return;
            }

            _arenaPendingDraftSkillApply = false;
            HostLog.Write("[arena] Draft skills applied after combat entry; actorsChanged=" + changedActors + ".");
        }

        private bool TryApplyArenaDraftSkillsToParty(IList<ActorInstance> party, out int changedActors, out string error)
        {
            if (!DoesArenaPartyMatchDraft(party))
            {
                changedActors = 0;
                error = "current party does not match the Arena draft";
                return false;
            }

            return TryApplyArenaDraftToActors(party, _arenaHeroDraftSlots, out changedActors, out error);
        }

        private bool TryApplyArenaDraftToActors(
            IList<ActorInstance> actors,
            ArenaHeroDraftSlot[] slots,
            out int changedActors,
            out string error)
        {
            changedActors = 0;
            error = string.Empty;
            if (!DoesArenaActorListMatchDraft(actors, slots))
            {
                error = "actor list does not match the Arena draft";
                return false;
            }

            int configuredCount = CountConfiguredArenaDraftActors(slots);
            for (int i = 0; i < configuredCount && i < actors.Count; i++)
            {
                ArenaHeroDraftSlot slot = slots[i];
                ActorInstance actor = actors[i];
                bool pathChanged;
                bool actorChanged;
                bool equipmentChanged;
                string actorError;
                if (!TryApplyArenaDraftPath(actor, slot, out pathChanged, out actorError))
                {
                    error = "slot " + (i + 1) + ": " + actorError;
                    return false;
                }

                if (!TryApplyArenaDraftSkills(actor, slot, out actorChanged, out actorError))
                {
                    error = "slot " + (i + 1) + ": " + actorError;
                    return false;
                }

                if (!TryApplyArenaDraftEquipment(actor, slot, out equipmentChanged, out actorError))
                {
                    error = "slot " + (i + 1) + ": " + actorError;
                    return false;
                }

                if (pathChanged || actorChanged || equipmentChanged)
                {
                    changedActors++;
                }

                HostLog.Write("[arena] Draft slot " + (i + 1) + " applied to " +
                    DescribeArenaActor(actor) +
                    " team=" + (actor == null || !actor.GetIsTeamPositionSet() ? "[unset]" : actor.TeamIndex + ":" + actor.TeamPosition) +
                    ", path=" + (string.IsNullOrWhiteSpace(slot.PathId) ? GetArenaDefaultPathId(slot.ActorId) : slot.PathId.Trim()) +
                    ", skills=" + string.Join(",", slot.SkillIds.ToArray()) +
                    ", combatItem=" + (string.IsNullOrWhiteSpace(slot.CombatItemId) ? "[none]" : slot.CombatItemId.Trim()) +
                    ", trinkets=" + (slot.TrinketIds.Count == 0 ? "[none]" : string.Join(",", slot.TrinketIds.ToArray())) + ".");
            }

            return true;
        }

        private bool TryApplyArenaDraftPath(ActorInstance actor, ArenaHeroDraftSlot slot, out bool changed, out string error)
        {
            changed = false;
            error = string.Empty;
            if (actor == null)
            {
                error = "actor is missing";
                return false;
            }

            if (slot == null)
            {
                error = "draft slot is missing";
                return false;
            }

            string pathId = (slot.PathId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(pathId))
            {
                pathId = GetArenaDefaultPathId(slot.ActorId);
            }

            if (string.IsNullOrWhiteSpace(pathId))
            {
                return true;
            }

            ActorDataPath path = TryGetArenaActorDataPath(pathId);
            if (path == null)
            {
                error = "path not found: " + pathId;
                return false;
            }

            if (actor.ActorDataPath != null && string.Equals(actor.ActorDataPath.Id, path.Id, StringComparison.Ordinal))
            {
                return true;
            }

            actor.SetActorPath(path, false);
            changed = true;
            return true;
        }

        private bool TryApplyArenaDraftSkills(ActorInstance actor, ArenaHeroDraftSlot slot, out bool changed, out string error)
        {
            changed = false;
            error = string.Empty;
            if (slot == null)
            {
                error = "draft slot is missing";
                return false;
            }

            DD2DebugDemoCore.Runtime.ActorSkillLoadoutService skillLoadoutService =
                new DD2DebugDemoCore.Runtime.ActorSkillLoadoutService();
            return skillLoadoutService.TryApplyDraftSkills(actor, slot.SkillIds, out changed, out error);
        }

        private static bool TryApplyArenaDraftEquipment(ActorInstance actor, ArenaHeroDraftSlot slot, out bool changed, out string error)
        {
            changed = false;
            error = string.Empty;
            if (actor == null)
            {
                error = "actor is missing";
                return false;
            }

            if (slot == null)
            {
                error = "draft slot is missing";
                return false;
            }

            DD2DebugDemoCore.Runtime.ActorEquipmentService equipmentService =
                new DD2DebugDemoCore.Runtime.ActorEquipmentService();
            if (!string.IsNullOrWhiteSpace(slot.CombatItemId))
            {
                if (!equipmentService.TryApplyCombatItem(actor, slot.CombatItemId.Trim(), out error))
                {
                    return false;
                }

                changed = true;
            }

            foreach (string trinketId in slot.TrinketIds.Where(id => !string.IsNullOrWhiteSpace(id)).Take(2))
            {
                if (!equipmentService.TryApplyTrinket(actor, trinketId.Trim(), out error))
                {
                    return false;
                }

                changed = true;
            }

            return true;
        }

        private static string DescribeArenaActor(ActorInstance actor)
        {
            if (actor == null)
            {
                return "[actor]";
            }

            return (actor.ActorDataId ?? "[actor]") + "/" + actor.ActorGuid;
        }

        private void PollArenaDraftQuirkApply()
        {
            if (!_arenaPendingDraftQuirkApply)
            {
                return;
            }

            if (Time.unscaledTime < _nextArenaDraftQuirkApplyAttemptTime)
            {
                return;
            }

            _nextArenaDraftQuirkApplyAttemptTime = Time.unscaledTime + 0.5f;

            if (Time.unscaledTime >= _arenaDraftQuirkApplyDeadline)
            {
                _arenaPendingDraftQuirkApply = false;
                HostLog.Write("[arena] Timed out applying draft quirks; party did not match the Arena draft in time.");
                return;
            }

            if (!Singleton<GameModeMgr>.HasInstance() || GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return;
            }

            List<ActorInstance> party = GetArenaCombatTeamActors(0);
            if (!DoesArenaActorListMatchDraft(party, _arenaHeroDraftSlots))
            {
                return;
            }

            int applied = ApplyArenaDraftQuirksToActors(party, _arenaHeroDraftSlots);
            _arenaPendingDraftQuirkApply = false;
            HostLog.Write("[arena] Draft quirks applied after combat entry: " + applied + " new quirk(s).");
        }

        private void PollArenaEnemyDraftApply()
        {
            if (!_arenaPendingEnemyDraftApply)
            {
                return;
            }

            if (Time.unscaledTime < _nextArenaEnemyDraftApplyAttemptTime)
            {
                return;
            }

            _nextArenaEnemyDraftApplyAttemptTime = Time.unscaledTime + 0.5f;

            if (Time.unscaledTime >= _arenaEnemyDraftApplyDeadline)
            {
                _arenaPendingEnemyDraftApply = false;
                HostLog.Write("[arena] Timed out applying custom enemy hero draft; team 1 did not match in time.");
                return;
            }

            if (!Singleton<GameModeMgr>.HasInstance() || GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return;
            }

            List<ActorInstance> enemies = GetArenaCombatTeamActors(1);
            if (!DoesArenaActorListMatchDraft(enemies, _arenaEnemyHeroDraftSlots))
            {
                return;
            }

            if (!TryApplyArenaDraftToActors(enemies, _arenaEnemyHeroDraftSlots, out int changedActors, out string error))
            {
                _arenaPendingEnemyDraftApply = false;
                HostLog.Write("[arena] Failed to apply custom enemy hero draft after combat entry: " + error + ".");
                return;
            }

            int appliedQuirks = ApplyArenaDraftQuirksToActors(enemies, _arenaEnemyHeroDraftSlots);
            _arenaPendingEnemyDraftApply = false;
            HostLog.Write("[arena] Custom enemy hero draft applied after combat entry; actorsChanged=" +
                changedActors + ", quirks=" + appliedQuirks + ".");
        }

        private static int ApplyArenaDraftQuirksToActors(IList<ActorInstance> actors, ArenaHeroDraftSlot[] slots)
        {
            int applied = 0;
            if (actors == null || slots == null)
            {
                return applied;
            }

            int configuredCount = CountConfiguredArenaDraftActors(slots);
            for (int i = 0; i < configuredCount && i < actors.Count; i++)
            {
                ArenaHeroDraftSlot slot = slots[i];
                ActorInstance actor = actors[i];
                foreach (string quirkId in GetArenaDraftQuirkIds(slot).Distinct(StringComparer.Ordinal))
                {
                    if (ApplyArenaDraftQuirk(actor, quirkId))
                    {
                        applied++;
                    }
                }
            }

            return applied;
        }

        private bool DoesArenaPartyMatchDraft(IList<ActorInstance> party)
        {
            return DoesArenaActorListMatchDraft(party, _arenaHeroDraftSlots);
        }

        private static bool DoesArenaActorListMatchDraft(IList<ActorInstance> actors, ArenaHeroDraftSlot[] slots)
        {
            if (actors == null || slots == null)
            {
                return false;
            }

            int configuredCount = CountConfiguredArenaDraftActors(slots);
            if (configuredCount < 1 || actors.Count < configuredCount)
            {
                return false;
            }

            for (int i = 0; i < configuredCount; i++)
            {
                string expected = (slots[i].ActorId ?? string.Empty).Trim();
                string actual = actors[i] == null ? string.Empty : (actors[i].ActorDataId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(expected) ||
                    !string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ApplyArenaDraftQuirk(ActorInstance actor, string quirkId)
        {
            if (actor == null || string.IsNullOrWhiteSpace(quirkId))
            {
                return false;
            }

            try
            {
                QuirkDefinition definition = TryGetArenaQuirkDefinition(quirkId);
                if (definition == null || actor.QuirkContainer == null)
                {
                    return false;
                }

                if (actor.QuirkContainer.GetHasInstanceWithId(definition.m_Id, false, 0U))
                {
                    return false;
                }

                actor.QuirkContainer.Add(definition, SourceType.DEBUG, null, 0U);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[arena] Failed to apply draft quirk " + quirkId + ": " + ex.Message);
                return false;
            }
        }

        private void PollArenaResultBypass()
        {
            if (!_arenaResultBypassArmed && !_arenaPostBattleMainMenuReturnPending && !_arenaPostBattleMainMenuReturnRequested)
            {
                if (_arenaBattleModifierOverrideArmed && !_arenaPendingLaunch && !_arenaDebugControlsSuppressed)
                {
                    ReleaseArenaBattleModifierOverride("arena guard inactive");
                }

                return;
            }

            if (!Singleton<GameModeMgr>.HasInstance())
            {
                return;
            }

            GameModeMgr gameModeMgr = Singleton<GameModeMgr>.Instance;
            GameModeType currentMode = GameModeMgr.CurrentMode;
            if (_arenaResultBypassArmed && currentMode == GameModeType.COMBAT && !_arenaResultBypassCombatEntered)
            {
                _arenaResultBypassCombatEntered = true;
                _arenaResultLoadingFirstSeenTime = 0f;
                HostLog.Write("[arena] Result bypass guard observed combat entry.");
            }

            if (_arenaWaitingForNextBattle)
            {
                if (currentMode == GameModeType.MAIN_MENU)
                {
                    _arenaResultBypassArmed = false;
                    _arenaResultBypassLogged = false;
                    _arenaResultBypassCombatEntered = false;
                    _arenaWaitingForNextBattle = false;
                    _arenaNextBattleWaitStartTime = 0f;
                    _arenaStatus = "Arena next-wave wait ended at main menu.";
                    ReleaseArenaBattleModifierOverride("next-wave wait ended at main menu");
                    HostLog.Write("[arena] Arena next-wave wait ended because current mode is MAIN_MENU.");
                    return;
                }

                if (currentMode == GameModeType.COMBAT)
                {
                    _arenaWaitingForNextBattle = false;
                    _arenaResultBypassCombatEntered = true;
                    _arenaResultBypassLogged = false;
                    _arenaResultsModeFirstSeenTime = 0f;
                    _arenaResultLoadingFirstSeenTime = 0f;
                    _arenaNextBattleWaitStartTime = 0f;
                    _arenaStatus = "Arena next wave entered.";
                    HostLog.Write("[arena] Arena next wave entered; result guard re-armed for final result.");
                    return;
                }

                if (_arenaNextBattleWaitStartTime <= 0f)
                {
                    _arenaNextBattleWaitStartTime = Time.unscaledTime;
                    _nextArenaNextBattleWaitLogTime = 0f;
                }

                if (Time.unscaledTime >= _nextArenaNextBattleWaitLogTime)
                {
                    _nextArenaNextBattleWaitLogTime = Time.unscaledTime + 5f;
                    HostLog.Write("[arena] Waiting for official/custom next arena wave; mode=" +
                        currentMode.GetName() +
                        ", next=" + gameModeMgr.GetNextMode().GetName() +
                        ", changing=" + gameModeMgr.IsChangingState() +
                        ", elapsed=" + (Time.unscaledTime - _arenaNextBattleWaitStartTime).ToString("0.0", CultureInfo.InvariantCulture) + "s.");
                }

                return;
            }

            if (_arenaPostBattleMainMenuReturnRequested && currentMode == GameModeType.MAIN_MENU)
            {
                _arenaResultBypassArmed = false;
                _arenaResultBypassLogged = false;
                _arenaResultBypassCombatEntered = false;
                _arenaWaitingForNextBattle = false;
                _arenaPostBattleMainMenuReturnPending = false;
                _arenaPostBattleMainMenuReturnRequested = false;
                _arenaResultLoadingFirstSeenTime = 0f;
                _arenaStatus = "Arena returned to main menu.";
                ReleaseArenaBattleModifierOverride("arena returned to main menu");
                HostLog.Write("[arena] Arena post-battle main-menu return completed.");
                return;
            }

            if (_arenaResultBypassArmed &&
                !_arenaResultBypassCombatEntered &&
                !_arenaPostBattleMainMenuReturnPending &&
                !_arenaPostBattleMainMenuReturnRequested)
            {
                if (Time.unscaledTime >= _arenaResultBypassDeadline)
                {
                    _arenaResultBypassArmed = false;
                    _arenaResultBypassLogged = false;
                    _arenaResultBypassCombatEntered = false;
                    _arenaWaitingForNextBattle = false;
                    ReleaseArenaBattleModifierOverride("result guard timed out before combat entry");
                    HostLog.Write("[arena] Result bypass guard timed out before combat entry; releasing guard. mode=" +
                        currentMode.GetName() + ", next=" + gameModeMgr.GetNextMode().GetName() + ".");
                }

                return;
            }

            if (_arenaPostBattleMainMenuReturnPending)
            {
                if (Time.unscaledTime < _arenaPostBattleMainMenuReturnTime)
                {
                    return;
                }

                if (gameModeMgr.IsChangingState())
                {
                    if (Time.unscaledTime >= _nextArenaPostBattleMainMenuLogTime)
                    {
                        _nextArenaPostBattleMainMenuLogTime = Time.unscaledTime + 3f;
                        HostLog.Write("[arena] Waiting to return arena fight to main menu; mode change already active, mode=" +
                            currentMode.GetName() + ", next=" + gameModeMgr.GetNextMode().GetName() + ".");
                    }

                    return;
                }

                if (currentMode != GameModeType.MAIN_MENU && currentMode != GameModeType.LOADING)
                {
                    _arenaPostBattleMainMenuReturnPending = false;
                    _arenaPostBattleMainMenuReturnRequested = true;
                    _arenaResultBypassArmed = false;
                    _arenaResultBypassCombatEntered = false;
                    _arenaWaitingForNextBattle = false;
                    _arenaStatus = "Arena fight complete; returning to main menu.";
                    HostLog.Write("[arena] Returning custom arena fight to MAIN_MENU from " +
                        currentMode.GetName() +
                        " before normal DD2 combat reward/map flow.");
                    RequestArenaMainMenuReturn(gameModeMgr, "battle-result", false);
                    return;
                }
            }

            if (currentMode == GameModeType.LOADING &&
                (_arenaPostBattleMainMenuReturnRequested ||
                 _arenaPostBattleMainMenuReturnPending ||
                 _arenaResultsModeFirstSeenTime > 0f))
            {
                if (_arenaResultLoadingFirstSeenTime <= 0f)
                {
                    _arenaResultLoadingFirstSeenTime = Time.unscaledTime;
                    _nextArenaResultLoadingLogTime = 0f;
                }

                if (Time.unscaledTime >= _nextArenaResultLoadingLogTime)
                {
                    _nextArenaResultLoadingLogTime = Time.unscaledTime + 3f;
                    HostLog.Write("[arena] Waiting for arena post-result loading to leave; next=" +
                        gameModeMgr.GetNextMode().GetName() +
                        ", changing=" + gameModeMgr.IsChangingState() +
                        ", elapsed=" + (Time.unscaledTime - _arenaResultLoadingFirstSeenTime).ToString("0.0", CultureInfo.InvariantCulture) + "s.");
                }

                if (Time.unscaledTime - _arenaResultLoadingFirstSeenTime >= 8f)
                {
                    _arenaPostBattleMainMenuReturnPending = false;
                    _arenaPostBattleMainMenuReturnRequested = true;
                    _arenaResultBypassArmed = false;
                    _arenaResultBypassCombatEntered = false;
                    _arenaWaitingForNextBattle = false;
                    _arenaStatus = "Arena loading watchdog returning to main menu.";
                    HostLog.Write("[arena] Arena post-result loading watchdog forcing MAIN_MENU.");
                    RequestArenaMainMenuReturn(gameModeMgr, "loading-watchdog", true);
                    _arenaResultLoadingFirstSeenTime = Time.unscaledTime;
                }

                return;
            }

            if (_arenaPostBattleMainMenuReturnRequested)
            {
                if (Time.unscaledTime >= _nextArenaPostBattleMainMenuLogTime)
                {
                    _nextArenaPostBattleMainMenuLogTime = Time.unscaledTime + 3f;
                    HostLog.Write("[arena] Waiting for arena main-menu return to complete; mode=" +
                        currentMode.GetName() + ", next=" + gameModeMgr.GetNextMode().GetName() + ".");
                }

                return;
            }

            if (currentMode == GameModeType.COMBAT || currentMode == GameModeType.LOADING || _arenaPendingLaunch)
            {
                return;
            }

            if (currentMode == GameModeType.RESULTS)
            {
                if (_arenaResultsModeFirstSeenTime <= 0f)
                {
                    _arenaResultsModeFirstSeenTime = Time.unscaledTime;
                }

                if (Time.unscaledTime < _nextArenaResultBypassAttemptTime)
                {
                    return;
                }

                _nextArenaResultBypassAttemptTime = Time.unscaledTime + 1f;

                bool lootActive = SingletonMonoBehaviour<CommonUiBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<CommonUiBhv>.Instance.IsLootActive;
                if (lootActive)
                {
                    string message = "adapter unavailable";
                    if (_resultSyncAdapter != null && _resultSyncAdapter.TryBypassActiveLootWindowForArena(out message))
                    {
                        _arenaStatus = "Arena result loot bypassed; returning after results timeline.";
                        HostLog.Write("[arena] Result loot bypassed for battle=" + _arenaLastLaunchBattleConfigId +
                            ": " + message + ".");
                    }
                    else if (!_arenaResultBypassLogged)
                    {
                        _arenaResultBypassLogged = true;
                        HostLog.Write("[arena] Waiting to bypass result loot: " + (message ?? "adapter unavailable") + ".");
                    }

                    return;
                }

                if (!_arenaResultBypassLogged)
                {
                    _arenaResultBypassLogged = true;
                    HostLog.Write("[arena] Results mode has no active loot window; waiting briefly for DD2 timeline return.");
                }

                if (Time.unscaledTime - _arenaResultsModeFirstSeenTime >= 4f && !gameModeMgr.IsChangingState())
                {
                    _arenaStatus = "Arena results returning to main menu.";
                    HostLog.Write("[arena] Forcing arena result return to MAIN_MENU after results mode remained active without loot.");
                    _arenaPostBattleMainMenuReturnRequested = true;
                    _arenaResultBypassArmed = false;
                    _arenaResultBypassCombatEntered = false;
                    _arenaWaitingForNextBattle = false;
                    RequestArenaMainMenuReturn(gameModeMgr, "results-timeout", false);
                }

                if (Time.unscaledTime >= _arenaResultBypassDeadline)
                {
                    _arenaResultBypassArmed = false;
                    _arenaResultBypassCombatEntered = false;
                    _arenaWaitingForNextBattle = false;
                    ReleaseArenaBattleModifierOverride("result bypass timeout expired");
                    HostLog.Write("[arena] Result bypass timeout expired; releasing arena result guard.");
                }

                return;
            }

            if (_arenaResultBypassArmed && _arenaResultBypassCombatEntered && currentMode != GameModeType.MAIN_MENU)
            {
                _arenaPostBattleMainMenuReturnPending = false;
                _arenaPostBattleMainMenuReturnRequested = true;
                _arenaResultBypassArmed = false;
                _arenaResultBypassCombatEntered = false;
                _arenaWaitingForNextBattle = false;
                _arenaStatus = "Arena fight left combat; returning to main menu.";
                HostLog.Write("[arena] Arena combat left COMBAT without a handled RESULTS state; forcing MAIN_MENU from " +
                    currentMode.GetName() + ".");
                RequestArenaMainMenuReturn(gameModeMgr, "left-combat", true);
                return;
            }

            _arenaResultBypassArmed = false;
            _arenaResultBypassLogged = false;
            _arenaResultBypassCombatEntered = false;
            _arenaWaitingForNextBattle = false;
            _arenaPostBattleMainMenuReturnPending = false;
            _arenaResultLoadingFirstSeenTime = 0f;
            ReleaseArenaBattleModifierOverride("result bypass released");
            HostLog.Write("[arena] Result bypass released; current mode=" + currentMode.GetName() + ".");
        }

        private void RequestArenaMainMenuReturn(GameModeMgr gameModeMgr, string reason, bool unloadEverything)
        {
            if (gameModeMgr == null)
            {
                return;
            }

            try
            {
                gameModeMgr.OnNextGameModeExitComplete(ClearArenaMapOnMainMenuExitComplete);
            }
            catch (Exception ex)
            {
                HostLog.Write("[arena] Failed to register map cleanup for main-menu return: " + ex.Message);
            }

            try
            {
                if (SingletonMonoBehaviour<NarrationMgr>.HasInstance(false))
                {
                    SingletonMonoBehaviour<NarrationMgr>.Instance.Stop();
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("[arena] Failed to stop narration before main-menu return: " + ex.Message);
            }

            HostLog.Write("[arena] Requesting MAIN_MENU return via cleanup path; reason=" +
                (string.IsNullOrWhiteSpace(reason) ? "unknown" : reason) +
                ", unloadEverything=" + unloadEverything +
                ", mode=" + GameModeMgr.CurrentMode.GetName() +
                ", next=" + gameModeMgr.GetNextMode().GetName() + ".");
            gameModeMgr.SetMode(GameModeType.MAIN_MENU, false, null, null, unloadEverything, false);
        }

        private static void ClearArenaMapOnMainMenuExitComplete(GameModeType exitingGameMode)
        {
            try
            {
                if (SingletonMonoBehaviour<MapMgrBhv>.HasInstance(false))
                {
                    SingletonMonoBehaviour<MapMgrBhv>.Instance.ClearGame();
                    HostLog.Write("[arena] Cleared map after arena main-menu return; exiting=" + exitingGameMode.GetName() + ".");
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("[arena] Map clear during main-menu return failed: " + ex.Message + ".");
            }
        }

        private void ArmArenaPostBattleMainMenuReturn(BattleResultPayload payload)
        {
            if (!_arenaResultBypassArmed || _arenaPostBattleMainMenuReturnPending || _arenaPostBattleMainMenuReturnRequested)
            {
                return;
            }

            _arenaResultBypassCombatEntered = true;
            if (payload != null &&
                payload.HasNextBattle &&
                payload.NextBattleConfigurationIndex >= 0 &&
                !payload.IsBattleSequenceComplete &&
                !payload.IsForceEnd &&
                !payload.IsRetreat)
            {
                _arenaWaitingForNextBattle = true;
                _arenaNextBattleWaitStartTime = Time.unscaledTime;
                _nextArenaNextBattleWaitLogTime = 0f;
                _arenaPostBattleMainMenuReturnPending = false;
                _arenaPostBattleMainMenuReturnRequested = false;
                _arenaResultsModeFirstSeenTime = 0f;
                _arenaResultLoadingFirstSeenTime = 0f;
                _arenaResultBypassLogged = false;
                _arenaStatus = "Arena wave complete; waiting for next wave.";
                HostLog.Write("[arena] Battle result received for arena wave=" +
                    (string.IsNullOrWhiteSpace(payload.CurrentBattleConfigurationId)
                        ? _arenaLastLaunchBattleConfigId
                        : payload.CurrentBattleConfigurationId) +
                    "; nextBattleIndex=" + payload.NextBattleConfigurationIndex +
                    ", sequenceComplete=" + payload.IsBattleSequenceComplete +
                    ". Allowing DD2 next-battle flow instead of returning to MAIN_MENU.");
                return;
            }

            _arenaWaitingForNextBattle = false;
            _arenaPostBattleMainMenuReturnPending = true;
            _arenaPostBattleMainMenuReturnTime = Time.unscaledTime + 0.25f;
            _nextArenaPostBattleMainMenuLogTime = 0f;
            _arenaStatus = "Arena battle result received; returning to main menu.";
            HostLog.Write("[arena] Battle result received for arena battle=" +
                (payload == null || string.IsNullOrWhiteSpace(payload.CurrentBattleConfigurationId)
                    ? _arenaLastLaunchBattleConfigId
                    : payload.CurrentBattleConfigurationId) +
                "; scheduling COMBAT/RESULTS -> MAIN_MENU return to avoid normal reward/map loading.");
        }

        private GameModeType GetArenaReturnMode()
        {
            if (_arenaReturnMode == null ||
                _arenaReturnMode == GameModeType.COMBAT ||
                _arenaReturnMode == GameModeType.RESULTS ||
                _arenaReturnMode == GameModeType.LOADING ||
                _arenaReturnMode == GameModeType.UNDETERMINED)
            {
                return GameModeType.DRIVING;
            }

            return _arenaReturnMode;
        }

        private bool TryGetArenaLaunchBattleSequenceConfigs(
            out List<BattleConfigurationDefinition> configs,
            out string error)
        {
            configs = new List<BattleConfigurationDefinition>();
            error = string.Empty;

            if (_arenaBattleSequenceIds.Count == 0)
            {
                BattleConfigurationDefinition config;
                if (!TryGetArenaBattleConfiguration(_arenaBattleConfigId, out config, out error))
                {
                    error = "invalid battle config: " + error;
                    return false;
                }

                configs.Add(config);
                return true;
            }

            for (int i = 0; i < _arenaBattleSequenceIds.Count; i++)
            {
                string id = (_arenaBattleSequenceIds[i] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    error = "sequence slot " + (i + 1) + " is empty.";
                    return false;
                }

                BattleConfigurationDefinition config;
                if (!TryGetArenaBattleConfiguration(id, out config, out error))
                {
                    error = "sequence slot " + (i + 1) + " invalid: " + error;
                    return false;
                }

                configs.Add(config);
            }

            return configs.Count > 0;
        }

        private bool TryValidateArenaBattleSequenceForLaunch(
            IReadOnlyList<BattleConfigurationDefinition> configs,
            out string error)
        {
            error = string.Empty;
            if (configs == null || configs.Count == 0)
            {
                error = "battle sequence is empty.";
                return false;
            }

            for (int i = 0; i < configs.Count; i++)
            {
                if (!TryValidateArenaBattleConfigForLaunch(configs[i], out error))
                {
                    error = "sequence wave " + (i + 1) + ": " + error;
                    return false;
                }
            }

            return true;
        }

        private string BuildArenaLaunchSequenceSummary()
        {
            if (_arenaBattleSequenceIds.Count == 0)
            {
                return "[selected preset]";
            }

            return string.Join(" -> ", _arenaBattleSequenceIds.ToArray());
        }

        private bool BuildArenaNativeLaunchPrefsLines(out string[] lines, out string error)
        {
            lines = null;
            error = string.Empty;

            List<BattleConfigurationDefinition> launchSequence;
            if (!TryGetArenaLaunchBattleSequenceConfigs(out launchSequence, out error))
            {
                return false;
            }

            BattleConfigurationDefinition config = launchSequence[0];
            if (!TryValidateArenaBattleSequenceForLaunch(launchSequence, out error))
            {
                return false;
            }

            if (!TryGetArenaHeroDraftForLaunch(
                    out List<string> heroIds,
                    out _,
                    out _,
                    out error))
            {
                error = "hero draft is not ready: " + error;
                return false;
            }

            if (!TryValidateArenaDraftItems(_arenaHeroDraftSlots, out error))
            {
                error = "hero item draft is not ready: " + error;
                return false;
            }

            if (!TryValidateArenaHeroDraftQuirks(out error))
            {
                error = "hero quirk draft is not ready: " + error;
                return false;
            }

            bool hasEnemyHeroDraft = HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots);
            List<string> enemyHeroIds = new List<string>();
            if (hasEnemyHeroDraft)
            {
                if (!TryGetArenaEnemyHeroDraftForLaunch(
                        out enemyHeroIds,
                        out _,
                        out _,
                        out error))
                {
                    error = "enemy hero draft is not ready: " + error;
                    return false;
                }

                if (!TryValidateArenaDraftItems(_arenaEnemyHeroDraftSlots, out error))
                {
                    error = "enemy hero item draft is not ready: " + error;
                    return false;
                }

                if (!TryValidateArenaHeroDraftQuirks(_arenaEnemyHeroDraftSlots, out error))
                {
                    error = "enemy hero quirk draft is not ready: " + error;
                    return false;
                }
            }

            List<string> startEffectIds = new List<string>();
            HashSet<string> seenStartEffects = new HashSet<string>(StringComparer.Ordinal);
            foreach (string effectId in _arenaHeroStartEffectIds)
            {
                string id = (effectId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id) || !seenStartEffects.Add(id))
                {
                    continue;
                }

                if (TryGetArenaEffectDefinition(id) == null)
                {
                    error = "hero start effect not found: " + id + ".";
                    return false;
                }

                startEffectIds.Add(id);
            }

            string bossModifierId = (_arenaBossModifierId ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(bossModifierId) &&
                TryGetArenaBossModifierDefinition(bossModifierId) == null)
            {
                error = "boss modifier not found: " + bossModifierId + ".";
                return false;
            }

            ArenaBattleAdvantageMode battleAdvantageMode = GetEffectiveArenaBattleAdvantageMode();
            string battleModifierId = (_arenaBattleModifierId ?? string.Empty).Trim();
            if (battleAdvantageMode == ArenaBattleAdvantageMode.Specific)
            {
                if (string.IsNullOrWhiteSpace(battleModifierId))
                {
                    error = "specific battle advantage is selected but no BattleModifier is chosen.";
                    return false;
                }

                if (TryGetArenaBattleModifierDefinition(battleModifierId) == null)
                {
                    error = "battle advantage modifier not found or not valid for monsters: " + battleModifierId + ".";
                    return false;
                }
            }

            string resolvedArena = GetArenaResolvedBackgroundSceneForLaunch(config);
            if (string.IsNullOrWhiteSpace(resolvedArena))
            {
                error = "battle config has no background scene and default arena fallback is empty.";
                return false;
            }

            string arenaOverride = (_arenaCombatArenaId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(arenaOverride) &&
                string.IsNullOrWhiteSpace(config.m_BackgroundSceneOverride))
            {
                HostLog.Write("[arena] Battle config " + config.m_Id +
                    " has no background override; using fallback arena " + resolvedArena + ".");
            }

            List<string> prefs = new List<string>();
            AddArenaNativeLaunchPref(prefs, "demo_build", "True");
            AddArenaNativeLaunchPref(prefs, "disable_load_profile", "True");
            AddArenaNativeLaunchPref(prefs, "disable_save_profile", "True");
            AddArenaNativeLaunchPref(prefs, "run_test_game_mode", "combat");
            AddArenaNativeLaunchPref(prefs, "battle_test_battle_configuration", config.m_Id);
            AddArenaNativeLaunchPref(prefs, "battle_test_combat_arena", resolvedArena);
            AddArenaNativeLaunchPref(prefs, "battle_test_controls", "True");
            AddArenaNativeLaunchPref(prefs, "battle_test_battle_modifier",
                battleAdvantageMode == ArenaBattleAdvantageMode.Specific ? battleModifierId : string.Empty);
            AddArenaNativeLaunchPref(prefs, "battle_test_roll_battle_modifier",
                battleAdvantageMode == ArenaBattleAdvantageMode.Random ? "True" : "False");
            AddArenaNativeLaunchPref(prefs, "battle_test_team_0_controller", "INPUT");
            AddArenaNativeLaunchCsv(prefs, "battle_test_team_0", heroIds);

            if (hasEnemyHeroDraft)
            {
                AddArenaNativeLaunchPref(prefs, "battle_test_team_1_controller", "RANDOM");
                AddArenaNativeLaunchCsv(prefs, "battle_test_team_1", enemyHeroIds);
            }

            if (startEffectIds.Count > 0)
            {
                AddArenaNativeLaunchCsv(prefs, "hero_test_start_effect", startEffectIds);
                AddArenaNativeLaunchPref(prefs, "hero_test_start_effect_source_type", "rest_item");
                AddArenaNativeLaunchPref(prefs, "hero_test_start_effect_source_id", "dd2steammp_arena");
            }

            if (!string.IsNullOrWhiteSpace(bossModifierId))
            {
                AddArenaNativeLaunchPref(prefs, "run_test_boss_modifier", bossModifierId);
            }

            lines = prefs.ToArray();
            return true;
        }

        private static void AddArenaNativeLaunchPref(List<string> prefs, string key, string value)
        {
            if (prefs == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            prefs.Add(key.Trim() + "-" + ((value ?? string.Empty).Trim()));
        }

        private static void AddArenaNativeLaunchCsv(List<string> prefs, string key, IEnumerable<string> values)
        {
            AddArenaNativeLaunchPref(
                prefs,
                key,
                string.Join(",", (values ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .ToArray()));
        }

        private static void ClearArenaNativeLoadoutEditorPrefs()
        {
            try
            {
                TextBasedEditorPrefsBaseType.HERO_TEST_PATHS.ClearValue();
                TextBasedEditorPrefsBaseType.HERO_TEST_START_SKILLS.ClearValue();
                TextBasedEditorPrefsBaseType.HERO_TEST_START_COMBAT_ITEM.ClearValue();
                TextBasedEditorPrefsBaseType.HERO_TEST_START_TRINKETS.ClearValue();
                TextBasedEditorPrefsBaseType.HERO_TEST_QUIRKS_PER_HERO.ClearValue();
            }
            catch
            {
            }
        }

        private bool TryValidateArenaBattleConfigForLaunch(BattleConfigurationDefinition config, out string error)
        {
            error = string.Empty;
            if (config == null)
            {
                error = "battle config is null.";
                return false;
            }

            bool customEnemyTeam = HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots);
            if (!customEnemyTeam && (config.m_EnemyActors == null || config.m_EnemyActors.Count == 0))
            {
                error = "battle config has no enemy actors.";
                return false;
            }

            if (!customEnemyTeam &&
                (ArenaPresetContainsExcludedActor(config.m_EnemyActors) ||
                 ArenaPresetReferenceContainsExcludedActor(BuildArenaBattleConfigReferenceIds(config, 32))))
            {
                error = "battle config contains coven_sister_a/coven_sister_b, which are excluded for now.";
                return false;
            }

            string resolvedArena = GetArenaResolvedBackgroundSceneForLaunch(config);
            if (string.IsNullOrWhiteSpace(resolvedArena))
            {
                error = "battle config has no background scene.";
                return false;
            }

            return true;
        }

        private bool IsArenaLaunchReady(out string reason)
        {
            if (!TryGetArenaLaunchBattleSequenceConfigs(out List<BattleConfigurationDefinition> launchSequence, out string battleError))
            {
                reason = "battle sequence is not valid: " + battleError;
                return false;
            }

            if (!TryValidateArenaBattleSequenceForLaunch(launchSequence, out battleError))
            {
                reason = "battle sequence is not launchable: " + battleError;
                return false;
            }

            if (!TryGetArenaHeroDraftForLaunch(out _, out _, out _, out string heroError))
            {
                reason = "hero draft is not ready: " + heroError;
                return false;
            }

            if (!TryValidateArenaDraftItems(_arenaHeroDraftSlots, out string itemError))
            {
                reason = "hero item draft is not ready: " + itemError;
                return false;
            }

            if (HasArenaHeroDraftAnyActor(_arenaEnemyHeroDraftSlots))
            {
                if (!TryGetArenaEnemyHeroDraftForLaunch(out _, out _, out _, out string enemyHeroError))
                {
                    reason = "enemy hero draft is not ready: " + enemyHeroError;
                    return false;
                }

                if (!TryValidateArenaDraftItems(_arenaEnemyHeroDraftSlots, out string enemyItemError))
                {
                    reason = "enemy hero item draft is not ready: " + enemyItemError;
                    return false;
                }
            }

            if (!Singleton<GameModeMgr>.HasInstance())
            {
                reason = "GameModeMgr is not ready";
                return false;
            }

            GameModeMgr gameModeMgr = Singleton<GameModeMgr>.Instance;
            if (gameModeMgr.IsChangingState())
            {
                reason = "game mode is changing";
                return false;
            }

            GameModeType mode = GameModeMgr.CurrentMode;
            if (mode == GameModeType.MAIN_MENU)
            {
                reason = "start or continue a run first";
                return false;
            }

            if (mode == GameModeType.COMBAT)
            {
                reason = "already in combat";
                return false;
            }

            if (mode == GameModeType.LOADING || mode == GameModeType.UNDETERMINED)
            {
                reason = "current mode is " + mode.GetName();
                return false;
            }

            if (!Singleton<GameTypeMgr>.HasInstance() || !Singleton<GameTypeMgr>.Instance.IsGameTypeStarted)
            {
                reason = "game type has not started";
                return false;
            }

            if (!SingletonMonoBehaviour<CampaignBhv>.HasInstance(false) ||
                !SingletonMonoBehaviour<CampaignBhv>.Instance.IsCampaignStarted)
            {
                reason = "campaign has not started";
                return false;
            }

            if (!SingletonMonoBehaviour<RunBhv>.HasInstance(false) ||
                !SingletonMonoBehaviour<RunBhv>.Instance.IsRunStarted)
            {
                reason = "run has not started";
                return false;
            }

            if (!SingletonMonoBehaviour<MapMgrBhv>.HasInstance(false))
            {
                reason = "map manager is not ready";
                return false;
            }

            if (!SingletonMonoBehaviour<ActorCreateGameObjectBhv>.HasInstance(false))
            {
                reason = "actor factory is not ready";
                return false;
            }

            if (RedHookSceneManagerBhv.isLoading)
            {
                reason = "scene manager is loading";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private string BuildArenaLaunchSnapshot()
        {
            List<string> parts = new List<string>();
            if (Singleton<GameModeMgr>.HasInstance())
            {
                GameModeMgr mgr = Singleton<GameModeMgr>.Instance;
                parts.Add("mode=" + GameModeMgr.CurrentMode.GetName());
                parts.Add("next=" + mgr.GetNextMode().GetName());
                parts.Add("changing=" + mgr.IsChangingState());
            }
            else
            {
                parts.Add("modeMgr=false");
            }

            parts.Add("gameTypeStarted=" + (Singleton<GameTypeMgr>.HasInstance() && Singleton<GameTypeMgr>.Instance.IsGameTypeStarted));
            parts.Add("campaignStarted=" + (SingletonMonoBehaviour<CampaignBhv>.HasInstance(false) && SingletonMonoBehaviour<CampaignBhv>.Instance.IsCampaignStarted));
            parts.Add("runStarted=" + (SingletonMonoBehaviour<RunBhv>.HasInstance(false) && SingletonMonoBehaviour<RunBhv>.Instance.IsRunStarted));
            parts.Add("mapMgr=" + SingletonMonoBehaviour<MapMgrBhv>.HasInstance(false));
            parts.Add("actorFactory=" + SingletonMonoBehaviour<ActorCreateGameObjectBhv>.HasInstance(false));
            parts.Add("sceneLoading=" + RedHookSceneManagerBhv.isLoading);
            parts.Add("party=" + GetArenaCurrentPartyActors().Count);
            return string.Join(", ", parts.ToArray());
        }

        private static List<ActorInstance> GetArenaCurrentPartyActors()
        {
            List<ActorInstance> actors = new List<ActorInstance>();
            if (!Singleton<GameTypeMgr>.HasInstance() || Singleton<GameTypeMgr>.Instance.RosterManager == null)
            {
                return actors;
            }

            IReadOnlyList<ActorInstance> partyActors = Singleton<GameTypeMgr>.Instance.RosterManager.GetPartyActors();
            if (partyActors != null)
            {
                actors.AddRange(partyActors.Where(actor => actor != null));
            }

            actors.Sort(CompareArenaActorsByDraftSlot);
            return actors;
        }

        private static List<ActorInstance> GetArenaCombatTeamActors(int teamIndex)
        {
            List<ActorInstance> actors = new List<ActorInstance>();
            if (!SingletonMonoBehaviour<Library<uint, ActorInstance>>.HasInstance(false))
            {
                return actors;
            }

            QueryTeamActors query = QueryTeamActors.Trigger(teamIndex, true);
            if (query == null || query.m_TeamActorGuids == null)
            {
                return actors;
            }

            foreach (uint actorGuid in query.m_TeamActorGuids)
            {
                ActorInstance actor = SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance.GetLibraryElement(actorGuid);
                if (actor != null)
                {
                    actors.Add(actor);
                }
            }

            actors.Sort(CompareArenaActorsByDraftSlot);
            return actors;
        }

        private static int CompareArenaActorsByDraftSlot(ActorInstance left, ActorInstance right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            int leftPosition = left.GetIsTeamPositionSet() ? left.TeamPosition : int.MaxValue;
            int rightPosition = right.GetIsTeamPositionSet() ? right.TeamPosition : int.MaxValue;
            int positionCompare = leftPosition.CompareTo(rightPosition);
            return positionCompare != 0 ? positionCompare : left.ActorGuid.CompareTo(right.ActorGuid);
        }

        private static List<string> GetArenaEquippedSkillIds(ActorInstance actor)
        {
            List<string> skills = new List<string>();
            if (actor == null)
            {
                return skills;
            }

            try
            {
                foreach (string skillId in actor.GetEquippedCombatSkillIds())
                {
                    if (!string.IsNullOrWhiteSpace(skillId))
                    {
                        skills.Add(skillId.Trim());
                    }
                }
            }
            catch
            {
            }

            return skills;
        }

        private static string GetArenaFirstInventoryItemId(ItemInventory inventory, ItemType itemType)
        {
            return GetArenaInventoryItemIds(inventory, itemType).FirstOrDefault() ?? string.Empty;
        }

        private static List<string> GetArenaInventoryItemIds(ItemInventory inventory, ItemType itemType)
        {
            List<string> itemIds = new List<string>();
            if (inventory == null)
            {
                return itemIds;
            }

            try
            {
                foreach (IReadOnlyItemInstance item in inventory.GetItems())
                {
                    ItemDefinition definition = item == null ? null : item.GetItemDefinition();
                    if (definition != null &&
                        definition.m_type == itemType &&
                        !string.IsNullOrWhiteSpace(definition.m_id))
                    {
                        itemIds.Add(definition.m_id.Trim());
                    }
                }
            }
            catch
            {
            }

            return itemIds;
        }

        private static string JoinArenaIds(IReadOnlyList<string> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return "[none]";
            }

            return string.Join(", ", ids.ToArray());
        }

        private static string BuildArenaEnemyCompositionText(BattleConfigurationDefinition config, bool compact)
        {
            if (config == null || config.m_EnemyActors == null || config.m_EnemyActors.Count == 0)
            {
                return "[none]";
            }

            List<string> ids = config.m_EnemyActors
                .Where(actorId => !string.IsNullOrWhiteSpace(actorId))
                .Select(actorId => actorId.Trim())
                .ToList();
            List<string> names = ids.Select(GetArenaActorClassDisplayName).ToList();
            return BuildArenaEnemyCompositionText(ids, names, compact);
        }

        private static string BuildArenaEnemyCompositionText(
            IReadOnlyList<string> actorIds,
            IReadOnlyList<string> actorNames,
            bool compact)
        {
            if (actorIds == null || actorIds.Count == 0)
            {
                return "[none]";
            }

            if (!compact)
            {
                return string.Join(", ", actorIds
                    .Select((actorId, index) => "#" + (index + 1) + " " + GetArenaActorNameAt(actorIds, actorNames, index))
                    .ToArray());
            }

            List<string> parts = new List<string>();
            for (int i = 0; i < actorIds.Count; i++)
            {
                string actorId = actorIds[i];
                if (string.IsNullOrWhiteSpace(actorId) ||
                    parts.Any(part => part.EndsWith("|" + actorId, StringComparison.Ordinal)))
                {
                    continue;
                }

                int count = actorIds.Count(candidate => string.Equals(candidate, actorId, StringComparison.Ordinal));
                string name = GetArenaActorNameAt(actorIds, actorNames, i);
                parts.Add((count > 1 ? count + "x " : string.Empty) + name + "|" + actorId);
            }

            string text = parts.Count == 0
                ? JoinArenaIds(actorIds)
                : string.Join(", ", parts.Select(part => part.Split('|')[0]).ToArray());
            return compact ? TrimPanelText(text, 120) : text;
        }

        private static string GetArenaActorNameAt(
            IReadOnlyList<string> actorIds,
            IReadOnlyList<string> actorNames,
            int index)
        {
            if (actorNames != null &&
                index >= 0 &&
                index < actorNames.Count &&
                !string.IsNullOrWhiteSpace(actorNames[index]))
            {
                return actorNames[index];
            }

            if (actorIds != null && index >= 0 && index < actorIds.Count)
            {
                return GetArenaActorClassDisplayName(actorIds[index]);
            }

            return "[actor]";
        }

        private static string BuildArenaBattleConfigTooltip(BattleConfigurationDefinition config)
        {
            if (config == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>
            {
                "Id: " + (config.m_Id ?? "[battle]"),
                "Enemies: " + BuildArenaEnemyCompositionText(config, false),
            };

            if (config.m_PlayerActors != null && config.m_PlayerActors.Count > 0)
            {
                lines.Add("Preset player actors: " + JoinArenaIds(config.m_PlayerActors));
            }

            if (!string.IsNullOrWhiteSpace(config.m_BackgroundSceneOverride))
            {
                lines.Add("Background: " + config.m_BackgroundSceneOverride);
            }

            if (config.m_Tags != null && config.m_Tags.Count > 0)
            {
                lines.Add("Tags: " + string.Join(", ", config.m_Tags.ToArray()));
            }

            if (config.HasNextBattle)
            {
                lines.Add("Has next battle" + (config.m_IsNextBattleOptional ? " (optional)" : string.Empty));
            }

            if (config.EnemySummonControllerConfiguration != null)
            {
                lines.Add("Enemy summon controller: " + config.EnemySummonControllerConfiguration.m_Id);
            }

            if (config.BattleModifierOverride != null)
            {
                lines.Add("Battle modifier: " + config.BattleModifierOverride.m_Id);
            }

            return string.Join("\n", lines.ToArray());
        }

        private static ActorDataClass TryGetArenaActorDataClass(string actorDataId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(actorDataId) ||
                    !SingletonMonoBehaviour<Library<string, ActorDataClass>>.HasInstance(false))
                {
                    return null;
                }

                return SingletonMonoBehaviour<Library<string, ActorDataClass>>.Instance.GetLibraryElement(actorDataId);
            }
            catch
            {
                return null;
            }
        }

        private static string GetArenaActorClassDisplayName(string actorDataId)
        {
            ActorDataClass actorClass = TryGetArenaActorDataClass(actorDataId);
            try
            {
                string className = ActorDescription.GetClassName(actorDataId);
                if (!string.IsNullOrWhiteSpace(className) &&
                    !string.Equals(className, actorDataId, StringComparison.Ordinal))
                {
                    return CleanInline(className);
                }
            }
            catch
            {
            }

            string canonicalHeroNameKey = actorClass != null && actorClass.IsPopulateInRoster
                ? "hero_name_canonical_" + actorDataId
                : null;
            string localized = TryGetLocalizedText(
                canonicalHeroNameKey,
                actorClass == null ? null : actorClass.m_NameOverrideId,
                actorDataId,
                "hero_name_" + actorDataId,
                "actor_name_" + actorDataId);
            return string.IsNullOrWhiteSpace(localized) ? actorDataId ?? "[actor]" : localized;
        }

        private static string GetArenaActorInstanceDisplayName(ActorInstance actor)
        {
            if (actor == null)
            {
                return "[actor]";
            }

            if (!string.IsNullOrWhiteSpace(actor.ActorName))
            {
                return CleanInline(actor.ActorName);
            }

            return GetArenaActorClassDisplayName(actor.ActorDataId);
        }

        private static string BuildArenaActorClassTooltip(string actorDataId, ActorDataClass actorClass)
        {
            List<string> lines = new List<string>
            {
                "Id: " + (actorDataId ?? "[actor]"),
            };

            if (actorClass != null)
            {
                lines.Add("Size: " + actorClass.m_Size);
                if (actorClass.IsBiomeBoss || actorClass.IsExpeditionBoss || actorClass.IsGangBoss)
                {
                    lines.Add("Boss: " +
                        (actorClass.IsBiomeBoss ? "biome " : string.Empty) +
                        (actorClass.IsExpeditionBoss ? "expedition " : string.Empty) +
                        (actorClass.IsGangBoss ? "gang" : string.Empty));
                }

                if (actorClass.m_IsBattleComplete)
                {
                    lines.Add("Battle-complete actor");
                }

                try
                {
                    IReadOnlyList<string> tags = actorClass.GetPotentialTags();
                    if (tags != null && tags.Count > 0)
                    {
                        lines.Add("Tags: " + TrimPanelText(string.Join(", ", tags.Take(20).ToArray()), 260));
                    }
                }
                catch
                {
                }
            }

            return string.Join("\n", lines.ToArray());
        }

        private static string BuildArenaHeroTooltip(ActorInstance actor)
        {
            if (actor == null)
            {
                return string.Empty;
            }

            List<string> lines = new List<string>
            {
                "Id: " + (actor.ActorDataId ?? "[actor]"),
                "Guid: " + actor.ActorGuid,
            };

            string pathDescription = GetArenaActorPathDescription(actor);
            if (!string.IsNullOrWhiteSpace(pathDescription))
            {
                lines.Add(pathDescription);
            }

            List<string> skills = GetArenaEquippedSkillIds(actor);
            if (skills.Count > 0)
            {
                lines.Add("Skills: " + string.Join(", ", skills.Select(GetArenaSkillDisplayName).ToArray()));
            }

            return string.Join("\n\n", lines.ToArray());
        }

        private static string GetArenaActorPathDisplayName(ActorInstance actor)
        {
            if (actor == null || actor.ActorDataPath == null)
            {
                return "[path]";
            }

            try
            {
                string gender = actor.ActorDataClass == null ? string.Empty : actor.ActorDataClass.m_LocalizationGender;
                string displayName = ActorPathDescription.GetNameString(actor.ActorDataPath, gender, false);
                return string.IsNullOrWhiteSpace(displayName)
                    ? actor.ActorDataPath.Id
                    : CleanInline(displayName);
            }
            catch
            {
                return actor.ActorDataPath.Id ?? "[path]";
            }
        }

        private static string GetArenaActorPathDescription(ActorInstance actor)
        {
            if (actor == null || actor.ActorDataPath == null)
            {
                return string.Empty;
            }

            try
            {
                return ActorPathDescription.GetDescriptionString(actor.ActorDataPath, actor, true, true);
            }
            catch
            {
                return string.Empty;
            }
        }

        private string GetCachedArenaSkillDisplayName(string skillId)
        {
            string key = (skillId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return "[skill]";
            }

            string cached;
            if (_arenaSkillDisplayNameCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            if (_arenaSkillDisplayNameCache.Count > 256)
            {
                _arenaSkillDisplayNameCache.Clear();
            }

            cached = GetArenaSkillDisplayName(key);
            _arenaSkillDisplayNameCache[key] = cached;
            return cached;
        }

        private string GetCachedArenaSkillDescription(string skillId)
        {
            string key = (skillId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            string cached;
            if (_arenaSkillDescriptionCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            if (_arenaSkillDescriptionCache.Count > 256)
            {
                _arenaSkillDescriptionCache.Clear();
            }

            cached = GetArenaSkillDescription(key, null);
            _arenaSkillDescriptionCache[key] = cached;
            return cached;
        }

        private static string GetArenaSkillDisplayName(string skillId)
        {
            if (string.IsNullOrWhiteSpace(skillId))
            {
                return "[skill]";
            }

            try
            {
                ActorDataSkill skill = TryGetArenaSkillData(skillId);
                if (skill != null)
                {
                    string displayName = SkillDescription.GetNameText(skill);
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        return CleanInline(displayName);
                    }
                }
            }
            catch
            {
            }

            return skillId;
        }

        private static string GetArenaSkillDescription(string skillId, ActorInstance actor)
        {
            try
            {
                ActorDataSkill skill = TryGetArenaSkillData(skillId);
                if (skill == null)
                {
                    return string.Empty;
                }

                List<string> blocks = new List<string>();
                AddArenaDescriptionBlock(blocks, SkillDescription.GetTopBarString(skill, actor));

                uint actorGuid = actor == null ? 0U : actor.ActorGuid;
                foreach (string result in SkillDescription.GetResultStringsByTargetType(skill, false, actorGuid) ?? new List<string>())
                {
                    AddArenaDescriptionBlock(blocks, result);
                }

                return string.Join("\n", blocks.ToArray());
            }
            catch
            {
                return string.Empty;
            }
        }

        private static ActorDataSkill TryGetArenaSkillData(string skillId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(skillId) ||
                    !SingletonMonoBehaviour<Library<string, ActorDataSkill>>.HasInstance(false))
                {
                    return null;
                }

                return SingletonMonoBehaviour<Library<string, ActorDataSkill>>.Instance.GetLibraryElement(skillId);
            }
            catch
            {
                return null;
            }
        }

        private static void AddArenaDescriptionBlock(List<string> blocks, string text)
        {
            if (blocks == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            string trimmed = text.Trim();
            if (blocks.Any(existing => string.Equals(existing, trimmed, StringComparison.Ordinal)))
            {
                return;
            }

            blocks.Add(trimmed);
        }

        private static string TryGetLocalizedText(params string[] keys)
        {
            if (keys == null || keys.Length == 0 || !Singleton<Localization>.HasInstance())
            {
                return string.Empty;
            }

            foreach (string key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                try
                {
                    string value = Singleton<Localization>.Instance.TryGetString(key, false);
                    if (!string.IsNullOrWhiteSpace(value) &&
                        !string.Equals(value, key, StringComparison.Ordinal))
                    {
                        return CleanInline(value);
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        private void DrawHeroSelectPanelSection()
        {
            GUILayout.Label("Hero Select");

            HeroSelectSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestHeroSelectSnapshot(out snapshot))
            {
                DrawWrappedLabel("Hero select: none");
                return;
            }

            IList<HeroSelectSlotPayload> slots = snapshot.Slots ?? Array.Empty<HeroSelectSlotPayload>();
            IList<HeroSelectHeroPayload> heroes = snapshot.Heroes ?? Array.Empty<HeroSelectHeroPayload>();
            DrawWrappedLabel("Hero select: active=" + snapshot.IsActive +
                ", confirmed=" + snapshot.RosterConfirmed +
                ", canConfirm=" + snapshot.CanConfirm +
                ", selected=" + (snapshot.SelectedActorGuid ?? "[none]") +
                ", path=" + (snapshot.SelectedPathId ?? "[none]") +
                ", slots=" + slots.Count +
                ", heroes=" + heroes.Count +
                ", digest=" + (snapshot.Digest ?? "[none]"));
            DrawVoteStatus(MultiplayerSession.VoteKeyHeroReady);

            if (!snapshot.IsActive)
            {
                return;
            }

            for (int i = 0; i < slots.Count; i++)
            {
                HeroSelectSlotPayload slot = slots[i];
                string ownerText = string.IsNullOrWhiteSpace(slot.OwnerName)
                    ? "[unassigned]"
                    : slot.OwnerName + "/" + slot.OwnerSteamId;
                DrawWrappedLabel("  Slot " + slot.HeroSlot +
                    " (hero pos " + slot.SlotIndex + ")" +
                    ": " + (FormatHeroSelectActor(slot.ActorGuid, slot.ActorDataId, slot.ActorName, slot.PathId)) +
                    " | owner=" + ownerText);
            }

            List<int> localSlotIndexes = GetLocalOwnedHeroSelectSlotIndexes(slots);
            if (localSlotIndexes.Count == 0)
            {
                DrawWrappedLabel("Input: no local assigned hero slot.");
            }
            else if (snapshot.RosterConfirmed)
            {
                DrawWrappedLabel("Input: roster is already confirmed.");
            }
            else
            {
                DrawHeroSelectLocalControls(slots, heroes, localSlotIndexes);
            }

            if (_lobbyClient != null && _lobbyClient.IsHost && snapshot.CanConfirm)
            {
                if (GUILayout.Button("Confirm Party On Host", GUILayout.Height(28f)))
                {
                    _session.RequestHeroSelectConfirm();
                }
            }

            GUI.enabled = snapshot.CanConfirm && !snapshot.RosterConfirmed;
            if (GUILayout.Button("Ready To Start", GUILayout.Height(28f)))
            {
                _session.RequestHeroSelectReady();
            }

            GUI.enabled = true;
        }

        private void DrawHeroSelectLocalControls(
            IList<HeroSelectSlotPayload> slots,
            IList<HeroSelectHeroPayload> heroes,
            IList<int> localSlotIndexes)
        {
            GUILayout.Label("My Slots");
            for (int i = 0; i < localSlotIndexes.Count; i++)
            {
                int slotIndex = localSlotIndexes[i];
                HeroSelectSlotPayload slot = slots.FirstOrDefault(candidate => candidate.SlotIndex == slotIndex);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Slot " + (slotIndex + 1), GUILayout.Width(80f));
                GUILayout.Label(slot == null
                    ? "[missing]"
                    : FormatHeroSelectActor(slot.ActorGuid, slot.ActorDataId, slot.ActorName, slot.PathId));
                if (slot != null && !string.IsNullOrWhiteSpace(slot.ActorGuid) && GUILayout.Button("Clear", GUILayout.Width(72f)))
                {
                    _session.RequestHeroSelectClear(slotIndex);
                }

                GUILayout.EndHorizontal();

                DrawHeroSelectPathControls(slot, heroes);
            }

            GUILayout.Label("Available Heroes");
            int shown = 0;
            foreach (HeroSelectHeroPayload hero in heroes
                .Where(hero => hero != null && !hero.IsSelected)
                .OrderBy(hero => hero.ActorDataId)
                .ThenBy(hero => hero.ActorGuid))
            {
                shown++;
                GUILayout.BeginHorizontal();
                GUILayout.Label(FormatHeroSelectActor(hero.ActorGuid, hero.ActorDataId, hero.ActorName, hero.PathId));
                for (int i = 0; i < localSlotIndexes.Count; i++)
                {
                    int slotIndex = localSlotIndexes[i];
                    if (GUILayout.Button("S" + (slotIndex + 1), GUILayout.Width(42f)))
                    {
                        _session.RequestHeroSelectAssign(slotIndex, hero.ActorGuid);
                    }
                }

                GUILayout.EndHorizontal();
            }

            if (shown == 0)
            {
                DrawWrappedLabel("Available heroes: none");
            }
        }

        private void DrawHeroSelectPathControls(HeroSelectSlotPayload slot, IList<HeroSelectHeroPayload> heroes)
        {
            if (slot == null || string.IsNullOrWhiteSpace(slot.ActorGuid) || heroes == null)
            {
                return;
            }

            HeroSelectHeroPayload hero = heroes.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.ActorGuid, slot.ActorGuid, StringComparison.Ordinal));
            IList<HeroSelectPathPayload> paths = hero == null ? Array.Empty<HeroSelectPathPayload>() : hero.Paths ?? Array.Empty<HeroSelectPathPayload>();
            if (paths.Count == 0)
            {
                return;
            }

            for (int i = 0; i < paths.Count; i++)
            {
                HeroSelectPathPayload path = paths[i];
                if (path == null || string.IsNullOrWhiteSpace(path.PathId))
                {
                    continue;
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label("  Path", GUILayout.Width(80f));
                string label = (string.IsNullOrWhiteSpace(path.DisplayName) ? path.PathId : path.DisplayName) +
                    " [" + path.PathId + "]";
                if (path.IsCurrent)
                {
                    GUILayout.Label(label + " (current)");
                }
                else if (GUILayout.Button(label, GUILayout.Height(24f)))
                {
                    _session.RequestHeroSelectPath(slot.SlotIndex, slot.ActorGuid, path.PathId);
                }

                GUILayout.EndHorizontal();
            }
        }

        private void DrawHeroLoadoutPanelSection()
        {
            GUILayout.Label("Hero Loadout");

            HeroLoadoutSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestHeroLoadoutSnapshot(out snapshot))
            {
                DrawWrappedLabel("Hero loadout: none");
                return;
            }

            IList<HeroLoadoutActorPayload> actors = snapshot.Actors ?? Array.Empty<HeroLoadoutActorPayload>();
            DrawWrappedLabel("Loadout: active=" + snapshot.IsActive +
                ", scope=" + (snapshot.Scope ?? "[none]") +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", mastery=" + snapshot.HeroUpgradePoints +
                ", trainer=" + snapshot.CanMasterSkills +
                ", actors=" + actors.Count +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            if (!snapshot.IsActive || actors.Count == 0)
            {
                return;
            }

            DrawHeroRestItemPanelSection(snapshot, actors);

            for (int i = 0; i < actors.Count; i++)
            {
                HeroLoadoutActorPayload actor = actors[i];
                if (actor == null)
                {
                    continue;
                }

                bool isLocalOwner = IsLocalHeroLoadoutOwner(actor);
                string ownerText = string.IsNullOrWhiteSpace(actor.OwnerName)
                    ? "[unassigned]"
                    : actor.OwnerName + "/" + actor.OwnerSteamId;
                DrawWrappedLabel("Slot " + actor.HeroSlot +
                    ": " + FormatLoadoutActor(actor) +
                    " | owner=" + ownerText +
                    " | skills=" + actor.EquippedSkillCount + "/" + actor.EquippedSkillLimit +
                    (isLocalOwner ? " | local" : string.Empty));

                DrawHeroEquipmentPanelSection(snapshot, actor, isLocalOwner);

                IList<HeroLoadoutSkillPayload> skills = actor.Skills ?? Array.Empty<HeroLoadoutSkillPayload>();
                for (int j = 0; j < skills.Count; j++)
                {
                    HeroLoadoutSkillPayload skill = skills[j];
                    if (skill == null)
                    {
                        continue;
                    }

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(18f);
                    string state = skill.IsEquipped ? "[E]" : "[ ]";
                    string flags =
                        (skill.IsUpgraded ? " mastered" : string.Empty) +
                        (!skill.IsUnlocked ? " locked" : string.Empty) +
                        (skill.IsAlwaysEquipped ? " fixed" : string.Empty);
                    GUILayout.Label(
                        state + " " + (string.IsNullOrWhiteSpace(skill.DisplayName) ? skill.SkillId : skill.DisplayName) +
                        " [" + (skill.SkillId ?? "[none]") + "]" + flags,
                        GUILayout.MinWidth(360f));

                    bool oldEnabled = GUI.enabled;
                    GUI.enabled = oldEnabled && isLocalOwner && actor.CanEditSkills && skill.CanEquip;
                    if (GUILayout.Button("Equip", GUILayout.Width(76f)))
                    {
                        _session.RequestHeroLoadoutSkill(actor.HeroSlot, actor.ActorGuid, skill.SkillId, true);
                    }

                    GUI.enabled = oldEnabled && isLocalOwner && actor.CanEditSkills && skill.CanUnequip;
                    if (GUILayout.Button("Unequip", GUILayout.Width(86f)))
                    {
                        _session.RequestHeroLoadoutSkill(actor.HeroSlot, actor.ActorGuid, skill.SkillId, false);
                    }

                    GUI.enabled = oldEnabled && isLocalOwner && snapshot.CanMasterSkills && skill.CanMaster;
                    if (GUILayout.Button("Master", GUILayout.Width(82f)))
                    {
                        _session.RequestHeroMasterSkill(actor.HeroSlot, actor.ActorGuid, skill.SkillId);
                    }

                    GUI.enabled = oldEnabled;
                    GUILayout.EndHorizontal();
                }
            }
        }

        private void DrawHeroRestItemPanelSection(
            HeroLoadoutSnapshotPayload snapshot,
            IList<HeroLoadoutActorPayload> actors)
        {
            IList<HeroLoadoutItemPayload> inventoryItems = snapshot.InventoryItems ?? Array.Empty<HeroLoadoutItemPayload>();
            List<HeroLoadoutItemPayload> restItems = inventoryItems
                .Where(item => item != null &&
                    !item.IsEmpty &&
                    item.CanUseRestItem &&
                    string.Equals(item.ItemKind, "rest", StringComparison.Ordinal))
                .OrderBy(item => item.InventoryIndex)
                .ToList();
            if (restItems.Count == 0)
            {
                return;
            }

            SyncRestItemSelectionDigest(snapshot.Digest);

            List<HeroLoadoutActorPayload> activeActors = (actors ?? Array.Empty<HeroLoadoutActorPayload>())
                .Where(actor => actor != null && !string.IsNullOrWhiteSpace(actor.ActorGuid))
                .OrderBy(actor => actor.HeroSlot)
                .ToList();
            HeroLoadoutActorPayload localPrimary = activeActors.FirstOrDefault(IsLocalHeroLoadoutOwner);
            bool canSend = _session != null && localPrimary != null;

            DrawWrappedLabel("Inn Items: choose targets here; host applies the real rest item effect and consumes one item.");

            bool oldEnabled = GUI.enabled;
            for (int i = 0; i < restItems.Count; i++)
            {
                HeroLoadoutItemPayload item = restItems[i];
                int selectableTargetCount = item.RestSelectableTargetCount > 0
                    ? item.RestSelectableTargetCount
                    : item.RestTargetCount;
                int requiredTargets = selectableTargetCount >= 4
                    ? activeActors.Count
                    : selectableTargetCount;
                string targetText = item.IsRandomRestTarget
                    ? "random " + item.RestTargetCount + "/" + requiredTargets
                    : "targets=" + requiredTargets;

                GUILayout.BeginHorizontal();
                GUILayout.Space(18f);
                GUILayout.Label(
                    "Bag #" + item.InventoryIndex + " " + FormatLoadoutItem(item) + " | " + targetText,
                    GUILayout.MinWidth(390f));

                if (requiredTargets <= 1)
                {
                    DrawSingleTargetRestItemButtons(item, activeActors, localPrimary, canSend, oldEnabled);
                }
                else if (selectableTargetCount >= 4)
                {
                    GUI.enabled = oldEnabled && canSend && activeActors.Count > 0;
                    if (GUILayout.Button(item.IsRandomRestTarget ? "Use Random" : "Use Party", GUILayout.Width(104f)))
                    {
                        RequestHeroUseRestItem(localPrimary, item, Array.Empty<string>());
                    }

                    GUI.enabled = oldEnabled;
                }
                else
                {
                    DrawMultiTargetRestItemButtons(item, activeActors, localPrimary, canSend, oldEnabled, requiredTargets);
                }

                GUI.enabled = oldEnabled;
                GUILayout.EndHorizontal();
            }
        }

        private void DrawSingleTargetRestItemButtons(
            HeroLoadoutItemPayload item,
            IList<HeroLoadoutActorPayload> actors,
            HeroLoadoutActorPayload localPrimary,
            bool canSend,
            bool oldEnabled)
        {
            for (int i = 0; i < actors.Count; i++)
            {
                HeroLoadoutActorPayload target = actors[i];
                GUI.enabled = oldEnabled && canSend;
                if (GUILayout.Button("Use S" + target.HeroSlot, GUILayout.Width(76f)))
                {
                    HeroLoadoutActorPayload requestOwner = IsLocalHeroLoadoutOwner(target) ? target : localPrimary;
                    RequestHeroUseRestItem(requestOwner, item, new[] { target.ActorGuid });
                }
            }
        }

        private void DrawMultiTargetRestItemButtons(
            HeroLoadoutItemPayload item,
            IList<HeroLoadoutActorPayload> actors,
            HeroLoadoutActorPayload localPrimary,
            bool canSend,
            bool oldEnabled,
            int requiredTargets)
        {
            HashSet<string> selected = GetRestItemTargetSelection(item);
            PruneRestItemSelection(selected, actors, requiredTargets);

            for (int i = 0; i < actors.Count; i++)
            {
                HeroLoadoutActorPayload target = actors[i];
                bool isSelected = selected.Contains(target.ActorGuid);
                GUI.enabled = oldEnabled && (isSelected || selected.Count < requiredTargets);
                if (GUILayout.Button((isSelected ? "[x] " : "[ ] ") + "S" + target.HeroSlot, GUILayout.Width(76f)))
                {
                    if (isSelected)
                    {
                        selected.Remove(target.ActorGuid);
                    }
                    else if (selected.Count < requiredTargets)
                    {
                        selected.Add(target.ActorGuid);
                    }
                }
            }

            GUI.enabled = oldEnabled && canSend && selected.Count == requiredTargets;
            if (GUILayout.Button("Use " + selected.Count + "/" + requiredTargets, GUILayout.Width(96f)))
            {
                RequestHeroUseRestItem(localPrimary, item, selected.ToList());
            }

            GUI.enabled = oldEnabled;
        }

        private void SyncRestItemSelectionDigest(string digest)
        {
            string nextDigest = digest ?? string.Empty;
            if (string.Equals(_restItemSelectionDigest, nextDigest, StringComparison.Ordinal))
            {
                return;
            }

            _restItemSelectionDigest = nextDigest;
            _restItemTargetSelections.Clear();
        }

        private HashSet<string> GetRestItemTargetSelection(HeroLoadoutItemPayload item)
        {
            string key = GetRestItemSelectionKey(item);
            HashSet<string> selected;
            if (!_restItemTargetSelections.TryGetValue(key, out selected))
            {
                selected = new HashSet<string>(StringComparer.Ordinal);
                _restItemTargetSelections[key] = selected;
            }

            return selected;
        }

        private static void PruneRestItemSelection(
            HashSet<string> selected,
            IList<HeroLoadoutActorPayload> actors,
            int requiredTargets)
        {
            if (selected == null)
            {
                return;
            }

            HashSet<string> available = new HashSet<string>(
                (actors ?? Array.Empty<HeroLoadoutActorPayload>())
                    .Where(actor => actor != null && !string.IsNullOrWhiteSpace(actor.ActorGuid))
                    .Select(actor => actor.ActorGuid),
                StringComparer.Ordinal);
            selected.RemoveWhere(guid => !available.Contains(guid));

            while (requiredTargets >= 0 && selected.Count > requiredTargets)
            {
                selected.Remove(selected.Last());
            }
        }

        private static string GetRestItemSelectionKey(HeroLoadoutItemPayload item)
        {
            if (item == null)
            {
                return "none";
            }

            return item.InventoryIndex + ":" + (item.ItemId ?? string.Empty);
        }

        private void RequestHeroUseRestItem(
            HeroLoadoutActorPayload ownerActor,
            HeroLoadoutItemPayload item,
            IEnumerable<string> targetActorGuids)
        {
            if (_session == null || ownerActor == null || item == null)
            {
                return;
            }

            _session.RequestHeroUseRestItem(
                ownerActor.HeroSlot,
                ownerActor.ActorGuid,
                item.InventoryIndex,
                item.ItemId,
                targetActorGuids);
        }

        private void DrawHeroEquipmentPanelSection(
            HeroLoadoutSnapshotPayload snapshot,
            HeroLoadoutActorPayload actor,
            bool isLocalOwner)
        {
            DrawHeroEquipmentKind(
                snapshot,
                actor,
                isLocalOwner,
                "trinket",
                "Trinkets",
                actor.Trinkets ?? Array.Empty<HeroLoadoutItemPayload>());
            DrawHeroEquipmentKind(
                snapshot,
                actor,
                isLocalOwner,
                "combat",
                "Combat Items",
                actor.CombatItems ?? Array.Empty<HeroLoadoutItemPayload>());
        }

        private void DrawHeroEquipmentKind(
            HeroLoadoutSnapshotPayload snapshot,
            HeroLoadoutActorPayload actor,
            bool isLocalOwner,
            string itemKind,
            string label,
            IList<HeroLoadoutItemPayload> equippedItems)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(18f);
            GUILayout.Label(label + ":", GUILayout.Width(110f));

            bool oldEnabled = GUI.enabled;
            for (int i = 0; i < equippedItems.Count; i++)
            {
                HeroLoadoutItemPayload item = equippedItems[i];
                if (item == null)
                {
                    continue;
                }

                string itemText = item.IsEmpty
                    ? "#" + item.InventoryIndex + " [empty]"
                    : "#" + item.InventoryIndex + " " + FormatLoadoutItem(item);
                GUILayout.Label(itemText, GUILayout.MinWidth(190f));

                GUI.enabled = oldEnabled && isLocalOwner && !item.IsEmpty;
                if (GUILayout.Button("Unequip", GUILayout.Width(86f)))
                {
                    _session.RequestHeroUnequipItem(
                        actor.HeroSlot,
                        actor.ActorGuid,
                        itemKind,
                        item.InventoryIndex,
                        item.ItemId);
                }

                GUI.enabled = oldEnabled;
            }

            GUILayout.EndHorizontal();

            IList<HeroLoadoutItemPayload> inventoryItems = snapshot.InventoryItems ?? Array.Empty<HeroLoadoutItemPayload>();
            List<HeroLoadoutItemPayload> candidates = inventoryItems
                .Where(item => item != null &&
                    !item.IsEmpty &&
                    string.Equals(item.ItemKind, itemKind, StringComparison.Ordinal))
                .OrderBy(item => item.InventoryIndex)
                .ToList();
            if (candidates.Count == 0 || equippedItems.Count == 0)
            {
                return;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                HeroLoadoutItemPayload candidate = candidates[i];
                GUILayout.BeginHorizontal();
                GUILayout.Space(36f);
                GUILayout.Label("Bag #" + candidate.InventoryIndex + " " + FormatLoadoutItem(candidate), GUILayout.MinWidth(300f));

                for (int slotIndex = 0; slotIndex < equippedItems.Count; slotIndex++)
                {
                    HeroLoadoutItemPayload slot = equippedItems[slotIndex];
                    if (slot == null)
                    {
                        continue;
                    }

                    GUI.enabled = oldEnabled && isLocalOwner;
                    if (GUILayout.Button("Equip " + slot.InventoryIndex, GUILayout.Width(78f)))
                    {
                        _session.RequestHeroEquipItem(
                            actor.HeroSlot,
                            actor.ActorGuid,
                            itemKind,
                            candidate.InventoryIndex,
                            slot.InventoryIndex,
                            candidate.ItemId);
                    }
                }

                GUI.enabled = oldEnabled;
                GUILayout.EndHorizontal();
            }
        }

        private void DrawRunStatePanelSection()
        {
            DrawMainMenuPanelSection();
            DrawPanelSeparator();

            GUILayout.Label("Run State");

            RunStateSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestRunStateSnapshot(out snapshot))
            {
                DrawWrappedLabel("Run state: none");
                return;
            }

            IList<RunStatePartyActorPayload> party = snapshot.Party ?? Array.Empty<RunStatePartyActorPayload>();
            DrawWrappedLabel("Run: mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", changing=" + snapshot.IsGameModeChanging +
                ", gameType=" + (snapshot.CurrentGameType ?? "[none]") +
                ", gameStarted=" + snapshot.IsGameTypeStarted +
                ", runStarted=" + snapshot.IsRunStarted +
                ", startType=" + (snapshot.RunStartType ?? "[none]") +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            DrawWrappedLabel("Map: state=" + (snapshot.MapState ?? "[none]") +
                ", driving=" + snapshot.IsInDrivingState +
                ", biome=" + (snapshot.BiomeType ?? "[none]") +
                (string.IsNullOrWhiteSpace(snapshot.BiomeSubType) ? string.Empty : "/" + snapshot.BiomeSubType) +
                ", biomeIndex=" + snapshot.BiomeIndex +
                ", row=" + snapshot.BiomeRowIndex +
                ", lastNode=" + snapshot.LastVisitedBiomeRowIndex + ":" + snapshot.LastVisitedNodeIndex +
                (string.IsNullOrWhiteSpace(snapshot.LastVisitedNodeType) ? string.Empty : " " + snapshot.LastVisitedNodeType));

            if (snapshot.ProgressIsValid)
            {
                DrawWrappedLabel("Progress: biome=" + snapshot.ProgressBiomeIndex +
                    ", row=" + snapshot.ProgressRowIndex + "/" + snapshot.ProgressRowCount +
                    ", index=" + snapshot.ProgressIndex +
                    ", atNode=" + snapshot.ProgressAtNode +
                    ", travel=" + FormatRatio(snapshot.ProgressBiomeTravelRatio) +
                    ", rows=" + FormatRatio(snapshot.ProgressBetweenRowsRatio) +
                    ", biomes=" + FormatRatio(snapshot.ProgressBetweenBiomesRatio));
            }
            else
            {
                DrawWrappedLabel("Progress: invalid");
            }

            if (party.Count == 0)
            {
                DrawWrappedLabel("Party: none");
                return;
            }

            DrawWrappedLabel("Party");
            foreach (RunStatePartyActorPayload actor in party
                .OrderBy(actor => actor.TeamPosition)
                .ThenBy(actor => actor.ActorGuid))
            {
                DrawWrappedLabel("  Slot " + actor.HeroSlot +
                    " (hero pos " + actor.TeamPosition + "): " +
                    FormatHeroSelectActor(actor.ActorGuid, actor.ActorDataId, actor.ActorName, actor.PathId));
            }
        }

        private void DrawExpeditionOverviewPanelSection()
        {
            GUILayout.Label("Expedition Overview");

            ExpeditionOverviewSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestExpeditionOverviewSnapshot(out snapshot))
            {
                DrawWrappedLabel("Overview: none");
                return;
            }

            IList<ExpeditionHeroPayload> heroes = snapshot.Heroes ?? Array.Empty<ExpeditionHeroPayload>();
            IList<ExpeditionItemPayload> inventoryItems = snapshot.InventoryItems ?? Array.Empty<ExpeditionItemPayload>();
            IList<ExpeditionItemPayload> stagecoachItems = snapshot.StagecoachItems ?? Array.Empty<ExpeditionItemPayload>();
            IList<ExpeditionCurrencyPayload> currencies = snapshot.Currencies ?? Array.Empty<ExpeditionCurrencyPayload>();
            IList<ExpeditionRelationshipPayload> relationships = snapshot.Relationships ?? Array.Empty<ExpeditionRelationshipPayload>();

            DrawWrappedLabel("Overview: active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", gameType=" + (snapshot.CurrentGameType ?? "[none]") +
                ", gameStarted=" + snapshot.IsGameTypeStarted +
                ", runStarted=" + snapshot.IsRunStarted +
                ", map=" + (snapshot.MapState ?? "[none]") +
                ", biome=" + (snapshot.BiomeType ?? "[none]") +
                (string.IsNullOrWhiteSpace(snapshot.BiomeSubType) ? string.Empty : "/" + snapshot.BiomeSubType) +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            DrawWrappedLabel("Resources: relics=" + snapshot.Relics +
                ", baubles=" + snapshot.Baubles +
                ", candles=" + snapshot.Candles +
                ", mastery=" + snapshot.MasteryPoints +
                ", torch=" + snapshot.Torch + "/" + snapshot.TorchMax +
                ", loathing=" + snapshot.Loathing + "/" + snapshot.LoathingMax);

            DrawWrappedLabel("Coach: armor=" + snapshot.Armor + "/" + snapshot.ArmorMax +
                ", wheels=" + snapshot.Wheels + "/" + snapshot.WheelsMax +
                ", equipped=" + stagecoachItems.Count);

            DrawWrappedLabel("Inventory: slots=" + snapshot.InventoryFilledSlots + "/" + snapshot.InventoryTotalSlots +
                ", trackedItems=" + inventoryItems.Count);

            if (currencies.Count > 0)
            {
                DrawWrappedLabel("Currencies: " + string.Join(", ", currencies
                    .Where(currency => currency != null)
                    .Select(currency => (currency.DisplayName ?? currency.ItemId ?? "[currency]") + "=" + currency.Quantity)
                    .ToArray()));
            }

            DrawExpeditionBiomeObjectives(snapshot.BiomeGoal, snapshot.BiomeModifier);

            DrawExpeditionCombatContext(
                snapshot.CombatScenario,
                snapshot.MapProgress,
                snapshot.MapRoute,
                snapshot.LastVisitedNode,
                snapshot.LastCompletedNode);

            DrawExpeditionRelationships(relationships);

            if (heroes.Count == 0)
            {
                DrawWrappedLabel("Heroes: none");
            }
            else
            {
                DrawWrappedLabel("Heroes");
                foreach (ExpeditionHeroPayload hero in heroes
                    .Where(hero => hero != null)
                    .OrderBy(hero => hero.TeamPosition)
                    .ThenBy(hero => hero.ActorGuid))
                {
                    DrawWrappedLabel("  Slot " + hero.HeroSlot +
                        " (hero pos " + hero.TeamPosition + "): " +
                        FormatHeroSelectActor(hero.ActorGuid, hero.ActorDataId, hero.ActorName, hero.PathId) +
                        " | hp=" + hero.Hp + "/" + hero.HpMax +
                        " stress=" + hero.Stress + "/" + hero.StressMax +
                        " wound=" + FormatRatio(hero.WoundPercent));

                    DrawExpeditionQuirkLine("    Quirks", hero.Quirks);
                    DrawExpeditionQuirkLine("    Diseases", hero.Diseases);
                    DrawExpeditionItemLine("    Memories", hero.Memories);
                    DrawExpeditionItemLine("    Trinkets", hero.Trinkets);
                    DrawExpeditionItemLine("    Combat", hero.CombatItems);
                    DrawExpeditionHeroRunGoal(hero);
                }
            }

            DrawExpeditionItems("Stagecoach items", stagecoachItems, 24);
            DrawExpeditionItems("Inventory items", inventoryItems, 40);
        }

        private void DrawMainMenuPanelSection()
        {
            GUILayout.Label("Main Menu");

            MainMenuSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestMainMenuSnapshot(out snapshot))
            {
                DrawWrappedLabel("Main menu: none");
                return;
            }

            DrawWrappedLabel("Main menu: active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", profile=" + (snapshot.ProfileName ?? "[none]") +
                ", save=" + snapshot.HasExpeditionSave +
                ", canContinue=" + snapshot.CanContinueExpedition +
                ", canNew=" + snapshot.CanStartNewExpedition +
                ", validation=" + (snapshot.SaveValidationAction ?? "[none]") +
                ", failure=" + (snapshot.SaveFailureReason ?? "[none]") +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            if (!string.IsNullOrWhiteSpace(snapshot.BlockReason))
            {
                DrawWrappedLabel("Main menu block: " + snapshot.BlockReason);
            }

            DrawVoteStatus(MultiplayerSession.VoteKeyMainMenu);

            bool oldEnabled = GUI.enabled;
            GUILayout.BeginHorizontal();
            GUI.enabled = oldEnabled && snapshot.CanContinueExpedition;
            if (GUILayout.Button("Continue Expedition", GUILayout.Height(28f)))
            {
                _session.RequestMainMenuAction("continue");
            }

            GUI.enabled = oldEnabled && snapshot.CanStartNewExpedition;
            if (GUILayout.Button("Start New Expedition", GUILayout.Height(28f)))
            {
                _session.RequestMainMenuAction("start_new");
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawTurnPanelSection()
        {
            GUILayout.Label("Turn");

            bool newAutoTurn = GUILayout.Toggle(_autoTurnPromptsEnabled, "Auto turn prompt");
            if (newAutoTurn != _autoTurnPromptsEnabled)
            {
                _autoTurnPromptsEnabled = newAutoTurn;
                ResetAutoTurnMemory();
                HostLog.Write("[autoturn] " + (_autoTurnPromptsEnabled ? "enabled" : "disabled") + " from panel.");
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Turn Current", GUILayout.Height(28f)))
            {
                ExecuteTurnCurrentCommand("auto");
            }

            if (GUILayout.Button("Combat", GUILayout.Height(28f)) && _combatAdapter != null)
            {
                _combatAdapter.LogCombatState();
            }

            GUILayout.EndHorizontal();

            TurnPromptPayload prompt;
            HeroSlotAssignmentPayload owner;
            string skillId;
            string targetGuid;
            bool isPass;
            if (!_session.TryGetPendingTurn(out prompt, out owner, out skillId, out targetGuid, out isPass))
            {
                SyncPanelPendingKey(null);
                GUILayout.Label("Pending: none");
                return;
            }

            SyncPanelPendingKey(prompt);
            string ownerText = owner == null ? "unassigned" : owner.Name + "/" + owner.SteamId;
            GUILayout.Label("Pending: r" + prompt.Round +
                "/t" + prompt.Turn +
                ", role " + (prompt.ControlRole ?? "hero") +
                ", team " + prompt.TeamIndex + ":" + prompt.TeamPosition +
                ", slot " + prompt.HeroSlot +
                ", owner " + ownerText);
            GUILayout.Label("Actor: " + prompt.ActorName + "/" + prompt.ActorGuid);
            GUILayout.Label("Accepted: skill=" + (skillId ?? "[none]") +
                ", target=" + (targetGuid ?? "[none]") +
                ", pass=" + isPass);

            if (!IsLocalTurnOwner(owner))
            {
                GUILayout.Label(owner == null
                    ? "Input: slot is unassigned."
                    : "Input: waiting for " + owner.Name + ".");
                return;
            }

            DrawTurnOptionButtons(prompt, skillId);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Skill", GUILayout.Width(50f));
            _panelSkillId = GUILayout.TextField(_panelSkillId ?? string.Empty);
            if (GUILayout.Button("Send", GUILayout.Width(80f)))
            {
                TrySendPanelSkill(prompt);
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Target", GUILayout.Width(50f));
            _panelTargetGuid = GUILayout.TextField(_panelTargetGuid ?? string.Empty);
            if (GUILayout.Button("Send", GUILayout.Width(80f)))
            {
                TrySendPanelTarget(prompt);
            }

            GUILayout.EndHorizontal();

            if (GUILayout.Button(Ui("Pass", "跳过"), GUILayout.Height(28f)))
            {
                _session.PassTurn(prompt.HeroSlot, prompt.ActorGuid);
            }
        }

        private void DrawTurnOptionButtons(TurnPromptPayload prompt, string acceptedSkillId)
        {
            IList<TurnSkillOptionPayload> options = prompt.SkillOptions ?? Array.Empty<TurnSkillOptionPayload>();
            if (options.Count == 0)
            {
                GUILayout.Label("Options: none in prompt");
                return;
            }

            GUILayout.Label("Skills");
            for (int i = 0; i < options.Count; i++)
            {
                TurnSkillOptionPayload option = options[i];
                if (GUILayout.Button(FormatSkillOption(option), GUILayout.Height(26f)))
                {
                    _panelSkillId = option.SkillId ?? string.Empty;
                    _session.ChooseSkill(prompt.HeroSlot, prompt.ActorGuid, _panelSkillId);
                }
            }

            string selectedSkillId = !string.IsNullOrWhiteSpace(_panelSkillId) ? _panelSkillId.Trim() : acceptedSkillId;
            TurnSkillOptionPayload selectedOption = options.FirstOrDefault(option =>
                string.Equals(option.SkillId, selectedSkillId, StringComparison.Ordinal));
            if (selectedOption == null)
            {
                return;
            }

            GUILayout.Label("Targets for " + selectedOption.SkillId);
            IList<TurnTargetOptionPayload> targets = selectedOption.Targets ?? Array.Empty<TurnTargetOptionPayload>();
            for (int i = 0; i < targets.Count; i++)
            {
                TurnTargetOptionPayload target = targets[i];
                if (GUILayout.Button(FormatTargetOption(target), GUILayout.Height(26f)))
                {
                    _panelTargetGuid = target.ActorGuid ?? string.Empty;
                    _session.ChooseTarget(prompt.HeroSlot, prompt.ActorGuid, _panelTargetGuid);
                }
            }
        }

        private void DrawCombatSnapshotPanelSection()
        {
            GUILayout.Label("Combat Snapshot");

            CombatSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestCombatSnapshot(out snapshot))
            {
                DrawWrappedLabel("Snapshot: none");
                return;
            }

            IList<ActorSnapshotPayload> actors = snapshot.Actors ?? Array.Empty<ActorSnapshotPayload>();
            DrawWrappedLabel("Snapshot: state=" + (snapshot.BattleState ?? "[none]") +
                ", next=" + (snapshot.NextState ?? "[none]") +
                ", r" + snapshot.Round +
                "/t" + snapshot.Turn +
                ", partyInBattle=" + snapshot.PartyInBattle +
                ", actors=" + actors.Count +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            if (!string.IsNullOrEmpty(snapshot.CurrentActorGuid))
            {
                DrawWrappedLabel("Current: " + (snapshot.CurrentActorName ?? "[unknown]") +
                    " / guid=" + snapshot.CurrentActorGuid);
            }

            DrawCombatTurnOrder(snapshot);
            DrawCombatSelectedSkill(snapshot.SelectedSkill);

            if (actors.Count == 0)
            {
                DrawWrappedLabel("Actors: none");
                return;
            }

            foreach (ActorSnapshotPayload actor in actors
                .OrderBy(actor => actor.TeamIndex)
                .ThenBy(actor => actor.TeamPosition)
                .ThenBy(actor => actor.ActorGuid))
            {
                string marker = string.Equals(actor.ActorGuid, snapshot.CurrentActorGuid, StringComparison.Ordinal) ? "> " : "  ";
                string alive = actor.IsLiving ? string.Empty : " [dead]";
                DrawWrappedLabel(marker +
                    "team=" + actor.TeamIndex +
                    " pos=" + actor.TeamPosition +
                    " " + (actor.ActorDataId ?? "[unknown]") +
                    " guid=" + actor.ActorGuid +
                    alive +
                    " HP " + actor.Health + "/" + actor.MaxHealth +
                    " ST " + actor.Stress + "/" + actor.StressMax);

                string tokens = FormatSnapshotStatuses(actor.Tokens);
                string buffs = FormatSnapshotStatuses(actor.Buffs);
                string dots = FormatSnapshotStatuses(actor.Dots);
                if (tokens != "-")
                {
                    DrawWrappedLabel("    TOK " + tokens);
                }

                if (buffs != "-")
                {
                    DrawWrappedLabel("    BUFF " + buffs);
                }

                if (dots != "-")
                {
                    DrawWrappedLabel("    DOT " + dots);
                }
            }
        }

        private static void DrawCombatSelectedSkill(CombatSelectedSkillPayload selectedSkill)
        {
            if (selectedSkill == null)
            {
                DrawWrappedLabel("Selected skill: none");
                return;
            }

            IList<TurnTargetOptionPayload> validTargets = selectedSkill.ValidTargets ?? Array.Empty<TurnTargetOptionPayload>();
            IList<TurnTargetOptionPayload> stealthedTargets = selectedSkill.StealthedTargets ?? Array.Empty<TurnTargetOptionPayload>();
            DrawWrappedLabel("Selected skill: " +
                (selectedSkill.DisplayName ?? selectedSkill.SkillId ?? "[skill]") +
                " [" + (selectedSkill.SkillId ?? "id") + "]" +
                ", validTargets=" + validTargets.Count +
                ", stealthedTargets=" + stealthedTargets.Count);

            if (validTargets.Count > 0)
            {
                DrawWrappedLabel("  Valid targets: " + FormatCombatTargets(validTargets));
            }

            if (stealthedTargets.Count > 0)
            {
                DrawWrappedLabel("  Stealthed targets: " + FormatCombatTargets(stealthedTargets));
            }
        }

        private static void DrawCombatTurnOrder(CombatSnapshotPayload snapshot)
        {
            IList<CombatTurnOrderEntryPayload> turnOrder = snapshot == null
                ? Array.Empty<CombatTurnOrderEntryPayload>()
                : snapshot.TurnOrder ?? Array.Empty<CombatTurnOrderEntryPayload>();
            if (turnOrder.Count == 0)
            {
                DrawWrappedLabel("Turn order: none");
                return;
            }

            DrawWrappedLabel("Turn order: " + string.Join(" -> ", turnOrder
                .OrderBy(entry => entry.Index)
                .Select(FormatCombatTurnOrderEntry)
                .ToArray()));
        }

        private static string FormatCombatTurnOrderEntry(CombatTurnOrderEntryPayload entry)
        {
            if (entry == null)
            {
                return "[turn]";
            }

            string actor = string.IsNullOrWhiteSpace(entry.ActorDataId)
                ? (entry.ActorGuid ?? "[actor]")
                : entry.ActorDataId;
            string flags =
                (entry.IsCurrentActor ? "*" : string.Empty) +
                (entry.IsFirstNormalTurn ? " first" : string.Empty) +
                (entry.IsLastNormalTurn ? " last" : string.Empty) +
                (entry.IsMissingActor ? " missing" : string.Empty);
            return "#" + entry.Index +
                " " + actor +
                " t" + entry.TeamIndex +
                "p" + entry.TeamPosition +
                (string.IsNullOrWhiteSpace(flags) ? string.Empty : " [" + flags.Trim() + "]");
        }

        private static string FormatCombatTargets(IList<TurnTargetOptionPayload> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                return "[none]";
            }

            return string.Join(", ", targets
                .Where(target => target != null)
                .OrderBy(target => target.TeamIndex)
                .ThenBy(target => target.TeamPosition)
                .ThenBy(target => target.ActorGuid)
                .Select(target =>
                    (target.DisplayName ?? target.ActorGuid ?? "[target]") +
                    " t" + target.TeamIndex +
                    "p" + target.TeamPosition)
                .ToArray());
        }

        private void DrawResultPanelSection()
        {
            GUILayout.Label("Battle Result");

            BattleResultPayload result;
            if (_session == null || !_session.TryGetLatestBattleResult(out result))
            {
                DrawWrappedLabel("Result: none");
                return;
            }

            IList<LootRewardPayload> rewards = result.LootRewards ?? Array.Empty<LootRewardPayload>();
            DrawWrappedLabel("Result: event=" + result.EventId +
                ", complete=" + result.IsFightComplete +
                ", sequenceComplete=" + result.IsBattleSequenceComplete +
                ", hasNext=" + result.HasNextBattle +
                ", reason=" + (result.LootReason ?? "[none]") +
                ", config=" + (result.CurrentBattleConfigurationId ?? "[none]") +
                ", digest=" + (result.Digest ?? "[none]"));

            if (rewards.Count == 0)
            {
                DrawWrappedLabel("Rewards: none");
                return;
            }

            DrawWrappedLabel("Rewards");
            for (int i = 0; i < rewards.Count; i++)
            {
                LootRewardPayload reward = rewards[i];
                DrawWrappedLabel("  " + (reward.Type ?? "[type]") +
                    " " + (reward.Id ?? "[id]") +
                    " x" + reward.Quantity);
            }
        }

        private void DrawGameResultsPanelSection()
        {
            GUILayout.Label("Game Results");

            GameResultsSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestGameResultsSnapshot(out snapshot))
            {
                DrawWrappedLabel("Game results: none");
                return;
            }

            DrawWrappedLabel("Game results: active=" + snapshot.IsActive +
                ", state=" + (snapshot.ScreenState ?? "[none]") +
                ", reason=" + (snapshot.GameOverReason ?? "[none]") +
                ", hasScore=" + snapshot.HasScore +
                ", canContinue=" + snapshot.CanContinue +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            DrawVoteStatus(MultiplayerSession.VoteKeyGameResults);

            if (!snapshot.IsActive)
            {
                return;
            }

            GUI.enabled = snapshot.CanContinue;
            if (GUILayout.Button("Vote Continue Results", GUILayout.Height(28f)))
            {
                _session.RequestGameResultsContinue();
            }

            GUI.enabled = true;
        }

        private void DrawLootWindowPanelSection()
        {
            GUILayout.Label("Loot Window");

            if (IsLocalPvpEnemyController())
            {
                DrawWrappedLabel("Loot voting is hidden for the active PVP enemy pilot.");
                return;
            }

            LootWindowSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestLootWindowSnapshot(out snapshot))
            {
                DrawWrappedLabel("Loot: none");
                return;
            }

            IList<LootItemSnapshotPayload> items = snapshot.Items ?? Array.Empty<LootItemSnapshotPayload>();
            SyncLootVoteSelection(snapshot);
            DrawWrappedLabel("Loot: active=" + snapshot.IsActive +
                ", state=" + (snapshot.ScreenState ?? "[none]") +
                ", reason=" + (snapshot.Reason ?? "[none]") +
                ", items=" + items.Count +
                ", takeAll=" + snapshot.CanTakeAll +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            if (snapshot.HeroPoints != 0 || snapshot.TorchGain != 0 || snapshot.ArmorGain != 0 || snapshot.WheelGain != 0)
            {
                DrawWrappedLabel("Gains: heroPoints=" + snapshot.HeroPoints +
                    ", torch=" + snapshot.TorchGain +
                    ", armor=" + snapshot.ArmorGain +
                    ", wheels=" + snapshot.WheelGain);
            }

            DrawVoteStatus(MultiplayerSession.VoteKeyLoot);

            for (int i = 0; i < items.Count; i++)
            {
                LootItemSnapshotPayload item = items[i];
                GUILayout.BeginHorizontal();
                DrawWrappedLabel("  #" + item.InventoryIndex +
                    " " + (item.DisplayName ?? item.ItemId ?? "[item]") +
                    " [" + (item.ItemId ?? "[id]") + "]" +
                    " x" + item.Quantity +
                    (string.IsNullOrEmpty(item.SlotType) ? string.Empty : " slot=" + item.SlotType));
                bool canSelect = snapshot.IsActive && item.InventoryIndex >= 0;
                bool selected = canSelect && _lootVoteSelectedIndexes.Contains(item.InventoryIndex);
                GUI.enabled = canSelect;
                if (GUILayout.Button(selected ? "Unwant" : "Want", GUILayout.Width(78f)))
                {
                    if (selected)
                    {
                        _lootVoteSelectedIndexes.Remove(item.InventoryIndex);
                    }
                    else
                    {
                        _lootVoteSelectedIndexes.Add(item.InventoryIndex);
                    }
                }

                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }

            if (snapshot.IsActive)
            {
                List<LootItemSnapshotPayload> selectedItems = items
                    .Where(item => item != null && _lootVoteSelectedIndexes.Contains(item.InventoryIndex))
                    .ToList();
                DrawWrappedLabel("Loot vote selection: " + selectedItems.Count + " item(s).");
                GUILayout.BeginHorizontal();
                GUI.enabled = selectedItems.Count > 0;
                if (GUILayout.Button("Vote Selected", GUILayout.Height(28f)))
                {
                    _session.RequestLootTakeSelected(selectedItems);
                }

                GUI.enabled = true;
                GUI.enabled = snapshot.CanTakeAll;
                if (GUILayout.Button("Vote Take All", GUILayout.Height(28f)))
                {
                    _session.RequestLootTakeAll();
                }

                GUI.enabled = true;
                if (GUILayout.Button("Vote Discard All", GUILayout.Height(28f)))
                {
                    _session.RequestLootDiscardAll();
                }

                GUILayout.EndHorizontal();
            }
        }

        private void DrawStorePanelSection()
        {
            GUILayout.Label("Store");

            StoreSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestStoreSnapshot(out snapshot))
            {
                DrawWrappedLabel("Store: none");
                return;
            }

            IList<StoreItemPayload> items = snapshot.Items ?? Array.Empty<StoreItemPayload>();
            DrawWrappedLabel("Store: active=" + snapshot.IsActive +
                ", kind=" + (snapshot.StoreKind ?? "[none]") +
                ", state=" + (snapshot.ScreenState ?? "[none]") +
                ", items=" + items.Count +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            if (!snapshot.IsActive)
            {
                return;
            }

            if (items.Count == 0)
            {
                DrawWrappedLabel("Items: none / out of stock");
                return;
            }

            bool oldEnabled = GUI.enabled;
            foreach (StoreItemPayload item in items
                .Where(item => item != null)
                .OrderBy(item => item.InventoryIndex))
            {
                GUILayout.BeginHorizontal();
                string itemText = "#" + item.InventoryIndex +
                    " " + (item.DisplayName ?? item.ItemId ?? "[item]") +
                    " [" + (item.ItemId ?? "[id]") + "]" +
                    " x" + item.Quantity +
                    (string.IsNullOrWhiteSpace(item.ItemType) ? string.Empty : " type=" + item.ItemType) +
                    (string.IsNullOrWhiteSpace(item.PriceText) ? string.Empty : " price=" + item.PriceText);
                GUILayout.Label(itemText, GUILayout.MinWidth(520f));

                GUI.enabled = oldEnabled && item.CanAfford && item.Quantity > 0;
                if (GUILayout.Button("Buy", GUILayout.Width(80f)))
                {
                    _session.RequestStorePurchase(item.InventoryIndex, item.ItemId);
                }

                GUI.enabled = oldEnabled;
                GUILayout.EndHorizontal();
            }
        }

        private void DrawStagecoachPanelSection()
        {
            GUILayout.Label("Stagecoach");

            StagecoachSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestStagecoachSnapshot(out snapshot))
            {
                DrawWrappedLabel("Stagecoach: none");
                return;
            }

            IList<StagecoachItemPayload> playerItems = snapshot.PlayerItems ?? Array.Empty<StagecoachItemPayload>();
            IList<StagecoachSlotPayload> slots = snapshot.Slots ?? Array.Empty<StagecoachSlotPayload>();
            DrawWrappedLabel("Stagecoach: active=" + snapshot.IsActive +
                ", editable=" + snapshot.IsEditable +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", state=" + (snapshot.ScreenState ?? "[none]") +
                ", armor=" + snapshot.Armor + "/" + snapshot.MaxArmor +
                ", wheels=" + snapshot.Wheels + "/" + snapshot.MaxWheels +
                ", playerItems=" + playerItems.Count +
                ", slots=" + slots.Count +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            if (!snapshot.IsActive)
            {
                DrawWrappedLabel("Open the host stagecoach / wainwright screen to mirror repair and equipment controls.");
                return;
            }

            DrawStagecoachRepairRow(snapshot.ArmorRepair);
            DrawStagecoachRepairRow(snapshot.WheelRepair);

            DrawPanelSeparator();
            GUILayout.Label("Equipped Slots");
            if (slots.Count == 0)
            {
                DrawWrappedLabel("Slots: none");
                return;
            }

            foreach (StagecoachSlotPayload slot in slots
                .Where(slot => slot != null)
                .OrderBy(slot => StagecoachSlotOrder(slot.SlotType))
                .ThenBy(slot => slot.SlotIndex))
            {
                GUILayout.BeginVertical(GUI.skin.box);
                string currentItemText = slot.Item == null
                    ? "[empty]"
                    : (slot.Item.DisplayName ?? slot.Item.ItemId ?? "[item]") +
                        " [" + (slot.Item.ItemId ?? "[id]") + "]" +
                        " x" + slot.Item.Quantity +
                        (slot.Item.IsUnequipInvalid ? " locked" : string.Empty);
                GUILayout.BeginHorizontal();
                GUILayout.Label((slot.SlotType ?? "[slot]") +
                    " #" + slot.SlotIndex +
                    ": " + currentItemText,
                    GUILayout.MinWidth(520f));

                bool oldEnabled = GUI.enabled;
                GUI.enabled = oldEnabled && snapshot.IsEditable && slot.Item != null && slot.CanUnequip;
                if (GUILayout.Button("Unequip", GUILayout.Width(90f)))
                {
                    _session.RequestStagecoachUnequip(slot.SlotType, slot.SlotIndex, slot.Item.ItemId);
                }

                GUI.enabled = oldEnabled;
                GUILayout.EndHorizontal();

                IList<StagecoachItemPayload> compatibleItems = playerItems
                    .Where(item => item != null &&
                        item.CanEquip &&
                        string.Equals(item.SlotType, slot.SlotType, StringComparison.Ordinal))
                    .OrderBy(item => item.InventoryIndex)
                    .ToList();
                if (compatibleItems.Count == 0)
                {
                    DrawWrappedLabel("No compatible player-inventory stagecoach item.");
                }
                else
                {
                    foreach (StagecoachItemPayload item in compatibleItems)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("Player #" + item.InventoryIndex +
                            " " + (item.DisplayName ?? item.ItemId ?? "[item]") +
                            " [" + (item.ItemId ?? "[id]") + "]" +
                            " x" + item.Quantity,
                            GUILayout.MinWidth(520f));
                        GUI.enabled = oldEnabled && snapshot.IsEditable && slot.CanAcceptItems;
                        if (GUILayout.Button("Equip Here", GUILayout.Width(110f)))
                        {
                            _session.RequestStagecoachEquip(
                                item.InventoryIndex,
                                item.ItemId,
                                slot.SlotType,
                                slot.SlotIndex);
                        }

                        GUI.enabled = oldEnabled;
                        GUILayout.EndHorizontal();
                    }
                }

                GUILayout.EndVertical();
            }
        }

        private void DrawStagecoachRepairRow(StagecoachRepairPayload repair)
        {
            if (repair == null)
            {
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label((repair.RepairKind ?? "[repair]") +
                ": " + repair.CurrentValue + "/" + repair.MaxValue +
                ", amount=" + repair.Amount +
                (string.IsNullOrWhiteSpace(repair.CostText) ? string.Empty : ", cost=" + repair.CostText),
                GUILayout.MinWidth(520f));

            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && repair.CanRepair && repair.CanAfford;
            if (GUILayout.Button("Repair", GUILayout.Width(90f)))
            {
                _session.RequestStagecoachRepair(repair.RepairKind);
            }

            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();
        }

        private static int StagecoachSlotOrder(string slotType)
        {
            switch ((slotType ?? string.Empty).ToLowerInvariant())
            {
                case "general":
                    return 0;
                case "trophy":
                    return 1;
                case "pet":
                    return 2;
                case "flame":
                    return 3;
                default:
                    return 10;
            }
        }

        private void DrawInnPanelSection()
        {
            GUILayout.Label("Inn / Embark");

            InnSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestInnSnapshot(out snapshot))
            {
                DrawWrappedLabel("Inn: none");
                return;
            }

            IList<InnBiomeChoicePayload> choices = snapshot.BiomeChoices ?? Array.Empty<InnBiomeChoicePayload>();
            DrawWrappedLabel("Inn: active=" + snapshot.IsActive +
                ", gameType=" + (snapshot.GameType ?? "[none]") +
                ", state=" + (snapshot.InnState ?? "[none]") +
                ", camp=" + snapshot.IsCamp +
                ", choices=" + choices.Count +
                ", selected=" + snapshot.SelectedBiomeChoiceIndex +
                ", canEmbark=" + snapshot.CanEmbark +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            DrawVoteStatus(MultiplayerSession.VoteKeyInnBiome);
            DrawVoteStatus(MultiplayerSession.VoteKeyInnEmbark);

            for (int i = 0; i < choices.Count; i++)
            {
                InnBiomeChoicePayload choice = choices[i];
                if (choice == null)
                {
                    continue;
                }

                string selectedText = choice.IsSelected ? " *selected*" : string.Empty;
                string endText = choice.IsEndBiome ? " end" : string.Empty;
                DrawWrappedLabel("  [" + choice.OptionIndex + "] " +
                    (choice.BiomeName ?? choice.BiomeType ?? "[biome]") +
                    " type=" + (choice.BiomeType ?? "[none]") +
                    " goal=" + (choice.BiomeGoalName ?? choice.BiomeGoalId ?? "[none]") +
                    " mod=" + (choice.BiomeModifierName ?? choice.BiomeModifierId ?? "[none]") +
                    endText +
                    selectedText);
                if (!string.IsNullOrWhiteSpace(choice.BiomeGoalDescription))
                {
                    DrawWrappedLabel("    Goal: " + TrimPanelText(CleanTooltip(choice.BiomeGoalDescription), 180));
                }

                if (!string.IsNullOrWhiteSpace(choice.BiomeModifierDescription))
                {
                    DrawWrappedLabel("    Modifier: " + TrimPanelText(CleanTooltip(choice.BiomeModifierDescription), 180));
                }
            }

            if (!snapshot.IsActive)
            {
                return;
            }

            GUILayout.BeginHorizontal();
            for (int i = 0; i < choices.Count; i++)
            {
                InnBiomeChoicePayload choice = choices[i];
                if (choice == null)
                {
                    continue;
                }

                if (GUILayout.Button("Vote Biome " + choice.OptionIndex, GUILayout.Height(28f)))
                {
                    _session.RequestInnSelectBiome(choice.OptionIndex);
                }
            }

            GUILayout.EndHorizontal();

            GUI.enabled = snapshot.CanEmbark;
            if (GUILayout.Button("Vote Embark", GUILayout.Height(30f)))
            {
                _session.RequestInnEmbark();
            }

            GUI.enabled = true;
        }

        private void DrawEmbarkPanelSection()
        {
            GUILayout.Label("Embark Scene");

            EmbarkSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestEmbarkSnapshot(out snapshot))
            {
                DrawWrappedLabel("Embark: none");
                return;
            }

            DrawWrappedLabel("Embark: active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", gameType=" + (snapshot.GameType ?? "[none]") +
                ", started=" + snapshot.EmbarkIsStarted +
                ", camp=" + snapshot.IsCamp +
                ", hasUi=" + snapshot.HasUi +
                ", exiting=" + snapshot.IsExiting +
                ", digest=" + (snapshot.Digest ?? "[none]"));
            DrawWrappedLabel("  next=" + (snapshot.NextBiomeName ?? snapshot.NextBiomeType ?? "[none]") +
                ", type=" + (snapshot.NextBiomeType ?? "[none]") +
                ", goal=" + (snapshot.BiomeGoalId ?? "[none]") +
                ", mod=" + (snapshot.BiomeModifierId ?? "[none]"));
            DrawWrappedLabel("  relationships=" + snapshot.RelationshipCount +
                ", applied=" + snapshot.HasRelationshipsApplied +
                ", applying=" + snapshot.IsApplyingRelationships +
                ", canApply=" + snapshot.CanApplyRelationships +
                ", canContinue=" + snapshot.CanContinue);

            DrawVoteStatus(MultiplayerSession.VoteKeyEmbarkContinue);

            if (!snapshot.IsActive)
            {
                return;
            }

            GUILayout.BeginHorizontal();

            GUI.enabled = snapshot.CanApplyRelationships;
            if (GUILayout.Button("Apply Relationships", GUILayout.Height(28f)))
            {
                _session.RequestEmbarkApplyRelationships();
            }

            GUI.enabled = snapshot.CanContinue;
            if (GUILayout.Button("Vote Continue", GUILayout.Height(28f)))
            {
                _session.RequestEmbarkContinue();
            }

            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawAltarPanelSection()
        {
            GUILayout.Label("Altar of Hope");

            AltarSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestAltarSnapshot(out snapshot))
            {
                DrawWrappedLabel("Altar: none");
                return;
            }

            IList<AltarTrackPayload> tracks = snapshot.Tracks ?? Array.Empty<AltarTrackPayload>();
            IList<AltarRewardButtonPayload> rewards = snapshot.RewardButtons ?? Array.Empty<AltarRewardButtonPayload>();
            DrawWrappedLabel("Altar: active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", screen=" + (snapshot.ActiveSubscreen ?? "[none]") +
                ", hasUi=" + snapshot.HasUi +
                ", system=" + snapshot.HasActiveSystem +
                ", intro=" + snapshot.IsIntro +
                ", candles=" + snapshot.CandleCount +
                ", tracks=" + tracks.Count +
                ", rewards=" + rewards.Count +
                ", exiting=" + snapshot.IsExiting +
                ", changing=" + snapshot.IsGameModeChanging +
                ", canEmbark=" + snapshot.CanEmbark +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            if (!string.IsNullOrWhiteSpace(snapshot.BlockReason))
            {
                DrawWrappedLabel("  Block: " + snapshot.BlockReason);
            }

            DrawVoteStatus(MultiplayerSession.VoteKeyAltarEmbark);

            if (!snapshot.IsActive)
            {
                return;
            }

            if (tracks.Count > 0)
            {
                DrawPanelSeparator();
                GUILayout.Label("Candle Tracks");
                foreach (AltarTrackPayload track in tracks
                    .Where(track => track != null)
                    .OrderBy(track => track.TrackIndex))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("#" + track.TrackIndex +
                        " " + (track.DisplayName ?? track.TrackId ?? "[track]") +
                        " [" + (track.TrackId ?? "[id]") + "]" +
                        " kind=" + (track.TrackKind ?? "[track]") +
                        " spent=" + track.SpentCandles + "/" + track.TotalCandles +
                        " next=" + track.NextMilestoneCandles +
                        " need=" + track.SpendToNext +
                        " can=" + track.CanPurchase,
                        GUILayout.MinWidth(620f));

                    bool oldEnabled = GUI.enabled;
                    GUI.enabled = oldEnabled && track.CanPurchase;
                    if (GUILayout.Button("Spend 1", GUILayout.Width(92f), GUILayout.Height(28f)))
                    {
                        _session.RequestAltarTrackSpend(track, 1f);
                    }

                    GUI.enabled = oldEnabled && track.CanPurchase && track.SpendToNext > 1f && snapshot.CandleCount >= track.SpendToNext;
                    if (GUILayout.Button("To Next", GUILayout.Width(92f), GUILayout.Height(28f)))
                    {
                        _session.RequestAltarTrackSpend(track, track.SpendToNext);
                    }

                    GUI.enabled = oldEnabled;
                    GUILayout.EndHorizontal();
                }
            }

            if (rewards.Count > 0)
            {
                DrawPanelSeparator();
                GUILayout.Label("Reward Buttons");
                foreach (AltarRewardButtonPayload reward in rewards
                    .Where(reward => reward != null)
                    .OrderBy(reward => reward.ButtonIndex))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("#" + reward.ButtonIndex +
                        " " + (reward.DisplayName ?? reward.CurrentUnlockTableId ?? reward.ItemType ?? "[reward]") +
                        " screen=" + (reward.ScreenKind ?? "[screen]") +
                        " table=" + (reward.CurrentUnlockTableId ?? "[none]") +
                        " track=" + (reward.UnlockTrackId ?? "[none]") +
                        " type=" + (reward.ItemType ?? "[none]") +
                        " progress=" + reward.NumUnlocked + "/" + reward.TotalItemCount +
                        " mode=" + (reward.PurchaseMode ?? "[none]") +
                        " cost=" + (reward.CostText ?? "[none]") +
                        " can=" + reward.CanPurchase,
                        GUILayout.MinWidth(720f));

                    bool oldEnabled = GUI.enabled;
                    GUI.enabled = oldEnabled && reward.CanPurchase;
                    if (GUILayout.Button("Purchase", GUILayout.Width(96f), GUILayout.Height(28f)))
                    {
                        _session.RequestAltarRewardPurchase(reward);
                    }

                    GUI.enabled = oldEnabled;
                    GUILayout.EndHorizontal();
                }
            }

            DrawPanelSeparator();
            GUI.enabled = snapshot.CanEmbark;
            if (GUILayout.Button("Vote Leave Altar", GUILayout.Height(28f)))
            {
                _session.RequestAltarEmbark();
            }

            GUI.enabled = true;
        }

        private void DrawConfessionChoicePanelSection()
        {
            GUILayout.Label("Confession Choice");

            ConfessionChoiceSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestConfessionChoiceSnapshot(out snapshot))
            {
                DrawWrappedLabel("Confession: none");
                return;
            }

            IList<ConfessionChoiceOptionPayload> choices = snapshot.Choices ?? Array.Empty<ConfessionChoiceOptionPayload>();
            DrawWrappedLabel("Confession: active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", screen=" + (snapshot.ScreenState ?? "[none]") +
                ", canChoose=" + snapshot.CanChoose +
                ", selected=" + snapshot.SelectedOptionIndex + "/" + (snapshot.SelectedBossId ?? "[none]") +
                ", choices=" + choices.Count +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            DrawVoteStatus(MultiplayerSession.VoteKeyConfessionChoice);

            for (int i = 0; i < choices.Count; i++)
            {
                ConfessionChoiceOptionPayload choice = choices[i];
                if (choice == null)
                {
                    continue;
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label("[" + choice.OptionIndex + "]", GUILayout.Width(44f));
                DrawWrappedLabel((choice.Label ?? choice.BossId ?? "[unknown]") +
                    " | id=" + (choice.BossId ?? "[none]") +
                    " | selectable=" + choice.IsSelectable +
                    " | selected=" + choice.IsSelected);
                GUI.enabled = snapshot.CanChoose && choice.IsSelectable;
                if (GUILayout.Button("Vote", GUILayout.Width(88f), GUILayout.Height(28f)))
                {
                    _session.RequestConfessionChoice(choice);
                }

                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }

        private void DrawLairDecisionPanelSection()
        {
            GUILayout.Label("Lair / Next Battle");

            LairDecisionSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestLairDecisionSnapshot(out snapshot))
            {
                DrawWrappedLabel("Lair: none");
                return;
            }

            DrawWrappedLabel("Lair: active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", screen=" + (snapshot.ScreenState ?? "[none]") +
                ", source=" + (snapshot.CombatSource ?? "[none]") +
                ", digest=" + (snapshot.Digest ?? "[none]"));
            DrawWrappedLabel("  battle=" + snapshot.CurrentBattleIndex + "->" + snapshot.NextBattleIndex +
                " / total=" + snapshot.TotalBattles +
                ", current=" + (snapshot.CurrentBattleConfigurationId ?? "[none]") +
                ", next=" + (snapshot.NextBattleConfigurationId ?? "[none]"));
            DrawWrappedLabel("  rewards: looted=" + snapshot.LootedRewardCount +
                ", upcoming=" + snapshot.UpcomingRewardCount +
                ", canContinue=" + snapshot.CanContinue +
                ", canRetreat=" + snapshot.CanRetreat);

            DrawVoteStatus(MultiplayerSession.VoteKeyLairDecision);

            if (!snapshot.IsActive)
            {
                return;
            }

            GUILayout.BeginHorizontal();

            GUI.enabled = snapshot.CanContinue;
            if (GUILayout.Button("Vote Continue Battle", GUILayout.Height(28f)))
            {
                _session.RequestLairDecision("continue");
            }

            GUI.enabled = snapshot.CanRetreat;
            if (GUILayout.Button("Vote Retreat", GUILayout.Height(28f)))
            {
                _session.RequestLairDecision("retreat");
            }

            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawConfirmationDialogPanelSection()
        {
            GUILayout.Label("Confirmation Dialog");

            ConfirmationDialogSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestConfirmationDialogSnapshot(out snapshot))
            {
                DrawWrappedLabel("Dialog: none");
                return;
            }

            DrawWrappedLabel("Dialog: active=" + snapshot.IsActive +
                ", allowed=" + snapshot.IsAllowed +
                ", kind=" + (snapshot.Kind ?? "[none]") +
                ", type=" + (snapshot.DialogType ?? "[none]") +
                ", screen=" + (snapshot.ScreenState ?? "[none]") +
                ", digest=" + (snapshot.Digest ?? "[none]"));
            if (!string.IsNullOrWhiteSpace(snapshot.Title))
            {
                DrawWrappedLabel("  Title: " + TrimPanelText(snapshot.Title, 160));
            }

            if (!string.IsNullOrWhiteSpace(snapshot.Description))
            {
                DrawWrappedLabel("  Desc: " + TrimPanelText(snapshot.Description, 260));
            }

            if (!snapshot.IsAllowed && !string.IsNullOrWhiteSpace(snapshot.BlockReason))
            {
                DrawWrappedLabel("  Blocked: " + snapshot.BlockReason);
            }

            DrawVoteStatus(MultiplayerSession.VoteKeyConfirmationDialog);

            if (!snapshot.IsActive || !snapshot.IsAllowed)
            {
                return;
            }

            GUILayout.BeginHorizontal();

            GUI.enabled = snapshot.CanConfirm;
            string confirmLabel = string.IsNullOrWhiteSpace(snapshot.ConfirmLabel)
                ? "Vote Confirm"
                : "Vote " + snapshot.ConfirmLabel;
            if (GUILayout.Button(confirmLabel, GUILayout.Height(28f)))
            {
                _session.RequestConfirmationDialog("confirm");
            }

            GUI.enabled = snapshot.CanDecline;
            string declineLabel = string.IsNullOrWhiteSpace(snapshot.DeclineLabel)
                ? "Vote Decline"
                : "Vote " + snapshot.DeclineLabel;
            if (GUILayout.Button(declineLabel, GUILayout.Height(28f)))
            {
                _session.RequestConfirmationDialog("decline");
            }

            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawRouteChoicePanelSection()
        {
            GUILayout.Label("Route Choice");

            RouteChoiceSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestRouteChoiceSnapshot(out snapshot))
            {
                DrawWrappedLabel("Route: none");
                return;
            }

            IList<RouteChoiceOptionPayload> choices = snapshot.Choices ?? Array.Empty<RouteChoiceOptionPayload>();
            DrawWrappedLabel("Route: active=" + snapshot.IsActive +
                ", choices=" + choices.Count +
                "/" + snapshot.ChoiceCount +
                ", selected=" + snapshot.SelectedOptionIndex +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            DrawVoteStatus(MultiplayerSession.VoteKeyRoute);

            for (int i = 0; i < choices.Count; i++)
            {
                RouteChoiceOptionPayload choice = choices[i];
                string nodeText = choice.IsRevealed
                    ? (choice.NodeType ?? "[unknown]")
                    : "Unknown";
                string subType = string.IsNullOrWhiteSpace(choice.NodeSubType) ? string.Empty : " / " + choice.NodeSubType;
                DrawWrappedLabel("  [" + choice.OptionIndex + "] " +
                    (choice.Direction ?? "[direction]") +
                    " -> " + nodeText + subType +
                    " rowIndex=" + choice.NodeIndexInRow);
            }

            if (!snapshot.IsActive || choices.Count == 0)
            {
                return;
            }

            GUILayout.BeginHorizontal();
            for (int i = 0; i < choices.Count; i++)
            {
                RouteChoiceOptionPayload choice = choices[i];
                if (GUILayout.Button("Vote " + choice.OptionIndex, GUILayout.Height(28f)))
                {
                    _session.RequestRouteChoice(choice.OptionIndex);
                }
            }

            GUILayout.EndHorizontal();
        }

        private void DrawStoryChoicePanelSection()
        {
            GUILayout.Label("Story Choice");

            StoryChoiceSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestStoryChoiceSnapshot(out snapshot))
            {
                DrawWrappedLabel("Story: none");
                return;
            }

            IList<StoryChoiceOptionPayload> choices = snapshot.Choices ?? Array.Empty<StoryChoiceOptionPayload>();
            DrawWrappedLabel("Story: active=" + snapshot.IsActive +
                ", type=" + (snapshot.StoryType ?? "[none]") +
                ", state=" + (snapshot.StoryState ?? "[none]") +
                ", engage=" + (snapshot.EngageType ?? "[none]") +
                ", choices=" + choices.Count +
                ", selected=" + (snapshot.SelectedActorGuid ?? "[none]") +
                ", digest=" + (snapshot.Digest ?? "[none]"));

            DrawVoteStatus(MultiplayerSession.VoteKeyStory);

            for (int i = 0; i < choices.Count; i++)
            {
                StoryChoiceOptionPayload choice = choices[i];
                string ownerText = string.IsNullOrWhiteSpace(choice.OwnerName)
                    ? "[unassigned]"
                    : choice.OwnerName + "/" + choice.OwnerSteamId;
                DrawWrappedLabel("  [" + choice.OptionIndex + "] slot " + choice.HeroSlot +
                    " | " + FormatStoryChoiceOption(choice) +
                    " | owner=" + ownerText +
                    " | canChoose=" + choice.CanChoose);

                string previews = FormatStoryPreviewList(choice.PlayerPreviews, "party");
                if (!string.IsNullOrWhiteSpace(previews))
                {
                    DrawWrappedLabel("    " + previews);
                }

                previews = FormatStoryPreviewList(choice.EnemyPreviews, "enemy");
                if (!string.IsNullOrWhiteSpace(previews))
                {
                    DrawWrappedLabel("    " + previews);
                }

                if (!string.IsNullOrWhiteSpace(choice.BarkText))
                {
                    DrawWrappedLabel("    " + choice.BarkText);
                }
            }

            if (!snapshot.IsActive || choices.Count == 0)
            {
                return;
            }

            GUILayout.Label("Story Votes");
            for (int i = 0; i < choices.Count; i++)
            {
                StoryChoiceOptionPayload choice = choices[i];
                if (choice == null)
                {
                    continue;
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label("[" + choice.OptionIndex + "] Slot " + choice.HeroSlot, GUILayout.Width(96f));
                DrawWrappedLabel(FormatStoryChoiceOption(choice));
                GUI.enabled = choice.CanChoose;
                if (GUILayout.Button("Vote", GUILayout.Width(88f), GUILayout.Height(28f)))
                {
                    _session.RequestStoryChoice(choice);
                }

                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }

        private void DrawDiagnosticsPanelSection()
        {
            GUILayout.Label("Diagnostics");
            DrawWrappedLabel(GetVersionPanelStatus());
            if (_session != null)
            {
                DrawWrappedLabel(_session.GetAutoResyncStatus());
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Dump Lobby", GUILayout.Height(28f)))
            {
                _lobbyClient.DumpLobby();
            }

            if (GUILayout.Button("Protocol State", GUILayout.Height(28f)))
            {
                _session.LogProtocolState();
            }

            if (GUILayout.Button("Resync State", GUILayout.Height(28f)))
            {
                _session.RequestFullState("panel");
            }

            if (GUILayout.Button("Native Probe", GUILayout.Height(28f)))
            {
                LogNativeSceneProbe("panel");
            }

            GUILayout.EndHorizontal();
        }

        private void DrawVoteStatus(string voteKey)
        {
            if (_session == null || string.IsNullOrWhiteSpace(voteKey))
            {
                return;
            }

            VoteStatusPayload status;
            if (!_session.TryGetLatestVoteStatus(voteKey, out status) || status == null || !status.IsActive)
            {
                return;
            }

            string stateText = status.IsResolved ? "resolved" : "waiting";
            DrawWrappedLabel("Vote status [" + voteKey + "]: " +
                stateText +
                " " + status.VotedCount + "/" + status.RequiredCount +
                ", digest=" + (status.ContextDigest ?? "[none]"));

            IList<VoteEntryPayload> votes = status.Votes ?? Array.Empty<VoteEntryPayload>();
            if (votes.Count > 0)
            {
                DrawWrappedLabel("  Voted: " + string.Join(", ", votes
                    .Select(vote => (vote.Name ?? vote.SteamId.ToString()) + "=" + (vote.Choice ?? "[none]"))
                    .ToArray()));
            }

            IList<VoteEntryPayload> missing = status.Missing ?? Array.Empty<VoteEntryPayload>();
            if (missing.Count > 0)
            {
                DrawWrappedLabel("  Waiting: " + string.Join(", ", missing
                    .Select(vote => vote.Name ?? vote.SteamId.ToString())
                    .ToArray()));
            }

            if (!string.IsNullOrWhiteSpace(status.Resolution))
            {
                DrawWrappedLabel("  Result: " + status.Resolution);
            }
        }

        private static void DrawWrappedLabel(string text)
        {
            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true,
            };
            GUILayout.Label(text ?? string.Empty, style);
        }

        private static string TrimPanelText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || maxLength <= 0 || text.Length <= maxLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxLength) + "...";
        }

        private string GetLobbyPanelStatus()
        {
            if (_lobbyClient == null || !_lobbyClient.IsInLobby)
            {
                return Ui("No active lobby", "未加入房间");
            }

            CSteamID owner = _lobbyClient.Owner;
            return Ui("Lobby ", "房间 ") + _lobbyClient.CurrentLobby.m_SteamID +
                " | " + (_lobbyClient.IsHost ? Ui("host", "房主") : Ui("client", "客机")) +
                " | " + Ui("owner ", "所有者 ") + _lobbyClient.GetPersonaName(owner) + "/" + owner.m_SteamID;
        }

        private string GetVersionPanelStatus()
        {
            string remoteLobbyVersion = _lobbyClient == null || string.IsNullOrEmpty(_lobbyClient.CurrentLobbyVersion)
                ? "[none]"
                : _lobbyClient.CurrentLobbyVersion;
            string compatible = _lobbyClient == null
                ? "unknown"
                : _lobbyClient.IsLobbyVersionCompatible.ToString();
            return Ui("Version: protocol=", "版本：协议=") + MultiplayerProtocol.CurrentVersion +
                ", local=" + SteamLobbyClient.LocalLobbyVersion +
                ", lobby=" + remoteLobbyVersion +
                ", compatible=" + compatible;
        }

        private string GetPvpPanelStatus()
        {
            PvpModeStatePayload state;
            if (_session == null || !_session.TryGetPvpModeState(out state) || state == null || !state.Enabled)
            {
                return Ui("PVP: off", "PVP：关闭");
            }

            return Ui("PVP: enemy=", "PVP：敌方=") +
                (string.IsNullOrWhiteSpace(state.EnemyControllerName)
                    ? state.EnemyControllerSteamId.ToString()
                    : state.EnemyControllerName);
        }

        private string GetPanelHeaderStatus()
        {
            if (_lobbyClient == null || !_lobbyClient.IsInLobby)
            {
                return Ui("Offline", "离线");
            }

            int memberCount = _lobbyClient.GetMembers().Count;
            return (_lobbyClient.IsHost ? Ui("Host", "房主") : Ui("Client", "客机")) +
                " | " + Ui("members=", "成员=") + memberCount +
                " | " + Ui("lobby=", "房间=") + _lobbyClient.CurrentLobby.m_SteamID;
        }

        private string FormatLobbyMember(CSteamID member)
        {
            string suffix = member == SteamUser.GetSteamID() ? Ui(" (me)", "（我）") : string.Empty;
            return _lobbyClient.GetPersonaName(member) + suffix;
        }

        private void TryJoinFromPanel()
        {
            if (string.IsNullOrWhiteSpace(_panelJoinLobbyId) || !TryParseLobbyId(_panelJoinLobbyId, out ulong lobbyId))
            {
                HostLog.Write("Panel join ignored; lobby id is empty or invalid.");
                return;
            }

            _lobbyClient.JoinLobby(lobbyId);
        }

        private void TrySendPanelSkill(TurnPromptPayload prompt)
        {
            string skillId = (_panelSkillId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(skillId))
            {
                HostLog.Write("Panel skill ignored; skill id is empty.");
                return;
            }

            _session.ChooseSkill(prompt.HeroSlot, prompt.ActorGuid, skillId);
        }

        private void TrySendPanelTarget(TurnPromptPayload prompt)
        {
            string targetGuid = (_panelTargetGuid ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(targetGuid))
            {
                HostLog.Write("Panel target ignored; target guid is empty.");
                return;
            }

            _session.ChooseTarget(prompt.HeroSlot, prompt.ActorGuid, targetGuid);
        }

        private static string FormatSkillOption(TurnSkillOptionPayload option)
        {
            string displayName = string.IsNullOrWhiteSpace(option.DisplayName) ? option.SkillId : option.DisplayName;
            int targetCount = option.Targets == null ? 0 : option.Targets.Count;
            return CleanInline(displayName) + " [" + option.SkillId + "] targets=" + targetCount;
        }

        private static string FormatTargetOption(TurnTargetOptionPayload target)
        {
            string displayName = string.IsNullOrWhiteSpace(target.DisplayName) ? "[unknown]" : target.DisplayName;
            return "team=" + target.TeamIndex +
                " pos=" + target.TeamPosition +
                " | " + displayName +
                " | guid=" + target.ActorGuid;
        }

        private static string FormatHeroSelectActor(string actorGuid, string actorDataId, string actorName, string pathId)
        {
            if (string.IsNullOrWhiteSpace(actorGuid))
            {
                return "[empty]";
            }

            string displayName = string.IsNullOrWhiteSpace(actorName) ? actorDataId : actorName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "[unknown]";
            }

            string path = string.IsNullOrWhiteSpace(pathId) ? string.Empty : " path=" + pathId;
            return displayName +
                " [" + (actorDataId ?? "[id]") + "]" +
                " guid=" + actorGuid +
                path;
        }

        private static string FormatLoadoutActor(HeroLoadoutActorPayload actor)
        {
            if (actor == null || string.IsNullOrWhiteSpace(actor.ActorGuid))
            {
                return "[empty]";
            }

            string displayName = string.IsNullOrWhiteSpace(actor.ActorName) ? actor.ActorDataId : actor.ActorName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "[unknown]";
            }

            string path = string.IsNullOrWhiteSpace(actor.PathId) ? string.Empty : " path=" + actor.PathId;
            return displayName +
                " [" + (actor.ActorDataId ?? "[id]") + "]" +
                " guid=" + actor.ActorGuid +
                path;
        }

        private static string FormatLoadoutItem(HeroLoadoutItemPayload item)
        {
            if (item == null || item.IsEmpty || string.IsNullOrWhiteSpace(item.ItemId))
            {
                return "[empty]";
            }

            string displayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.ItemId : item.DisplayName;
            string quantity = item.Quantity > 1 ? " x" + item.Quantity : string.Empty;
            return displayName + quantity + " [" + item.ItemId + "]";
        }

        private static void DrawExpeditionQuirkLine(string label, IList<ExpeditionQuirkPayload> quirks)
        {
            if (quirks == null || quirks.Count == 0)
            {
                return;
            }

            DrawWrappedLabel(label + ": " + string.Join(", ", quirks
                .Where(quirk => quirk != null)
                .Select(FormatExpeditionQuirk)
                .ToArray()));
        }

        private static void DrawExpeditionItemLine(string label, IList<ExpeditionItemPayload> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            DrawWrappedLabel(label + ": " + string.Join(", ", items
                .Where(item => item != null)
                .Select(FormatExpeditionItemCompact)
                .ToArray()));
        }

        private static void DrawExpeditionItems(string label, IList<ExpeditionItemPayload> items, int maxItems)
        {
            if (items == null || items.Count == 0)
            {
                DrawWrappedLabel(label + ": none");
                return;
            }

            int limit = Math.Max(1, maxItems);
            DrawWrappedLabel(label + ": " + items.Count + (items.Count > limit ? " (showing first " + limit + ")" : string.Empty));
            foreach (ExpeditionItemPayload item in items
                .Where(item => item != null)
                .OrderBy(item => item.Scope)
                .ThenBy(item => item.InventoryIndex)
                .ThenBy(item => item.ItemId)
                .Take(limit))
            {
                DrawWrappedLabel("  " + FormatExpeditionItem(item));
            }
        }

        private static void DrawExpeditionBiomeObjectives(
            ExpeditionBiomeGoalPayload goal,
            ExpeditionBiomeModifierPayload modifier)
        {
            if (goal == null && modifier == null)
            {
                DrawWrappedLabel("Region objective: none");
                return;
            }

            if (goal != null)
            {
                DrawWrappedLabel("Region goal: " + FormatBiomeGoal(goal));
            }

            if (modifier != null)
            {
                DrawWrappedLabel("Region modifier: " + FormatBiomeModifier(modifier));
            }
        }

        private static void DrawExpeditionHeroRunGoal(ExpeditionHeroPayload hero)
        {
            if (hero == null || string.IsNullOrWhiteSpace(hero.RunGoalId))
            {
                return;
            }

            DrawWrappedLabel("    Goal: " + FormatHeroRunGoal(hero));
        }

        private static void DrawExpeditionCombatContext(
            ExpeditionCombatScenarioPayload combatScenario,
            ExpeditionMapProgressPayload mapProgress,
            ExpeditionMapRoutePayload mapRoute,
            ExpeditionMapNodePayload lastVisitedNode,
            ExpeditionMapNodePayload lastCompletedNode)
        {
            if (combatScenario == null &&
                mapProgress == null &&
                mapRoute == null &&
                lastVisitedNode == null &&
                lastCompletedNode == null)
            {
                DrawWrappedLabel("Combat/map context: none");
                return;
            }

            if (combatScenario != null)
            {
                DrawWrappedLabel("Combat scenario: " + FormatCombatScenario(combatScenario));
            }

            if (mapProgress != null)
            {
                DrawWrappedLabel("Map progress: " + FormatMapProgress(mapProgress));
            }

            if (mapRoute != null)
            {
                DrawWrappedLabel("Map route: " + FormatMapRoute(mapRoute));
                foreach (ExpeditionMapRouteRowPayload row in (mapRoute.Rows ?? Array.Empty<ExpeditionMapRouteRowPayload>())
                    .Where(row => row != null)
                    .OrderBy(row => row.RowIndex))
                {
                    DrawWrappedLabel("  " + FormatMapRouteRow(row));
                }
            }

            if (lastVisitedNode != null)
            {
                DrawWrappedLabel("Last visited node: " + FormatMapNode(lastVisitedNode));
            }

            if (lastCompletedNode != null)
            {
                DrawWrappedLabel("Last completed node: " + FormatMapNode(lastCompletedNode));
            }
        }

        private static void DrawExpeditionRelationships(IList<ExpeditionRelationshipPayload> relationships)
        {
            if (relationships == null || relationships.Count == 0)
            {
                DrawWrappedLabel("Relationships: none");
                return;
            }

            DrawWrappedLabel("Relationships: " + relationships.Count +
                ", leaning=" + FormatRelationshipLeaningRange(relationships));
            foreach (ExpeditionRelationshipPayload relationship in relationships
                .Where(relationship => relationship != null)
                .OrderBy(relationship => relationship.TeamPositionA)
                .ThenBy(relationship => relationship.TeamPositionB)
                .ThenBy(relationship => relationship.ActorGuidA)
                .ThenBy(relationship => relationship.ActorGuidB))
            {
                DrawWrappedLabel("  " + FormatExpeditionRelationship(relationship));
            }
        }

        private static string FormatExpeditionQuirk(ExpeditionQuirkPayload quirk)
        {
            if (quirk == null)
            {
                return "[quirk]";
            }

            string displayName = string.IsNullOrWhiteSpace(quirk.DisplayName) ? quirk.QuirkId : quirk.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "[quirk]";
            }

            string suffix = quirk.IsLocked ? " locked" : string.Empty;
            if (quirk.Duration > 0)
            {
                suffix += " dur=" + quirk.Duration;
            }

            return displayName + " [" + (quirk.QuirkId ?? "[id]") + (string.IsNullOrWhiteSpace(suffix) ? string.Empty : ";" + suffix.Trim()) + "]";
        }

        private static string FormatBiomeGoal(ExpeditionBiomeGoalPayload goal)
        {
            if (goal == null)
            {
                return "[none]";
            }

            string description = string.IsNullOrWhiteSpace(goal.Description) ? goal.GoalId : goal.Description;
            if (string.IsNullOrWhiteSpace(description))
            {
                description = "[goal]";
            }

            string threshold = string.Empty;
            if (goal.HasCompleteThreshold)
            {
                threshold = ", complete=" + goal.CompleteThresholdType + " " + goal.CompleteThresholdAmount;
            }
            else if (goal.HasFailThreshold)
            {
                threshold = ", fail=" + goal.FailThresholdType + " " + goal.FailThresholdAmount;
            }

            string types = goal.TypeStrings == null || goal.TypeStrings.Count == 0
                ? string.Empty
                : ", types=" + string.Join("/", goal.TypeStrings.ToArray());
            string reward = string.IsNullOrWhiteSpace(goal.RewardId)
                ? string.Empty
                : ", reward=" + goal.RewardId;

            return description +
                " [" + (goal.GoalId ?? "id") + "]" +
                ", state=" + (goal.State ?? "[state]") +
                ", count=" + goal.CurrentCount +
                threshold +
                ", type=" + (goal.GoalType ?? "[type]") +
                types +
                reward;
        }

        private static string FormatBiomeGoalCompact(ExpeditionBiomeGoalPayload goal)
        {
            if (goal == null)
            {
                return "goal=[none]";
            }

            string description = string.IsNullOrWhiteSpace(goal.Description) ? goal.GoalId : goal.Description;
            if (string.IsNullOrWhiteSpace(description))
            {
                description = "[goal]";
            }

            return "goal=" + description +
                " [" + (goal.GoalId ?? "id") + "]" +
                " state=" + (goal.State ?? "[state]");
        }

        private static string FormatBiomeModifier(ExpeditionBiomeModifierPayload modifier)
        {
            if (modifier == null)
            {
                return "[none]";
            }

            string displayName = string.IsNullOrWhiteSpace(modifier.DisplayName)
                ? modifier.ModifierId
                : modifier.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "[modifier]";
            }

            string tags = modifier.Tags == null || modifier.Tags.Count == 0
                ? string.Empty
                : ", tags=" + string.Join("/", modifier.Tags.ToArray());
            string description = string.IsNullOrWhiteSpace(modifier.Description)
                ? string.Empty
                : ", info=" + modifier.Description;

            return displayName +
                " [" + (modifier.ModifierId ?? "id") + "]" +
                tags +
                description;
        }

        private static string FormatBiomeModifierCompact(ExpeditionBiomeModifierPayload modifier)
        {
            if (modifier == null)
            {
                return "[none]";
            }

            string displayName = string.IsNullOrWhiteSpace(modifier.DisplayName)
                ? modifier.ModifierId
                : modifier.DisplayName;
            return string.IsNullOrWhiteSpace(displayName) ? "[modifier]" : displayName + " [" + (modifier.ModifierId ?? "id") + "]";
        }

        private static string FormatHeroRunGoal(ExpeditionHeroPayload hero)
        {
            if (hero == null || string.IsNullOrWhiteSpace(hero.RunGoalId))
            {
                return "[none]";
            }

            string description = string.IsNullOrWhiteSpace(hero.RunGoalDescription)
                ? hero.RunGoalId
                : hero.RunGoalDescription;
            string progress = string.IsNullOrWhiteSpace(hero.RunGoalProgress)
                ? string.Empty
                : " progress=" + hero.RunGoalProgress;
            string category = string.IsNullOrWhiteSpace(hero.RunGoalCategoryId)
                ? string.Empty
                : " category=" + hero.RunGoalCategoryId;
            string loot = string.IsNullOrWhiteSpace(hero.RunGoalLootTableId)
                ? string.Empty
                : " loot=" + hero.RunGoalLootTableId;

            return description +
                " [" + hero.RunGoalId + "]" +
                " complete=" + hero.RunGoalComplete +
                " score=" + hero.RunGoalScore +
                category +
                progress +
                loot;
        }

        private static string FormatHeroRunGoalCompact(ExpeditionHeroPayload hero)
        {
            if (hero == null || string.IsNullOrWhiteSpace(hero.RunGoalId))
            {
                return "[none]";
            }

            string description = string.IsNullOrWhiteSpace(hero.RunGoalDescription)
                ? hero.RunGoalId
                : hero.RunGoalDescription;
            return (hero.RunGoalComplete ? "done " : string.Empty) +
                description +
                " [" + hero.RunGoalId + "]";
        }

        private static string FormatCombatScenario(ExpeditionCombatScenarioPayload combatScenario)
        {
            if (combatScenario == null)
            {
                return "[none]";
            }

            string status = FormatCombatScenarioStatus(combatScenario);
            string node = string.IsNullOrWhiteSpace(combatScenario.NodeType)
                ? string.Empty
                : ", node=" + combatScenario.NodeType +
                    (string.IsNullOrWhiteSpace(combatScenario.NodeSubType) ? string.Empty : "/" + combatScenario.NodeSubType);
            string additional = string.IsNullOrWhiteSpace(combatScenario.AdditionalBattleConfigurationId)
                ? string.Empty
                : ", additional=" + combatScenario.AdditionalBattleConfigurationId;
            string story = string.IsNullOrWhiteSpace(combatScenario.StoryChoiceId)
                ? string.Empty
                : ", story=" + combatScenario.StoryChoiceId +
                    " actor=" + (combatScenario.StoryActorDataId ?? combatScenario.StoryActorGuid ?? "[actor]") +
                    " retry=" + combatScenario.StoryRetryCount;
            string sequence = combatScenario.BattleConfigurationIds == null || combatScenario.BattleConfigurationIds.Count == 0
                ? string.Empty
                : ", sequence=" + string.Join(">", combatScenario.BattleConfigurationIds.ToArray());
            string enemies = combatScenario.EnemyActorIds == null || combatScenario.EnemyActorIds.Count == 0
                ? string.Empty
                : ", enemies=" + string.Join("/", combatScenario.EnemyActorIds.ToArray());
            string tags = combatScenario.Tags == null || combatScenario.Tags.Count == 0
                ? string.Empty
                : ", tags=" + string.Join("/", combatScenario.Tags.ToArray());

            return "source=" + (combatScenario.CombatSource ?? "[source]") +
                node +
                ", battle=" + (combatScenario.CurrentBattleConfigurationId ?? "[battle]") +
                additional +
                ", index=" + combatScenario.CurrentBattleNumber + "/" + combatScenario.TotalNumberOfBattles +
                ", remaining=" + combatScenario.RemainingNumberOfBattles +
                ", status=" + status +
                ", bg=" + (combatScenario.BackgroundSceneName ?? "[scene]") +
                ", next=" + combatScenario.HasNextBattle +
                ", optionalNext=" + combatScenario.IsNextBattleOptional +
                ", addl=" + combatScenario.HasAdditionalBattle +
                ", boss=" + combatScenario.IsExpeditionBoss +
                (combatScenario.BiomeKillContractGuid == 0U ? string.Empty : ", contract=" + combatScenario.BiomeKillContractGuid) +
                story +
                sequence +
                enemies +
                tags;
        }

        private static string FormatCombatScenarioCompact(ExpeditionCombatScenarioPayload combatScenario)
        {
            if (combatScenario == null)
            {
                return "[none]";
            }

            return (combatScenario.CombatSource ?? "[source]") +
                " " + (combatScenario.CurrentBattleConfigurationId ?? "[battle]") +
                " " + combatScenario.CurrentBattleNumber + "/" + combatScenario.TotalNumberOfBattles +
                " " + FormatCombatScenarioStatus(combatScenario);
        }

        private static string FormatCombatScenarioStatus(ExpeditionCombatScenarioPayload combatScenario)
        {
            if (combatScenario == null)
            {
                return "[none]";
            }

            if (combatScenario.IsLoadingCombatIntro)
            {
                return "intro";
            }

            if (combatScenario.IsLoading)
            {
                return "loading";
            }

            if (combatScenario.IsLoaded)
            {
                return "loaded";
            }

            if (combatScenario.IsUnloading)
            {
                return "unloading";
            }

            if (combatScenario.IsUnloaded)
            {
                return "unloaded";
            }

            return combatScenario.IsLoadStarted ? "started" : "created";
        }

        private static string FormatMapProgress(ExpeditionMapProgressPayload progress)
        {
            if (progress == null)
            {
                return "[none]";
            }

            if (!progress.IsValid)
            {
                return "invalid";
            }

            return "biome=" + progress.BiomeIndex +
                ", row=" + progress.RowIndex + "/" + progress.RowCount +
                ", index=" + progress.NodeIndex +
                ", atNode=" + progress.IsAtNode +
                ", travel=" + FormatRatio(progress.BiomeTravelRatio) +
                ", rows=" + FormatRatio(progress.BetweenRowsRatio) +
                ", biomes=" + FormatRatio(progress.BetweenBiomesRatio);
        }

        private static string FormatMapProgressCompact(ExpeditionMapProgressPayload progress)
        {
            if (progress == null)
            {
                return "[none]";
            }

            if (!progress.IsValid)
            {
                return "invalid";
            }

            return "biome=" + progress.BiomeIndex +
                " row=" + progress.RowIndex + "/" + progress.RowCount +
                " index=" + progress.NodeIndex +
                " travel=" + FormatRatio(progress.BiomeTravelRatio) +
                (progress.IsAtNode ? " atNode" : string.Empty);
        }

        private static string FormatMapRoute(ExpeditionMapRoutePayload route)
        {
            if (route == null)
            {
                return "[none]";
            }

            return "biome=" + route.BiomeIndex +
                ", rows=" + route.RowCount +
                ", nodes=" + route.RevealedNodeCount + "/" + route.NodeCount + " revealed" +
                ", links=" + route.RevealedLinkCount + "/" + route.LinkCount + " revealed" +
                ", current=" + route.CurrentRowIndex + ":" + route.CurrentNodeIndex +
                ", visited=" + route.LastVisitedRowIndex + ":" + route.LastVisitedNodeIndex +
                ", completed=" + route.LastCompletedRowIndex + ":" + route.LastCompletedNodeIndex;
        }

        private static string FormatMapRouteCompact(ExpeditionMapRoutePayload route)
        {
            if (route == null)
            {
                return "[none]";
            }

            return "rows=" + route.RowCount +
                ", nodes=" + route.RevealedNodeCount + "/" + route.NodeCount +
                ", links=" + route.RevealedLinkCount + "/" + route.LinkCount +
                ", current=" + route.CurrentRowIndex + ":" + route.CurrentNodeIndex;
        }

        private static string FormatMapRouteRow(ExpeditionMapRouteRowPayload row)
        {
            if (row == null)
            {
                return "[row]";
            }

            string flags =
                (row.IsCurrentRow ? " current" : string.Empty) +
                (row.IsLastVisitedRow ? " visited" : string.Empty) +
                (row.IsLastCompletedRow ? " completed" : string.Empty);

            string nodes = row.Nodes == null || row.Nodes.Count == 0
                ? "nodes=[none]"
                : "nodes=" + string.Join(", ", row.Nodes
                    .Where(node => node != null)
                    .OrderBy(node => node.NodeIndex)
                    .Select(FormatMapRouteNode)
                    .ToArray());
            string links = row.Links == null || row.Links.Count == 0
                ? "links=[none]"
                : "links=" + string.Join(", ", row.Links
                    .Where(link => link != null)
                    .OrderBy(link => link.FromNodeIndex)
                    .ThenBy(link => link.ToNodeIndex)
                    .Select(FormatMapRouteLink)
                    .ToArray());

            return "Row " + row.RowIndex + flags + ": " + nodes + " | " + links;
        }

        private static string FormatMapRouteNode(ExpeditionMapRouteNodePayload node)
        {
            if (node == null)
            {
                return "[node]";
            }

            string flags =
                (node.IsCurrentNode ? "*" : string.Empty) +
                (node.IsLastVisitedNode ? "v" : string.Empty) +
                (node.IsLastCompletedNode ? "c" : string.Empty) +
                (node.HasBiomeKillContract ? "!" : string.Empty);
            string subtype = string.IsNullOrWhiteSpace(node.NodeSubType) ? string.Empty : "/" + node.NodeSubType;
            string reveal = node.IsRevealed ? string.Empty : " hidden";
            return node.NodeIndex +
                ":" + (node.NodeType ?? "[type]") +
                subtype +
                (string.IsNullOrWhiteSpace(flags) ? string.Empty : "[" + flags + "]") +
                reveal;
        }

        private static string FormatMapRouteLink(ExpeditionMapRouteLinkPayload link)
        {
            if (link == null)
            {
                return "[link]";
            }

            string routeType = string.IsNullOrWhiteSpace(link.RouteType) ? "[route]" : link.RouteType;
            string routeId = string.IsNullOrWhiteSpace(link.RouteId) ? string.Empty : "/" + link.RouteId;
            string flags =
                (link.IsRevealed ? string.Empty : " hidden") +
                (link.IsChosen ? " chosen" : string.Empty);
            return link.FromNodeIndex + ">" + link.ToNodeIndex + ":" + routeType + routeId + flags;
        }

        private static string FormatMapNode(ExpeditionMapNodePayload node)
        {
            if (node == null)
            {
                return "[none]";
            }

            return "row=" + node.RowIndex +
                ", index=" + node.NodeIndex +
                ", type=" + (node.NodeType ?? "[type]") +
                (string.IsNullOrWhiteSpace(node.NodeSubType) ? string.Empty : "/" + node.NodeSubType) +
                ", in=" + node.IncomingPathCount +
                ", out=" + node.OutgoingPathCount;
        }

        private static string FormatMapNodeCompact(ExpeditionMapNodePayload node)
        {
            if (node == null)
            {
                return "[none]";
            }

            return node.RowIndex + ":" + node.NodeIndex +
                " " + (node.NodeType ?? "[type]") +
                (string.IsNullOrWhiteSpace(node.NodeSubType) ? string.Empty : "/" + node.NodeSubType);
        }

        private static string FormatExpeditionItemCompact(ExpeditionItemPayload item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ItemId))
            {
                return "[item]";
            }

            string displayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.ItemId : item.DisplayName;
            string quantity = item.Quantity > 1 ? " x" + item.Quantity : string.Empty;
            return displayName + quantity + " [" + item.ItemId + "]";
        }

        private static string FormatExpeditionItem(ExpeditionItemPayload item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ItemId))
            {
                return "[item]";
            }

            string displayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.ItemId : item.DisplayName;
            string quantity = item.Quantity > 1 ? " x" + item.Quantity : string.Empty;
            string type = string.IsNullOrWhiteSpace(item.ItemType) ? string.Empty : " type=" + item.ItemType;
            string slot = string.IsNullOrWhiteSpace(item.SlotType) ? string.Empty : " slot=" + item.SlotType;
            return (item.Scope ?? "[scope]") +
                " #" + item.InventoryIndex +
                " " + displayName + quantity +
                " [" + item.ItemId + "]" +
                type +
                slot;
        }

        private static string FormatExpeditionRelationship(ExpeditionRelationshipPayload relationship)
        {
            if (relationship == null)
            {
                return "[relationship]";
            }

            string actorA = FormatRelationshipActor(
                relationship.HeroSlotA,
                relationship.ActorNameA,
                relationship.ActorDataIdA,
                relationship.ActorGuidA);
            string actorB = FormatRelationshipActor(
                relationship.HeroSlotB,
                relationship.ActorNameB,
                relationship.ActorDataIdB,
                relationship.ActorGuidB);
            string relationshipName = string.IsNullOrWhiteSpace(relationship.RelationshipName)
                ? relationship.RelationshipId
                : relationship.RelationshipName;
            if (string.IsNullOrWhiteSpace(relationshipName))
            {
                relationshipName = "[none]";
            }

            string status = relationship.HasPendingRelationship
                ? "pending"
                : relationship.HasCurrentRelationship ? "current" : "none";
            string duration = relationship.HasRelationshipDuration
                ? ", days=" + relationship.RelationshipDurationRemaining
                : string.Empty;
            string level = string.IsNullOrWhiteSpace(relationship.LeaningLevelId)
                ? string.Empty
                : ", level=" + relationship.LeaningLevelId;

            return actorA + " <-> " + actorB +
                ": leaning=" + relationship.Leaning +
                "/" + relationship.LeaningMin +
                ".." + relationship.LeaningMax +
                " (" + FormatRatio(relationship.LeaningPercent) + ")" +
                level +
                ", relationship=" + relationshipName +
                " [" + (relationship.RelationshipId ?? "none") + "]" +
                ", kind=" + (relationship.RelationshipKind ?? "none") +
                ", status=" + status +
                duration;
        }

        private static string FormatRelationshipActor(
            int heroSlot,
            string actorName,
            string actorDataId,
            string actorGuid)
        {
            string displayName = string.IsNullOrWhiteSpace(actorName) ? actorDataId : actorName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "[actor]";
            }

            string slot = heroSlot > 0 ? "S" + heroSlot + " " : string.Empty;
            return slot + displayName + " [" + (actorDataId ?? "id") + "/" + (actorGuid ?? "guid") + "]";
        }

        private static string FormatRelationshipLeaningRange(IList<ExpeditionRelationshipPayload> relationships)
        {
            IList<ExpeditionRelationshipPayload> validRelationships =
                (relationships ?? Array.Empty<ExpeditionRelationshipPayload>())
                    .Where(relationship => relationship != null)
                    .ToList();
            if (validRelationships.Count == 0)
            {
                return "[none]";
            }

            int min = validRelationships.Min(relationship => relationship.Leaning);
            int max = validRelationships.Max(relationship => relationship.Leaning);
            double average = validRelationships.Average(relationship => relationship.Leaning);
            return min + ".." + max + " avg=" + average.ToString("0.0");
        }

        private static bool IsLocalHeroLoadoutOwner(HeroLoadoutActorPayload actor)
        {
            return actor != null &&
                actor.OwnerSteamId != 0UL &&
                actor.OwnerSteamId == SteamUser.GetSteamID().m_SteamID;
        }

        private static string FormatStoryChoiceOption(StoryChoiceOptionPayload choice)
        {
            if (choice == null)
            {
                return "[missing]";
            }

            string actorName = string.IsNullOrWhiteSpace(choice.ActorName) ? choice.ActorDataId : choice.ActorName;
            if (string.IsNullOrWhiteSpace(actorName))
            {
                actorName = "[unknown]";
            }

            return CleanInline(actorName) +
                " [" + (choice.ActorDataId ?? "[id]") + "]" +
                " choice=" + (choice.ChoiceId ?? "[unknown]") +
                " result=" + (choice.ResultType ?? "[unknown]");
        }

        private static string FormatStoryPreviewList(IList<StoryChoicePreviewPayload> previews, string label)
        {
            if (previews == null || previews.Count == 0)
            {
                return string.Empty;
            }

            return label + ": " + string.Join(", ", previews
                .Where(preview => preview != null && !string.IsNullOrWhiteSpace(preview.PreviewId))
                .Select(preview =>
                    CleanInline(string.IsNullOrWhiteSpace(preview.DisplayName) ? preview.PreviewId : preview.DisplayName) +
                    (preview.ShowNumber ? "=" + preview.Value : string.Empty))
                .ToArray());
        }

        private static string FormatRatio(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return "[invalid]";
            }

            return value.ToString("0.00");
        }

        private void MeasureSnapshotPoll(string label, Action poll)
        {
            if (!SnapshotPerfLoggingEnabled || poll == null)
            {
                if (poll != null)
                {
                    poll();
                }

                return;
            }

            int pollsBefore = _snapshotPollsThisFrame;
            long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                poll();
            }
            finally
            {
                if (_snapshotPollsThisFrame > pollsBefore)
                {
                    double elapsedMs = StopwatchTicksToMilliseconds(
                        System.Diagnostics.Stopwatch.GetTimestamp() - startTicks);
                    RecordSnapshotPerf(label, elapsedMs);
                }
            }
        }

        private void RecordSnapshotPerf(string label, double elapsedMs)
        {
            SnapshotPerfStats stats;
            if (!_snapshotPerfStats.TryGetValue(label, out stats))
            {
                stats = new SnapshotPerfStats();
                _snapshotPerfStats[label] = stats;
            }

            stats.Calls++;
            stats.TotalMs += elapsedMs;
            if (elapsedMs > stats.MaxMs)
            {
                stats.MaxMs = elapsedMs;
            }

            if (elapsedMs >= SnapshotPerfSlowThresholdMs)
            {
                stats.SlowCalls++;
                if (Time.unscaledTime >= stats.NextSlowLogTime)
                {
                    stats.NextSlowLogTime = Time.unscaledTime + SnapshotPerfSummaryInterval;
                    HostLog.Write("[perf/snapshot/slow] " + label +
                        " poll took " + FormatPerfMs(elapsedMs) +
                        " ms. Further slow samples for this label are summarized.");
                }
            }
        }

        private void LogSnapshotPerfSummaryIfDue()
        {
            if (!SnapshotPerfLoggingEnabled || Time.unscaledTime < _nextSnapshotPerfSummaryTime)
            {
                return;
            }

            _nextSnapshotPerfSummaryTime = Time.unscaledTime + SnapshotPerfSummaryInterval;
            List<string> rows = _snapshotPerfStats
                .Where(pair => pair.Value.Calls > 0)
                .OrderByDescending(pair => pair.Value.TotalMs)
                .ThenByDescending(pair => pair.Value.MaxMs)
                .Take(8)
                .Select(pair =>
                {
                    SnapshotPerfStats stats = pair.Value;
                    double averageMs = stats.TotalMs / Math.Max(1, stats.Calls);
                    return pair.Key +
                        " calls=" + stats.Calls +
                        " avg=" + FormatPerfMs(averageMs) +
                        " max=" + FormatPerfMs(stats.MaxMs) +
                        " slow=" + stats.SlowCalls;
                })
                .ToList();

            if (rows.Count > 0)
            {
                HostLog.Write("[perf/snapshot] last " +
                    FormatPerfMs(SnapshotPerfSummaryInterval) + "s top: " +
                    string.Join("; ", rows.ToArray()));
            }

            foreach (SnapshotPerfStats stats in _snapshotPerfStats.Values)
            {
                stats.Calls = 0;
                stats.SlowCalls = 0;
                stats.TotalMs = 0;
                stats.MaxMs = 0;
            }
        }

        private bool TryBeginSnapshotPoll(float nextPollTime)
        {
            if (Time.unscaledTime < nextPollTime)
            {
                return false;
            }

            if (_snapshotPollsThisFrame >= MaxSnapshotPollsPerFrame)
            {
                return false;
            }

            _snapshotPollsThisFrame++;
            return true;
        }

        private static float GetSnapshotPollInterval(bool isActive)
        {
            return isActive ? ActiveSnapshotPollInterval : InactiveSnapshotPollInterval;
        }

        private static float GetSnapshotPollInterval(bool isActive, float activeInterval)
        {
            return isActive ? activeInterval : InactiveSnapshotPollInterval;
        }

        private static float GetSnapshotForcedSendInterval(bool isActive)
        {
            return isActive ? ActiveSnapshotForcedSendInterval : InactiveSnapshotForcedSendInterval;
        }

        private static double StopwatchTicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static string FormatPerfMs(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private List<int> GetLocalOwnedHeroSelectSlotIndexes(IList<HeroSelectSlotPayload> slots)
        {
            List<int> slotIndexes = new List<int>();
            if (slots == null || _session == null)
            {
                return slotIndexes;
            }

            for (int i = 0; i < slots.Count; i++)
            {
                HeroSelectSlotPayload slot = slots[i];
                if (slot == null)
                {
                    continue;
                }

                HeroSlotAssignmentPayload owner;
                if (_session.TryGetHeroSlotOwner(slot.HeroSlot, out owner) && IsLocalTurnOwner(owner))
                {
                    slotIndexes.Add(slot.SlotIndex);
                }
            }

            return slotIndexes.Distinct().OrderBy(slotIndex => slotIndex).ToList();
        }

        private void SyncLootVoteSelection(LootWindowSnapshotPayload snapshot)
        {
            string digest = snapshot == null || !snapshot.IsActive ? null : snapshot.Digest;
            if (!string.Equals(_lootVoteSelectionDigest, digest, StringComparison.Ordinal))
            {
                _lootVoteSelectionDigest = digest;
                _lootVoteSelectedIndexes.Clear();
            }

            IList<LootItemSnapshotPayload> items = snapshot == null
                ? null
                : snapshot.Items;
            if (items == null || items.Count == 0)
            {
                _lootVoteSelectedIndexes.Clear();
                return;
            }

            HashSet<int> currentIndexes = new HashSet<int>(
                items.Where(item => item != null && item.InventoryIndex >= 0)
                    .Select(item => item.InventoryIndex));
            foreach (int index in _lootVoteSelectedIndexes.Where(index => !currentIndexes.Contains(index)).ToArray())
            {
                _lootVoteSelectedIndexes.Remove(index);
            }
        }

        private static bool IsLocalTurnOwner(HeroSlotAssignmentPayload owner)
        {
            if (owner == null)
            {
                return false;
            }

            try
            {
                return owner.SteamId == SteamUser.GetSteamID().m_SteamID;
            }
            catch
            {
                return false;
            }
        }

        private bool IsLocalPvpEnemyController()
        {
            PvpModeStatePayload state;
            if (_session == null ||
                !_session.TryGetPvpModeState(out state) ||
                state == null ||
                !state.Enabled ||
                state.EnemyControllerSteamId == 0UL)
            {
                return false;
            }

            try
            {
                return state.EnemyControllerSteamId == SteamUser.GetSteamID().m_SteamID;
            }
            catch
            {
                return false;
            }
        }

        private void SyncPanelPendingKey(TurnPromptPayload prompt)
        {
            string key = prompt == null
                ? null
                : prompt.Round + ":" + prompt.Turn + ":" + prompt.HeroSlot + ":" + prompt.ActorGuid;
            if (string.Equals(_panelPendingKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _panelPendingKey = key;
            _panelSkillId = string.Empty;
            _panelTargetGuid = string.Empty;
        }

        private void ClampPanelRectToScreen()
        {
            float maxWidth = Mathf.Max(320f, Screen.width - 20f);
            float maxHeight = Mathf.Max(260f, Screen.height - 20f);
            float minWidth = Mathf.Min(PanelMinWidth, maxWidth);
            float minHeight = Mathf.Min(PanelMinHeight, maxHeight);
            _panelRect.width = Mathf.Clamp(_panelRect.width, minWidth, maxWidth);
            _panelRect.height = Mathf.Clamp(_panelRect.height, minHeight, maxHeight);
            _panelRect.x = Mathf.Clamp(_panelRect.x, 0f, Mathf.Max(0f, Screen.width - _panelRect.width));
            _panelRect.y = Mathf.Clamp(_panelRect.y, 0f, Mathf.Max(0f, Screen.height - _panelRect.height));
        }

        private void ClampArenaBattlePresetBrowserRectToScreen()
        {
            float maxWidth = Mathf.Max(480f, Screen.width - 20f);
            float maxHeight = Mathf.Max(360f, Screen.height - 20f);
            float minWidth = Mathf.Min(880f, maxWidth);
            float minHeight = Mathf.Min(560f, maxHeight);
            _arenaBattlePresetBrowserRect.width = Mathf.Clamp(_arenaBattlePresetBrowserRect.width, minWidth, maxWidth);
            _arenaBattlePresetBrowserRect.height = Mathf.Clamp(_arenaBattlePresetBrowserRect.height, minHeight, maxHeight);
            _arenaBattlePresetBrowserRect.x = Mathf.Clamp(_arenaBattlePresetBrowserRect.x, 0f, Mathf.Max(0f, Screen.width - _arenaBattlePresetBrowserRect.width));
            _arenaBattlePresetBrowserRect.y = Mathf.Clamp(_arenaBattlePresetBrowserRect.y, 0f, Mathf.Max(0f, Screen.height - _arenaBattlePresetBrowserRect.height));
        }

        private void InitializeCommandFileCursor()
        {
            try
            {
                if (File.Exists(HostPaths.CommandPath))
                {
                    _lastCommandWriteTimeUtc = File.GetLastWriteTimeUtc(HostPaths.CommandPath);
                    HostLog.Write("Command file cursor initialized; existing command will not be replayed.");
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("Failed to initialize command file cursor: " + ex.Message);
            }
        }

        private void PollCommandFile()
        {
            if (_lobbyClient == null || Time.unscaledTime < _nextCommandPollTime)
            {
                return;
            }

            _nextCommandPollTime = Time.unscaledTime + 1f;

            try
            {
                if (!File.Exists(HostPaths.CommandPath))
                {
                    return;
                }

                DateTime writeTimeUtc = File.GetLastWriteTimeUtc(HostPaths.CommandPath);
                if (writeTimeUtc <= _lastCommandWriteTimeUtc)
                {
                    return;
                }

                _lastCommandWriteTimeUtc = writeTimeUtc;
                string command = File.ReadAllText(HostPaths.CommandPath).Trim();
                ExecuteCommand(command);
            }
            catch (Exception ex)
            {
                HostLog.Write("Command file polling failed: " + ex.Message);
            }
        }

        private void PollAutoTurnPrompts()
        {
            if (Time.unscaledTime < _nextAutoTurnPollTime)
            {
                return;
            }

            _nextAutoTurnPollTime = Time.unscaledTime + 0.25f;

            if (!_autoTurnPromptsEnabled ||
                _lobbyClient == null ||
                _session == null ||
                _combatAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            CombatTurnInfo info;
            HeroSlotAssignmentPayload pvpEnemyOwner = null;
            bool pvpEnemyControl = _session.TryGetPvpEnemyController(out pvpEnemyOwner);
            if (!_combatAdapter.TryGetCurrentTurnInfo(pvpEnemyControl, out info))
            {
                ResetAutoTurnMemory();
                return;
            }

            if (IsRememberedAutoTurn(info, info.HeroSlot))
            {
                return;
            }

            HeroSlotAssignmentPayload owner;
            if (info.IsHeroTeam)
            {
                if (!_session.TryGetHeroSlotOwner(info.HeroSlot, out owner))
                {
                    LogAutoTurnSkipOnce(info, "slot " + info.HeroSlot + " is not assigned");
                    return;
                }
            }
            else if (pvpEnemyControl)
            {
                owner = pvpEnemyOwner;
            }
            else
            {
                LogAutoTurnSkipOnce(info, "enemy turn without active PVP enemy controller");
                return;
            }

            HostLog.Write("[autoturn] prompting " + info.ControlRole +
                " slot " + info.HeroSlot +
                " owner=" + owner.Name + "/" + owner.SteamId +
                ", team=" + info.TeamIndex + ":" + info.TeamPosition +
                ", actor=" + info.ActorName + "/" + info.ActorGuid +
                ", round=" + info.Round +
                ", turn=" + info.Turn + ".");

            _session.SendTurnPrompt(
                info.Round,
                info.Turn,
                info.HeroSlot,
                info.ActorGuidText,
                info.ActorName,
                info.TeamIndex,
                info.TeamPosition,
                info.ControlRole,
                owner,
                _combatAdapter.GetCurrentTurnSkillOptions(pvpEnemyControl));
            RememberAutoTurn(info, info.HeroSlot);
        }

        private void PollPvpEnemyControllerRuntime()
        {
            if (_combatAdapter == null || _session == null || _lobbyClient == null || !_lobbyClient.IsHost)
            {
                return;
            }

            if (Time.unscaledTime < _nextPvpControllerPollTime)
            {
                return;
            }

            _nextPvpControllerPollTime = Time.unscaledTime + 0.5f;

            HeroSlotAssignmentPayload enemyOwner;
            bool shouldUseInputControllers = _session.TryGetPvpEnemyController(out enemyOwner);
            if (!shouldUseInputControllers)
            {
                if (_pvpEnemyInputControllersActive)
                {
                    _combatAdapter.SetEnemyTeamInputControllers(false);
                    _pvpEnemyInputControllersActive = false;
                    _lastPvpEnemyControllerDigest = null;
                    ResetAutoTurnMemory();
                }

                return;
            }

            string digest;
            if (!_combatAdapter.TryGetEnemyControllerDigest(out digest))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(digest) || string.Equals(digest, "inactive", StringComparison.Ordinal))
            {
                return;
            }

            if (_pvpEnemyInputControllersActive &&
                string.Equals(_lastPvpEnemyControllerDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _combatAdapter.SetEnemyTeamInputControllers(true);
            _pvpEnemyInputControllersActive = true;
            string updatedDigest;
            _lastPvpEnemyControllerDigest = _combatAdapter.TryGetEnemyControllerDigest(out updatedDigest)
                ? updatedDigest
                : digest;
            ResetAutoTurnMemory();
        }

        private void PollCombatSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextCombatSnapshotPollTime))
            {
                return;
            }

            _nextCombatSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _combatAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            CombatSnapshotPayload snapshot;
            if (!_combatAdapter.TryGetCombatSnapshot(out snapshot))
            {
                snapshot = CreateInactiveCombatSnapshot();
            }

            bool isActive = snapshot.PartyInBattle || (snapshot.Actors != null && snapshot.Actors.Count > 0);
            _nextCombatSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(isActive);
            bool forceSend = Time.unscaledTime >= _nextCombatSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastCombatSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastCombatSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextCombatSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(isActive);
            }

            _session.SendCombatSnapshot(snapshot);
        }

        private void PollLootWindowSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextLootWindowSnapshotPollTime))
            {
                return;
            }

            _nextLootWindowSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _resultSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            LootWindowSnapshotPayload snapshot;
            if (!_resultSyncAdapter.TryGetLootWindowSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextLootWindowSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextLootWindowSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastLootWindowSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLootWindowSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextLootWindowSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendLootWindowSnapshot(snapshot);
        }

        private void PollGameResultsSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextGameResultsSnapshotPollTime))
            {
                return;
            }

            _nextGameResultsSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _resultSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            GameResultsSnapshotPayload snapshot;
            if (!_resultSyncAdapter.TryGetGameResultsSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextGameResultsSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextGameResultsSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastGameResultsSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastGameResultsSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextGameResultsSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendGameResultsSnapshot(snapshot);
        }

        private void PollRouteChoiceSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextRouteChoiceSnapshotPollTime))
            {
                return;
            }

            _nextRouteChoiceSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _routeSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            RouteChoiceSnapshotPayload snapshot;
            if (!_routeSyncAdapter.TryGetRouteChoiceSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextRouteChoiceSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextRouteChoiceSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastRouteChoiceSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastRouteChoiceSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextRouteChoiceSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendRouteChoiceSnapshot(snapshot);
        }

        private void PollHeroSelectSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextHeroSelectSnapshotPollTime))
            {
                return;
            }

            _nextHeroSelectSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _heroSelectSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            HeroSelectSnapshotPayload snapshot;
            if (!_heroSelectSyncAdapter.TryGetHeroSelectSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextHeroSelectSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive);
            bool forceSend = Time.unscaledTime >= _nextHeroSelectSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastHeroSelectSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastHeroSelectSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextHeroSelectSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendHeroSelectSnapshot(snapshot);
        }

        private void PollHeroLoadoutSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextHeroLoadoutSnapshotPollTime))
            {
                return;
            }

            _nextHeroLoadoutSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _heroLoadoutSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            HeroLoadoutSnapshotPayload snapshot;
            if (!_heroLoadoutSyncAdapter.TryGetHeroLoadoutSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextHeroLoadoutSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextHeroLoadoutSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastHeroLoadoutSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastHeroLoadoutSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextHeroLoadoutSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendHeroLoadoutSnapshot(snapshot);
        }

        private void PollRunStateSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextRunStateSnapshotPollTime))
            {
                return;
            }

            _nextRunStateSnapshotPollTime = Time.unscaledTime + RunStateSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _runStateSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            RunStateSnapshotPayload snapshot;
            if (!_runStateSyncAdapter.TryGetRunStateSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            bool forceSend = Time.unscaledTime >= _nextRunStateSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastRunStateSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastRunStateSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextRunStateSnapshotForcedSendTime = Time.unscaledTime + RunStateSnapshotForcedSendInterval;
            }

            _session.SendRunStateSnapshot(snapshot);
        }

        private void PollExpeditionOverviewSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextExpeditionOverviewSnapshotPollTime))
            {
                return;
            }

            _nextExpeditionOverviewSnapshotPollTime = Time.unscaledTime + OverviewSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _expeditionOverviewSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            ExpeditionOverviewSnapshotPayload snapshot;
            if (!_expeditionOverviewSyncAdapter.TryGetExpeditionOverviewSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            bool forceSend = Time.unscaledTime >= _nextExpeditionOverviewSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastExpeditionOverviewSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastExpeditionOverviewSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextExpeditionOverviewSnapshotForcedSendTime = Time.unscaledTime + OverviewSnapshotForcedSendInterval;
            }

            _session.SendExpeditionOverviewSnapshot(snapshot);
        }

        private void PollMainMenuSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextMainMenuSnapshotPollTime))
            {
                return;
            }

            _nextMainMenuSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _mainMenuSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            MainMenuSnapshotPayload snapshot;
            if (!_mainMenuSyncAdapter.TryGetMainMenuSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextMainMenuSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextMainMenuSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastMainMenuSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastMainMenuSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextMainMenuSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendMainMenuSnapshot(snapshot);
        }

        private void PollStoryChoiceSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextStoryChoiceSnapshotPollTime))
            {
                return;
            }

            _nextStoryChoiceSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _storyChoiceSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            StoryChoiceSnapshotPayload snapshot;
            if (!_storyChoiceSyncAdapter.TryGetStoryChoiceSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextStoryChoiceSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextStoryChoiceSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastStoryChoiceSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastStoryChoiceSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextStoryChoiceSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendStoryChoiceSnapshot(snapshot);
        }

        private void PollInnSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextInnSnapshotPollTime))
            {
                return;
            }

            _nextInnSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _innSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            InnSnapshotPayload snapshot;
            if (!_innSyncAdapter.TryGetInnSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextInnSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive);
            bool forceSend = Time.unscaledTime >= _nextInnSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastInnSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastInnSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextInnSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendInnSnapshot(snapshot);
        }

        private void PollEmbarkSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextEmbarkSnapshotPollTime))
            {
                return;
            }

            _nextEmbarkSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _embarkSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            EmbarkSnapshotPayload snapshot;
            if (!_embarkSyncAdapter.TryGetEmbarkSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextEmbarkSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextEmbarkSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastEmbarkSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastEmbarkSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextEmbarkSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendEmbarkSnapshot(snapshot);
        }

        private void PollAltarSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextAltarSnapshotPollTime))
            {
                return;
            }

            _nextAltarSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _altarSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            AltarSnapshotPayload snapshot;
            if (!_altarSyncAdapter.TryGetAltarSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextAltarSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextAltarSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastAltarSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastAltarSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextAltarSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendAltarSnapshot(snapshot);
        }

        private void PollConfessionChoiceSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextConfessionChoiceSnapshotPollTime))
            {
                return;
            }

            _nextConfessionChoiceSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _confessionChoiceSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            ConfessionChoiceSnapshotPayload snapshot;
            if (!_confessionChoiceSyncAdapter.TryGetConfessionChoiceSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextConfessionChoiceSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextConfessionChoiceSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastConfessionChoiceSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastConfessionChoiceSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextConfessionChoiceSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendConfessionChoiceSnapshot(snapshot);
        }

        private void PollLairDecisionSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextLairDecisionSnapshotPollTime))
            {
                return;
            }

            _nextLairDecisionSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _lairDecisionSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            LairDecisionSnapshotPayload snapshot;
            if (!_lairDecisionSyncAdapter.TryGetLairDecisionSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextLairDecisionSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextLairDecisionSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastLairDecisionSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLairDecisionSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextLairDecisionSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendLairDecisionSnapshot(snapshot);
        }

        private void PollConfirmationDialogSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextConfirmationDialogSnapshotPollTime))
            {
                return;
            }

            _nextConfirmationDialogSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _confirmationDialogSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            ConfirmationDialogSnapshotPayload snapshot;
            if (!_confirmationDialogSyncAdapter.TryGetConfirmationDialogSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextConfirmationDialogSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextConfirmationDialogSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastConfirmationDialogSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastConfirmationDialogSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextConfirmationDialogSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendConfirmationDialogSnapshot(snapshot);
        }

        private void PollStoreSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextStoreSnapshotPollTime))
            {
                return;
            }

            _nextStoreSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _storeSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            StoreSnapshotPayload snapshot;
            if (!_storeSyncAdapter.TryGetStoreSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextStoreSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextStoreSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastStoreSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastStoreSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextStoreSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendStoreSnapshot(snapshot);
        }

        private void PollStagecoachSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextStagecoachSnapshotPollTime))
            {
                return;
            }

            _nextStagecoachSnapshotPollTime = Time.unscaledTime + InactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _stagecoachSyncAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            StagecoachSnapshotPayload snapshot;
            if (!_stagecoachSyncAdapter.TryGetStagecoachSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            _nextStagecoachSnapshotPollTime = Time.unscaledTime + GetSnapshotPollInterval(snapshot.IsActive, HeavyActiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextStagecoachSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastStagecoachSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastStagecoachSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextStagecoachSnapshotForcedSendTime = Time.unscaledTime + GetSnapshotForcedSendInterval(snapshot.IsActive);
            }

            _session.SendStagecoachSnapshot(snapshot);
        }

        private void PollDamageMeterSnapshots()
        {
            if (!TryBeginSnapshotPoll(_nextDamageMeterSnapshotPollTime))
            {
                return;
            }

            _nextDamageMeterSnapshotPollTime = Time.unscaledTime + DamageMeterInactiveSnapshotPollInterval;

            if (_lobbyClient == null ||
                _session == null ||
                _damageMeterSnapshotAdapter == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            CombatSnapshotPayload combatSnapshot;
            _session.TryGetLatestCombatSnapshot(out combatSnapshot);

            DamageMeterSnapshotPayload snapshot;
            if (!_damageMeterSnapshotAdapter.TryGetDamageMeterSnapshot(combatSnapshot, out snapshot) || snapshot == null)
            {
                return;
            }

            _nextDamageMeterSnapshotPollTime = Time.unscaledTime +
                (snapshot.IsActive ? DamageMeterActiveSnapshotPollInterval : DamageMeterInactiveSnapshotPollInterval);
            bool forceSend = Time.unscaledTime >= _nextDamageMeterSnapshotForcedSendTime;
            if (!forceSend && string.Equals(_lastDamageMeterSnapshotDigest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastDamageMeterSnapshotDigest = snapshot.Digest;
            if (forceSend)
            {
                _nextDamageMeterSnapshotForcedSendTime = Time.unscaledTime +
                    (snapshot.IsActive ? DamageMeterActiveSnapshotForcedSendInterval : DamageMeterInactiveSnapshotForcedSendInterval);
            }

            _session.SendDamageMeterSnapshot(snapshot);
        }

        private void ApplyRemoteDamageMeterSnapshotToLocalPlugin()
        {
            if (_lobbyClient == null ||
                _session == null ||
                !_lobbyClient.IsInLobby ||
                _lobbyClient.IsHost)
            {
                return;
            }

            DamageMeterSnapshotPayload snapshot;
            if (!_session.TryGetLatestDamageMeterSnapshot(out snapshot) || snapshot == null)
            {
                return;
            }

            string digest = snapshot.Digest ?? string.Empty;
            if (string.Equals(_lastAppliedLocalDamageMeterDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            MethodInfo applyMethod = GetDamageMeterRemoteApplyMethod();
            if (applyMethod == null)
            {
                return;
            }

            try
            {
                object result = applyMethod.Invoke(null, new object[] { snapshot });
                if (result is bool accepted && accepted)
                {
                    _lastAppliedLocalDamageMeterDigest = digest;
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("DamageMeter remote snapshot apply failed: " + ex.Message);
                _lastAppliedLocalDamageMeterDigest = digest;
            }
        }

        private MethodInfo GetDamageMeterRemoteApplyMethod()
        {
            if (_damageMeterRemoteApplyChecked)
            {
                return _damageMeterRemoteApplyMethod;
            }

            _damageMeterRemoteApplyChecked = true;
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly == null)
                    {
                        continue;
                    }

                    AssemblyName name = assembly.GetName();
                    if (name == null || !string.Equals(name.Name, "DD2DamageMeter", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _damageMeterRemoteApiType = assembly.GetType("DD2DamageMeter.DamageMeterMultiplayerApi", false);
                    if (_damageMeterRemoteApiType != null)
                    {
                        _damageMeterRemoteApplyMethod = _damageMeterRemoteApiType.GetMethod(
                            "TryApplyRemoteSnapshot",
                            BindingFlags.Public | BindingFlags.Static,
                            null,
                            new[] { typeof(object) },
                            null);
                    }

                    break;
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("DamageMeter remote snapshot apply discovery failed: " + ex.Message);
                _damageMeterRemoteApplyMethod = null;
            }

            return _damageMeterRemoteApplyMethod;
        }

        private void OnBattleResultReady(BattleResultPayload payload)
        {
            if (payload != null)
            {
                ArmArenaPostBattleMainMenuReturn(payload);
            }

            if (payload == null ||
                _lobbyClient == null ||
                _session == null ||
                !_lobbyClient.IsInLobby ||
                !_lobbyClient.IsHost)
            {
                return;
            }

            _session.SendBattleResult(payload);
        }

        private void ExecuteCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            HostLog.Write("Command file request: " + command);
            string[] parts = command.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();
            string argument = GetArgument(command, parts[0]);

            switch (verb)
            {
                case "host":
                    int maxMembers = 4;
                    if (parts.Length >= 2)
                    {
                        int.TryParse(parts[1], out maxMembers);
                    }

                    _lobbyClient.CreateLobby(maxMembers);
                    break;
                case "invite":
                    _lobbyClient.OpenInviteDialog();
                    break;
                case "leave":
                    _lobbyClient.LeaveLobby(_messageTransport);
                    break;
                case "dump":
                    _lobbyClient.DumpLobby();
                    break;
                case "say":
                    if (string.IsNullOrEmpty(argument))
                    {
                        HostLog.Write("Say command requires message text.");
                        return;
                    }

                    _lobbyClient.BroadcastText(_messageTransport, argument);
                    break;
                case "chat":
                    if (string.IsNullOrEmpty(argument))
                    {
                        HostLog.Write("Chat command requires message text.");
                        return;
                    }

                    _session.SendChat(argument);
                    break;
                case "slot":
                    ExecuteSlotCommand(argument);
                    break;
                case "slotauto":
                    ExecuteSlotAutoCommand(argument);
                    break;
                case "turn":
                    ExecuteTurnCommand(argument);
                    break;
                case "turncurrent":
                    ExecuteTurnCurrentCommand(argument);
                    break;
                case "autoturn":
                    ExecuteAutoTurnCommand(argument);
                    break;
                case "pvp":
                    ExecutePvpCommand(argument);
                    break;
                case "skill":
                    ExecuteSkillCommand(argument);
                    break;
                case "target":
                    ExecuteTargetCommand(argument);
                    break;
                case "pass":
                    ExecutePassCommand(argument);
                    break;
                case "heroassign":
                    ExecuteHeroAssignCommand(argument);
                    break;
                case "heroclear":
                    ExecuteHeroClearCommand(argument);
                    break;
                case "heropath":
                    ExecuteHeroPathCommand(argument);
                    break;
                case "heroready":
                    _session.RequestHeroSelectReady();
                    break;
                case "heroconfirm":
                    _session.RequestHeroSelectConfirm();
                    break;
                case "storychoice":
                    ExecuteStoryChoiceCommand(argument);
                    break;
                case "innbiome":
                    ExecuteInnBiomeCommand(argument);
                    break;
                case "innembark":
                    _session.RequestInnEmbark();
                    break;
                case "embarkapply":
                    _session.RequestEmbarkApplyRelationships();
                    break;
                case "embarkcontinue":
                    _session.RequestEmbarkContinue();
                    break;
                case "altarcontinue":
                case "altarembark":
                    _session.RequestAltarEmbark();
                    break;
                case "altarspend":
                    ExecuteAltarSpendCommand(argument);
                    break;
                case "altarreward":
                    ExecuteAltarRewardCommand(argument);
                    break;
                case "confessionchoice":
                case "confession":
                    ExecuteConfessionChoiceCommand(argument);
                    break;
                case "resultscontinue":
                case "gameresultscontinue":
                    _session.RequestGameResultsContinue();
                    break;
                case "laircontinue":
                    _session.RequestLairDecision("continue");
                    break;
                case "lairretreat":
                    _session.RequestLairDecision("retreat");
                    break;
                case "dialogconfirm":
                    _session.RequestConfirmationDialog("confirm");
                    break;
                case "dialogdecline":
                    _session.RequestConfirmationDialog("decline");
                    break;
                case "resync":
                case "fullstate":
                    _session.RequestFullState(string.IsNullOrWhiteSpace(argument) ? "command" : argument);
                    break;
                case "digest":
                    ExecuteDigestCommand(argument);
                    break;
                case "state":
                    _session.LogProtocolState();
                    break;
                case "nativeprobe":
                case "sceneprobe":
                    LogNativeSceneProbe(string.IsNullOrWhiteSpace(argument) ? "command" : argument);
                    break;
                case "combat":
                    if (_combatAdapter != null)
                    {
                        _combatAdapter.LogCombatState();
                    }

                    break;
                case "join":
                    if (string.IsNullOrWhiteSpace(argument) || !TryParseLobbyId(argument, out ulong lobbyId))
                    {
                        HostLog.Write("Join command requires a numeric lobby id.");
                        return;
                    }

                    _lobbyClient.JoinLobby(lobbyId);
                    break;
                default:
                    HostLog.Write("Unknown command file request: " + command);
                    break;
            }
        }

        private void ExecuteSlotCommand(string argument)
        {
            string[] parts = argument.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int slot))
            {
                HostLog.Write("Usage: slot <1-4> <memberName|steamId|me>.");
                return;
            }

            _session.AssignHeroSlot(slot, parts[1]);
        }

        private void ExecuteSlotAutoCommand(string argument)
        {
            string mode = string.IsNullOrWhiteSpace(argument) ? "fill" : argument.Trim().ToLowerInvariant();
            if (string.Equals(mode, "fill", StringComparison.Ordinal))
            {
                _session.AutoAssignHeroSlots(false);
                return;
            }

            if (string.Equals(mode, "replace", StringComparison.Ordinal) ||
                string.Equals(mode, "reset", StringComparison.Ordinal))
            {
                _session.AutoAssignHeroSlots(true);
                return;
            }

            HostLog.Write("Usage: slotauto [fill|replace].");
        }

        private void ExecuteTurnCommand(string argument)
        {
            string[] parts = argument.Split(new[] { ' ', '\t' }, 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 ||
                !int.TryParse(parts[0], out int round) ||
                !int.TryParse(parts[1], out int turn) ||
                !int.TryParse(parts[2], out int slot))
            {
                HostLog.Write("Usage: turn <round> <turn> <slot> <actorGuid> <actorName>.");
                return;
            }

            _session.SendTurnPrompt(round, turn, slot, parts[3], parts[4]);
        }

        private void ExecuteTurnCurrentCommand(string argument)
        {
            if (_combatAdapter == null)
            {
                HostLog.Write("Turncurrent ignored; combat adapter is not initialized.");
                return;
            }

            HeroSlotAssignmentPayload pvpEnemyOwner = null;
            bool pvpEnemyControl = _session != null && _session.TryGetPvpEnemyController(out pvpEnemyOwner);
            CombatTurnInfo info;
            if (!_combatAdapter.TryGetCurrentTurnInfo(pvpEnemyControl, out info))
            {
                HostLog.Write("Turncurrent ignored; no controllable turn is currently selectable.");
                return;
            }

            int slot = info.HeroSlot;
            if (!string.IsNullOrWhiteSpace(argument) &&
                !string.Equals(argument.Trim(), "auto", StringComparison.OrdinalIgnoreCase) &&
                !int.TryParse(argument.Trim(), out slot))
            {
                HostLog.Write("Usage: turncurrent [slot|auto].");
                return;
            }

            if (info.IsHeroTeam && (slot < 1 || slot > 4))
            {
                HostLog.Write("Turncurrent ignored; slot must be 1-4.");
                return;
            }

            if (!info.IsHeroTeam)
            {
                slot = info.HeroSlot;
            }

            HeroSlotAssignmentPayload owner;
            if (info.IsHeroTeam)
            {
                if (!_session.TryGetHeroSlotOwner(slot, out owner))
                {
                    HostLog.Write("Turncurrent warning: slot " + slot + " has no assigned owner.");
                }
            }
            else
            {
                owner = pvpEnemyOwner;
                if (owner == null)
                {
                    HostLog.Write("Turncurrent ignored; enemy turn needs PVP enemy pilot enabled.");
                    return;
                }
            }

            HostLog.Write("[turncurrent] prompting " + info.ControlRole +
                " slot " + slot +
                ", team=" + info.TeamIndex + ":" + info.TeamPosition +
                ", actor=" + info.ActorName + "/" + info.ActorGuid +
                ", round=" + info.Round +
                ", turn=" + info.Turn + ".");

            _session.SendTurnPrompt(
                info.Round,
                info.Turn,
                slot,
                info.ActorGuidText,
                info.ActorName,
                info.TeamIndex,
                info.TeamPosition,
                info.ControlRole,
                owner,
                _combatAdapter.GetCurrentTurnSkillOptions(pvpEnemyControl));
            RememberAutoTurn(info, slot);
        }

        private void ExecuteAutoTurnCommand(string argument)
        {
            string value = (argument ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(value) || value == "state")
            {
                HostLog.Write("[autoturn] enabled=" + _autoTurnPromptsEnabled +
                    ". Hero turns use TeamPosition + 1; PVP enemy turns use synthetic negative slots.");
                return;
            }

            if (value == "on" || value == "true" || value == "1")
            {
                _autoTurnPromptsEnabled = true;
                ResetAutoTurnMemory();
                HostLog.Write("[autoturn] enabled.");
                return;
            }

            if (value == "off" || value == "false" || value == "0")
            {
                _autoTurnPromptsEnabled = false;
                ResetAutoTurnMemory();
                HostLog.Write("[autoturn] disabled.");
                return;
            }

            HostLog.Write("Usage: autoturn [on|off|state].");
        }

        private void ExecutePvpCommand(string argument)
        {
            if (_session == null)
            {
                HostLog.Write("PVP command ignored; session is not initialized.");
                return;
            }

            string[] parts = (argument ?? string.Empty)
                .Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string action = parts.Length == 0 ? "state" : parts[0].Trim().ToLowerInvariant();
            string memberQuery = parts.Length >= 2 ? parts[1].Trim() : string.Empty;

            if (action == "on" || action == "enable" || action == "enemy")
            {
                _session.SetPvpEnemyPilot(true, memberQuery);
                return;
            }

            if (action == "off" || action == "disable")
            {
                _session.SetPvpEnemyPilot(false, string.Empty);
                return;
            }

            if (action == "state" || action == "status")
            {
                PvpModeStatePayload state;
                if (_session.TryGetPvpModeState(out state) && state != null && state.Enabled)
                {
                    HostLog.Write("[pvp] enabled mode=" + (state.Mode ?? "[none]") +
                        ", enemyController=" + (state.EnemyControllerName ?? "[none]") +
                        "/" + state.EnemyControllerSteamId +
                        ", runtimeEnemyInput=" + state.RuntimeEnemyInput +
                        ", suppressHeroSync=" + state.SuppressHeroSyncForEnemyController + ".");
                }
                else
                {
                    HostLog.Write("[pvp] disabled.");
                }

                return;
            }

            HostLog.Write("Usage: pvp [on <memberName|steamId|me>|off|state].");
        }

        private void ExecuteSkillCommand(string argument)
        {
            string[] parts = argument.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !int.TryParse(parts[0], out int slot))
            {
                HostLog.Write("Usage: skill <slot> <actorGuid> <skillId>.");
                return;
            }

            _session.ChooseSkill(slot, parts[1], parts[2]);
        }

        private void ExecuteTargetCommand(string argument)
        {
            string[] parts = argument.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !int.TryParse(parts[0], out int slot))
            {
                HostLog.Write("Usage: target <slot> <actorGuid> <targetGuid>.");
                return;
            }

            _session.ChooseTarget(slot, parts[1], parts[2]);
        }

        private void ExecutePassCommand(string argument)
        {
            string[] parts = argument.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int slot))
            {
                HostLog.Write("Usage: pass <slot> <actorGuid>.");
                return;
            }

            _session.PassTurn(slot, parts[1]);
        }

        private void ExecuteHeroAssignCommand(string argument)
        {
            string[] parts = argument.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int heroSlot))
            {
                HostLog.Write("Usage: heroassign <1-4> <actorGuid>.");
                return;
            }

            if (heroSlot < 1 || heroSlot > 4)
            {
                HostLog.Write("Heroassign ignored; slot must be 1-4.");
                return;
            }

            _session.RequestHeroSelectAssign(heroSlot - 1, parts[1]);
        }

        private void ExecuteHeroClearCommand(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument) || !int.TryParse(argument.Trim(), out int heroSlot))
            {
                HostLog.Write("Usage: heroclear <1-4>.");
                return;
            }

            if (heroSlot < 1 || heroSlot > 4)
            {
                HostLog.Write("Heroclear ignored; slot must be 1-4.");
                return;
            }

            _session.RequestHeroSelectClear(heroSlot - 1);
        }

        private void ExecuteHeroPathCommand(string argument)
        {
            string[] parts = argument.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !int.TryParse(parts[0], out int heroSlot))
            {
                HostLog.Write("Usage: heropath <1-4> <actorGuid> <pathId>.");
                return;
            }

            if (heroSlot < 1 || heroSlot > 4)
            {
                HostLog.Write("Heropath ignored; slot must be 1-4.");
                return;
            }

            _session.RequestHeroSelectPath(heroSlot - 1, parts[1], parts[2]);
        }

        private void ExecuteStoryChoiceCommand(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument) || !int.TryParse(argument.Trim(), out int optionIndex))
            {
                HostLog.Write("Usage: storychoice <optionIndex>.");
                return;
            }

            StoryChoiceSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestStoryChoiceSnapshot(out snapshot) || snapshot == null)
            {
                HostLog.Write("Storychoice ignored; no story choice snapshot is available.");
                return;
            }

            StoryChoiceOptionPayload option = (snapshot.Choices ?? Array.Empty<StoryChoiceOptionPayload>())
                .FirstOrDefault(choice => choice != null && choice.OptionIndex == optionIndex);
            if (option == null)
            {
                HostLog.Write("Storychoice ignored; option " + optionIndex + " is not active.");
                return;
            }

            _session.RequestStoryChoice(option);
        }

        private void ExecuteAltarSpendCommand(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                HostLog.Write("Usage: altarspend <trackIndex|trackId> [1|next|amount].");
                return;
            }

            AltarSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestAltarSnapshot(out snapshot) || snapshot == null)
            {
                HostLog.Write("Altar spend ignored; no altar snapshot is available.");
                return;
            }

            string[] parts = argument.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            IList<AltarTrackPayload> tracks = snapshot.Tracks ?? Array.Empty<AltarTrackPayload>();
            AltarTrackPayload track = null;
            int trackIndex;
            if (int.TryParse(parts[0], out trackIndex))
            {
                track = tracks.FirstOrDefault(candidate => candidate != null && candidate.TrackIndex == trackIndex);
            }
            else
            {
                track = tracks.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.TrackId ?? string.Empty, parts[0], StringComparison.OrdinalIgnoreCase));
            }

            if (track == null)
            {
                HostLog.Write("Altar spend ignored; track not found: " + parts[0] + ".");
                return;
            }

            float spendValue = 1f;
            if (parts.Length >= 2)
            {
                if (string.Equals(parts[1], "next", StringComparison.OrdinalIgnoreCase))
                {
                    spendValue = track.SpendToNext <= 0f ? 1f : track.SpendToNext;
                }
                else if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out spendValue))
                {
                    HostLog.Write("Usage: altarspend <trackIndex|trackId> [1|next|amount].");
                    return;
                }
            }

            _session.RequestAltarTrackSpend(track, spendValue);
        }

        private void ExecuteAltarRewardCommand(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                HostLog.Write("Usage: altarreward <buttonIndex|unlockTableId|unlockTrackId|itemType>.");
                return;
            }

            AltarSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestAltarSnapshot(out snapshot) || snapshot == null)
            {
                HostLog.Write("Altar reward ignored; no altar snapshot is available.");
                return;
            }

            IList<AltarRewardButtonPayload> rewards = snapshot.RewardButtons ?? Array.Empty<AltarRewardButtonPayload>();
            string key = argument.Trim();
            AltarRewardButtonPayload reward = null;
            int buttonIndex;
            if (int.TryParse(key, out buttonIndex))
            {
                reward = rewards.FirstOrDefault(candidate => candidate != null && candidate.ButtonIndex == buttonIndex);
            }
            else
            {
                reward = rewards
                    .Where(candidate => candidate != null &&
                        (string.Equals(candidate.CurrentUnlockTableId ?? string.Empty, key, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(candidate.UnlockTrackId ?? string.Empty, key, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(candidate.ItemType ?? string.Empty, key, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(candidate => candidate.CanPurchase)
                    .ThenBy(candidate => candidate.ButtonIndex)
                    .FirstOrDefault();
            }

            if (reward == null)
            {
                HostLog.Write("Altar reward ignored; reward not found: " + key + ".");
                return;
            }

            _session.RequestAltarRewardPurchase(reward);
        }

        private void ExecuteConfessionChoiceCommand(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                HostLog.Write("Usage: confessionchoice <optionIndex|bossId>.");
                return;
            }

            ConfessionChoiceSnapshotPayload snapshot;
            if (_session == null || !_session.TryGetLatestConfessionChoiceSnapshot(out snapshot) || snapshot == null)
            {
                HostLog.Write("Confession choice ignored; no active confession snapshot.");
                return;
            }

            IList<ConfessionChoiceOptionPayload> choices = snapshot.Choices ?? Array.Empty<ConfessionChoiceOptionPayload>();
            string trimmed = argument.Trim();
            ConfessionChoiceOptionPayload option = null;
            int optionIndex;
            if (int.TryParse(trimmed, out optionIndex))
            {
                option = choices.FirstOrDefault(candidate => candidate != null && candidate.OptionIndex == optionIndex);
            }
            else
            {
                option = choices.FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.BossId ?? string.Empty, trimmed, StringComparison.OrdinalIgnoreCase));
            }

            if (option == null)
            {
                HostLog.Write("Confession choice ignored; option not found: " + trimmed + ".");
                return;
            }

            _session.RequestConfessionChoice(option);
        }

        private void ExecuteInnBiomeCommand(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument) || !int.TryParse(argument.Trim(), out int optionIndex))
            {
                HostLog.Write("Usage: innbiome <optionIndex>.");
                return;
            }

            _session.RequestInnSelectBiome(optionIndex);
        }

        private void ExecuteDigestCommand(string argument)
        {
            string[] parts = argument.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                HostLog.Write("Usage: digest <label> <digest>.");
                return;
            }

            _session.SendStateDigest(parts[0], parts[1]);
        }

        private static string GetArgument(string command, string verb)
        {
            return command.Length > verb.Length ? command.Substring(verb.Length).Trim() : string.Empty;
        }

        private static bool TryParseLobbyId(string value, out ulong lobbyId)
        {
            lobbyId = 0UL;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string[] parts = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[0], "+connect_lobby", StringComparison.OrdinalIgnoreCase))
            {
                return ulong.TryParse(parts[1], out lobbyId);
            }

            return ulong.TryParse(value.Trim(), out lobbyId);
        }

        private bool IsRememberedAutoTurn(CombatTurnInfo info, int slot)
        {
            return _lastAutoTurnActorGuid == info.ActorGuid &&
                _lastAutoTurnRound == info.Round &&
                _lastAutoTurnTurn == info.Turn &&
                _lastAutoTurnSlot == slot;
        }

        private void RememberAutoTurn(CombatTurnInfo info, int slot)
        {
            _lastAutoTurnActorGuid = info.ActorGuid;
            _lastAutoTurnRound = info.Round;
            _lastAutoTurnTurn = info.Turn;
            _lastAutoTurnSlot = slot;
            _lastAutoTurnSkipKey = null;
        }

        private void ResetAutoTurnMemory()
        {
            _lastAutoTurnActorGuid = 0U;
            _lastAutoTurnRound = -1;
            _lastAutoTurnTurn = -1;
            _lastAutoTurnSlot = -1;
            _lastAutoTurnSkipKey = null;
        }

        private static CombatSnapshotPayload CreateInactiveCombatSnapshot()
        {
            return new CombatSnapshotPayload
            {
                Round = 0,
                Turn = 0,
                BattleState = "[none]",
                NextState = "[none]",
                PartyInBattle = false,
                HasCurrentActor = false,
                Actors = new List<ActorSnapshotPayload>(),
                Digest = "inactive",
            };
        }

        private static string FormatSnapshotStatuses(IList<StatusSnapshotPayload> statuses)
        {
            if (statuses == null || statuses.Count == 0)
            {
                return "-";
            }

            return string.Join(", ", statuses
                .Where(status => status != null && status.Count > 0)
                .OrderBy(status => status.Id)
                .Select(status =>
                {
                    string name = string.IsNullOrWhiteSpace(status.DisplayName) ? status.Id : status.DisplayName;
                    return CleanInline(name) + "x" + status.Count;
                })
                .ToArray());
        }

        private void LogAutoTurnSkipOnce(CombatTurnInfo info, string reason)
        {
            string key = info.Round + ":" + info.Turn + ":" + info.ActorGuid + ":" + info.HeroSlot + ":" + reason;
            if (string.Equals(_lastAutoTurnSkipKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _lastAutoTurnSkipKey = key;
            HostLog.Write("[autoturn] skipped " + info.ControlRole +
                " actor=" + info.ActorName + "/" + info.ActorGuid +
                ", controlSlot=" + info.HeroSlot +
                ", team=" + info.TeamIndex + ":" + info.TeamPosition +
                ": " + reason + ".");
        }
    }
}
