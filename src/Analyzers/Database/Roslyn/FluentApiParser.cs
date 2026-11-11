using DatabaseAnalyzer.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DatabaseAnalyzer.Roslyn;

internal sealed class FluentApiParser
{
    private readonly List<string> _warnings;
    private readonly Func<string, DiscoveredEntity> _getOrAddEntity;
    private readonly Func<IEnumerable<DiscoveredEntity>>? _getAllEntities;

    public HashSet<string> AppliedConfigurationClassNames { get; } = new(StringComparer.Ordinal);

    public bool HasApplyConfigurationsFromAssembly { get; private set; }

    public FluentApiParser(
        Func<string, DiscoveredEntity> getOrAddEntity,
        List<string> warnings,
        Func<IEnumerable<DiscoveredEntity>>? getAllEntities = null)
    {
        _getOrAddEntity = getOrAddEntity;
        _warnings = warnings;
        _getAllEntities = getAllEntities;
    }

    public void ParseOnModelCreating(
        MethodDeclarationSyntax onModelCreatingMethod,
        LocalMethodTraverser localMethodTraverser)
    {
        var parameter = onModelCreatingMethod.ParameterList.Parameters.FirstOrDefault();
        var modelBuilderParamName = parameter?.Identifier.ValueText ?? "modelBuilder";

        ParseStatements(
            onModelCreatingMethod.Body?.Statements ?? Enumerable.Empty<StatementSyntax>(),
            modelBuilderParamName,
            localMethodTraverser,
            currentDepth: 0);
    }

    public void ParseEntityTypeConfiguration(
        MethodDeclarationSyntax configureMethod,
        string entityTypeName)
    {
        var parameter = configureMethod.ParameterList.Parameters.FirstOrDefault();
        var builderParamName = parameter?.Identifier.ValueText ?? "builder";

        var targetEntity = _getOrAddEntity(entityTypeName);

        var statements = configureMethod.Body?.Statements ??
            (configureMethod.ExpressionBody != null
                ? [SyntaxFactory.ExpressionStatement(configureMethod.ExpressionBody.Expression)]
                : Enumerable.Empty<StatementSyntax>());

        foreach (var statement in statements)
        {
            if (statement is ExpressionStatementSyntax exprStmt &&
                exprStmt.Expression is InvocationExpressionSyntax invocation)
            {
                ProcessEntityBuilderChain(invocation, builderParamName, targetEntity);
            }
        }
    }

    private void ParseStatements(
        IEnumerable<StatementSyntax> statements,
        string modelBuilderParamName,
        LocalMethodTraverser localMethodTraverser,
        int currentDepth)
    {
        foreach (var statement in statements)
        {
            if (statement is not ExpressionStatementSyntax exprStmt ||
                exprStmt.Expression is not InvocationExpressionSyntax invocation)
            {
                continue;
            }

            // 1. Проверяем локальный вспомогательный метод (например, ConfigureUsers(modelBuilder))
            var traversed = localMethodTraverser.TryTraverseLocalMethod(
                invocation,
                currentDepth,
                (targetMethod, nextDepth) =>
                {
                    // Проверяем, какой параметр передаётся:
                    // Если передаётся modelBuilder, связываем с параметром вызываемого метода
                    var methodParam = targetMethod.ParameterList.Parameters.FirstOrDefault();
                    var methodParamName = methodParam?.Identifier.ValueText ?? modelBuilderParamName;

                    // Если вызываемый метод принимает EntityTypeBuilder<T>
                    if (methodParam?.Type is GenericNameSyntax g &&
                        string.Equals(g.Identifier.ValueText, "EntityTypeBuilder", StringComparison.Ordinal) &&
                        g.TypeArgumentList.Arguments.Count == 1)
                    {
                        var entityName = ExtractTypeName(g.TypeArgumentList.Arguments[0]);
                        ParseEntityTypeConfiguration(targetMethod, entityName);
                    }
                    else
                    {
                        var methodStatements = targetMethod.Body?.Statements ??
                            (targetMethod.ExpressionBody != null
                                ? [SyntaxFactory.ExpressionStatement(targetMethod.ExpressionBody.Expression)]
                                : Enumerable.Empty<StatementSyntax>());

                        ParseStatements(methodStatements, methodParamName, localMethodTraverser, nextDepth);
                    }
                });

            if (traversed)
            {
                continue;
            }

            // 2. Разбираем цепочку вызовов на modelBuilder
            ProcessModelBuilderInvocation(invocation, modelBuilderParamName);
        }
    }

