using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Passenger.Core;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Metadata;

/// <summary>The types one Visual Basic file of a project declares, read from its syntax alone, as <see cref="SourceTypes"/>
/// reads a C# file's: what the project's XAML names before any build, its view models' properties, a code-behind's
/// handlers. Every namespace it declares lies in the project's root namespace, as the compiler puts it, unless written from
/// <c>Global</c>; a member written without its access is the language's default (a field private, but a structure's, and
/// anything else public), and a module's members are shared.</summary>
public static class VisualBasicTypes
{
    /// <summary>How deep a type's arguments are read, past what any type is written to.</summary>
    private const int DeepestType = 64;

    /// <summary>A Visual Basic file's types, in <paramref name="rootNamespace"/> (none for an empty one); a file nested past
    /// what the parser can read declares none.</summary>
    public static SourceTypes Read(string path, string text, string assemblyName, string? rootNamespace)
    {
        if (DeepText.TooDeep(text))
        {
            return SourceTypes.Of(path, [], [], []);
        }
        return DeepText.Read(text.Length, () =>
        {
            SyntaxTree tree = VisualBasicSyntaxTree.ParseText(SourceText.From(text), null, path);
            Reader reader = new(path, tree, assemblyName);
            reader.Unit((CompilationUnitSyntax)tree.GetRoot(), rootNamespace is { Length: > 0 } root ? root : null);
            return SourceTypes.Of(path, reader.Types, [], reader.AssemblyAttributes);
        });
    }

    private sealed class Reader(string path, SyntaxTree tree, string assemblyName)
    {
        public List<ClrType> Types { get; } = [];

        public List<ClrAttribute> AssemblyAttributes { get; } = [];

        public void Unit(CompilationUnitSyntax root, string? rootNamespace)
        {
            List<string> usings = [];
            Dictionary<string, string> aliases = new(StringComparer.Ordinal);
            foreach (SimpleImportsClauseSyntax import in root.Imports.SelectMany(i => i.ImportsClauses).OfType<SimpleImportsClauseSyntax>())
            {
                string name = NameOf(import.Name).Name;
                if (import.Alias is { } alias)
                {
                    aliases[alias.Identifier.ValueText] = name;
                }
                else
                {
                    usings.Add(name);
                }
            }
            foreach (AttributeSyntax attribute in root.Attributes.SelectMany(s => s.AttributeLists).SelectMany(l => l.Attributes))
            {
                if (attribute.Target?.AttributeModifier.IsKind(SyntaxKind.AssemblyKeyword) == true)
                {
                    AssemblyAttributes.Add(Attribute(attribute));
                }
            }
            Members(root.Members, rootNamespace, usings, aliases, []);
        }

        private void Members(SyntaxList<StatementSyntax> members, string? @namespace, List<string> usings, Dictionary<string, string> aliases, List<string> enclosing)
        {
            foreach (StatementSyntax member in members)
            {
                switch (member)
                {
                    case NamespaceBlockSyntax block:
                        (string name, bool global) = NameOf(block.NamespaceStatement.Name);
                        string inner = global || @namespace is null ? name : name.Length == 0 ? @namespace : $"{@namespace}.{name}";
                        Members(block.Members, inner.Length == 0 ? null : inner, usings, aliases, enclosing);
                        break;
                    case TypeBlockSyntax or EnumBlockSyntax:
                        Type(member, @namespace ?? string.Empty, new SourceScope(@namespace, usings, aliases, enclosing), enclosing);
                        break;
                    case DelegateStatementSyntax @delegate:
                        Types.Add(new ClrType(@namespace ?? string.Empty, Arity(@delegate.Identifier.ValueText, @delegate.TypeParameterList?.Parameters.Count ?? 0),
                            ClrTypeKind.Delegate, Origin(@delegate.Identifier), _ => ClrTypeDetails.None, enclosing.Count > 0 ? enclosing[^1] : null));
                        break;
                }
            }
        }

