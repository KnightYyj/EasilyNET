﻿using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;

namespace EasilyNET.AutoInjection.SourceGenerator;

//https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md
/// <summary>
/// 自动注入生成器
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AutoInjectionIIncremental : IIncrementalGenerator
{
    /// <summary>
    /// 默认名字
    /// </summary>
    private const string DefaultName = "Injection";

    /// <summary>
    /// 名字前缀
    /// </summary>
    private const string Prefix = "Auto";



    private const string TransientDependencyName = "EasilyNET.AutoDependencyInjection.Core.Abstractions.ITransientDependency";
    private const string ScopedDependencyName = "EasilyNET.AutoDependencyInjection.Core.Abstractions.IScopedDependency";
    private const string SingletonDependencyName = "EasilyNET.AutoDependencyInjection.Core.Abstractions.ISingletonDependency";
    private const string IgnoreDependencyAttributeName = "EasilyNET.AutoDependencyInjection.Core.Attributes.IgnoreDependencyAttribute";
    private const string DependencyInjectionAttributeName = "EasilyNET.AutoDependencyInjection.Core.Attributes.DependencyInjectionAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        if (!Debugger.IsAttached)
        {
            Debugger.Launch();
        }

        var pipeline = context.SyntaxProvider.CreateSyntaxProvider(SyntacticPredicate, SemanticTransform).Where(static context => context is not null).Collect();

        //得到命名空间
        var assemblyName = context.CompilationProvider
                                  .Select(static (c, _) => c.AssemblyName);

        //得到.csproj下配置名字
        //< PropertyGroup >
        //< InjectionName > Injection </ InjectionName >
        //</ PropertyGroup >
        //<ItemGroup >
        //<CompilerVisibleProperty Include = "InjectionName" />
        //</ItemGroup >
        var methodName = context.AnalyzerConfigOptionsProvider
                                .Select(static (c, _) =>
                                {
                                    c.GlobalOptions.TryGetValue("build_property.InjectionName", out var methodName);
                                    return methodName;
                                });


        var options = assemblyName.Combine(methodName);
        var generation = pipeline.Combine(options);
        context.RegisterSourceOutput(generation, ExecuteGeneration);
    }

