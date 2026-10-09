// SysManager · SourceBracesTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Xunit;

namespace SysManager.Tests;

/// <summary>
/// <c>SourceBraces.MatchingBrace</c> must find the brace that closes a block and no other, which means every
/// brace that is not code has to be stepped over: the ones inside string, verbatim, raw, interpolated and
/// character literals, and the ones inside comments.
/// </summary>
/// <remarks>
/// These are not hypothetical inputs. The corpus these guards read holds 293 string literals with an
/// unbalanced brace, and two of them — <c>"\n    }"</c> in <c>FileShredderViewModelTests</c> — close a body a
/// nesting level early. A walker fooled that way fails in both directions: the truncated body can lose the
/// evidence that made it compliant, which reports a clean test as an offender, or lose the mention that put it
/// in scope, which drops a real offender and quietly erodes the vacuity floor that was supposed to notice.
/// <para><c>SourceBraces.CodeOnly</c> makes the same walk, and has to blank exactly what it steps over while
/// leaving every other character where it was, so a match on its result points into the source.</para>
/// </remarks>
public class SourceBracesTests
{
    [Fact]
    public void MatchingBrace_FindsTheCloserOfAPlainBlock()
    {
        const string source = """
            void M()
            {
                Done();
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_PrefersTheOuterCloserOverANestedOne()
    {
        const string source = """
            void M()
            {
                if (x)
                {
                    Done();
                }
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_IsNotClosedEarlyByACloserInsideAStringLiteral()
    {
        const string source = """
            void M()
            {
                var end = source.IndexOf("\n    }", start);
                Done();
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_IsNotHeldOpenByAnOpenerInsideAStringLiteral()
    {
        const string source = """
            void M()
            {
                var json = "{";
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverAVerbatimLiteral()
    {
        const string source = """
            void M()
            {
                var path = @"C:\a } b";
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_EndsAVerbatimLiteralAtAQuoteAfterABackslash()
    {
        // In a verbatim literal a backslash is content, so this string ENDS at that quote and the brace
        // after it is code. A scanner that treated \" as an escape would swallow the rest of the block.
        const string source = """
            void M()
            {
                var dir = @"C:\";
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverADoubledQuoteInsideAVerbatimLiteral()
    {
        const string source = """
            void M()
            {
                var quoted = @"say ""} "" and stop";
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverAnEscapedQuoteInsideAStringLiteral()
    {
        const string source = """"
            void M()
            {
                var quoted = "say \"} \" and stop";
            }
            """";

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverARawLiteral()
    {
        const string source = """"
            void M()
            {
                var json = """{"k":"v"} }""";
            }
            """";

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverARawLiteralWhoseFenceIsLongerThanThree()
    {
        const string source = """""
            void M()
            {
                var nested = """"a """ inside } """";
            }
            """"";

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverAMultiLineRawLiteral()
    {
        const string source = """"
            void M()
            {
                var block = """
                    }
                    """;
            }
            """";

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverAnInterpolatedLiteralHoleAndItsEscapedBraces()
    {
        const string source = """
            void M()
            {
                var text = $"{{literal}} {value} }";
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverACharacterLiteral()
    {
        const string source = """
            void M()
            {
                var c = '}';
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverAnEscapedCharacterLiteral()
    {
        const string source = """
            void M()
            {
                var quote = '\'';
                var c = '}';
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverALineComment()
    {
        const string source = """
            void M()
            {
                // }
                Done();
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_StepsOverABlockComment()
    {
        const string source = """
            void M()
            {
                /* } { */
                Done();
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_DoesNotTreatADivisionAsAComment()
    {
        const string source = """
            void M()
            {
                var half = total / 2;
            }
            """;

        Assert.Equal(source.LastIndexOf('}'), SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_ReportsMinusOneWhenTheBlockNeverCloses()
    {
        const string source = """
            void M()
            {
                Done();
            """;

        Assert.Equal(-1, SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void MatchingBrace_ReportsMinusOneWhenTheOnlyCloserIsInsideALiteral()
    {
        // The negative case for literal awareness: nothing here closes the block, so a walker that counted
        // the brace in the literal would answer with a position inside a string.
        const string source = """
            void M()
            {
                var s = "}";
            """;

        Assert.Equal(-1, SourceBraces.MatchingBrace(source, source.IndexOf('{')));
    }

    [Fact]
    public void CodeOnly_BlanksLiteralsAndComments_AndLeavesTheCodeWhereItWas()
    {
        const string text = "\"x { y\"";
        const string character = "'}'";
        const string block = "/* b */";
        const string line = "// c";

        var code = SourceBraces.CodeOnly($"var a = {text}; {block} Done({character}, a); {line}");

        Assert.Equal($"var a = {Spaces(text)}; {Spaces(block)} Done({Spaces(character)}, a); {Spaces(line)}", code);
    }

    [Fact]
    public void CodeOnly_KeepsTheLineBreaksOfAMultiLineLiteralOrComment()
    {
        const string source = """"
            var block = """
                catch { }
                """;
            /* one
               two */
            Done();
            """";

        var code = SourceBraces.CodeOnly(source);

        Assert.Equal(source.Length, code.Length);
        Assert.Equal(source.Split('\n').Length, code.Split('\n').Length);
        Assert.DoesNotContain("catch", code, StringComparison.Ordinal);
        Assert.Equal(source.IndexOf("Done();", StringComparison.Ordinal),
                     code.IndexOf("Done();", StringComparison.Ordinal));
    }

    [Fact]
    public void CodeOnly_BlanksAnInterpolatedLiteralWithItsHoles()
    {
        const string source = """var text = $"{{literal}} {value} }"; Done();""";

        var code = SourceBraces.CodeOnly(source);

        Assert.DoesNotContain("value", code, StringComparison.Ordinal);
        Assert.Equal("var text =", code[..10]);
        Assert.EndsWith("; Done();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeOnly_LeavesAVerbatimIdentifierAndADivisionAlone()
    {
        const string source = "var @class = total / 2;";

        Assert.Equal(source, SourceBraces.CodeOnly(source));
    }

    [Fact]
    public void CodeOnly_BlanksTheRestOfTheSourceAfterACommentThatNeverEnds()
    {
        const string open = "/* never closed";
        const string rest = " catch { }";

        var code = SourceBraces.CodeOnly($"Done(); {open}\n{rest}");

        Assert.Equal($"Done(); {Spaces(open)}\n{Spaces(rest)}", code);
    }

    private static string Spaces(string text) => new(' ', text.Length);
}
