using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DatabaseAnalyzer.Roslyn;

internal sealed class ParsedFileSyntax
{
    public string FilePath { get; set; } = string.Empty;

    public bool HasEfCoreUsing { get; set; }

    public bool HasDataAnnotationsUsing { get; set; }

    public bool HasSchemaUsing { get; set; }

    public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);

    public List<string> ImportedNamespaces { get; } = [];

    public List<ClassCandidate> Classes { get; } = [];

    public List<string> Interfaces { get; } = [];
}

internal sealed class ClassCandidate
{
    public string ClassName { get; set; } = null!;

    public string? Namespace { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public ClassDeclarationSyntax SyntaxNode { get; set; } = null!;

    public ParsedFileSyntax FileSyntax { get; set; } = null!;
}

internal sealed class DbContextSyntaxVisitor : CSharpSyntaxWalker
{
    private readonly ParsedFileSyntax _fileSyntax;
    private readonly Action<string>? _onGlobalUsingFound;
    private readonly Action<string, string>? _onGlobalAliasFound;

    public DbContextSyntaxVisitor(
        ParsedFileSyntax fileSyntax,
        Action<string>? onGlobalUsingFound = null,
        Action<string, string>? onGlobalAliasFound = null)
    {
        _fileSyntax = fileSyntax;
        _onGlobalUsingFound = onGlobalUsingFound;
        _onGlobalAliasFound = onGlobalAliasFound;
    }

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var targetNamespaceOrType = node.Name?.ToString().Trim();

        if (string.IsNullOrWhiteSpace(targetNamespaceOrType))
        {
            base.VisitUsingDirective(node);
            return;
        }

        var isGlobal = node.GlobalKeyword != default;

        if (node.Alias is not null)
        {
            var aliasName = node.Alias.Name.Identifier.ValueText;

            if (isGlobal)
            {
                _onGlobalAliasFound?.Invoke(aliasName, targetNamespaceOrType);
            }
            else
            {
                _fileSyntax.Aliases[aliasName] = targetNamespaceOrType;
            }
        }
        else
        {
            if (isGlobal)
            {
                _onGlobalUsingFound?.Invoke(targetNamespaceOrType);
            }
            else
            {
                _fileSyntax.ImportedNamespaces.Add(targetNamespaceOrType);

                if (IsEfCoreNamespace(targetNamespaceOrType))
                {
                    _fileSyntax.HasEfCoreUsing = true;
                }
                else if (string.Equals(targetNamespaceOrType, "System.ComponentModel.DataAnnotations", StringComparison.Ordinal) ||
                         string.Equals(targetNamespaceOrType, "global::System.ComponentModel.DataAnnotations", StringComparison.Ordinal))
                {
                    _fileSyntax.HasDataAnnotationsUsing = true;
                }
                else if (string.Equals(targetNamespaceOrType, "System.ComponentModel.DataAnnotations.Schema", StringComparison.Ordinal) ||
                         string.Equals(targetNamespaceOrType, "global::System.ComponentModel.DataAnnotations.Schema", StringComparison.Ordinal))
                {
                    _fileSyntax.HasSchemaUsing = true;
                }
            }
        }

        base.VisitUsingDirective(node);
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var candidate = new ClassCandidate
        {
            ClassName = node.Identifier.ValueText,
            Namespace = GetEnclosingNamespace(node),
            FilePath = _fileSyntax.FilePath,
            SyntaxNode = node,
            FileSyntax = _fileSyntax
        };

        _fileSyntax.Classes.Add(candidate);

        base.VisitClassDeclaration(node);
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        _fileSyntax.Interfaces.Add(node.Identifier.ValueText);

        base.VisitInterfaceDeclaration(node);
    }