        private void Type(StatementSyntax declaration, string @namespace, SourceScope scope, List<string> enclosing)
        {
            (SyntaxToken identifier, int arity, ClrTypeKind kind) = declaration switch
            {
                EnumBlockSyntax @enum => (@enum.EnumStatement.Identifier, 0, ClrTypeKind.Enum),
                InterfaceBlockSyntax face => (face.BlockStatement.Identifier, face.BlockStatement.TypeParameterList?.Parameters.Count ?? 0, ClrTypeKind.Interface),
                StructureBlockSyntax structure => (structure.BlockStatement.Identifier, structure.BlockStatement.TypeParameterList?.Parameters.Count ?? 0, ClrTypeKind.Struct),
                TypeBlockSyntax type => (type.BlockStatement.Identifier, type.BlockStatement.TypeParameterList?.Parameters.Count ?? 0, ClrTypeKind.Class),
                _ => throw new ArgumentException("Not a type's declaration.", nameof(declaration)),
            };
            ClrType made = new(@namespace, Arity(identifier.ValueText, arity), kind, Origin(identifier), t => Details(t, declaration, scope),
                enclosing.Count > 0 ? enclosing[^1] : null);
            // Read now, so the type holds what it declares and not the file's whole syntax tree.
            _ = made.Members;
            Types.Add(made);
            if (declaration is TypeBlockSyntax withMembers)
            {
                List<string> inner = [.. enclosing, made.FullName];
                SourceScope innerScope = scope with { Enclosing = inner };
                foreach (StatementSyntax nested in withMembers.Members.Where(m => m is TypeBlockSyntax or EnumBlockSyntax))
                {
                    Type(nested, @namespace, innerScope, inner);
                }
            }
        }

