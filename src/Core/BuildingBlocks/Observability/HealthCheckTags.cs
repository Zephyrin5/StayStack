namespace BuildingBlocks.Observability;

/// <summary>
///     Tags that decide which health checks answer which probe.
///     <para>
///         A constant rather than a literal at three call sites, because the
///         failure mode of a typo here is silent and severe: a readiness check
///         tagged "redy" is simply never run, and the endpoint answers
///         Healthy - which is exactly the shape of the bug this split exists
///         to fix.
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