    private void ProcessModelBuilderInvocation(
        InvocationExpressionSyntax invocation,
        string modelBuilderParamName)
    {
        var chain = DeconstructInvocationChain(invocation);

        if (chain.Count == 0)
        {
            return;
        }

        var root = chain[0];

        // Игнорируем base.OnModelCreating(modelBuilder)
        if (root.Invocation.Expression is MemberAccessExpressionSyntax m &&
            m.Expression is BaseExpressionSyntax)
        {
            return;
        }

        // Проверяем, что цепочка начинается с обращения к modelBuilder
        if (!IsCallOnReceiver(root.Invocation, modelBuilderParamName))
        {
            return;
        }

        // modelBuilder.Entity<T>() или modelBuilder.Entity(typeof(T))
        if (string.Equals(root.MethodName, "Entity", StringComparison.Ordinal))
        {
            string? entityTypeName = null;

            if (root.GenericTypeArgument is not null)
            {
                entityTypeName = ExtractTypeName(root.GenericTypeArgument);
            }
            else if (root.Arguments.Count > 0 &&
                     root.Arguments[0].Expression is TypeOfExpressionSyntax typeOfExpr)
            {
                entityTypeName = ExtractTypeName(typeOfExpr.Type);
            }

            if (string.IsNullOrWhiteSpace(entityTypeName))
            {
                _warnings.Add($"Не удалось извлечь тип сущности из вызова 'modelBuilder.Entity(...)' в позиции {root.Invocation.GetLocation().GetLineSpan().StartLinePosition}.");
                return;
            }

            var entity = _getOrAddEntity(entityTypeName);

            // Случай А: modelBuilder.Entity<T>(b => { ... })
            if (root.Arguments.Count == 1 &&
                root.Arguments[0].Expression is LambdaExpressionSyntax lambda)
            {
                ProcessEntityLambda(lambda, entity);
            }

            // Случай Б: цепочка вызовов после modelBuilder.Entity<T>().ToTable(...).HasKey(...)
            for (var i = 1; i < chain.Count; i++)
            {
                ProcessEntityBuilderStep(chain, ref i, entity);
            }
        }
        else if (string.Equals(root.MethodName, "ApplyConfiguration", StringComparison.Ordinal))
        {
            if (root.Arguments.Count > 0)
            {
                var argExpr = root.Arguments[0].Expression;
                string? configClassName = null;

                if (argExpr is ObjectCreationExpressionSyntax objCreation)
                {
                    configClassName = ExtractTypeName(objCreation.Type);
                }
                else if (argExpr is ImplicitObjectCreationExpressionSyntax && root.GenericTypeArgument is not null)
                {
                    configClassName = ExtractTypeName(root.GenericTypeArgument);
                }
                else if (argExpr is IdentifierNameSyntax id)
                {
                    configClassName = id.Identifier.ValueText;
                }

                if (!string.IsNullOrWhiteSpace(configClassName))
                {
                    AppliedConfigurationClassNames.Add(configClassName);
                }
            }
        }
        else if (string.Equals(root.MethodName, "ApplyConfigurationsFromAssembly", StringComparison.Ordinal))
        {
            HasApplyConfigurationsFromAssembly = true;
            _warnings.Add("Вызов 'modelBuilder.ApplyConfigurationsFromAssembly(...)' не позволяет статически подтвердить перечень применяемых конфигураций.");
        }
        else
        {
            // Неизвестный метод на modelBuilder
            _warnings.Add($"Вызов метода 'modelBuilder.{root.MethodName}' не поддерживается статическим анализом EF Core.");
        }
    }

    private void ProcessEntityLambda(LambdaExpressionSyntax lambda, DiscoveredEntity entity)
    {
        var lambdaParamName = GetLambdaParameterName(lambda) ?? "b";

        var statements = lambda.Body switch
        {
            BlockSyntax block => block.Statements,
            ExpressionSyntax expr => [SyntaxFactory.ExpressionStatement(expr)],
            _ => Enumerable.Empty<StatementSyntax>()
        };

        foreach (var statement in statements)
        {
            if (statement is ExpressionStatementSyntax exprStmt &&
                exprStmt.Expression is InvocationExpressionSyntax invocation)
            {
                ProcessEntityBuilderChain(invocation, lambdaParamName, entity);
            }
        }
    }

