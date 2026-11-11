using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DatabaseAnalyzer.Roslyn;

internal sealed class LocalMethodTraverser
{
    private const int MaxTraversalDepth = 3;
    private readonly HashSet<MethodDeclarationSyntax> _currentCallStack = [];
    private readonly Dictionary<string, List<MethodDeclarationSyntax>> _classMethods = new(StringComparer.Ordinal);
    private readonly List<string> _warnings;

    public LocalMethodTraverser(
        IEnumerable<ClassDeclarationSyntax> classDeclarations,
        List<string> warnings)
    {
        _warnings = warnings;

        foreach (var classDecl in classDeclarations)
        {
            foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodName = method.Identifier.ValueText;
                if (!_classMethods.TryGetValue(methodName, out var list))
                {
                    list = [];
                    _classMethods[methodName] = list;
                }

                list.Add(method);
            }
        }
    }

    public bool TryTraverseLocalMethod(
        InvocationExpressionSyntax invocation,
        int currentDepth,
        Action<MethodDeclarationSyntax, int> methodBodyHandler)
    {
        string methodName;

        if (invocation.Expression is IdentifierNameSyntax id)
        {
            methodName = id.Identifier.ValueText;
        }
        else if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                 memberAccess.Expression is ThisExpressionSyntax)
        {
            methodName = memberAccess.Name.Identifier.ValueText;
        }
        else
        {
            return false;
        }

        if (!_classMethods.TryGetValue(methodName, out var candidates) || candidates.Count == 0)
        {
            return false;
        }

        // Берём подходящую перегрузку
        var targetMethod = candidates.FirstOrDefault(m => m.ParameterList.Parameters.Count == invocation.ArgumentList.Arguments.Count)
            ?? candidates[0];

        if (currentDepth >= MaxTraversalDepth)
        {
            _warnings.Add($"Превышена максимальная глубина обхода локальных методов конфигурации (глубина > {MaxTraversalDepth}) при вызове метода '{methodName}'.");
            return true;
        }

        if (_currentCallStack.Contains(targetMethod))
        {
            _warnings.Add($"Обнаружен циклический вызов локального метода конфигурации '{methodName}'. Обход прерван для предотвращения зацикливания.");
            return true;
        }

        _currentCallStack.Add(targetMethod);
        try
        {
            methodBodyHandler(targetMethod, currentDepth + 1);
        }
        finally
        {
            _currentCallStack.Remove(targetMethod);
        }

        return true;
    }
}