#nullable enable
    /// <summary>
    /// 执行生成 
    /// </summary>
    /// <param name="sourceContext">上下文</param>
    /// <param name="source">
    /// ValueTuple<ImmutableArray<ClassMetadata>, ValueTuple<string, string>>
    /// Item1=>ImmutableArray<ClassMetadata>;
    /// Item2.Item1=>RootNamespace
    /// Item2.Item2=>MethodName
    /// 源
    ///</param>
    private void ExecuteGeneration(
        SourceProductionContext sourceContext,
        (ImmutableArray<ClassMetadata> ClassMetadatas, (string? RootNamespace, string? MethodName) Options) source)
    {

        //方法名
        var methodName = source.Options.MethodName;
        //如果空值，就使用默认名
        if (string.IsNullOrWhiteSpace(methodName))
        {
            methodName = DefaultName;
        }
        CodeGenerationContext codeContext = new();
        codeContext.WriteLines("// <auto-generated/>");
        codeContext.WriteLines("using EasilyNET.AutoDependencyInjection.Core;");
        codeContext.WriteLines("using EasilyNET.AutoDependencyInjection.Core.Abstractions;");
        codeContext.WriteLines("using System;");
        codeContext.WriteLines("using Microsoft.Extensions.DependencyInjection;");
        codeContext.WriteLines($"namespace {source.Options.RootNamespace};");
        codeContext.WriteLines($"public static partial class _{Prefix}{methodName}");
        var add = "Add";
        using (codeContext.CodeBlock())
        {
            codeContext.WriteLines($"public static IServiceCollection {add}{Prefix}{methodName}(this IServiceCollection services)");
            using (codeContext.CodeBlock())
            {
                foreach (var temp in source.ClassMetadatas)
                {

                    foreach (var serviceType in temp.ServiceTypes)
                    {

                        codeContext.WriteLines($"services.Add(new ServiceDescriptor(typeof({GetTypeName(serviceType)}), typeof({GetTypeName(temp.ImplementationType)}),ServiceLifetime.{temp.Lifetime}));");
                    }
                }
                codeContext.WriteLines("return services;");
            }
        }

        var sourceCode = codeContext.SourceCode;
        var extensionTextFormatted = CSharpSyntaxTree.ParseText(sourceCode).GetRoot().NormalizeWhitespace().SyntaxTree.GetText().ToString();

        sourceContext.AddSource($"{Prefix}{methodName}.g.cs", SourceText.From(extensionTextFormatted, Encoding.UTF8));

    }

    /// <summary>
    /// 句法谓语
    /// </summary>
    /// <param name="syntaxNode"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>

    private static bool SyntacticPredicate(SyntaxNode syntaxNode, CancellationToken cancellationToken)
    {
        return syntaxNode is ClassDeclarationSyntax { AttributeLists.Count: > 0 } classDeclaration
               && classDeclaration.Modifiers.Any(SyntaxKind.PublicKeyword)
               && !classDeclaration.Modifiers.Any(SyntaxKind.AbstractKeyword)
               && !classDeclaration.Modifiers.Any(SyntaxKind.StaticKeyword);
    }

    /// <summary>
    /// 语义转换
    /// </summary>
    /// <param name="context">上下文</param>
    /// <param name="_"></param>
    /// <returns></returns>
    private static ClassMetadata? SemanticTransform(GeneratorSyntaxContext context, CancellationToken _)
    {

        var typeSymbol = (ITypeSymbol)context.SemanticModel.GetDeclaredSymbol(context.Node)!; //定义成

        return CreateAttributeMetadata(typeSymbol) ?? CreateClassMetadata(typeSymbol);
    }


    /// <summary>
    /// 创建元数据
    /// </summary>
    /// <param name="typeSymbol"></param>
    /// <returns></returns>
    private static ClassMetadata? CreateClassMetadata(ITypeSymbol typeSymbol)
    {
        var dependencyInterface = typeSymbol.Interfaces.SelectMany(o => o.Interfaces).FirstOrDefault(o => $"{TransientDependencyName},{ScopedDependencyName},{SingletonDependencyName}".Contains(o.ToDisplayString()));
        var interfaces = typeSymbol.Interfaces;
        return dependencyInterface is null ? default : new ClassMetadata(typeSymbol, GetLifetime(dependencyInterface.ToDisplayString())).AddServiceTypes(interfaces);
    }

    /// <summary>
    /// 创建特性
    /// </summary>
    /// <param name="typeSymbol"></param>
    /// <returns></returns>
    private static ClassMetadata? CreateAttributeMetadata(ITypeSymbol typeSymbol)
    {

        var attr = typeSymbol.GetAttributes().FirstOrDefault(o => o.AttributeClass?.ToDisplayString() == DependencyInjectionAttributeName);
        if (attr is null)
        {

            return default;
        }
        var lifetime = (int)attr.ConstructorArguments[0].Value!;


        foreach (var attribute in typeSymbol.GetAttributes())
        {
            foreach (var parameter in attribute.NamedArguments)
            {

            }
        }
        //是否要添加自己
        var addSelf = attr.ConstructorArguments.ElementAtOrDefault(1).Value;
        ClassMetadata classMetadata = new ClassMetadata(typeSymbol, GetLifetime(lifetime));

        classMetadata.AddServiceTypes(bool.TryParse(addSelf?.ToString(), out var addSelfResult) && addSelfResult
                                          ? new[] { typeSymbol }
                                          : (typeSymbol.Interfaces.Any() ? typeSymbol.Interfaces : new[] { typeSymbol }));
        return classMetadata;
    }

    /// <summary>
    /// 得到生命周期
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>

    private static string GetLifetime(string value) =>
        value switch
        {
            SingletonDependencyName => "Singleton",
            ScopedDependencyName => "Scoped",
            TransientDependencyName => "Transient",
            _ => throw new NotImplementedException()
        };

    /// <summary>
    /// 得到生命周期
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    private static string GetLifetime(int value) =>
        value switch
        {
            0 => "Singleton",
            1 => "Scoped",
            2 => "Transient",
            _ => throw new NotImplementedException()
        };

    /// <summary>
    /// 得到类型名
    /// </summary>
    /// <param name="typeSymbol"></param>
    /// <returns></returns>
    private string GetTypeName(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } namedTypeSymbol)
        {
            var name = typeSymbol.Name;
            var typeParameters = namedTypeSymbol.TypeParameters;
            return $"{name}<{new string(',', typeParameters.Length - 1)}>";
        }
        return typeSymbol.ToDisplayString();
    }


}

/// <summary>
/// 数元数据
/// </summary>
/// <remarks>
/// 构造函数
/// </remarks>
/// <param name="implementationType">实例类型</param>
/// <param name="lifetime">生命周期</param>
public sealed class ClassMetadata(ITypeSymbol implementationType, string lifetime)
{

    /// <summary>
    /// 实例类型
    /// </summary>
    public ITypeSymbol ImplementationType { get; } = implementationType;

    /// <summary>
    /// 生命周期
    /// </summary>
    public string Lifetime { get; } = lifetime;

    /// <summary>
    /// 服务类型集合
    /// </summary>
    public List<ITypeSymbol> ServiceTypes { get; } = [];

    /// <summary>
    /// 添加服务类型
    /// </summary>
    /// <param name="serviceTypes">服务类型集合</param>
    /// <returns></returns>
    public ClassMetadata AddServiceTypes(IEnumerable<ITypeSymbol> serviceTypes)
    {
        ServiceTypes.AddRange(serviceTypes);

        return this;
    }
}