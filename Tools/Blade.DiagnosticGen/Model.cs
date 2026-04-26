using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Blade.DiagnosticGen;

/// <summary>
/// Represents the parsed diagnostic message definition file.
/// </summary>
public sealed class Model
{
    private static readonly Regex CodePattern = new("^[EWI][0-9]{4}$", RegexOptions.Compiled);

    /// <summary>
    /// Parses diagnostic message definitions from the supplied lines.
    /// </summary>
    public static Model Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        HashSet<string> usings = new(StringComparer.Ordinal);
        List<Message> messages = [];
        HashSet<string> usedCodes = new(StringComparer.Ordinal);
        HashSet<string> usedNames = new(StringComparer.Ordinal);
        HashSet<string> usedClassNames = new(StringComparer.Ordinal);
        MessageBuilder? currentMessage = null;
        int lineNumber = 0;

        foreach (string rawLine in lines)
        {
            lineNumber++;
            string line = StripComment(rawLine).Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (TryStripPrefix(line, "using ", out string usingDecl))
            {
                if (currentMessage is not null)
                    messages.Add(currentMessage.Build(lineNumber - 1));
                currentMessage = null;

                ValidateUsing(usingDecl, lineNumber);
                usings.Add(usingDecl);
                continue;
            }

            if (TryParseMessageHeader(line, out MessageKind kind, out string name, out string code))
            {
                if (currentMessage is not null)
                    messages.Add(currentMessage.Build(lineNumber - 1));

                ValidateMessageHeader(name, code, lineNumber);
                string className = GetClassName(name, code);
                if (!usedNames.Add(name))
                    throw ParseException(lineNumber, $"Duplicate diagnostic name '{name}'.");
                if (!usedClassNames.Add(className))
                    throw ParseException(lineNumber, $"Duplicate generated diagnostic class name '{className}'.");
                if (!usedCodes.Add(code))
                    throw ParseException(lineNumber, $"Duplicate diagnostic code '{code}'.");

                currentMessage = new MessageBuilder(kind, name, code, className);
                continue;
            }

            if (currentMessage is null)
                throw ParseException(lineNumber, $"Unexpected top-level line '{line}'.");

            if (TryStripPrefix(line, "message:", out string messageExpression))
            {
                currentMessage.SetMessage(ParseMessageExpression(messageExpression, lineNumber), lineNumber);
                continue;
            }

            if (TryStripPrefix(line, "param:", out string paramDecl))
            {
                currentMessage.AddParameter(ParseParameter(paramDecl, lineNumber), lineNumber);
                continue;
            }

            throw ParseException(lineNumber, $"Unexpected message body line '{line}'.");
        }

        if (currentMessage is not null)
            messages.Add(currentMessage.Build(lineNumber));

