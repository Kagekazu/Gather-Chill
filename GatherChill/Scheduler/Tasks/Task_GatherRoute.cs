using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using ECommons.Logging;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GatherChill.ConfigFiles;
using GatherChill.Enums;
using GatherChill.GatheringInfo;
using GatherChill.Utilities.GatheringHelpers;
using GatherChill.Utilities.Tools;
using GatherChill.Utilities.Utility;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace GatherChill.Scheduler.Tasks
{
    internal class Task_GatherRoute
    {
        private static GatheringRoute selectedRoute = null;
        private static readonly Random _random = new Random();

        private static uint loadedRouteId;
        private static bool pendingRouteChange;
        private static int RouteIndex = 0;
        private static List<GatheringNode> GatherRoute = new();
        private static Vector3? TargetFanPoint = null;
        private static uint? TargetNodeId = null;
        private static int NodeCheckIndex = 0;
        private static bool _openedGatheringWindowThisNode;

        public static void Reset()
        {
            selectedRoute = null;
            loadedRouteId = 0;
            pendingRouteChange = false;
            RouteIndex = 0;
            GatherRoute.Clear();
            TargetFanPoint = null;
            TargetNodeId = null;
            NodeCheckIndex = 0;
            _openedGatheringWindowThisNode = false;
            GatherRouteNavigation.ResetValidationApproach();
        }

        public static void OnRouteTargetChanged(bool routeChanged)
        {
            if (routeChanged)
                pendingRouteChange = true;
            else if (SchedulerMain.QueueActive)
                ResetRouteProgress();

            if (!GatherRouteNavigation.IsGatheringSessionActive())
                TryApplyPendingRouteChange();
        }

        private static void ResetRouteProgress()
        {
            RouteIndex = 0;
            NodeCheckIndex = 0;
            TargetFanPoint = null;
            TargetNodeId = null;
            _openedGatheringWindowThisNode = false;
            GatherRouteNavigation.ResetValidationApproach();
        }

        public static void Enqueue(uint routeId, uint itemId)
        {
            if (SchedulerMain.QueueActive)
                P.taskManager.Enqueue(EnsureQueueTerritory, "Ensure queue territory", TaskConfig);

            if (!GatherRouteNavigation.IsGatheringSessionActive())
                P.taskManager.Enqueue(EnsureGatheringJob, "Ensure gathering class", TaskConfig);

            if (GatherRouteNavigation.IsGatheringSessionActive())
                EnqueueGatheringSession();
            else
                P.taskManager.Enqueue(TravelFarCheck, "Traveling to node group", TaskConfig);
        }

        public static void EnqueueGatheringSession() =>
            P.taskManager.Enqueue(GatheringInteraction, "Gathering Interaction", TaskConfig);

        private static bool EnsureQueueTerritory()
        {
            if (!SchedulerMain.QueueActive || !SchedulerMain.RouteId.HasValue)
                return true;

            var route = P.routeEditor.GetRoute(SchedulerMain.RouteId.Value);
            if (route == null)
                return true;

            return GatherZoneTravel.TryEnsureTerritory(route.TerritoryId, route.ZoneName);
        }

        private static bool EnsureGatheringJob()
        {
            if (!C.AutoSwapGatheringClass || GatherRouteNavigation.IsGatheringSessionActive())
                return true;

            var route = P.routeEditor.GetRoute(SchedulerMain.RouteId.Value);
            if (route == null)
                return true;

            return Utils.TrySwapToGatheringJob(route.GatheringJobId);
        }

        private static void TryApplyPendingRouteChange()
        {
            if (!pendingRouteChange || !SchedulerMain.RouteId.HasValue)
                return;

            LoadRoute(SchedulerMain.RouteId.Value);
            pendingRouteChange = false;
        }

        private static void LoadRoute(uint routeId)
        {
            var route = P.routeEditor.GetRoute(routeId);
            if (route == null)
                return;

            selectedRoute = route;
            loadedRouteId = routeId;
            RouteIndex = 0;
            NodeCheckIndex = 0;
            TargetFanPoint = null;
            TargetNodeId = null;
            _openedGatheringWindowThisNode = false;
            GatherRoute.Clear();
            foreach (var group in route.NodeInfo)
                GatherRoute.Add(group);
        }



        private static bool TravelFarCheck()
        {
            if (GatherRouteNavigation.IsGatheringSessionActive())
            {
                GatherRouteNavigation.StopMovementForGathering();
                // End travel task so Tick can run Gathering Interaction instead of blocking here.
                return true;
            }

            TryApplyPendingRouteChange();

            var route = P.routeEditor.GetRoute(SchedulerMain.RouteId.Value);
            if (route != null && loadedRouteId != route.RouteId)
            {
                IceLogging.Verbose("Route target changed, reloading gather route");
                LoadRoute(route.RouteId);
            }

            if (GatherRoute.Count == 0)
            {
                IceLogging.Warning("No route is loaded, going to just immediately cancel here");
                return true;
            }

            if (!GatherRouteNavigation.IsCorrectTerritory(selectedRoute))
                return SchedulerMain.QueueActive ? false : true;

            AdvancePastUnavailableNodeGroups();

            if (RouteIndex >= GatherRoute.Count)
            {
                if (SchedulerMain.QueueActive)
                {
                    if (GatherQueueSession.IsCurrentTargetQuantityMet())
                        SchedulerMain.CompleteCurrentTarget();
                    else
                        RouteIndex = 0;

                    return true;
                }

                RouteIndex = 0;
            }

            var currentNode = GatherRoute[RouteIndex];
            TargetNodeId = currentNode.NodeId;

            if (currentNode.Locations.Count == 0)
            {
                RouteIndex += 1;
                NodeCheckIndex = 0;
                return false;
            }

            if (NodeCheckIndex >= currentNode.Locations.Count)
            {
                IceLogging.Debug($"Node {currentNode.NodeId} unavailable at all locations, moving to next group");
                RouteIndex += 1;
                NodeCheckIndex = 0;
                GatherRouteNavigation.ResetValidationApproach();
                return false;
            }

            var spawned = NavmeshMovement.FindSpawnedNodeInGroup(currentNode);
            var approachLocation = spawned?.location ?? currentNode.Locations[NodeCheckIndex];
            if (spawned != null)
            {
                var spawnedIndex = currentNode.Locations.IndexOf(spawned.Value.location);
                if (spawnedIndex >= 0)
                    NodeCheckIndex = spawnedIndex;
            }

            if (EzThrottler.Throttle("Travel Check throttle message"))
                IceLogging.Verbose("Currently in travel check mode");

            if (!GatherRouteNavigation.TryTravelWithinLoadRange(approachLocation))
            {
                if (EzThrottler.Throttle("Throttle message"))
                    IceLogging.Verbose($"Traveling to node location. Distance: {Player.DistanceTo(approachLocation.Position):N1}");
                return false;
            }

            var validNode = NavmeshMovement.GetAvailableNodeAtLocation(currentNode.NodeId, approachLocation.Position);

            if (EzThrottler.Throttle("IsNodeValid"))
                IceLogging.Debug($"Node available at location {NodeCheckIndex} (within {NavmeshMovement.LoadRange}y): {validNode != null}");

            if (validNode == null)
            {
                IceLogging.Debug($"Node not up at location {NodeCheckIndex}, trying next location");
                GatherRouteNavigation.ResetValidationApproach();
                NodeCheckIndex += 1;
                return false;
            }

            if (!GatherRouteNavigation.TryApproachGatherFan(approachLocation))
                return false;

            P.navmesh.StopIfOwned();

            if (NavmeshMovement.GetAvailableNodeAtLocation(currentNode.NodeId, approachLocation.Position) == null)
            {
                IceLogging.Debug("Node despawned before approach finished, rescanning");
                GatherRouteNavigation.ResetValidationApproach();
                NodeCheckIndex += 1;
                return false;
            }

            GatherRouteNavigation.ResetValidationApproach();
            NodeCheckIndex = 0;
            IceLogging.Debug("Found available node, starting approach");
            P.taskManager.Enqueue(() => CheckTravelKind(validNode), "Checking Travel Kind");
            return true;
        }

        private static void AdvancePastUnavailableNodeGroups()
        {
            while (RouteIndex < GatherRoute.Count && NavmeshMovement.IsGroupConfirmedUnavailable(GatherRoute[RouteIndex]))
            {
                IceLogging.Debug($"Node group {GatherRoute[RouteIndex].NodeId} not available, skipping");
                RouteIndex += 1;
                NodeCheckIndex = 0;
                GatherRouteNavigation.ResetValidationApproach();
            }
        }
        private static bool CheckTravelKind(IGameObject node)
        {
            if (GatherRouteNavigation.IsGatheringSessionActive())
                return true;

            var currentNode = GatherRoute[RouteIndex];
            var targetLocation = NavmeshMovement.MatchRouteLocation(currentNode, node.Position);
            if (targetLocation == null)
            {
                IceLogging.Error("Spawned node does not match any route location for this group");
                return true;
            }

            if (NavmeshMovement.GetAvailableNodeAtLocation(currentNode.NodeId, targetLocation.Position) == null)
            {
                IceLogging.Debug("Node no longer available at matched location, rescanning");
                GatherRouteNavigation.ResetValidationApproach();
                return true;
            }

            TargetFanPoint = NodeLocationExtensions.GetRandomFlightPosition(targetLocation, Player.Position);
            GatherRouteNavigation.ResetInteractRetries();
            GatherRouteNavigation.EnqueueApproach(node, currentNode, targetLocation);
            return true;
        }

        // Scheduler step after EnqueueApproach — targets node and opens gathering window.
        internal static bool InteractWithNode(uint nodeId)
        {
            if (!GatherRouteNavigation.TryCompleteInteract(nodeId, out var gatheringOpen))
                return false;

            if (gatheringOpen)
                _openedGatheringWindowThisNode = true;

            return true;
        }
        private static bool GatheringInteraction()
        {
            // Stop path following once gathering starts; ResetGatheringNavHalt when node is done.
            if (GatherRouteNavigation.IsGatheringSessionActive())
                NavmeshMovement.HaltNavmeshForGathering();

            if (!SchedulerMain.ItemId.HasValue)
                return true;

            var itemId = SchedulerMain.ItemId.Value;
            if (SchedulerMain.QueueActive)
            {
                var queueTarget = GatherQueueSession.CurrentTarget();
                if (queueTarget == null || queueTarget.TargetQuantity <= 0)
                    return true;

                itemId = queueTarget.ItemId;
            }

            if (Svc.Condition[ConditionFlag.Gathering])
            {
                _openedGatheringWindowThisNode = true;

                if (!Svc.Condition[ConditionFlag.ExecutingGatheringAction])
                {
                    if (GenericHelpers.TryGetAddonMaster<Gathering>("Gathering", out var gather) && gather.IsAddonReady)
                    {
                        if (gather.CurrentIntegrity != 0)
                        {
                            List<uint> Crystals = new()
                            {
                                // shards
                                2, 3, 4, 5, 6, 7,

                                // crystals
                                8, 9, 10, 11, 12, 13,

                                // clusters
                                14, 15, 16, 17, 18, 19
                            };

                            var gatherTarget = gather.GatheredItems.FirstOrDefault(x => x.ItemID == itemId);
                            if (gatherTarget == null)
                            {
                                if (EzThrottler.Throttle($"Missing gather slot for {itemId}", 3000))
                                    IceLogging.Warning($"Item {itemId} not in gathering window; check queue item vs node.");
                                return false;
                            }

                            // Select the queue item before buffs so default slot (often tomato) is not gathered.
                            if (EzThrottler.Throttle($"Select gather item: {itemId}", 200))
                                gatherTarget.Gather();

                            if (Crystals.Contains(itemId))
                            {
                                bool maxInteg = gather.TotalIntegrity == gather.CurrentIntegrity;

                                if (Crystal_BuffCheck(maxInteg))
                                    return false;
                            }
                            else if (Basic_BuffCheck())
                                return false;

                            if (EzThrottler.Throttle($"Gathering Item: {itemId}", 200))
                                gatherTarget.Gather();

                            return false;
                        }
                    }
                }
            }
            else
            {
                if (GatherRouteNavigation.IsGatheringSessionActive())
                    return false;

                NavmeshMovement.ResetGatheringNavHalt();
                if (_openedGatheringWindowThisNode)
                {
                    _openedGatheringWindowThisNode = false;
                    RouteIndex += 1;
                    TryApplyPendingRouteChange();
                }

                return true;
            }

            return false;
        }
        private static unsafe bool Basic_BuffCheck()
        {
            var actionInfo = Gather_Util.GathActionDict[GatherBuffId.BYII];
            bool hasBuff = Utils.HasStatusId(actionInfo.StatusId) || Utils.HasStatusId(actionInfo.StatusId2);
            bool hasGp = Utils.GetGp() >= actionInfo.RequiredGp;
            var actionId = actionInfo.ClassAction[Player.Job];

            if (!hasBuff && hasGp)
            {
                ActionManager.Instance()->UseAction(ActionType.Action, actionId);
                return true;
            }
            else
                return false;
        }

        private static unsafe bool Crystal_BuffCheck(bool MaxDurability)
        {
            var job = Player.Job;
            var level = Player.Level;

            var givingLand = Gather_Util.GathActionDict[GatherBuffId.GivingLand];
            var twelveBounty = Gather_Util.GathActionDict[GatherBuffId.TwelveBounty];

            if (MaxDurability)
            {
                var cooldown = BuffCooldown(givingLand.ClassAction[job]);
                if (cooldown < 40)
                {
                    // We're within possible range of using the skill, need to see if it's actually 0, if yes then we can buff, if not then we continue onwards.
                    if (cooldown == 0)
                    {
                        if (!Utils.HasStatusId(givingLand.StatusId) && (Utils.GetGp() >= givingLand.RequiredGp) && level >= givingLand.RequiredLv)
                        {
                            if (EzThrottler.Throttle("Using Action", 100))
                                ActionManager.Instance()->UseAction(ActionType.Action, givingLand.ClassAction[job]);

                            return true;
                        }
                    }
                }
                else
                {
                    if (!Utils.HasStatusId(twelveBounty.StatusId) && Utils.GetGp() >= twelveBounty.RequiredGp && level >= twelveBounty.RequiredLv)
                    {
                        if (EzThrottler.Throttle("Using Action", 100))
                            ActionManager.Instance()->UseAction(ActionType.Action, twelveBounty.ClassAction[job]);

                        return true;
                    }
                }
            }
            else
            {
                var increaseInteg = Gather_Util.GathActionDict[GatherBuffId.BonusIntegrity];
                var bonusInteg = Gather_Util.GathActionDict[GatherBuffId.BonusIntegrity_Chance];

                if (Utils.HasStatusId(bonusInteg.StatusId))
                {
                    if (EzThrottler.Throttle("Using Action", 100))
                        ActionManager.Instance()->UseAction(ActionType.Action, bonusInteg.ClassAction[job]);

                    return true;
                }

                if (Utils.HasStatusId(givingLand.StatusId))
                {
                    if (Utils.GetGp() >= increaseInteg.RequiredGp && level >= increaseInteg.RequiredLv)
                    {
                        if (EzThrottler.Throttle("Using Action", 100))
                            ActionManager.Instance()->UseAction(ActionType.Action, increaseInteg.ClassAction[job]);

                        return true;
                    }
                }
            }

            return false;
        }

        private static unsafe float BuffCooldown(uint ActionId)
        {
            var recastGroup = ActionManager.Instance()->GetRecastGroupDetail(ActionManager.Instance()->GetRecastGroup(1, ActionId));

            if (recastGroup != null)
            {
                float total = recastGroup->Total;     // total cooldown duration
                float elapsed = recastGroup->Elapsed; // how much has elapsed
                float remaining = total - elapsed;    // time remaining
                bool isActive = recastGroup->IsActive; // Is Active (leaving these here because it's just nice to know/might use in future)

                return remaining;
            }
            else
            {
                return 0;
            }
        }
    }
}