    private void ProcessEntityBuilderChain(
        InvocationExpressionSyntax invocation,
        string builderParamName,
        DiscoveredEntity entity)
    {
        var chain = DeconstructInvocationChain(invocation);

        if (chain.Count == 0)
        {
            return;
        }

        var root = chain[0];

        if (!IsCallOnReceiver(root.Invocation, builderParamName))
        {
            return;
        }

        for (var i = 0; i < chain.Count; i++)
        {
            ProcessEntityBuilderStep(chain, ref i, entity);
        }
    }

    private void ProcessEntityBuilderStep(
        List<ChainStep> chain,
        ref int index,
        DiscoveredEntity entity)
    {
        var step = chain[index];

        switch (step.MethodName)
        {
            case "ToTable":
            {
                if (step.Arguments.Count > 0 &&
                    ExpressionParsingHelper.TryGetStringLiteralOrNameof(step.Arguments[0].Expression, out var tableName) &&
                    tableName is not null)
                {
                    entity.TableName = tableName;
                    entity.IsTableNameConfiguredByFluentApi = true;

                    if (step.Arguments.Count > 1 &&
                        ExpressionParsingHelper.TryGetStringLiteralOrNameof(step.Arguments[1].Expression, out var schema) &&
                        schema is not null)
                    {
                        entity.SchemaName = schema;
                        entity.IsSchemaNameConfiguredByFluentApi = true;
                    }
                }
                break;
            }

            case "HasKey":
            {
                var propertyNames = new List<string>();

                foreach (var arg in step.Arguments)
                {
                    propertyNames.AddRange(ExpressionParsingHelper.ExtractPropertyNames(arg.Expression));
                }

                if (propertyNames.Count > 0)
                {
                    var keyName = $"PK_{entity.TableName ?? entity.EntityName}";
                    entity.SetPrimaryKeyFromFluentApi(new DatabaseKeyMetadata
                    {
                        Name = keyName,
                        PropertyNames = propertyNames,
                        IsPrimaryKey = true
                    });
                }
                break;
            }

            case "HasIndex":
            {
                var propertyNames = new List<string>();

                foreach (var arg in step.Arguments)
                {
                    propertyNames.AddRange(ExpressionParsingHelper.ExtractPropertyNames(arg.Expression));
                }

                if (propertyNames.Count == 0)
                {
                    break;
                }

                var isUnique = false;
                string? customName = null;
                string? filter = null;

                // Заглядываем вперёд по цепочке для сбора модификаторов индекса: .IsUnique(), .HasDatabaseName(), .HasFilter()
                while (index + 1 < chain.Count)
                {
                    var nextStep = chain[index + 1];

                    if (string.Equals(nextStep.MethodName, "IsUnique", StringComparison.Ordinal))
                    {
                        isUnique = true;
                        if (nextStep.Arguments.Count > 0 &&
                            ExpressionParsingHelper.TryGetBooleanLiteral(nextStep.Arguments[0].Expression, out var uVal))
                        {
                            isUnique = uVal;
                        }
                        index++;
                    }
                    else if (string.Equals(nextStep.MethodName, "HasDatabaseName", StringComparison.Ordinal))
                    {
                        if (nextStep.Arguments.Count > 0 &&
                            ExpressionParsingHelper.TryGetStringLiteralOrNameof(nextStep.Arguments[0].Expression, out var nameVal))
                        {
                            customName = nameVal;
                        }
                        index++;
                    }
                    else if (string.Equals(nextStep.MethodName, "HasFilter", StringComparison.Ordinal))
                    {
                        if (nextStep.Arguments.Count > 0 &&
                            ExpressionParsingHelper.TryGetStringLiteralOrNameof(nextStep.Arguments[0].Expression, out var filterVal))
                        {
                            filter = filterVal;
                        }
                        index++;
                    }
                    else
                    {
                        break;
                    }
                }

                var indexName = customName ?? $"IX_{entity.TableName ?? entity.EntityName}_{string.Join("_", propertyNames)}";

                entity.AddIndex(new DatabaseIndexMetadata
                {
                    Name = indexName,
                    PropertyNames = propertyNames,
                    IsUnique = isUnique,
                    Filter = filter
                });
                break;
            }

            case "HasOne":
            case "HasMany":
            {
                ProcessRelationshipChain(chain, ref index, entity, step);
                break;
            }
        }
    }

