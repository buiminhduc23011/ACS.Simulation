using System.Globalization;
using System.Linq;
using System.Text.Json;
using ACS.Simulator.API.Core.Models;
using ACS.Simulator.API.Core.Utils;
using Microsoft.Extensions.Logging;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// VirtualAgv — Order processing: receive VDA5050 orders, execute nodes, handle instant actions, cancel orders.
/// </summary>
public partial class VirtualAgv
{
    private async Task ProcessOrderAsync(Vda5050Order order)
    {
        var effectiveOrder = TranslateIncomingOrderMapIds(order);

        if (_currentOrder != null)
        {
            if (_currentOrder.OrderId == effectiveOrder.OrderId &&
                effectiveOrder.OrderUpdateId <= _currentOrder.OrderUpdateId)
            {
                _logger.LogInformation(
                    "AGV {SerialNumber} ignored stale/duplicate order message: orderId={OrderId}, updateId={IncomingUpdateId}, currentUpdateId={CurrentUpdateId}",
                    _config.SerialNumber,
                    effectiveOrder.OrderId,
                    effectiveOrder.OrderUpdateId,
                    _currentOrder.OrderUpdateId);
                return;
            }

            // Guard against delayed ORDER UPDATE from a previous orderId after we've switched to a new order.
            if (_currentOrder.OrderId != effectiveOrder.OrderId && effectiveOrder.OrderUpdateId > 0)
            {
                _logger.LogWarning(
                    "AGV {SerialNumber} ignored stale foreign order update: incomingOrderId={IncomingOrderId}, incomingUpdateId={IncomingUpdateId}, activeOrderId={ActiveOrderId}",
                    _config.SerialNumber,
                    effectiveOrder.OrderId,
                    effectiveOrder.OrderUpdateId,
                    _currentOrder.OrderId);
                return;
            }
        }

        // Check if this is an order update (same orderId, higher updateId)
        bool isOrderUpdate = _currentOrder != null
            && _currentOrder.OrderId == effectiveOrder.OrderId
            && effectiveOrder.OrderUpdateId > _currentOrder.OrderUpdateId;

        if (isOrderUpdate)
        {
            _logger.LogInformation("AGV {SerialNumber} received ORDER UPDATE {OrderId} (updateId: {OldUpdateId} -> {NewUpdateId})",
                _config.SerialNumber, effectiveOrder.OrderId, _currentOrder!.OrderUpdateId, effectiveOrder.OrderUpdateId);

            // Extensions are merged in the actor path and must preserve the live action CTS.
            await ApplyCompatibilityOrderUpdateAsync(effectiveOrder);
            return;
        }

        // VDA5050 Validation for new order
        if (effectiveOrder.Nodes.Count == 0)
        {
            _logger.LogWarning("AGV {SerialNumber} rejected new order {OrderId}: order has no nodes", _config.SerialNumber, effectiveOrder.OrderId);
            _currentState.Errors.Add(new VdaError { ErrorType = "orderError", ErrorLevel = "WARNING", ErrorDescription = "Order has no nodes" });
            await PublishStateAsync(true);
            return;
        }

        // The vehicle owns its charging transition before it starts a new navigation order.
        if (_isCharging)
        {
            _isCharging = false;
            _currentState.BatteryState.Charging = false;
            _logger.LogInformation(
                "AGV {SerialNumber} stopped charging before accepting navigation order {OrderId}",
                _config.SerialNumber,
                effectiveOrder.OrderId);
        }

        var incomingFirstNode = effectiveOrder.Nodes[0];

        // New order - reset and start fresh. Keep a live action CTS so cancelOrder/E-stop
        // can interrupt pick/drop; startPause never owns this token.
        _actionCts?.Cancel();
        _actionCts?.Dispose();
        _actionCts = new CancellationTokenSource();
        _currentOrder = CloneIncomingOrder(effectiveOrder);
        _currentNodeIndex = -1;
        _isMoving = false;
        _isRotating = false;
        _isReversing = false;
        _arrivalTheta = null;
        _targetTheta = _currentTheta;
        _currentMovementBlocker = null;

        _currentState.OrderId = effectiveOrder.OrderId;
        _currentState.OrderUpdateId = effectiveOrder.OrderUpdateId;
        _currentState.NewBaseRequest = false;
        _currentState.ActionStates.Clear();
        _currentState.EdgeStates.Clear();

        var releasedNodes = effectiveOrder.Nodes.Where(n => n.Released).Select(n => n.NodeId).ToList();
        var unreleasedNodes = effectiveOrder.Nodes.Where(n => !n.Released).Select(n => n.NodeId).ToList();

        _logger.LogInformation("AGV {SerialNumber} processing NEW order {OrderId} - Nodes: {TotalCount} (released: [{Released}], unreleased: [{Unreleased}])",
            _config.SerialNumber, effectiveOrder.OrderId, effectiveOrder.Nodes.Count,
            string.Join(", ", releasedNodes),
            string.Join(", ", unreleasedNodes));

        // Publish state immediately on order received (state change)
        await PublishStateAsync(true);

        // Start executing order
        await ExecuteNextNodeAsync();
    }

