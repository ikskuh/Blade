using System;
using System.Collections.Generic;
using Blade.Diagnostics;
using Blade.Source;
using Blade.Syntax.Nodes;

namespace Blade.Syntax;

/// <summary>
/// Recursive descent parser for the Blade language.
/// Operates on a pre-lexed token array.
/// </summary>
public sealed class Parser
{
    private readonly SourceText _source;
    private readonly IReadOnlyList<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _position;

    public Parser(SourceText source, IReadOnlyList<Token> tokens, DiagnosticBag diagnostics)
    {
        _source = source;
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public DiagnosticBag Diagnostics => _diagnostics;
    public int TokenCount => _tokens.Count;

    private Token Current => Peek(0);

    private Token Peek(int offset)
    {
        int index = _position + offset;
        if (index >= _tokens.Count)
            return _tokens[_tokens.Count - 1]; // EOF
        return _tokens[index];
    }

    private Token NextToken()
    {
        Token current = Current;
        _position++;
        return current;
    }

    private Token MatchToken(TokenKind kind)
    {
        if (Current.Kind == kind)
            return NextToken();

        string expected = SyntaxFacts.GetText(kind) ?? kind.ToString();
        _diagnostics.ReportUnexpectedToken(Current.Span, $"'{expected}'", Current.Text);
        return new Token(kind, new TextSpan(Current.Span.Start, 0), "");
    }

    // ──────────────────────────────────────────
    //  Top-level
    // ──────────────────────────────────────────

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        List<MemberSyntax> members = new();

        while (Current.Kind != TokenKind.EndOfFile)
        {
            Token startToken = Current;
            MemberSyntax member = ParseMember();
            members.Add(member);

            // Safety: if we didn't consume any tokens, skip one to avoid infinite loop
            if (Current == startToken)
                NextToken();
        }

        Token eof = MatchToken(TokenKind.EndOfFile);
        return new CompilationUnitSyntax(members, eof);
    }

    private MemberSyntax ParseMember()
    {
        switch (Current.Kind)
        {
            case TokenKind.ImportKeyword:
                return ParseImportDeclaration();

            case TokenKind.ExternKeyword:
                return ParseVariableDeclaration(NextToken());

            case TokenKind.RegKeyword or TokenKind.LutKeyword or TokenKind.HubKeyword:
                return ParseVariableDeclaration(externKeyword: null);

            case TokenKind.VarKeyword:
                return ParseGlobalStatement();

            case TokenKind.ConstKeyword:
                return ParseGlobalStatement();

            case TokenKind.TypeKeyword:
                return ParseTypeAliasDeclaration();

            case TokenKind.FnKeyword:
                return ParseFunctionDeclaration(funcKindKeyword: null);

            case TokenKind.LeafKeyword or TokenKind.InlineKeyword or TokenKind.NoinlineKeyword or TokenKind.RecKeyword
                 or TokenKind.CoroKeyword or TokenKind.Int1Keyword or TokenKind.Int2Keyword
                 or TokenKind.Int3Keyword:
                return ParseFunctionDeclaration(NextToken());

            case TokenKind.AsmKeyword:
                // asm fn ... or asm volatile fn ... → asm function declaration
                // asm { ... }; → global statement with asm block
                if (Peek(1).Kind == TokenKind.FnKeyword)
                    return ParseAsmFunctionDeclaration();
                if (Peek(1).Kind == TokenKind.VolatileKeyword && Peek(2).Kind == TokenKind.FnKeyword)
                    return ParseAsmFunctionDeclaration();
                return ParseGlobalStatement();

            case TokenKind.ComptimeKeyword:
                if (Peek(1).Kind == TokenKind.FnKeyword)
                    return ParseFunctionDeclaration(NextToken());
                return ParseGlobalStatement();

            default:
                return ParseGlobalStatement();
        }
    }

    private ImportDeclarationSyntax ParseImportDeclaration()
    {
        Token importKw = MatchToken(TokenKind.ImportKeyword);

        // File import:  import "path/to/file.blade" as alias;
        // Named module: import extmod;
        // Named module with rename: import extmod as alias;
        Token source;
        if (Current.Kind == TokenKind.StringLiteral)
            source = NextToken();
        else
            source = MatchToken(TokenKind.Identifier);

        Token? asKw = null;
        Token? alias = null;
        if (Current.Kind == TokenKind.AsKeyword)
        {
            asKw = NextToken();
            alias = MatchToken(TokenKind.Identifier);
        }

        Token semi = MatchToken(TokenKind.Semicolon);
        return new ImportDeclarationSyntax(importKw, source, asKw, alias, semi);
    }

    private FunctionDeclarationSyntax ParseFunctionDeclaration(Token? funcKindKeyword)
    {
        Token fnKw = MatchToken(TokenKind.FnKeyword);
        Token name = MatchToken(TokenKind.Identifier);
        Token openParen = MatchToken(TokenKind.OpenParen);
        SeparatedSyntaxList<ParameterSyntax> parameters = ParseParameterList();
        Token closeParen = MatchToken(TokenKind.CloseParen);

        Token? arrow = null;
        SeparatedSyntaxList<ReturnItemSyntax>? returnSpec = null;

        if (Current.Kind == TokenKind.Arrow)
        {
            arrow = NextToken();
            returnSpec = ParseReturnSpec();
        }
        else if (Current.Kind != TokenKind.OpenBrace && IsTypeStart(Current.Kind))
        {
            // Bare return type without -> (e.g., "fn nop() void { }")
            returnSpec = ParseReturnSpec();
        }

        BlockStatementSyntax body = ParseBlockStatement();
        return new FunctionDeclarationSyntax(funcKindKeyword, fnKw, name, openParen, parameters, closeParen, arrow, returnSpec, body);
    }