    private void ProcessRelationshipChain(
        List<ChainStep> chain,
        ref int index,
        DiscoveredEntity entity,
        ChainStep startStep)
    {
        var isStartHasOne = string.Equals(startStep.MethodName, "HasOne", StringComparison.Ordinal);
        string? navigationToOther = null;
        string? otherEntityGenericType = null;

        if (startStep.GenericTypeArgument is not null)
        {
            otherEntityGenericType = ExtractTypeName(startStep.GenericTypeArgument);
        }

        if (startStep.Arguments.Count > 0)
        {
            var props = ExpressionParsingHelper.ExtractPropertyNames(startStep.Arguments[0].Expression);
            navigationToOther = props.FirstOrDefault();
        }

        string? inverseMethod = null;
        string? navigationBack = null;
        string? inverseEntityGenericType = null;
        var fkProperties = new List<string>();
        string? dependentEntityOverride = null;

        while (index + 1 < chain.Count)
        {
            var next = chain[index + 1];

            if (string.Equals(next.MethodName, "WithMany", StringComparison.Ordinal) ||
                string.Equals(next.MethodName, "WithOne", StringComparison.Ordinal))
            {
                inverseMethod = next.MethodName;
                if (next.GenericTypeArgument is not null)
                {
                    inverseEntityGenericType = ExtractTypeName(next.GenericTypeArgument);
                }

                if (next.Arguments.Count > 0)
                {
                    var props = ExpressionParsingHelper.ExtractPropertyNames(next.Arguments[0].Expression);
                    navigationBack = props.FirstOrDefault();
                }
                index++;
            }
            else if (string.Equals(next.MethodName, "HasForeignKey", StringComparison.Ordinal))
            {
                if (next.GenericTypeArgument is not null)
                {
                    dependentEntityOverride = ExtractTypeName(next.GenericTypeArgument);
                }

                foreach (var arg in next.Arguments)
                {
                    fkProperties.AddRange(ExpressionParsingHelper.ExtractPropertyNames(arg.Expression));
                }
                index++;
            }
            else
            {
                break;
            }
        }

        if (fkProperties.Count > 0)
        {
            var otherEntityName = otherEntityGenericType ??
                navigationToOther ??
                (!string.IsNullOrWhiteSpace(navigationBack) ? navigationBack : "Principal");

            DiscoveredEntity targetEntity;
            string principalEntityName;
            string? navToPrincipal;
            string? navToDependent;

            if (!string.IsNullOrWhiteSpace(dependentEntityOverride) &&
                !string.Equals(dependentEntityOverride, entity.EntityName, StringComparison.Ordinal))
            {
                targetEntity = _getOrAddEntity(dependentEntityOverride);
                principalEntityName = entity.EntityName;
                navToPrincipal = navigationBack;
                navToDependent = navigationToOther;
            }
            else if (!isStartHasOne && string.Equals(inverseMethod, "WithOne", StringComparison.Ordinal))
            {
                targetEntity = _getOrAddEntity(otherEntityName);
                principalEntityName = entity.EntityName;
                navToPrincipal = navigationBack;
                navToDependent = navigationToOther;
            }
            else
            {
                targetEntity = entity;
                principalEntityName = otherEntityName;
                navToPrincipal = isStartHasOne ? navigationToOther : navigationBack;
                navToDependent = isStartHasOne ? navigationBack : navigationToOther;
            }

            var fkName = $"FK_{targetEntity.TableName ?? targetEntity.EntityName}_{principalEntityName}_{string.Join("_", fkProperties)}";

            targetEntity.AddForeignKey(new DatabaseForeignKeyMetadata
            {
                Name = fkName,
                PropertyNames = fkProperties,
                PrincipalEntityName = principalEntityName,
                NavigationToPrincipal = navToPrincipal,
                NavigationToDependent = navToDependent
            });
        }
        else if (!isStartHasOne && string.Equals(inverseMethod, "WithMany", StringComparison.Ordinal))
        {
            var otherEntityName = ResolveEntityName(otherEntityGenericType, navigationToOther);

            var isImplicit = true;
            string? explicitJoinTableName = null;

            if (index + 1 < chain.Count && string.Equals(chain[index + 1].MethodName, "UsingEntity", StringComparison.Ordinal))
            {
                var usingStep = chain[index + 1];
                index++;

                if (usingStep.GenericTypeArgument is not null)
                {
                    isImplicit = false;
                    explicitJoinTableName = ExtractTypeName(usingStep.GenericTypeArgument);
                }
                else if (usingStep.Arguments.Count > 0 &&
                         ExpressionParsingHelper.TryGetStringLiteralOrNameof(usingStep.Arguments[0].Expression, out var joinName) &&
                         joinName is not null)
                {
                    explicitJoinTableName = joinName;
                }
            }

            var joinTableName = explicitJoinTableName ??
                (string.Compare(entity.EntityName, otherEntityName, StringComparison.Ordinal) < 0
                    ? entity.EntityName + otherEntityName
                    : otherEntityName + entity.EntityName);

            var otherEntity = _getOrAddEntity(otherEntityName);

            entity.AddSkipNavigation(new DatabaseSkipNavigationMetadata
            {
                NavigationName = navigationToOther ?? otherEntityName,
                TargetEntityName = otherEntityName,
                JoinEntityTableName = joinTableName,
                JoinEntityName = joinTableName,
                IsImplicitJoinEntity = isImplicit,
                InverseNavigationName = navigationBack
            });

            otherEntity.AddSkipNavigation(new DatabaseSkipNavigationMetadata
            {
                NavigationName = navigationBack ?? entity.EntityName,
                TargetEntityName = entity.EntityName,
                JoinEntityTableName = joinTableName,
                JoinEntityName = joinTableName,
                IsImplicitJoinEntity = isImplicit,
                InverseNavigationName = navigationToOther
            });
        }
    }