    private Vda5050Order TranslateIncomingOrderMapIds(Vda5050Order order)
    {
        return new Vda5050Order
        {
            HeaderId = order.HeaderId,
            Timestamp = order.Timestamp,
            Version = order.Version,
            Manufacturer = order.Manufacturer,
            SerialNumber = order.SerialNumber,
            OrderId = order.OrderId,
            OrderUpdateId = order.OrderUpdateId,
            ZoneSetId = order.ZoneSetId,
            Nodes = order.Nodes
                .Select(node => new Node
                {
                    NodeId = node.NodeId,
                    SequenceId = node.SequenceId,
                    Released = node.Released,
                    NodePosition = node.NodePosition == null
                        ? null
                        : new NodePosition
                        {
                            X = node.NodePosition.X,
                            Y = node.NodePosition.Y,
                            Theta = node.NodePosition.Theta,
                            MapId = ResolveInboundMapId(node.NodePosition.MapId)
                        },
                    Actions = node.Actions
                        .Select(CloneAction)
                        .ToList()
                })
                .OrderBy(node => node.SequenceId)
                .ToList(),
            Edges = order.Edges
                .Select(edge => new Edge
                {
                    EdgeId = edge.EdgeId,
                    SequenceId = edge.SequenceId,
                    Released = edge.Released,
                    StartNodeId = edge.StartNodeId,
                    EndNodeId = edge.EndNodeId,
                    MaxSpeed = edge.MaxSpeed,
                    Orientation = edge.Orientation,
                    OrientationType = edge.OrientationType,
                    Direction = edge.Direction,
                    RotationAllowed = edge.RotationAllowed,
                    MaxRotationSpeed = edge.MaxRotationSpeed,
                    Length = edge.Length,
                    Actions = edge.Actions
                        .Select(CloneAction)
                        .ToList(),
                    Trajectory = edge.Trajectory == null
                        ? null
                        : new Trajectory
                        {
                            Degree = edge.Trajectory.Degree,
                            KnotVector = edge.Trajectory.KnotVector.ToList(),
                            ControlPoints = edge.Trajectory.ControlPoints
                                .Select(point => new ControlPoint
                                {
                                    X = point.X,
                                    Y = point.Y,
                                    Weight = point.Weight
                                })
                                .ToList()
                        }
                })
                .OrderBy(edge => edge.SequenceId)
                .ToList()
        };
    }

    private static VdaAction CloneAction(VdaAction action)
    {
        return new VdaAction
        {
            ActionType = action.ActionType,
            ActionId = action.ActionId,
            ActionDescription = action.ActionDescription,
            BlockingType = action.BlockingType,
            ActionParameters = action.ActionParameters?
                .Select(parameter => new ActionParameter
                {
                    Key = parameter.Key,
                    Value = parameter.Value
                })
                .ToList()
        };
    }

