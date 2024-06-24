﻿using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;

namespace Plumber;

internal sealed class RequestHandler<TRequest, TResponse>(
    ServiceProvider services,
    TimeSpan timeout)
    : IRequestHandler<TRequest, TResponse>
    where TRequest : class
{
    public ServiceProvider Services { get; } = services
        ?? throw new ArgumentNullException(nameof(services));
    public TimeSpan Timeout { get; } = timeout;
    private readonly List<Func<RequestMiddleware<TRequest, TResponse>, RequestMiddleware<TRequest, TResponse>>> components = [];

    private RequestMiddleware<TRequest, TResponse>? handler;

    public Task<TResponse?> InvokeAsync(TRequest request) =>
        Timeout == System.Threading.Timeout.InfiniteTimeSpan
            ? InvokeInternalAsync(request)
            : InvokeInternalAsync(request, Timeout);

    private async Task<TResponse?> InvokeInternalAsync(TRequest request)
    {
        using var serviceScope = services.CreateScope();
        var context = new RequestContext<TRequest, TResponse>(
            request,
            Ulid.NewUlid(),
            DateTime.UtcNow,
            serviceScope.ServiceProvider,
            CancellationToken.None);

        await EnsureHandler()(context);

        return context.Response;
    }

    private async Task<TResponse?> InvokeInternalAsync(TRequest request, TimeSpan timeout)
    {
        using var serviceScope = services.CreateScope();
        using var timeoutTokenSource = new CancellationTokenSource(timeout);

        var context = new RequestContext<TRequest, TResponse>(
            request,
            Ulid.NewUlid(),
            DateTime.UtcNow,
            serviceScope.ServiceProvider,
            timeoutTokenSource.Token);

        await EnsureHandler()(context);

        return context.Response;
    }

    public IRequestHandler<TRequest, TResponse> Prepare()
    {
        handler ??= BuildPipeline();
        return this;
    }

    private RequestMiddleware<TRequest, TResponse> EnsureHandler()
    {
        handler ??= BuildPipeline();
        return handler;
    }

    private RequestMiddleware<TRequest, TResponse> BuildPipeline()
    {
        RequestMiddleware<TRequest, TResponse> pipeline = context =>
            context.CancellationToken.IsCancellationRequested
                ? Task.FromCanceled<RequestContext<TRequest, TResponse>>(context.CancellationToken)
                : Task.FromResult(context);

        for (var i = components.Count - 1; i >= 0; --i)
        {
            pipeline = components[i](pipeline);
        }

        return pipeline;
    }

    public IRequestHandler<TRequest, TResponse> Use(Func<RequestMiddleware<TRequest, TResponse>, RequestMiddleware<TRequest, TResponse>> middleware)
    {
        components.Add(middleware);
        return this;
    }

    // this is a good place to start working out how to inject services into the middleware 
    public IRequestHandler<TRequest, TResponse> Use(Func<RequestContext<TRequest, TResponse>, RequestMiddleware<TRequest, TResponse>, Task> middleware) =>
        Use(next => context => middleware(context, next));

    public IRequestHandler<TRequest, TResponse> Use<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)] TMiddleware>()
        where TMiddleware : class, IMiddleware<TRequest, TResponse>
    {
        RequestMiddleware<TRequest, TResponse> Component(RequestMiddleware<TRequest, TResponse> next)
        {
            var component = CreateMiddleware(typeof(TMiddleware), next, null);
            return component.Invoke;
        }

        return Use(Component);
    }

    public IRequestHandler<TRequest, TResponse> Use<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)] TMiddleware>(params object[] parameters)
        where TMiddleware : class, IMiddleware<TRequest, TResponse>
    {
        RequestMiddleware<TRequest, TResponse> Component(RequestMiddleware<TRequest, TResponse> next)
        {
            var component = CreateMiddleware(typeof(TMiddleware), next, parameters);
            return component.Invoke;
        }

        return Use(Component);
    }

    private RequestMiddleware<TRequest, TResponse> CreateMiddleware(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        RequestMiddleware<TRequest, TResponse> next,
        object[]? parameters)
    {
        parameters = parameters is null
            ? [next]
            : parameters.Prepend(next).ToArray();

        var middleware = ActivatorUtilities
            .CreateInstance(
                services,
                type,
                parameters);

        var method = type.GetMethod(nameof(IMiddleware<TRequest, TResponse>.InvokeAsync));
        return method is null
            ? throw new InvalidOperationException($"{nameof(IMiddleware<TRequest, TResponse>.InvokeAsync)} not found.")
            : (context => (Task)(method.Invoke(middleware, [context])
                ?? throw new InvalidOperationException($"{nameof(IMiddleware<TRequest, TResponse>.InvokeAsync)} must return task")));
    }
}

