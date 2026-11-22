using NetScope.DatabaseAnalyzer.Runner.Contracts;
using NetScope.DatabaseAnalyzer.Runner.Rules;
using DatabaseAnalyzer.Contracts;
using DatabaseAnalyzer.Roslyn;
using DatabaseAnalyzer.Runner;

namespace DatabaseAnalyzer.Runner.Tests;

public sealed class DatabaseRulesEngineTests
{
    private readonly DatabaseRulesEngine _engine = new();

    private static AnalysisInputContract CreateBaseInput()
    {
        return new AnalysisInputContract
        {
            SchemaVersion = "1.0",
            AnalysisId = 42,
            ExecutionToken = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            MetadataCapabilities =
            [
                "CoreRelational",
                "KeylessEntities",
                "ViewMapping",
                "SqlQueryMapping",
                "FunctionMapping",
                "FilteredIndexes",
                "ImplicitJoinEntities",
                "ShadowProperties",
                "InheritanceHierarchy",
                "SharedTypeEntities",
                "CompositeKeys",
                "CompositeForeignKeys"
            ],
            Configuration = new ConfigurationDto
            {
                EnabledRules = ["DB01", "DB02", "DB03", "DB04", "DB05"],
                MaxFindings = 1000
            },
            Metadata = new MetadataDto
            {
                DbContexts =
                [
                    new DbContextDto
                    {
                        Name = "AppDbContext",
                        ContextId = "AppDbContext",
                        Entities = []
                    }
                ]
            }
        };
    }

    #region DB01: Primary Key Required