    private async Task ApplyCompatibilityOrderUpdateAsync(Vda5050Order order)
    {
        var previousOrder = _currentOrder!;
        var previousCursorNode = TryGetCurrentTargetNode(previousOrder);
        var previousOrderCompleted = _currentNodeIndex >= previousOrder.Nodes.Count;

        if (!TryMergeOrderUpdate(previousOrder, order, out var mergedOrder, out var mergeFailure))
        {
            _logger.LogWarning(
                "AGV {SerialNumber} kept order {OrderId} update {CurrentUpdateId}; rejected discontinuous update {IncomingUpdateId}. reason={Reason}",
                _config.SerialNumber,
                previousOrder.OrderId,
                previousOrder.OrderUpdateId,
                order.OrderUpdateId,
                mergeFailure);
            _currentState.NewBaseRequest = true;
            await PublishStateAsync(true);
            return;
        }

        var cursorIndex = -1;
        if (previousCursorNode != null &&
            !TryFindOrderNodeIndex(mergedOrder.Nodes, previousCursorNode.NodeId, previousCursorNode.SequenceId, out cursorIndex))
        {
            _logger.LogWarning(
                "AGV {SerialNumber} kept order {OrderId} update {CurrentUpdateId}; rejected update {IncomingUpdateId} because the active target {TargetNodeId}/{TargetSequenceId} is absent.",
                _config.SerialNumber,
                previousOrder.OrderId,
                previousOrder.OrderUpdateId,
                order.OrderUpdateId,
                previousCursorNode.NodeId,
                previousCursorNode.SequenceId);
            _currentState.NewBaseRequest = true;
            await PublishStateAsync(true);
            return;
        }

        if (previousOrderCompleted &&
            !TryFindOrderNodeIndex(
                mergedOrder.Nodes,
                _currentState.LastNodeId,
                _currentState.LastNodeSequenceId,
                out cursorIndex))
        {
            _logger.LogWarning(
                "AGV {SerialNumber} kept order {OrderId} update {CurrentUpdateId}; rejected extension {IncomingUpdateId} because completed base {LastNodeId}/{LastNodeSequenceId} is absent.",
                _config.SerialNumber,
                previousOrder.OrderId,
                previousOrder.OrderUpdateId,
                order.OrderUpdateId,
                _currentState.LastNodeId,
                _currentState.LastNodeSequenceId);
            _currentState.NewBaseRequest = true;
            await PublishStateAsync(true);
            return;
        }

        _currentOrder = mergedOrder;
        _currentState.OrderId = order.OrderId;
        _currentState.OrderUpdateId = order.OrderUpdateId;

        if (previousCursorNode != null || previousOrderCompleted)
        {
            _currentNodeIndex = cursorIndex;
        }

        if (_isMoving)
        {
            _logger.LogInformation(
                "AGV {SerialNumber} merged ORDER UPDATE {OrderId} ({OldUpdateId} -> {NewUpdateId}) while preserving active target {TargetNodeId}/{TargetSequenceId}",
                _config.SerialNumber,
                order.OrderId,
                previousOrder.OrderUpdateId,
                order.OrderUpdateId,
                previousCursorNode?.NodeId ?? "none",
                previousCursorNode?.SequenceId);
            await PublishStateAsync(true);
            return;
        }

        var shouldResume = HasReleasedNodeAfterCursor(_currentOrder.Nodes, _currentNodeIndex);

        // A wait for the next released node is not a new-base request per VDA5050.
        // Keep NewBaseRequest as-is (normally false) so ACS keeps extending.
        if (!shouldResume)
        {
            _logger.LogInformation(
                "AGV {SerialNumber} merged ORDER UPDATE {OrderId} and is waiting at cursor {Cursor} for the next released node.",
                _config.SerialNumber,
                order.OrderId,
                _currentNodeIndex);
            await PublishStateAsync(true);
            return;
        }

        _logger.LogInformation(
            "AGV {SerialNumber} merged ORDER UPDATE {OrderId}; resuming from cursor {Cursor} on the retained order path.",
            _config.SerialNumber,
            order.OrderId,
            _currentNodeIndex);
        _currentState.NewBaseRequest = false;
        await PublishStateAsync(true);
        await ExecuteNextNodeAsync();
    }

    private Vda5050Order CloneIncomingOrder(Vda5050Order order)
    {
        return new Vda5050Order
        {
            HeaderId = order.HeaderId,
            Timestamp = order.Timestamp,
            Version = order.Version,
            Manufacturer = order.Manufacturer,
            SerialNumber = order.SerialNumber,
            OrderId = order.OrderId,
            OrderUpdateId = order.OrderUpdateId,
            ZoneSetId = order.ZoneSetId,
            Nodes = order.Nodes
                .OrderBy(node => node.SequenceId)
                .ToList(),
            Edges = order.Edges
                .OrderBy(edge => edge.SequenceId)
                .ToList()
        };
    }

    private static bool TryMergeOrderUpdate(
        Vda5050Order currentOrder,
        Vda5050Order incomingOrder,
        out Vda5050Order mergedOrder,
        out string failure)
    {
        mergedOrder = null!;
        failure = string.Empty;

        if (incomingOrder.Nodes.Count == 0)
        {
            failure = "incoming_order_has_no_nodes";
            return false;
        }

        var incomingNodes = incomingOrder.Nodes
            .OrderBy(node => node.SequenceId)
            .ToList();
        var firstIncomingNode = incomingNodes[0];
        var matchingAnchor = currentOrder.Nodes.FirstOrDefault(node =>
            node.SequenceId == firstIncomingNode.SequenceId &&
            NodeIdsEqual(node.NodeId, firstIncomingNode.NodeId));

        if (matchingAnchor == null)
        {
            failure = $"missing_anchor:{firstIncomingNode.NodeId}/{firstIncomingNode.SequenceId}";
            return false;
        }

        var retainedNodes = currentOrder.Nodes
            .Where(node => node.SequenceId < firstIncomingNode.SequenceId);
        var mergedNodes = retainedNodes
            .Concat(incomingNodes)
            .OrderBy(node => node.SequenceId)
            .ToList();

        if (mergedNodes.Select(node => node.SequenceId).Distinct().Count() != mergedNodes.Count)
        {
            failure = "duplicate_node_sequence";
            return false;
        }

        var mergedEdges = currentOrder.Edges
            .Where(edge => edge.SequenceId < firstIncomingNode.SequenceId)
            .Concat(incomingOrder.Edges)
            .GroupBy(edge => edge.SequenceId)
            .Select(group => group.Last())
            .OrderBy(edge => edge.SequenceId)
            .ToList();

        mergedOrder = new Vda5050Order
        {
            HeaderId = incomingOrder.HeaderId,
            Timestamp = incomingOrder.Timestamp,
            Version = incomingOrder.Version,
            Manufacturer = incomingOrder.Manufacturer,
            SerialNumber = incomingOrder.SerialNumber,
            OrderId = incomingOrder.OrderId,
            OrderUpdateId = incomingOrder.OrderUpdateId,
            ZoneSetId = incomingOrder.ZoneSetId,
            Nodes = mergedNodes,
            Edges = mergedEdges
        };
        return true;
    }

