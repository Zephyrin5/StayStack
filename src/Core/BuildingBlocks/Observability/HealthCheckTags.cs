namespace BuildingBlocks.Observability;

/// <summary>
///     Tags that decide which health checks answer which probe.
///     <para>
///         Constants rather than literals at three call sites, because a typo
///         fails silently: a readiness check tagged "redy" never runs, and the
///         endpoint answers Healthy.
///     </para>
/// </summary>
public static class HealthCheckTags
{
    /// <summary>
    ///     Checks that decide whether this node should receive traffic.
    ///     Anything a request needs in order to succeed belongs here.
    /// </summary>
    public const string Ready = "ready";
}
