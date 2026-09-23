using ACS.Simulator.API.Core.Models;
using Microsoft.Extensions.Logging;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// VirtualAgv — Movement physics: update tick, heading configuration, trajectory following,
/// collision blocking, edge lookup, and angle helpers.
///
///   [P1 #1] Battery drain now uses actual tick displacement (prevX/prevY → nextX/nextY)
///           instead of remaining distance-to-target.
///   [P1 #2] Phase-1 rotation is skipped entirely when following a curved trajectory;
///           the AGV rotates gradually via per-segment tangent updates instead.
/// </summary>
public partial class VirtualAgv
{
    private async Task UpdateMovementAsync()
    {
        // Honor MQTT-latched startPause even if ProcessInstantActions has not drained yet.
        if (_pauseLatched || _currentState.Paused)
        {
            if (_isMoving || _currentState.Driving)
            {
                _isMoving = false;
                _currentState.Driving = false;
                _currentState.Paused = true;
                ZeroStoppedVelocities();
                StopVisualizationTimer();
            }

            return;
        }

        if (!_isMoving)
            return;

        // Safety check: validate _currentOrder and _currentNodeIndex before accessing
        if (_currentOrder == null || _currentOrder.Nodes == null ||
            _currentNodeIndex < 0 || _currentNodeIndex >= _currentOrder.Nodes.Count)
        {
            _isMoving = false;
            _isRotating = false;
            _currentState.Driving = false;
            return;
        }

        // Calculate distance to target
        double dx = _targetX - _currentX;
        double dy = _targetY - _currentY;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double rotationTolerance = 0.05; // ~3 degrees tolerance

        // Arrival check FIRST: when the AGV is already within tolerance of the target node,
        // stay on that node and finish any required in-place rotation before completing it.
        if (distance < _movementConfig.Tolerance)
        {
            _currentX = _targetX;
            _currentY = _targetY;

            var currentNode = _currentOrder.Nodes[_currentNodeIndex];
            _currentState.LastNodeId = currentNode.NodeId;
            _currentState.LastNodeSequenceId = currentNode.SequenceId;
            _currentState.DistanceSinceLastNode = 0;

            _currentState.AgvPosition!.X = _currentX;
            _currentState.AgvPosition.Y = _currentY;
            _currentVisualization.AgvPosition!.X = _currentX;
            _currentVisualization.AgvPosition.Y = _currentY;

            if (TryContinueInPlaceArrivalRotation(currentNode, rotationTolerance))
            {
                return;
            }

            if (_arrivalTheta.HasValue)
            {
                _currentTheta = NormalizeAngle(_arrivalTheta.Value);
            }

            _currentState.AgvPosition.Theta = ToServerTheta(_currentTheta);
            _currentVisualization.AgvPosition.Theta = ToServerTheta(_currentTheta);
            _currentState.Velocity!.Vx = 0;
            _currentState.Velocity.Vy = 0;
            _currentState.Velocity.Omega = 0;
            _currentVisualization.Velocity!.Vx = 0;
            _currentVisualization.Velocity.Vy = 0;
            _currentVisualization.Velocity.Omega = 0;
            _currentState.Driving = false;
            _isMoving = false;
            _isRotating = false;
            _isReversing = false;
            _arrivalTheta = null;
            _targetTheta = _currentTheta;

            // Clear active trajectory state on arrival
            _activeTrajectoryPoints = null;
            _currentTrajectorySegmentIndex = 0;

            StopVisualizationTimer();

            _logger.LogInformation("AGV {SerialNumber} arrived at node {NodeId} ({X}, {Y})",
                _config.SerialNumber, currentNode.NodeId, _currentX, _currentY);

            await PublishStateAsync(true);
            await ExecuteNodeActionsAsync(currentNode);
            await ExecuteNextNodeAsync();
            return;
        }

        // When following a curve, skip Phase-1 rotation entirely: the AGV's heading is
        // managed incrementally per-segment via the tangent updates below, preventing the
        // stop-go oscillation caused by _targetTheta (straight-line heading) fighting the
        // segment tangent reassignment.
        bool isFollowingCurve = _activeTrajectoryPoints != null
            && _currentTrajectorySegmentIndex < _activeTrajectoryPoints.Count - 1;

        // Phase 1: align the body heading before translating (only for straight-line moves).
        double headingTarget = _targetTheta;
        double angleDiff = NormalizeAngle(headingTarget - _currentTheta);

        if (!isFollowingCurve && Math.Abs(angleDiff) > rotationTolerance)
        {
            _isRotating = true;

            // Ensure linear velocity is zero during rotation to prevent sliding
            _currentState.Velocity!.Vx = 0;
            _currentState.Velocity.Vy = 0;
            _currentVisualization.Velocity!.Vx = 0;
            _currentVisualization.Velocity.Vy = 0;

            // Dynamic fleet safety: stop rotation when front area is blocked.
            if (IsMovementBlocked(_currentX, _currentY, true))
                return;

            _currentState.Driving = true;

            double rotationSpeed = 0.05; // rad/100ms ≈ 0.5 rad/s ≈ 28.6 °/s
            double rotationStep = Math.Sign(angleDiff) * Math.Min(Math.Abs(angleDiff), rotationSpeed);

            _currentTheta += rotationStep;
            _currentTheta = NormalizeAngle(_currentTheta);

            _currentState.AgvPosition!.Theta = ToServerTheta(_currentTheta);
            _currentState.Velocity!.Omega = rotationStep * 1000.0 / _movementConfig.MovementUpdateInterval;

            _currentVisualization.AgvPosition!.Theta = ToServerTheta(_currentTheta);
            _currentVisualization.Velocity!.Omega = _currentState.Velocity.Omega;

            if (DateTime.UtcNow.Millisecond < 200)
            {
                _logger.LogDebug("AGV {SerialNumber} rotating... Current: {Current:F2}, Target: {Target:F2}, Diff: {Diff:F2}",
                    _config.SerialNumber, _currentTheta, headingTarget, angleDiff);
            }

            return;
        }

        // Phase 2: translate toward the target while keeping the chosen heading.
        _isRotating = false;
        _currentState.Velocity!.Omega = 0;
        _currentVisualization.Velocity!.Omega = 0;

        double edgeMaxSpeed = _currentEdgeMaxSpeed > 0 ? _currentEdgeMaxSpeed : _movementConfig.Speed;
        double fastSpeed = edgeMaxSpeed;
        double stepDistance = fastSpeed * _movementConfig.MovementUpdateInterval / 1000.0; // metres per tick

        double nextX, nextY;

        if (isFollowingCurve)
        {
            // Follow curved polyline
            var currentSegEnd = _activeTrajectoryPoints![_currentTrajectorySegmentIndex + 1];
            double segDx = currentSegEnd.X - _currentX;
            double segDy = currentSegEnd.Y - _currentY;
            double segDist = Math.Sqrt(segDx * segDx + segDy * segDy);

            if (segDist <= stepDistance)
            {
                // Reached the next segment point — advance to the following segment
                _currentTrajectorySegmentIndex++;
                nextX = currentSegEnd.X;
                nextY = currentSegEnd.Y;

                // Adjust heading towards the new segment direction
                if (_currentTrajectorySegmentIndex < _activeTrajectoryPoints.Count - 1)
                {
                    var nextSeg = _activeTrajectoryPoints[_currentTrajectorySegmentIndex + 1];
                    _currentTheta = NormalizeAngle(Math.Atan2(nextSeg.Y - nextY, nextSeg.X - nextX));
                    if (_isReversing) _currentTheta = NormalizeAngle(_currentTheta + Math.PI);
                }
            }
            else
            {
                double ratio = stepDistance / segDist;
                nextX = _currentX + segDx * ratio;
                nextY = _currentY + segDy * ratio;

                // Keep heading aligned with current segment tangent
                _currentTheta = NormalizeAngle(Math.Atan2(segDy, segDx));
                if (_isReversing) _currentTheta = NormalizeAngle(_currentTheta + Math.PI);
            }

            // Re-eval dx/dy/distance for velocity reporting
            dx = nextX - _currentX;
            dy = nextY - _currentY;
            distance = Math.Sqrt(dx * dx + dy * dy);
        }
        else
        {
            // Standard straight move
            double ratio = Math.Min(stepDistance / distance, 1.0);
            nextX = _currentX + dx * ratio;
            nextY = _currentY + dy * ratio;
        }

        // Dynamic fleet safety: no coordinate overlap and no moving into blocked front area.
        if (IsMovementBlocked(nextX, nextY, false))
            return;

        // Final latch check: MQTT may have set _pauseLatched after the entry guard.
        // Do not commit position/velocity once pause is requested.
        if (_pauseLatched || _currentState.Paused)
        {
            _isMoving = false;
            _currentState.Driving = false;
            _currentState.Paused = true;
            ZeroStoppedVelocities();
            StopVisualizationTimer();
            return;
        }

        _currentState.Driving = true;

        // Previously, distance-to-target was used, which over-drains battery on long edges
        // (distance ≈ full edge length on the first tick, not the tiny step we actually move).
        double prevX = _currentX;
        double prevY = _currentY;

        _currentX = nextX;
        _currentY = nextY;

        // Battery drain: use actual displacement this tick, not remaining distance.
        if (_batteryConfig.Enabled && !_isCharging)
        {
            double actualMoved = Math.Sqrt(
                (_currentX - prevX) * (_currentX - prevX) +
                (_currentY - prevY) * (_currentY - prevY));
            _batteryLevel = Math.Max(0, _batteryLevel - actualMoved * _batteryConfig.DrainRatePerMeter);
            _currentState.BatteryState.BatteryCharge = _batteryLevel;
            _currentState.BatteryState.Reach = CalculateReach(_batteryLevel);
            await CheckBatteryWarningAsync();
        }

        // Update state
        _currentState.AgvPosition!.X = _currentX;
        _currentState.AgvPosition.Y = _currentY;
        _currentState.AgvPosition.Theta = ToServerTheta(_currentTheta);
        _currentState.Velocity!.Vx = distance > 0 ? (dx / distance) * fastSpeed : 0;
        _currentState.Velocity.Vy = distance > 0 ? (dy / distance) * fastSpeed : 0;
        _currentState.DistanceSinceLastNode += stepDistance;

        // Update visualization
        _currentVisualization.AgvPosition!.X = _currentX;
        _currentVisualization.AgvPosition.Y = _currentY;
        _currentVisualization.AgvPosition.Theta = ToServerTheta(_currentTheta);
        _currentVisualization.Velocity!.Vx = _currentState.Velocity.Vx;
        _currentVisualization.Velocity.Vy = _currentState.Velocity.Vy;
    }