    private Node? TryGetCurrentTargetNode(Vda5050Order order)
    {
        if (_currentNodeIndex < 0 || _currentNodeIndex >= order.Nodes.Count)
        {
            return null;
        }

        return order.Nodes[_currentNodeIndex];
    }

    private void StopActiveMovementForCompatibilityUpdate()
    {
        _isMoving = false;
        _isRotating = false;
        _isReversing = false;
        _arrivalTheta = null;
        _targetTheta = _currentTheta;
        _currentMovementBlocker = null;
        _activeTrajectoryPoints = null;
        _currentTrajectorySegmentIndex = 0;
        _currentEdgeMaxSpeed = 0.0;
        _currentState.Driving = false;
        _currentState.Velocity!.Vx = 0;
        _currentState.Velocity.Vy = 0;
        _currentState.Velocity.Omega = 0;
        _currentVisualization.Velocity!.Vx = 0;
        _currentVisualization.Velocity.Vy = 0;
        _currentVisualization.Velocity.Omega = 0;
        StopVisualizationTimer();
    }

    private static bool TryFindOrderNodeIndex(
        IReadOnlyList<Node> nodes,
        string? nodeId,
        int sequenceId,
        out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        index = nodes
            .Select((node, idx) => new { node, idx })
            .Where(entry => entry.node.SequenceId == sequenceId &&
                            NodeIdsEqual(entry.node.NodeId, nodeId))
            .Select(entry => entry.idx)
            .DefaultIfEmpty(-1)
            .First();
        return index >= 0;
    }

    private static bool HasReleasedNodeAfterCursor(
        IReadOnlyList<Node> nodes,
        int cursor)
    {
        var nextIndex = cursor + 1;
        return nextIndex >= 0 &&
               nextIndex < nodes.Count &&
               nodes[nextIndex].Released;
    }