    [Fact]
    public void DB01_Positive_TableWithoutPrimaryKey_ProducesFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Order",
            TableName = "Orders",
            MappingType = "Table",
            KeyType = "Unknown",
            Properties =
            [
                new PropertyDto { Name = "CustomerName", ColumnName = "CustomerName" }
            ],
            Keys = []
        });

        var output = _engine.Evaluate(input);

        Assert.Equal("Success", output.Status);
        Assert.Single(output.Findings);
        var finding = output.Findings[0];
        Assert.Equal("DB01", finding.RuleId);
        Assert.Equal("PrimaryKeyRequired", finding.RuleName);
        Assert.Equal("Order", finding.TargetEntityName);
        Assert.Equal("Orders", finding.TargetTable);
    }

    [Fact]
    public void DB01_Negative_TableWithPrimaryKey_NoFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Order",
            TableName = "Orders",
            MappingType = "Table",
            KeyType = "PrimaryKey",
            Properties =
            [
                new PropertyDto { Name = "Id", ColumnName = "Id", IsPrimaryKey = true }
            ],
            Keys =
            [
                new KeyDto { Properties = ["Id"], IsPrimaryKey = true }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    [Fact]
    public void DB01_Negative_KeylessEntity_NoFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderSummary",
            TableName = "OrderSummaries",
            MappingType = "Table",
            KeyType = "Keyless",
            Properties =
            [
                new PropertyDto { Name = "Total", ColumnName = "Total" }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    [Fact]
    public void DB01_Negative_ViewAndFunctionMapping_NoFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderView",
            MappingType = "View",
            ViewName = "v_Orders",
            Properties = [new PropertyDto { Name = "Count", ColumnName = "Count" }]
        });
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "GetMetrics",
            MappingType = "Function",
            FunctionName = "fn_Metrics",
            Properties = [new PropertyDto { Name = "Value", ColumnName = "Value" }]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    [Fact]
    public void DB01_Negative_DerivedEntityInHierarchy_NoFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "SpecialOrder",
            MappingType = "Table",
            IsDerivedEntity = true,
            BaseEntityName = "Order",
            Properties = [new PropertyDto { Name = "Discount", ColumnName = "Discount" }]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    [Fact]
    public void DB01_IncompleteMetadata_SkipsWithDiagnostic()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "PartialOrder",
            MappingType = "Table",
            HasIncompleteMetadata = true,
            Properties = []
        });

        var output = _engine.Evaluate(input);

        Assert.Equal("PartialSuccess", output.Status);
        Assert.Empty(output.Findings);
        Assert.Contains(output.Diagnostics, d => d.Code == "INCOMPLETE_METADATA_SKIPPED" && d.RuleId == "DB01");
    }

    #endregion

    #region DB02: Unidirectional Relationship

    [Fact]
    public void DB02_Scenario1_BidirectionalRelationship_NoFinding()
    {
        // На обеих сторонах есть навигация -> норма, finding не должен создаваться
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderItem",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "Order",
                    Properties = ["OrderId"],
                    PrincipalProperties = ["Id"],
                    NavigationToPrincipal = "Order",
                    NavigationToDependent = "Items",
                    IsNavigationResolved = true
                }
            ],
            Indexes = [new IndexDto { Name = "IX_Order", Properties = ["OrderId"] }]
        });

        var output = _engine.Evaluate(input);

        Assert.DoesNotContain(output.Findings, f => f.RuleId == "DB02");
    }

    [Fact]
    public void DB02_Scenario2_NavigationToPrincipalExists_EvenWithoutDependentNavigation_NoFinding()
    {
        // Навигация к главной сущности (Order) присутствует, обратной навигации нет -> по соглашению проекта норма
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderItem",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "Order",
                    Properties = ["OrderId"],
                    PrincipalProperties = ["Id"],
                    NavigationToPrincipal = "Order",
                    NavigationToDependent = null, // Нет навигации на главной сущности
                    IsNavigationResolved = true
                }
            ],
            Indexes = [new IndexDto { Name = "IX_Order", Properties = ["OrderId"] }]
        });

        var output = _engine.Evaluate(input);

        Assert.DoesNotContain(output.Findings, f => f.RuleId == "DB02");
    }

    [Fact]
    public void DB02_Scenario3_Unidirectional_NavigationOnlyToDependent_ProducesFinding()
    {
        // Навигация только на главной сущности (Items в Order), но нет свойства Order в OrderItem
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderItem",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "Order",
                    Properties = ["OrderId"],
                    PrincipalProperties = ["Id"],
                    NavigationToPrincipal = null,
                    NavigationToDependent = "Items",
                    IsNavigationResolved = true
                }
            ],
            Indexes = [new IndexDto { Name = "IX_Order", Properties = ["OrderId"] }]
        });

        var output = _engine.Evaluate(input);

        var finding = Assert.Single(output.Findings, f => f.RuleId == "DB02");
        Assert.Equal("UnidirectionalRelationship", finding.RuleName);
        Assert.Contains("не имеет навигационного свойства к главной сущности", finding.Message);
    }

    [Fact]
    public void DB02_Scenario4_NoNavigationsOnEitherSide_ProducesFinding()
    {
        // Связь без навигационных свойств с обеих сторон (теневая связь)
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderItem",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "Order",
                    Properties = ["OrderId"],
                    PrincipalProperties = ["Id"],
                    NavigationToPrincipal = null,
                    NavigationToDependent = null,
                    IsNavigationResolved = true
                }
            ],
            Indexes = [new IndexDto { Name = "IX_Order", Properties = ["OrderId"] }]
        });

        var output = _engine.Evaluate(input);

        var finding = Assert.Single(output.Findings, f => f.RuleId == "DB02");
        Assert.Equal("UnidirectionalRelationship", finding.RuleName);
        Assert.Contains("навигационные свойства отсутствуют с обеих сторон", finding.Message);
    }

    [Fact]
    public void DB02_Scenario5_UnresolvedNavigation_SkipsWithDiagnostic()
    {
        // Неизвестная/неразрешённая навигация не трактуется как отсутствие
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderItem",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "Order",
                    Properties = ["OrderId"],
                    PrincipalProperties = ["Id"],
                    NavigationToPrincipal = null,
                    IsNavigationResolved = false // Не удалось определить
                }
            ],
            Indexes = [new IndexDto { Name = "IX_Order", Properties = ["OrderId"] }]
        });

        var output = _engine.Evaluate(input);

        Assert.Equal("PartialSuccess", output.Status);
        Assert.DoesNotContain(output.Findings, f => f.RuleId == "DB02");
        Assert.Contains(output.Diagnostics, d => d.Code == "UNRESOLVED_NAVIGATION_SKIPPED" && d.RuleId == "DB02");
    }

    #endregion

    #region DB03: Many-to-Many Pattern Detection

    [Fact]
    public void DB03_SingleRelationship_ProducesFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Author",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "Books",
                    TargetEntityName = "Book",
                    JoinEntityName = "AuthorBook"
                }
            ]
        });

        var output = _engine.Evaluate(input);

        var db03 = Assert.Single(output.Findings, f => f.RuleId == "DB03");
        Assert.Equal("ManyToManyPattern", db03.RuleName);
    }

    [Fact]
    public void DB03_ReciprocalRepresentation_IsDeduplicated()
    {
        // Обратное представление той же самой связи при наличии стабильных идентификаторов не дублирует finding
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Author",
            EntityId = "Author",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "Books",
                    TargetEntityName = "Book",
                    TargetEntityId = "Book",
                    JoinEntityName = "AuthorBook",
                    JoinEntityId = "AuthorBook",
                    InverseNavigationPropertyName = "Authors"
                }
            ]
        });
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Book",
            EntityId = "Book",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "Authors",
                    TargetEntityName = "Author",
                    TargetEntityId = "Author",
                    JoinEntityName = "AuthorBook",
                    JoinEntityId = "AuthorBook",
                    InverseNavigationPropertyName = "Books"
                }
            ]
        });

        var output = _engine.Evaluate(input);

        var db03Findings = output.Findings.Where(f => f.RuleId == "DB03").ToList();
        Assert.Single(db03Findings);
    }

    [Fact]
    public void DB03_MissingIdentifiers_EmitsDiagnostic_AndSafelySkipsDeduplication()
    {
        // При отсутствии стабильных идентификаторов (TargetEntityId, JoinEntityId или ContextId)
        // система НЕ пытается угадывать отношение по именам, выдаёт диагностику AMBIGUOUS_MANY_TO_MANY_SKIPPED
        // и безопасно пропускает дедупликацию, сохраняя оба findings без ложного слияния.
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].ContextId = null; // Отсутствует ContextId!
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Author",
            EntityId = "Author",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "Books",
                    TargetEntityName = "Book",
                    TargetEntityId = null, // Отсутствует TargetEntityId!
                    JoinEntityName = "AuthorBook",
                    JoinEntityId = null,
                    InverseNavigationPropertyName = "Authors"
                }
            ]
        });
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Book",
            EntityId = "Book",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "Authors",
                    TargetEntityName = "Author",
                    TargetEntityId = null, // Отсутствует TargetEntityId!
                    JoinEntityName = "AuthorBook",
                    JoinEntityId = null,
                    InverseNavigationPropertyName = "Books"
                }
            ]
        });

        var output = _engine.Evaluate(input);

        // Должна быть сформирована диагностика о пропуске дедупликации
        Assert.Contains(output.Diagnostics, d => d.Code == "AMBIGUOUS_MANY_TO_MANY_SKIPPED" && d.RuleId == "DB03");

        // Без стабильных идентификаторов дедупликация пропущена -> оба findings сохранены
        var db03Findings = output.Findings.Where(f => f.RuleId == "DB03").ToList();
        Assert.Equal(2, db03Findings.Count);
    }

    [Fact]
    public void DB03_SameEntityNames_AcrossDifferentContexts_AreNotMerged()
    {
        // Одинаковые имена сущностей (User, Role) в двух разных контекстах
        // не должны склеиваться между контекстами благодаря разным ContextId.
        var input = CreateBaseInput();
        input.Metadata.DbContexts =
        [
            new DbContextDto
            {
                Name = "CoreDbContext",
                ContextId = "Company.Core.CoreDbContext",
                Entities =
                [
                    new EntityDto
                    {
                        Name = "User",
                        EntityId = "Company.Core.User",
                        MappingType = "Table",
                        SkipNavigations =
                        [
                            new SkipNavigationDto
                            {
                                NavigationPropertyName = "Roles",
                                TargetEntityName = "Role",
                                TargetEntityId = "Company.Core.Role",
                                JoinEntityName = "UserRoles",
                                JoinEntityId = "Company.Core.UserRoles",
                                InverseNavigationPropertyName = "Users"
                            }
                        ]
                    },
                    new EntityDto
                    {
                        Name = "Role",
                        EntityId = "Company.Core.Role",
                        MappingType = "Table",
                        SkipNavigations =
                        [
                            new SkipNavigationDto
                            {
                                NavigationPropertyName = "Users",
                                TargetEntityName = "User",
                                TargetEntityId = "Company.Core.User",
                                JoinEntityName = "UserRoles",
                                JoinEntityId = "Company.Core.UserRoles",
                                InverseNavigationPropertyName = "Roles"
                            }
                        ]
                    }
                ]
            },
            new DbContextDto
            {
                Name = "IdentityDbContext",
                ContextId = "Company.Identity.IdentityDbContext",
                Entities =
                [
                    new EntityDto
                    {
                        Name = "User",
                        EntityId = "Company.Identity.User",
                        MappingType = "Table",
                        SkipNavigations =
                        [
                            new SkipNavigationDto
                            {
                                NavigationPropertyName = "Roles",
                                TargetEntityName = "Role",
                                TargetEntityId = "Company.Identity.Role",
                                JoinEntityName = "UserRoles",
                                JoinEntityId = "Company.Identity.UserRoles",
                                InverseNavigationPropertyName = "Users"
                            }
                        ]
                    },
                    new EntityDto
                    {
                        Name = "Role",
                        EntityId = "Company.Identity.Role",
                        MappingType = "Table",
                        SkipNavigations =
                        [
                            new SkipNavigationDto
                            {
                                NavigationPropertyName = "Users",
                                TargetEntityName = "User",
                                TargetEntityId = "Company.Identity.User",
                                JoinEntityName = "UserRoles",
                                JoinEntityId = "Company.Identity.UserRoles",
                                InverseNavigationPropertyName = "Roles"
                            }
                        ]
                    }
                ]
            }
        ];

        var output = _engine.Evaluate(input);

        var db03Findings = output.Findings.Where(f => f.RuleId == "DB03").ToList();
        // В каждом контексте дедупликация до 1 finding -> всего ровно 2 findings
        Assert.Equal(2, db03Findings.Count);
        Assert.Contains(db03Findings, f => f.ContextId == "Company.Core.CoreDbContext");
        Assert.Contains(db03Findings, f => f.ContextId == "Company.Identity.IdentityDbContext");
    }

    [Fact]
    public void DB03_TwoIndependentRelationshipsBetweenSameEntities_AreBothPreserved()
    {
        // Две самостоятельные M:N связи между одними и теми же сущностями через разные join entities
        // Например, User и Project связаны как:
        // 1. Участники проекта (через ProjectMemberships)
        // 2. Наблюдатели проекта (через ProjectWatchers)
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "User",
            EntityId = "User",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "MemberProjects",
                    TargetEntityName = "Project",
                    TargetEntityId = "Project",
                    JoinEntityName = "ProjectMemberships",
                    JoinEntityId = "ProjectMemberships",
                    InverseNavigationPropertyName = "Members"
                },
                new SkipNavigationDto
                {
                    NavigationPropertyName = "WatchedProjects",
                    TargetEntityName = "Project",
                    TargetEntityId = "Project",
                    JoinEntityName = "ProjectWatchers",
                    JoinEntityId = "ProjectWatchers",
                    InverseNavigationPropertyName = "Watchers"
                }
            ]
        });
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Project",
            EntityId = "Project",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "Members",
                    TargetEntityName = "User",
                    TargetEntityId = "User",
                    JoinEntityName = "ProjectMemberships",
                    JoinEntityId = "ProjectMemberships",
                    InverseNavigationPropertyName = "MemberProjects"
                },
                new SkipNavigationDto
                {
                    NavigationPropertyName = "Watchers",
                    TargetEntityName = "User",
                    TargetEntityId = "User",
                    JoinEntityName = "ProjectWatchers",
                    JoinEntityId = "ProjectWatchers",
                    InverseNavigationPropertyName = "WatchedProjects"
                }
            ]
        });

        var output = _engine.Evaluate(input);

        var db03Findings = output.Findings.Where(f => f.RuleId == "DB03").ToList();
        // Обе самостоятельные связи должны быть обнаружены (не потеряны из-за дедупликации)!
        Assert.Equal(2, db03Findings.Count);
    }

    [Fact]
    public void DB03_MultipleDbContexts_WithSameClrTypes_AreIsolated()
    {
        // Несколько DbContext с одинаковыми CLR-типами сущностей должны обрабатываться раздельно
        var input = CreateBaseInput();
        input.Metadata.DbContexts =
        [
            new DbContextDto
            {
                Name = "PrimaryDbContext",
                Entities =
                [
                    new EntityDto
                    {
                        Name = "User",
                        MappingType = "Table",
                        SkipNavigations =
                        [
                            new SkipNavigationDto
                            {
                                NavigationPropertyName = "Roles",
                                TargetEntityName = "Role",
                                JoinEntityName = "UserRoles"
                            }
                        ]
                    }
                ]
            },
            new DbContextDto
            {
                Name = "ArchiveDbContext",
                Entities =
                [
                    new EntityDto
                    {
                        Name = "User",
                        MappingType = "Table",
                        SkipNavigations =
                        [
                            new SkipNavigationDto
                            {
                                NavigationPropertyName = "Roles",
                                TargetEntityName = "Role",
                                JoinEntityName = "UserRoles"
                            }
                        ]
                    }
                ]
            }
        ];

        var output = _engine.Evaluate(input);

        var db03Findings = output.Findings.Where(f => f.RuleId == "DB03").ToList();
        Assert.Equal(2, db03Findings.Count);
        Assert.Contains(db03Findings, f => f.ContextId == "PrimaryDbContext");
        Assert.Contains(db03Findings, f => f.ContextId == "ArchiveDbContext");
    }

    [Fact]
    public void DB03_SameDisplayName_DifferentContextId_AreNotCollapsed()
    {
        // Два разных DbContext с одинаковым именем Name = "AppDbContext", но разными ContextId
        var input = CreateBaseInput();
        input.Metadata.DbContexts =
        [
            new DbContextDto
            {
                Name = "AppDbContext",
                ContextId = "BillingModule.AppDbContext",
                Entities =
                [
                    new EntityDto
                    {
                        Name = "User",
                        MappingType = "Table",
                        SkipNavigations =
                        [
                            new SkipNavigationDto
                            {
                                NavigationPropertyName = "Roles",
                                TargetEntityName = "Role",
                                JoinEntityName = "UserRoles"
                            }
                        ]
                    }
                ]
            },
            new DbContextDto
            {
                Name = "AppDbContext",
                ContextId = "AuthModule.AppDbContext",
                Entities =
                [
                    new EntityDto
                    {
                        Name = "User",
                        MappingType = "Table",
                        SkipNavigations =
                        [
                            new SkipNavigationDto
                            {
                                NavigationPropertyName = "Roles",
                                TargetEntityName = "Role",
                                JoinEntityName = "UserRoles"
                            }
                        ]
                    }
                ]
            }
        ];

        var output = _engine.Evaluate(input);

        var db03Findings = output.Findings.Where(f => f.RuleId == "DB03").ToList();
        Assert.Equal(2, db03Findings.Count);
        Assert.Contains(db03Findings, f => f.ContextId == "BillingModule.AppDbContext");
        Assert.Contains(db03Findings, f => f.ContextId == "AuthModule.AppDbContext");
    }

    [Fact]
    public void DB03_TwoRelationships_ThroughSameJoinEntity_AreNotCollapsed()
    {
        // Две независимые M:N связи через одну и ту же join entity (с разными парами навигаций)
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Student",
            EntityId = "App.Student",
            MappingType = "Table",
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "MajorCourses",
                    TargetEntityName = "Course",
                    TargetEntityId = "App.Course",
                    JoinEntityName = "StudentCourses",
                    JoinEntityId = "App.StudentCourses",
                    InverseNavigationPropertyName = "MajorStudents"
                },
                new SkipNavigationDto
                {
                    NavigationPropertyName = "ElectiveCourses",
                    TargetEntityName = "Course",
                    TargetEntityId = "App.Course",
                    JoinEntityName = "StudentCourses",
                    JoinEntityId = "App.StudentCourses",
                    InverseNavigationPropertyName = "ElectiveStudents"
                }
            ]
        });
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Course",
            EntityId = "App.Course",
            MappingType = "Table",
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "MajorStudents",
                    TargetEntityName = "Student",
                    TargetEntityId = "App.Student",
                    JoinEntityName = "StudentCourses",
                    JoinEntityId = "App.StudentCourses",
                    InverseNavigationPropertyName = "MajorCourses"
                },
                new SkipNavigationDto
                {
                    NavigationPropertyName = "ElectiveStudents",
                    TargetEntityName = "Student",
                    TargetEntityId = "App.Student",
                    JoinEntityName = "StudentCourses",
                    JoinEntityId = "App.StudentCourses",
                    InverseNavigationPropertyName = "ElectiveCourses"
                }
            ]
        });

        var output = _engine.Evaluate(input);

        var db03Findings = output.Findings.Where(f => f.RuleId == "DB03").ToList();
        Assert.Equal(2, db03Findings.Count);
    }

    [Fact]
    public void DB03_DifferentNavigationPairs_WithIdenticalNames_AreNotCollapsed()
    {
        // Разные сущности имеют одинаковые имена навигационных свойств ("Items")
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Invoice",
            EntityId = "App.Invoice",
            MappingType = "Table",
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "Tags",
                    TargetEntityName = "Tag",
                    TargetEntityId = "App.Tag",
                    JoinEntityName = "InvoiceTags"
                }
            ]
        });
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Article",
            EntityId = "App.Article",
            MappingType = "Table",
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "Tags",
                    TargetEntityName = "Tag",
                    TargetEntityId = "App.Tag",
                    JoinEntityName = "ArticleTags"
                }
            ]
        });

        var output = _engine.Evaluate(input);

        var db03Findings = output.Findings.Where(f => f.RuleId == "DB03").ToList();
        Assert.Equal(2, db03Findings.Count);
    }

    #endregion

    #region DB04: Missing Foreign Key Index

    [Fact]
    public void DB04_Positive_UnindexedForeignKey_ProducesFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderItem",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "Order",
                    Properties = ["OrderId"],
                    PrincipalProperties = ["Id"],
                    NavigationToPrincipal = "Order",
                    NavigationToDependent = "Items",
                    IsNavigationResolved = true
                }
            ],
            Indexes = []
        });

        var output = _engine.Evaluate(input);

        Assert.Single(output.Findings);
        var finding = output.Findings[0];
        Assert.Equal("DB04", finding.RuleId);
        Assert.Equal("MissingForeignKeyIndex", finding.RuleName);
    }

    [Fact]
    public void DB04_Negative_IndexedForeignKey_NoFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderItem",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "Order",
                    Properties = ["TenantId", "OrderId"],
                    PrincipalProperties = ["TenantId", "Id"],
                    NavigationToPrincipal = "Order",
                    NavigationToDependent = "Items",
                    IsNavigationResolved = true
                }
            ],
            Indexes =
            [
                // Covers prefix (TenantId, OrderId)
                new IndexDto { Name = "IX_Composite", Properties = ["TenantId", "OrderId", "CreatedAt"] }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    [Fact]
    public void DB04_Negative_CoveredByPrimaryKeyOrAlternateKey_NoFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderItem",
            MappingType = "Table",
            Properties =
            [
                new PropertyDto { Name = "OrderId", IsPrimaryKey = true, ColumnName = "OrderId" },
                new PropertyDto { Name = "LineNumber", IsPrimaryKey = true, ColumnName = "LineNumber" }
            ],
            Keys =
            [
                new KeyDto { Properties = ["OrderId", "LineNumber"], IsPrimaryKey = true }
            ],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "Order",
                    Properties = ["OrderId"],
                    PrincipalProperties = ["Id"],
                    NavigationToPrincipal = "Order",
                    NavigationToDependent = "Items",
                    IsNavigationResolved = true
                }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    [Fact]
    public void DB04_Positive_WrongColumnOrderInIndex_ProducesFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "OrderItem",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "Order",
                    Properties = ["TenantId", "OrderId"],
                    PrincipalProperties = ["TenantId", "Id"],
                    NavigationToPrincipal = "Order",
                    NavigationToDependent = "Items",
                    IsNavigationResolved = true
                }
            ],
            Indexes =
            [
                // Different order! (OrderId, TenantId) does NOT cover (TenantId, OrderId)
                new IndexDto { Name = "IX_WrongOrder", Properties = ["OrderId", "TenantId"] }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Single(output.Findings);
        Assert.Equal("DB04", output.Findings[0].RuleId);
    }

    #endregion

    #region DB05: Duplicate Index Detection & SQL Filter Normalization

    [Fact]
    public void DB05_IdenticalFilters_WithDifferentOuterParentheses_ProducesFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Document",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            Indexes =
            [
                new IndexDto { Name = "IX_Doc_1", Properties = ["Title"], IsUnique = false, Filter = "(IsDeleted = 0)" },
                new IndexDto { Name = "IX_Doc_2", Properties = ["Title"], IsUnique = false, Filter = "((IsDeleted = 0))" }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Single(output.Findings);
        Assert.Equal("DB05", output.Findings[0].RuleId);
    }

    [Fact]
    public void DB05_DifferentStringLiterals_NoFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Task",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            Indexes =
            [
                new IndexDto { Name = "IX_Task_Active", Properties = ["Name"], IsUnique = true, Filter = "Status = 'Active'" },
                new IndexDto { Name = "IX_Task_Archived", Properties = ["Name"], IsUnique = true, Filter = "Status = 'Archived'" }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    [Fact]
    public void DB05_StringLiteralsWithDifferentSpaces_NoFinding()
    {
        // Пробелы внутри литерала 'hello   world' НЕ должны удаляться/схлопываться!
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Item",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            Indexes =
            [
                new IndexDto { Name = "IX_1", Properties = ["Code"], IsUnique = false, Filter = "Desc = 'hello   world'" },
                new IndexDto { Name = "IX_2", Properties = ["Code"], IsUnique = false, Filter = "Desc = 'hello world'" }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    [Fact]
    public void DB05_DifferentOperators_NoFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Account",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            Indexes =
            [
                new IndexDto { Name = "IX_Acc_Eq", Properties = ["Number"], IsUnique = true, Filter = "Balance = 0" },
                new IndexDto { Name = "IX_Acc_Neq", Properties = ["Number"], IsUnique = true, Filter = "Balance <> 0" }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    [Fact]
    public void DB05_NullPredicates_NoFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "AuditLog",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            Indexes =
            [
                new IndexDto { Name = "IX_Audit_Null", Properties = ["UserId"], IsUnique = false, Filter = "DeletedAt IS NULL" },
                new IndexDto { Name = "IX_Audit_NotNull", Properties = ["UserId"], IsUnique = false, Filter = "DeletedAt IS NOT NULL" }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    [Fact]
    public void DB05_NestedExpressions_EquivalentSemantics_ProducesFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Record",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            Indexes =
            [
                new IndexDto { Name = "IX_Rec_1", Properties = ["RefId"], IsUnique = false, Filter = "((a = 1 OR (b = 2 AND c = 3)))" },
                new IndexDto { Name = "IX_Rec_2", Properties = ["RefId"], IsUnique = false, Filter = "(a = 1 OR (b = 2 AND c = 3))" }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Single(output.Findings);
        Assert.Equal("DB05", output.Findings[0].RuleId);
    }

    [Fact]
    public void DB05_DifferentFiltersOnSameColumns_NoFinding()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "User",
            MappingType = "Table",
            Properties = [new PropertyDto { Name = "Id", IsPrimaryKey = true, ColumnName = "Id" }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            Indexes =
            [
                new IndexDto { Name = "IX_User_Filtered", Properties = ["Email"], IsUnique = true, Filter = "IsDeleted = 0" },
                new IndexDto { Name = "IX_User_Unfiltered", Properties = ["Email"], IsUnique = true, Filter = null }
            ]
        });

        var output = _engine.Evaluate(input);

        Assert.Empty(output.Findings);
    }

    #endregion

    #region SqlFilterComparer Tests

    [Fact]
    public void SqlFilterComparer_NullComparisons_NotEquivalent()
    {
        // IS NULL не эквивалентен = NULL (в SQL = NULL дает UNKNOWN)
        var eq1 = SqlFilterComparer.AreFiltersEquivalent("DeletedAt IS NULL", "DeletedAt = NULL", out var amb1);
        Assert.False(eq1);
        Assert.False(amb1);

        // IS NOT NULL не эквивалентен != NULL
        var eq2 = SqlFilterComparer.AreFiltersEquivalent("DeletedAt IS NOT NULL", "DeletedAt != NULL", out var amb2);
        Assert.False(eq2);
        Assert.False(amb2);
    }

    [Fact]
    public void SqlFilterComparer_QuotedIdentifiers_PostgreSql_CaseSensitivity()
    {
        // В PostgreSQL идентификаторы в двойных кавычках чувствительны к регистру
        var eqDiffCase = SqlFilterComparer.AreFiltersEquivalent("\"Status\" = 1", "\"status\" = 1", out var amb1);
        Assert.False(eqDiffCase);
        Assert.False(amb1);

        var eqSameCase = SqlFilterComparer.AreFiltersEquivalent("\"Status\" = 1", "\"Status\" = 1", out var amb2);
        Assert.True(eqSameCase);
        Assert.False(amb2);
    }

    [Fact]
    public void SqlFilterComparer_UnquotedIdentifiers_CaseInsensitive()
    {
        // Неквотированные идентификаторы в SQL нечувствительны к регистру
        var eq = SqlFilterComparer.AreFiltersEquivalent("Status = 1", "status = 1", out var amb);
        Assert.True(eq);
        Assert.False(amb);
    }

    [Fact]
    public void SqlFilterComparer_DifferentDialects_NotEquivalent()
    {
        // [Col] (T-SQL) и "Col" (PostgreSQL) не универсально эквивалентны
        var eq1 = SqlFilterComparer.AreFiltersEquivalent("[Col] = 1", "\"Col\" = 1", out var amb1);
        Assert.False(eq1);
        Assert.False(amb1);

        // `Col` (MySQL) и "Col" (PostgreSQL) не эквивалентны
        var eq2 = SqlFilterComparer.AreFiltersEquivalent("`Col` = 1", "\"Col\" = 1", out var amb2);
        Assert.False(eq2);
        Assert.False(amb2);
    }

    [Fact]
    public void SqlFilterComparer_StringLiterals_ExactWhitespaceAndCase()
    {
        // Содержимое строковых литералов сохраняется строго: пробелы и регистр значимы
        var eqSpaces = SqlFilterComparer.AreFiltersEquivalent("Type = 'ACTIVE STATUS'", "Type = 'ACTIVESTATUS'", out var amb1);
        Assert.False(eqSpaces);
        Assert.False(amb1);

        var eqCase = SqlFilterComparer.AreFiltersEquivalent("Type = 'ACTIVE'", "Type = 'active'", out var amb2);
        Assert.False(eqCase);
        Assert.False(amb2);

        var eqExact = SqlFilterComparer.AreFiltersEquivalent("Type = 'ACTIVE STATUS'", "Type = 'ACTIVE STATUS'", out var amb3);
        Assert.True(eqExact);
        Assert.False(amb3);
    }

    [Fact]
    public void SqlFilterComparer_EscapedQuotes_HandledCorrectly()
    {
        // Экранированная одинарная кавычка '' внутри литерала
        var eq = SqlFilterComparer.AreFiltersEquivalent("Name = 'O''Reilly'", "Name = 'O''Reilly'", out var amb1);
        Assert.True(eq);
        Assert.False(amb1);

        var eqDiff = SqlFilterComparer.AreFiltersEquivalent("Name = 'O''Reilly'", "Name = 'OReilly'", out var amb2);
        Assert.False(eqDiff);
        Assert.False(amb2);
    }

    [Fact]
    public void SqlFilterComparer_AmbiguousExpressions_FlaggedAsAmbiguous()
    {
        // Незакрытая кавычка -> синтаксическая ошибка -> isAmbiguous = true
        var eq = SqlFilterComparer.AreFiltersEquivalent("Name = 'Unclosed", "Name = 'Other'", out var amb);
        Assert.False(eq);
        Assert.True(amb);
    }

    #endregion

    #region Granular Capabilities Tests

    [Fact]
    public void GranularCapabilities_IncompleteKeys_SkipsDb01AndDb04_EvaluatesDb02()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Order",
            MappingType = "Table",
            HasIncompleteKeys = true,
            Properties = [new PropertyDto { Name = "Id", ColumnName = "Id" }],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "User",
                    Properties = ["UserId"],
                    NavigationToPrincipal = null,
                    IsNavigationResolved = true
                }
            ]
        });

        var output = _engine.Evaluate(input);

        // DB01 и DB04 пропущены с соответствующими диагностиками
        Assert.Contains(output.Diagnostics, d => d.Code == "INCOMPLETE_KEYS_SKIPPED" && d.RuleId == "DB01");
        Assert.Contains(output.Diagnostics, d => d.Code == "INCOMPLETE_INDEXES_SKIPPED" && d.RuleId == "DB04");

        // DB02 выполнен и зарегистрировал finding (не заблокирован неполными ключами!)
        Assert.Contains(output.Findings, f => f.RuleId == "DB02");
    }

    [Fact]
    public void GranularCapabilities_IncompleteNavigations_SkipsDb02_EvaluatesDb04()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Order",
            MappingType = "Table",
            HasIncompleteNavigations = true,
            Properties = [new PropertyDto { Name = "Id", ColumnName = "Id", IsPrimaryKey = true }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            ForeignKeys =
            [
                new ForeignKeyDto
                {
                    PrincipalEntityName = "User",
                    Properties = ["UserId"],
                    NavigationToPrincipal = null,
                    IsNavigationResolved = true
                }
            ]
        });

        var output = _engine.Evaluate(input);

        // DB02 пропущен из-за неполных навигаций
        Assert.Contains(output.Diagnostics, d => d.Code == "INCOMPLETE_NAVIGATIONS_SKIPPED" && d.RuleId == "DB02");

        // DB04 выполнен успешно (неполные навигации не мешают проверке индексов внешних ключей!)
        Assert.Contains(output.Findings, f => f.RuleId == "DB04");
    }

    [Fact]
    public void GranularCapabilities_IncompleteSkipNavigations_SkipsDb03_EvaluatesOthers()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Order",
            MappingType = "Table",
            HasIncompleteSkipNavigations = true,
            Properties = [new PropertyDto { Name = "Id", ColumnName = "Id", IsPrimaryKey = true }],
            Keys = [new KeyDto { Properties = ["Id"], IsPrimaryKey = true }],
            SkipNavigations =
            [
                new SkipNavigationDto
                {
                    NavigationPropertyName = "Tags",
                    TargetEntityName = "Tag"
                }
            ]
        });

        var output = _engine.Evaluate(input);

        // DB03 пропущен с диагностикой
        Assert.Contains(output.Diagnostics, d => d.Code == "INCOMPLETE_SKIP_NAVIGATIONS_SKIPPED" && d.RuleId == "DB03");
        Assert.DoesNotContain(output.Findings, f => f.RuleId == "DB03");
    }

    [Fact]
    public void GranularCapabilities_IncompleteIndexes_SkipsDb04AndDb05_EvaluatesDb01()
    {
        var input = CreateBaseInput();
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "Order",
            MappingType = "Table",
            HasIncompleteIndexes = true,
            Properties = [new PropertyDto { Name = "Id", ColumnName = "Id" }],
            Keys = []
        });

        var output = _engine.Evaluate(input);

        // DB04 и DB05 пропущены
        Assert.Contains(output.Diagnostics, d => d.Code == "INCOMPLETE_INDEXES_SKIPPED" && d.RuleId == "DB04");
        Assert.Contains(output.Diagnostics, d => d.Code == "INCOMPLETE_INDEXES_SKIPPED" && d.RuleId == "DB05");

        // DB01 выполнен и нашёл отсутствие PK
        Assert.Contains(output.Findings, f => f.RuleId == "DB01");
    }

    [Fact]
    public void GranularCapabilities_IncompleteMetadata_SkipsAllDbRulesForIncompleteEntity_AndEvaluatesValidEntity()
    {
        var input = CreateBaseInput();
        // 1. Сущность с неполными метаданными (hasIncompleteMetadata = true):
        // Несмотря на отсутствие PK, FK индексов и навигаций, правила DB01–DB05 не должны рапортовать ложные дефекты
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "IncompleteEntity",
            MappingType = "Table",
            HasIncompleteMetadata = true,
            Properties = [new PropertyDto { Name = "Data", ColumnName = "Data" }],
            Keys = [],
            ForeignKeys = [new ForeignKeyDto { PrincipalEntityName = "Other", Properties = ["OtherId"] }],
            Indexes = []
        });

        // 2. Достоверная сущность с реальным дефектом DB01 (отсутствие PK)
        input.Metadata.DbContexts[0].Entities.Add(new EntityDto
        {
            Name = "ValidEntityWithoutPk",
            MappingType = "Table",
            HasIncompleteMetadata = false,
            Properties = [new PropertyDto { Name = "Name", ColumnName = "Name" }],
            Keys = []
        });

        var output = _engine.Evaluate(input);

        // Для сущности с неполными метаданными не формируются ложные срабатывания
        Assert.DoesNotContain(output.Findings, f => f.TargetEntityName == "IncompleteEntity");

        // Формируются диагностические предупреждения о пропуске проверок из-за неполноты
        Assert.Contains(output.Diagnostics, d => d.Code == "INCOMPLETE_METADATA_SKIPPED" && d.EntityId == "IncompleteEntity");

        // Для достоверной сущности дефект DB01 найден
        Assert.Contains(output.Findings, f => f.RuleId == "DB01" && f.TargetEntityName == "ValidEntityWithoutPk");
    }

    [Fact]
    public async Task EndToEnd_RoslynExtraction_ToDto_ToJson_ToRunnerRulesEngine_IncompleteMetadataHandledCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RoslynToRunnerE2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dbContextSource = """
                using Microsoft.EntityFrameworkCore;

                namespace TestApp;

                public class AppDbContext : DbContext
                {
                    public DbSet<ValidEntityWithoutPk> ValidEntities => Set<ValidEntityWithoutPk>();
                    public DbSet<IncompleteEntity> IncompleteEntities => Set<IncompleteEntity>();
                }
                """;

            var validEntitySource = """
                namespace TestApp;

                public class ValidEntityWithoutPk
                {
                    public string Title { get; set; } = string.Empty;
                }
                """;

            var incompleteEntitySource = """
                namespace TestApp;

                public class IncompleteEntity : ExternalBaseClassWithoutSource
                {
                    public string Description { get; set; } = string.Empty;
                }
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "AppDbContext.cs"), dbContextSource);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "ValidEntityWithoutPk.cs"), validEntitySource);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "IncompleteEntity.cs"), incompleteEntitySource);

            // 1. Извлечение метаданных через Roslyn
            var extractor = new RoslynMetadataExtractor();
            var extraction = await extractor.ExtractAsync(tempDir);

            Assert.Single(extraction.Contexts);
            var dbContext = extraction.Contexts[0];
            Assert.Equal(2, dbContext.Entities.Count);

            var incompleteDiscovered = dbContext.Entities.First(e => e.EntityName == "IncompleteEntity");
            var validDiscovered = dbContext.Entities.First(e => e.EntityName == "ValidEntityWithoutPk");

            Assert.True(incompleteDiscovered.HasIncompleteMetadata);
            Assert.True(incompleteDiscovered.HasIncompleteKeys);
            Assert.False(validDiscovered.HasIncompleteMetadata);

            // 2. Преобразование метаданных Roslyn в DTO документа ввода контейнера
            var inputDoc = DatabaseContainerRunner.BuildInputDocument(extraction, 77, Guid.NewGuid());

            // 3. Сериализация в JSON
            var json = System.Text.Json.JsonSerializer.Serialize(inputDoc, DatabaseAnalyzerJsonContext.Default.DatabaseAnalyzerInputDocument);
            Assert.Contains("\"hasIncompleteMetadata\":true", json);
            Assert.Contains("\"hasIncompleteKeys\":true", json);

            // 4. Десериализация JSON на стороне Runner в контракт AnalysisInputContract
            var runnerContract = System.Text.Json.JsonSerializer.Deserialize<AnalysisInputContract>(
                json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(runnerContract);

            // 5. Выполнение движка правил Runner
            var output = _engine.Evaluate(runnerContract);

            // Проверка: IncompleteEntity НЕ содержит ложных дефектов DB01–DB05
            Assert.DoesNotContain(output.Findings, f => f.TargetEntityName == "IncompleteEntity");

            // Проверка: IncompleteEntity содержит диагностику о пропуске проверок из-за неполноты
            Assert.Contains(output.Diagnostics, d =>
                d.Code == "INCOMPLETE_METADATA_SKIPPED" || d.Code == "INCOMPLETE_KEYS_SKIPPED");

            // Проверка: Достоверная сущность без ключа ValidEntityWithoutPk проверена и для неё зафиксирован DB01
            Assert.Contains(output.Findings, f => f.RuleId == "DB01" && f.TargetEntityName == "ValidEntityWithoutPk");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    #endregion
}
