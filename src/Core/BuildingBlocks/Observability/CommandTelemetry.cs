using System.Diagnostics;
using System.Diagnostics.Metrics;
namespace BuildingBlocks.Observability;

/// <summary>
///     A single, shared ActivitySource and Meter for every command that flows
///     through the command bus. One name, registered once with the OTel SDK
///     in Api/Program.cs, picks up every feature automatically as you add them -
///     no per-feature wiring needed.
/// </summary>
public static class CommandTelemetry
{
    public const string SourceName = "StayStack.Commands";

    public static readonly ActivitySource ActivitySource = new ActivitySource(SourceName);

    private static readonly Meter Meter = new Meter(SourceName);

    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "command.duration",
        "ms",
        "Time taken to execute a command, tagged by command name and outcome.");

    public static readonly Counter<long> Executions = Meter.CreateCounter<long>(
        "command.executions",
        description: "Number of command executions, tagged by command name and outcome.");
}