    private AsmFunctionDeclarationSyntax ParseAsmFunctionDeclaration()
    {
        Token asmKw = MatchToken(TokenKind.AsmKeyword);
        Token? volatileKw = null;
        if (Current.Kind == TokenKind.VolatileKeyword)
            volatileKw = NextToken();

        Token fnKw = MatchToken(TokenKind.FnKeyword);
        Token name = MatchToken(TokenKind.Identifier);
        Token openParen = MatchToken(TokenKind.OpenParen);
        SeparatedSyntaxList<ParameterSyntax> parameters = ParseParameterList();
        Token closeParen = MatchToken(TokenKind.CloseParen);

        Token? arrow = null;
        SeparatedSyntaxList<ReturnItemSyntax>? returnSpec = null;

        if (Current.Kind == TokenKind.Arrow)
        {
            arrow = NextToken();
            returnSpec = ParseReturnSpec();
        }

        Token openBrace = MatchToken(TokenKind.OpenBrace);

        // Capture raw text between braces
        int bodyStart = openBrace.Span.End;
        int depth = 1;
        while (Current.Kind != TokenKind.EndOfFile && depth > 0)
        {
            if (Current.Kind == TokenKind.OpenBrace)
            {
                depth++;
            }
            else if (Current.Kind == TokenKind.CloseBrace)
            {
                depth--;
                if (depth == 0)
                    break;
            }
            NextToken();
        }

        int bodyEnd = Current.Span.Start;
        string body = _source.ToString(TextSpan.FromBounds(bodyStart, bodyEnd));

        Token closeBrace = MatchToken(TokenKind.CloseBrace);
        return new AsmFunctionDeclarationSyntax(asmKw, volatileKw, fnKw, name, openParen, parameters, closeParen,
                                                 arrow, returnSpec, openBrace, body, closeBrace);
    }

    private TypeAliasDeclarationSyntax ParseTypeAliasDeclaration()
    {
        Token typeKw = MatchToken(TokenKind.TypeKeyword);
        Token name = MatchToken(TokenKind.Identifier);
        Token equals = MatchToken(TokenKind.Equal);
        TypeSyntax type = ParseType();
        Token semi = MatchToken(TokenKind.Semicolon);
        return new TypeAliasDeclarationSyntax(typeKw, name, equals, type, semi);
    }

    private SeparatedSyntaxList<ParameterSyntax> ParseParameterList()
    {
        List<object> nodesAndSeparators = new();

        while (Current.Kind != TokenKind.CloseParen && Current.Kind != TokenKind.EndOfFile)
        {
            ParameterSyntax param = ParseParameter();
            nodesAndSeparators.Add(param);

            if (Current.Kind == TokenKind.Comma)
                nodesAndSeparators.Add(NextToken());
            else
                break;
        }

        return new SeparatedSyntaxList<ParameterSyntax>(nodesAndSeparators);
    }

    private ParameterSyntax ParseParameter()
    {
        Token? storageClass = null;
        if (Current.Kind is TokenKind.RegKeyword or TokenKind.LutKeyword or TokenKind.HubKeyword)
            storageClass = NextToken();

        Token name = MatchToken(TokenKind.Identifier);
        Token colon = MatchToken(TokenKind.Colon);
        TypeSyntax type = ParseType();
        return new ParameterSyntax(storageClass, name, colon, type);
    }

    private SeparatedSyntaxList<ReturnItemSyntax> ParseReturnSpec()
    {
        List<object> nodesAndSeparators = new();

        ReturnItemSyntax item = ParseReturnItem();
        nodesAndSeparators.Add(item);

        while (Current.Kind == TokenKind.Comma)
        {
            nodesAndSeparators.Add(NextToken());
            nodesAndSeparators.Add(ParseReturnItem());
        }

        return new SeparatedSyntaxList<ReturnItemSyntax>(nodesAndSeparators);
    }

    private ReturnItemSyntax ParseReturnItem()
    {
        // Optional: name: type @Flag
        // We need lookahead to detect name: prefix
        Token? name = null;
        Token? colon = null;

        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
        {
            name = NextToken();
            colon = NextToken();
        }

        TypeSyntax type = ParseType();

        FlagAnnotationSyntax? flagAnnotation = null;
        if (Current.Kind == TokenKind.At)
        {
            Token at = NextToken();
            Token flag = MatchToken(TokenKind.Identifier);
            flagAnnotation = new FlagAnnotationSyntax(at, flag);
        }

        return new ReturnItemSyntax(name, colon, type, flagAnnotation);
    }

    private VariableDeclarationSyntax ParseVariableDeclaration(Token? externKeyword)
    {
        Token? storageClass = null;
        if (Current.Kind is TokenKind.RegKeyword or TokenKind.LutKeyword or TokenKind.HubKeyword)
            storageClass = NextToken();

        Token mutability = Current.Kind == TokenKind.VarKeyword || Current.Kind == TokenKind.ConstKeyword
            ? NextToken()
            : MatchToken(TokenKind.VarKeyword); // will report error

        Token name = MatchToken(TokenKind.Identifier);
        Token colon = MatchToken(TokenKind.Colon);
        TypeSyntax type = ParseType();

        // Parse optional clauses: @(addr), align(n), = initializer
        // These can appear in any order after the type (spec examples show align before =)
        AddressClauseSyntax? atClause = null;
        AlignClauseSyntax? alignClause = null;
        Token? equalsToken = null;
        ExpressionSyntax? initializer = null;

        bool parsing = true;
        while (parsing)
        {
            switch (Current.Kind)
            {
                case TokenKind.At:
                {
                    Token at = NextToken();
                    Token openParen = MatchToken(TokenKind.OpenParen);
                    ExpressionSyntax address = ParseExpression();
                    Token closeParen = MatchToken(TokenKind.CloseParen);
                    atClause = new AddressClauseSyntax(at, openParen, address, closeParen);
                    break;
                }
                case TokenKind.AlignKeyword:
                {
                    Token alignKw = NextToken();
                    Token openParen = MatchToken(TokenKind.OpenParen);
                    ExpressionSyntax alignment = ParseExpression();
                    Token closeParen = MatchToken(TokenKind.CloseParen);
                    alignClause = new AlignClauseSyntax(alignKw, openParen, alignment, closeParen);
                    break;
                }
                case TokenKind.Equal:
                    equalsToken = NextToken();
                    initializer = ParseExpression();
                    break;
                default:
                    parsing = false;
                    break;
            }
        }

        Token semi = MatchToken(TokenKind.Semicolon);
        return new VariableDeclarationSyntax(externKeyword, storageClass, mutability, name, colon, type, equalsToken, initializer, atClause, alignClause, semi);
    }