        private ClrTypeDetails Details(ClrType type, StatementSyntax declaration, SourceScope scope)
        {
            SourceScope inside = scope with { Enclosing = [.. scope.Enclosing, type.FullName] };
            if (declaration is EnumBlockSyntax @enum)
            {
                TypeRef self = new(type.FullName, []);
                List<ClrMember> values = [.. @enum.Members.OfType<EnumMemberDeclarationSyntax>()
                    .Select(m => new ClrMember(type, m.Identifier.ValueText, MemberKind.Field, self, true, true, false, place: Place(m.Identifier)))];
                return new ClrTypeDetails(TypeRef.Named("System.Enum"), [], Attributes(@enum.EnumStatement.AttributeLists), values, false, false,
                    IsVisible(@enum.EnumStatement.Modifiers, type.IsNested), false, []);
            }
            TypeBlockSyntax typed = (TypeBlockSyntax)declaration;
            TypeStatementSyntax statement = typed.BlockStatement;
            bool isModule = typed is ModuleBlockSyntax;
            bool isInterface = typed is InterfaceBlockSyntax;
            bool isStructure = typed is StructureBlockSyntax;
            List<TypeRef> inherited = [.. typed.Inherits.SelectMany(i => i.Types).Select(t => Reference(t, inside))];
            List<TypeRef> implemented = [.. typed.Implements.SelectMany(i => i.Types).Select(t => Reference(t, inside))];
            List<ClrMember> members = [];
            bool anyConstructor = false;
            bool defaultConstructor = false;
            foreach (StatementSyntax member in typed.Members)
            {
                switch (member)
                {
                    case PropertyBlockSyntax block:
                        Property(block.PropertyStatement, block.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorBlock) && Reachable(a.AccessorStatement.Modifiers, inherit: true) == true),
                            Given(block));
                        break;
                    case PropertyStatementSyntax property:
                        Property(property, !property.Modifiers.Any(SyntaxKind.ReadOnlyKeyword), null);
                        break;
                    case FieldDeclarationSyntax field:
                        bool shared = isModule || field.Modifiers.Any(SyntaxKind.SharedKeyword) || field.Modifiers.Any(SyntaxKind.ConstKeyword);
                        bool readOnly = field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) || field.Modifiers.Any(SyntaxKind.ConstKeyword);
                        bool seen = Reachable(field.Modifiers) ?? isStructure;
                        IReadOnlyList<ClrAttribute> fieldAttributes = Attributes(field.AttributeLists);
                        foreach (VariableDeclaratorSyntax declarator in field.Declarators)
                        {
                            TypeRef declared = AsType(declarator.AsClause, inside);
                            foreach (ModifiedIdentifierSyntax name in declarator.Names)
                            {
                                TypeRef its = name.ArrayBounds is not null || name.ArrayRankSpecifiers.Count > 0 ? declared with { IsArray = true } : declared;
                                members.Add(new ClrMember(type, name.Identifier.ValueText, MemberKind.Field, Registered(its, declarator, inside), shared, seen, !readOnly,
                                    attributes: fieldAttributes, place: Place(name.Identifier)));
                            }
                        }
                        break;
                    case MethodBlockSyntax block:
                        Method(block.SubOrFunctionStatement);
                        break;
                    case MethodStatementSyntax method:
                        Method(method);
                        break;
                    case ConstructorBlockSyntax constructor when !constructor.SubNewStatement.Modifiers.Any(SyntaxKind.SharedKeyword):
                        SubNewStatementSyntax made = constructor.SubNewStatement;
                        bool reached = Reachable(made.Modifiers) ?? true;
                        IReadOnlyList<ParameterSyntax> taken = made.ParameterList?.Parameters ?? [];
                        anyConstructor = true;
                        defaultConstructor |= taken.Count == 0 && reached;
                        members.Add(new ClrMember(type, ".ctor", MemberKind.Constructor, TypeRef.Named("System.Void"), false, reached, false,
                            [.. taken.Select(p => AsType(p.AsClause, inside))], [.. taken.Select(p => p.Identifier.Identifier.ValueText)],
                            Attributes(made.AttributeLists), Place(made.NewKeyword)));
                        break;
                    case EventStatementSyntax @event:
                        Event(@event);
                        break;
                    case EventBlockSyntax block:
                        Event(block.EventStatement);
                        break;
                }
            }
            string[] generics = statement.TypeParameterList is { } list ? [.. list.Parameters.Select(p => p.Identifier.ValueText)] : [];
            TypeRef? baseType = isInterface ? null : inherited.Count > 0 ? inherited[0] : isStructure ? TypeRef.Named("System.ValueType") : null;
            IReadOnlyList<TypeRef> interfaces = isInterface ? [.. inherited, .. implemented] : implemented;
            return new ClrTypeDetails(baseType, interfaces, Attributes(statement.AttributeLists), members, statement.Modifiers.Any(SyntaxKind.MustInheritKeyword),
                isModule, IsVisible(statement.Modifiers, type.IsNested), !anyConstructor || defaultConstructor, generics);

            void Property(PropertyStatementSyntax property, bool writable, string? constant)
            {
                bool shared = isModule || property.Modifiers.Any(SyntaxKind.SharedKeyword);
                members.Add(new ClrMember(type, property.Identifier.ValueText, MemberKind.Property, AsType(property.AsClause, inside), shared,
                    isInterface || (Reachable(property.Modifiers) ?? true), writable && !property.Modifiers.Any(SyntaxKind.ReadOnlyKeyword),
                    attributes: Attributes(property.AttributeLists), place: Place(property.Identifier), constant: shared ? constant : null));
            }

            void Method(MethodStatementSyntax method)
            {
                IReadOnlyList<ParameterSyntax> taken = method.ParameterList?.Parameters ?? [];
                TypeRef returned = method.IsKind(SyntaxKind.FunctionStatement) ? AsType(method.AsClause, inside) : TypeRef.Named("System.Void");
                members.Add(new ClrMember(type, method.Identifier.ValueText, MemberKind.Method, returned, isModule || method.Modifiers.Any(SyntaxKind.SharedKeyword),
                    isInterface || (Reachable(method.Modifiers) ?? true), false, [.. taken.Select(p => AsType(p.AsClause, inside))],
                    [.. taken.Select(p => p.Identifier.Identifier.ValueText)], Attributes(method.AttributeLists), Place(method.Identifier)));
            }

            void Event(EventStatementSyntax @event)
            {
                // An event declared by its parameters has a delegate of its own, which the compiler names after it.
                TypeRef handler = @event.AsClause is { } typedAs ? Reference(typedAs.Type, inside) : new TypeRef($"{@event.Identifier.ValueText}EventHandler", [], Scope: inside);
                members.Add(new ClrMember(type, @event.Identifier.ValueText, MemberKind.Event, handler, isModule || @event.Modifiers.Any(SyntaxKind.SharedKeyword),
                    isInterface || (Reachable(@event.Modifiers) ?? true), true, place: Place(@event.Identifier)));
            }
        }

        /// <summary>The string a shared property's getter first gives a call or a constructor, as a class a .resx generates
        /// writes its key (<c>ResourceManager.GetString("Welcome", resourceCulture)</c>) and its ResourceManager's name; null
        /// for none.</summary>
        private static string? Given(PropertyBlockSyntax property)
        {
            foreach (SyntaxNode node in property.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorBlock))?.DescendantNodes() ?? [])
            {
                ArgumentListSyntax? arguments = node switch
                {
                    InvocationExpressionSyntax call => call.ArgumentList,
                    ObjectCreationExpressionSyntax made => made.ArgumentList,
                    _ => null,
                };
                if (arguments?.Arguments is [SimpleArgumentSyntax { Expression: LiteralExpressionSyntax literal }, ..] && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    return literal.Token.ValueText;
                }
            }
            return null;
        }

        /// <summary>An Avalonia property's field declared by its untyped base (<c>AvaloniaProperty</c>) takes the property
        /// type its registration (<c>AvaloniaProperty.Register(Of Owner, T)(…)</c>) makes, whose value type a XAML value is
        /// read as.</summary>
        private static TypeRef Registered(TypeRef declared, VariableDeclaratorSyntax declarator, SourceScope scope)
        {
            if (declared.Arguments.Count > 0 || declarator.Initializer?.Value is not InvocationExpressionSyntax call
                || call.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "AvaloniaProperty" }, Name: GenericNameSyntax generic }
                || generic.TypeArgumentList.Arguments.Count == 0)
            {
                return declared;
            }
            TypeRef value = Reference(generic.TypeArgumentList.Arguments[^1], scope);
            return generic.Identifier.ValueText switch
            {
                "Register" => new TypeRef("Avalonia.StyledProperty`1", [value]),
                "RegisterAttached" => new TypeRef("Avalonia.AttachedProperty`1", [value]),
                "RegisterDirect" => new TypeRef("Avalonia.DirectProperty`2", [Reference(generic.TypeArgumentList.Arguments[0], scope), value]),
                _ => declared,
            };
        }

        private static IReadOnlyList<ClrAttribute> Attributes(SyntaxList<AttributeListSyntax> lists) => [.. lists.SelectMany(l => l.Attributes).Select(Attribute)];

        private static ClrAttribute Attribute(AttributeSyntax attribute)
        {
            string name = attribute.Name is NameSyntax named ? NameOf(named).Name : attribute.Name.ToString();
            if (!name.EndsWith("Attribute", StringComparison.Ordinal))
            {
                name += "Attribute";
            }
            List<object?> positional = [];
            List<KeyValuePair<string, object?>> keyed = [];
            foreach (SimpleArgumentSyntax argument in attribute.ArgumentList?.Arguments.OfType<SimpleArgumentSyntax>() ?? [])
            {
                object? value = argument.Expression switch
                {
                    LiteralExpressionSyntax literal => literal.Token.Value,
                    GetTypeExpressionSyntax getType when getType.Type is NameSyntax typeName => NameOf(typeName).Name,
                    NameOfExpressionSyntax nameOf => nameOf.Argument.ToString().Split('.')[^1],
                    _ => argument.Expression.ToString(),
                };
                if (argument.NameColonEquals is { } equals)
                {
                    keyed.Add(new KeyValuePair<string, object?>(equals.Name.Identifier.ValueText, value));
                }
                else
                {
                    positional.Add(value);
                }
            }
            return new ClrAttribute(name, positional, keyed);
        }

        private TypeOrigin Origin(SyntaxToken identifier) => new(assemblyName, null, Place(identifier));

        private SourcePlace Place(SyntaxToken identifier)
        {
            LinePosition start = tree.GetLineSpan(identifier.Span).StartLinePosition;
            return new SourcePlace(path, start.Line, start.Character);
        }
    }

    /// <summary>The type an <c>As</c> clause names, <c>As New T(…)</c>'s too; an object where none is written.</summary>
    private static TypeRef AsType(AsClauseSyntax? clause, SourceScope scope) => clause switch
    {
        SimpleAsClauseSyntax simple => Reference(simple.Type, scope),
        AsNewClauseSyntax { NewExpression: ObjectCreationExpressionSyntax made } => Reference(made.Type, scope),
        _ => TypeRef.Named("System.Object"),
    };

    /// <summary>A type written in source as a reference to resolve in its scope; one nested past <see cref="DeepestType"/>
    /// an object.</summary>
    private static TypeRef Reference(TypeSyntax syntax, SourceScope scope) => Reference(syntax, scope, 0);

    private static TypeRef Reference(TypeSyntax syntax, SourceScope scope, int depth)
    {
        if (depth > DeepestType || !StackLeft.Enough())
        {
            return TypeRef.Named("System.Object");
        }
        switch (syntax)
        {
            case PredefinedTypeSyntax predefined:
                return TypeRef.Named(Predefined(predefined.Keyword.Kind()));
            case NullableTypeSyntax nullable:
                return Reference(nullable.ElementType, scope, depth + 1);
            case ArrayTypeSyntax array:
                return Reference(array.ElementType, scope, depth + 1) with { IsArray = true };
            case GenericNameSyntax generic:
                return new TypeRef(Arity(generic.Identifier.ValueText, generic.TypeArgumentList.Arguments.Count),
                    [.. generic.TypeArgumentList.Arguments.Select(a => Reference(a, scope, depth + 1))], Scope: scope);
            case QualifiedNameSyntax qualified:
                TypeRef right = Reference(qualified.Right, scope, depth + 1);
                string left = NameOf(qualified.Left).Name;
                return right with { Name = left.Length == 0 ? right.Name : $"{left}.{right.Name}" };
            case IdentifierNameSyntax identifier:
                return new TypeRef(identifier.Identifier.ValueText, [], Scope: scope);
            case TupleTypeSyntax tuple:
                return new TypeRef($"System.ValueTuple`{tuple.Elements.Count}", [.. tuple.Elements.Select(e => e switch
                {
                    TypedTupleElementSyntax typed => Reference(typed.Type, scope, depth + 1),
                    NamedTupleElementSyntax { AsClause: { } named } => Reference(named.Type, scope, depth + 1),
                    _ => TypeRef.Named("System.Object"),
                })]);
            default:
                return TypeRef.Named("System.Object");
        }
    }

    /// <summary>The CLR type a Visual Basic keyword names.</summary>
    private static string Predefined(SyntaxKind keyword) => keyword switch
    {
        SyntaxKind.BooleanKeyword => "System.Boolean",
        SyntaxKind.ByteKeyword => "System.Byte",
        SyntaxKind.CharKeyword => "System.Char",
        SyntaxKind.DateKeyword => "System.DateTime",
        SyntaxKind.DecimalKeyword => "System.Decimal",
        SyntaxKind.DoubleKeyword => "System.Double",
        SyntaxKind.IntegerKeyword => "System.Int32",
        SyntaxKind.LongKeyword => "System.Int64",
        SyntaxKind.SByteKeyword => "System.SByte",
        SyntaxKind.ShortKeyword => "System.Int16",
        SyntaxKind.SingleKeyword => "System.Single",
        SyntaxKind.StringKeyword => "System.String",
        SyntaxKind.UIntegerKeyword => "System.UInt32",
        SyntaxKind.ULongKeyword => "System.UInt64",
        SyntaxKind.UShortKeyword => "System.UInt16",
        _ => "System.Object",
    };

    /// <summary>A name as metadata has it, its parts' brackets taken off, and whether it was written from <c>Global</c>,
    /// which no root namespace goes before; a generic part keeps its plain name.</summary>
    private static (string Name, bool Global) NameOf(NameSyntax name)
    {
        List<string> parts = [];
        bool global = false;
        NameSyntax? at = name;
        for (int depth = 0; at is not null && depth <= DeepestType; depth++)
        {
            switch (at)
            {
                case QualifiedNameSyntax qualified:
                    parts.Add(qualified.Right.Identifier.ValueText);
                    at = qualified.Left;
                    break;
                case SimpleNameSyntax simple:
                    parts.Add(simple.Identifier.ValueText);
                    at = null;
                    break;
                case GlobalNameSyntax:
                    global = true;
                    at = null;
                    break;
                default:
                    at = null;
                    break;
            }
        }
        parts.Reverse();
        return (string.Join('.', parts), global);
    }

    private static string Arity(string name, int arity) => arity == 0 ? name : $"{name}`{arity}";

    /// <summary>Whether a member's written access lets the project's own XAML reach it (anything but private, as
    /// <see cref="SourceTypes"/> takes it); null where none is written, which leaves it to the language's default.</summary>
    private static bool? Reachable(SyntaxTokenList modifiers, bool inherit = false)
    {
        if (modifiers.Any(SyntaxKind.PrivateKeyword) && !modifiers.Any(SyntaxKind.ProtectedKeyword))
        {
            return false;
        }
        return inherit || modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.FriendKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword) ? true : null;
    }

    private static bool IsVisible(SyntaxTokenList modifiers, bool nested) => !modifiers.Any(SyntaxKind.PrivateKeyword) || !nested;
}