    private static Edge? FindEdgeOccurrence(
        IReadOnlyList<Edge> edges,
        Node startNode,
        Node endNode)
    {
        var pairMatches = edges
            .Where(edge =>
                edge.StartNodeId.Equals(startNode.NodeId, StringComparison.OrdinalIgnoreCase) &&
                edge.EndNodeId.Equals(endNode.NodeId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return pairMatches.FirstOrDefault(edge =>
            EdgeSequenceMatchesOccurrence(edge.SequenceId, startNode.SequenceId, endNode.SequenceId));
    }

    private static bool EdgeSequenceMatchesOccurrence(int edgeSequenceId, int startSequenceId, int endSequenceId)
        => edgeSequenceId > startSequenceId && edgeSequenceId < endSequenceId ||
           endSequenceId <= startSequenceId + 1 &&
           (edgeSequenceId == startSequenceId + 1 || edgeSequenceId == startSequenceId);

    private static bool NodeIdsEqual(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task ExecuteNextNodeAsync()
    {
        if (_currentOrder == null || _currentOrder.Nodes.Count == 0)
            return;

        _currentNodeIndex++;

        if (_currentNodeIndex >= _currentOrder.Nodes.Count)
        {
            _logger.LogInformation("AGV {SerialNumber} completed order {OrderId}",
                _config.SerialNumber, _currentOrder.OrderId);

            _currentState.Driving = false;
            _isMoving = false;
            _isRotating = false;
            _isReversing = false;
            _arrivalTheta = null;
            _targetTheta = _currentTheta;

            StopVisualizationTimer();

            await PublishStateAsync(true);
            return;
        }

        var node = _currentOrder.Nodes[_currentNodeIndex];

        // VDA5050 Horizon Control: Check if node is released
        if (!node.Released)
        {
            _logger.LogWarning("AGV {SerialNumber} waiting at node index {Index} - next node {NodeId} is NOT RELEASED (traffic control)",
                _config.SerialNumber, _currentNodeIndex - 1, node.NodeId);

            // A release-boundary wait is NOT a request for a new base per VDA5050:
            // the AGV simply stops and waits for the fleet manager to extend the order.
            // newBaseRequest must stay false here so ACS keeps dispatching EXTENSIONs.
            _isMoving = false;
            _currentState.Driving = false;
            StopVisualizationTimer();
            await PublishStateAsync(true);

            // Decrement index to retry this node when order is updated
            _currentNodeIndex--;
            return;
        }

        // Reset NewBaseRequest if we can proceed
        _currentState.NewBaseRequest = false;

        _logger.LogInformation("AGV {SerialNumber} executing node {NodeId} (sequence {Sequence}, released: {Released})",
            _config.SerialNumber, node.NodeId, node.SequenceId, node.Released);

        if (node.NodePosition != null)
        {
            // Track previous node id for edge-aware speed lookup
            var previousNode = _currentNodeIndex > 0 && _currentOrder.Nodes.Count > 0
                ? _currentOrder.Nodes[_currentNodeIndex - 1]
                : null;
            _prevNodeId = previousNode?.NodeId ?? string.Empty;
            var orderEdge = previousNode != null
                ? FindOrderEdge(previousNode, node)
                : null;

            if (previousNode != null && orderEdge == null)
            {
                _logger.LogError(
                    "AGV {SerialNumber} cannot execute node {NodeId}/{SequenceId}: retained order has no edge from {PreviousNodeId}/{PreviousSequenceId}.",
                    _config.SerialNumber,
                    node.NodeId,
                    node.SequenceId,
                    previousNode.NodeId,
                    previousNode.SequenceId);
                _currentNodeIndex--;
                _currentState.NewBaseRequest = true;
                _isMoving = false;
                _currentState.Driving = false;
                StopVisualizationTimer();
                await PublishStateAsync(true);
                return;
            }

            _targetX = node.NodePosition.X;
            _targetY = node.NodePosition.Y;

            // Generate curved trajectory points if needed
            if (orderEdge != null && orderEdge.Trajectory?.ControlPoints?.Count > 0)
            {
                // Canonical VDA NURBS control points include start/end. CurveUtils treats CPs as interior handles.
                var cps = orderEdge.Trajectory.ControlPoints;
                var degree = orderEdge.Trajectory.Degree > 0 ? orderEdge.Trajectory.Degree : (cps.Count >= 4 ? 3 : 2);
                List<ACS.Simulator.API.Core.Models.ControlPoint> handles;
                if (degree == 2 && cps.Count == 3)
                    handles = new List<ACS.Simulator.API.Core.Models.ControlPoint> { cps[1] };
                else if (degree == 3 && cps.Count == 4)
                    handles = new List<ACS.Simulator.API.Core.Models.ControlPoint> { cps[1], cps[2] };
                else if (cps.Count > 2)
                    handles = cps.Skip(1).Take(cps.Count - 2).ToList();
                else
                    handles = cps;

                _activeTrajectoryPoints = CurveUtils.GenerateCurvePoints(
                    _currentX, _currentY,
                    _targetX, _targetY,
                    handles,
                    50 // 50 segments for smooth movement
                );
                _currentTrajectorySegmentIndex = 0;
            }
            else
            {
                _activeTrajectoryPoints = null;
                _currentTrajectorySegmentIndex = 0;
            }

            ConfigureMovementPlan(node, orderEdge);

            // ── Cross-Map: propagate MapId to state and visualization ──
            var previousMapId = _currentMapId;
            _currentMapId = node.NodePosition.MapId;
            if (_currentState.AgvPosition != null)
                _currentState.AgvPosition.MapId = _currentMapId;
            if (_currentVisualization?.AgvPosition != null)
                _currentVisualization.AgvPosition.MapId = _currentMapId;
            if (!string.IsNullOrEmpty(previousMapId) && previousMapId != _currentMapId)
            {
                _logger.LogInformation(
                    "AGV {Serial}: Map transition {PrevMap} → {NewMap} at node {NodeId}",
                    _config.SerialNumber, previousMapId, _currentMapId, node.NodeId);
            }

            // Consume a pause that arrived during station action before rolling to next hop.
            if (_pauseLatched || _currentState.Paused)
            {
                _pauseLatched = true;
                _isMoving = false;
                _currentState.Paused = true;
                _currentState.Driving = false;
                ZeroStoppedVelocities();
                StopVisualizationTimer();
                _currentNodeIndex--;
                _logger.LogInformation(
                    "AGV {SerialNumber} held at node {NodeId} — startPause pending, waiting for stopPause before movement",
                    _config.SerialNumber,
                    node.NodeId);
                await PublishStateAsync(true);
                return;
            }

            _isMoving = true;
            _currentState.Driving = true;

            // Cache the current edge maxSpeed
            _currentEdgeMaxSpeed = orderEdge?.MaxSpeed ?? _movementConfig.Speed;

            StartVisualizationTimer();

            await PublishStateAsync(true);

            _logger.LogInformation(
                "AGV {SerialNumber} moving to ({X}, {Y}) with heading {Heading:F2} rad ({Direction})",
                _config.SerialNumber,
                _targetX,
                _targetY,
                _targetTheta,
                _isReversing ? "reverse" : "forward");
        }
        else
        {
            await ExecuteNodeActionsAsync(node);
            await ExecuteNextNodeAsync();
        }
    }

    private bool HasRunningNodeLoadAction()
        => _currentState.ActionStates.Any(action =>
            action.ActionStatus is "WAITING" or "INITIALIZING" or "RUNNING" or "PAUSED" &&
            action.ActionType is not null &&
            (action.ActionType.Equals("pick", StringComparison.OrdinalIgnoreCase) ||
             action.ActionType.Equals("drop", StringComparison.OrdinalIgnoreCase) ||
             action.ActionType.Equals("lift", StringComparison.OrdinalIgnoreCase) ||
             action.ActionType.Equals("lower", StringComparison.OrdinalIgnoreCase)));

    private async Task ExecuteNodeActionsAsync(Node node)
    {
        if (node.Actions == null || node.Actions.Count == 0)
        {
            return;
        }

        foreach (var action in node.Actions)
        {
            await ExecuteActionAsync(action);
        }
    }

    private Edge? FindOrderEdge(Node startNode, Node endNode)
    {
        if (_currentOrder == null)
            return null;

        return FindEdgeOccurrence(_currentOrder.Edges, startNode, endNode);
    }

    private async Task ProcessInstantActionsAsync(Vda5050InstantActions instantActions)
    {
        foreach (var action in instantActions.Actions)
        {
            _logger.LogInformation("AGV {SerialNumber} processing instant action: {ActionType} (id: {ActionId})",
                _config.SerialNumber, action.ActionType, action.ActionId);

            var actionState = new ActionState
            {
                ActionId = action.ActionId,
                ActionType = action.ActionType,
                ActionStatus = "RUNNING"
            };
            _currentState.ActionStates.Add(actionState);

            try
            {
                var normalizedActionType = NormalizeInstantActionType(action.ActionType);
                switch (normalizedActionType)
                {
                    case InstantActionType.StartPause:
                        // Movement pause only — never cancel pick/drop action tokens.
                        _pauseLatched = true;
                        _currentState.Paused = true;
                        _isMoving = false;
                        _currentState.Driving = false;
                        ZeroStoppedVelocities();
                        StopVisualizationTimer();
                        _logger.LogInformation("AGV {SerialNumber} PAUSED via instant action", _config.SerialNumber);
                        actionState.ActionStatus = "FINISHED";
                        break;

                    case InstantActionType.StopPause:
                        _pauseLatched = false;
                        _currentState.Paused = false;
                        _logger.LogInformation("AGV {SerialNumber} RESUMED via instant action", _config.SerialNumber);
                        // Station pick/drop continues independently while paused. After resume:
                        // - if a navigation hop is already active, re-enable movement;
                        // - otherwise advance to the next released node (pause may have deferred it).
                        if (_currentOrder != null &&
                            _currentNodeIndex >= 0 &&
                            !HasRunningNodeLoadAction())
                        {
                            if (_isMoving ||
                                (_currentNodeIndex < _currentOrder.Nodes.Count &&
                                 Math.Abs(_targetX - _currentX) + Math.Abs(_targetY - _currentY) > _movementConfig.Tolerance))
                            {
                                _isMoving = true;
                                _currentState.Driving = true;
                                StartVisualizationTimer();
                            }
                            else
                            {
                                await ExecuteNextNodeAsync();
                            }
                        }
                        actionState.ActionStatus = "FINISHED";
                        break;

                    case InstantActionType.CancelOrder:
                        actionState.ActionStatus = "FINISHED";
                        actionState.ResultDescription = "Order cancelled";
                        ResetStateForCancelledOrder(actionState);
                        _logger.LogInformation("AGV {SerialNumber} order CANCELLED via instant action", _config.SerialNumber);
                        break;

                    case InstantActionType.StartCharging:
                        _isCharging = true;
                        _currentState.BatteryState.Charging = true;
                        _currentState.OperatingMode = "AUTOMATIC";
                        // Restart the battery clock so the first charging tick credits only the time
                        // actually spent charging, not the whole interval since the previous tick.
                        _lastIdleDrainTime = DateTime.UtcNow;
                        _logger.LogInformation("AGV {SerialNumber} started CHARGING (battery: {Level:F1}%)",
                            _config.SerialNumber, _batteryLevel);
                        actionState.ActionStatus = "FINISHED";
                        break;

                    case InstantActionType.InitPosition:
                        if (!TryGetActionParameterDouble(action, "x", out var initializedX) ||
                            !TryGetActionParameterDouble(action, "y", out var initializedY) ||
                            !TryGetActionParameterDouble(action, "theta", out var initializedTheta) ||
                            string.IsNullOrWhiteSpace(GetActionParameterText(action, "mapId")))
                        {
                            throw new InvalidOperationException(
                                "initPosition requires numeric x, y, theta and a non-empty mapId");
                        }

                        var initializedMapId = GetActionParameterText(action, "mapId")!;
                        var initializedLastNodeId = GetActionParameterText(action, "lastNodeId");
                        ApplyInitializedPositionInternal(
                            initializedX,
                            initializedY,
                            initializedTheta,
                            ResolveInboundMapId(initializedMapId),
                            initializedLastNodeId);
                        actionState.ActionStatus = "FINISHED";
                        actionState.ResultDescription = "Position initialized";
                        break;

                    case InstantActionType.Pick:
                    case InstantActionType.Lift:
                        _hasLoad = true;
                        var loadIdPick = GetActionParameterString(action, "loadId") ?? $"LOAD_{DateTime.UtcNow.Ticks}";
                        if (GetActionParameterBool(action, "simLoadMismatch"))
                        {
                            loadIdPick = $"MISMATCH_{DateTime.UtcNow.Ticks}";
                        }
                        _currentState.Loads.Add(new Load
                        {
                            LoadId = loadIdPick,
                            LoadType = GetActionParameterString(action, "loadType") ?? "pallet",
                            LoadPosition = GetActionParameterString(action, "loadPosition") ?? "center",
                            Weight = 100.0
                        });
                        _logger.LogInformation("AGV {SerialNumber} picked up load {LoadId} via instant action", _config.SerialNumber, loadIdPick);
                        actionState.ActionStatus = "FINISHED";
                        break;

                    case InstantActionType.Drop:
                    case InstantActionType.Lower:
                        _hasLoad = false;
                        var loadIdDrop = GetActionParameterString(action, "loadId");
                        if (!string.IsNullOrEmpty(loadIdDrop))
                        {
                            _currentState.Loads.RemoveAll(l => l.LoadId == loadIdDrop);
                        }
                        else
                        {
                            _currentState.Loads.Clear();
                        }
                        _logger.LogInformation("AGV {SerialNumber} dropped load(s) via instant action", _config.SerialNumber);
                        actionState.ActionStatus = "FINISHED";
                        break;

                    default:
                        _logger.LogWarning("AGV {SerialNumber} unknown instant action type: {ActionType}",
                            _config.SerialNumber, action.ActionType);
                        actionState.ActionStatus = "FAILED";
                        actionState.ResultDescription = $"Unsupported instant action type '{action.ActionType}'";
                        break;
                }
            }
            catch (Exception ex)
            {
                actionState.ActionStatus = "FAILED";
                actionState.ResultDescription = ex.Message;
                _logger.LogError(ex, "AGV {SerialNumber} failed instant action {ActionType}", _config.SerialNumber, action.ActionType);
            }
        }

        await PublishStateAsync(true);
    }

    private static bool TryGetActionParameterDouble(VdaAction action, string key, out double value)
    {
        value = 0;
        var raw = action.ActionParameters?
            .FirstOrDefault(parameter => string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var parsed = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetDouble(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number) => number,
            string text when double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number) => number,
            IConvertible convertible => TryConvertDouble(convertible),
            _ => null
        };

        if (!parsed.HasValue || !double.IsFinite(parsed.Value))
        {
            return false;
        }

        value = parsed.Value;
        return true;
    }

    private static double? TryConvertDouble(IConvertible value)
    {
        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static string? GetActionParameterText(VdaAction action, string key)
    {
        var raw = action.ActionParameters?
            .FirstOrDefault(parameter => string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element when element.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined => element.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => raw?.ToString()
        };
    }

    private static string NormalizeInstantActionType(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
            return string.Empty;

        var trimmed = actionType.Trim();
        if (trimmed.Equals("pause", StringComparison.OrdinalIgnoreCase)) return InstantActionType.StartPause;
        if (trimmed.Equals("resume", StringComparison.OrdinalIgnoreCase)) return InstantActionType.StopPause;
        if (trimmed.Equals(InstantActionType.CancelOrder, StringComparison.OrdinalIgnoreCase)) return InstantActionType.CancelOrder;
        if (trimmed.Equals(InstantActionType.StartCharging, StringComparison.OrdinalIgnoreCase)) return InstantActionType.StartCharging;
        if (trimmed.Equals(InstantActionType.InitPosition, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals(InstantActionType.InitializePosition, StringComparison.OrdinalIgnoreCase))
            return InstantActionType.InitPosition;
        if (trimmed.Equals(InstantActionType.StartPause, StringComparison.OrdinalIgnoreCase)) return InstantActionType.StartPause;
        if (trimmed.Equals(InstantActionType.StopPause, StringComparison.OrdinalIgnoreCase)) return InstantActionType.StopPause;
        if (trimmed.Equals(InstantActionType.Pick, StringComparison.OrdinalIgnoreCase)) return InstantActionType.Pick;
        if (trimmed.Equals(InstantActionType.Drop, StringComparison.OrdinalIgnoreCase)) return InstantActionType.Drop;
        if (trimmed.Equals(InstantActionType.Lift, StringComparison.OrdinalIgnoreCase)) return InstantActionType.Lift;
        if (trimmed.Equals(InstantActionType.Lower, StringComparison.OrdinalIgnoreCase)) return InstantActionType.Lower;

        return trimmed;
    }

    private void ResetStateForCancelledOrder(ActionState cancelActionState)
    {
        ClearMissionState();
        _currentState.ActionStates.Add(cancelActionState);
    }

    /// <summary>
    /// Forget current mission (orderId, nodes, edges, pick/action states, movement).
    /// Used by cancelOrder and when switching to MANUAL.
    /// </summary>
    private void ClearMissionState()
    {
        _currentOrder = null;
        _currentNodeIndex = -1;
        _isMoving = false;
        _isRotating = false;
        _isReversing = false;
        _arrivalTheta = null;
        _targetTheta = _currentTheta;
        _currentMovementBlocker = null;
        _activeTrajectoryPoints = null;
        _currentTrajectorySegmentIndex = 0;
        _prevNodeId = string.Empty;
        _currentEdgeMaxSpeed = 0.0;
        _pauseLatched = false;

        _currentState.Driving = false;
        _currentState.Paused = false;
        _currentState.NewBaseRequest = false;
        _currentState.NodeStates.Clear();
        _currentState.EdgeStates.Clear();
        _currentState.ActionStates.Clear();
        _currentState.Velocity!.Vx = 0;
        _currentState.Velocity.Vy = 0;
        _currentState.Velocity.Omega = 0;

        _currentVisualization.Velocity!.Vx = 0;
        _currentVisualization.Velocity.Vy = 0;
        _currentVisualization.Velocity.Omega = 0;

        StopVisualizationTimer();
        _actionCts?.Cancel();
        _actionCts = null;
    }

    private bool IsManualOperatingMode() =>
        string.Equals(_currentState.OperatingMode, "MANUAL", StringComparison.OrdinalIgnoreCase);

    private async Task SetOperatingModeInternalAsync(string mode)
    {
        var normalized = NormalizeOperatingMode(mode);
        var previous = _currentState.OperatingMode;

        if (string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("AGV {SerialNumber} operatingMode already {Mode}",
                _config.SerialNumber, normalized);
            return;
        }

        _currentState.OperatingMode = normalized;

        if (string.Equals(normalized, "MANUAL", StringComparison.OrdinalIgnoreCase))
        {
            // Drop remembered mission: nodes, edges, orderId, pick/action states.
            // Wait for ACS to re-send after returning to AUTOMATIC.
            ClearMissionState();
            _logger.LogInformation(
                "AGV {SerialNumber} switched to MANUAL — cleared order/nodes/edges/actions; waiting for ACS to re-send after AUTOMATIC",
                _config.SerialNumber);
        }
        else
        {
            _logger.LogInformation(
                "AGV {SerialNumber} switched to {Mode} — mission still empty until ACS sends a new order",
                _config.SerialNumber, normalized);
        }

        await PublishStateAsync(true);
    }

    private async Task ClearOrderStateInternalAsync()
    {
        _currentState.OrderId = "";
        _currentState.OrderUpdateId = 0;
        _logger.LogInformation("AGV {SerialNumber} manually cleared OrderId and OrderUpdateId", _config.SerialNumber);
        await PublishStateAsync(true);
    }

    private static string NormalizeOperatingMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return "AUTOMATIC";

        var trimmed = mode.Trim();
        if (trimmed.Equals("MANUAL", StringComparison.OrdinalIgnoreCase)) return "MANUAL";
        if (trimmed.Equals("SEMIAUTOMATIC", StringComparison.OrdinalIgnoreCase)) return "SEMIAUTOMATIC";
        if (trimmed.Equals("SERVICE", StringComparison.OrdinalIgnoreCase)) return "SERVICE";
        if (trimmed.Equals("TEACHIN", StringComparison.OrdinalIgnoreCase)) return "TEACHIN";
        return "AUTOMATIC";
    }
}