    private GlobalStatementSyntax ParseGlobalStatement()
    {
        StatementSyntax statement = ParseStatement();
        return new GlobalStatementSyntax(statement);
    }

    // ──────────────────────────────────────────
    //  Statements
    // ──────────────────────────────────────────

    private StatementSyntax ParseStatement()
    {
        switch (Current.Kind)
        {
            case TokenKind.OpenBrace:
                return ParseBlockStatement();

            case TokenKind.VarKeyword or TokenKind.RegKeyword or TokenKind.LutKeyword or TokenKind.HubKeyword:
                return new VariableDeclarationStatementSyntax(ParseVariableDeclaration(externKeyword: null));

            case TokenKind.ConstKeyword:
                // Local const declaration: const name: type = expr;
                // Disambiguate from const Name = type (type alias at statement level is unusual)
                if (Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind == TokenKind.Colon)
                    return new VariableDeclarationStatementSyntax(ParseVariableDeclaration(externKeyword: null));
                // Fall through to expression/assignment
                return ParseExpressionOrAssignmentStatement();

            case TokenKind.ExternKeyword:
                return new VariableDeclarationStatementSyntax(ParseVariableDeclaration(NextToken()));

            case TokenKind.IfKeyword:
                return ParseIfStatement();

            case TokenKind.WhileKeyword:
                return ParseWhileStatement();

            case TokenKind.ForKeyword:
                return ParseForStatement();

            case TokenKind.LoopKeyword:
                return ParseLoopStatement();

            case TokenKind.RepKeyword:
                return ParseRepStatement();

            case TokenKind.NoirqKeyword:
                return ParseNoirqStatement();

            case TokenKind.AssertKeyword:
                return ParseAssertStatement();

            case TokenKind.ReturnKeyword:
                return ParseReturnStatement();

            case TokenKind.BreakKeyword:
                return ParseBreakStatement();

            case TokenKind.ContinueKeyword:
                return ParseContinueStatement();

            case TokenKind.YieldKeyword:
                return ParseYieldStatement();

            case TokenKind.YieldtoKeyword:
                return ParseYieldtoStatement();

            case TokenKind.AsmKeyword:
                return ParseAsmBlockStatement();

            default:
                return ParseExpressionOrAssignmentStatement();
        }
    }

    private BlockStatementSyntax ParseBlockStatement()
    {
        Token openBrace = MatchToken(TokenKind.OpenBrace);
        List<StatementSyntax> statements = new();

        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            Token startToken = Current;
            StatementSyntax statement = ParseStatement();
            statements.Add(statement);

            if (Current == startToken)
                NextToken();
        }

