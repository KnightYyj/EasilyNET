﻿namespace EasilyNET.Core.Domain.SourceGenerator;

/// <summary>
/// 审计后实休源代码生成器
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AuditedEntitySourceGenerator : ISourceGenerator
{
    /// <inheritdoc />
    public void Initialize(GeneratorInitializationContext context)
    {
        // if (!Debugger.IsAttached)
        // {
        //     Debugger.Launch();
        // }
    }

    /// <inheritdoc />
    public void Execute(GeneratorExecutionContext context)
    {
        var compilation = context.Compilation;
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var root = syntaxTree.GetRoot();
            var semanticModel = context.Compilation.GetSemanticModel(syntaxTree);
            var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            foreach (var classDeclaration in classDeclarations)
            {
                //判断是否分部类
                if (!classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    continue;
                }
                // // 处理分部类并继承自 Entity`1 的情况
                // if (classDeclaration.BaseList?.Types.OfType<SimpleBaseTypeSyntax>()
                //                     .Any(typeSyntax =>
                //                     {
                //                         if (typeSyntax.Type.Kind() == SyntaxKind.GenericName)
                //                         {
                //                             var genericName = (GenericNameSyntax)typeSyntax.Type;
                //                             return genericName.Identifier.ValueText == "Entity";
                //                         }
                //                         return false;
                //                     }) ==
                //     false)
                // {
                //     continue;
                // }
                var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration);
                if (classSymbol is null)
                {
                    continue;
                }
                //只处理这接口
                foreach (var interfaceSymbol in classSymbol.AllInterfaces.Where(i =>
                             i.Name is "IMayHaveCreator" or
                                 "IHasCreationTime" or
                                 "IHasModifierId" or
                                 "IHasModificationTime" or
                                 "IHasDeleterId" or
                                 "IHasDeletionTime"))
                {
                    //得到接口属性
                    var propertySymbols = interfaceSymbol.GetMembers().OfType<IPropertySymbol>();
                    foreach (var propertySymbol in propertySymbols)
                    {
                        var ns = classSymbol.ContainingNamespace.ToString();
                        var propertyName = propertySymbol.Name;
                        var propertyType = propertySymbol.Type.ToDisplayString();
                        var get = propertySymbol.GetMethod;
                        var set = propertySymbol.SetMethod;
                        var getName = get is not null ? "get;" : string.Empty;
                        var setName = set is not null ? "set;" : string.Empty;
                        var source = $$"""
                                       // <auto-generated/>
                                       using EasilyNET.Core.Domains;
                                       using System;
                                       using System.ComponentModel;

                                       namespace {{ns}};

                                       public partial class  {{classSymbol.Name}}
                                       {
                                          public {{propertyType}} {{propertyName}} {{{getName}} {{setName}}}
                                       }
                                       """;
                        var extensionTextFormatted = CSharpSyntaxTree.ParseText(source).GetRoot().NormalizeWhitespace().SyntaxTree.GetText().ToString();
                        context.AddSource($"{interfaceSymbol.Name}.{classSymbol.Name}.g.cs", SourceText.From(extensionTextFormatted, Encoding.UTF8));
                        //
                        //
                        // Debug.WriteLine($"Property Name: {propertyName}, Property Type: {propertyType}, Accessibility: {propertyAccessibility}");
                        // Debug.WriteLine(source);
                    }
                }
            }
        }
    }
}