    private bool TryContinueInPlaceArrivalRotation(Node currentNode, double rotationTolerance)
    {
        if (!_arrivalTheta.HasValue)
        {
            return false;
        }

        var angleDiff = NormalizeAngle(_arrivalTheta.Value - _currentTheta);
        if (Math.Abs(angleDiff) <= rotationTolerance)
        {
            return false;
        }

        _currentState.LastNodeId = currentNode.NodeId;
        _currentState.LastNodeSequenceId = currentNode.SequenceId;
        _currentState.DistanceSinceLastNode = 0;

        _currentState.Velocity!.Vx = 0;
        _currentState.Velocity.Vy = 0;
        _currentVisualization.Velocity!.Vx = 0;
        _currentVisualization.Velocity.Vy = 0;

        if (IsMovementBlocked(_currentX, _currentY, true))
        {
            return true;
        }

        _targetTheta = NormalizeAngle(_arrivalTheta.Value);
        _isRotating = true;
        _currentState.Driving = true;

        double rotationSpeed = 0.05; // rad/100ms ≈ 0.5 rad/s ≈ 28.6 °/s
        double rotationStep = Math.Sign(angleDiff) * Math.Min(Math.Abs(angleDiff), rotationSpeed);

        _currentTheta = NormalizeAngle(_currentTheta + rotationStep);

        _currentState.AgvPosition!.Theta = ToServerTheta(_currentTheta);
        _currentState.Velocity!.Omega = rotationStep * 1000.0 / _movementConfig.MovementUpdateInterval;

        _currentVisualization.AgvPosition!.Theta = ToServerTheta(_currentTheta);
        _currentVisualization.Velocity!.Omega = _currentState.Velocity.Omega;

        return true;
    }

