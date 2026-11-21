using DatabaseAnalyzer.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DatabaseAnalyzer.Mapping;

public sealed class EfCoreMetadataMapper
{
    public DatabaseMetadata Map(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var entities = model.GetEntityTypes().Select(MapEntity).ToList();

        return new DatabaseMetadata
        {
            Entities = entities
        };
    }

    private static DatabaseEntityMetadata MapEntity(IEntityType entityType)
    {
        var tableName = entityType.GetTableName() ?? entityType.Name;

        var properties = entityType.GetProperties().Select
        (
            property => new DatabasePropertyMetadata
            {
                PropertyName = property.Name,
                ColumnName = property.GetColumnName() ?? property.Name,
                StoreType = property.GetColumnType(),
                IsNullable = property.IsNullable,
                IsPrimaryKey = entityType.FindPrimaryKey()?.Properties.Contains(property) == true,
                IsUnique = false,
                IsGenerated = property.ValueGenerated != ValueGenerated.Never,
                DefaultValueSql = property.GetDefaultValueSql()
            }
        ).ToList();

        var keys = entityType.GetKeys().Select
        (
            key =>
            {
                var propertyNames = key.Properties.Select(property => property.Name).ToList();
                var fallbackName = key.IsPrimaryKey()
                    ? $"PK_{tableName}"
                    : $"AK_{tableName}_{string.Join("_", propertyNames)}";

                return new DatabaseKeyMetadata
                {
                    Name = key.GetName() ?? fallbackName,
                    PropertyNames = propertyNames,
                    IsPrimaryKey = key.IsPrimaryKey()
                };
            }
        ).ToList();

        var foreignKeys = entityType.GetForeignKeys().Select
        (
            foreignKey => new DatabaseForeignKeyMetadata
            {
                Name = foreignKey.GetConstraintName() ?? foreignKey.Properties[0].Name,
                PropertyNames = [.. foreignKey.Properties.Select(property => property.Name)],
                PrincipalEntityName = foreignKey.PrincipalEntityType.Name,
                PrincipalPropertyNames = [.. foreignKey.PrincipalKey.Properties.Select(property => property.Name)],
                DeleteBehavior = foreignKey.DeleteBehavior.ToString(),
                NavigationToPrincipal = foreignKey.DependentToPrincipal?.Name,
                NavigationToDependent = foreignKey.PrincipalToDependent?.Name
            }
        ).ToList();

        var explicitIndexes = entityType.GetIndexes().Select
        (
            index => new DatabaseIndexMetadata
            {
                Name = index.GetDatabaseName() ?? index.Properties.First().Name,
                PropertyNames = [.. index.Properties.Select(property => property.Name)],
                IsUnique = index.IsUnique
            }
        );

        var keyIndexes = entityType.GetKeys().Select
        (
            key =>
            {
                var propertyNames = key.Properties.Select(property => property.Name).ToList();
                var fallbackName = key.IsPrimaryKey()
                    ? $"PK_{tableName}"
                    : $"AK_{tableName}_{string.Join("_", propertyNames)}";

                return new DatabaseIndexMetadata
                {
                    Name = key.GetName() ?? fallbackName,
                    PropertyNames = propertyNames,
                    IsUnique = true
                };
            }
        );

        var indexes = explicitIndexes.Concat(keyIndexes).ToList();

        var relationships = entityType.GetForeignKeys().Select
        (
            foreignKey => new DatabaseRelationshipMetadata
            {
                PrincipalEntityName = foreignKey.PrincipalEntityType.Name,
                DependentEntityName = entityType.Name,
                RelationshipType = GetRelationshipType(foreignKey),
                NavigationToPrincipal = foreignKey.DependentToPrincipal?.Name,
                NavigationToDependent = foreignKey.PrincipalToDependent?.Name
            }
        ).ToList();

        var skipNavigations = entityType.GetSkipNavigations()
            .Where(IsDeclaredOrExplicitSkipNavigation)
            .Select
            (
                skipNav =>
                {
                    var joinEntity = skipNav.JoinEntityType;
                    var isImplicit = IsImplicitJoinEntity(skipNav);
                    var inverseName = GetInverseNavigationName(skipNav);

                    return new DatabaseSkipNavigationMetadata
                    {
                        NavigationName = skipNav.Name,
                        TargetEntityName = skipNav.TargetEntityType.Name,
                        JoinEntityTableName = joinEntity.GetTableName() ?? joinEntity.Name,
                        JoinEntityName = joinEntity.Name,
                        IsImplicitJoinEntity = isImplicit,
                        InverseNavigationName = inverseName
                    };
                }
            ).ToList();

        return new DatabaseEntityMetadata
        {
            EntityName = entityType.Name,
            TableName = tableName,
            SchemaName = entityType.GetSchema(),
            Properties = properties,
            Keys = keys,
            AlternateKeys = keys.Where(k => !k.IsPrimaryKey).ToList(),
            ForeignKeys = foreignKeys,
            Indexes = indexes,
            Relationships = relationships,
            SkipNavigations = skipNavigations
        };
    }

    private static string GetRelationshipType(IForeignKey foreignKey)
    {
        if (foreignKey.IsOwnership)
        {
            return "Ownership";
        }

        if (foreignKey.IsUnique)
        {
            return "OneToOne";
        }

        var dependentNavigation = foreignKey.DependentToPrincipal;
        var principalNavigation = foreignKey.PrincipalToDependent;

        if (principalNavigation?.IsCollection == true)
        {
            return "OneToMany";
        }

        if (dependentNavigation is not null)
        {
            return "ManyToOne";
        }

        if (principalNavigation is not null)
        {
            return "OneToMany";
        }

        return "ManyToOne";
    }

    private static bool IsDeclaredOrExplicitSkipNavigation(ISkipNavigation skipNav)
    {
        if (!skipNav.IsShadowProperty())
        {
            return true;
        }

        if (skipNav.FieldInfo != null || skipNav.PropertyInfo != null)
        {
            return true;
        }

        // В EF Core для однонаправленных связей (HasMany().WithMany()) на целевой сущности создаётся
        // синтетическая теневая навигация с именем TargetEntityType.ShortName().
        // В скомпилированной RuntimeModel вызовы .WithMany() и .WithMany("TargetShortName") порождают
        // идентичную метамодель. Синтетическая навигация с таким именем исключается из метаданных целевой сущности.
        return !string.Equals(skipNav.Name, skipNav.TargetEntityType.ShortName(), StringComparison.Ordinal);
    }

    private static string? GetInverseNavigationName(ISkipNavigation skipNav)
    {
        var inverse = skipNav.Inverse;
        if (inverse == null)
        {
            return null;
        }

        if (inverse.IsShadowProperty() &&
            string.Equals(inverse.Name, inverse.TargetEntityType.ShortName(), StringComparison.Ordinal))
        {
            return null;
        }

        return inverse.Name;
    }

    private static bool IsImplicitJoinEntity(ISkipNavigation skipNav)
    {
        return skipNav.JoinEntityType.ClrType == typeof(Dictionary<string, object>);
    }
}
