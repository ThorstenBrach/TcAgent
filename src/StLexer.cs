// Lossless lexer for TwinCAT Structured Text (the formatter: StFormatter.cs, docs/FORMATTER.md).
//
//   Every character of the input belongs to exactly one token, so the tokens put together give back the input byte for byte.
//   Whitespace, line breaks, comments ((* *) nested, /* */, //), pragmas ({ }), strings ('...', "..." with $ escapes),
//   numbers (16#FF, 1.5E3, 1_000), typed and time literals (INT#5, T#1s, TOD#12:00:00, E_State#Idle), direct addresses
//   (%IX0.0, %I*), operators (:=, =>, S=, R=, REF=, <=, >=, <>, **, ..) and punctuation.
//
// SPDX-License-Identifier: MIT
using System.Text;

namespace TcAgent;

public enum StTok { Space, Newline, LineComment, BlockComment, Pragma, String, Word, Number, Literal, Op, Punct, Other }

public sealed record StToken(StTok Kind, string Text)
{
    public bool Trivia => Kind is StTok.Space or StTok.Newline;
    public bool Comment => Kind is StTok.LineComment or StTok.BlockComment;
    public bool Is(string s) => (Kind is StTok.Word or StTok.Op or StTok.Punct) && Text.Equals(s, StringComparison.OrdinalIgnoreCase);
    public string Upper => Text.ToUpperInvariant();
    public override string ToString() => $"{Kind}:{Text}";
}

/// <summary>A section the lexer cannot read: the formatter leaves the text as it is and reports it.</summary>
public sealed class StSyntaxException(string message) : Exception(message);

