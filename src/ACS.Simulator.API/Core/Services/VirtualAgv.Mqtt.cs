using ACS.Simulator.API.Core.Models;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System.Text;
using System.Text.Json;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// VirtualAgv — MQTT connectivity: connect, subscribe, receive, publish, disconnect, network chaos.
/// </summary>
public partial class VirtualAgv
{
    public async Task ConnectAsync()
    {
        try
        {
            // Prepare Last Will message (CONNECTIONBROKEN) - published by broker if client disconnects ungracefully
            var lastWillPayload = JsonSerializer.Serialize(new Vda5050Connection
            {
                HeaderId = 0,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Version = "2.0.0",
                Manufacturer = _config.Manufacturer,
                SerialNumber = _config.SerialNumber,
                ConnectionState = VDA5050ConnectionState.ConnectionBroken
            });

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_mqttConfig.Address, _mqttConfig.Port)
                .WithClientId($"AGV_{_config.MacAddress.Replace(":", "")}")
                .WithCleanSession()
                .WithWillTopic(ConnectionTopic)
                .WithWillPayload(Encoding.UTF8.GetBytes(lastWillPayload))
                .WithWillRetain(true)
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            AttachMqttHandler();

            await _mqttClient.ConnectAsync(options);
            _logger.LogInformation("AGV {SerialNumber} connected to MQTT broker at {Broker}:{Port}",
                _config.SerialNumber, _mqttConfig.Address, _mqttConfig.Port);

            // Subscribe to order topic with explicit QoS for reliable delivery.
            await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                .WithTopic(OrderTopic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
                .Build());
            _logger.LogInformation("AGV {SerialNumber} subscribed to order topic: {Topic}", _config.SerialNumber, OrderTopic);

            // Subscribe to instantActions topic with explicit QoS.
            await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                .WithTopic(InstantActionsTopic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
                .Build());
            _logger.LogInformation("AGV {SerialNumber} subscribed to instantActions topic: {Topic}", _config.SerialNumber, InstantActionsTopic);

            // Publish ONLINE connection state (retain=true)
            await PublishConnectionStateAsync(VDA5050ConnectionState.Online);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect AGV {SerialNumber} to MQTT broker", _config.SerialNumber);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_mqttClient.IsConnected)
        {
            // Publish OFFLINE gracefully before disconnecting
            await PublishConnectionStateAsync(VDA5050ConnectionState.Offline);
            await _mqttClient.DisconnectAsync();
            _logger.LogInformation("AGV {SerialNumber} disconnected from MQTT broker", _config.SerialNumber);
        }

        DetachMqttHandler();
    }

    private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

