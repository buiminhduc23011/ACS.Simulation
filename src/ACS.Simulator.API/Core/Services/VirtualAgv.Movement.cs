using ACS.Simulator.API.Core.Models;
using ACS.Simulator.API.Core.Utils;
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
        // Honor MQTT-latched startPause, paused state, or any active error
        if (_pauseLatched || _currentState.Paused || _currentState.Errors.Count > 0)
        {
            if (_isMoving || _currentState.Driving)
            {
                _isMoving = false;
                _isRotating = false;
                _currentSpeed = 0.0;
                _currentState.Driving = false;
                if (_pauseLatched)
                {
                    _currentState.Paused = true;
                }
                ZeroStoppedVelocities();
                StopVisualizationTimer();
            }
            else
            {
                _currentSpeed = 0.0;
            }

            return;
        }

        if (!_isMoving)
        {
            _currentSpeed = 0.0;
            return;
        }

        // Safety check: validate _currentOrder and _currentNodeIndex before accessing
        if (_currentOrder == null || _currentOrder.Nodes == null ||
            _currentNodeIndex < 0 || _currentNodeIndex >= _currentOrder.Nodes.Count)
        {
            _isMoving = false;
            _isRotating = false;
            _currentSpeed = 0.0;
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
        // Node trung gian (không action, không yêu cầu xoay tại chỗ, hướng đi tiếp gần thẳng)
        // được bay qua — giữ nguyên _currentSpeed để không tăng/giảm tốc giữa các node.
        if (distance < _movementConfig.Tolerance)
        {
            _currentX = _targetX;
            _currentY = _targetY;

            var currentNode = _currentOrder.Nodes[_currentNodeIndex];
            // Xe line từ: tới node phải đọc được tag RFID; thiếu tag → mất vị trí, dừng và báo lỗi.
            var reportedNodeId = ToReportedNodeId(currentNode.NodeId);
            if (_config.IsLineGuided && reportedNodeId is null)
            {
                _isMoving = false;
                _isRotating = false;
                _isReversing = false;
                _arrivalTheta = null;
                _currentState.Driving = false;
                ZeroStoppedVelocities();
                StopVisualizationTimer();
                await PublishStateAsync(true);
                return;
            }

            // Bay qua node trung gian: không dừng, không zero tốc độ, không publish stop.
            if (TrySetupFlyThrough(currentNode, reportedNodeId, rotationTolerance))
            {
                return;
            }
            _currentState.LastNodeId = reportedNodeId ?? string.Empty;
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
            _currentSpeed = 0.0;
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
        // Góc lệch nhỏ (rẽ nhẹ giữa 2 node thẳng hàng) thì vừa chạy vừa nắn lái,
        // không dừng xoay tại chỗ gây giật. Chỉ xoay tại chỗ khi góc lớn hoặc sắp phải dừng.
        double headingTarget = _targetTheta;
        double angleDiff = NormalizeAngle(headingTarget - _currentTheta);

        // Nhìn trước tới điểm dừng thật sự (node có action / node cuối / rẽ gấp),
        // không phanh về 0 ở node trung gian bay qua.
        double distanceToStop = ComputeDistanceToNextStop(distance);

        const double steerWhileMovingLimit = 0.6; // ~34°: nhỏ hơn thì vừa chạy vừa nắn
        bool needInPlaceRotation = !isFollowingCurve
            && Math.Abs(angleDiff) > rotationTolerance
            && (Math.Abs(angleDiff) > steerWhileMovingLimit || distanceToStop < 0.3);

        if (needInPlaceRotation)
        {
            _isRotating = true;
            _currentSpeed = 0.0;

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
        // Tốc độ tuân theo hình thang: tăng theo Acceleration, kẹp trần MaxSpeed
        // (edge cho cao hơn vẫn bị kẹp), phanh theo Deceleration để dừng đúng ĐIỂM DỪNG
        // (node cuối / node có action / rẽ gấp), không phanh ở node bay qua.
        _isRotating = false;

        // Vừa chạy vừa nắn lái về heading mục tiêu khi lệch nhỏ.
        if (!isFollowingCurve && Math.Abs(angleDiff) > rotationTolerance)
        {
            double steerRate = 0.05; // rad/tick, cùng tốc độ xoay tại chỗ
            double steerStep = Math.Sign(angleDiff) * Math.Min(Math.Abs(angleDiff), steerRate);
            _currentTheta = NormalizeAngle(_currentTheta + steerStep);
            _currentState.Velocity!.Omega = steerStep * 1000.0 / _movementConfig.MovementUpdateInterval;
            _currentVisualization.Velocity!.Omega = _currentState.Velocity.Omega;
        }
        else
        {
            _currentState.Velocity!.Omega = 0;
            _currentVisualization.Velocity!.Omega = 0;
        }

        double dt = _movementConfig.MovementUpdateInterval / 1000.0;
        double edgeLimit = _currentEdgeMaxSpeed > 0 ? _currentEdgeMaxSpeed : _movementConfig.MaxSpeed;
        double cruiseSpeed = Math.Min(edgeLimit, _movementConfig.MaxSpeed);
        cruiseSpeed = Math.Max(0.0, cruiseSpeed);
        // Phanh để kịp dừng tại ĐIỂM DỪNG: v = sqrt(2*a*d_stop).
        // Dùng max(distance, ...) để tick cuối vẫn về 0 êm, không vọt lố.
        double brakeSpeed = _movementConfig.Deceleration > 0 && distanceToStop > 0
            ? Math.Sqrt(2.0 * _movementConfig.Deceleration * distanceToStop)
            : cruiseSpeed;
        double targetSpeed = Math.Min(cruiseSpeed, brakeSpeed);
        if (_currentSpeed < targetSpeed)
        {
            double accel = _movementConfig.Acceleration > 0 ? _movementConfig.Acceleration : double.MaxValue;
            _currentSpeed = Math.Min(targetSpeed, _currentSpeed + accel * dt);
        }
        else if (_currentSpeed > targetSpeed)
        {
            double decel = _movementConfig.Deceleration > 0 ? _movementConfig.Deceleration : double.MaxValue;
            _currentSpeed = Math.Max(targetSpeed, _currentSpeed - decel * dt);
        }
        double fastSpeed = _currentSpeed;
        double stepDistance = fastSpeed * dt; // metres per tick

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
            _currentSpeed = 0.0;
            _currentState.Driving = false;
            _currentState.Paused = true;
            ZeroStoppedVelocities();
            StopVisualizationTimer();
            return;
        }

        _currentState.Driving = true;

        _currentX = nextX;
        _currentY = nextY;

        // Battery drain: xả theo thời gian chạy liên tục (ContinuousOperatingHours),
        // không theo quãng đường — tốc độ nào thì hết pin sau đúng từng ấy giờ chạy.
        if (_batteryConfig.Enabled && !_isCharging)
        {
            double elapsedMinutes = _movementConfig.MovementUpdateInterval / 60000.0;
            // Floor applies only when the level is still at/above it — a user-set level already
            // below AutoDrainFloor stays there and keeps draining instead of jumping back up.
            double drainFloor = _batteryLevel >= _batteryConfig.AutoDrainFloor
                ? _batteryConfig.AutoDrainFloor
                : 0.0;
            _batteryLevel = Math.Max(drainFloor, _batteryLevel - elapsedMinutes * _batteryConfig.DrainRateDrivingPerMinute);
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

        _currentState.LastNodeId = ToReportedNodeId(currentNode.NodeId) ?? string.Empty;
        _currentState.LastNodeSequenceId = currentNode.SequenceId;
        _currentState.DistanceSinceLastNode = 0;
        _currentSpeed = 0.0;

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
        _currentSpeed = 0.0;
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

    /// <summary>
    /// Bay qua node trung gian: trả về true nếu đã chuyển mục tiêu sang node kế tiếp
    /// mà KHÔNG dừng (giữ nguyên _currentSpeed). Điều kiện bay qua:
    /// - còn node tiếp theo, đã released, có vị trí
    /// - node hiện tại không có action, không yêu cầu xoay tại chỗ (arrivalTheta)
    /// - không rẽ gấp (góc giữa hướng vào và hướng ra &lt; ~34°), không đảo chiều tiến/lùi
    /// - không pause/error
    /// </summary>
    private bool TrySetupFlyThrough(Node currentNode, string? reportedNodeId, double rotationTolerance)
    {
        if (_currentOrder == null) return false;
        if (_pauseLatched || _currentState.Paused || _currentState.Errors.Count > 0) return false;
        if (currentNode.Actions is { Count: > 0 }) return false;

        int nextIndex = _currentNodeIndex + 1;
        if (nextIndex < 0 || nextIndex >= _currentOrder.Nodes.Count) return false;
        var nextNode = _currentOrder.Nodes[nextIndex];
        if (!nextNode.Released || nextNode.NodePosition == null) return false;
        if (nextNode.Actions is { Count: > 0 })
        {
            // Node kế tiếp có action → vẫn bay qua node hiện tại được, nhưng điểm dừng
            // là node kế tiếp. Bay qua ở đây vẫn đúng; action sẽ chạy khi tới nơi.
            // (Không return false ở đây.)
        }

        // Hướng vào (vị trí hiện tại == node hiện tại → dùng đoạn vừa đi qua để ước lượng).
        // Vì đã snap _currentX/_currentY về node, hướng vào = hướng từ prev node tới đây.
        var prevNode = _currentNodeIndex > 0 ? _currentOrder.Nodes[_currentNodeIndex - 1] : null;
        double inX = prevNode?.NodePosition?.X ?? (_currentX - Math.Cos(_currentTheta) * 0.001);
        double inY = prevNode?.NodePosition?.Y ?? (_currentY - Math.Sin(_currentTheta) * 0.001);
        double inDir = Math.Atan2(_currentY - inY, _currentX - inX);
        double outDir = Math.Atan2(nextNode.NodePosition.Y - _currentY, nextNode.NodePosition.X - _currentX);
        double corner = Math.Abs(NormalizeAngle(outDir - inDir));
        const double maxThroughCorner = 0.6; // ~34°
        if (corner > maxThroughCorner) return false;

        // Node yêu cầu heading đến khác hướng đi tiếp → phải dừng để xoay.
        if (_arrivalTheta.HasValue)
        {
            double needAtNode = Math.Abs(NormalizeAngle(_arrivalTheta.Value - outDir));
            double needAtNodeRev = Math.Abs(NormalizeAngle(_arrivalTheta.Value - NormalizeAngle(outDir + Math.PI)));
            if (Math.Min(needAtNode, needAtNodeRev) > rotationTolerance * 2) return false;
        }

        // Đảo chiều tiến/lùi giữa 2 chặng → phải dừng.
        bool wasReversing = _isReversing;
        // Ước lượng reversing cho chặng tiếp theo bằng cùng luật ConfigureMovementPlan.
        var nextEdge = FindOrderEdge(currentNode, nextNode);
        double savedTarget = _targetTheta;
        bool savedRev = _isReversing;
        double? savedArrival = _arrivalTheta;
        ConfigureMovementPlan(nextNode, nextEdge);
        bool wouldReverse = _isReversing;
        // Khôi phục — bên dưới sẽ setup lại đầy đủ.
        _targetTheta = savedTarget;
        _isReversing = savedRev;
        _arrivalTheta = savedArrival;
        if (wouldReverse != wasReversing) return false;

        // ── Đủ điều kiện bay qua: cập nhật bookkeeping như đã tới node, rồi setup chặng tiếp ──
        _currentState.LastNodeId = reportedNodeId ?? string.Empty;
        _currentState.LastNodeSequenceId = currentNode.SequenceId;
        _currentState.DistanceSinceLastNode = 0;
        _currentState.AgvPosition!.X = _currentX;
        _currentState.AgvPosition.Y = _currentY;
        _currentVisualization.AgvPosition!.X = _currentX;
        _currentVisualization.AgvPosition.Y = _currentY;

        _currentNodeIndex = nextIndex;
        _prevNodeId = currentNode.NodeId ?? string.Empty;

        _targetX = nextNode.NodePosition.X;
        _targetY = nextNode.NodePosition.Y;

        if (nextEdge?.Trajectory?.ControlPoints?.Count > 0)
        {
            var cps = nextEdge.Trajectory.ControlPoints;
            int degree = nextEdge.Trajectory.Degree > 0 ? nextEdge.Trajectory.Degree : (cps.Count >= 4 ? 3 : 2);
            List<ControlPoint> handles;
            if (degree == 2 && cps.Count == 3)
                handles = new List<ControlPoint> { cps[1] };
            else if (degree == 3 && cps.Count == 4)
                handles = new List<ControlPoint> { cps[1], cps[2] };
            else if (cps.Count > 2)
                handles = cps.Skip(1).Take(cps.Count - 2).ToList();
            else
                handles = cps;
            _activeTrajectoryPoints = CurveUtils.GenerateCurvePoints(
                _currentX, _currentY, _targetX, _targetY, handles, 50);
            _currentTrajectorySegmentIndex = 0;
        }
        else
        {
            _activeTrajectoryPoints = null;
            _currentTrajectorySegmentIndex = 0;
        }

        ConfigureMovementPlan(nextNode, nextEdge);

        var previousMapId = _currentMapId;
        _currentMapId = nextNode.NodePosition.MapId;
        if (_currentState.AgvPosition != null) _currentState.AgvPosition.MapId = _currentMapId;
        if (_currentVisualization?.AgvPosition != null) _currentVisualization.AgvPosition.MapId = _currentMapId;
        if (!string.IsNullOrEmpty(previousMapId) && previousMapId != _currentMapId)
        {
            _logger.LogInformation("AGV {Serial}: Map transition {PrevMap} → {NewMap} at node {NodeId}",
                _config.SerialNumber, previousMapId, _currentMapId, nextNode.NodeId);
        }

        _currentEdgeMaxSpeed = nextEdge?.MaxSpeed ?? 0.0;
        // KHÔNG reset _currentSpeed — giữ tốc độ bay qua.
        _isMoving = true;
        _currentState.Driving = true;

        _logger.LogInformation("AGV {SerialNumber} fly-through node {NodeId} → {NextNodeId} (góc {Corner:F1}°)",
            _config.SerialNumber, currentNode.NodeId, nextNode.NodeId, corner * 180.0 / Math.PI);
        return true;
    }

    /// <summary>
    /// Khoảng cách từ vị trí hiện tại tới ĐIỂM DỪNG kế tiếp (node cuối / node có action /
    /// rẽ gấp / đảo chiều / yêu cầu xoay tại chỗ). Node bay qua được cộng dồn vào,
    /// nên xe không phanh về 0 ở mỗi node trung gian.
    /// </summary>
    private double ComputeDistanceToNextStop(double distanceToCurrentTarget)
    {
        if (_currentOrder == null) return distanceToCurrentTarget;
        double total = Math.Max(0.0, distanceToCurrentTarget);
        int idx = _currentNodeIndex;
        if (idx < 0 || idx >= _currentOrder.Nodes.Count) return total;

        var cur = _currentOrder.Nodes[idx];
        // Node hiện tại đang tới mà đã là điểm dừng → chỉ phanh tới đó.
        if (IsStopNode(idx, cur)) return total;

        // Cộng dồn các chặng bay qua liên tiếp cho tới điểm dừng.
        double fromX = _targetX;
        double fromY = _targetY;
        int j = idx;
        while (true)
        {
            int nxt = j + 1;
            if (nxt < 0 || nxt >= _currentOrder.Nodes.Count) break;
            var nj = _currentOrder.Nodes[nxt];
            if (nj.NodePosition == null || !nj.Released) break;
            double seg = Math.Sqrt(
                (nj.NodePosition.X - fromX) * (nj.NodePosition.X - fromX) +
                (nj.NodePosition.Y - fromY) * (nj.NodePosition.Y - fromY));
            total += seg;
            if (IsStopNode(nxt, nj)) break;
            fromX = nj.NodePosition.X;
            fromY = nj.NodePosition.Y;
            j = nxt;
            // Chặn vòng lặp vô hạn với order rất dài — nhìn trước tối đa 20 node là đủ mượt.
            if (nxt - idx > 20) break;
        }
        return total;
    }

    private bool IsStopNode(int nodeIndex, Node node)
    {
        if (_currentOrder == null) return true;
        if (nodeIndex >= _currentOrder.Nodes.Count - 1) return true; // node cuối
        if (node.Actions is { Count: > 0 }) return true;
        int nxt = nodeIndex + 1;
        if (nxt < 0 || nxt >= _currentOrder.Nodes.Count) return true;
        var nextNode = _currentOrder.Nodes[nxt];
        if (!nextNode.Released || nextNode.NodePosition == null) return true;
        // Rẽ gấp → dừng để xoay tại chỗ an toàn.
        double curX = node.NodePosition?.X ?? _targetX;
        double curY = node.NodePosition?.Y ?? _targetY;
        var prevNode = nodeIndex > 0 ? _currentOrder.Nodes[nodeIndex - 1] : null;
        double inX = prevNode?.NodePosition?.X ?? (curX - Math.Cos(_currentTheta) * 1.0);
        double inY = prevNode?.NodePosition?.Y ?? (curY - Math.Sin(_currentTheta) * 1.0);
        double inDir = Math.Atan2(curY - inY, curX - inX);
        double outDir = Math.Atan2(nextNode.NodePosition.Y - curY, nextNode.NodePosition.X - curX);
        if (Math.Abs(NormalizeAngle(outDir - inDir)) > 0.6) return true;
        // Node yêu cầu heading đến (node.Theta) lệch hướng đi tiếp → phải dừng xoay.
        // Với node đang tới, _arrivalTheta đã được tính từ node.Theta/edge orientation;
        // với node nhìn trước, kiểm tra trực tiếp node.Theta.
        double? requiredTheta = nodeIndex == _currentNodeIndex
            ? _arrivalTheta
            : node.NodePosition?.Theta;
        if (requiredTheta.HasValue)
        {
            double need = Math.Abs(NormalizeAngle(requiredTheta.Value - outDir));
            double needRev = Math.Abs(NormalizeAngle(requiredTheta.Value - NormalizeAngle(outDir + Math.PI)));
            if (Math.Min(need, needRev) > 0.1) return true;
        }
        return false;
    }

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
