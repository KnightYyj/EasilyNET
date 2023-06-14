﻿using EasilyNET.Mongo.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global

namespace EasilyNET.Mongo;

/// <summary>
/// 1.Create a DbContext use connectionString with [ConnectionStrings.Mongo in appsettings.json] or with
/// [CONNECTIONSTRINGS_MONGO] setting value in environment variable
/// 2.Inject DbContext use services.AddSingleton(db);
/// 3.Inject IMongoDataBase use services.AddSingleton(db._database);
/// 4.添加SkyAPM的诊断支持.在添加服务的时候填入 ClusterConfigurator,为减少依赖,所以需手动填入
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 是否是第一次注册BsonSerializer
    /// </summary>
    private static bool first;

    /// <summary>
    /// 通过默认连接字符串名称添加DbContext
    /// </summary>
    /// <typeparam name="T">DbContext</typeparam>
    /// <param name="services">IServiceCollection</param>
    /// <param name="configuration">IConfiguration</param>
    /// <param name="option">其他参数</param>
    /// <returns></returns>
    public static IServiceCollection AddMongoContext<T>(this IServiceCollection services, IConfiguration configuration, Action<EasilyMongoOptions>? option = null)
        where T : EasilyMongoContext
    {
        var connStr = configuration["CONNECTIONSTRINGS_MONGO"] ?? configuration.GetConnectionString("Mongo") ?? throw new("💔:no [CONNECTIONSTRINGS_MONGO] env or ConnectionStrings.Mongo is null in appsettings.json");
        _ = services.AddMongoContext<T>(connStr, option);
        return services;
    }

    /// <summary>
    /// 通过连接字符串添加DbContext
    /// </summary>
    /// <typeparam name="T">DbContext</typeparam>
    /// <param name="services">IServiceCollection</param>
    /// <param name="connStr">链接字符串</param>
    /// <param name="option">其他参数</param>
    /// <returns></returns>
    public static IServiceCollection AddMongoContext<T>(this IServiceCollection services, string connStr, Action<EasilyMongoOptions>? option = null)
        where T : EasilyMongoContext
    {
        var options = new EasilyMongoOptions();
        option?.Invoke(options);
        RegistryConventionPack(options);
        var mongoUrl = new MongoUrl(connStr);
        var settings = MongoClientSettings.FromUrl(mongoUrl);
        var dbName = !string.IsNullOrWhiteSpace(mongoUrl.DatabaseName) ? mongoUrl.DatabaseName : options.DatabaseName ?? Constant.DbName;
        if (options.DatabaseName is not null) dbName = options.DatabaseName;
        _ = services.AddMongoContext<T>(settings, c =>
        {
            c.ObjectIdToStringTypes = options.ObjectIdToStringTypes;
            c.DefaultConventionRegistry = options.DefaultConventionRegistry;
            c.ConventionRegistry = options.ConventionRegistry;
            c.ClusterBuilder = options.ClusterBuilder;
            c.LinqProvider = options.LinqProvider;
            c.DatabaseName = dbName;
        });
        return services;
    }

    /// <summary>
    /// 使用MongoClientSettings配置添加DbContext
    /// </summary>
    /// <typeparam name="T">DbContext</typeparam>
    /// <param name="services">IServiceCollection</param>
    /// <param name="settings">HoyoMongoClientSettings</param>
    /// <param name="option">其他参数</param>
    /// <returns></returns>
    public static IServiceCollection AddMongoContext<T>(this IServiceCollection services, MongoClientSettings settings, Action<EasilyMongoOptions>? option = null)
        where T : EasilyMongoContext
    {
        var dbOptions = new EasilyMongoOptions();
        option?.Invoke(dbOptions);
        RegistryConventionPack(dbOptions);
        settings.ClusterConfigurator = dbOptions.ClusterBuilder ?? settings.ClusterConfigurator;
        settings.LinqProvider = dbOptions.LinqProvider;
        var db = EasilyMongoContext.CreateInstance<T>(settings, dbOptions.DatabaseName ?? Constant.DbName);
        _ = services.AddSingleton(db).AddSingleton(db.Database).AddSingleton(db.Client);
        return services;
    }

    private static void RegistryConventionPack(EasilyMongoOptions options)
    {
        if (options.DefaultConventionRegistry)
        {
            ConventionRegistry.Register($"{Constant.Pack}-{ObjectId.GenerateNewId()}", new ConventionPack
            {
                new CamelCaseElementNameConvention(),             // 驼峰名称格式
                new IgnoreExtraElementsConvention(true),          // 忽略掉实体中不存在的字段
                new NamedIdMemberConvention("Id", "ID"),          // _id映射为实体中的ID或者Id
                new EnumRepresentationConvention(BsonType.String) // 将枚举类型存储为字符串格式
            }, _ => true);
        }
        foreach (var item in options.ConventionRegistry)
        {
            ConventionRegistry.Register(item.Key, item.Value, _ => true);
        }
        ConventionRegistry.Register($"easily-id-pack-{ObjectId.GenerateNewId()}", new ConventionPack
        {
            new StringObjectIdIdGeneratorConvention() //ObjectId → String mapping ObjectId
        }, x => !options.ObjectIdToStringTypes.Contains(x));
        if (first) return;
        BsonSerializer.RegisterSerializer(new DateTimeSerializer(DateTimeKind.Local)); //to local time
        BsonSerializer.RegisterSerializer(new DecimalSerializer(BsonType.Decimal128)); //decimal to decimal default
        first = !first;
    }
}