        Token closeBrace = MatchToken(TokenKind.CloseBrace);
        return new BlockStatementSyntax(openBrace, statements, closeBrace);
    }

    private IfStatementSyntax ParseIfStatement()
    {
        Token ifKw = MatchToken(TokenKind.IfKeyword);
        Token openParen = MatchToken(TokenKind.OpenParen);
        ExpressionSyntax condition = ParseExpression();
        Token closeParen = MatchToken(TokenKind.CloseParen);

        StatementSyntax thenBody = Current.Kind == TokenKind.OpenBrace
            ? ParseBlockStatement()
            : ParseStatement();

        ElseClauseSyntax? elseClause = null;
        if (Current.Kind == TokenKind.ElseKeyword)
        {
            Token elseKw = NextToken();
            StatementSyntax elseBody = Current.Kind == TokenKind.IfKeyword
                ? ParseIfStatement()
                : Current.Kind == TokenKind.OpenBrace
                    ? ParseBlockStatement()
                    : ParseStatement();
            elseClause = new ElseClauseSyntax(elseKw, elseBody);
        }

        return new IfStatementSyntax(ifKw, openParen, condition, closeParen, thenBody, elseClause);
    }

    private WhileStatementSyntax ParseWhileStatement()
    {
        Token whileKw = MatchToken(TokenKind.WhileKeyword);
        Token openParen = MatchToken(TokenKind.OpenParen);
        ExpressionSyntax condition = ParseExpression();
        Token closeParen = MatchToken(TokenKind.CloseParen);
        BlockStatementSyntax body = ParseBlockStatement();
        return new WhileStatementSyntax(whileKw, openParen, condition, closeParen, body);
    }

    private ForStatementSyntax ParseForStatement()
    {
        Token forKw = MatchToken(TokenKind.ForKeyword);
        Token openParen = MatchToken(TokenKind.OpenParen);
        ExpressionSyntax iterable = ParseExpression();
        Token closeParen = MatchToken(TokenKind.CloseParen);

        ForBindingSyntax? binding = null;
        if (Current.Kind == TokenKind.Arrow)
            binding = ParseForBinding();

        BlockStatementSyntax body = ParseBlockStatement();
        return new ForStatementSyntax(forKw, openParen, iterable, closeParen, binding, body);
    }

    private ForBindingSyntax ParseForBinding()
    {
        Token arrow = MatchToken(TokenKind.Arrow);

        Token? ampersand = null;
        if (Current.Kind == TokenKind.Ampersand)
            ampersand = NextToken();

        Token itemName = MatchToken(TokenKind.Identifier);

        Token? comma = null;
        Token? indexName = null;
        if (Current.Kind == TokenKind.Comma)
        {
            comma = NextToken();
            indexName = MatchToken(TokenKind.Identifier);
        }

        return new ForBindingSyntax(arrow, ampersand, itemName, comma, indexName);
    }

    private LoopStatementSyntax ParseLoopStatement()
    {
        Token loopKw = MatchToken(TokenKind.LoopKeyword);
        BlockStatementSyntax body = ParseBlockStatement();
        return new LoopStatementSyntax(loopKw, body);
    }

    private StatementSyntax ParseRepStatement()
    {
        Token repKw = MatchToken(TokenKind.RepKeyword);

        if (Current.Kind == TokenKind.LoopKeyword)
        {
            Token loopKw = NextToken();
            BlockStatementSyntax body = ParseBlockStatement();
            return new RepLoopStatementSyntax(repKw, loopKw, body);
        }

        // rep for (expr) [-> binding] { body }
        Token forKw = MatchToken(TokenKind.ForKeyword);
        Token openParen2 = MatchToken(TokenKind.OpenParen);
        ExpressionSyntax iterable = ParseExpression();
        Token closeParen2 = MatchToken(TokenKind.CloseParen);

        ForBindingSyntax? binding = null;
        if (Current.Kind == TokenKind.Arrow)
            binding = ParseForBinding();

        BlockStatementSyntax body3 = ParseBlockStatement();
        return new RepForStatementSyntax(repKw, forKw, openParen2, iterable, closeParen2, binding, body3);
    }

    private NoirqStatementSyntax ParseNoirqStatement()
    {
        Token noirqKw = MatchToken(TokenKind.NoirqKeyword);
        BlockStatementSyntax body = ParseBlockStatement();
        return new NoirqStatementSyntax(noirqKw, body);
    }

    private AssertStatementSyntax ParseAssertStatement()
    {
        Token assertKw = MatchToken(TokenKind.AssertKeyword);
        ExpressionSyntax condition = ParseExpression();

        Token? commaToken = null;
        Token? messageLiteral = null;
        if (Current.Kind == TokenKind.Comma)
        {
            commaToken = NextToken();
            if (Current.Kind == TokenKind.StringLiteral)
            {
                messageLiteral = NextToken();
            }
            else
            {
                _diagnostics.ReportUnexpectedToken(Current.Span, "string literal", Current.Text);
                if (Current.Kind is not TokenKind.Semicolon and not TokenKind.EndOfFile)
                    NextToken();
            }
        }

        Token semicolon = MatchToken(TokenKind.Semicolon);
        return new AssertStatementSyntax(assertKw, condition, commaToken, messageLiteral, semicolon);
    }

    private ReturnStatementSyntax ParseReturnStatement()
    {
        Token returnKw = MatchToken(TokenKind.ReturnKeyword);

        SeparatedSyntaxList<ExpressionSyntax>? values = null;
        if (Current.Kind != TokenKind.Semicolon)
        {
            values = ParseExpressionList();
        }

        Token semi = MatchToken(TokenKind.Semicolon);
        return new ReturnStatementSyntax(returnKw, values, semi);
    }

    private BreakStatementSyntax ParseBreakStatement()
    {
        Token breakKw = MatchToken(TokenKind.BreakKeyword);
        Token semi = MatchToken(TokenKind.Semicolon);
        return new BreakStatementSyntax(breakKw, semi);
    }

    private ContinueStatementSyntax ParseContinueStatement()
    {
        Token continueKw = MatchToken(TokenKind.ContinueKeyword);
        Token semi = MatchToken(TokenKind.Semicolon);
        return new ContinueStatementSyntax(continueKw, semi);
    }

    private YieldStatementSyntax ParseYieldStatement()
    {
        Token yieldKw = MatchToken(TokenKind.YieldKeyword);
        Token semi = MatchToken(TokenKind.Semicolon);
        return new YieldStatementSyntax(yieldKw, semi);
    }

    private YieldtoStatementSyntax ParseYieldtoStatement()
    {
        Token yieldtoKw = MatchToken(TokenKind.YieldtoKeyword);
        Token target = MatchToken(TokenKind.Identifier);
        Token openParen = MatchToken(TokenKind.OpenParen);
        SeparatedSyntaxList<ExpressionSyntax> args = ParseArgumentList();
        Token closeParen = MatchToken(TokenKind.CloseParen);
        Token semi = MatchToken(TokenKind.Semicolon);
        return new YieldtoStatementSyntax(yieldtoKw, target, openParen, args, closeParen, semi);
    }

    private AsmBlockStatementSyntax ParseAsmBlockStatement()
    {
        Token asmKw = MatchToken(TokenKind.AsmKeyword);
        Token? volatileKw = null;
        if (Current.Kind == TokenKind.VolatileKeyword)
            volatileKw = NextToken();

        Token openBrace = MatchToken(TokenKind.OpenBrace);

        // Capture raw text between braces
        int bodyStart = openBrace.Span.End;
        int depth = 1;
        while (Current.Kind != TokenKind.EndOfFile && depth > 0)
        {
            if (Current.Kind == TokenKind.OpenBrace)
            {
                depth++;
            }
            else if (Current.Kind == TokenKind.CloseBrace)
            {
                depth--;
                if (depth == 0)
                    break;
            }
            NextToken();
        }

        int bodyEnd = Current.Span.Start;
        string body = _source.ToString(TextSpan.FromBounds(bodyStart, bodyEnd));

        Token closeBrace = MatchToken(TokenKind.CloseBrace);

        // Parse optional output binding after body: -> name: type@Flag
        AsmOutputBindingSyntax? outputBinding = null;
        if (Current.Kind == TokenKind.Arrow)
        {
            Token arrow = NextToken();
            Token name = MatchToken(TokenKind.Identifier);
            Token colon = MatchToken(TokenKind.Colon);
            TypeSyntax type = ParseType();

            FlagAnnotationSyntax? flagAnnotation = null;
            if (Current.Kind == TokenKind.At)
            {
                Token at = NextToken();
                Token flag = MatchToken(TokenKind.Identifier);
                flagAnnotation = new FlagAnnotationSyntax(at, flag);
            }

            outputBinding = new AsmOutputBindingSyntax(arrow, name, colon, type, flagAnnotation);
        }

        Token semi = MatchToken(TokenKind.Semicolon);
        return new AsmBlockStatementSyntax(asmKw, volatileKw, openBrace, body, closeBrace, outputBinding, semi);
    }

    private StatementSyntax ParseExpressionOrAssignmentStatement()
    {
        ExpressionSyntax expression = ParseExpression();

        // Multi-target assignment: expr, expr, ... = expr;
        if (Current.Kind == TokenKind.Comma)
        {
            List<object> nodesAndSeparators = [expression];
            while (Current.Kind == TokenKind.Comma)
            {
                nodesAndSeparators.Add(NextToken());
                nodesAndSeparators.Add(ParseExpression());
            }

            SeparatedSyntaxList<ExpressionSyntax> targets = new(nodesAndSeparators);
            Token op = MatchToken(TokenKind.Equal);
            ExpressionSyntax value = ParseExpression();
            Token semi = MatchToken(TokenKind.Semicolon);
            return new MultiAssignmentStatementSyntax(targets, op, value, semi);
        }

        if (SyntaxFacts.IsAssignmentOperator(Current.Kind))
        {
            Token op = NextToken();
            ExpressionSyntax value = ParseExpression();
            Token semi = MatchToken(TokenKind.Semicolon);
            return new AssignmentStatementSyntax(expression, op, value, semi);
        }

        Token semicolon = MatchToken(TokenKind.Semicolon);
        return new ExpressionStatementSyntax(expression, semicolon);
    }

    // ──────────────────────────────────────────
    //  Types
    // ──────────────────────────────────────────

    private TypeSyntax ParseType()
    {
        switch (Current.Kind)
        {
            // Primitive types
            case TokenKind.BoolKeyword or TokenKind.BitKeyword or TokenKind.NitKeyword
                 or TokenKind.NibKeyword or TokenKind.U8Keyword or TokenKind.I8Keyword
                 or TokenKind.U16Keyword or TokenKind.I16Keyword or TokenKind.U32Keyword
                 or TokenKind.I32Keyword or TokenKind.VoidKeyword or TokenKind.U8x4Keyword:
                return new PrimitiveTypeSyntax(NextToken());

            // Generic width: uint(N), int(N)
            case TokenKind.UintKeyword or TokenKind.IntKeyword:
                return ParseGenericWidthType();

            // Array or multi-pointer: [expr]type or [*]type
            case TokenKind.OpenBracket:
                if (Peek(1).Kind == TokenKind.Star && Peek(2).Kind == TokenKind.CloseBracket)
                    return ParseMultiPointerType();
                return ParseArrayType();

            // Pointer: *type
            case TokenKind.Star:
                return ParsePointerType();

            case TokenKind.StructKeyword:
                return ParseStructType();

            // Union
            case TokenKind.UnionKeyword:
                return ParseUnionType();

            // Enum
            case TokenKind.EnumKeyword:
                return ParseEnumType();

            // Bitfield
            case TokenKind.BitfieldKeyword:
                return ParseBitfieldType();

            // Named type (user-defined)
            case TokenKind.Identifier:
            {
                List<Token> parts = [NextToken()];
                while (Current.Kind == TokenKind.Dot && Peek(1).Kind == TokenKind.Identifier)
                {
                    _ = NextToken();
                    parts.Add(NextToken());
                }

                return parts.Count == 1
                    ? new NamedTypeSyntax(parts[0])
                    : new QualifiedTypeSyntax(parts);
            }

            default:
                _diagnostics.ReportExpectedTypeName(Current.Span);
                return new NamedTypeSyntax(new Token(TokenKind.Identifier, new TextSpan(Current.Span.Start, 0), ""));
        }
    }

    private GenericWidthTypeSyntax ParseGenericWidthType()
    {
        Token keyword = NextToken(); // uint or int
        Token openParen = MatchToken(TokenKind.OpenParen);
        ExpressionSyntax width = ParseExpression();
        Token closeParen = MatchToken(TokenKind.CloseParen);
        return new GenericWidthTypeSyntax(keyword, openParen, width, closeParen);
    }

    private ArrayTypeSyntax ParseArrayType()
    {
        Token openBracket = MatchToken(TokenKind.OpenBracket);
        ExpressionSyntax size = ParseExpression();
        Token closeBracket = MatchToken(TokenKind.CloseBracket);
        TypeSyntax elementType = ParseType();
        return new ArrayTypeSyntax(openBracket, size, closeBracket, elementType);
    }

    private PointerTypeSyntax ParsePointerType()
    {
        Token star = MatchToken(TokenKind.Star);
        ParsePointerAttributes(out Token? storageClass, out Token? constKw, out Token? volatileKw, out AlignClauseSyntax? alignClause);
        TypeSyntax pointeeType = ParseType();
        return new PointerTypeSyntax(star, storageClass, constKw, volatileKw, alignClause, pointeeType);
    }

    private MultiPointerTypeSyntax ParseMultiPointerType()
    {
        Token openBracket = MatchToken(TokenKind.OpenBracket);
        Token star = MatchToken(TokenKind.Star);
        Token closeBracket = MatchToken(TokenKind.CloseBracket);
        ParsePointerAttributes(out Token? storageClass, out Token? constKw, out Token? volatileKw, out AlignClauseSyntax? alignClause);
        TypeSyntax pointeeType = ParseType();
        return new MultiPointerTypeSyntax(openBracket, star, closeBracket, storageClass, constKw, volatileKw, alignClause, pointeeType);
    }

    private void ParsePointerAttributes(out Token? storageClass, out Token? constKw, out Token? volatileKw, out AlignClauseSyntax? alignClause)
    {
        storageClass = null;
        constKw = null;
        volatileKw = null;
        alignClause = null;

        // Parse optional attributes in any order: storage, const, volatile, align(N)
        bool parsing = true;
        while (parsing)
        {
            switch (Current.Kind)
            {
                case TokenKind.RegKeyword or TokenKind.LutKeyword or TokenKind.HubKeyword when storageClass is null:
                    storageClass = NextToken();
                    break;
                case TokenKind.ConstKeyword when constKw is null:
                    constKw = NextToken();
                    break;
                case TokenKind.VolatileKeyword when volatileKw is null:
                    volatileKw = NextToken();
                    break;
                case TokenKind.AlignKeyword when alignClause is null:
                {
                    Token alignKw = NextToken();
                    Token openParen = MatchToken(TokenKind.OpenParen);
                    ExpressionSyntax alignment = ParseExpression();
                    Token closeParen = MatchToken(TokenKind.CloseParen);
                    alignClause = new AlignClauseSyntax(alignKw, openParen, alignment, closeParen);
                    break;
                }
                default:
                    parsing = false;
                    break;
            }
        }
    }

    private StructTypeSyntax ParseStructType()
    {
        Token structKw = MatchToken(TokenKind.StructKeyword);
        Token openBrace = MatchToken(TokenKind.OpenBrace);

        List<object> fieldsAndSeparators = new();
        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            Token fieldName = MatchToken(TokenKind.Identifier);
            Token colon = MatchToken(TokenKind.Colon);
            TypeSyntax fieldType = ParseType();
            fieldsAndSeparators.Add(new StructFieldSyntax(fieldName, colon, fieldType));

            if (Current.Kind == TokenKind.Comma)
                fieldsAndSeparators.Add(NextToken());
            else
                break;
        }

        Token closeBrace = MatchToken(TokenKind.CloseBrace);
        return new StructTypeSyntax(structKw, openBrace, new SeparatedSyntaxList<StructFieldSyntax>(fieldsAndSeparators), closeBrace);
    }

    private UnionTypeSyntax ParseUnionType()
    {
        Token unionKw = MatchToken(TokenKind.UnionKeyword);
        Token openBrace = MatchToken(TokenKind.OpenBrace);

        List<object> fieldsAndSeparators = new();
        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            Token fieldName = MatchToken(TokenKind.Identifier);
            Token colon = MatchToken(TokenKind.Colon);
            TypeSyntax fieldType = ParseType();
            fieldsAndSeparators.Add(new StructFieldSyntax(fieldName, colon, fieldType));

            if (Current.Kind == TokenKind.Comma)
                fieldsAndSeparators.Add(NextToken());
            else
                break;
        }

        Token closeBrace = MatchToken(TokenKind.CloseBrace);
        return new UnionTypeSyntax(unionKw, openBrace, new SeparatedSyntaxList<StructFieldSyntax>(fieldsAndSeparators), closeBrace);
    }

    private EnumTypeSyntax ParseEnumType()
    {
        Token enumKw = MatchToken(TokenKind.EnumKeyword);
        Token openParen = MatchToken(TokenKind.OpenParen);
        TypeSyntax backingType = ParseType();
        Token closeParen = MatchToken(TokenKind.CloseParen);
        Token openBrace = MatchToken(TokenKind.OpenBrace);

        List<object> membersAndSeparators = new();
        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            if (Current.Kind == TokenKind.DotDotDot)
            {
                // Open enum marker: ...
                Token dots = NextToken();
                membersAndSeparators.Add(new EnumMemberSyntax(dots, null, null, isOpenMarker: true));
            }
            else
            {
                Token memberName = MatchToken(TokenKind.Identifier);

                Token? equalsToken = null;
                ExpressionSyntax? value = null;
                if (Current.Kind == TokenKind.Equal)
                {
                    equalsToken = NextToken();
                    value = ParseExpression();
                }

                membersAndSeparators.Add(new EnumMemberSyntax(memberName, equalsToken, value));
            }

            if (Current.Kind == TokenKind.Comma)
                membersAndSeparators.Add(NextToken());
            else
                break;
        }

        Token closeBrace = MatchToken(TokenKind.CloseBrace);
        return new EnumTypeSyntax(enumKw, openParen, backingType, closeParen, openBrace,
                                  new SeparatedSyntaxList<EnumMemberSyntax>(membersAndSeparators), closeBrace);
    }

    private BitfieldTypeSyntax ParseBitfieldType()
    {
        Token bitfieldKw = MatchToken(TokenKind.BitfieldKeyword);
        Token openParen = MatchToken(TokenKind.OpenParen);
        TypeSyntax backingType = ParseType();
        Token closeParen = MatchToken(TokenKind.CloseParen);
        Token openBrace = MatchToken(TokenKind.OpenBrace);

        List<object> fieldsAndSeparators = new();
        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            Token fieldName = MatchToken(TokenKind.Identifier);
            Token colon = MatchToken(TokenKind.Colon);
            TypeSyntax fieldType = ParseType();
            fieldsAndSeparators.Add(new StructFieldSyntax(fieldName, colon, fieldType));

            if (Current.Kind == TokenKind.Comma)
                fieldsAndSeparators.Add(NextToken());
            else
                break;
        }

        Token closeBrace = MatchToken(TokenKind.CloseBrace);
        return new BitfieldTypeSyntax(bitfieldKw, openParen, backingType, closeParen, openBrace,
                                      new SeparatedSyntaxList<StructFieldSyntax>(fieldsAndSeparators), closeBrace);
    }

    // ──────────────────────────────────────────
    //  Expressions
    // ──────────────────────────────────────────

    public ExpressionSyntax ParseExpression()
    {
        return ParseBinaryExpression(0);
    }

    private ExpressionSyntax ParseBinaryExpression(int parentPrecedence)
    {
        ExpressionSyntax left;

        int unaryPrecedence = SyntaxFacts.GetUnaryOperatorPrecedence(Current.Kind);
        if (unaryPrecedence > 0)
        {
            Token op = NextToken();
            ExpressionSyntax operand = ParseBinaryExpression(unaryPrecedence);
            left = new UnaryExpressionSyntax(op, operand);
        }
        else
        {
            left = ParsePostfixExpression();
        }

        while (true)
        {
            // Check for range operator (..) — lower precedence than all binary ops
            if (Current.Kind == TokenKind.DotDot && parentPrecedence == 0)
            {
                Token dotDot = NextToken();
                ExpressionSyntax right = ParseBinaryExpression(1); // bind tighter than range itself
                left = new RangeExpressionSyntax(left, dotDot, right);
                continue;
            }

            int precedence = SyntaxFacts.GetBinaryOperatorPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
                break;

            Token operatorToken = NextToken();
            ExpressionSyntax right2 = ParseBinaryExpression(precedence);
            left = new BinaryExpressionSyntax(left, operatorToken, right2);
        }

        return left;
    }

    private ExpressionSyntax ParsePostfixExpression()
    {
        ExpressionSyntax expression = ParsePrimaryExpression();

        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.Dot:
                    Token dot = NextToken();
                    if (Current.Kind == TokenKind.Star)
                    {
                        // ptr.*
                        Token star = NextToken();
                        expression = new PointerDerefExpressionSyntax(expression, dot, star);
                    }
                    else
                    {
                        Token member = MatchToken(TokenKind.Identifier);
                        expression = new MemberAccessExpressionSyntax(expression, dot, member);
                    }
                    break;

                case TokenKind.OpenBracket:
                {
                    Token openBracket = NextToken();
                    ExpressionSyntax index = ParseExpression();
                    Token closeBracket = MatchToken(TokenKind.CloseBracket);
                    expression = new IndexExpressionSyntax(expression, openBracket, index, closeBracket);
                    break;
                }

                case TokenKind.OpenParen:
                {
                    Token openParen = NextToken();
                    SeparatedSyntaxList<ExpressionSyntax> args = ParseArgumentList();
                    Token closeParen = MatchToken(TokenKind.CloseParen);
                    expression = new CallExpressionSyntax(expression, openParen, args, closeParen);
                    break;
                }

                case TokenKind.PlusPlus or TokenKind.MinusMinus:
                    expression = new PostfixUnaryExpressionSyntax(expression, NextToken());
                    break;

                case TokenKind.AsKeyword:
                {
                    // expr as Type
                    Token asKw = NextToken();
                    TypeSyntax targetType = ParseType();
                    expression = new CastExpressionSyntax(expression, asKw, targetType);
                    break;
                }

                case TokenKind.OpenBrace when expression is NameExpressionSyntax:
                {
                    // TypeName { .field = value, ... } — typed struct literal
                    // Disambiguate: only if the first token inside the brace is '.'
                    if (Peek(1).Kind != TokenKind.Dot)
                        return expression;

                    Token openBrace = NextToken();
                    List<object> initializersAndSeparators = new();
                    while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
                    {
                        Token fieldDot = MatchToken(TokenKind.Dot);
                        Token fieldName = MatchToken(TokenKind.Identifier);
                        Token equals = MatchToken(TokenKind.Equal);
                        ExpressionSyntax value = ParseExpression();
                        initializersAndSeparators.Add(new FieldInitializerSyntax(fieldDot, fieldName, equals, value));

                        if (Current.Kind == TokenKind.Comma)
                            initializersAndSeparators.Add(NextToken());
                        else
                            break;
                    }

                    Token closeBrace = MatchToken(TokenKind.CloseBrace);
                    expression = new TypedStructLiteralExpressionSyntax(expression, openBrace,
                        new SeparatedSyntaxList<FieldInitializerSyntax>(initializersAndSeparators), closeBrace);
                    break;
                }

                default:
                    return expression;
            }
        }
    }

    private ExpressionSyntax ParsePrimaryExpression()
    {
        switch (Current.Kind)
        {
            case TokenKind.IntegerLiteral or TokenKind.StringLiteral or TokenKind.ZeroTerminatedStringLiteral or TokenKind.CharLiteral:
                return new LiteralExpressionSyntax(NextToken());

            case TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.UndefinedKeyword:
                return new LiteralExpressionSyntax(NextToken());

            case TokenKind.Identifier:
                return new NameExpressionSyntax(NextToken());

            case TokenKind.OpenParen:
            {
                Token openParen = NextToken();
                ExpressionSyntax expr = ParseExpression();
                Token closeParen = MatchToken(TokenKind.CloseParen);
                return new ParenthesizedExpressionSyntax(openParen, expr, closeParen);
            }

            case TokenKind.OpenBracket:
                return ParseArrayLiteral();

            case TokenKind.At:
                return ParseIntrinsicCall();

            case TokenKind.Dot:
                if (Peek(1).Kind == TokenKind.OpenBrace)
                    return ParseStructLiteral();
                if (SyntaxFacts.IsIdentifierLike(Peek(1).Kind))
                    return ParseEnumLiteral();
                goto default;

            case TokenKind.BitcastKeyword:
                return ParseBitcastExpression();

            case TokenKind.SizeofKeyword:
            case TokenKind.AlignofKeyword:
            case TokenKind.MemoryofKeyword:
                return ParseQueryExpression();

            case TokenKind.IfKeyword:
                return ParseIfExpression();

            default:
                _diagnostics.ReportExpectedExpression(Current.Span);
                return new LiteralExpressionSyntax(
                    new Token(TokenKind.IntegerLiteral, new TextSpan(Current.Span.Start, 0), "", 0L));
        }
    }

    private ArrayLiteralExpressionSyntax ParseArrayLiteral()
    {
        Token openBracket = MatchToken(TokenKind.OpenBracket);

        List<object> elementsAndSeparators = new();
        while (Current.Kind != TokenKind.CloseBracket && Current.Kind != TokenKind.EndOfFile)
        {
            ExpressionSyntax value = ParseExpression();

            Token? spread = null;
            if (Current.Kind == TokenKind.DotDotDot)
                spread = NextToken();

            elementsAndSeparators.Add(new ArrayElementSyntax(value, spread));

            if (Current.Kind == TokenKind.Comma)
                elementsAndSeparators.Add(NextToken());
            else
                break;
        }

        Token closeBracket = MatchToken(TokenKind.CloseBracket);
        return new ArrayLiteralExpressionSyntax(openBracket,
            new SeparatedSyntaxList<ArrayElementSyntax>(elementsAndSeparators), closeBracket);
    }

    private EnumLiteralExpressionSyntax ParseEnumLiteral()
    {
        Token dot = MatchToken(TokenKind.Dot);
        Token memberName = SyntaxFacts.IsIdentifierLike(Current.Kind) ? NextToken() : MatchToken(TokenKind.Identifier);
        return new EnumLiteralExpressionSyntax(dot, memberName);
    }

    private BitcastExpressionSyntax ParseBitcastExpression()
    {
        Token bitcastKw = MatchToken(TokenKind.BitcastKeyword);
        Token openParen = MatchToken(TokenKind.OpenParen);
        TypeSyntax targetType = ParseType();
        Token comma = MatchToken(TokenKind.Comma);
        ExpressionSyntax value = ParseExpression();
        Token closeParen = MatchToken(TokenKind.CloseParen);
        return new BitcastExpressionSyntax(bitcastKw, openParen, targetType, comma, value, closeParen);
    }

    private QueryExpressionSyntax ParseQueryExpression()
    {
        Token keyword = NextToken();
        Token openParen = MatchToken(TokenKind.OpenParen);
        TypeSyntax subject = ParseType();

        Token? comma = null;
        ExpressionSyntax? memorySpace = null;
        if (Current.Kind == TokenKind.Comma)
        {
            comma = NextToken();
            memorySpace = ParseExpression();
        }

        Token closeParen = MatchToken(TokenKind.CloseParen);
        return new QueryExpressionSyntax(keyword, openParen, subject, comma, memorySpace, closeParen);
    }

    private IntrinsicCallExpressionSyntax ParseIntrinsicCall()
    {
        Token at = MatchToken(TokenKind.At);
        Token name = MatchToken(TokenKind.Identifier);
        Token openParen = MatchToken(TokenKind.OpenParen);
        SeparatedSyntaxList<ExpressionSyntax> args = ParseArgumentList();
        Token closeParen = MatchToken(TokenKind.CloseParen);
        return new IntrinsicCallExpressionSyntax(at, name, openParen, args, closeParen);
    }

    private StructLiteralExpressionSyntax ParseStructLiteral()
    {
        Token dot = MatchToken(TokenKind.Dot);
        Token openBrace = MatchToken(TokenKind.OpenBrace);

        List<object> initializersAndSeparators = new();
        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            Token fieldDot = MatchToken(TokenKind.Dot);
            Token fieldName = MatchToken(TokenKind.Identifier);
            Token equals = MatchToken(TokenKind.Equal);
            ExpressionSyntax value = ParseExpression();
            initializersAndSeparators.Add(new FieldInitializerSyntax(fieldDot, fieldName, equals, value));

            if (Current.Kind == TokenKind.Comma)
                initializersAndSeparators.Add(NextToken());
            else
                break;
        }

        Token closeBrace = MatchToken(TokenKind.CloseBrace);
        return new StructLiteralExpressionSyntax(dot, openBrace,
            new SeparatedSyntaxList<FieldInitializerSyntax>(initializersAndSeparators), closeBrace);
    }

    private IfExpressionSyntax ParseIfExpression()
    {
        Token ifKw = MatchToken(TokenKind.IfKeyword);
        Token openParen = MatchToken(TokenKind.OpenParen);
        ExpressionSyntax condition = ParseExpression();
        Token closeParen = MatchToken(TokenKind.CloseParen);
        ExpressionSyntax thenExpr = ParseExpression();
        Token elseKw = MatchToken(TokenKind.ElseKeyword);
        ExpressionSyntax elseExpr = ParseExpression();
        return new IfExpressionSyntax(ifKw, openParen, condition, closeParen, thenExpr, elseKw, elseExpr);
    }

    // ──────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────

    private SeparatedSyntaxList<ExpressionSyntax> ParseArgumentList()
    {
        List<object> nodesAndSeparators = new();

        while (Current.Kind != TokenKind.CloseParen && Current.Kind != TokenKind.EndOfFile)
        {
            ExpressionSyntax arg;

            // Check for named argument: identifier = expr
            if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Equal
                && Peek(2).Kind != TokenKind.Equal) // not ==
            {
                Token name = NextToken();
                Token equals = NextToken();
                ExpressionSyntax value = ParseExpression();
                arg = new NamedArgumentSyntax(name, equals, value);
            }
            else
            {
                arg = ParseExpression();
            }

            nodesAndSeparators.Add(arg);

            if (Current.Kind == TokenKind.Comma)
                nodesAndSeparators.Add(NextToken());
            else
                break;
        }

        return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators);
    }

    private SeparatedSyntaxList<ExpressionSyntax> ParseExpressionList()
    {
        List<object> nodesAndSeparators = new();

        ExpressionSyntax first = ParseExpression();
        nodesAndSeparators.Add(first);

        while (Current.Kind == TokenKind.Comma)
        {
            nodesAndSeparators.Add(NextToken());
            nodesAndSeparators.Add(ParseExpression());
        }

        return new SeparatedSyntaxList<ExpressionSyntax>(nodesAndSeparators);
    }

    private static bool IsTypeStart(TokenKind kind) => kind switch
    {
        TokenKind.BoolKeyword or TokenKind.BitKeyword or TokenKind.NitKeyword or TokenKind.NibKeyword
            or TokenKind.U8Keyword or TokenKind.I8Keyword or TokenKind.U16Keyword or TokenKind.I16Keyword
            or TokenKind.U32Keyword or TokenKind.I32Keyword or TokenKind.VoidKeyword or TokenKind.U8x4Keyword
            or TokenKind.UintKeyword or TokenKind.IntKeyword
            or TokenKind.Star or TokenKind.OpenBracket
            or TokenKind.StructKeyword or TokenKind.UnionKeyword or TokenKind.EnumKeyword
            or TokenKind.BitfieldKeyword
            or TokenKind.Identifier => true,
        _ => false,
    };

    /// <summary>
    /// Convenience method: lex source text and create a parser.
    /// </summary>
    public static Parser Create(SourceText source, DiagnosticBag diagnostics)
    {
        Lexer lexer = new(source, diagnostics);
        List<Token> tokens = new();
        Token token;
        do
        {
            token = lexer.NextToken();
            if (token.Kind != TokenKind.Bad)
                tokens.Add(token);
        } while (token.Kind != TokenKind.EndOfFile);

        return new Parser(source, tokens, diagnostics);
    }
}