public static class StLexer
{
    public static List<StToken> Lex(string s)
    {
        var t = new List<StToken>();
        int i = 0, n = s.Length;
        char At(int k) => k < n ? s[k] : '\0';
        int line = 1;
        while (i < n)
        {
            char c = s[i];
            int start = i;
            if (c == '\r' || c == '\n')
            {
                i += c == '\r' && At(i + 1) == '\n' ? 2 : 1;
                t.Add(new(StTok.Newline, s[start..i])); line++;
                continue;
            }
            if (c == ' ' || c == '\t')
            {
                while (i < n && (s[i] == ' ' || s[i] == '\t')) i++;
                t.Add(new(StTok.Space, s[start..i]));
                continue;
            }
            if (c == '/' && At(i + 1) == '/')
            {
                while (i < n && s[i] != '\r' && s[i] != '\n') i++;
                t.Add(new(StTok.LineComment, s[start..i]));
                continue;
            }
            if (c == '(' && At(i + 1) == '*')
            {
                int depth = 0;
                while (i < n)
                {
                    if (s[i] == '(' && At(i + 1) == '*') { depth++; i += 2; continue; }
                    if (s[i] == '*' && At(i + 1) == ')') { depth--; i += 2; if (depth == 0) break; continue; }
                    if (s[i] == '\n') line++;
                    i++;
                }
                if (depth != 0) throw new StSyntaxException($"line {LineOf(s, start)}: comment (* is not closed");
                t.Add(new(StTok.BlockComment, s[start..i]));
                continue;
            }
            if (c == '/' && At(i + 1) == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new StSyntaxException($"line {LineOf(s, start)}: comment /* is not closed");
                i = end + 2;
                t.Add(new(StTok.BlockComment, s[start..i]));
                continue;
            }
            if (c == '{')
            {
                char q = '\0';
                i++;
                while (i < n)
                {
                    char x = s[i];
                    if (q != '\0') { if (x == '$' && i + 1 < n) { i += 2; continue; } if (x == q) q = '\0'; i++; continue; }
                    // a quote opens a string only when it is closed on the same line ({region 'J2-} is a region named J2-)
                    if ((x == '\'' || x == '"') && ClosedOnLine(s, i)) { q = x; i++; continue; }
                    if (x == '}') { i++; break; }
                    i++;
                }
                if (s[i - 1] != '}' || i > n) throw new StSyntaxException($"line {LineOf(s, start)}: pragma {{ is not closed");
                t.Add(new(StTok.Pragma, s[start..i]));
                continue;
            }
            if (c == '\'' || c == '"')
            {
                i++;
                bool closed = false;
                while (i < n)
                {
                    if (s[i] == '$' && i + 1 < n) { i += 2; continue; }
                    if (s[i] == c) { i++; closed = true; break; }
                    if (s[i] == '\n' || s[i] == '\r') break;
                    i++;
                }
                if (!closed) throw new StSyntaxException($"line {LineOf(s, start)}: string is not closed");
                t.Add(new(StTok.String, s[start..i]));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                if (At(i) == '#' && i + 1 < n && !char.IsWhiteSpace(s[i + 1]))
                {
                    // typed literal, time literal or enum value: INT#5, T#1s500ms, TOD#12:00:00, DT#2026-01-01-12:00, E_State#Idle
                    i++;
                    i = LiteralBody(s, i);
                    t.Add(new(StTok.Literal, s[start..i]));
                    continue;
                }
                var w = s[start..i];
                // S= R= REF= after an operand: the set/reset/reference assignment
                if (At(i) == '=' && At(i + 1) != '=' && (w.Equals("S", StringComparison.OrdinalIgnoreCase) || w.Equals("R", StringComparison.OrdinalIgnoreCase) || w.Equals("REF", StringComparison.OrdinalIgnoreCase))
                    && PreviousSignificant(t) is { } prev && (prev.Kind == StTok.Word && !StKeywords.IsKeyword(prev.Text) || prev.Text is "]" or ")" or "^"))
                {
                    i++;
                    t.Add(new(StTok.Op, s[start..i]));
                    continue;
                }
                t.Add(new(StTok.Word, w));
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < n && (char.IsDigit(s[i]) || s[i] == '_')) i++;
                if (At(i) == '#') { i++; while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++; }
                else
                {
                    if (At(i) == '.' && char.IsDigit(At(i + 1))) { i++; while (i < n && (char.IsDigit(s[i]) || s[i] == '_')) i++; }
                    if ((At(i) == 'e' || At(i) == 'E') && (char.IsDigit(At(i + 1)) || (At(i + 1) is '+' or '-') && char.IsDigit(At(i + 2))))
                    { i += 2; while (i < n && char.IsDigit(s[i])) i++; }
                }
                t.Add(new(StTok.Number, s[start..i]));
                continue;
            }
            if (c == '%' && (char.IsLetter(At(i + 1))))
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] is '.' or '*')) i++;
                t.Add(new(StTok.Literal, s[start..i]));
                continue;
            }
            string two = i + 1 < n ? s.Substring(i, 2) : "";
            if (two is ":=" or "=>" or "<=" or ">=" or "<>" or "**" or "..")
            {
                i += 2;
                t.Add(new(StTok.Op, two));
                continue;
            }
            if ("()[],;:".IndexOf(c) >= 0) { i++; t.Add(new(StTok.Punct, c.ToString())); continue; }
            if ("+-*/=<>^.&#@?".IndexOf(c) >= 0) { i++; t.Add(new(StTok.Op, c.ToString())); continue; }
            i++;
            t.Add(new(StTok.Other, c.ToString()));
        }
        return t;
    }

    static bool ClosedOnLine(string s, int i)
    {
        char q = s[i];
        for (int k = i + 1; k < s.Length && s[k] != '\n' && s[k] != '\r'; k++)
        {
            if (s[k] == '$') { k++; continue; }
            if (s[k] == q) return true;
        }
        return false;
    }

    static int LiteralBody(string s, int i)
    {
        int n = s.Length;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        while (i < n)
        {
            char x = s[i];
            if (char.IsLetterOrDigit(x) || x == '_' || x == '.' && i + 1 < n && char.IsLetterOrDigit(s[i + 1]) || x == '#') { i++; continue; }
            if ((x == ':' || x == '-') && i + 1 < n && char.IsDigit(s[i + 1]) && i > 0 && char.IsDigit(s[i - 1])) { i++; continue; }
            break;
        }
        return i;
    }

    static StToken PreviousSignificant(List<StToken> t)
    {
        for (int k = t.Count - 1; k >= 0; k--) if (!t[k].Trivia && !t[k].Comment) return t[k];
        return null;
    }

    static int LineOf(string s, int pos)
    {
        int l = 1;
        for (int k = 0; k < pos && k < s.Length; k++) if (s[k] == '\n') l++;
        return l;
    }

    public static string Join(IEnumerable<StToken> t)
    {
        var sb = new StringBuilder();
        foreach (var x in t) sb.Append(x.Text);
        return sb.ToString();
    }

    /// <summary>The tokens that carry meaning, in a form that formatting may not change: keywords and the type prefix of a
    /// literal without case, comments without trailing spaces. Two texts with the same sequence mean the same.</summary>
    public static List<string> Significant(IEnumerable<StToken> tokens, bool ignoreParens = false)
    {
        var r = new List<string>();
        foreach (var t in tokens)
        {
            if (t.Trivia) continue;
            if (ignoreParens && t.Kind == StTok.Punct && t.Text is "(" or ")") continue;
            r.Add(t.Kind switch
            {
                StTok.Word => StKeywords.IsCaseFree(t.Text) ? "W:" + t.Upper : "W:" + t.Text,
                StTok.Literal => "L:" + t.Upper,
                StTok.Number => "N:" + t.Upper,
                StTok.Op => "O:" + t.Upper,
                StTok.LineComment => "C:" + t.Text.TrimEnd(),
                StTok.BlockComment => "B:" + t.Text,
                _ => t.Kind + ":" + t.Text
            });
        }
        return r;
    }
}

