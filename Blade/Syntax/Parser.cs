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
                // Could be variable declaration (reg var ...) or something else
                return ParseVariableDeclaration(externKeyword: null);

            case TokenKind.ConstKeyword:
                // const Name = packed struct { ... }; (type alias)
                // or const Name = comptime { ... }; (global const)
                return ParseTypeAliasOrConstDeclaration();

            case TokenKind.FnKeyword:
                return ParseFunctionDeclaration(funcKindKeyword: null);

            case TokenKind.LeafKeyword or TokenKind.InlineKeyword or TokenKind.RecKeyword
                 or TokenKind.CoroKeyword or TokenKind.Int1Keyword or TokenKind.Int2Keyword
                 or TokenKind.Int3Keyword:
                return ParseFunctionDeclaration(NextToken());

            case TokenKind.ComptimeKeyword:
                // comptime fn ... → function declaration
                // comptime { ... } → global statement with comptime expression
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
        Token path = MatchToken(TokenKind.StringLiteral);
        Token asKw = MatchToken(TokenKind.AsKeyword);
        Token alias = MatchToken(TokenKind.Identifier);
        Token semi = MatchToken(TokenKind.Semicolon);
        return new ImportDeclarationSyntax(importKw, path, asKw, alias, semi);
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
        Token storageClass = MatchStorageClass();
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

    private Token MatchStorageClass()
    {
        if (Current.Kind is TokenKind.RegKeyword or TokenKind.LutKeyword or TokenKind.HubKeyword)
            return NextToken();

        _diagnostics.ReportUnexpectedToken(Current.Span, "'reg', 'lut', or 'hub'", Current.Text);
        return new Token(TokenKind.RegKeyword, new TextSpan(Current.Span.Start, 0), "");
    }

    private MemberSyntax ParseTypeAliasOrConstDeclaration()
    {
        // const Name = packed struct { ... };
        // const Name = comptime { ... };
        Token constKw = NextToken(); // consume 'const'
        Token name = MatchToken(TokenKind.Identifier);
        Token equals = MatchToken(TokenKind.Equal);

        if (Current.Kind == TokenKind.PackedKeyword)
        {
            TypeSyntax type = ParseStructType();
            Token semi = MatchToken(TokenKind.Semicolon);
            return new TypeAliasDeclarationSyntax(constKw, name, equals, type, semi);
        }

        // Otherwise it's a const with an expression initializer (e.g., comptime { ... })
        ExpressionSyntax initializer = ParseExpression();
        Token semicolon = MatchToken(TokenKind.Semicolon);

        // We model this as a type alias for now — but it's really a global constant without storage class.
        // Wrap as a GlobalStatementSyntax containing the whole thing as an assignment-like construct.
        // Actually, let's just create a VariableDeclarationSyntax with a fabricated storage class.
        // The semantic analyzer will handle the special case.
        return new TypeAliasDeclarationSyntax(constKw, name, equals,
            new NamedTypeSyntax(new Token(TokenKind.Identifier, initializer.Span, "auto")),
            semicolon);
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

            case TokenKind.RegKeyword or TokenKind.LutKeyword or TokenKind.HubKeyword:
                // Variable declaration inside a block
                return new VariableDeclarationStatementSyntax(ParseVariableDeclaration(externKeyword: null));

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
        Token variable = MatchToken(TokenKind.Identifier);
        Token closeParen = MatchToken(TokenKind.CloseParen);
        BlockStatementSyntax body = ParseBlockStatement();
        return new ForStatementSyntax(forKw, openParen, variable, closeParen, body);
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
            Token openParen = MatchToken(TokenKind.OpenParen);
            ExpressionSyntax count = ParseExpression();
            Token closeParen = MatchToken(TokenKind.CloseParen);
            BlockStatementSyntax body = ParseBlockStatement();
            return new RepLoopStatementSyntax(repKw, loopKw, openParen, count, closeParen, body);
        }

        // rep for (ident in start..end) { ... }
        Token forKw = MatchToken(TokenKind.ForKeyword);
        Token openParen2 = MatchToken(TokenKind.OpenParen);
        Token variable = MatchToken(TokenKind.Identifier);
        Token inKw = MatchToken(TokenKind.InKeyword);
        RangeExpressionSyntax range = ParseRangeExpression();
        Token closeParen2 = MatchToken(TokenKind.CloseParen);
        BlockStatementSyntax body2 = ParseBlockStatement();
        return new RepForStatementSyntax(repKw, forKw, openParen2, variable, inKw, range, closeParen2, body2);
    }

    private NoirqStatementSyntax ParseNoirqStatement()
    {
        Token noirqKw = MatchToken(TokenKind.NoirqKeyword);
        BlockStatementSyntax body = ParseBlockStatement();
        return new NoirqStatementSyntax(noirqKw, body);
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

        AsmFlagOutputSyntax? flagOutput = null;
        if (Current.Kind == TokenKind.Arrow)
        {
            Token arrow = NextToken();
            Token at = MatchToken(TokenKind.At);
            Token flag = MatchToken(TokenKind.Identifier);
            flagOutput = new AsmFlagOutputSyntax(arrow, at, flag);
        }

        Token openBrace = MatchToken(TokenKind.OpenBrace);

        // Capture raw text between braces
        int bodyStart = openBrace.Span.End;
        int depth = 1;
        while (Current.Kind != TokenKind.EndOfFile && depth > 0)
        {
            if (Current.Kind == TokenKind.OpenBrace)
                depth++;
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
        Token semi = MatchToken(TokenKind.Semicolon);
        return new AsmBlockStatementSyntax(asmKw, flagOutput, openBrace, body, closeBrace, semi);
    }

    private StatementSyntax ParseExpressionOrAssignmentStatement()
    {
        ExpressionSyntax expression = ParseExpression();

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
                 or TokenKind.I32Keyword or TokenKind.VoidKeyword:
                return new PrimitiveTypeSyntax(NextToken());

            // Generic width: uint(N), int(N)
            case TokenKind.UintKeyword or TokenKind.IntKeyword:
                return ParseGenericWidthType();

            // Array: [expr]type
            case TokenKind.OpenBracket:
                return ParseArrayType();

            // Pointer: *type, *const type
            case TokenKind.Star:
                return ParsePointerType();

            // Packed struct
            case TokenKind.PackedKeyword:
                return ParseStructType();

            // Named type (user-defined)
            case TokenKind.Identifier:
                return new NamedTypeSyntax(NextToken());

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
        Token? constKw = null;
        if (Current.Kind == TokenKind.ConstKeyword)
            constKw = NextToken();
        TypeSyntax pointeeType = ParseType();
        return new PointerTypeSyntax(star, constKw, pointeeType);
    }

    private StructTypeSyntax ParseStructType()
    {
        Token packedKw = MatchToken(TokenKind.PackedKeyword);
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
        return new StructTypeSyntax(packedKw, structKw, openBrace, new SeparatedSyntaxList<StructFieldSyntax>(fieldsAndSeparators), closeBrace);
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
                    else if (Current.Kind == TokenKind.OpenBrace)
                    {
                        // .{ field initializers } — struct literal
                        // This is handled specially: the dot was already consumed as part of postfix.
                        // Actually, struct literals start with `.{` at primary level. Let's not handle here.
                        // Put the dot back? No, we already consumed it. This case shouldn't happen in valid code.
                        Token member = MatchToken(TokenKind.Identifier);
                        expression = new MemberAccessExpressionSyntax(expression, dot, member);
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

                default:
                    return expression;
            }
        }
    }

    private ExpressionSyntax ParsePrimaryExpression()
    {
        switch (Current.Kind)
        {
            case TokenKind.IntegerLiteral or TokenKind.StringLiteral:
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

            case TokenKind.At:
                return ParseIntrinsicCall();

            case TokenKind.Dot:
                if (Peek(1).Kind == TokenKind.OpenBrace)
                    return ParseStructLiteral();
                goto default;

            case TokenKind.ComptimeKeyword:
            {
                Token comptimeKw = NextToken();
                BlockStatementSyntax body = ParseBlockStatement();
                return new ComptimeExpressionSyntax(comptimeKw, body);
            }

            case TokenKind.IfKeyword:
                return ParseIfExpression();

            default:
                _diagnostics.ReportExpectedExpression(Current.Span);
                return new LiteralExpressionSyntax(
                    new Token(TokenKind.IntegerLiteral, new TextSpan(Current.Span.Start, 0), "", 0L));
        }
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

    private RangeExpressionSyntax ParseRangeExpression()
    {
        ExpressionSyntax start = ParseBinaryExpression(1); // parse at higher than range precedence
        Token dotDot = MatchToken(TokenKind.DotDot);
        ExpressionSyntax end = ParseBinaryExpression(1);
        return new RangeExpressionSyntax(start, dotDot, end);
    }

    // ──────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────

    private SeparatedSyntaxList<ExpressionSyntax> ParseArgumentList()
    {
        List<object> nodesAndSeparators = new();

        while (Current.Kind != TokenKind.CloseParen && Current.Kind != TokenKind.EndOfFile)
        {
            ExpressionSyntax arg = ParseExpression();
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
            or TokenKind.U32Keyword or TokenKind.I32Keyword or TokenKind.VoidKeyword
            or TokenKind.UintKeyword or TokenKind.IntKeyword
            or TokenKind.Star or TokenKind.OpenBracket or TokenKind.PackedKeyword
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
