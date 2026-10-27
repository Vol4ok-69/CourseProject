using Application.Analyzers;
using DatabaseAnalyzer;
using DatabaseAnalyzer.Metadata;
using DatabaseAnalyzer.Providers;
using Domain.Enums;
using Moq;

namespace DatabaseAnalyzer.Tests;

public sealed class DatabaseAnalyzerTests
{
    [Fact]
    public async Task Type_ShouldReturnDatabase()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);

        Assert.Equal(AnalyzerType.Database, analyzer.Type);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldCallMetadataProvider()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider.Setup
        (
            provider => provider
                .GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DatabaseMetadata()
        );

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);

        var context = new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        };

        var result = await analyzer.AnalyzeAsync(context);

        metadataProvider.Verify(
            provider => provider.GetMetadataAsync(
                context,
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldPropagateMetadataProviderException()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider.Setup(provider => provider.GetMetadataAsync
        (
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()
        )).ThrowsAsync(new InvalidOperationException("Test error"));

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);

        var context = new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => analyzer.AnalyzeAsync(context));

        Assert.Equal("Test error", exception.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReportEntityWithoutPrimaryKey()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);

        var context = new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        };

        var findings = await analyzer.AnalyzeAsync(context);

        var finding = Assert.Single(findings);
        Assert.Equal("DB01", finding.Rule);
        Assert.Equal("ReportRows", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("ReportRow", finding.Message);
        Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotReportEntityWithPrimaryKey()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(
                It.IsAny<AnalyzerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);

        var context = new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        };

        var findings = await analyzer.AnalyzeAsync(context);

        Assert.Empty(findings);
    }


    [Fact]
    public async Task AnalyzeAsync_ShouldReportForeignKeyWithoutIndex()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        var finding = Assert.Single(result);
        Assert.Equal("DB04", finding.Rule);
        Assert.Equal("Orders", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("CustomerId", finding.Message);
        Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotReportForeignKeyWithMatchingIndex()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        Assert.Empty(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldAcceptCompositeIndexStartingWithForeignKeyColumns()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        Assert.Empty(result);
    }


    [Fact]
    public async Task AnalyzeAsync_ShouldReportDuplicateIndexes()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        var finding = Assert.Single(result);
        Assert.Equal("DB05", finding.Rule);
        Assert.Equal("Orders", finding.FilePath);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("CustomerId", finding.Message);
        Assert.False(string.IsNullOrWhiteSpace(finding.Recommendation));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotReportIndexesWithDifferentPropertyOrderAsDuplicates()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        Assert.Empty(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotReportIndexesWithDifferentUniquenessAsDuplicates()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();

        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        Assert.Empty(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReportForeignKeyWithoutAnyNavigation()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

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
    public async Task AnalyzeAsync_ShouldReportForeignKeyWhenOnlyDependentNavigationExists()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        var finding = Assert.Single(result);
        Assert.Equal("DB02", finding.Rule);
        Assert.Equal("Orders", finding.FilePath);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("FK_Orders_Users_UserId", finding.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotReportForeignKeyWhenPrincipalNavigationExistsEvenWithoutDependentNavigation()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        Assert.Empty(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotReportForeignKeyWhenBothNavigationsExist()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        Assert.Empty(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldHandleMultipleForeignKeysIndependently()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        var finding = Assert.Single(result);
        Assert.Equal("DB02", finding.Rule);
        Assert.Contains("FK_Orders_PaymentMethods_PaymentMethodId", finding.Message);
        Assert.Contains("PaymentMethod", finding.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReportDB03WithInfoSeverityForImplicitManyToMany()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

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
    public async Task AnalyzeAsync_ShouldReportSingleFindingForBidirectionalImplicitManyToMany()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        var finding = Assert.Single(result);
        Assert.Equal("DB03", finding.Rule);
        Assert.Equal("Courses", finding.FilePath);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("Course", finding.Message);
        Assert.Contains("Student", finding.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReportFindingForUnidirectionalImplicitManyToMany()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        var finding = Assert.Single(result);
        Assert.Equal("DB03", finding.Rule);
        Assert.Equal("Articles", finding.FilePath);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("Article", finding.Message);
        Assert.Contains("Tag", finding.Message);
        Assert.Contains("ArticleTag", finding.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotReportDB03WhenNoSkipNavigations()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        Assert.Empty(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotReportDB03ForExplicitJoinEntityWithOwnClrClass()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        Assert.Empty(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReportDB03ForDictionarySharedTypeJoinEntityRegardlessOfName()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        var finding = Assert.Single(result);
        Assert.Equal("DB03", finding.Rule);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("CustomJoinTableName", finding.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotReportDB03ForOrdinaryOneToManyRelationship()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        Assert.DoesNotContain(result, f => f.Rule == "DB03");
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReportSingleFindingForSelfReferencingImplicitManyToMany()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        var finding = Assert.Single(result);
        Assert.Equal("DB03", finding.Rule);
        Assert.Equal("Users", finding.FilePath);
        Assert.Contains("UserUser", finding.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReportTwoFindingsForTwoDistinctManyToManyRelationshipsBetweenSameEntities()
    {
        var metadataProvider = new Mock<IDatabaseMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.GetMetadataAsync(It.IsAny<AnalyzerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadata
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
            });

        var analyzer = new DatabaseAnalyzer(metadataProvider.Object);
        var result = await analyzer.AnalyzeAsync(new AnalyzerContext
        {
            RepositoryPath = @"C:\Test\Repository",
            CommitHash = "test-commit"
        });

        // Two distinct physical relationships between the same entity types should yield exactly two findings
        Assert.Equal(2, result.Count);
        Assert.All(result, f => Assert.Equal("DB03", f.Rule));
        Assert.Contains(result, f => f.Message.Contains("PersonPerson"));
        Assert.Contains(result, f => f.Message.Contains("PersonPerson1"));
    }
}
