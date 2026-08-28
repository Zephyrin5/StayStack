using Mediator;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
namespace BuildingBlocks.Observability;

/// <summary>
///     Wraps every request execution with a trace span, a duration/outcome
///     metric, and a structured log line. Registered once as an open generic
///     against IPipelineBehavior&lt;,&gt; so it applies to every message on the
///     mediator pipeline automatically.
///     Redaction itself lives in PayloadRedactor, whose type-flow annotation
///     preserves only the public message properties it needs to inspect.
/// </summary>
public sealed class TelemetryPipelineBehavior<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    TMessage,
    TResponse>(
    ILogger<TelemetryPipelineBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        string messageName = typeof(TMessage).Name;

        using Activity? activity = CommandTelemetry.ActivitySource.StartActivity(messageName);

        activity?.SetTag("message.type", messageName);
        activity?.SetTag("message.payload", PayloadRedactor.Redact(message));

        Stopwatch stopwatch = Stopwatch.StartNew();
        string outcome = "success";

        try
        {
            TResponse result = await next(message, cancellationToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Message {MessageName} succeeded in {ElapsedMs}ms. Payload: {Payload}",
                    messageName,
                    stopwatch.Elapsed.TotalMilliseconds,
                    PayloadRedactor.Redact(message));
            }

            return result;
        }
        catch (Exception ex)
        {
            outcome = "failure";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogWarning(
                    ex,
                    "Message {MessageName} failed after {ElapsedMs}ms. Payload: {Payload}",
                    messageName,
                    stopwatch.Elapsed.TotalMilliseconds,
                    PayloadRedactor.Redact(message));
            }

            throw;
        }
        finally
        {
            stopwatch.Stop();

            var tags = new[]
            {
                new KeyValuePair<string, object?>("message.name", messageName),
                new KeyValuePair<string, object?>("message.outcome", outcome)
            };

            CommandTelemetry.Duration.Record(stopwatch.Elapsed.TotalMilliseconds, tags);
            CommandTelemetry.Executions.Add(1, tags);

            activity?.SetTag("message.outcome", outcome);
        }
    }
}