            if (topic == OrderTopic)
            {
                _logger.LogInformation("AGV {SerialNumber} received order on topic {Topic}",
                    _config.SerialNumber, topic);
                var order = JsonSerializer.Deserialize<Vda5050Order>(payload);
                if (order != null)
                {
                    var ignoredManual = IsManualOperatingMode();
                    var safetyActionSummary = string.Join(", ", order.Nodes
                        .SelectMany(node => node.Actions ?? [])
                        .Where(action => string.Equals(action.ActionType, AcsNodeActionType.DisableSafetyFront, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(action.ActionType, AcsNodeActionType.DisableSafetyRear, StringComparison.OrdinalIgnoreCase))
                        .Select(action => action.ActionType));
                    RecordInboundMqttMessage(
                        "order",
                        topic,
                        payload,
                        accepted: !ignoredManual,
                        note: ignoredManual
                            ? $"Ignored: operatingMode=MANUAL (orderId={order.OrderId}, updateId={order.OrderUpdateId})"
                            : $"orderId={order.OrderId}, updateId={order.OrderUpdateId}, nodes={order.Nodes?.Count ?? 0}, edges={order.Edges?.Count ?? 0}" +
                              (string.IsNullOrWhiteSpace(safetyActionSummary)
                                  ? string.Empty
                                  : $", safety actions={safetyActionSummary}"));

                    if (ignoredManual)
                    {
                        return;
                    }

                    // A base order replacement must interrupt a running action before the actor
                    // reaches the queued command. Same-order updates are extensions; leave their
                    // token live so the actor can merge the update and execute later actions.
                    CancelActionForIncomingOrderIfReplacing(order);
                    await _commandChannel.Writer.WriteAsync(new ProcessOrderCmd(order));
                }
                else
                {
                    RecordInboundMqttMessage("order", topic, payload, accepted: false, note: "Deserialize returned null");
                }
            }
            else if (topic == InstantActionsTopic)
            {
                _logger.LogInformation("AGV {SerialNumber} received instantActions on topic {Topic}",
                    _config.SerialNumber, topic);
                try
                {
                    var instantActions = JsonSerializer.Deserialize<Vda5050InstantActions>(payload);
                    if (instantActions?.Actions is not { Count: > 0 })
                    {
                        RecordInboundMqttMessage(
                            "instantActions",
                            topic,
                            payload,
                            accepted: false,
                            note: "Rejected: expected a non-empty actions array");
                        _logger.LogError(
                            "AGV {SerialNumber} rejected instantActions payload on topic {Topic}: expected a non-empty actions array.",
                            _config.SerialNumber,
                            topic);
                        return;
                    }

                    var actionSummary = string.Join(", ",
                        instantActions.Actions.Select(a => $"{a.ActionType}({a.ActionId})"));
                    RecordInboundMqttMessage(
                        "instantActions",
                        topic,
                        payload,
                        accepted: true,
                        note: actionSummary);

                    // Only cancelOrder (and E-stop elsewhere) may cancel a running pick/drop action.
                    // startPause must stop movement only — cancelling the action token turns pick into
                    // FAILED ("Cancelled by new order") and aborts the workflow before drop.
                    var hasCancelOrder = false;
                    var hasStartPause = false;
                    var hasStopPause = false;
                    foreach (var action in instantActions.Actions)
                    {
                        if (action.ActionType == null)
                        {
                            continue;
                        }

                        if (action.ActionType.Equals("cancelOrder", StringComparison.OrdinalIgnoreCase))
                        {
                            hasCancelOrder = true;
                        }
                        else if (action.ActionType.Equals("startPause", StringComparison.OrdinalIgnoreCase) ||
                                 action.ActionType.Equals("pause", StringComparison.OrdinalIgnoreCase))
                        {
                            hasStartPause = true;
                        }
                        else if (action.ActionType.Equals("stopPause", StringComparison.OrdinalIgnoreCase) ||
                                 action.ActionType.Equals("resume", StringComparison.OrdinalIgnoreCase))
                        {
                            hasStopPause = true;
                        }
                    }

                    if (hasCancelOrder)
                    {
                        _actionCts?.Cancel();
                    }

                    // Atomic pause request only — actor path owns movement/state/velocity mutations.
                    // Movement ticks honor _pauseLatched and re-check before committing a step.
                    if (hasStartPause)
                    {
                        _pauseLatched = true;
                    }
                    else if (hasStopPause)
                    {
                        _pauseLatched = false;
                    }

                    await _commandChannel.Writer.WriteAsync(new ProcessInstantActionsCmd(instantActions));
                }
                catch (JsonException ex) when (PayloadUsesLegacyInstantActionsProperty(payload))
                {
                    RecordInboundMqttMessage(
                        "instantActions",
                        topic,
                        payload,
                        accepted: false,
                        note: "Rejected: legacy instantActions body property; expected actions");
                    _logger.LogError(
                        ex,
                        "AGV {SerialNumber} rejected legacy instantActions body property on topic {Topic}; expected actions.",
                        _config.SerialNumber,
                        topic);
                }
                catch (JsonException ex)
                {
                    RecordInboundMqttMessage(
                        "instantActions",
                        topic,
                        payload,
                        accepted: false,
                        note: $"Rejected: malformed JSON ({ex.Message})");
                    _logger.LogError(
                        ex,
                        "AGV {SerialNumber} rejected malformed instantActions payload on topic {Topic}.",
                        _config.SerialNumber,
                        topic);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message for AGV {SerialNumber}", _config.SerialNumber);
        }
    }

    private void CancelActionForIncomingOrderIfReplacing(Vda5050Order incomingOrder)
    {
        var activeOrder = _currentOrder;
        var isNewBaseOrder = activeOrder == null ||
            (!string.Equals(activeOrder.OrderId, incomingOrder.OrderId, StringComparison.OrdinalIgnoreCase) &&
             incomingOrder.OrderUpdateId == 0);

        if (isNewBaseOrder)
        {
            _actionCts?.Cancel();
        }
    }

    private static bool PayloadUsesLegacyInstantActionsProperty(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("instantActions", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task PublishConnectionStateAsync(string state)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new Vda5050Connection
            {
                HeaderId = GetNextHeaderId(),
                Timestamp = DateTime.UtcNow.ToString("o"),
                Version = "2.0.0",
                Manufacturer = _config.Manufacturer,
                SerialNumber = _config.SerialNumber,
                ConnectionState = state
            });

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(ConnectionTopic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithRetainFlag(true)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message);
            _logger.LogInformation("AGV {SerialNumber} published connection state: {State}", _config.SerialNumber, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish connection state for AGV {SerialNumber}", _config.SerialNumber);
        }
    }

    // ── Network Chaos ──────────────────────────────────────────

    /// <summary>Simulate MQTT latency. Pass (0,0) to disable.</summary>
    public void SetChaosLatency(int minMs, int maxMs)
        => _commandChannel.Writer.TryWrite(new SetChaosCmd(minMs, maxMs, _chaosPacketLossPercent));

    /// <summary>Simulate packet loss. 0 = off, 100 = drop all state publishes.</summary>
    public void SetPacketLoss(int percent)
        => _commandChannel.Writer.TryWrite(new SetChaosCmd(_chaosMinLatencyMs, _chaosMaxLatencyMs, percent));

    /// <summary>Force-disconnect from MQTT broker, automatically reconnect after durationMs.</summary>
    public async Task TriggerDisconnectAsync(int durationMs)
    {
        if (!_mqttClient.IsConnected) return;

        _logger.LogWarning("AGV {SerialNumber} chaos disconnect triggered ({Duration}ms)", _config.SerialNumber, durationMs);

        _chaosReconnectCts?.Cancel();
        _chaosReconnectCts?.Dispose();
        _chaosReconnectCts = new CancellationTokenSource();
        var ct = _chaosReconnectCts.Token;

        await _mqttClient.DisconnectAsync();
        DetachMqttHandler();

        _chaosReconnectTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(durationMs, ct);
                if (ct.IsCancellationRequested || _disposed || !_isRunning)
                {
                    return;
                }

                await ConnectAsync();
                _logger.LogInformation("AGV {SerialNumber} chaos reconnected after {Duration}ms", _config.SerialNumber, durationMs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AGV {SerialNumber} failed to reconnect after chaos disconnect", _config.SerialNumber);
            }
        });
    }

    private void AttachMqttHandler()
    {
        if (_mqttHandlerAttached)
        {
            return;
        }

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;
        _mqttHandlerAttached = true;
    }

    private void DetachMqttHandler()
    {
        if (!_mqttHandlerAttached)
        {
            return;
        }

        _mqttClient.ApplicationMessageReceivedAsync -= OnMessageReceived;
        _mqttHandlerAttached = false;
    }
}
