using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Passenger.Debugger.ExpressionEvaluator.Compiler;

/// <summary>A Visual Basic expression written again in C#, which the expression compiler reads (Roslyn publishes none for
/// Visual Basic): the operators and words (<c>AndAlso</c>, <c>Is Nothing</c>, <c>Me</c>, <c>CType</c>, <c>If(a, b)</c>), the
/// types (<c>List(Of Integer)</c>) and the literals. <c>name(1)</c> is a call or an index in Visual Basic and only one of
/// them in C#, so an expression has several readings, calls first, for the compiler to take the first that binds. What C#
/// has no words for (a query, a lambda, <c>Like</c>, a date literal) has none.</summary>
internal static class VisualBasicExpressions
{
    /// <summary>How many of an expression's calls are tried as an index too: each doubles its readings.</summary>
    private const int Tried = 3;

    /// <summary>The expression's readings in C#, the likeliest first; none where it is no Visual Basic expression, or one C# cannot say.</summary>
    public static IReadOnlyList<string> ToCSharp(string expression)
    {
        ExpressionSyntax parsed = SyntaxFactory.ParseExpression(expression, consumeFullText: true);
        if (parsed.ContainsDiagnostics || parsed.IsMissing)
        {
            return [];
        }
        List<InvocationExpressionSyntax> either = [.. parsed.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(i => i.ArgumentList is { Arguments.Count: > 0 }).Take(Tried)];
        List<string> readings = [];
        try
        {
            // Every call a call first, then one an index, then two.
            foreach (int indexed in Enumerable.Range(0, 1 << either.Count).OrderBy(int.PopCount))
            {
                HashSet<SyntaxNode> indexes = [.. either.Where((_, i) => (indexed & (1 << i)) != 0)];
                readings.Add(Written(parsed, indexes));
            }
        }
        catch (NotSupportedException)
        {
            return [];
        }
        return readings;
    }