    private string ResolveEntityName(string? explicitType, string? navigationName)
    {
        if (!string.IsNullOrWhiteSpace(explicitType))
        {
            return explicitType;
        }

        if (string.IsNullOrWhiteSpace(navigationName))
        {
            return "Target";
        }

        if (_getAllEntities is not null)
        {
            var entities = _getAllEntities().ToList();

            // 1. Прямое совпадение с именем сущности
            var direct = entities.FirstOrDefault(e => string.Equals(e.EntityName, navigationName, StringComparison.OrdinalIgnoreCase));
            if (direct is not null)
            {
                return direct.EntityName;
            }

            // 2. Совпадение по имени DbSet (например, Tags -> Tag, Posts -> Post)
            var byDbSet = entities.FirstOrDefault(e => string.Equals(e.DbSetPropertyName, navigationName, StringComparison.OrdinalIgnoreCase));
            if (byDbSet is not null)
            {
                return byDbSet.EntityName;
            }

            // 3. Совпадение по английскому множественному числу (singular: Tags -> Tag)
            if (navigationName.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                var singular = navigationName[..^1];
                var bySingular = entities.FirstOrDefault(e => string.Equals(e.EntityName, singular, StringComparison.OrdinalIgnoreCase));
                if (bySingular is not null)
                {
                    return bySingular.EntityName;
                }
            }
        }

        if (navigationName.EndsWith("s", StringComparison.OrdinalIgnoreCase) && navigationName.Length > 1)
        {
            return navigationName[..^1];
        }

        return navigationName;
    }

    private static bool IsCallOnReceiver(InvocationExpressionSyntax invocation, string receiverName)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is IdentifierNameSyntax id)
            {
                return string.Equals(id.Identifier.ValueText, receiverName, StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static string? GetLambdaParameterName(LambdaExpressionSyntax lambda)
    {
        return lambda switch
        {
            SimpleLambdaExpressionSyntax s => s.Parameter.Identifier.ValueText,
            ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters.FirstOrDefault()?.Identifier.ValueText,
            _ => null
        };
    }

    private static string ExtractTypeName(TypeSyntax typeSyntax)
    {
        return typeSyntax switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            _ => typeSyntax.ToString().Trim()
        };
    }

    private static List<ChainStep> DeconstructInvocationChain(InvocationExpressionSyntax invocation)
    {
        var chain = new List<ChainStep>();
        ExpressionSyntax current = invocation;

        while (current is InvocationExpressionSyntax inv)
        {
            if (inv.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                TypeSyntax? genericArg = null;
                if (memberAccess.Name is GenericNameSyntax g && g.TypeArgumentList.Arguments.Count > 0)
                {
                    genericArg = g.TypeArgumentList.Arguments[0];
                }

                chain.Add(new ChainStep
                {
                    MethodName = memberAccess.Name.Identifier.ValueText,
                    GenericTypeArgument = genericArg,
                    Arguments = inv.ArgumentList.Arguments,
                    Invocation = inv
                });

                current = memberAccess.Expression;
            }
            else
            {
                break;
            }
        }

        chain.Reverse();
        return chain;
    }

    private sealed class ChainStep
    {
        public string MethodName { get; init; } = null!;
        public TypeSyntax? GenericTypeArgument { get; init; }
        public SeparatedSyntaxList<ArgumentSyntax> Arguments { get; init; }
        public InvocationExpressionSyntax Invocation { get; init; } = null!;
    }
}
