// SysManager · SourceBraces — one place that knows where a C# block ends
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Tests;

/// <summary>
/// Finds the closing brace that matches an opening one, for the source-text guards that carve a file into
/// members, methods or blocks before asserting something about one of them.
/// </summary>
/// <remarks>
/// Counting bare <c>{</c> and <c>}</c> characters is not enough on this corpus: 293 string literals across the
/// three test projects hold an unbalanced brace, so a walker that cannot tell code from text ends a body in the
/// wrong place and then asserts about the wrong text — loudly, by reporting a compliant member, or silently, by
/// losing the mention that put a real offender in scope. Everything that is not code is stepped over here:
/// string, verbatim, raw, interpolated and character literals, and both comment forms.
/// </remarks>
internal static class SourceBraces
{
    /// <summary>
    /// The index of the <c>}</c> that closes the <c>{</c> at <paramref name="open"/>, or -1 when the source
    /// runs out first.
    /// </summary>
    internal static int MatchingBrace(string source, int open)
    {
        var depth = 0;

        for (var i = open; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return i;
                case '/' when CharAt(source, i + 1) is '/' or '*':
                    i = EndOfComment(source, i);
                    break;
                case '\'':
                    i = EndOfCharacterLiteral(source, i);
                    break;
                case '"':
                    i = EndOfStringLiteral(source, i);
                    break;
                case '@' or '$' when OpensAStringLiteral(source, i):
                    i = EndOfStringLiteral(source, i);
                    break;
            }

            // An unterminated literal or comment means the rest of the source is not parseable, so the block
            // this walk started in never closes.
            if (i < 0) return -1;
        }

        return -1;
    }

    /// <summary>The character at <paramref name="index"/>, or NUL past the end, so lookahead needs no bounds check.</summary>
    private static char CharAt(string source, int index) => index < source.Length ? source[index] : '\0';

    /// <summary>
    /// Whether the run of <c>@</c> and <c>$</c> characters at <paramref name="start"/> prefixes a string
    /// literal rather than being a verbatim identifier such as <c>@class</c>.
    /// </summary>
    private static bool OpensAStringLiteral(string source, int start)
    {
        var i = start;
        while (i < source.Length && source[i] is '@' or '$') i++;

        return CharAt(source, i) == '"';
    }

    /// <summary>The last index of the comment starting at <paramref name="start"/>, or -1 when it never ends.</summary>
    private static int EndOfComment(string source, int start)
    {
        if (source[start + 1] == '/')
        {
            return source.IndexOf('\n', start + 2);
        }

        var close = source.IndexOf("*/", start + 2, StringComparison.Ordinal);

        return close < 0 ? -1 : close + 1;
    }

    /// <summary>The index of the <c>'</c> that closes the character literal at <paramref name="start"/>.</summary>
    private static int EndOfCharacterLiteral(string source, int start)
    {
        for (var i = start + 1; i < source.Length; i++)
        {
            if (source[i] == '\\') i++;             // an escape covers the character after it
            else if (source[i] == '\'') return i;
        }

        return -1;
    }

    /// <summary>
    /// The last index of the string literal whose prefix or opening quote is at <paramref name="start"/>,
    /// whichever of the five forms it is, or -1 when it never closes.
    /// </summary>
    private static int EndOfStringLiteral(string source, int start)
    {
        var dollars = 0;
        var verbatim = false;

        var i = start;
        for (; i < source.Length && source[i] is '@' or '$'; i++)
        {
            if (source[i] == '$') dollars++;
            else verbatim = true;
        }

        // A run of three or more quotes opens a raw literal, where nothing is escaped and only the closing
        // fence ends it. Two quotes are an empty regular string, and @""" is a verbatim string that starts
        // with a doubled quote, so neither is raw.
        var fence = 0;
        while (CharAt(source, i + fence) == '"') fence++;

        return fence >= 3 && !verbatim
            ? EndOfRawStringLiteral(source, i + fence, fence)
            : EndOfQuotedStringLiteral(source, i, verbatim, interpolated: dollars > 0);
    }

    /// <summary>The last index of the fence that closes a raw literal whose content starts at <paramref name="contentStart"/>.</summary>
    private static int EndOfRawStringLiteral(string source, int contentStart, int fence)
    {
        for (var i = contentStart; i < source.Length; i++)
        {
            if (source[i] != '"') continue;

            var run = 0;
            while (CharAt(source, i + run) == '"') run++;

            if (run >= fence) return i + fence - 1;

            i += run - 1;                          // a shorter run is content; the loop step clears it
        }

        return -1;
    }

    /// <summary>
    /// The index of the quote that closes the single- or doubled-quote literal opening at
    /// <paramref name="quote"/>, stepping over escapes and, when interpolated, over holes of code.
    /// </summary>
    private static int EndOfQuotedStringLiteral(string source, int quote, bool verbatim, bool interpolated)
    {
        for (var i = quote + 1; i < source.Length; i++)
        {
            var c = source[i];

            if (c == '\\' && !verbatim)
            {
                i++;                               // an escape covers the character after it
            }
            else if (c == '"')
            {
                if (verbatim && CharAt(source, i + 1) == '"') i++;   // a doubled quote is content
                else return i;
            }
            else if (interpolated && c is '{' or '}')
            {
                // {{ and }} are escaped braces. A lone { opens a hole, which is code and can hold literals of
                // its own, so the same walk resolves it; a lone } does not compile and stays content.
                if (CharAt(source, i + 1) == c) i++;
                else if (c == '{' && (i = MatchingBrace(source, i)) < 0) return -1;
            }
        }

        return -1;
    }
}