    private static string Written(SyntaxNode? node, HashSet<SyntaxNode> indexes)
    {
        string W(SyntaxNode? inner) => Written(inner, indexes);
        switch (node)
        {
            case ParenthesizedExpressionSyntax inner:
                return $"({W(inner.Expression)})";
            case LiteralExpressionSyntax literal:
                return Literal(literal);
            case MeExpressionSyntax or MyClassExpressionSyntax:
                return "this";
            case MyBaseExpressionSyntax:
                return "base";
            case PredefinedTypeSyntax type:
                return Type(type.Keyword);
            case IdentifierNameSyntax name:
                return Name(name.Identifier);
            case GenericNameSyntax generic:
                return $"{Name(generic.Identifier)}<{string.Join(", ", generic.TypeArgumentList.Arguments.Select(W))}>";
            case QualifiedNameSyntax qualified:
                return $"{W(qualified.Left)}.{W(qualified.Right)}";
            case NullableTypeSyntax nullable:
                return W(nullable.ElementType) + "?";
            case ArrayTypeSyntax array:
                return W(array.ElementType) + string.Concat(array.RankSpecifiers.Select(r => $"[{new string(',', r.CommaTokens.Count)}]"));
            case MemberAccessExpressionSyntax member when member.OperatorToken.IsKind(SyntaxKind.DotToken):
                // With no expression before its dot it follows a ?. whose target is written before it.
                return $"{(member.Expression is null ? string.Empty : W(member.Expression))}.{W(member.Name)}";
            case ConditionalAccessExpressionSyntax { Expression: { } target } conditional:
                return $"{W(target)}?{W(conditional.WhenNotNull)}";
            case InvocationExpressionSyntax invocation:
                string called = invocation.Expression is null ? string.Empty : W(invocation.Expression);
                string arguments = string.Join(", ", (invocation.ArgumentList?.Arguments ?? default).Select(W));
                return indexes.Contains(invocation) ? $"{called}[{arguments}]" : $"{called}({arguments})";
            case SimpleArgumentSyntax argument:
                return argument.NameColonEquals is { } named ? $"{Name(named.Name.Identifier)}: {W(argument.Expression)}" : W(argument.Expression);
            case ObjectCreationExpressionSyntax made:
                string created = $"new {W(made.Type)}({string.Join(", ", (made.ArgumentList?.Arguments ?? default).Select(W))})";
                return made.Initializer switch
                {
                    null => created,
                    ObjectCollectionInitializerSyntax items => $"{created} {{ {string.Join(", ", items.Initializer.Initializers.Select(W))} }}",
                    ObjectMemberInitializerSyntax members => $"{created} {{ {string.Join(", ", members.Initializers.Select(W))} }}",
                    _ => throw new NotSupportedException(),
                };
            case NamedFieldInitializerSyntax field:
                return $"{Name(field.Name.Identifier)} = {W(field.Expression)}";
            case ArrayCreationExpressionSyntax array:
                return $"new {W(array.Type)}[] {{ {string.Join(", ", array.Initializer.Initializers.Select(W))} }}";
            case CollectionInitializerSyntax items:
                return $"new[] {{ {string.Join(", ", items.Initializers.Select(W))} }}";
            case TupleExpressionSyntax tuple:
                return $"({string.Join(", ", tuple.Arguments.Select(W))})";
            case UnaryExpressionSyntax unary:
                return unary.Kind() switch
                {
                    SyntaxKind.NotExpression => $"!({W(unary.Operand)})",
                    SyntaxKind.UnaryMinusExpression => $"-({W(unary.Operand)})",
                    SyntaxKind.UnaryPlusExpression => $"+({W(unary.Operand)})",
                    _ => throw new NotSupportedException(),
                };
            case BinaryExpressionSyntax binary:
                return Binary(binary, W(binary.Left), W(binary.Right));
            case TernaryConditionalExpressionSyntax choice:
                return $"(({W(choice.Condition)}) ? ({W(choice.WhenTrue)}) : ({W(choice.WhenFalse)}))";
            case BinaryConditionalExpressionSyntax either:
                return $"(({W(either.FirstExpression)}) ?? ({W(either.SecondExpression)}))";
            case TryCastExpressionSyntax cast:
                return $"(({W(cast.Expression)}) as {W(cast.Type)})";
            case CastExpressionSyntax cast:
                return $"(({W(cast.Type)})({W(cast.Expression)}))";
            case PredefinedCastExpressionSyntax cast:
                return cast.Keyword.IsKind(SyntaxKind.CObjKeyword) ? $"((object)({W(cast.Expression)}))" : $"System.Convert.{Conversion(cast.Keyword)}({W(cast.Expression)})";
            case TypeOfExpressionSyntax typed:
                return typed.IsKind(SyntaxKind.TypeOfIsNotExpression) ? $"!(({W(typed.Expression)}) is {W(typed.Type)})" : $"(({W(typed.Expression)}) is {W(typed.Type)})";
            case GetTypeExpressionSyntax reflected:
                return $"typeof({W(reflected.Type)})";
            case NameOfExpressionSyntax asked:
                return $"nameof({W(asked.Argument)})";
            case InterpolatedStringExpressionSyntax interpolated:
                return Interpolated(interpolated, indexes);
            default:
                throw new NotSupportedException();
        }
    }

    private static string Binary(BinaryExpressionSyntax binary, string left, string right)
    {
        bool nothing = binary.Left.IsKind(SyntaxKind.NothingLiteralExpression) || binary.Right.IsKind(SyntaxKind.NothingLiteralExpression);
        string? written = binary.Kind() switch
        {
            SyntaxKind.AddExpression => "+",
            SyntaxKind.SubtractExpression => "-",
            SyntaxKind.MultiplyExpression => "*",
            SyntaxKind.DivideExpression or SyntaxKind.IntegerDivideExpression => "/",
            SyntaxKind.ModuloExpression => "%",
            SyntaxKind.EqualsExpression => "==",
            SyntaxKind.NotEqualsExpression => "!=",
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            SyntaxKind.GreaterThanExpression => ">",
            SyntaxKind.GreaterThanOrEqualExpression => ">=",
            SyntaxKind.AndAlsoExpression => "&&",
            SyntaxKind.OrElseExpression => "||",
            SyntaxKind.AndExpression => "&",
            SyntaxKind.OrExpression => "|",
            SyntaxKind.ExclusiveOrExpression => "^",
            SyntaxKind.LeftShiftExpression => "<<",
            SyntaxKind.RightShiftExpression => ">>",
            _ => null,
        };
        if (written is not null)
        {
            return $"{left} {written} {right}";
        }
        return binary.Kind() switch
        {
            SyntaxKind.ConcatenateExpression => $"string.Concat({left}, {right})",
            SyntaxKind.ExponentiateExpression => $"System.Math.Pow({left}, {right})",
            // Against Nothing a value's own null is asked, a Nullable's too; else whether the two are one object.
            SyntaxKind.IsExpression => nothing ? $"{left} == {right}" : $"object.ReferenceEquals({left}, {right})",
            SyntaxKind.IsNotExpression => nothing ? $"{left} != {right}" : $"!object.ReferenceEquals({left}, {right})",
            _ => throw new NotSupportedException(),
        };
    }