    private void ConfigureMovementPlan(Node node, Edge? orderEdge)
    {
        var dx = _targetX - _currentX;
        var dy = _targetY - _currentY;
        var travelTheta = NormalizeAngle(Math.Atan2(dy, dx));
        var nodeTheta = node.NodePosition?.Theta.HasValue == true
            ? NormalizeAngle(node.NodePosition!.Theta!.Value)
            : (double?)null;
        var edgeTheta = orderEdge?.Orientation.HasValue == true
            ? NormalizeAngle(orderEdge.Orientation!.Value)
            : (double?)null;
        var edgeOrientationType = orderEdge?.OrientationType?.Trim();

        _arrivalTheta = nodeTheta;
        if (_arrivalTheta == null &&
            edgeTheta.HasValue &&
            string.Equals(edgeOrientationType, "GLOBAL", StringComparison.OrdinalIgnoreCase))
        {
            _arrivalTheta = edgeTheta.Value;
        }

        if (edgeTheta.HasValue &&
            string.Equals(edgeOrientationType, "GLOBAL", StringComparison.OrdinalIgnoreCase) &&
            TryResolveHeadingPlan(edgeTheta.Value, travelTheta, out var reverseFromEdge))
        {
            _targetTheta = edgeTheta.Value;
            _isReversing = reverseFromEdge;
            return;
        }

        if (nodeTheta.HasValue && TryResolveHeadingPlan(nodeTheta.Value, travelTheta, out var reverseFromNode))
        {
            _targetTheta = nodeTheta.Value;
            _isReversing = reverseFromNode;
            return;
        }

        if (edgeTheta.HasValue && TryResolveHeadingPlan(edgeTheta.Value, travelTheta, out var reverseFromOrientation))
        {
            _targetTheta = edgeTheta.Value;
            _isReversing = reverseFromOrientation;
            return;
        }

        var forwardHeading = travelTheta;
        var reverseHeading = NormalizeAngle(travelTheta + Math.PI);
        var forwardRotation = Math.Abs(NormalizeAngle(forwardHeading - _currentTheta));
        var reverseRotation = Math.Abs(NormalizeAngle(reverseHeading - _currentTheta));

        if (reverseRotation + 0.05 < forwardRotation)
        {
            _targetTheta = reverseHeading;
            _isReversing = true;
            return;
        }

        _targetTheta = forwardHeading;
        _isReversing = false;
    }

