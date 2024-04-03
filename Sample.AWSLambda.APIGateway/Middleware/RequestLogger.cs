﻿using Amazon.Lambda.APIGatewayEvents;
using Dialogue;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Sample.AWSLambda.APIGateway.Middleware;

// This sample event logger is similar to the Serilog web request logger that can be used in ASP.NET Core.
// Register the event logger ahead of the other middleware components.
internal sealed class RequestLogger(
    Handler<APIGatewayHttpProxyContext, APIGatewayHttpApiV2ProxyResponse> next,
    ILogger<RequestLogger> logger)
    : IMiddleware<APIGatewayHttpProxyContext, APIGatewayHttpApiV2ProxyResponse>
{
    private readonly ILogger<RequestLogger> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Handler<APIGatewayHttpProxyContext, APIGatewayHttpApiV2ProxyResponse> Next { get; } = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(RequestContext<APIGatewayHttpProxyContext, APIGatewayHttpApiV2ProxyResponse> context)
    {
        using var logscope = logger.BeginScope($"{nameof(APIGateway)}::{context.Request.LambdaContext.InvokedFunctionArn}::{context.Request.LambdaContext.AwsRequestId}");

        var timer = Stopwatch.StartNew();
        try
        {
            // always check for cancellation
            context.CancellationToken.ThrowIfCancellationRequested();
            await Next(context); // have to await because of the using block started on line 21
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "error processing SQS event");
            throw;
        }
        finally
        {
            // there's more request information to log here, but this is minimal example
            logger.LogInformation(
                "request id {RequestId} processed in {ElapsedMilliseconds}ms with {RemainingMilliseconds}ms remaining. {Path}",
                context.Request.HttpRequest.RequestContext.RequestId,
                timer.ElapsedMilliseconds,
                context.Request.LambdaContext.RemainingTime.TotalMilliseconds,
                context.Request.HttpRequest.RawPath);
        }
    }
}