/// <summary>Keywords, types and literals of TwinCAT ST - the words whose case the formatter may change.</summary>
public static class StKeywords
{
    public static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF", "THEN", "ELSIF", "ELSE", "END_IF", "CASE", "OF", "END_CASE", "FOR", "TO", "BY", "DO", "END_FOR", "WHILE", "END_WHILE",
        "REPEAT", "UNTIL", "END_REPEAT", "EXIT", "CONTINUE", "RETURN", "JMP", "AND", "OR", "XOR", "NOT", "MOD", "AND_THEN", "OR_ELSE",
        "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_GLOBAL", "VAR_TEMP", "VAR_STAT", "VAR_EXTERNAL", "VAR_INST", "VAR_CONFIG",
        "END_VAR", "CONSTANT", "RETAIN", "PERSISTENT", "AT", "FUNCTION", "FUNCTION_BLOCK", "PROGRAM", "METHOD", "PROPERTY", "ACTION",
        "INTERFACE", "END_FUNCTION", "END_FUNCTION_BLOCK", "END_PROGRAM", "END_METHOD", "END_PROPERTY", "END_ACTION", "END_INTERFACE",
        "TYPE", "END_TYPE", "STRUCT", "END_STRUCT", "UNION", "END_UNION", "EXTENDS", "IMPLEMENTS", "PUBLIC", "PRIVATE", "PROTECTED",
        "INTERNAL", "ABSTRACT", "FINAL", "THIS", "SUPER", "ARRAY", "POINTER", "REFERENCE", "__NEW", "__DELETE", "__QUERYINTERFACE",
        "__QUERYPOINTER", "__ISVALIDREF", "ADR", "SIZEOF", "BITADR"
    };

    public static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL", "BIT", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "USINT", "INT", "UINT", "DINT", "UDINT", "LINT", "ULINT", "REAL", "LREAL",
        "TIME", "LTIME", "DATE", "LDATE", "TOD", "LTOD", "DT", "LDT", "TIME_OF_DAY", "LTIME_OF_DAY", "DATE_AND_TIME", "LDATE_AND_TIME",
        "STRING", "WSTRING", "ANY", "ANY_INT", "ANY_NUM", "ANY_REAL", "ANY_BIT", "ANY_STRING", "ANY_DATE", "PVOID", "XINT", "UXINT", "XWORD", "__XINT", "__UXINT", "__XWORD"
    };

    public static readonly HashSet<string> Literals = new(StringComparer.OrdinalIgnoreCase) { "TRUE", "FALSE" };

    /// <summary>Prefixes of typed and time literals: T#, TIME#, INT#, 16# is a number.</summary>
    public static readonly HashSet<string> LiteralPrefixes = new(StringComparer.OrdinalIgnoreCase) { "T", "D", "LT", "LD", "LTOD", "LDT" };

    public static bool IsKeyword(string w) => Keywords.Contains(w);
    public static bool IsCaseFree(string w) => Keywords.Contains(w) || Types.Contains(w) || Literals.Contains(w);

    /// <summary>Operators written as words.</summary>
    public static bool IsWordOperator(string w) => w.ToUpperInvariant() is "AND" or "OR" or "XOR" or "MOD" or "NOT" or "AND_THEN" or "OR_ELSE";
}