    private bool TryResolveHeadingPlan(double desiredHeading, double travelTheta, out bool reverse)
    {
        var normalizedDesiredHeading = NormalizeAngle(desiredHeading);
        var reverseHeading = NormalizeAngle(travelTheta + Math.PI);

        if (Math.Abs(NormalizeAngle(normalizedDesiredHeading - travelTheta)) <= MovementHeadingAlignmentTolerance)
        {
            reverse = false;
            return true;
        }

        if (Math.Abs(NormalizeAngle(normalizedDesiredHeading - reverseHeading)) <= MovementHeadingAlignmentTolerance)
        {
            reverse = true;
            return true;
        }

        reverse = false;
        return false;
    }

    private bool IsMovementBlocked(double nextX, double nextY, bool isRotating)
    {
        if (_movementBlockResolver == null)
            return false;

        var blockingAgv = _movementBlockResolver(
            _config.SerialNumber,
            _currentX,
            _currentY,
            nextX,
            nextY,
            _currentTheta,
            isRotating,
            _currentMapId);

        if (string.IsNullOrEmpty(blockingAgv))
        {
            if (!string.IsNullOrEmpty(_currentMovementBlocker))
            {
                _logger.LogInformation("AGV {SerialNumber} path clear, resuming movement", _config.SerialNumber);
                _currentMovementBlocker = null;
            }
            return false;
        }

        _currentState.Driving = false;
        _currentState.Velocity!.Vx = 0;
        _currentState.Velocity.Vy = 0;
        _currentState.Velocity.Omega = 0;
        _currentVisualization.Velocity!.Vx = 0;
        _currentVisualization.Velocity.Vy = 0;
        _currentVisualization.Velocity.Omega = 0;

        var now = DateTime.UtcNow;
        if (_currentMovementBlocker != blockingAgv || now - _lastBlockLogAt > TimeSpan.FromSeconds(2))
        {
            _logger.LogWarning("AGV {SerialNumber} movement blocked by AGV {BlockingAgv}",
                _config.SerialNumber, blockingAgv);
            _lastBlockLogAt = now;
        }

        _currentMovementBlocker = blockingAgv;
        return true;
    }

    // ── Edge / Speed helpers ─────────────────────────────────────────────

    // ── Angle helpers ────────────────────────────────────────────────────

    // Helper method to normalize angle to [-pi, pi].
    private static double NormalizeAngle(double angle)
    {
        while (angle > Math.PI) angle -= 2 * Math.PI;
        while (angle < -Math.PI) angle += 2 * Math.PI;
        return angle;
    }

    // VDA5050 expects theta in radians within [-pi, pi].
    private double ToServerTheta(double radians) => NormalizeAngle(radians);
}
