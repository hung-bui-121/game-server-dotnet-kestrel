namespace GameServer.Commands;

/// <summary>Binds a <see cref="CommandDescriptor"/> to its request/result types and handler type.</summary>
public abstract class CommandRegistration
{
    protected CommandRegistration(CommandDescriptor descriptor, Type requestType, Type resultType, Type handlerType)
    {
        Descriptor = descriptor;
        RequestType = requestType;
        ResultType = resultType;
        HandlerType = handlerType;
    }

    public CommandDescriptor Descriptor { get; }

    public Type RequestType { get; }

    public Type ResultType { get; }

    /// <summary>Handler type, activated from DI per execution.</summary>
    public Type HandlerType { get; }

    public abstract ValueTask<ICommandOutcome> InvokeAsync(object handler, object request, ICommandContext context);

    /// <summary>Returns a copy bound to another handler type (EXT-02, ReplaceHandler).</summary>
    public abstract CommandRegistration WithHandler(Type handlerType);
}

/// <summary>Typed registration.</summary>
public sealed class CommandRegistration<TRequest, TResult> : CommandRegistration
{
    public CommandRegistration(CommandDescriptor descriptor, Type handlerType)
        : base(descriptor, typeof(TRequest), typeof(TResult), handlerType)
    {
        if (!typeof(ICommandHandler<TRequest, TResult>).IsAssignableFrom(handlerType))
        {
            throw new ArgumentException(
                $"{handlerType} does not implement ICommandHandler<{typeof(TRequest).Name}, {typeof(TResult).Name}>.",
                nameof(handlerType));
        }
    }

    public override async ValueTask<ICommandOutcome> InvokeAsync(object handler, object request, ICommandContext context)
    {
        var typed = (ICommandHandler<TRequest, TResult>)handler;
        return await typed.HandleAsync((TRequest)request, context).ConfigureAwait(false);
    }

    public override CommandRegistration WithHandler(Type handlerType) =>
        new CommandRegistration<TRequest, TResult>(Descriptor, handlerType);
}

/// <summary>Fluent entry point: <c>Command.Define&lt;TReq, TRes&gt;(descriptor).HandledBy&lt;THandler&gt;()</c>.</summary>
public static class Command
{
    public static CommandDefinition<TRequest, TResult> Define<TRequest, TResult>(CommandDescriptor descriptor) => new(descriptor);
}

public readonly struct CommandDefinition<TRequest, TResult>(CommandDescriptor descriptor)
{
    public CommandRegistration<TRequest, TResult> HandledBy<THandler>()
        where THandler : ICommandHandler<TRequest, TResult> =>
        new(descriptor, typeof(THandler));
}