        return new Model
        {
            Usings = usings.OrderBy(static item => item, StringComparer.Ordinal).ToArray(),
            Messages = messages,
        };
    }

    /// <summary>
    /// Gets the using declarations requested by the definition file.
    /// </summary>
    public required IReadOnlyList<string> Usings { get; init; }

    /// <summary>
    /// Gets the diagnostic messages declared by the definition file.
    /// </summary>
    public required IReadOnlyList<Message> Messages { get; init; }

    private static bool TryParseMessageHeader(
        string line,
        out MessageKind kind,
        out string name,
        out string code)
    {
        kind = MessageKind.Located;
        name = "";
        code = "";

        string? rest = null;
        if (TryStripPrefix(line, "located ", out string locatedDecl))
        {
            kind = MessageKind.Located;
            rest = locatedDecl;
        }
        else if (TryStripPrefix(line, "generic ", out string genericDecl))
        {
            kind = MessageKind.Generic;
            rest = genericDecl;
        }

        if (rest is null)
            return false;

        string[] items = rest.Split(':', StringSplitOptions.TrimEntries);
        if (items.Length != 2)
            return false;

        name = items[0];
        code = items[1];
        return true;
    }

    private static string StripComment(string rawLine)
    {
        bool inString = false;
        bool verbatimString = false;
        for (int i = 0; i < rawLine.Length; i++)
        {
            char ch = rawLine[i];
            if (!inString)
            {
                if (ch == '"')
                {
                    inString = true;
                    verbatimString = i > 0 && rawLine[i - 1] == '@';
                }
                else if (ch == '#')
                {
                    return rawLine[..i];
                }

                continue;
            }

            if (ch != '"')
                continue;

            if (verbatimString && i + 1 < rawLine.Length && rawLine[i + 1] == '"')
            {
                i++;
                continue;
            }

            if (!verbatimString && i > 0 && rawLine[i - 1] == '\\')
            {
                int backslashCount = 0;
                int scan = i - 1;
                while (scan >= 0 && rawLine[scan] == '\\')
                {
                    backslashCount++;
                    scan--;
                }

                if (backslashCount % 2 == 1)
                    continue;
            }

            inString = false;
            verbatimString = false;
        }

        return rawLine;
    }

    private static void ValidateUsing(string usingDecl, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(usingDecl))
            throw ParseException(lineNumber, "Expected a namespace after 'using'.");

        string[] parts = usingDecl.Split('.');
        foreach (string part in parts)
        {
            if (!IsValidIdentifier(part))
                throw ParseException(lineNumber, $"Invalid using declaration '{usingDecl}'.");
        }
    }

    private static void ValidateMessageHeader(string name, string code, int lineNumber)
    {
        if (!IsValidIdentifier(name))
            throw ParseException(lineNumber, $"Invalid diagnostic name '{name}'.");
        if (!CodePattern.IsMatch(code))
            throw ParseException(lineNumber, $"Invalid diagnostic code '{code}'. Expected E0000, W0000, or I0000.");
    }

    private static MessageText ParseMessageExpression(string expression, int lineNumber)
    {
        if (expression.StartsWith("$\"", StringComparison.Ordinal) || expression.StartsWith("$@\"", StringComparison.Ordinal))
            return new MessageText(expression, IsInterpolated: true);
        if (expression.StartsWith('"') || expression.StartsWith("@\"", StringComparison.Ordinal))
            return new MessageText(expression, IsInterpolated: false);

        throw ParseException(lineNumber, "Expected message text to be a string literal or interpolated string literal.");
    }

    private static MessageParameter ParseParameter(string paramDecl, int lineNumber)
    {
        int split = paramDecl.LastIndexOf(' ');
        if (split <= 0 || split == paramDecl.Length - 1)
            throw ParseException(lineNumber, "Expected parameter declaration '<type> <name>'.");

        string type = paramDecl[..split].Trim();
        string name = paramDecl[(split + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(type))
            throw ParseException(lineNumber, "Expected parameter type.");
        if (!IsValidIdentifier(name))
            throw ParseException(lineNumber, $"Invalid parameter name '{name}'.");

        return new MessageParameter(type, name);
    }

    private static string GetClassName(string name, string code)
    {
        string suffix = code[0] switch
        {
            'E' => "Error",
            'W' => "Warning",
            'I' => "Note",
            _ => throw new InvalidOperationException("Validated diagnostic codes must start with E, W, or I."),
        };

        return name + suffix;
    }

    private static bool IsValidIdentifier(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!IsIdentifierStart(text[0]))
            return false;

        for (int i = 1; i < text.Length; i++)
        {
            if (!IsIdentifierPart(text[i]))
                return false;
        }

        return true;
    }

    private static bool IsIdentifierStart(char ch) => ch == '_' || char.IsLetter(ch);

    private static bool IsIdentifierPart(char ch) => ch == '_' || char.IsLetterOrDigit(ch);

    private static bool TryStripPrefix(string line, string prefix, out string stripped)
    {
        stripped = "";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        stripped = line[prefix.Length..].Trim();
        return true;
    }

    private static FormatException ParseException(int lineNumber, string message)
    {
        return new FormatException($"Messages.def line {lineNumber}: {message}");
    }

    private sealed class MessageBuilder(
        MessageKind kind,
        string name,
        string code,
        string className)
    {
        private readonly HashSet<string> _parameterNames = new(StringComparer.Ordinal);
        private readonly List<MessageParameter> _parameters = [];
        private MessageText? _messageText;

        public void SetMessage(MessageText messageText, int lineNumber)
        {
            if (_messageText is not null)
                throw ParseException(lineNumber, $"Diagnostic '{name}' already has a message.");
            _messageText = messageText;
        }

        public void AddParameter(MessageParameter parameter, int lineNumber)
        {
            if (!_parameterNames.Add(parameter.Name))
                throw ParseException(lineNumber, $"Diagnostic '{name}' already has a parameter named '{parameter.Name}'.");
            _parameters.Add(parameter);
        }

        public Message Build(int lineNumber)
        {
            if (_messageText is null)
                throw ParseException(lineNumber, $"Diagnostic '{name}' is missing a message.");

            return new Message(
                kind,
                name,
                code,
                className,
                _messageText,
                _parameters.ToArray());
        }
    }
}

/// <summary>
/// A typed diagnostic message declaration parsed from the definition file.
/// </summary>
public sealed record Message(
    MessageKind Kind,
    string Name,
    string Code,
    string ClassName,
    MessageText Text,
    IReadOnlyList<MessageParameter> Parameters);

/// <summary>
/// Describes whether a diagnostic is tied to a source span.
/// </summary>
public enum MessageKind
{
    /// <summary>
    /// A diagnostic that carries a source text and text span.
    /// </summary>
    Located,

    /// <summary>
    /// A diagnostic that is not associated with a source span.
    /// </summary>
    Generic,
}

/// <summary>
/// The C# string expression used to construct a diagnostic message.
/// </summary>
public sealed record MessageText(string Expression, bool IsInterpolated);

/// <summary>
/// A typed constructor parameter declared for a diagnostic message.
/// </summary>
public sealed record MessageParameter(string Type, string Name);
