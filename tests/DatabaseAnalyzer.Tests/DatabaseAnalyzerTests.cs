using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore;
using DatabaseAnalyzer.Mapping;
using Application.Analyzers;
using DatabaseAnalyzer;
using DatabaseAnalyzer.Metadata;
using DatabaseAnalyzer.Providers;
using DatabaseAnalyzer.Roslyn;
using DatabaseAnalyzer.Rules;
using Domain.Enums;
using Moq;

namespace DatabaseAnalyzer.Tests;

public sealed class DatabaseAnalyzerTests
{
    [Fact]
    public void Type_ShouldReturnDatabase()
    {
        var mockExtractor = new Mock<IRoslynMetadataExtractor>();
        var analyzer = new DatabaseAnalyzer(mockExtractor.Object);

        Assert.Equal(AnalyzerType.Database, analyzer.Type);
    }

    [Fact]
    public void ExecutionStatus_InitialValue_ShouldBeNotStarted()
    {
        var mockExtractor = new Mock<IRoslynMetadataExtractor>();
        var analyzer = new DatabaseAnalyzer(mockExtractor.Object);

        Assert.Equal(DatabaseAnalysisExecutionStatus.NotStarted, analyzer.ExecutionStatus);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldCallRoslynMetadataExtractor()
    {
        var mockExtractor = new Mock<IRoslynMetadataExtractor>();
        var tempDir = Path.Combine(Path.GetTempPath(), "DbAnalyzerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            mockExtractor
                .Setup(e => e.ExtractAsync(tempDir, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RoslynModelExtractionResult
                {
                    Contexts = [new DiscoveredDbContext { Name = "AppDbContext" }]
                });

            var analyzer = new DatabaseAnalyzer(mockExtractor.Object);

            var context = new AnalyzerContext
            {
                RepositoryPath = tempDir,
                CommitHash = "test-commit"
            };

            var result = await analyzer.AnalyzeAsync(context);

            mockExtractor.Verify(
                e => e.ExtractAsync(tempDir, It.IsAny<CancellationToken>()),
                Times.Once);

            Assert.Contains(result, f => f.Rule == DatabaseAnalyzer.ContainerUnavailableRule);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AnalyzeAsync_WhenNoDbContextFound_ReturnsInformationSkippedStatusAndNoContainerUnavailableFinding()
    {
        var mockExtractor = new Mock<IRoslynMetadataExtractor>();
        var tempDir = Path.Combine(Path.GetTempPath(), "DbAnalyzerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            mockExtractor
                .Setup(e => e.ExtractAsync(tempDir, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RoslynModelExtractionResult());

            var analyzer = new DatabaseAnalyzer(mockExtractor.Object);

            var context = new AnalyzerContext
            {
                RepositoryPath = tempDir,
                CommitHash = "test-commit"
            };

            var result = await analyzer.AnalyzeAsync(context);

            Assert.Equal(DatabaseAnalysisExecutionStatus.InformationSkipped, analyzer.ExecutionStatus);
            Assert.Contains(result, f => f.Rule == DatabaseAnalyzer.MissingDbContextRule);
            Assert.DoesNotContain(result, f => f.Rule == DatabaseAnalyzer.ContainerUnavailableRule);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AnalyzeAsync_WhenExtractorThrows_ReturnsDiagnosticWarningFinding()
    {
        var mockExtractor = new Mock<IRoslynMetadataExtractor>();
        var tempDir = Path.Combine(Path.GetTempPath(), "DbAnalyzerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            mockExtractor
                .Setup(e => e.ExtractAsync(tempDir, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Test error"));

            var analyzer = new DatabaseAnalyzer(mockExtractor.Object);

            var context = new AnalyzerContext
            {
                RepositoryPath = tempDir,
                CommitHash = "test-commit"
            };

            var findings = await analyzer.AnalyzeAsync(context);

            Assert.Contains(findings, f => f.Rule == DatabaseAnalyzer.ExtractionErrorRule);
            var finding = findings.First(f => f.Rule == DatabaseAnalyzer.ExtractionErrorRule);
            Assert.Contains("Test error", finding.Message);
            Assert.Equal(Severity.Warning, finding.Severity);
            Assert.DoesNotContain(findings, f => f.Rule == DatabaseAnalyzer.ContainerUnavailableRule);
            Assert.Equal(DatabaseAnalysisExecutionStatus.ExtractionError, analyzer.ExecutionStatus);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void AnalyzeAsync_ShouldReportEntityWithoutPrimaryKey()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                {
                    EntityName = "ReportRow",
                    TableName = "ReportRows",
                    Keys = []
                }
                ]
            };

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(findings);
        Assert.Equal("DB01", finding.Rule);
        Assert.Equal("ReportRows", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("ReportRow", finding.Message);
        Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation));
    }

    [Fact]
    public void AnalyzeAsync_ShouldNotReportEntityWithPrimaryKey()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "User",
                        TableName = "Users",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Users",
                                PropertyNames = ["UserId"],
                                IsPrimaryKey = true
                            }
                        ]
                    }
                    ]
            };

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.Empty(findings);
    }


    [Fact]
    public void AnalyzeAsync_ShouldReportForeignKeyWithoutIndex()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Order",
                        TableName = "Orders",
                        ForeignKeys =
                        [
                            new DatabaseForeignKeyMetadata
                            {
                                Name = "FK_Orders_Customers",
                                PropertyNames = ["CustomerId"],
                                PrincipalEntityName = "Customer",
                                PrincipalPropertyNames = ["CustomerId"],
                                NavigationToPrincipal = "Customer"
                            }
                        ],
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Orders",
                                PropertyNames = ["OrderId"],
                                IsPrimaryKey = true
                            }
                        ]

                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(result);
        Assert.Equal("DB04", finding.Rule);
        Assert.Equal("Orders", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("CustomerId", finding.Message);
        Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation));
    }

    [Fact]
    public void AnalyzeAsync_ShouldNotReportForeignKeyWithMatchingIndex()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Order",
                        TableName = "Orders",
                        ForeignKeys =
                        [
                            new DatabaseForeignKeyMetadata
                            {
                                Name = "FK_Orders_Customers",
                                PropertyNames = ["CustomerId"],
                                PrincipalEntityName = "Customer",
                                PrincipalPropertyNames = ["CustomerId"],
                                NavigationToPrincipal = "Customer"
                            }
                        ],
                        Indexes =
                        [
                            new DatabaseIndexMetadata
                            {
                                Name = "IX_Orders_CustomerId",
                                PropertyNames = ["CustomerId"]
                            }
                        ],
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Orders",
                                PropertyNames = ["OrderId"],
                                IsPrimaryKey = true
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.Empty(result);
    }

    [Fact]
    public void AnalyzeAsync_ShouldAcceptCompositeIndexStartingWithForeignKeyColumns()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "OrderItem",
                        TableName = "OrderItems",
                        ForeignKeys =
                        [
                            new DatabaseForeignKeyMetadata
                            {
                                Name = "FK_OrderItems_Products",
                                PropertyNames = ["ProductId", "WarehouseId"],
                                PrincipalEntityName = "Product",
                                PrincipalPropertyNames = ["ProductId", "WarehouseId"],
                                NavigationToPrincipal = "Product"
                            }
                        ],
                        Indexes =
                        [
                            new DatabaseIndexMetadata
                            {
                                Name = "IX_OrderItems_Product_Warehouse_Date",
                                PropertyNames = ["ProductId", "WarehouseId", "CreatedAt"]
                            }
                        ],
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_OrderItems",
                                PropertyNames = ["OrderItemId"],
                                IsPrimaryKey = true
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.Empty(result);
    }


    [Fact]
    public void AnalyzeAsync_ShouldReportDuplicateIndexes()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                {
                    EntityName = "Order",
                    TableName = "Orders",
                    Keys =
                    [
                        new DatabaseKeyMetadata
                        {
                            Name = "PK_Orders",
                            PropertyNames = ["OrderId"],
                            IsPrimaryKey = true
                        }
                    ],
                    Indexes =
                    [
                        new DatabaseIndexMetadata
                        {
                            Name = "IX_Orders_CustomerId",
                            PropertyNames = ["CustomerId"]
                        },
                        new DatabaseIndexMetadata
                        {
                            Name = "IX_Orders_CustomerId_Copy",
                            PropertyNames = ["CustomerId"]
                        }
                    ]
                }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(result);
        Assert.Equal("DB05", finding.Rule);
        Assert.Equal("Orders", finding.FilePath);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("CustomerId", finding.Message);
        Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation));
    }

    [Fact]
    public void AnalyzeAsync_ShouldNotReportIndexesWithDifferentPropertyOrderAsDuplicates()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                {
                    EntityName = "Order",
                    TableName = "Orders",
                    Keys =
                    [
                        new DatabaseKeyMetadata
                        {
                            Name = "PK_Orders",
                            PropertyNames = ["OrderId"],
                            IsPrimaryKey = true
                        }
                    ],
                    Indexes =
                    [
                        new DatabaseIndexMetadata
                        {
                            Name = "IX_Orders_Customer_Status",
                            PropertyNames = ["CustomerId", "Status"]
                        },
                        new DatabaseIndexMetadata
                        {
                            Name = "IX_Orders_Status_Customer",
                            PropertyNames = ["Status", "CustomerId"]
                        }
                    ]
                }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.Empty(result);
    }

    [Fact]
    public void AnalyzeAsync_ShouldNotReportIndexesWithDifferentUniquenessAsDuplicates()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                {
                    EntityName = "Order",
                    TableName = "Orders",
                    Keys =
                    [
                        new DatabaseKeyMetadata
                        {
                            Name = "PK_Orders",
                            PropertyNames = ["OrderId"],
                            IsPrimaryKey = true
                        }
                    ],
                    Indexes =
                    [
                        new DatabaseIndexMetadata
                        {
                            Name = "IX_Orders_CustomerId",
                            PropertyNames = ["CustomerId"],
                            IsUnique = false
                        },
                        new DatabaseIndexMetadata
                        {
                            Name = "UX_Orders_CustomerId",
                            PropertyNames = ["CustomerId"],
                            IsUnique = true
                        }
                    ]
                }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.Empty(result);
    }

    [Fact]
    public void AnalyzeAsync_ShouldReportForeignKeyWithoutAnyNavigation()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Order",
                        TableName = "Orders",
                        ForeignKeys =
                        [
                            new DatabaseForeignKeyMetadata
                            {
                                Name = "FK_Orders_Users_UserId",
                                PropertyNames = ["UserId"],
                                PrincipalEntityName = "User",
                                PrincipalPropertyNames = ["Id"],
                                NavigationToPrincipal = null,
                                NavigationToDependent = null
                            }
                        ],
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Orders",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        Indexes =
                        [
                            new DatabaseIndexMetadata
                            {
                                Name = "IX_Orders_UserId",
                                PropertyNames = ["UserId"]
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(result);
        Assert.Equal("DB02", finding.Rule);
        Assert.Equal("Orders", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("FK_Orders_Users_UserId", finding.Message);
        Assert.Contains("User", finding.Message);
        Assert.Contains("Order", finding.Message);
        Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation));
    }

    [Fact]
    public void AnalyzeAsync_ShouldReportForeignKeyWhenOnlyDependentNavigationExists()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Order",
                        TableName = "Orders",
                        ForeignKeys =
                        [
                            new DatabaseForeignKeyMetadata
                            {
                                Name = "FK_Orders_Users_UserId",
                                PropertyNames = ["UserId"],
                                PrincipalEntityName = "User",
                                PrincipalPropertyNames = ["Id"],
                                NavigationToPrincipal = null,
                                NavigationToDependent = "Orders"
                            }
                        ],
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Orders",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        Indexes =
                        [
                            new DatabaseIndexMetadata
                            {
                                Name = "IX_Orders_UserId",
                                PropertyNames = ["UserId"]
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(result);
        Assert.Equal("DB02", finding.Rule);
        Assert.Equal("Orders", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("FK_Orders_Users_UserId", finding.Message);
    }

    [Fact]
    public void AnalyzeAsync_ShouldNotReportForeignKeyWhenPrincipalNavigationExistsEvenWithoutDependentNavigation()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Order",
                        TableName = "Orders",
                        ForeignKeys =
                        [
                            new DatabaseForeignKeyMetadata
                            {
                                Name = "FK_Orders_Users_UserId",
                                PropertyNames = ["UserId"],
                                PrincipalEntityName = "User",
                                PrincipalPropertyNames = ["Id"],
                                NavigationToPrincipal = "User",
                                NavigationToDependent = null
                            }
                        ],
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Orders",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        Indexes =
                        [
                            new DatabaseIndexMetadata
                            {
                                Name = "IX_Orders_UserId",
                                PropertyNames = ["UserId"]
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.Empty(result);
    }

    [Fact]
    public void AnalyzeAsync_ShouldNotReportForeignKeyWhenBothNavigationsExist()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Order",
                        TableName = "Orders",
                        ForeignKeys =
                        [
                            new DatabaseForeignKeyMetadata
                            {
                                Name = "FK_Orders_Users_UserId",
                                PropertyNames = ["UserId"],
                                PrincipalEntityName = "User",
                                PrincipalPropertyNames = ["Id"],
                                NavigationToPrincipal = "User",
                                NavigationToDependent = "Orders"
                            }
                        ],
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Orders",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        Indexes =
                        [
                            new DatabaseIndexMetadata
                            {
                                Name = "IX_Orders_UserId",
                                PropertyNames = ["UserId"]
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.Empty(result);
    }

    [Fact]
    public void AnalyzeAsync_ShouldHandleMultipleForeignKeysIndependently()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Order",
                        TableName = "Orders",
                        ForeignKeys =
                        [
                            new DatabaseForeignKeyMetadata
                            {
                                Name = "FK_Orders_Users_UserId",
                                PropertyNames = ["UserId"],
                                PrincipalEntityName = "User",
                                PrincipalPropertyNames = ["Id"],
                                NavigationToPrincipal = "User",
                                NavigationToDependent = "Orders"
                            },
                            new DatabaseForeignKeyMetadata
                            {
                                Name = "FK_Orders_PaymentMethods_PaymentMethodId",
                                PropertyNames = ["PaymentMethodId"],
                                PrincipalEntityName = "PaymentMethod",
                                PrincipalPropertyNames = ["Id"],
                                NavigationToPrincipal = null,
                                NavigationToDependent = null
                            }
                        ],
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Orders",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        Indexes =
                        [
                            new DatabaseIndexMetadata
                            {
                                Name = "IX_Orders_UserId",
                                PropertyNames = ["UserId"]
                            },
                            new DatabaseIndexMetadata
                            {
                                Name = "IX_Orders_PaymentMethodId",
                                PropertyNames = ["PaymentMethodId"]
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(result);
        Assert.Equal("DB02", finding.Rule);
        Assert.Contains("FK_Orders_PaymentMethods_PaymentMethodId", finding.Message);
        Assert.Contains("PaymentMethod", finding.Message);
    }

    [Fact]
    public void AnalyzeAsync_ShouldReportDB03WithInfoSeverityForImplicitManyToMany()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Student",
                        TableName = "Students",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Students",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations =
                        [
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Courses",
                                TargetEntityName = "Course",
                                JoinEntityTableName = "CourseStudent",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = null
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(result);
        Assert.Equal("DB03", finding.Rule);
        Assert.Equal("Students", finding.FilePath);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("Student", finding.Message);
        Assert.Contains("Course", finding.Message);
        Assert.Contains("CourseStudent", finding.Message);
        Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation));
    }

    [Fact]
    public void AnalyzeAsync_ShouldReportSingleFindingForBidirectionalImplicitManyToMany()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Student",
                        TableName = "Students",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Students",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations =
                        [
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Courses",
                                TargetEntityName = "Course",
                                JoinEntityTableName = "CourseStudent",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = "Students"
                            }
                        ]
                    },
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Course",
                        TableName = "Courses",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Courses",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations =
                        [
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Students",
                                TargetEntityName = "Student",
                                JoinEntityTableName = "CourseStudent",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = "Courses"
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(result);
        Assert.Equal("DB03", finding.Rule);
        Assert.Equal("Courses", finding.FilePath);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("Course", finding.Message);
        Assert.Contains("Student", finding.Message);
    }

    [Fact]
    public void AnalyzeAsync_ShouldReportFindingForUnidirectionalImplicitManyToMany()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Article",
                        TableName = "Articles",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Articles",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations =
                        [
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Tags",
                                TargetEntityName = "Tag",
                                JoinEntityTableName = "ArticleTag",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = null
                            }
                        ]
                    },
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Tag",
                        TableName = "Tags",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Tags",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations = []
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(result);
        Assert.Equal("DB03", finding.Rule);
        Assert.Equal("Articles", finding.FilePath);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("Article", finding.Message);
        Assert.Contains("Tag", finding.Message);
        Assert.Contains("ArticleTag", finding.Message);
    }

    [Fact]
    public void AnalyzeAsync_ShouldNotReportDB03WhenNoSkipNavigations()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Category",
                        TableName = "Categories",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Categories",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations = []
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.Empty(result);
    }

    [Fact]
    public void AnalyzeAsync_ShouldNotReportDB03ForExplicitJoinEntityWithOwnClrClass()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "User",
                        TableName = "Users",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Users",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations =
                        [
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Roles",
                                TargetEntityName = "Role",
                                JoinEntityTableName = "UserRoles",
                                IsImplicitJoinEntity = false,
                                InverseNavigationName = "Users"
                            }
                        ]
                    },
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Role",
                        TableName = "Roles",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Roles",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations =
                        [
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Users",
                                TargetEntityName = "User",
                                JoinEntityTableName = "UserRoles",
                                IsImplicitJoinEntity = false,
                                InverseNavigationName = "Roles"
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.Empty(result);
    }

    [Fact]
    public void AnalyzeAsync_ShouldReportDB03ForDictionarySharedTypeJoinEntityRegardlessOfName()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Developer",
                        TableName = "Developers",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Developers",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations =
                        [
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Skills",
                                TargetEntityName = "Skill",
                                JoinEntityTableName = "CustomJoinTableName",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = "Developers"
                            }
                        ]
                    },
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Skill",
                        TableName = "Skills",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Skills",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations =
                        [
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Developers",
                                TargetEntityName = "Developer",
                                JoinEntityTableName = "CustomJoinTableName",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = "Skills"
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(result);
        Assert.Equal("DB03", finding.Rule);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("CustomJoinTableName", finding.Message);
    }

    [Fact]
    public void AnalyzeAsync_ShouldNotReportDB03ForOrdinaryOneToManyRelationship()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Order",
                        TableName = "Orders",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Orders",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        ForeignKeys =
                        [
                            new DatabaseForeignKeyMetadata
                            {
                                PrincipalEntityName = "User",
                                PropertyNames = ["UserId"],
                                PrincipalPropertyNames = ["Id"],
                                NavigationToPrincipal = "User",
                                NavigationToDependent = "Orders"
                            }
                        ],
                        Indexes =
                        [
                            new DatabaseIndexMetadata
                            {
                                Name = "IX_Orders_UserId",
                                PropertyNames = ["UserId"]
                            }
                        ],
                        SkipNavigations = []
                    },
                    new DatabaseEntityMetadata
                    {
                        EntityName = "User",
                        TableName = "Users",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Users",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations = []
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.DoesNotContain(result, f => f.Rule == "DB03");
    }

    [Fact]
    public void AnalyzeAsync_ShouldReportSingleFindingForSelfReferencingImplicitManyToMany()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "User",
                        TableName = "Users",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Users",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations =
                        [
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Friends",
                                TargetEntityName = "User",
                                JoinEntityTableName = "UserUser",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = "FriendOf"
                            },
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "FriendOf",
                                TargetEntityName = "User",
                                JoinEntityTableName = "UserUser",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = "Friends"
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(result);
        Assert.Equal("DB03", finding.Rule);
        Assert.Equal("Users", finding.FilePath);
        Assert.Contains("UserUser", finding.Message);
    }

    [Fact]
    public void AnalyzeAsync_ShouldReportTwoFindingsForTwoDistinctManyToManyRelationshipsBetweenSameEntities()
    {
        var metadata = new DatabaseMetadata
            {
                Entities =
                [
                    new DatabaseEntityMetadata
                    {
                        EntityName = "Person",
                        TableName = "Persons",
                        Keys =
                        [
                            new DatabaseKeyMetadata
                            {
                                Name = "PK_Persons",
                                PropertyNames = ["Id"],
                                IsPrimaryKey = true
                            }
                        ],
                        SkipNavigations =
                        [
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Friends",
                                TargetEntityName = "Person",
                                JoinEntityTableName = "PersonPerson",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = "FriendOf"
                            },
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "FriendOf",
                                TargetEntityName = "Person",
                                JoinEntityTableName = "PersonPerson",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = "Friends"
                            },
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "Colleagues",
                                TargetEntityName = "Person",
                                JoinEntityTableName = "PersonPerson1",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = "ColleagueOf"
                            },
                            new DatabaseSkipNavigationMetadata
                            {
                                NavigationName = "ColleagueOf",
                                TargetEntityName = "Person",
                                JoinEntityTableName = "PersonPerson1",
                                IsImplicitJoinEntity = true,
                                InverseNavigationName = "Colleagues"
                            }
                        ]
                    }
                ]
            };

        var result = DatabaseRuleEvaluator.Evaluate(metadata);

        // Two distinct physical relationships between the same entity types should yield exactly two findings
        Assert.Equal(2, result.Count);
        Assert.All(result, f => Assert.Equal("DB03", f.Rule));
        Assert.Contains(result, f => f.Message.Contains("PersonPerson"));
        Assert.Contains(result, f => f.Message.Contains("PersonPerson1"));
    }

    #region TD-004: Regression Tests for DB04 and DB05 with Real EfCoreMetadataMapper

    [Fact]
    public void DB04_WhenForeignKeyIsAlsoPrimaryKey_InOneToOne_NoDB04Reported()
    {
        using var context = CreateDb04OneToOneContext();
        var mapper = new EfCoreMetadataMapper();
        var metadata = mapper.Map(context.Model);

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.DoesNotContain(findings, f => f.Rule == "DB04");
    }

    [Fact]
    public void DB04_WhenForeignKeyIsCoveredByAlternateKey_NoDB04Reported()
    {
        using var context = CreateDb04AlternateKeyContext();
        var mapper = new EfCoreMetadataMapper();
        var metadata = mapper.Map(context.Model);

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.DoesNotContain(findings, f => f.Rule == "DB04");
    }

    [Fact]
    public void DB04_WhenForeignKeyIsCoveredByLeadingPrefixOfCompositeIndex_NoDB04Reported()
    {
        using var context = CreateDb04CompositePrefixContext();
        var mapper = new EfCoreMetadataMapper();
        var metadata = mapper.Map(context.Model);

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.DoesNotContain(findings, f => f.Rule == "DB04");
    }

    [Fact]
    public void DB04_WhenIndexHasReverseColumnOrder_ProducesDB04Finding()
    {
        using var context = CreateDb04ReverseOrderContext();
        var mapper = new EfCoreMetadataMapper();
        var metadata = mapper.Map(context.Model);

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(findings, f => f.Rule == "DB04");
        Assert.Contains("Employees", finding.FilePath);
        Assert.Contains("CountryId", finding.Message);
        Assert.Contains("CompanyId", finding.Message);
    }

    [Fact]
    public void DB04_WhenForeignKeyIsNotLeadingPrefixOfIndex_ProducesDB04Finding()
    {
        using var context = CreateDb04NonLeadingColumnContext();
        var mapper = new EfCoreMetadataMapper();
        var metadata = mapper.Map(context.Model);

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(findings, f => f.Rule == "DB04");
        Assert.Contains("Employees", finding.FilePath);
        Assert.Contains("CountryId", finding.Message);
    }

    [Fact]
    public void DB05_PrimaryKeyAndExplicitNonUniqueIndex_DoesNotProduceDB05()
    {
        using var context = CreateDb05NonUniqueOverlapContext();
        var mapper = new EfCoreMetadataMapper();
        var metadata = mapper.Map(context.Model);

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        Assert.DoesNotContain(findings, f => f.Rule == "DB05");
    }

    [Fact]
    public void DB05_PrimaryKeyAndExplicitUniqueIndex_ProducesDB05()
    {
        using var context = CreateDb05UniqueOverlapContext();
        var mapper = new EfCoreMetadataMapper();
        var metadata = mapper.Map(context.Model);

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(findings, f => f.Rule == "DB05");
        Assert.Contains("UniqueOverlaps", finding.FilePath);
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void DB05_AlternateKeyAndExplicitUniqueIndex_ProducesDB05()
    {
        using var context = CreateDb05AlternateKeyUniqueOverlapContext();
        var mapper = new EfCoreMetadataMapper();
        var metadata = mapper.Map(context.Model);

        var findings = DatabaseRuleEvaluator.Evaluate(metadata);

        var finding = Assert.Single(findings, f => f.Rule == "DB05");
        Assert.Contains("AkUniqueOverlaps", finding.FilePath);
        Assert.Equal(Severity.Info, finding.Severity);
    }

    #region Helper Contexts and Entities for DB04 and DB05 Tests

    private static Td004OneToOneDbContext CreateDb04OneToOneContext()
    {
        var options = new DbContextOptionsBuilder<Td004OneToOneDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new Td004OneToOneDbContext(options);
    }

    private static Td004AlternateKeyDbContext CreateDb04AlternateKeyContext()
    {
        var options = new DbContextOptionsBuilder<Td004AlternateKeyDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new Td004AlternateKeyDbContext(options);
    }

    private static Td004CompositePrefixDbContext CreateDb04CompositePrefixContext()
    {
        var options = new DbContextOptionsBuilder<Td004CompositePrefixDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new Td004CompositePrefixDbContext(options);
    }

    private static Td004ReverseOrderDbContext CreateDb04ReverseOrderContext()
    {
        var options = new DbContextOptionsBuilder<Td004ReverseOrderDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new Td004ReverseOrderDbContext(options);
    }

    private static Td004NonLeadingColumnDbContext CreateDb04NonLeadingColumnContext()
    {
        var options = new DbContextOptionsBuilder<Td004NonLeadingColumnDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new Td004NonLeadingColumnDbContext(options);
    }

    private static Td005NonUniqueOverlapDbContext CreateDb05NonUniqueOverlapContext()
    {
        var options = new DbContextOptionsBuilder<Td005NonUniqueOverlapDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new Td005NonUniqueOverlapDbContext(options);
    }

    private static Td005UniqueOverlapDbContext CreateDb05UniqueOverlapContext()
    {
        var options = new DbContextOptionsBuilder<Td005UniqueOverlapDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new Td005UniqueOverlapDbContext(options);
    }

    private static Td005AkUniqueOverlapDbContext CreateDb05AlternateKeyUniqueOverlapContext()
    {
        var options = new DbContextOptionsBuilder<Td005AkUniqueOverlapDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new Td005AkUniqueOverlapDbContext(options);
    }

    private sealed class Td004User
    {
        public int Id { get; set; }
        public Td004UserProfile? Profile { get; set; }
    }

    private sealed class Td004UserProfile
    {
        public int Id { get; set; }
        public Td004User User { get; set; } = null!;
    }

    private sealed class Td004OneToOneDbContext(DbContextOptions<Td004OneToOneDbContext> options) : DbContext(options)
    {
        public DbSet<Td004User> Users => Set<Td004User>();
        public DbSet<Td004UserProfile> Profiles => Set<Td004UserProfile>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Td004User>(b =>
            {
                b.ToTable("Users");
                b.HasKey(x => x.Id);
            });

            modelBuilder.Entity<Td004UserProfile>(b =>
            {
                b.ToTable("Profiles");
                b.HasKey(x => x.Id);
                b.HasOne(x => x.User)
                    .WithOne(x => x.Profile)
                    .HasForeignKey<Td004UserProfile>(x => x.Id);
            });
        }
    }

    private sealed class Td004Tenant
    {
        public int Id { get; set; }
    }

    private sealed class Td004Department
    {
        public int Id { get; set; }
        public int TenantId { get; set; }
        public string Code { get; set; } = null!;
    }

    private sealed class Td004AlternateKeyDbContext(DbContextOptions<Td004AlternateKeyDbContext> options) : DbContext(options)
    {
        public DbSet<Td004Tenant> Tenants => Set<Td004Tenant>();
        public DbSet<Td004Department> Departments => Set<Td004Department>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Td004Tenant>(b =>
            {
                b.ToTable("Tenants");
                b.HasKey(x => x.Id);
            });

            modelBuilder.Entity<Td004Department>(b =>
            {
                b.ToTable("Departments");
                b.HasKey(x => x.Id);
                b.HasAlternateKey(x => new { x.TenantId, x.Code });
                b.HasOne<Td004Tenant>()
                    .WithMany()
                    .HasForeignKey(x => x.TenantId);
            });
        }
    }

    private sealed class Td004Company
    {
        public int CountryId { get; set; }
        public int CompanyId { get; set; }
    }

    private sealed class Td004Employee
    {
        public int Id { get; set; }
        public int CountryId { get; set; }
        public int CompanyId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class Td004CompositePrefixDbContext(DbContextOptions<Td004CompositePrefixDbContext> options) : DbContext(options)
    {
        public DbSet<Td004Company> Companies => Set<Td004Company>();
        public DbSet<Td004Employee> Employees => Set<Td004Employee>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Td004Company>(b =>
            {
                b.ToTable("Companies");
                b.HasKey(x => new { x.CountryId, x.CompanyId });
            });

            modelBuilder.Entity<Td004Employee>(b =>
            {
                b.ToTable("Employees");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.CountryId, x.CompanyId, x.CreatedAt });
                b.HasOne<Td004Company>()
                    .WithMany()
                    .HasForeignKey(x => new { x.CountryId, x.CompanyId });
            });
        }
    }

    private sealed class Td004ReverseOrderDbContext(DbContextOptions<Td004ReverseOrderDbContext> options) : DbContext(options)
    {
        public DbSet<Td004Company> Companies => Set<Td004Company>();
        public DbSet<Td004Employee> Employees => Set<Td004Employee>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Td004Company>(b =>
            {
                b.ToTable("Companies");
                b.HasKey(x => new { x.CountryId, x.CompanyId });
            });

            modelBuilder.Entity<Td004Employee>(b =>
            {
                b.ToTable("Employees");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.CompanyId, x.CountryId });
                b.HasOne<Td004Company>()
                    .WithMany()
                    .HasForeignKey(x => new { x.CountryId, x.CompanyId });
            });
        }
    }

    private sealed class Td004Country
    {
        public int CountryId { get; set; }
    }

    private sealed class Td004NonLeadingColumnDbContext(DbContextOptions<Td004NonLeadingColumnDbContext> options) : DbContext(options)
    {
        public DbSet<Td004Country> Countries => Set<Td004Country>();
        public DbSet<Td004Employee> Employees => Set<Td004Employee>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Td004Country>(b =>
            {
                b.ToTable("Countries");
                b.HasKey(x => x.CountryId);
            });

            modelBuilder.Entity<Td004Employee>(b =>
            {
                b.ToTable("Employees");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.CompanyId, x.CountryId });
                b.HasOne<Td004Country>()
                    .WithMany()
                    .HasForeignKey(x => x.CountryId);
            });
        }
    }

    private sealed class Td005NonUniqueOverlapEntity
    {
        public int Id { get; set; }
    }

    private sealed class Td005NonUniqueOverlapDbContext(DbContextOptions<Td005NonUniqueOverlapDbContext> options) : DbContext(options)
    {
        public DbSet<Td005NonUniqueOverlapEntity> Overlaps => Set<Td005NonUniqueOverlapEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Td005NonUniqueOverlapEntity>(b =>
            {
                b.ToTable("NonUniqueOverlaps");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.Id).IsUnique(false);
            });
        }
    }

    private sealed class Td005UniqueOverlapEntity
    {
        public int Id { get; set; }
    }

    private sealed class Td005UniqueOverlapDbContext(DbContextOptions<Td005UniqueOverlapDbContext> options) : DbContext(options)
    {
        public DbSet<Td005UniqueOverlapEntity> Overlaps => Set<Td005UniqueOverlapEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Td005UniqueOverlapEntity>(b =>
            {
                b.ToTable("UniqueOverlaps");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.Id).IsUnique(true);
            });
        }
    }

    private sealed class Td005AkUniqueOverlapEntity
    {
        public int Id { get; set; }
        public string Code { get; set; } = null!;
    }

    private sealed class Td005AkUniqueOverlapDbContext(DbContextOptions<Td005AkUniqueOverlapDbContext> options) : DbContext(options)
    {
        public DbSet<Td005AkUniqueOverlapEntity> Overlaps => Set<Td005AkUniqueOverlapEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Td005AkUniqueOverlapEntity>(b =>
            {
                b.ToTable("AkUniqueOverlaps");
                b.HasKey(x => x.Id);
                b.HasAlternateKey(x => x.Code);
                b.HasIndex(x => x.Code).IsUnique(true);
            });
        }
    }

    #endregion

    #endregion
}
