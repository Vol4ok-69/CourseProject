using DatabaseAnalyzer.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DatabaseAnalyzer.Roslyn;

internal sealed class DataAnnotationParser
{
    private readonly bool _isEfCoreGloballyImported;
    private readonly bool _isDataAnnotationsGloballyImported;
    private readonly bool _isSchemaGloballyImported;
    private readonly IReadOnlyDictionary<string, string> _globalAliases;
    private readonly List<string> _warnings;
    private readonly IReadOnlyList<ClassCandidate> _allCandidates;
    private readonly HashSet<string> _globalImportedNamespaces;
    private readonly HashSet<string> _allInterfaceNames;

    public DataAnnotationParser(
        bool isEfCoreGloballyImported,
        bool isDataAnnotationsGloballyImported,
        bool isSchemaGloballyImported,
        IReadOnlyDictionary<string, string> globalAliases,
        List<string> warnings,
        IReadOnlyList<ClassCandidate>? allCandidates = null,
        HashSet<string>? globalImportedNamespaces = null,
        HashSet<string>? allInterfaceNames = null)
    {
        _isEfCoreGloballyImported = isEfCoreGloballyImported;
        _isDataAnnotationsGloballyImported = isDataAnnotationsGloballyImported;
        _isSchemaGloballyImported = isSchemaGloballyImported;
        _globalAliases = globalAliases;
        _warnings = warnings;
        _allCandidates = allCandidates ?? [];
        _globalImportedNamespaces = globalImportedNamespaces ?? [];
        _allInterfaceNames = allInterfaceNames ?? [];
    }

    public void ParseEntityAnnotations(
        ClassDeclarationSyntax classDeclaration,
        ParsedFileSyntax fileSyntax,
        DiscoveredEntity entity)
    {
        foreach (var attributeList in classDeclaration.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var attrName = GetSimpleAttributeName(attribute);

                if (attrName is "Table" or "TableAttribute")
                {
                    if (IsTableAttribute(attribute, fileSyntax))
                    {
                        ParseTableAttribute(attribute, entity);
                    }
                    else
                    {
                        _warnings.Add($"Атрибут '[{attribute.Name}]' класса '{entity.EntityName}' пропущен: не подтверждена принадлежность к System.ComponentModel.DataAnnotations.Schema.");
                    }
                }
                else if (attrName is "Index" or "IndexAttribute")
                {
                    if (IsEfCoreIndexAttribute(attribute, fileSyntax))
                    {
                        ParseIndexAttribute(attribute, entity);
                    }
                    else
                    {
                        _warnings.Add($"Атрибут '[{attribute.Name}]' класса '{entity.EntityName}' пропущен: не подтверждена принадлежность к Microsoft.EntityFrameworkCore.");
                    }
                }
                else if (attrName is "Keyless" or "KeylessAttribute")
                {
                    entity.KeyType = EntityKeyType.Keyless;
                }
                else if (attrName is "NotMapped" or "NotMappedAttribute")
                {
                    entity.MappingType = EntityMappingType.Unmapped;
                }
            }
        }

        // Парсинг атрибутов свойств класса.
        var primaryKeyProperties = new List<string>();

        foreach (var property in classDeclaration.Members.OfType<PropertyDeclarationSyntax>())
        {
            var propName = property.Identifier.ValueText;
            var isNullable = property.Type is NullableTypeSyntax;
            var isKey = false;
            var isRequired = false;
            var isNotMapped = false;

            foreach (var attributeList in property.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    var attrName = GetSimpleAttributeName(attribute);

                    if (attrName is "NotMapped" or "NotMappedAttribute")
                    {
                        isNotMapped = true;
                    }
                    else if (attrName is "Key" or "KeyAttribute")
                    {
                        if (IsKeyAttribute(attribute, fileSyntax))
                        {
                            isKey = true;
                            primaryKeyProperties.Add(propName);
                        }
                        else
                        {
                            _warnings.Add($"Атрибут '[{attribute.Name}]' свойства '{entity.EntityName}.{propName}' пропущен: не подтверждена принадлежность к System.ComponentModel.DataAnnotations.");
                        }
                    }
                    else if (attrName is "Required" or "RequiredAttribute")
                    {
                        if (IsRequiredAttribute(attribute, fileSyntax))
                        {
                            isRequired = true;
                            isNullable = false;
                        }
                        else
                        {
                            _warnings.Add($"Атрибут '[{attribute.Name}]' свойства '{entity.EntityName}.{propName}' пропущен: не подтверждена принадлежность к System.ComponentModel.DataAnnotations.");
                        }
                    }
                    else if (attrName is "ForeignKey" or "ForeignKeyAttribute")
                    {
                        if (IsForeignKeyAttribute(attribute, fileSyntax))
                        {
                            ParseForeignKeyAttribute(attribute, propName, property, entity);
                        }
                        else
                        {
                            _warnings.Add($"Атрибут '[{attribute.Name}]' свойства '{entity.EntityName}.{propName}' пропущен: не подтверждена принадлежность к System.ComponentModel.DataAnnotations.Schema.");
                        }
                    }
                }
            }