    public static bool IsDbContext(
        ClassCandidate candidate,
        bool isEfCoreGloballyImported,
        IReadOnlyDictionary<string, string> globalAliases)
    {
        var baseList = candidate.SyntaxNode.BaseList;

        if (baseList is null || baseList.Types.Count == 0)
        {
            return false;
        }

        var fileSyntax = candidate.FileSyntax;
        var efCoreInScope = isEfCoreGloballyImported || fileSyntax.HasEfCoreUsing;

        foreach (var baseType in baseList.Types)
        {
            if (MatchesDbContextType(baseType.Type, efCoreInScope, fileSyntax.Aliases, globalAliases))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<DiscoveredEntity> ExtractDbSetEntities(
        ClassDeclarationSyntax classNode,
        ParsedFileSyntax fileSyntax,
        bool isEfCoreGloballyImported,
        IReadOnlyDictionary<string, string> globalAliases)
    {
        var entities = new List<DiscoveredEntity>();
        var efCoreInScope = isEfCoreGloballyImported || fileSyntax.HasEfCoreUsing;

        foreach (var property in classNode.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (TryExtractDbSetEntityType(property.Type, efCoreInScope, fileSyntax.Aliases, globalAliases, out var entityName, out var fullTypeName))
            {
                entities.Add(new DiscoveredEntity
                {
                    EntityName = entityName!,
                    DbSetPropertyName = property.Identifier.ValueText,
                    FullEntityTypeName = fullTypeName
                });
            }
        }

        return entities;
    }

    private static bool MatchesDbContextType(
        TypeSyntax typeSyntax,
        bool efCoreInScope,
        IReadOnlyDictionary<string, string> localAliases,
        IReadOnlyDictionary<string, string> globalAliases)
    {
        switch (typeSyntax)
        {
            case IdentifierNameSyntax id:
            {
                var identifier = id.Identifier.ValueText;

                if (localAliases.TryGetValue(identifier, out var localAliasedTarget) && IsEfCoreDbContextTarget(localAliasedTarget))
                    return true;

                if (globalAliases.TryGetValue(identifier, out var globalAliasedTarget) && IsEfCoreDbContextTarget(globalAliasedTarget))
                    return true;

                if (string.Equals(identifier, "DbContext", StringComparison.Ordinal))
                {
                    // Если короткое имя DbContext переопределено локальным или глобальным псевдонимом на другой тип,
                    // это не EF Core DbContext.
                    if (localAliases.ContainsKey("DbContext") || globalAliases.ContainsKey("DbContext"))
                        return false;

                    return efCoreInScope;
                }

                return false;
            }

            case QualifiedNameSyntax qualified:
            {
                var fullText = qualified.ToString().Trim();

                if (IsEfCoreDbContextTarget(fullText))
                    return true;

                if (qualified.Right.Identifier.ValueText == "DbContext")
                {
                    var leftPrefix = qualified.Left.ToString().Trim();

                    if (localAliases.TryGetValue(leftPrefix, out var targetNs) && IsEfCoreNamespace(targetNs))
                        return true;

                    if (globalAliases.TryGetValue(leftPrefix, out var gTargetNs) && IsEfCoreNamespace(gTargetNs))
                        return true;
                }

                return false;
            }

            case AliasQualifiedNameSyntax aliasQualified:
            {
                var aliasName = aliasQualified.Alias.Identifier.ValueText;
                var rightName = aliasQualified.Name.ToString().Trim();

                if (string.Equals(aliasName, "global", StringComparison.Ordinal))
                {
                    if (string.Equals(rightName, "Microsoft.EntityFrameworkCore.DbContext", StringComparison.Ordinal))
                        return true;

                    if (string.Equals(rightName, "DbContext", StringComparison.Ordinal) && efCoreInScope)
                        return true;
                }

                return false;
            }

            default:
                return false;
        }
    }

    private static bool TryExtractDbSetEntityType(
        TypeSyntax typeSyntax,
        bool efCoreInScope,
        IReadOnlyDictionary<string, string> localAliases,
        IReadOnlyDictionary<string, string> globalAliases,
        out string? entityName,
        out string? fullTypeName)
    {
        entityName = null;
        fullTypeName = null;

        if (typeSyntax is NullableTypeSyntax nullable)
        {
            typeSyntax = nullable.ElementType;
        }

        GenericNameSyntax? genericName = null;

        switch (typeSyntax)
        {
            case GenericNameSyntax g:
                genericName = g;
                break;

            case QualifiedNameSyntax q when q.Right is GenericNameSyntax rightGeneric:
            {
                var prefix = q.Left.ToString().Trim();
                var isValidPrefix = IsEfCoreNamespace(prefix) ||
                    (localAliases.TryGetValue(prefix, out var target) && IsEfCoreNamespace(target)) ||
                    (globalAliases.TryGetValue(prefix, out var gTarget) && IsEfCoreNamespace(gTarget));

                if (isValidPrefix)
                {
                    genericName = rightGeneric;
                }
                break;
            }

            case AliasQualifiedNameSyntax aliasQ when aliasQ.Alias.Identifier.ValueText == "global" && aliasQ.Name is GenericNameSyntax ag:
            {
                genericName = ag;
                break;
            }
        }

        if (genericName is null ||
            !string.Equals(genericName.Identifier.ValueText, "DbSet", StringComparison.Ordinal) ||
            genericName.TypeArgumentList.Arguments.Count != 1)
        {
            return false;
        }

        var typeArgument = genericName.TypeArgumentList.Arguments[0];

        fullTypeName = typeArgument.ToString().Trim();

        entityName = typeArgument switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            _ => fullTypeName
        };

        return !string.IsNullOrWhiteSpace(entityName);
    }

    private static string? GetEnclosingNamespace(BaseTypeDeclarationSyntax declaration)
    {
        var parent = declaration.Parent;
        var segments = new List<string>();

        while (parent is not null)
        {
            if (parent is BaseNamespaceDeclarationSyntax namespaceDeclaration)
            {
                segments.Insert(0, namespaceDeclaration.Name.ToString().Trim());
            }
            else if (parent is ClassDeclarationSyntax enclosingClass)
            {
                segments.Insert(0, enclosingClass.Identifier.ValueText);
            }

            parent = parent.Parent;
        }

        return segments.Count > 0 ? string.Join(".", segments) : null;
    }

    private static bool IsEfCoreNamespace(string name)
    {
        return string.Equals(name, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
               string.Equals(name, "global::Microsoft.EntityFrameworkCore", StringComparison.Ordinal);
    }

    private static bool IsEfCoreDbContextTarget(string target)
    {
        return string.Equals(target, "Microsoft.EntityFrameworkCore.DbContext", StringComparison.Ordinal) ||
               string.Equals(target, "global::Microsoft.EntityFrameworkCore.DbContext", StringComparison.Ordinal);
    }
}
