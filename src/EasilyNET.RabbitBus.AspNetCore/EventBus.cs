﻿using EasilyNET.Core.Misc;
using EasilyNET.RabbitBus.AspNetCore.Abstraction;
using EasilyNET.RabbitBus.AspNetCore.Extensions;
using EasilyNET.RabbitBus.Core.Abstraction;
using EasilyNET.RabbitBus.Core.Attributes;
using EasilyNET.RabbitBus.Core.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace EasilyNET.RabbitBus;

internal sealed class EventBus(IPersistentConnection conn, int retry, ISubscriptionsManager subsManager, IServiceProvider sp, ILogger<EventBus> logger) : IBus, IDisposable
{
    private const string HandleName = nameof(IEventHandler<IEvent>.HandleAsync);

    private static readonly JsonSerializerOptions jsonSerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private bool disposed;

    /// <inheritdoc />
    public async Task Publish<T>(T @event, string? routingKey = null, byte? priority = 0, CancellationToken? cancellationToken = null) where T : IEvent
    {
        if (!conn.IsConnected) await conn.TryConnect();
        var type = @event.GetType();
        var exc = type.GetCustomAttribute<ExchangeAttribute>() ?? throw new($"{nameof(@event)}未设置<{nameof(ExchangeAttribute)}>,无法创建发布事件");
        if (!exc.Enable) return;
        var channel = await conn.GetChannel();
        var properties = new BasicProperties
        {
            Persistent = true,
            DeliveryMode = DeliveryModes.Persistent,
            Priority = priority.GetValueOrDefault()
        };
        var headers = @event.GetHeaderAttributes();
        if (headers is not null && headers.Count is not 0) properties.Headers = headers;
        if (exc is not { WorkModel: EModel.None })
        {
            var exchange_args = @event.GetExchangeArgAttributes();
            await channel.ExchangeDeclareAsync(exc.ExchangeName, exc.WorkModel.ToDescription(), true, arguments: exchange_args);
        }
        // 在发布事件前检查是否已经取消发布
        if (cancellationToken is not null && cancellationToken.Value.IsCancellationRequested) return;
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType(), jsonSerializerOptions);
        // 创建Policy规则
        var policy = Policy.Handle<BrokerUnreachableException>()
                           .Or<SocketException>()
                           .WaitAndRetry(retry, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), (ex, time) =>
                               logger.LogError(ex, "无法发布: {EventId} 超时 {Timeout}s ({ExceptionMessage})", @event.EventId, $"{time.TotalSeconds:n1}", ex.Message));
        await policy.Execute(async () =>
        {
            logger.LogTrace("发布: {EventId}", @event.EventId);
            await channel.BasicPublishAsync(exc.ExchangeName, routingKey ?? exc.RoutingKey, properties, body, true).ConfigureAwait(false);
            await conn.ReturnChannel(channel).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Publish<T>(T @event, uint ttl, string? routingKey = null, byte? priority = 0, CancellationToken? cancellationToken = null) where T : IEvent
    {
        if (!conn.IsConnected) await conn.TryConnect();
        var type = @event.GetType();
        var exc = type.GetCustomAttribute<ExchangeAttribute>() ?? throw new($"{nameof(@event)}未设置<{nameof(ExchangeAttribute)}>,无法发布事件");
        if (!exc.Enable) return;
        if (exc is not { WorkModel: EModel.Delayed } | exc.IsDlx is not true) throw new($"延时队列的交换机类型必须为{nameof(EModel.Delayed)}且 isDlx 参数必须为 true");
        var channel = await conn.GetChannel();
        var properties = new BasicProperties
        {
            Persistent = true,
            DeliveryMode = DeliveryModes.Persistent,
            Priority = priority.GetValueOrDefault()
        };
        //延时时间从header赋值
        var headers = @event.GetHeaderAttributes();
        if (headers is not null)
        {
            var xDelay = headers.TryGetValue("x-delay", out var delay);
            headers["x-delay"] = xDelay && ttl == 0 && delay is not null ? delay : ttl;
            properties.Headers = headers;
        }
        else
        {
            properties.Headers?.Add("x-delay", ttl);
        }
        // x-delayed-type 必须加
        var exc_args = @event.GetExchangeArgAttributes();
        if (exc_args is not null)
        {
            var xDelayedType = exc_args.TryGetValue("x-delayed-type", out var delayedType);
            exc_args["x-delayed-type"] = !xDelayedType || delayedType is null ? "direct" : delayedType;
        }
        //创建延时交换机,type类型为x-delayed-message
        await channel.ExchangeDeclareAsync(exc.ExchangeName, exc.WorkModel.ToDescription(), true, false, exc_args);
        // 在发布事件前检查是否已经取消发布
        if (cancellationToken is not null && cancellationToken.Value.IsCancellationRequested) return;
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType(), jsonSerializerOptions);
        // 创建Policy规则
        var policy = Policy.Handle<BrokerUnreachableException>()
                           .Or<SocketException>()
                           .WaitAndRetry(retry, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), (ex, time) =>
                               logger.LogError(ex, "无法发布: {EventId} 超时 {Timeout}s ({ExceptionMessage})", @event.EventId, $"{time.TotalSeconds:n1}", ex.Message));
        await policy.Execute(async () =>
        {
            logger.LogTrace("发布: {EventId}", @event.EventId);
            await channel.BasicPublishAsync(exc.ExchangeName, routingKey ?? exc.RoutingKey, properties, body, true).ConfigureAwait(false);
            await conn.ReturnChannel(channel).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        subsManager.Clear();
        disposed = true;
    }

    internal async Task Subscribe()
    {
        if (!conn.IsConnected) await conn.TryConnect();
        await InitialRabbit();
    }

    private async Task InitialRabbit()
    {
        var events = AssemblyHelper.FindTypes(o => o is { IsClass: true, IsAbstract: false } && o.IsBaseOn(typeof(IEvent)) && o.HasAttribute<ExchangeAttribute>());
        var handlers = AssemblyHelper.FindTypes(o => o is
                                                     {
                                                         IsClass: true,
                                                         IsAbstract: false
                                                     } &&
                                                     o.IsBaseOn(typeof(IEventHandler<>)) &&
                                                     !o.HasAttribute<IgnoreHandlerAttribute>()).Select(s => s.GetTypeInfo()).ToList();
        foreach (var @event in events)
        {
            var exc = @event.GetCustomAttribute<ExchangeAttribute>();
            if (exc is null || exc.Enable is false) continue;
            var handler = handlers.FindAll(o => o.ImplementedInterfaces.Any(s => s.GenericTypeArguments.Contains(@event)));
            if (handler.Count is 0) continue;
            await Task.Factory.StartNew(async () =>
            {
                using var channel = await CreateConsumerChannel(exc, @event);
                if (exc is not { WorkModel: EModel.None })
                {
                    await DoInternalSubscription(@event.Name, exc, channel);
                }
                //using var scope = sp.GetService<IServiceScopeFactory>()?.CreateScope();
                //for (var i = 0; i < handler.Count; i++)
                //{
                //    var handlerService = scope?.ServiceProvider.GetService(handler[i]);
                //    // 检查消费者是否已经注册,若是未注册则不启动消费.
                //    // 这里由于我们在注入服务的时候,已经注册了服务,并非用户手动注入，所以这里没有必要再检查是否注册.
                //    if (handlerService is null) handler.RemoveAt(i);
                //}
                subsManager.AddSubscription(@event, exc.IsDlx, handler);
                await StartBasicConsume(@event, exc, channel);
            }, TaskCreationOptions.LongRunning);
        }
    }

    private async Task<IChannel> CreateConsumerChannel(ExchangeAttribute exc, Type @event)
    {
        logger.LogTrace("创建消费者通道");
        var channel = await conn.GetChannel();
        var queue_args = @event.GetQueueArgAttributes();
        if (exc.IsDlx)
        {
            queue_args ??= new Dictionary<string, object?>();
            queue_args.Add("x-dead-letter-exchange", exc.ExchangeName);
            queue_args.Add("x-dead-letter-routing-key", exc.RoutingKey);
        }
        if (exc is not { WorkModel: EModel.None })
        {
            var exchange_args = @event.GetExchangeArgAttributes();
            if (exchange_args is not null)
            {
                var success = exchange_args.TryGetValue("x-delayed-type", out _);
                if (!success && exc is { WorkModel: EModel.Delayed }) exchange_args.Add("x-delayed-type", "direct"); //x-delayed-type必须加
            }
            //创建交换机
            await channel.ExchangeDeclareAsync(exc.ExchangeName, exc.WorkModel.ToDescription(), true, false, exchange_args);
        }
        //创建队列
        await channel.QueueDeclareAsync(exc.Queue, true, false, false, queue_args);
        channel.CallbackException += async (_, ea) =>
        {
            logger.LogWarning(ea.Exception, "重新创建消费者通道");
            subsManager.Clear();
            await Subscribe();
        };
        return channel;
    }

    private async Task DoInternalSubscription(string eventName, ExchangeAttribute exc, IChannel channel)
    {
        var containsKey = subsManager.HasSubscriptionsForEvent(eventName, exc.IsDlx);
        if (containsKey) return;
        if (!conn.IsConnected) await conn.TryConnect();
        await channel.QueueBindAsync(exc.Queue, exc.ExchangeName, exc.RoutingKey);
    }

    private async Task StartBasicConsume(Type eventType, ExchangeAttribute exc, IChannel channel)
    {
        var qos = eventType.GetCustomAttribute<QosAttribute>();
        if (qos is not null) await channel.BasicQosAsync(qos.PrefetchSize, qos.PrefetchCount, qos.Global);
        var consumer = new AsyncEventingBasicConsumer(channel);
        await channel.BasicConsumeAsync(exc.Queue, false, consumer);
        consumer.Received += async (_, ea) =>
        {
            var message = Encoding.UTF8.GetString(ea.Body.Span);
            try
            {
                if (message.Contains("throw-fake-exception", StringComparison.InvariantCultureIgnoreCase))
                {
                    throw new InvalidOperationException($"假异常请求:{message}");
                }
                await ProcessEvent(eventType, message, exc.IsDlx, () => channel.BasicAck(ea.DeliveryTag, false));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "错误处理消息:{Message}", message);
                // 先注释掉,若是消费者没写对,造成大量消息重新入队,容易拖垮MQ,这里的处理办法是长时间不确认,所有消息由Unacked重新变为Ready
                //channel.BasicNack(ea.DeliveryTag, false, true);
            }
        };
        while (true)
        {
            if (channel.IsClosed) break;
            await Task.Delay(100000);
        }
    }

    private async Task ProcessEvent(Type eventType, string message, bool isDlx, Action ack)
    {
        logger.LogTrace("处理事件: {EventName}", eventType.Name);
        var policy = Policy.Handle<BrokerUnreachableException>()
                           .Or<SocketException>()
                           .WaitAndRetry(retry, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), (ex, time) =>
                               logger.LogError(ex, "无法消费: {EventName} 超时 {Timeout}s ({ExceptionMessage})", eventType.Name, $"{time.TotalSeconds:n1}", ex.Message));
        if (subsManager.HasSubscriptionsForEvent(eventType.Name, isDlx))
        {
            var @event = JsonSerializer.Deserialize(message, eventType, jsonSerializerOptions);
            var handlerTypes = subsManager.GetHandlersForEvent(eventType.Name, isDlx);
            using var scope = sp.GetService<IServiceScopeFactory>()?.CreateScope();
            await policy.Execute(async () =>
            {
                foreach (var handlerType in handlerTypes)
                {
                    if (@event is null)
                    {
                        throw new($"{nameof(@event)}不能为空");
                    }
                    var method = typeof(IEventHandler<>).MakeGenericType(eventType).GetMethod(HandleName);
                    if (method is null)
                    {
                        logger.LogError($"无法找到{nameof(@event)}事件处理器下处理方法");
                        throw new($"无法找到{nameof(@event)}事件处理器下处理方法");
                    }
                    var handler = scope?.ServiceProvider.GetService(handlerType);
                    if (handler is null) continue;
                    await Task.Yield();
                    var obj = method.Invoke(handler, [@event]);
                    if (obj is null) continue;
                    await (Task)obj;
                }
                ack.Invoke();
            });
        }
        else
        {
            logger.LogError("没有订阅事件:{EventName}", eventType.Name);
        }
    }
}