            if (isNotMapped)
            {
                continue;
            }

            var propMetadata = new DatabasePropertyMetadata
            {
                PropertyName = propName,
                ColumnName = propName,
                IsNullable = isNullable && !isRequired,
                IsPrimaryKey = isKey
            };

            entity.AddOrUpdateProperty(propMetadata);
        }

        // Обход базовых классов для извлечения унаследованных свойств и первичных ключей (DEF-004)
        TraverseBaseClasses(classDeclaration, fileSyntax, entity, primaryKeyProperties);

        if (primaryKeyProperties.Count > 0)
        {
            var keyName = $"PK_{entity.TableName ?? entity.EntityName}";
            entity.AddKey(new DatabaseKeyMetadata
            {
                Name = keyName,
                PropertyNames = primaryKeyProperties,
                IsPrimaryKey = true
            });
        }
    }

    private void TraverseBaseClasses(
        ClassDeclarationSyntax classDeclaration,
        ParsedFileSyntax fileSyntax,
        DiscoveredEntity entity,
        List<string> primaryKeyProperties)
    {
        var visited = new HashSet<ClassDeclarationSyntax> { classDeclaration };
        TraverseBaseClassesRecursive(classDeclaration, fileSyntax, entity, primaryKeyProperties, visited, depth: 1);
    }

    private void TraverseBaseClassesRecursive(
        ClassDeclarationSyntax classDeclaration,
        ParsedFileSyntax fileSyntax,
        DiscoveredEntity entity,
        List<string> primaryKeyProperties,
        HashSet<ClassDeclarationSyntax> visited,
        int depth)
    {
        if (depth > 5 || classDeclaration.BaseList is null || classDeclaration.BaseList.Types.Count == 0)
        {
            return;
        }

        foreach (var baseType in classDeclaration.BaseList.Types)
        {
            var baseTypeName = ExtractSimpleTypeName(baseType.Type);

            if (string.IsNullOrWhiteSpace(baseTypeName) ||
                string.Equals(baseTypeName, "object", StringComparison.Ordinal) ||
                _allInterfaceNames.Contains(baseTypeName) ||
                (baseTypeName.StartsWith("I", StringComparison.Ordinal) && baseTypeName.Length > 1 && char.IsUpper(baseTypeName[1])))
            {
                // Пропускаем object, подтверждённые интерфейсы проекта и вероятные интерфейсы
                continue;
            }

            var candidates = _allCandidates
                .Where(c => string.Equals(c.ClassName, baseTypeName, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
            {
                _warnings.Add($"Базовый тип '{baseTypeName}' сущности '{entity.EntityName}' не найден среди анализируемых исходников; унаследованные свойства и ключи не могут быть извлечены.");
                entity.HasIncompleteMetadata = true;
                entity.HasIncompleteKeys = true;
                continue;
            }

            ClassCandidate? resolvedCandidate;

            if (candidates.Count == 1)
            {
                resolvedCandidate = candidates[0];
            }
            else
            {
                var enclosingNamespace = fileSyntax.Classes.FirstOrDefault(c => c.SyntaxNode == classDeclaration)?.Namespace;

                resolvedCandidate = candidates.FirstOrDefault(c => string.Equals(c.Namespace, enclosingNamespace, StringComparison.Ordinal))
                    ?? candidates.FirstOrDefault(c => c.Namespace is not null && fileSyntax.ImportedNamespaces.Contains(c.Namespace))
                    ?? candidates.FirstOrDefault(c => c.Namespace is not null && _globalImportedNamespaces.Contains(c.Namespace));

                if (resolvedCandidate is null)
                {
                    _warnings.Add($"Не удалось однозначно разрешить базовый тип '{baseTypeName}' сущности '{entity.EntityName}': обнаружено несколько определений в разных пространствах имён ({string.Join(", ", candidates.Select(c => c.Namespace))}).");
                    entity.HasIncompleteMetadata = true;
                    entity.HasIncompleteKeys = true;
                    continue;
                }
            }

            if (!visited.Add(resolvedCandidate.SyntaxNode))
            {
                _warnings.Add($"Обнаружен цикл в наследовании базового класса '{baseTypeName}' сущности '{entity.EntityName}'. Обход прерван.");
                entity.HasIncompleteMetadata = true;
                continue;
            }

            entity.IsDerivedEntity = true;
            entity.BaseEntityName ??= baseTypeName;

            foreach (var property in resolvedCandidate.SyntaxNode.Members.OfType<PropertyDeclarationSyntax>())
            {
                var propName = property.Identifier.ValueText;

                if (entity.Properties.Any(p => string.Equals(p.PropertyName, propName, StringComparison.Ordinal)))
                {
                    continue;
                }

                var isNullable = property.Type is NullableTypeSyntax;
                var isKey = false;
                var isRequired = false;
                var isNotMapped = false;

                foreach (var attributeList in property.AttributeLists)
                {
                    foreach (var attribute in attributeList.Attributes)
                    {
                        var attrName = GetSimpleAttributeName(attribute);

                        if (attrName is "NotMapped" or "NotMappedAttribute")
                        {
                            isNotMapped = true;
                        }
                        else if (attrName is "Key" or "KeyAttribute" && IsKeyAttribute(attribute, resolvedCandidate.FileSyntax))
                        {
                            isKey = true;
                            if (!primaryKeyProperties.Contains(propName))
                            {
                                primaryKeyProperties.Add(propName);
                            }
                        }
                        else if (attrName is "Required" or "RequiredAttribute" && IsRequiredAttribute(attribute, resolvedCandidate.FileSyntax))
                        {
                            isRequired = true;
                            isNullable = false;
                        }
                        else if (attrName is "ForeignKey" or "ForeignKeyAttribute" && IsForeignKeyAttribute(attribute, resolvedCandidate.FileSyntax))
                        {
                            ParseForeignKeyAttribute(attribute, propName, property, entity);
                        }
                    }
                }

                if (isNotMapped)
            {
                continue;
            }

            var propMetadata = new DatabasePropertyMetadata
                {
                    PropertyName = propName,
                    ColumnName = propName,
                    IsNullable = isNullable && !isRequired,
                    IsPrimaryKey = isKey
                };

                entity.AddOrUpdateProperty(propMetadata);
            }

            TraverseBaseClassesRecursive(resolvedCandidate.SyntaxNode, resolvedCandidate.FileSyntax, entity, primaryKeyProperties, visited, depth + 1);
        }
    }

    private static string ExtractSimpleTypeName(TypeSyntax typeSyntax)
    {
        return typeSyntax switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            _ => typeSyntax.ToString().Trim()
        };
    }

    private void ParseTableAttribute(AttributeSyntax attribute, DiscoveredEntity entity)
    {
        if (attribute.ArgumentList is null || attribute.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        var firstArg = attribute.ArgumentList.Arguments[0];
        if (!entity.IsTableNameConfiguredByFluentApi &&
            ExpressionParsingHelper.TryGetStringLiteralOrNameof(firstArg.Expression, out var tableName) && tableName is not null)
        {
            entity.TableName = tableName;
        }

        if (!entity.IsSchemaNameConfiguredByFluentApi)
        {
            foreach (var arg in attribute.ArgumentList.Arguments)
            {
                if (arg.NameColon is not null && string.Equals(arg.NameColon.Name.Identifier.ValueText, "Schema", StringComparison.OrdinalIgnoreCase))
                {
                    if (ExpressionParsingHelper.TryGetStringLiteralOrNameof(arg.Expression, out var schema))
                    {
                        entity.SchemaName = schema;
                    }
                }
                else if (arg.NameEquals is not null && string.Equals(arg.NameEquals.Name.Identifier.ValueText, "Schema", StringComparison.OrdinalIgnoreCase))
                {
                    if (ExpressionParsingHelper.TryGetStringLiteralOrNameof(arg.Expression, out var schema))
                    {
                        entity.SchemaName = schema;
                    }
                }
            }

            if (entity.SchemaName is null && attribute.ArgumentList.Arguments.Count > 1)
            {
                var secondArg = attribute.ArgumentList.Arguments[1];
                if (secondArg.NameColon is null && secondArg.NameEquals is null)
                {
                    if (ExpressionParsingHelper.TryGetStringLiteralOrNameof(secondArg.Expression, out var schema))
                    {
                        entity.SchemaName = schema;
                    }
                }
            }
        }
    }

    private static void ParseIndexAttribute(AttributeSyntax attribute, DiscoveredEntity entity)
    {
        if (attribute.ArgumentList is null || attribute.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        var propertyNames = new List<string>();
        var isUnique = false;
        string? customIndexName = null;

        foreach (var argument in attribute.ArgumentList.Arguments)
        {
            if (argument.NameColon is not null || argument.NameEquals is not null)
            {
                var name = argument.NameColon?.Name.Identifier.ValueText ?? argument.NameEquals?.Name.Identifier.ValueText;

                if (string.Equals(name, "IsUnique", StringComparison.OrdinalIgnoreCase))
                {
                    if (ExpressionParsingHelper.TryGetBooleanLiteral(argument.Expression, out var uniqueVal))
                    {
                        isUnique = uniqueVal;
                    }
                }
                else if (string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase))
                {
                    if (ExpressionParsingHelper.TryGetStringLiteralOrNameof(argument.Expression, out var indexName))
                    {
                        customIndexName = indexName;
                    }
                }
            }
            else
            {
                if (ExpressionParsingHelper.TryGetStringLiteralOrNameof(argument.Expression, out var prop) && prop is not null)
                {
                    propertyNames.Add(prop);
                }
            }
        }

        if (propertyNames.Count > 0)
        {
            var indexName = customIndexName ?? $"IX_{entity.TableName ?? entity.EntityName}_{string.Join("_", propertyNames)}";

            entity.AddIndex(new DatabaseIndexMetadata
            {
                Name = indexName,
                PropertyNames = propertyNames,
                IsUnique = isUnique
            });
        }
    }

    private static void ParseForeignKeyAttribute(
        AttributeSyntax attribute,
        string currentPropertyName,
        PropertyDeclarationSyntax propertySyntax,
        DiscoveredEntity entity)
    {
        if (attribute.ArgumentList is null || attribute.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        var firstArg = attribute.ArgumentList.Arguments[0];
        if (!ExpressionParsingHelper.TryGetStringLiteralOrNameof(firstArg.Expression, out var relatedName) || string.IsNullOrWhiteSpace(relatedName))
        {
            return;
        }

        var propType = propertySyntax.Type.ToString().Trim().TrimEnd('?');

        // В EF Core атрибут [ForeignKey] может висеть:
        // 1. На навигационном свойстве: [ForeignKey("UserId")] public User User { get; set; }
        // 2. На свойстве внешнего ключа: [ForeignKey("User")] public int UserId { get; set; }
        string fkPropertyName;
        string navigationToPrincipal;
        string principalEntityName;

        if (IsPrimitiveOrSimpleType(propType))
        {
            // Висит на столбце внешнего ключа (например, UserId).
            fkPropertyName = currentPropertyName;
            navigationToPrincipal = relatedName;
            principalEntityName = relatedName;
        }
        else
        {
            // Висит на навигационном свойстве (например, User).
            fkPropertyName = relatedName;
            navigationToPrincipal = currentPropertyName;
            principalEntityName = propType;
        }

        var fkName = $"FK_{entity.TableName ?? entity.EntityName}_{principalEntityName}_{fkPropertyName}";

        entity.AddForeignKey(new DatabaseForeignKeyMetadata
        {
            Name = fkName,
            PropertyNames = [fkPropertyName],
            PrincipalEntityName = principalEntityName,
            NavigationToPrincipal = navigationToPrincipal
        });
    }

    private static bool IsPrimitiveOrSimpleType(string typeName)
    {
        return typeName switch
        {
            "int" or "long" or "short" or "byte" or "Guid" or "string" or
            "Int32" or "Int64" or "Int16" or "Byte" or "DateTime" or "DateTimeOffset" => true,
            _ => false
        };
    }

    private static string GetSimpleAttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Trim();
        var dotIndex = name.LastIndexOf('.');
        return dotIndex >= 0 ? name[(dotIndex + 1)..] : name;
    }

    private bool IsTableAttribute(AttributeSyntax attribute, ParsedFileSyntax fileSyntax)
    {
        var full = attribute.Name.ToString().Trim();

        if (full.StartsWith("System.ComponentModel.DataAnnotations.Schema", StringComparison.Ordinal) ||
            full.StartsWith("global::System.ComponentModel.DataAnnotations.Schema", StringComparison.Ordinal))
        {
            return true;
        }

        return fileSyntax.HasSchemaUsing || _isSchemaGloballyImported;
    }

    private bool IsEfCoreIndexAttribute(AttributeSyntax attribute, ParsedFileSyntax fileSyntax)
    {
        var full = attribute.Name.ToString().Trim();

        if (full.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            full.StartsWith("global::Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
        {
            return true;
        }

        return fileSyntax.HasEfCoreUsing || _isEfCoreGloballyImported;
    }

    private bool IsKeyAttribute(AttributeSyntax attribute, ParsedFileSyntax fileSyntax)
    {
        var full = attribute.Name.ToString().Trim();

        if (full.StartsWith("System.ComponentModel.DataAnnotations", StringComparison.Ordinal) ||
            full.StartsWith("global::System.ComponentModel.DataAnnotations", StringComparison.Ordinal))
        {
            return true;
        }

        return fileSyntax.HasDataAnnotationsUsing || _isDataAnnotationsGloballyImported;
    }

    private bool IsRequiredAttribute(AttributeSyntax attribute, ParsedFileSyntax fileSyntax)
    {
        return IsKeyAttribute(attribute, fileSyntax);
    }

    private bool IsForeignKeyAttribute(AttributeSyntax attribute, ParsedFileSyntax fileSyntax)
    {
        return IsTableAttribute(attribute, fileSyntax);
    }
}
