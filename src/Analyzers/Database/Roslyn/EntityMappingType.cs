namespace DatabaseAnalyzer.Metadata;

public enum EntityMappingType
{
    Table,
    View,
    SqlQuery,
    Function,
    Unmapped,
    Unknown
}

public enum EntityKeyType
{
    PrimaryKey,
    Keyless,
    ImplicitKey,
    Unknown
}