    private static string Literal(LiteralExpressionSyntax literal)
    {
        if (literal.IsKind(SyntaxKind.NothingLiteralExpression))
        {
            return "null";
        }
        string suffix = literal.Token.Value switch
        {
            long => "L",
            ulong => "UL",
            uint => "U",
            float => "F",
            double => "D",
            decimal => "M",
            _ => string.Empty,
        };
        return literal.Token.Value switch
        {
            bool value => value ? "true" : "false",
            string text => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(text, quote: true),
            char one => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(one, quote: true),
            IFormattable number and not DateTime => number.ToString(number is float or double ? "R" : null, CultureInfo.InvariantCulture) + suffix,
            _ => throw new NotSupportedException(),
        };
    }

    private static string Interpolated(InterpolatedStringExpressionSyntax interpolated, HashSet<SyntaxNode> indexes)
    {
        StringBuilder written = new("$\"");
        foreach (InterpolatedStringContentSyntax part in interpolated.Contents)
        {
            if (part is InterpolatedStringTextSyntax text)
            {
                // The token's value has a doubled quote as one and keeps a doubled brace, as C# writes a brace too.
                written.Append(text.TextToken.ValueText.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal));
            }
            else if (part is InterpolationSyntax hole)
            {
                written.Append('{').Append('(').Append(Written(hole.Expression, indexes)).Append(')');
                written.Append(hole.AlignmentClause is { } aligned ? "," + Written(aligned.Value, indexes) : string.Empty);
                written.Append(hole.FormatClause is { } format ? ":" + format.FormatStringToken.ValueText : string.Empty).Append('}');
            }
        }
        return written.Append('"').ToString();
    }

    private static string Name(SyntaxToken identifier) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(identifier.ValueText) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None && identifier.ValueText != "this"
            ? "@" + identifier.ValueText : identifier.ValueText;

    private static string Type(SyntaxToken keyword) => keyword.Kind() switch
    {
        SyntaxKind.IntegerKeyword => "int",
        SyntaxKind.LongKeyword => "long",
        SyntaxKind.ShortKeyword => "short",
        SyntaxKind.ByteKeyword => "byte",
        SyntaxKind.SByteKeyword => "sbyte",
        SyntaxKind.UIntegerKeyword => "uint",
        SyntaxKind.ULongKeyword => "ulong",
        SyntaxKind.UShortKeyword => "ushort",
        SyntaxKind.SingleKeyword => "float",
        SyntaxKind.DoubleKeyword => "double",
        SyntaxKind.DecimalKeyword => "decimal",
        SyntaxKind.BooleanKeyword => "bool",
        SyntaxKind.CharKeyword => "char",
        SyntaxKind.StringKeyword => "string",
        SyntaxKind.ObjectKeyword => "object",
        SyntaxKind.DateKeyword => "System.DateTime",
        _ => throw new NotSupportedException(),
    };

    private static string Conversion(SyntaxToken keyword) => keyword.Kind() switch
    {
        SyntaxKind.CIntKeyword => "ToInt32",
        SyntaxKind.CLngKeyword => "ToInt64",
        SyntaxKind.CShortKeyword => "ToInt16",
        SyntaxKind.CByteKeyword => "ToByte",
        SyntaxKind.CSByteKeyword => "ToSByte",
        SyntaxKind.CUIntKeyword => "ToUInt32",
        SyntaxKind.CULngKeyword => "ToUInt64",
        SyntaxKind.CUShortKeyword => "ToUInt16",
        SyntaxKind.CSngKeyword => "ToSingle",
        SyntaxKind.CDblKeyword => "ToDouble",
        SyntaxKind.CDecKeyword => "ToDecimal",
        SyntaxKind.CBoolKeyword => "ToBoolean",
        SyntaxKind.CCharKeyword => "ToChar",
        SyntaxKind.CStrKeyword => "ToString",
        SyntaxKind.CDateKeyword => "ToDateTime",
        _ => throw new NotSupportedException(),
    };
}
