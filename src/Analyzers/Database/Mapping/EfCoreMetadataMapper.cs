using Database.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Database.Mapping;

public sealed class EfCoreMetadataMapper
{
    public DatabaseMetadata Map(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var entities = model
            .GetEntityTypes()
            .Select(MapEntity)
            .ToList();

        return new DatabaseMetadata
        {
            Entities = entities
        };
    }

    private static DatabaseEntityMetadata MapEntity(IEntityType entityType)
    {
        var properties = entityType
            .GetProperties()
            .Select(property => new DatabasePropertyMetadata
            {
                PropertyName = property.Name,
                ColumnName = property.GetColumnName() ?? property.Name,
                StoreType = property.GetColumnType(),
                IsNullable = property.IsNullable,
                IsPrimaryKey = entityType.FindPrimaryKey()?.Properties.Contains(property) == true,
                IsUnique = false,
                IsGenerated = property.ValueGenerated != ValueGenerated.Never,
                DefaultValueSql = property.GetDefaultValueSql()
            })
            .ToList();

        var keys = entityType
            .GetKeys()
            .Select(key => new DatabaseKeyMetadata
            {
                Name = key.GetName() ?? key.Properties.First().Name,
                PropertyNames = key.Properties
                    .Select(property => property.Name)
                    .ToList(),
                IsPrimaryKey = key.IsPrimaryKey()
            })
            .ToList();

        var foreignKeys = entityType
            .GetForeignKeys()
            .Select(foreignKey => new DatabaseForeignKeyMetadata
            {
                Name = foreignKey.GetConstraintName() ?? foreignKey.Properties.First().Name,
                PropertyNames = foreignKey.Properties
                    .Select(property => property.Name)
                    .ToList(),
                PrincipalEntityName = foreignKey.PrincipalEntityType.Name,
                PrincipalPropertyNames = foreignKey.PrincipalKey.Properties
                    .Select(property => property.Name)
                    .ToList(),
                DeleteBehavior = foreignKey.DeleteBehavior.ToString()
            })
            .ToList();

        var indexes = entityType
            .GetIndexes()
            .Select(index => new DatabaseIndexMetadata
            {
                Name = index.GetDatabaseName() ?? index.Properties.First().Name,
                PropertyNames = index.Properties
                    .Select(property => property.Name)
                    .ToList(),
                IsUnique = index.IsUnique
            })
            .ToList();

        var relationships = entityType
            .GetForeignKeys()
            .Select(foreignKey => new DatabaseRelationshipMetadata
            {
                PrincipalEntityName = foreignKey.PrincipalEntityType.Name,
                DependentEntityName = entityType.Name,
                RelationshipType = GetRelationshipType(foreignKey),
                NavigationToPrincipal = foreignKey.DependentToPrincipal?.Name,
                NavigationToDependent = foreignKey.PrincipalToDependent?.Name
            })
            .ToList();

        return new DatabaseEntityMetadata
        {
            EntityName = entityType.Name,
            TableName = entityType.GetTableName() ?? entityType.Name,
            SchemaName = entityType.GetSchema(),
            Properties = properties,
            Keys = keys,
            ForeignKeys = foreignKeys,
            Indexes = indexes,
            Relationships = relationships
        };
    }

    private static string GetRelationshipType(IForeignKey foreignKey)
    {
        if (foreignKey.IsOwnership)
        {
            return "Ownership";
        }

        var dependentNavigation = foreignKey.DependentToPrincipal;
        var principalNavigation = foreignKey.PrincipalToDependent;

        if (dependentNavigation is not null && principalNavigation is not null)
        {
            return "ManyToMany";
        }

        if (principalNavigation is not null)
        {
            return "OneToMany";
        }

        return "ManyToOne";
    }
}