using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using GatherChill.ConfigFiles;
using GatherChill.GatheringInfo;
using GatherChill.Utilities.GatheringHelpers;
using GatherChill.Utilities.Tools;
using GatherChill.Utilities.Utility;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace GatherChill.Scheduler.Tasks;

/// <summary>
/// Gathering-specific navigation layered on <see cref="Task_NavmeshMove"/>.
/// Implements the two-stage "fan" approach: fly to a flight fan (optional), ground walk to a gather fan,
/// then interact. <see cref="TryCompleteInteract"/> retries targeting when the node didn't open.
/// </summary>
internal static class GatherRouteNavigation
{
    // Remember which gather fan we walked to so interact retries can re-path if we're still short.
    private static uint _gatherFanNodeId;
    private static Vector3? _gatherFanPoint;

    // Cached gather fan while approaching a route location to validate node spawn.
    private static Vector3? _validationGatherFan;
    private static Vector3 _validationNodePos;

    public static bool IsGatheringSessionActive() => NavmeshMovement.IsGatheringSessionActive();

    public static void StopMovementForGathering() => NavmeshMovement.HaltNavmeshForGathering();

    public static void ResetInteractRetries()
    {
        _gatherFanNodeId = 0;
        _gatherFanPoint = null;
    }

    public static void ResetValidationApproach()
    {
        _validationGatherFan = null;
        _validationNodePos = default;
    }

    public static bool IsCorrectTerritory(GatheringRoute route)
    {
        if (route.TerritoryId == Player.Territory.RowId)
            return true;

        if (EzThrottler.Throttle($"Wrong territory {route.TerritoryId}", 5000))
            IceLogging.Warning($"Route {route.RouteId} expects territory {route.TerritoryId}, current is {Player.Territory.RowId}.");

        return false;
    }

    public static bool TryFlyToLocation(NodeLocation location, float closeRange, bool stayMounted, Vector3? nodeWorldPos = null)
    {
        var fanPoint = NavmeshMovement.ResolvePathPoint(
            NodeLocationExtensions.GetRandomFlightPosition(location, Player.Position, nodeWorldPos));

        if (location.AllowFlying && NavmeshMovement.CanUseFlyMovement())
            return Task_NavmeshMove.Task_FlyTo(fanPoint, waitForBusy: false, closeRange, stayMounted) == true;

        return Task_NavmeshMove.Task_GroundTo(fanPoint, waitForBusy: false, closeRange, stayMounted) == true;
    }

    public static bool TryGroundToPoint(Vector3 point, float closeRange = NavmeshMovement.FinalApproachCloseRange) =>
        Task_NavmeshMove.Task_GroundTo(NavmeshMovement.ResolvePathPoint(point), waitForBusy: true, closeRange) == true;

    public static bool TryFlyToPoint(Vector3 point, float closeRange, bool stayMounted = false) =>
        Task_NavmeshMove.Task_FlyTo(NavmeshMovement.ResolvePathPoint(point), waitForBusy: true, closeRange, stayMounted) == true;

    /// <summary>Fly in until within 75y of the live node (when known) or route anchor so the client node list can update.</summary>
    public static bool TryTravelWithinLoadRange(NodeLocation location, Vector3? nodeWorldPos = null)
    {
        var travelAnchor = nodeWorldPos ?? location.Position;

        if (NavmeshMovement.IsWithinLoadRangeOf(travelAnchor))
            return true;

        TryFlyToLocation(location, NavmeshMovement.FanApproachCloseRange, stayMounted: true, nodeWorldPos);
        return false;
    }

    /// <summary>
    /// Short final walk to the gather fan after node availability was confirmed within load range.
    /// Returns true only when standing at the gather fan on foot.
    /// </summary>
    public static bool TryApproachGatherFan(NodeLocation location, IGameObject node)
    {
        var nodeWorldPos = node.Position;

        if (_validationGatherFan is null || _validationNodePos != nodeWorldPos)
        {
            _validationNodePos = nodeWorldPos;
            _validationGatherFan = ResolveGatherFanPoint(location, nodeWorldPos);
        }

        var gatherFan = _validationGatherFan.Value;

        if (NavmeshMovement.HorizontalDistance(gatherFan) > NavmeshMovement.GroundValidationWalkRange)
        {
            if (location.AllowFlying && NavmeshMovement.CanUseFlyMovement() && NavmeshMovement.ShouldUseFlyPath(gatherFan))
            {
                Task_NavmeshMove.Task_FlyTo(
                    NavmeshMovement.ResolvePathPoint(gatherFan),
                    waitForBusy: false,
                    NavmeshMovement.GroundValidationWalkRange,
                    stayMounted: true);
            }
            else
            {
                Task_NavmeshMove.Task_GroundTo(gatherFan, waitForBusy: false, NavmeshMovement.GroundValidationWalkRange);
            }

            return false;
        }

        if (Player.Mounted)
        {
            Utils.Dismount();
            return false;
        }

        return Task_NavmeshMove.Task_GroundTo(
            gatherFan,
            waitForBusy: false,
            NavmeshMovement.NodeValidationCloseRange) == true;
    }

