using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Affinity;
using Assets.Code.Combat;
using Assets.Code.Combat.BattleConfiguration;
using Assets.Code.Game;
using Assets.Code.Game.StageCoach;
using Assets.Code.Inn.Presentation;
using Assets.Code.Item;
using Assets.Code.Library;
using Assets.Code.Locale;
using Assets.Code.Map;
using Assets.Code.Map.Generation;
using Assets.Code.Map.Generation.Biome;
using Assets.Code.Map.Generation.Route;
using Assets.Code.Map.Generation.Row;
using Assets.Code.Map.Minimap;
using Assets.Code.Profile;
using Assets.Code.Quirk;
using Assets.Code.Run;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2ExpeditionOverviewSyncAdapter
    {
        private static readonly string[] BaubleItemIds =
        {
            "cave_dirt",
            "forest_medals",
            "farm_spoons",
            "city_books",
            "coast_starfish",
            "valley_baubles",
            "tundra_baubles",
        };

        public bool TryGetExpeditionOverviewSnapshot(out ExpeditionOverviewSnapshotPayload snapshot)
        {
            snapshot = CreateInactiveSnapshot();

            try
            {
                CollectGameState(snapshot);
                CollectMapContext(snapshot);
                CollectCombatScenario(snapshot);
                CollectResources(snapshot);
                CollectInventory(snapshot);
                CollectStagecoach(snapshot);
                CollectHeroes(snapshot);
                CollectRelationships(snapshot);
                CollectBiomeObjectives(snapshot);
                snapshot.IsActive = snapshot.IsGameTypeStarted || snapshot.IsRunStarted || snapshot.Heroes.Count > 0;
                snapshot.Digest = ComputeExpeditionOverviewDigest(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[overview] Failed to collect expedition overview snapshot: " + ex.Message + ".");
                snapshot = null;
                return false;
            }
        }

        private static void CollectGameState(ExpeditionOverviewSnapshotPayload snapshot)
        {
            try
            {
                snapshot.CurrentGameMode = SafeGetName(GameModeMgr.CurrentMode);

                if (Singleton<GameTypeMgr>.HasInstance())
                {
                    GameTypeMgr gameTypeMgr = Singleton<GameTypeMgr>.Instance;
                    snapshot.CurrentGameType = SafeGetName(gameTypeMgr.CurrentGameType);
                    snapshot.IsGameTypeStarted = gameTypeMgr.IsGameTypeStarted;
                }

                if (SingletonMonoBehaviour<RunBhv>.HasInstance(false))
                {
                    RunBhv runBhv = SingletonMonoBehaviour<RunBhv>.Instance;
                    snapshot.IsRunStarted = runBhv.IsRunStarted;
                    snapshot.RunStartType = SafeGetName(runBhv.RunStartType);
                }

                if (SingletonMonoBehaviour<MapMgrBhv>.HasInstance(false))
                {
                    MapMgrBhv mapMgr = SingletonMonoBehaviour<MapMgrBhv>.Instance;
                    snapshot.MapState = Convert.ToString(mapMgr.CurrentState);
                    Map map = mapMgr.GetMap();
                    if (map != null)
                    {
                        BiomeType biomeType = map.GetCurrentBiomeType();
                        snapshot.BiomeType = SafeGetName(biomeType);
                        snapshot.BiomeSubType = biomeType == null ? null : SafeGetName(biomeType.m_SubType);
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.WriteThrottled(
                    "overview-game-state-snapshot-failed",
                    "[overview] Game state snapshot failed: " + ex.Message + ".",
                TimeSpan.FromSeconds(15));
            }
        }

        private static void CollectMapContext(ExpeditionOverviewSnapshotPayload snapshot)
        {
            try
            {
                if (!SingletonMonoBehaviour<MapMgrBhv>.HasInstance(false))
                {
                    return;
                }

                MapMgrBhv mapMgr = SingletonMonoBehaviour<MapMgrBhv>.Instance;
                Map map = mapMgr.GetMap();
                if (map == null)
                {
                    return;
                }

                snapshot.MapProgress = BuildMapProgress(mapMgr);
                snapshot.MapRoute = BuildMapRoute(map, mapMgr, snapshot.MapProgress);
                snapshot.LastVisitedNode = BuildMapNode("last_visited", map.GetLastVisitedNode());
                snapshot.LastCompletedNode = BuildMapNode("last_completed", map.GetLastCompletedNode());
            }
            catch (Exception ex)
            {
                HostLog.WriteThrottled(
                    "overview-map-context-snapshot-failed",
                    "[overview] Map context snapshot failed: " + ex.Message + ".",
                    TimeSpan.FromSeconds(15));
            }
        }

        private static ExpeditionMapProgressPayload BuildMapProgress(MapMgrBhv mapMgr)
        {
            if (mapMgr == null)
            {
                return null;
            }

            try
            {
                ProgressInfo progress = mapMgr.GetProgress();
                ExpeditionMapProgressPayload payload = new ExpeditionMapProgressPayload
                {
                    IsValid = progress.IsValid,
                    BiomeIndex = -1,
                    RowIndex = -1,
                    NodeIndex = -1,
                    RowCount = -1,
                };

                if (!progress.IsValid)
                {
                    return payload;
                }

                payload.IsAtNode = progress.IsAtNode();
                payload.BiomeIndex = progress.GetBiomeIndex();
                payload.RowIndex = progress.GetRowIndex();
                payload.NodeIndex = progress.GetIndex();
                payload.RowCount = progress.GetRowCount();
                payload.BiomeTravelRatio = progress.GetBiomeTravelRatio();
                payload.BetweenRowsRatio = progress.GetMinimapRatioBetweenRows();
                payload.BetweenBiomesRatio = progress.GetMinimapRatioBetweenBiomes();
                return payload;
            }
            catch
            {
                return null;
            }
        }

        private static ExpeditionMapRoutePayload BuildMapRoute(
            Map map,
            MapMgrBhv mapMgr,
            ExpeditionMapProgressPayload progress)
        {
            if (map == null)
            {
                return null;
            }

            try
            {
                BiomeScaffold scaffold = map.GetCurrentBiomeScaffold();
                IReadOnlyList<BiomeRowScaffold> rows = scaffold == null ? null : scaffold.GetRowScaffolds();
                if (rows == null || rows.Count == 0)
                {
                    return null;
                }

                int currentRowIndex = progress != null && progress.IsValid
                    ? progress.RowIndex
                    : SafeInt(map.GetCurrentBiomeRowIndex);
                int currentNodeIndex = progress != null && progress.IsValid && progress.IsAtNode
                    ? progress.NodeIndex
                    : -1;
                int lastVisitedRowIndex = SafeInt(map.GetLastVisitedBiomeRowIndex);
                int lastVisitedNodeIndex = SafeInt(map.GetLastVisitedNodeIndex);
                int lastCompletedRowIndex = -1;
                int lastCompletedNodeIndex = -1;
                MapObjectNode lastCompletedNode = SafeGetLastCompletedNode(map);
                TileNodeBhv lastCompletedTile = SafeGetTileNode(lastCompletedNode);
                if (lastCompletedTile != null)
                {
                    lastCompletedRowIndex = SafeInt(lastCompletedTile.GetRowIndex);
                    lastCompletedNodeIndex = SafeInt(lastCompletedTile.GetIndexInRow);
                }

                ExpeditionMapRoutePayload route = new ExpeditionMapRoutePayload
                {
                    BiomeIndex = SafeInt(map.GetCurrentBiomeIndex),
                    CurrentRowIndex = currentRowIndex,
                    CurrentNodeIndex = currentNodeIndex,
                    LastVisitedRowIndex = lastVisitedRowIndex,
                    LastVisitedNodeIndex = lastVisitedNodeIndex,
                    LastCompletedRowIndex = lastCompletedRowIndex,
                    LastCompletedNodeIndex = lastCompletedNodeIndex,
                    RowCount = rows.Count,
                };

                MinimapMgrBhv minimap = SafeGetMinimap(mapMgr);
                for (int i = 0; i < rows.Count; i++)
                {
                    BiomeRowScaffold row = rows[i];
                    ExpeditionMapRouteRowPayload rowPayload = BuildMapRouteRow(
                        route,
                        row,
                        minimap,
                        currentRowIndex,
                        currentNodeIndex,
                        lastVisitedRowIndex,
                        lastVisitedNodeIndex,
                        lastCompletedRowIndex,
                        lastCompletedNodeIndex);
                    if (rowPayload != null)
                    {
                        route.Rows.Add(rowPayload);
                    }
                }

                route.NodeCount = route.Rows.Sum(row => row.Nodes == null ? 0 : row.Nodes.Count);
                route.RevealedNodeCount = route.Rows.Sum(row => row.Nodes == null ? 0 : row.Nodes.Count(node => node != null && node.IsRevealed));
                route.LinkCount = route.Rows.Sum(row => row.Links == null ? 0 : row.Links.Count);
                route.RevealedLinkCount = route.Rows.Sum(row => row.Links == null ? 0 : row.Links.Count(link => link != null && link.IsRevealed));
                return route;
            }
            catch (Exception ex)
            {
                HostLog.WriteThrottled(
                    "overview-map-route-snapshot-failed",
                    "[overview] Map route snapshot failed: " + ex.Message + ".",
                    TimeSpan.FromSeconds(15));
                return null;
            }
        }

        private static ExpeditionMapRouteRowPayload BuildMapRouteRow(
            ExpeditionMapRoutePayload route,
            BiomeRowScaffold row,
            MinimapMgrBhv minimap,
            int currentRowIndex,
            int currentNodeIndex,
            int lastVisitedRowIndex,
            int lastVisitedNodeIndex,
            int lastCompletedRowIndex,
            int lastCompletedNodeIndex)
        {
            if (route == null || row == null)
            {
                return null;
            }

            int rowIndex = SafeInt(row.GetRowIndex);
            ExpeditionMapRouteRowPayload payload = new ExpeditionMapRouteRowPayload
            {
                RowIndex = rowIndex,
                IsCurrentRow = rowIndex == currentRowIndex,
                IsLastVisitedRow = rowIndex == lastVisitedRowIndex,
                IsLastCompletedRow = rowIndex == lastCompletedRowIndex,
            };

            IReadOnlyList<NodeInstance> nodes = SafeGetNodeInstances(row);
            for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                NodeInstance node = nodes[nodeIndex];
                MinimapIcon icon = SafeGetMinimapIcon(minimap, route.BiomeIndex, rowIndex, nodeIndex);
                bool revealed = icon != null && SafeBool(icon.IsRevealed);
                payload.Nodes.Add(new ExpeditionMapRouteNodePayload
                {
                    NodeIndex = nodeIndex,
                    NodeType = revealed ? SafeGetNodeType(node) : "Unknown",
                    NodeSubType = revealed ? SafeGetNodeSubType(node) : null,
                    IsGenerated = node != null && node.IsGenerated,
                    IsRevealed = revealed,
                    IsCurrentNode = rowIndex == currentRowIndex && nodeIndex == currentNodeIndex,
                    IsLastVisitedNode = rowIndex == lastVisitedRowIndex && nodeIndex == lastVisitedNodeIndex,
                    IsLastCompletedNode = rowIndex == lastCompletedRowIndex && nodeIndex == lastCompletedNodeIndex,
                    HasBiomeKillContract = revealed && node != null && node.GetHasBiomeKillContract(),
                    BiomeKillContractGuid = revealed && node != null ? node.BiomeKillContractGuid : 0U,
                });
            }

            IReadOnlyList<NodeLink> links = SafeGetNodeLinks(row);
            IList<MinimapLink> minimapLinks = SafeGetMinimapLinks(minimap, route.BiomeIndex, rowIndex);
            for (int linkIndex = 0; linkIndex < links.Count; linkIndex++)
            {
                NodeLink link = links[linkIndex];
                MinimapLink minimapLink = minimapLinks != null && linkIndex < minimapLinks.Count ? minimapLinks[linkIndex] : null;
                bool revealed = minimapLink != null && SafeBool(minimapLink.IsRevealed);
                bool chosen = minimapLink != null && SafeBool(minimapLink.IsChosen);
                RouteDefinition routeDefinition = minimapLink == null ? (link == null ? null : link.Route) : minimapLink.RouteDef;
                payload.Links.Add(new ExpeditionMapRouteLinkPayload
                {
                    FromNodeIndex = link == null ? -1 : link.m_startIndex,
                    ToNodeIndex = link == null ? -1 : link.m_endIndex,
                    RouteId = revealed && routeDefinition != null ? routeDefinition.m_Id : null,
                    RouteType = revealed && routeDefinition != null && routeDefinition.m_RouteType != null
                        ? routeDefinition.m_RouteType.GetName()
                        : "Unknown",
                    IsRevealed = revealed,
                    IsChosen = chosen,
                });
            }

            return payload;
        }

        private static MinimapMgrBhv SafeGetMinimap(MapMgrBhv mapMgr)
        {
            try
            {
                return mapMgr == null ? null : mapMgr.GetMinimapMgr();
            }
            catch
            {
                return null;
            }
        }

        private static MinimapIcon SafeGetMinimapIcon(MinimapMgrBhv minimap, int biomeIndex, int rowIndex, int nodeIndex)
        {
            try
            {
                return minimap == null ? null : minimap.GetMinimapIcon(biomeIndex, rowIndex, nodeIndex);
            }
            catch
            {
                return null;
            }
        }

        private static IList<MinimapLink> SafeGetMinimapLinks(MinimapMgrBhv minimap, int biomeIndex, int rowIndex)
        {
            try
            {
                MinimapRow row = minimap == null ? null : minimap.GetMinimapRow(biomeIndex, rowIndex);
                return row == null ? null : row.GetLinks();
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyList<NodeInstance> SafeGetNodeInstances(BiomeRowScaffold row)
        {
            try
            {
                return row == null ? Array.Empty<NodeInstance>() : row.GetNodeInstances();
            }
            catch
            {
                return Array.Empty<NodeInstance>();
            }
        }

        private static IReadOnlyList<NodeLink> SafeGetNodeLinks(BiomeRowScaffold row)
        {
            try
            {
                return row == null ? Array.Empty<NodeLink>() : row.GetNodeLinks();
            }
            catch
            {
                return Array.Empty<NodeLink>();
            }
        }

        private static string SafeGetNodeType(NodeInstance node)
        {
            try
            {
                return node == null || node.NodeType == null ? "[node]" : node.NodeType.GetName();
            }
            catch
            {
                return "[node]";
            }
        }

        private static string SafeGetNodeSubType(NodeInstance node)
        {
            try
            {
                return node == null || !node.IsGenerated ? null : node.NodeSubType;
            }
            catch
            {
                return null;
            }
        }

        private static MapObjectNode SafeGetLastCompletedNode(Map map)
        {
            try
            {
                return map == null ? null : map.GetLastCompletedNode();
            }
            catch
            {
                return null;
            }
        }

        private static ExpeditionMapNodePayload BuildMapNode(string role, MapObjectNode node)
        {
            if (node == null)
            {
                return null;
            }

            TileNodeBhv tile = SafeGetTileNode(node);
            return new ExpeditionMapNodePayload
            {
                Role = role,
                RowIndex = tile == null ? -1 : SafeInt(tile.GetRowIndex),
                NodeIndex = tile == null ? -1 : SafeInt(tile.GetIndexInRow),
                NodeType = SafeGetName(node.GetNodeType()),
                NodeSubType = tile == null ? null : SafeString(tile.GetNodeSubType),
                OutgoingPathCount = node.GetOutgoingPaths() == null ? 0 : node.GetOutgoingPaths().Count,
                IncomingPathCount = node.GetIncomingPaths() == null ? 0 : node.GetIncomingPaths().Count,
            };
        }

        private static TileNodeBhv SafeGetTileNode(MapObjectNode node)
        {
            try
            {
                return node == null ? null : node.GetTileNode();
            }
            catch
            {
                return null;
            }
        }

        private static void CollectCombatScenario(ExpeditionOverviewSnapshotPayload snapshot)
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance())
                {
                    return;
                }

                CombatScenarioData combatScenario = Singleton<GameTypeMgr>.Instance.CombatScenarioData;
                snapshot.CombatScenario = BuildCombatScenario(combatScenario);
            }
            catch (Exception ex)
            {
                HostLog.WriteThrottled(
                    "overview-combat-scenario-snapshot-failed",
                    "[overview] Combat scenario snapshot failed: " + ex.Message + ".",
                    TimeSpan.FromSeconds(15));
            }
        }

        private static ExpeditionCombatScenarioPayload BuildCombatScenario(CombatScenarioData combatScenario)
        {
            if (combatScenario == null)
            {
                return null;
            }

            BattleConfigurationDefinition currentBattle = SafeGetCurrentBattleConfiguration(combatScenario);
            BattleConfigurationDefinition additionalBattle = SafeGetAdditionalBattleConfiguration(combatScenario);
            CombatSource combatSource = combatScenario.m_LoadedFrom;
            int currentBattleIndex = SafeInt(() => combatScenario.CurrentBattleConfigurationIndex);

            return new ExpeditionCombatScenarioPayload
            {
                IsActive = true,
                IsLoadStarted = SafeBool(() => combatScenario.IsLoadStarted),
                IsLoading = SafeBool(() => combatScenario.IsLoading),
                IsLoaded = SafeBool(() => combatScenario.IsLoaded),
                IsUnloading = SafeBool(() => combatScenario.IsUnloading),
                IsUnloaded = SafeBool(() => combatScenario.IsUnloaded),
                IsLoadingCombatIntro = SafeBool(() => combatScenario.IsLoadingCombatIntro),
                CombatSource = SafeGetName(combatSource),
                NodeType = combatSource == null ? null : SafeGetName(combatSource.m_NodeType),
                NodeSubType = combatScenario.m_NodeSubType,
                BackgroundSceneName = SafeString(() => combatScenario.BackgroundSceneName),
                CurrentBattleConfigurationId = currentBattle == null ? null : currentBattle.m_Id,
                AdditionalBattleConfigurationId = additionalBattle == null ? null : additionalBattle.m_Id,
                CurrentBattleConfigurationIndex = currentBattleIndex,
                CurrentBattleNumber = currentBattleIndex + 1,
                TotalNumberOfBattles = SafeInt(() => combatScenario.TotalNumberOfBattles),
                RemainingNumberOfBattles = SafeInt(() => combatScenario.RemainingNumberOfBattles),
                HasNextBattle = currentBattle != null && SafeBool(() => currentBattle.HasNextBattle),
                HasAdditionalBattle = currentBattle != null && SafeBool(() => currentBattle.HasAdditionalBattle),
                IsNextBattleOptional = currentBattle != null && currentBattle.m_IsNextBattleOptional,
                IsExpeditionBoss = combatScenario.m_IsExpeditionBoss,
                BiomeKillContractGuid = SafeUInt(() => combatScenario.BiomeKillContractGuid),
                StoryActorGuid = combatScenario.m_StoryActorGuid == 0U ? null : combatScenario.m_StoryActorGuid.ToString(),
                StoryActorDataId = combatScenario.m_StoryActorDataId,
                StoryChoiceId = combatScenario.m_StoryChoice == null ? null : combatScenario.m_StoryChoice.m_Id,
                StoryRetryCount = combatScenario.m_StoryRetryCount,
                BattleConfigurationIds = BuildBattleConfigurationIds(combatScenario),
                EnemyActorIds = currentBattle == null || currentBattle.m_EnemyActors == null
                    ? new List<string>()
                    : currentBattle.m_EnemyActors.ToList(),
                Tags = currentBattle == null || currentBattle.m_Tags == null
                    ? new List<string>()
                    : currentBattle.m_Tags.ToList(),
            };
        }

        private static BattleConfigurationDefinition SafeGetCurrentBattleConfiguration(CombatScenarioData combatScenario)
        {
            try
            {
                return combatScenario == null ? null : combatScenario.CurrentBattleConfiguration;
            }
            catch
            {
                return null;
            }
        }

        private static BattleConfigurationDefinition SafeGetAdditionalBattleConfiguration(CombatScenarioData combatScenario)
        {
            try
            {
                return combatScenario == null ? null : combatScenario.AdditionalBattleConfiguration;
            }
            catch
            {
                return null;
            }
        }

        private static IList<string> BuildBattleConfigurationIds(CombatScenarioData combatScenario)
        {
            try
            {
                return combatScenario == null || combatScenario.BattleConfigurations == null
                    ? new List<string>()
                    : combatScenario.BattleConfigurations
                        .Where(configuration => configuration != null)
                        .Select(configuration => configuration.m_Id)
                        .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static void CollectResources(ExpeditionOverviewSnapshotPayload snapshot)
        {
            try
            {
                snapshot.Relics = GetRelics();
                snapshot.Baubles = BaubleItemIds.Sum(GetInventoryItemQty);
                snapshot.Candles = GetCandles();
                snapshot.MasteryPoints = GetMasteryPoints();
                snapshot.Torch = GetRunValue(RunValueType.TORCH);
                snapshot.TorchMax = GetRunValueMax(RunValueType.TORCH);
                snapshot.Loathing = GetRunValue(RunValueType.DOOM);
                snapshot.LoathingMax = GetRunValueMax(RunValueType.DOOM);
                snapshot.Armor = GetRunValue(RunValueType.STAGE_COACH_ARMOR);
                snapshot.ArmorMax = GetRunStatValue(RunStatType.STAGE_COACH_ARMOR_MAX_VALUE);
                snapshot.Wheels = GetRunValue(RunValueType.STAGE_COACH_WHEELS);
                snapshot.WheelsMax = GetRunStatValue(RunStatType.STAGE_COACH_WHEELS_MAX_VALUE);
                snapshot.Currencies = BuildCurrencies(snapshot);
            }
            catch (Exception ex)
            {
                HostLog.Write("[overview] Resource snapshot failed: " + ex.Message + ".");
            }
        }

        private static void CollectInventory(ExpeditionOverviewSnapshotPayload snapshot)
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.PlayerInventory == null)
                {
                    return;
                }

                ItemInventory inventory = Singleton<GameTypeMgr>.Instance.PlayerInventory;
                snapshot.InventoryFilledSlots = inventory.GetNumberOfFilledSlots();
                snapshot.InventoryTotalSlots = inventory.GetNumberOfTotalSlots();
                snapshot.InventoryItems = BuildInventoryItems(inventory, "player");
            }
            catch (Exception ex)
            {
                HostLog.Write("[overview] Player inventory snapshot failed: " + ex.Message + ".");
            }
        }

        private static void CollectStagecoach(ExpeditionOverviewSnapshotPayload snapshot)
        {
            try
            {
                if (!TryGetStagecoach(out StageCoach stageCoach))
                {
                    return;
                }

                List<ExpeditionItemPayload> items = new List<ExpeditionItemPayload>();
                AddStagecoachSlotItems(items, stageCoach, ItemSlotType.GENERAL);
                AddStagecoachSlotItems(items, stageCoach, ItemSlotType.TROPHY);
                AddStagecoachSlotItems(items, stageCoach, ItemSlotType.PET);
                AddStagecoachSlotItems(items, stageCoach, ItemSlotType.FLAME);
                snapshot.StagecoachItems = items;
            }
            catch (Exception ex)
            {
                HostLog.Write("[overview] Stagecoach item snapshot failed: " + ex.Message + ".");
            }
        }

        private static void CollectHeroes(ExpeditionOverviewSnapshotPayload snapshot)
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.RosterManager == null)
                {
                    return;
                }

                IReadOnlyList<ActorInstance> actors = Singleton<GameTypeMgr>.Instance.RosterManager.GetPartyActors();
                if (actors == null)
                {
                    return;
                }

                snapshot.Heroes = actors
                    .Where(actor => actor != null)
                    .Select(BuildHero)
                    .OrderBy(hero => hero.TeamPosition)
                    .ThenBy(hero => hero.ActorGuid)
                    .ToList();
            }
            catch (Exception ex)
            {
                HostLog.Write("[overview] Party snapshot failed: " + ex.Message + ".");
            }
        }

        private static ExpeditionHeroPayload BuildHero(ActorInstance actor)
        {
            int teamPosition = SafeGetTeamPosition(actor);
            ExpeditionHeroPayload hero = new ExpeditionHeroPayload
            {
                HeroSlot = teamPosition >= 0 ? teamPosition + 1 : 0,
                TeamPosition = teamPosition,
                ActorGuid = actor.ActorGuid.ToString(),
                ActorDataId = SafeGetActorDataId(actor),
                ActorName = SafeGetActorName(actor),
                PathId = SafeGetActorPathId(actor),
                Hp = SafeRound(() => actor.DisplayedHp),
                HpMax = SafeRound(() => actor.DisplayedHpMax),
                Stress = SafeRound(() => actor.Stress),
                StressMax = SafeRound(() => actor.StressMax),
                WoundPercent = SafeFloat(() => actor.WoundPercent),
                Quirks = BuildQuirks(actor, false),
                Diseases = BuildQuirks(actor, true),
                Memories = BuildInventoryItems(actor.GetMemoryInventory(), "memory"),
                Trinkets = BuildInventoryItems(actor.GetTrinketInventory(), "trinket"),
                CombatItems = BuildInventoryItems(actor.GetCombatSkillInventory(), "combat"),
            };
            PopulateHeroRunGoal(hero, actor);
            return hero;
        }

        private static void CollectRelationships(ExpeditionOverviewSnapshotPayload snapshot)
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.AffinityManager == null)
                {
                    return;
                }

                AffinityManager affinityManager = Singleton<GameTypeMgr>.Instance.AffinityManager;
                int connectionCount = affinityManager.GetNumberOfConnections();
                List<ExpeditionRelationshipPayload> relationships = new List<ExpeditionRelationshipPayload>();

                for (int i = 0; i < connectionCount; i++)
                {
                    AffinityConnection connection = affinityManager.GetConnectionAtIndex(i);
                    ExpeditionRelationshipPayload relationship = BuildRelationship(connection);
                    if (relationship != null)
                    {
                        relationships.Add(relationship);
                    }
                }

                snapshot.Relationships = relationships
                    .OrderBy(relationship => relationship.TeamPositionA)
                    .ThenBy(relationship => relationship.TeamPositionB)
                    .ThenBy(relationship => relationship.ActorGuidA)
                    .ThenBy(relationship => relationship.ActorGuidB)
                    .ToList();
            }
            catch (Exception ex)
            {
                HostLog.WriteThrottled(
                    "overview-relationship-snapshot-failed",
                    "[overview] Relationship snapshot failed: " + ex.Message + ".",
                    TimeSpan.FromSeconds(15));
            }
        }

        private static ExpeditionRelationshipPayload BuildRelationship(AffinityConnection connection)
        {
            if (connection == null ||
                !SafeBool(() => connection.IsActive) ||
                connection.ActorGuids == null ||
                connection.ActorGuids.Count < 2)
            {
                return null;
            }

            uint actorGuidA = connection.ActorGuids[0];
            uint actorGuidB = connection.ActorGuids[1];
            ActorInstance actorA = SafeGetActorInstance(actorGuidA);
            ActorInstance actorB = SafeGetActorInstance(actorGuidB);
            int teamPositionA = SafeGetTeamPosition(actorA);
            int teamPositionB = SafeGetTeamPosition(actorB);
            AffinityRelationshipDefinition relationship = SafeGetRelationship(connection);
            AffinityLeaningLevelDefinition leaningLevel = SafeGetLeaningLevel(connection);
            bool hasDuration = SafeBool(connection.GetHasRelationshipDuration);

            return new ExpeditionRelationshipPayload
            {
                ActorGuidA = actorGuidA.ToString(),
                ActorGuidB = actorGuidB.ToString(),
                ActorNameA = SafeGetActorName(actorA),
                ActorNameB = SafeGetActorName(actorB),
                ActorDataIdA = SafeGetActorDataId(actorA),
                ActorDataIdB = SafeGetActorDataId(actorB),
                HeroSlotA = teamPositionA >= 0 ? teamPositionA + 1 : 0,
                HeroSlotB = teamPositionB >= 0 ? teamPositionB + 1 : 0,
                TeamPositionA = teamPositionA,
                TeamPositionB = teamPositionB,
                Leaning = SafeInt(connection.GetLeaning),
                LeaningMin = SafeInt(connection.GetLeaningMin),
                LeaningMax = SafeInt(connection.GetLeaningMax),
                LeaningPercent = SafeFloat(connection.GetLeaningPercent),
                LeaningLevelId = leaningLevel == null ? null : leaningLevel.m_Id,
                HasCurrentRelationship = SafeBool(connection.GetHasCurrentRelationship),
                HasPendingRelationship = SafeBool(connection.GetHasPendingRelationship),
                HasRelationshipDuration = hasDuration,
                RelationshipDurationRemaining = hasDuration ? SafeInt(connection.GetDisplayedRelationshipDurationRemaining) : 0,
                RelationshipId = relationship == null ? null : relationship.m_Id,
                RelationshipName = GetRelationshipDisplayName(relationship),
                RelationshipKind = GetRelationshipKind(relationship),
            };
        }

        private static ActorInstance SafeGetActorInstance(uint actorGuid)
        {
            try
            {
                return actorGuid == 0U
                    ? null
                    : SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance.GetLibraryElement(actorGuid);
            }
            catch
            {
                return null;
            }
        }

        private static AffinityRelationshipDefinition SafeGetRelationship(AffinityConnection connection)
        {
            try
            {
                return connection == null ? null : connection.GetPendingOrCurrentRelationship();
            }
            catch
            {
                return null;
            }
        }

        private static AffinityLeaningLevelDefinition SafeGetLeaningLevel(AffinityConnection connection)
        {
            try
            {
                return connection == null ? null : connection.GetAffinityLeaningLevel();
            }
            catch
            {
                return null;
            }
        }

        private static string GetRelationshipDisplayName(AffinityRelationshipDefinition relationship)
        {
            if (relationship == null)
            {
                return "[none]";
            }

            try
            {
                string displayName = AffinityRelationshipDescription.GetName(relationship.m_Id);
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return StripRichText(displayName);
                }
            }
            catch
            {
            }

            return relationship.m_Id ?? "[relationship]";
        }

        private static string GetRelationshipKind(AffinityRelationshipDefinition relationship)
        {
            if (relationship == null || relationship.m_Tags == null)
            {
                return "none";
            }

            if (relationship.m_Tags.Any(tag => string.Equals(tag, "positive", StringComparison.OrdinalIgnoreCase)))
            {
                return "positive";
            }

            if (relationship.m_Tags.Any(tag => string.Equals(tag, "negative", StringComparison.OrdinalIgnoreCase)))
            {
                return "negative";
            }

            return "relationship";
        }

        private static void CollectBiomeObjectives(ExpeditionOverviewSnapshotPayload snapshot)
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.BiomeManager == null)
                {
                    return;
                }

                BiomeManager biomeManager = Singleton<GameTypeMgr>.Instance.BiomeManager;
                snapshot.BiomeGoal = BuildBiomeGoal(biomeManager);
                snapshot.BiomeModifier = BuildBiomeModifier(biomeManager.CurrentBiomeModifier);
            }
            catch (Exception ex)
            {
                HostLog.WriteThrottled(
                    "overview-biome-objective-snapshot-failed",
                    "[overview] Biome objective snapshot failed: " + ex.Message + ".",
                    TimeSpan.FromSeconds(15));
            }
        }

        private static ExpeditionBiomeGoalPayload BuildBiomeGoal(BiomeManager biomeManager)
        {
            if (biomeManager == null || biomeManager.CurrentBiomeGoal == null)
            {
                return null;
            }

            BiomeGoalDefinition goal = biomeManager.CurrentBiomeGoal;
            BiomeGoalState state = biomeManager.CurrentBiomeGoalState;
            int currentCount = SafeInt(biomeManager.GetBiomeGoalCount);
            return new ExpeditionBiomeGoalPayload
            {
                GoalId = goal.m_Id,
                Description = GetBiomeGoalDescription(goal, currentCount),
                GoalType = Convert.ToString(goal.m_Type),
                State = Convert.ToString(state),
                CurrentCount = currentCount,
                ShowCountProgress = goal.m_ShowCountProgressInDriving,
                HasCompleteThreshold = goal.HasCompleteThreshold,
                CompleteThresholdType = Convert.ToString(goal.m_CompleteThresholdType),
                CompleteThresholdAmount = goal.m_CompleteThresholdAmount,
                HasFailThreshold = goal.HasFailThreshold,
                FailThresholdType = Convert.ToString(goal.m_FailThresholdType),
                FailThresholdAmount = goal.m_FailThresholdAmount,
                IsComplete = state == BiomeGoalState.COMPLETED,
                IsFailed = state == BiomeGoalState.FAILED,
                RewardId = goal.m_LootId,
                TypeStrings = (goal.TypeStrings ?? Array.Empty<string>()).ToList(),
            };
        }

        private static ExpeditionBiomeModifierPayload BuildBiomeModifier(BiomeModifierDefinition modifier)
        {
            if (modifier == null)
            {
                return null;
            }

            return new ExpeditionBiomeModifierPayload
            {
                ModifierId = modifier.m_Id,
                DisplayName = GetBiomeModifierDisplayName(modifier),
                Description = GetBiomeModifierDescription(modifier),
                Tags = (modifier.Tags ?? Array.Empty<string>()).ToList(),
            };
        }

        private static string GetBiomeGoalDescription(BiomeGoalDefinition goal, int currentCount)
        {
            if (goal == null)
            {
                return "[none]";
            }

            try
            {
                string text = Singleton<Localization>.Instance.GetString("biome_goal_description_" + goal.m_Id, true);
                if (!string.IsNullOrWhiteSpace(text) && goal.m_ShowCountProgressInDriving)
                {
                    if (goal.m_CompleteThresholdAmount != -1)
                    {
                        text += ": " + currentCount + "/" + goal.m_CompleteThresholdAmount;
                    }
                    else if (goal.m_FailThresholdAmount != -1)
                    {
                        text += ": " + currentCount + "/" + goal.m_FailThresholdAmount;
                    }
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return StripRichText(text);
                }
            }
            catch
            {
            }

            return goal.m_Id ?? "[biome-goal]";
        }

        private static string GetBiomeModifierDisplayName(BiomeModifierDefinition modifier)
        {
            if (modifier == null)
            {
                return "[none]";
            }

            try
            {
                string text = Singleton<Localization>.Instance.GetString("biome_mutator_" + modifier.m_Id, true);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return StripRichText(text);
                }
            }
            catch
            {
            }

            return modifier.m_Id ?? "[biome-modifier]";
        }

        private static string GetBiomeModifierDescription(BiomeModifierDefinition modifier)
        {
            if (modifier == null)
            {
                return null;
            }

            try
            {
                string text = BiomeDescription.GetBiomeModifierDescription(modifier);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return StripRichText(text);
                }
            }
            catch
            {
            }

            return null;
        }

        private static void PopulateHeroRunGoal(ExpeditionHeroPayload hero, ActorInstance actor)
        {
            if (hero == null || actor == null)
            {
                return;
            }

            RunGoalDefinition runGoal = SafeGetRunGoal(actor);
            if (runGoal == null)
            {
                return;
            }

            hero.RunGoalId = runGoal.m_Id;
            hero.RunGoalDescription = GetRunGoalDescription(runGoal);
            hero.RunGoalProgress = GetRunGoalProgress(runGoal, actor);
            hero.RunGoalCategoryId = runGoal.RunGoalCategoryDefinition == null
                ? null
                : runGoal.RunGoalCategoryDefinition.m_Id;
            hero.RunGoalComplete = SafeBool(() => actor.GetIsRunGoalComplete(runGoal));
            hero.RunGoalScore = runGoal.m_Score;
            hero.RunGoalLootTableId = runGoal.m_LootTableId;
        }

        private static RunGoalDefinition SafeGetRunGoal(ActorInstance actor)
        {
            try
            {
                return actor == null ? null : actor.RunGoal;
            }
            catch
            {
                return null;
            }
        }

        private static string GetRunGoalDescription(RunGoalDefinition runGoal)
        {
            string description = InvokeRunGoalDescription(
                "GetDescription",
                new[] { typeof(RunGoalDefinition), typeof(bool) },
                new object[] { runGoal, false });
            return string.IsNullOrWhiteSpace(description)
                ? runGoal == null ? null : runGoal.m_Id
                : description;
        }

        private static string GetRunGoalProgress(RunGoalDefinition runGoal, ActorInstance actor)
        {
            string flavour = InvokeRunGoalDescription(
                "GetProgressFlavourString",
                new[] { typeof(RunGoalDefinition) },
                new object[] { runGoal });
            string progress = InvokeRunGoalDescription(
                "GetProgressString",
                new[] { typeof(RunGoalDefinition), typeof(ActorInstance) },
                new object[] { runGoal, actor });

            if (string.IsNullOrWhiteSpace(flavour))
            {
                return progress;
            }

            if (string.IsNullOrWhiteSpace(progress))
            {
                return flavour;
            }

            return flavour + " " + progress;
        }

        private static string InvokeRunGoalDescription(string methodName, Type[] parameterTypes, object[] args)
        {
            try
            {
                Type type = typeof(RunGoalDefinition).Assembly.GetType("Assets.Code.Run.RunGoalDescription");
                MethodInfo method = type == null ? null : type.GetMethod(methodName, parameterTypes);
                string value = method == null ? null : method.Invoke(null, args) as string;
                return string.IsNullOrWhiteSpace(value) ? null : StripRichText(value);
            }
            catch
            {
                return null;
            }
        }

        private static IList<ExpeditionQuirkPayload> BuildQuirks(ActorInstance actor, bool diseases)
        {
            List<ExpeditionQuirkPayload> quirks = new List<ExpeditionQuirkPayload>();
            try
            {
                if (actor == null || !actor.HasEnabledQuirkContainer || actor.QuirkContainer == null)
                {
                    return quirks;
                }

                foreach (QuirkInstance quirk in actor.QuirkContainer.GetInstances())
                {
                    if (quirk == null || quirk.Definition == null || quirk.Definition.IsDisease != diseases)
                    {
                        continue;
                    }

                    QuirkDefinition definition = quirk.Definition;
                    quirks.Add(new ExpeditionQuirkPayload
                    {
                        QuirkId = definition.Id,
                        DisplayName = GetQuirkDisplayName(definition, actor),
                        Kind = GetQuirkKind(definition),
                        IsLocked = SafeBool(quirk.IsLocked),
                        IsNew = SafeBool(quirk.IsNew),
                        Duration = SafeInt(quirk.GetDurationAmount),
                        SourceType = SafeGetName(quirk.SourceType),
                        SourceId = quirk.SourceId,
                    });
                }
            }
            catch
            {
            }

            return quirks
                .OrderBy(quirk => quirk.Kind)
                .ThenBy(quirk => quirk.DisplayName)
                .ThenBy(quirk => quirk.QuirkId)
                .ToList();
        }

        private static IList<ExpeditionItemPayload> BuildInventoryItems(ItemInventory inventory, string scope)
        {
            List<ExpeditionItemPayload> items = new List<ExpeditionItemPayload>();
            if (inventory == null)
            {
                return items;
            }

            try
            {
                for (int i = 0; i < inventory.GetNumberOfTotalSlots(); i++)
                {
                    IReadOnlyItemInstance item = inventory.GetItemOrDefault(i);
                    if (!ItemUtils.IsValid(item))
                    {
                        continue;
                    }

                    items.Add(BuildItemPayload(scope, i, item));
                }
            }
            catch
            {
            }

            return items;
        }

        private static void AddStagecoachSlotItems(
            List<ExpeditionItemPayload> items,
            StageCoach stageCoach,
            ItemSlotType slotType)
        {
            ItemInventory inventory = stageCoach.GetSlotInventory(slotType);
            if (inventory == null)
            {
                return;
            }

            string scope = "coach:" + SafeGetName(slotType);
            foreach (ExpeditionItemPayload item in BuildInventoryItems(inventory, scope))
            {
                items.Add(item);
            }
        }

        private static ExpeditionItemPayload BuildItemPayload(
            string scope,
            int inventoryIndex,
            IReadOnlyItemInstance item)
        {
            ItemDefinition definition = item.GetItemDefinition();
            return new ExpeditionItemPayload
            {
                Scope = scope,
                InventoryIndex = inventoryIndex,
                ItemId = definition == null ? null : definition.m_id,
                DisplayName = GetItemDisplayName(definition),
                ItemType = definition == null || definition.m_type == null ? null : definition.m_type.GetName(),
                SlotType = definition == null || definition.m_slot == null ? null : definition.m_slot.GetName(),
                Quantity = item.GetQty(),
            };
        }

        private static IList<ExpeditionCurrencyPayload> BuildCurrencies(ExpeditionOverviewSnapshotPayload snapshot)
        {
            List<ExpeditionCurrencyPayload> currencies = new List<ExpeditionCurrencyPayload>
            {
                new ExpeditionCurrencyPayload { ItemId = "gold", DisplayName = "Relics", Quantity = snapshot.Relics },
                new ExpeditionCurrencyPayload { ItemId = "baubles_total", DisplayName = "Baubles", Quantity = snapshot.Baubles },
                new ExpeditionCurrencyPayload { ItemId = "candles", DisplayName = "Candles", Quantity = snapshot.Candles },
                new ExpeditionCurrencyPayload { ItemId = "hero_upgrade_points", DisplayName = "Mastery", Quantity = snapshot.MasteryPoints },
            };

            foreach (string itemId in BaubleItemIds)
            {
                int quantity = GetInventoryItemQty(itemId);
                if (quantity <= 0)
                {
                    continue;
                }

                currencies.Add(new ExpeditionCurrencyPayload
                {
                    ItemId = itemId,
                    DisplayName = GetItemDisplayName(GetItemDefinition(itemId)),
                    Quantity = quantity,
                });
            }

            return currencies;
        }

        private static bool TryGetStagecoach(out StageCoach stageCoach)
        {
            stageCoach = null;
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance())
                {
                    return false;
                }

                stageCoach = Singleton<GameTypeMgr>.Instance.StageCoach;
                return stageCoach != null;
            }
            catch
            {
                stageCoach = null;
                return false;
            }
        }

        private static int GetRelics()
        {
            try
            {
                return Singleton<GameTypeMgr>.HasInstance() && Singleton<GameTypeMgr>.Instance.PlayerInventory != null
                    ? Singleton<GameTypeMgr>.Instance.PlayerInventory.GetGoldQty()
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetInventoryItemQty(string itemId)
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.PlayerInventory == null)
                {
                    return 0;
                }

                ItemDefinition definition = GetItemDefinition(itemId);
                return definition == null
                    ? 0
                    : Singleton<GameTypeMgr>.Instance.PlayerInventory.GetItemQty(definition);
            }
            catch
            {
                return 0;
            }
        }

        private static int GetCandles()
        {
            try
            {
                return SingletonMonoBehaviour<ProfileBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile() != null
                    ? Mathf.RoundToInt(SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile().ProfileValues.GetValue(ProfileValueType.CANDLES))
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetMasteryPoints()
        {
            int points = GetRunValue(RunValueType.HERO_UPGRADE_POINTS);
            try
            {
                if (GameModeMgr.CurrentMode == GameModeType.INN &&
                    SingletonMonoBehaviour<InnPresentationBhv>.HasInstance(false))
                {
                    points -= SingletonMonoBehaviour<InnPresentationBhv>.Instance.UpgradeSkillsPointsSpent;
                }
            }
            catch
            {
            }

            return points;
        }

        private static int GetRunValue(RunValueType runValueType)
        {
            try
            {
                return Singleton<GameTypeMgr>.HasInstance() && Singleton<GameTypeMgr>.Instance.RunValues != null
                    ? Mathf.RoundToInt(Singleton<GameTypeMgr>.Instance.RunValues.GetValue(runValueType))
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetRunValueMax(RunValueType runValueType)
        {
            try
            {
                return Singleton<GameTypeMgr>.HasInstance() && Singleton<GameTypeMgr>.Instance.RunValues != null
                    ? Mathf.RoundToInt(Singleton<GameTypeMgr>.Instance.RunValues.GetMaxValue(runValueType))
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetRunStatValue(RunStatType runStatType)
        {
            try
            {
                return Singleton<GameTypeMgr>.HasInstance() && Singleton<GameTypeMgr>.Instance.RunDataManager != null
                    ? Mathf.RoundToInt(Singleton<GameTypeMgr>.Instance.RunDataManager.GetStatValue(runStatType, (string)null))
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static int SafeGetTeamPosition(ActorInstance actor)
        {
            try
            {
                return actor != null && actor.GetIsTeamPositionSet() ? actor.TeamPosition : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static string SafeGetActorDataId(ActorInstance actor)
        {
            try
            {
                return actor == null ? null : actor.ActorDataId;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetActorName(ActorInstance actor)
        {
            try
            {
                return actor == null ? null : actor.ActorName;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetActorPathId(ActorInstance actor)
        {
            try
            {
                return actor == null || actor.ActorDataPath == null ? null : actor.ActorDataPath.Id;
            }
            catch
            {
                return null;
            }
        }

        private static int SafeRound(Func<float> getter)
        {
            try
            {
                return Mathf.RoundToInt(getter());
            }
            catch
            {
                return 0;
            }
        }

        private static float SafeFloat(Func<float> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return 0f;
            }
        }

        private static int SafeInt(Func<int> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return 0;
            }
        }

        private static bool SafeBool(Func<bool> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return false;
            }
        }

        private static uint SafeUInt(Func<uint> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return 0U;
            }
        }

        private static string SafeString(Func<string> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetName<T>(CustomEnum<T> value) where T : CustomEnum<T>
        {
            try
            {
                return value == null ? null : value.GetName();
            }
            catch
            {
                return value == null ? null : Convert.ToString(value);
            }
        }

        private static ItemDefinition GetItemDefinition(string itemId)
        {
            try
            {
                return string.IsNullOrWhiteSpace(itemId)
                    ? null
                    : SingletonMonoBehaviour<Library<string, ItemDefinition>>.Instance.GetLibraryElement(itemId);
            }
            catch
            {
                return null;
            }
        }

        private static string GetItemDisplayName(ItemDefinition itemDefinition)
        {
            if (itemDefinition == null)
            {
                return "[item]";
            }

            try
            {
                string displayName = ItemDescription.GetTitle(itemDefinition, 0);
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return StripRichText(displayName);
                }
            }
            catch
            {
            }

            return itemDefinition.m_id ?? "[item]";
        }

        private static string GetQuirkDisplayName(QuirkDefinition definition, ActorInstance actor)
        {
            if (definition == null)
            {
                return "[quirk]";
            }

            try
            {
                string displayName = QuirkDescription.GetNameString(definition, actor, false);
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return StripRichText(displayName);
                }
            }
            catch
            {
            }

            return definition.Id ?? "[quirk]";
        }

        private static string GetQuirkKind(QuirkDefinition definition)
        {
            if (definition == null)
            {
                return "[none]";
            }

            if (definition.IsDisease)
            {
                return "disease";
            }

            if (definition.IsCurse)
            {
                return "curse";
            }

            if (definition.IsNegative)
            {
                return "negative";
            }

            return definition.IsPositive ? "positive" : "quirk";
        }

        private static string StripRichText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            bool inTag = false;
            List<char> chars = new List<char>(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '<')
                {
                    inTag = true;
                    continue;
                }

                if (c == '>')
                {
                    inTag = false;
                    continue;
                }

                if (!inTag)
                {
                    chars.Add(c);
                }
            }

            return new string(chars.ToArray());
        }

        private static ExpeditionOverviewSnapshotPayload CreateInactiveSnapshot()
        {
            ExpeditionOverviewSnapshotPayload snapshot = new ExpeditionOverviewSnapshotPayload
            {
                IsActive = false,
                CurrentGameMode = SafeGetName(GameModeMgr.CurrentMode),
                CurrentGameType = "[none]",
                MapState = "[none]",
            };
            snapshot.Digest = ComputeExpeditionOverviewDigest(snapshot);
            return snapshot;
        }

        private static string ComputeExpeditionOverviewDigest(ExpeditionOverviewSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.IsActive + ";" +
                (snapshot.CurrentGameMode ?? string.Empty) + ";" +
                (snapshot.CurrentGameType ?? string.Empty) + ";" +
                snapshot.IsGameTypeStarted + ";" +
                snapshot.IsRunStarted + ";" +
                (snapshot.RunStartType ?? string.Empty) + ";" +
                (snapshot.MapState ?? string.Empty) + ";" +
                (snapshot.BiomeType ?? string.Empty) + ";" +
                (snapshot.BiomeSubType ?? string.Empty) + ";" +
                snapshot.Relics + ";" +
                snapshot.Baubles + ";" +
                snapshot.Candles + ";" +
                snapshot.MasteryPoints + ";" +
                snapshot.Torch + "/" + snapshot.TorchMax + ";" +
                snapshot.Loathing + "/" + snapshot.LoathingMax + ";" +
                snapshot.Armor + "/" + snapshot.ArmorMax + ";" +
                snapshot.Wheels + "/" + snapshot.WheelsMax + ";" +
                snapshot.InventoryFilledSlots + "/" + snapshot.InventoryTotalSlots + ";" +
                DescribeCurrencies(snapshot.Currencies) + ";" +
                DescribeItems(snapshot.InventoryItems) + ";" +
                DescribeItems(snapshot.StagecoachItems) + ";" +
                DescribeHeroes(snapshot.Heroes) + ";" +
                DescribeRelationships(snapshot.Relationships) + ";" +
                DescribeBiomeGoal(snapshot.BiomeGoal) + ";" +
                DescribeBiomeModifier(snapshot.BiomeModifier) + ";" +
                DescribeCombatScenario(snapshot.CombatScenario) + ";" +
                DescribeMapProgress(snapshot.MapProgress) + ";" +
                DescribeMapRoute(snapshot.MapRoute) + ";" +
                DescribeMapNode(snapshot.LastVisitedNode) + ";" +
                DescribeMapNode(snapshot.LastCompletedNode);

            return ComputeStableDigest(raw);
        }

        private static string DescribeCurrencies(IList<ExpeditionCurrencyPayload> currencies)
        {
            return string.Join(
                "|",
                (currencies ?? Array.Empty<ExpeditionCurrencyPayload>())
                    .OrderBy(currency => currency.ItemId)
                    .Select(currency => (currency.ItemId ?? string.Empty) + ":" + currency.Quantity)
                    .ToArray());
        }

        private static string DescribeHeroes(IList<ExpeditionHeroPayload> heroes)
        {
            return string.Join(
                "|",
                (heroes ?? Array.Empty<ExpeditionHeroPayload>())
                    .OrderBy(hero => hero.TeamPosition)
                    .ThenBy(hero => hero.ActorGuid)
                    .Select(hero =>
                        hero.HeroSlot + ":" +
                        hero.TeamPosition + ":" +
                        (hero.ActorGuid ?? string.Empty) + ":" +
                        (hero.ActorDataId ?? string.Empty) + ":" +
                        (hero.ActorName ?? string.Empty) + ":" +
                        (hero.PathId ?? string.Empty) + ":" +
                        hero.Hp + "/" + hero.HpMax + ":" +
                        hero.Stress + "/" + hero.StressMax + ":" +
                        hero.WoundPercent.ToString("0.000", CultureInfo.InvariantCulture) + ":" +
                        (hero.RunGoalId ?? string.Empty) + ":" +
                        hero.RunGoalComplete + ":" +
                        (hero.RunGoalProgress ?? string.Empty) + ":" +
                        DescribeQuirks(hero.Quirks) + ":" +
                        DescribeQuirks(hero.Diseases) + ":" +
                        DescribeItems(hero.Memories) + ":" +
                        DescribeItems(hero.Trinkets) + ":" +
                        DescribeItems(hero.CombatItems))
                    .ToArray());
        }

        private static string DescribeBiomeGoal(ExpeditionBiomeGoalPayload goal)
        {
            if (goal == null)
            {
                return string.Empty;
            }

            return (goal.GoalId ?? string.Empty) + ":" +
                (goal.GoalType ?? string.Empty) + ":" +
                (goal.State ?? string.Empty) + ":" +
                goal.CurrentCount + ":" +
                goal.HasCompleteThreshold + ":" +
                (goal.CompleteThresholdType ?? string.Empty) + ":" +
                goal.CompleteThresholdAmount + ":" +
                goal.HasFailThreshold + ":" +
                (goal.FailThresholdType ?? string.Empty) + ":" +
                goal.FailThresholdAmount + ":" +
                goal.IsComplete + ":" +
                goal.IsFailed + ":" +
                (goal.RewardId ?? string.Empty) + ":" +
                string.Join(",", goal.TypeStrings ?? Array.Empty<string>());
        }

        private static string DescribeBiomeModifier(ExpeditionBiomeModifierPayload modifier)
        {
            if (modifier == null)
            {
                return string.Empty;
            }

            return (modifier.ModifierId ?? string.Empty) + ":" +
                string.Join(",", modifier.Tags ?? Array.Empty<string>());
        }

        private static string DescribeCombatScenario(ExpeditionCombatScenarioPayload combatScenario)
        {
            if (combatScenario == null)
            {
                return string.Empty;
            }

            return combatScenario.IsActive + ":" +
                combatScenario.IsLoadStarted + ":" +
                combatScenario.IsLoading + ":" +
                combatScenario.IsLoaded + ":" +
                combatScenario.IsUnloading + ":" +
                combatScenario.IsUnloaded + ":" +
                (combatScenario.CombatSource ?? string.Empty) + ":" +
                (combatScenario.NodeType ?? string.Empty) + ":" +
                (combatScenario.NodeSubType ?? string.Empty) + ":" +
                (combatScenario.BackgroundSceneName ?? string.Empty) + ":" +
                (combatScenario.CurrentBattleConfigurationId ?? string.Empty) + ":" +
                (combatScenario.AdditionalBattleConfigurationId ?? string.Empty) + ":" +
                combatScenario.CurrentBattleConfigurationIndex + ":" +
                combatScenario.CurrentBattleNumber + "/" + combatScenario.TotalNumberOfBattles + "/" + combatScenario.RemainingNumberOfBattles + ":" +
                combatScenario.HasNextBattle + ":" +
                combatScenario.HasAdditionalBattle + ":" +
                combatScenario.IsNextBattleOptional + ":" +
                combatScenario.IsExpeditionBoss + ":" +
                combatScenario.BiomeKillContractGuid + ":" +
                (combatScenario.StoryActorGuid ?? string.Empty) + ":" +
                (combatScenario.StoryActorDataId ?? string.Empty) + ":" +
                (combatScenario.StoryChoiceId ?? string.Empty) + ":" +
                combatScenario.StoryRetryCount + ":" +
                string.Join(",", combatScenario.BattleConfigurationIds ?? Array.Empty<string>()) + ":" +
                string.Join(",", combatScenario.EnemyActorIds ?? Array.Empty<string>()) + ":" +
                string.Join(",", combatScenario.Tags ?? Array.Empty<string>());
        }

        private static string DescribeMapNode(ExpeditionMapNodePayload node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            return (node.Role ?? string.Empty) + ":" +
                node.RowIndex + ":" +
                node.NodeIndex + ":" +
                (node.NodeType ?? string.Empty) + ":" +
                (node.NodeSubType ?? string.Empty) + ":" +
                node.IncomingPathCount + ":" +
                node.OutgoingPathCount;
        }

        private static string DescribeMapProgress(ExpeditionMapProgressPayload progress)
        {
            if (progress == null)
            {
                return string.Empty;
            }

            return progress.IsValid + ":" +
                progress.IsAtNode + ":" +
                progress.BiomeIndex + ":" +
                progress.RowIndex + ":" +
                progress.NodeIndex + ":" +
                progress.RowCount + ":" +
                FormatFloat(progress.BiomeTravelRatio) + ":" +
                FormatFloat(progress.BetweenRowsRatio) + ":" +
                FormatFloat(progress.BetweenBiomesRatio);
        }

        private static string DescribeMapRoute(ExpeditionMapRoutePayload route)
        {
            if (route == null)
            {
                return string.Empty;
            }

            return route.BiomeIndex + ":" +
                route.CurrentRowIndex + "/" + route.CurrentNodeIndex + ":" +
                route.LastVisitedRowIndex + "/" + route.LastVisitedNodeIndex + ":" +
                route.LastCompletedRowIndex + "/" + route.LastCompletedNodeIndex + ":" +
                route.RowCount + ":" +
                route.NodeCount + "/" + route.RevealedNodeCount + ":" +
                route.LinkCount + "/" + route.RevealedLinkCount + ":" +
                string.Join("|", (route.Rows ?? Array.Empty<ExpeditionMapRouteRowPayload>())
                    .OrderBy(row => row.RowIndex)
                    .Select(row =>
                        row.RowIndex + "[" +
                        string.Join(",", (row.Nodes ?? Array.Empty<ExpeditionMapRouteNodePayload>())
                            .OrderBy(node => node.NodeIndex)
                            .Select(node =>
                                node.NodeIndex + ":" +
                                (node.NodeType ?? string.Empty) + ":" +
                                (node.NodeSubType ?? string.Empty) + ":" +
                                node.IsRevealed + ":" +
                                node.IsCurrentNode + ":" +
                                node.IsLastVisitedNode + ":" +
                                node.IsLastCompletedNode + ":" +
                                node.HasBiomeKillContract)
                            .ToArray()) +
                        "](" +
                        string.Join(",", (row.Links ?? Array.Empty<ExpeditionMapRouteLinkPayload>())
                            .OrderBy(link => link.FromNodeIndex)
                            .ThenBy(link => link.ToNodeIndex)
                            .Select(link =>
                                link.FromNodeIndex + ">" +
                                link.ToNodeIndex + ":" +
                                (link.RouteId ?? string.Empty) + ":" +
                                (link.RouteType ?? string.Empty) + ":" +
                                link.IsRevealed + ":" +
                                link.IsChosen)
                            .ToArray()) +
                        ")")
                    .ToArray());
        }

        private static string DescribeQuirks(IList<ExpeditionQuirkPayload> quirks)
        {
            return string.Join(
                ",",
                (quirks ?? Array.Empty<ExpeditionQuirkPayload>())
                    .OrderBy(quirk => quirk.Kind)
                    .ThenBy(quirk => quirk.QuirkId)
                    .Select(quirk =>
                        (quirk.Kind ?? string.Empty) + ":" +
                        (quirk.QuirkId ?? string.Empty) + ":" +
                        quirk.IsLocked + ":" +
                        quirk.Duration)
                    .ToArray());
        }

        private static string DescribeRelationships(IList<ExpeditionRelationshipPayload> relationships)
        {
            return string.Join(
                "|",
                (relationships ?? Array.Empty<ExpeditionRelationshipPayload>())
                    .OrderBy(relationship => relationship.ActorGuidA)
                    .ThenBy(relationship => relationship.ActorGuidB)
                    .Select(relationship =>
                        (relationship.ActorGuidA ?? string.Empty) + ":" +
                        (relationship.ActorGuidB ?? string.Empty) + ":" +
                        relationship.Leaning + "/" + relationship.LeaningMin + "/" + relationship.LeaningMax + ":" +
                        (relationship.LeaningLevelId ?? string.Empty) + ":" +
                        relationship.HasCurrentRelationship + ":" +
                        relationship.HasPendingRelationship + ":" +
                        relationship.HasRelationshipDuration + ":" +
                        relationship.RelationshipDurationRemaining + ":" +
                        (relationship.RelationshipId ?? string.Empty) + ":" +
                        (relationship.RelationshipKind ?? string.Empty))
                    .ToArray());
        }

        private static string DescribeItems(IList<ExpeditionItemPayload> items)
        {
            return string.Join(
                "|",
                (items ?? Array.Empty<ExpeditionItemPayload>())
                    .OrderBy(item => item.Scope)
                    .ThenBy(item => item.InventoryIndex)
                    .ThenBy(item => item.ItemId)
                    .Select(item =>
                        (item.Scope ?? string.Empty) + ":" +
                        item.InventoryIndex + ":" +
                        (item.ItemId ?? string.Empty) + ":" +
                        item.Quantity)
                    .ToArray());
        }

        private static string ComputeStableDigest(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                string value = text ?? string.Empty;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 1099511628211UL;
                }

                return hash.ToString("x16");
            }
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }
    }
}
