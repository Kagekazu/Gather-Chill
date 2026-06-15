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

    /// <summary>Fly in until within 75y of the route anchor so the client node list can update.</summary>
    public static bool TryTravelWithinLoadRange(NodeLocation location)
    {
        if (NavmeshMovement.IsWithinLoadRangeOf(location.Position))
            return true;

        TryFlyToLocation(location, NavmeshMovement.FanApproachCloseRange, stayMounted: true);
        return false;
    }

    /// <summary>
    /// Walk until within interact range of the live gathering object (not a navmesh-snapped point on a nearby path).
    /// </summary>
    public static bool TryApproachGatherFan(NodeLocation location, IGameObject liveNode)
    {
        var nodeCenter = liveNode.Position;

        if (_validationGatherFan is null || _validationNodePos != nodeCenter)
        {
            _validationNodePos = nodeCenter;
            _validationGatherFan = NavmeshMovement.GetInteractApproachPoint(liveNode);
        }

        var approachPoint = _validationGatherFan.Value;

        if (NavmeshMovement.IsWithinGatherInteractRange(liveNode))
        {
            // In interact range: drop the mount so targeting/gathering can fire.
            if (Player.Mounted)
            {
                Utils.Dismount();
                return false;
            }

            return true;
        }

        // Still travelling toward the node: stay mounted (or let MoveTo auto-mount). Dismounting here
        // fights MoveTo's long-distance auto-mount and leaves us mounting/dismounting in place — which
        // strands us when the next node is within load range but well outside the final-approach radius.
        if (NavmeshMovement.HorizontalDistance(approachPoint) > NavmeshMovement.GroundValidationWalkRange)
        {
            if (location.AllowFlying && NavmeshMovement.CanUseFlyMovement() && NavmeshMovement.ShouldUseFlyPath(approachPoint))
            {
                Task_NavmeshMove.Task_FlyTo(
                    NavmeshMovement.ResolvePathPoint(approachPoint),
                    waitForBusy: false,
                    NavmeshMovement.GroundValidationWalkRange,
                    stayMounted: true);
            }
            else
            {
                Task_NavmeshMove.Task_GroundTo(approachPoint, waitForBusy: false, NavmeshMovement.GroundValidationWalkRange);
            }

            return false;
        }

        // Final approach: dismount and walk the last few yalms onto the gather fan.
        if (Player.Mounted)
        {
            Utils.Dismount();
            return false;
        }

        return Task_NavmeshMove.Task_GroundTo(
            approachPoint,
            waitForBusy: false,
            NavmeshMovement.FinalApproachCloseRange) == true
            && NavmeshMovement.IsWithinGatherInteractRange(liveNode);
    }

    /// <summary>Final ground walk until the live node is within game interact range.</summary>
    public static bool WalkToInteractNode(IGameObject node)
    {
        if (Player.Mounted)
        {
            Utils.Dismount();
            return false;
        }

        if (NavmeshMovement.IsWithinGatherInteractRange(node))
            return true;

        var approachPoint = NavmeshMovement.GetInteractApproachPoint(node);
        _gatherFanNodeId = node.BaseId;
        _gatherFanPoint = approachPoint;

        var arrived = Task_NavmeshMove.Task_GroundTo(
            approachPoint,
            waitForBusy: true,
            NavmeshMovement.FinalApproachCloseRange) == true;

        if (!arrived)
            return false;

        if (NavmeshMovement.IsWithinGatherInteractRange(node))
            return true;

        if (EzThrottler.Throttle($"Short of interact range {node.BaseId}", 2000))
            IceLogging.Debug(
                $"Reached approach point but still {Player.DistanceTo(node):N2}y from node {node.BaseId} (need {NavmeshMovement.GatherInteractDistance}y)");

        return false;
    }

    /// <summary>
    /// Queue fly→ground→interact (or ground→interact) based on node flight settings and distance.
    /// Flight and gather fans come from route NodeLocation fan points, with standoff applied to gather fan.
    /// </summary>
    public static void EnqueueApproach(IGameObject node, GatheringNode group, NodeLocation targetLocation)
    {
        var nodeCenter = node.Position;
        var flightFan = NavmeshMovement.ResolvePathPoint(
            NodeLocationExtensions.GetRandomFlightPosition(targetLocation, Player.Position, nodeCenter));
        var interactPoint = NavmeshMovement.GetInteractApproachPoint(node);

        _gatherFanNodeId = node.BaseId;
        _gatherFanPoint = interactPoint;

        if (NavmeshMovement.ShouldUseFlyApproachForNode(targetLocation, interactPoint))
        {
            IceLogging.Debug($"Approach: fly then walk to node {node.BaseId} (interact range)");
            P.taskManager.EnqueueMulti
            (
                new(() => Task_NavmeshMove.Task_FlyTo(flightFan, true, NavmeshMovement.FinalApproachCloseRange, true), "Fly to fan", TaskConfig),
                new(() => WalkToInteractNode(node), "Walk to node", TaskConfig),
                new(() => Task_GatherRoute.InteractWithNode(node.BaseId), "Interact with node", TaskConfig)
            );
        }
        else
        {
            IceLogging.Debug($"Approach: ground walk to node {node.BaseId} (interact range)");
            P.taskManager.EnqueueMulti
            (
                new(() => WalkToInteractNode(node), "Walk to node", TaskConfig),
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

        if (_gatherFanPoint is { } approachPt && _gatherFanNodeId == nodeId
            && targetNode != null && !NavmeshMovement.IsWithinGatherInteractRange(targetNode))
        {
            if (Player.Mounted)
            {
                Utils.Dismount();
                return false;
            }

            var liveApproach = NavmeshMovement.GetInteractApproachPoint(targetNode);
            _gatherFanPoint = liveApproach;
            if (P.navmesh.TryMoveTo(liveApproach, fly: false, NavmeshMovement.FinalApproachCloseRange))
                return false;
        }

        if (Player.Mounted)
            Utils.Dismount();

        if (!Player.Mounted && !Player.IsJumping && targetNode != null
            && NavmeshMovement.IsWithinGatherInteractRange(targetNode)
            && EzThrottler.Throttle("Target + Interaction throttle"))
        {
            Utils.TargetgameObject(targetNode);
            Utils.InteractWithObject(targetNode);
        }

        return false;
    }

    private static bool IsAtGatherFan(Vector3 fan) =>
        Player.DistanceTo(fan) <= NavmeshMovement.GatherFanCloseRange + NavmeshMovement.InteractRetrySlack;

    /// <summary>Prefer explicit walk spots from the route editor; otherwise random gather fan around the node.</summary>
    private static Vector3 GetGatherFanPoint(NodeLocation targetLocation, Vector3 flightFanPoint, Vector3 nodeCenter)
    {
        if (targetLocation.UseSpecificWalkingSpots && targetLocation.WalkablePositions.Count > 0)
            return NodeLocationExtensions.GetNearestWalkablePosition(targetLocation.WalkablePositions, Player.Position);

        return NodeLocationExtensions.GetRandomGatherPosition(targetLocation, Player.Position, nodeCenter);
    }

    private static Vector3 ResolveGatherFanPoint(NodeLocation location, Vector3 nodeCenter, Vector3? flightFanPoint = null)
    {
        var rawFan = flightFanPoint is { } fan
            ? GetGatherFanPoint(location, fan, nodeCenter)
            : GetGatherFanPoint(location, Player.Position, nodeCenter);

        return NavmeshMovement.ResolveGatherApproachPoint(
            NavmeshMovement.ApplyNodeStandoff(rawFan, nodeCenter), nodeCenter);
    }
}