    /// <summary>
    /// Queue fly→ground→interact (or ground→interact) based on node flight settings and distance.
    /// Flight and gather fans come from route NodeLocation fan points, with standoff applied to gather fan.
    /// </summary>
    public static void EnqueueApproach(IGameObject node, GatheringNode group, NodeLocation targetLocation)
    {
        var nodePos = node.Position;
        var flightFan = NavmeshMovement.ResolvePathPoint(
            NodeLocationExtensions.GetRandomFlightPosition(targetLocation, Player.Position, nodePos));
        var gatherFan = ResolveGatherFanPoint(targetLocation, nodePos, flightFan);

        _gatherFanNodeId = node.BaseId;
        _gatherFanPoint = gatherFan;

        if (NavmeshMovement.ShouldUseFlyApproachForNode(targetLocation, node.Position))
        {
            IceLogging.Debug("Approach: fly then ground to gather fan");
            P.taskManager.EnqueueMulti
            (
                new(() => Task_NavmeshMove.Task_FlyTo(flightFan, true, NavmeshMovement.FinalApproachCloseRange, true), "Fly to fan", TaskConfig),
                new(() => Task_NavmeshMove.Task_GroundTo(gatherFan, true, NavmeshMovement.GatherFanCloseRange), "Ground to gather fan", TaskConfig),
                new(() => Task_GatherRoute.InteractWithNode(node.BaseId), "Interact with node", TaskConfig)
            );
        }
        else
        {
            IceLogging.Debug("Approach: ground to gather fan");
            P.taskManager.EnqueueMulti
            (
                new(() => Task_NavmeshMove.Task_GroundTo(gatherFan, true, NavmeshMovement.GatherFanCloseRange), "Ground to gather fan", TaskConfig),
                new(() => Task_GatherRoute.InteractWithNode(node.BaseId), "Interact with node", TaskConfig)
            );
        }
    }

    /// <summary>
    /// Called each tick while waiting for the gathering window. Returns true when gathering started.
    /// Re-targets and re-walks to gather fan if interact failed but the node is still targetable.
    /// </summary>
    public static bool TryCompleteInteract(uint nodeId, out bool gatheringWindowOpen)
    {
        gatheringWindowOpen = false;
        var targetNode = NavmeshMovement.GetNearestGatheringNode(nodeId);

        if (IsGatheringSessionActive())
        {
            gatheringWindowOpen = true;
            StopMovementForGathering();
            ResetInteractRetries();
            IceLogging.Info("Gathering window visible, continuing");
            return true;
        }

        if (targetNode == null)
        {
            if (EzThrottler.Throttle("No targetable gathering node", 2000))
                IceLogging.Debug("No targetable gathering node at fan, retrying interact");

            return false;
        }

        if (_gatherFanPoint is { } fan && _gatherFanNodeId == nodeId && !IsAtGatherFan(fan))
        {
            if (Player.Mounted)
            {
                Utils.Dismount();
                return false;
            }

            if (P.navmesh.TryMoveTo(fan, fly: false, NavmeshMovement.GatherFanCloseRange))
                return false;
        }

        if (Player.Mounted)
            Utils.Dismount();

        if (!Player.Mounted && !Player.IsJumping && EzThrottler.Throttle("Target + Interaction throttle"))
        {
            Utils.TargetgameObject(targetNode);
            Utils.InteractWithObject(targetNode);
        }

        return false;
    }

    private static bool IsAtGatherFan(Vector3 fan) =>
        Player.DistanceTo(fan) <= NavmeshMovement.GatherFanCloseRange + NavmeshMovement.InteractRetrySlack;

    /// <summary>Prefer explicit walk spots from the route editor; otherwise random gather fan around the node.</summary>
    private static Vector3 GetGatherFanPoint(NodeLocation targetLocation, Vector3 flightFanPoint, Vector3 nodeWorldPos)
    {
        if (targetLocation.UseSpecificWalkingSpots && targetLocation.WalkablePositions.Count > 0)
        {
            var offset = nodeWorldPos - targetLocation.Position;
            var shifted = targetLocation.WalkablePositions.Select(pos => pos + offset).ToList();
            return NodeLocationExtensions.GetNearestWalkablePosition(shifted, Player.Position);
        }

        return NodeLocationExtensions.GetRandomGatherPosition(targetLocation, Player.Position, nodeWorldPos);
    }

    private static Vector3 ResolveGatherFanPoint(NodeLocation location, Vector3 nodeWorldPos, Vector3? flightFanPoint = null)
    {
        var rawFan = flightFanPoint is { } fan
            ? GetGatherFanPoint(location, fan, nodeWorldPos)
            : GetGatherFanPoint(location, Player.Position, nodeWorldPos);

        return NavmeshMovement.ResolveGroundPathPoint(
            NavmeshMovement.ApplyNodeStandoff(rawFan, nodeWorldPos));
    }
}
