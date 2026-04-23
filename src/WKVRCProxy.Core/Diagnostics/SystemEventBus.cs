using System;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Diagnostics;

public class SystemEventBus
{
    public event Action<SystemEvent>? OnEvent;

    public void Publish(SystemEvent evt)
    {
        OnEvent?.Invoke(evt);
    }

    public void PublishLog(LogEntry entry, string? correlationId = null)
    {
        Publish(new SystemEvent
        {
            Type = SystemEventType.Log,
            Timestamp = entry.Timestamp,
            SourceModule = entry.Source,
            CorrelationId = correlationId,
            Payload = entry
        });
    }

    public void PublishStatus(string source, string message, object? stats, string? correlationId = null)
    {
        Publish(new SystemEvent
        {
            Type = SystemEventType.Status,
            Timestamp = DateTime.Now,
            SourceModule = source,
            CorrelationId = correlationId,
            Payload = new { message = message, stats = stats }
        });
    }

    public void PublishRelay(RelayEvent evt, string? correlationId = null)
    {
        Publish(new SystemEvent
        {
            Type = SystemEventType.Relay,
            Timestamp = DateTime.Now,
            SourceModule = "RelayServer",
            CorrelationId = correlationId,
            Payload = evt
        });
    }

    public void PublishPrompt(string source, string promptType, object data)
    {
        Publish(new SystemEvent
        {
            Type = SystemEventType.Prompt,
            Timestamp = DateTime.Now,
            SourceModule = source,
            Payload = new { type = promptType, data = data }
        });
    }

    public void PublishHealth(ModuleHealthReport report)
    {
        Publish(new SystemEvent
        {
            Type = SystemEventType.Health,
            Timestamp = DateTime.Now,
            SourceModule = report.ModuleName,
            Payload = report
        });
    }

    public void PublishError(string source, ErrorContext error, string? correlationId = null)
    {
        Publish(new SystemEvent
        {
            Type = SystemEventType.Error,
            Timestamp = DateTime.Now,
            SourceModule = source,
            CorrelationId = correlationId,
            Payload = error
        });
    }

    public void PublishStrategyDemoted(string strategyName, string memKey, string reason, string? correlationId = null)
    {
        Publish(new SystemEvent
        {
            Type = SystemEventType.StrategyDemoted,
            Timestamp = DateTime.Now,
            SourceModule = "ResolutionEngine",
            CorrelationId = correlationId,
            Payload = new { strategyName = strategyName, memKey = memKey, reason = reason }
        });
    }
}
