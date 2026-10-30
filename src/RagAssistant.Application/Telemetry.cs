using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RagAssistant.Application;

public static class Telemetry
{
    public const string Name = "RagAssistant";
    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);
}
