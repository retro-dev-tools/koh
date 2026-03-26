# Koh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a modern C# Game Boy assembler and linker platform with Roslyn-style architecture, full RGBDS syntax compatibility, and library-first design.

**Architecture:** Red-green syntax trees (immutable green nodes + lazy red wrappers), hand-written recursive descent parser with Pratt expression parsing, binding phase producing semantic model and emit model, constraint-based linker, VS Code LSP server. All consumed through `Koh.Core` and `Koh.Linker.Core` libraries.

**Tech Stack:** C# 14 / .NET 10, TUnit, Native AOT, VS Code LSP protocol

**Spec:** `docs/superpowers/specs/2026-03-25-koh-assembler-design.md`

---

## Phase Overview

| Phase | Milestone | You can now... |
|-------|-----------|----------------|
| 1 | Foundation | Parse `ld a, b` + `NOP` and walk the red-green tree |
| 2 | Core Instructions | Parse any SM83 instruction with all addressing modes |
| 3 | Labels & Expressions | Parse `label: ld a, [hl]` with forward refs, evaluate `1 + 2 * 3` |
| 4 | Sections & Data | Parse `SECTION "ROM0", ROM0` + `DB`, `DW`, `DS` directives |
| 5 | Binding & Emit | Assemble a single-file program to a `.kobj` with resolved symbols, instruction validation, SemanticModel API |
| 6 | CLI Assembler | Run `koh-asm hello.asm -o hello.kobj` from the command line |
| 7 | Linker | Run `koh-link hello.kobj -o hello.gb` — a working ROM |
| 8 | Macros & Directives | Full RGBDS directive coverage — assemble real-world projects |
| 9 | RGBDS Compat | `--format rgbds` emits `.o` files linkable with `rgblink` |
| 10 | LSP Server | VS Code extension with diagnostics, hover, go-to-definition |
| 11 | Polish | Incremental reparsing, parallel parsing, constraint solver diagnostics |

---

## Phase 1: Foundation

**Milestone:** Parse `nop` and `ld a, b`, walk the red-green syntax tree, round-trip to source text.

### Task 1.1: Solution Scaffold

**Files:**
- Create: `Koh.sln`
- Create: `src/Koh.Core/Koh.Core.csproj`
- Create: `tests/Koh.Core.Tests/Koh.Core.Tests.csproj`
- Create: `.gitignore`
- Create: `global.json`
- Create: `Directory.Build.props`

- [x] **Step 1: Create solution and projects**

```bash
cd /c/projekty/koh
dotnet new sln -n Koh
dotnet new classlib -n Koh.Core -o src/Koh.Core -f net10.0
dotnet new classlib -n Koh.Core.Tests -o tests/Koh.Core.Tests -f net10.0
dotnet sln add src/Koh.Core/Koh.Core.csproj
dotnet sln add tests/Koh.Core.Tests/Koh.Core.Tests.csproj
dotnet add tests/Koh.Core.Tests reference src/Koh.Core
```

- [x] **Step 2: Configure TUnit**

Add TUnit package to test project. Configure `Directory.Build.props` for shared settings (nullable, implicit usings, LangVersion preview).

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>
</Project>
```

```bash
cd /c/projekty/koh
dotnet add tests/Koh.Core.Tests package TUnit
```

- [x] **Step 3: Add .gitignore**

Standard .NET gitignore (bin/, obj/, .vs/, *.user).

- [x] **Step 4: Verify build**

```bash
dotnet build
dotnet test
```

Expected: build succeeds, 0 tests run.

- [x] **Step 5: Commit**

```bash
git add -A
git commit -m "scaffold: solution with Koh.Core and test project"
```

### Task 1.2: SyntaxKind Enum (Starter Set)

**Files:**
- Create: `src/Koh.Core/Syntax/SyntaxKind.cs`
- Test: `tests/Koh.Core.Tests/Syntax/SyntaxKindTests.cs`

- [x] **Step 1: Write test that SyntaxKind has expected members**

```csharp
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class SyntaxKindTests
{
    [Test]
    public async Task SyntaxKind_HasTokenKinds()
    {
        // Verify essential token kinds exist
        var kind = SyntaxKind.EndOfFileToken;
        await Assert.That(kind).IsEqualTo(SyntaxKind.EndOfFileToken);
    }

    [Test]
    public async Task SyntaxKind_HasTriviaKinds()
    {
        var kind = SyntaxKind.WhitespaceTrivia;
        await Assert.That(kind).IsEqualTo(SyntaxKind.WhitespaceTrivia);
    }

    [Test]
    public async Task SyntaxKind_HasNodeKinds()
    {
        var kind = SyntaxKind.CompilationUnit;
        await Assert.That(kind).IsEqualTo(SyntaxKind.CompilationUnit);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

```bash
dotnet test
```

Expected: FAIL — `SyntaxKind` type does not exist.

- [x] **Step 3: Implement SyntaxKind**

```csharp
namespace Koh.Core.Syntax;

public enum SyntaxKind : ushort
{
    // Special
    None = 0,
    EndOfFileToken,
    BadToken,
    MissingToken,

    // Trivia
    WhitespaceTrivia,
    LineCommentTrivia,
    BlockCommentTrivia,
    NewlineTrivia,
    SkippedTokensTrivia,

    // Punctuation
    CommaToken,
    OpenParenToken,
    CloseParenToken,
    OpenBracketToken,
    CloseBracketToken,
    ColonToken,
    DoubleColonToken,
    DotToken,
    HashToken,

    // Operators
    PlusToken,
    MinusToken,
    StarToken,
    SlashToken,
    PercentToken,
    AmpersandToken,
    PipeToken,
    CaretToken,
    TildeToken,
    BangToken,
    LessThanToken,
    GreaterThanToken,
    LessThanLessThanToken,
    GreaterThanGreaterThanToken,
    EqualsEqualsToken,
    BangEqualsToken,
    LessThanEqualsToken,
    GreaterThanEqualsToken,
    AmpersandAmpersandToken,
    PipePipeToken,

    // Literals
    NumberLiteral,
    StringLiteral,
    IdentifierToken,

    // SM83 instruction keywords (starter set)
    NopKeyword,
    LdKeyword,
    AddKeyword,

    // Register keywords
    AKeyword,
    BKeyword,
    CKeyword,
    DKeyword,
    EKeyword,
    HKeyword,
    LKeyword,
    HlKeyword,
    SpKeyword,
    AfKeyword,
    BcKeyword,
    DeKeyword,

    // Directive keywords (starter set)
    SectionKeyword,
    DbKeyword,
    DwKeyword,
    DsKeyword,

    // Nodes
    CompilationUnit,
    InstructionStatement,
    LabelDeclaration,
    DirectiveStatement,
    SectionDirective,
    DataDirective,

    // Expression nodes
    LiteralExpression,
    NameExpression,
    BinaryExpression,
    UnaryExpression,
    ParenthesizedExpression,
}
```

- [x] **Step 4: Run tests**

```bash
dotnet test
```

Expected: PASS

- [x] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add SyntaxKind enum with starter token/node kinds"
```

### Task 1.3: Green Node Infrastructure

**Files:**
- Create: `src/Koh.Core/Syntax/InternalSyntax/GreenNode.cs`
- Create: `src/Koh.Core/Syntax/InternalSyntax/GreenToken.cs`
- Create: `src/Koh.Core/Syntax/InternalSyntax/GreenTrivia.cs`
- Test: `tests/Koh.Core.Tests/Syntax/InternalSyntax/GreenNodeTests.cs`

- [x] **Step 1: Write failing tests for green nodes**

```csharp
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Tests.Syntax.InternalSyntax;

public class GreenNodeTests
{
    [Test]
    public async Task GreenToken_StoresKindAndWidth()
    {
        var token = new GreenToken(SyntaxKind.NopKeyword, "nop");
        await Assert.That(token.Kind).IsEqualTo(SyntaxKind.NopKeyword);
        await Assert.That(token.FullWidth).IsEqualTo(3);
    }

    [Test]
    public async Task GreenToken_WithLeadingTrivia()
    {
        var trivia = new GreenTrivia(SyntaxKind.WhitespaceTrivia, "  ");
        var token = new GreenToken(SyntaxKind.NopKeyword, "nop",
            leadingTrivia: [trivia], trailingTrivia: []);
        await Assert.That(token.FullWidth).IsEqualTo(5); // 2 spaces + 3 chars
        await Assert.That(token.Width).IsEqualTo(3); // text only
    }

    [Test]
    public async Task GreenNode_WithChildren()
    {
        var nop = new GreenToken(SyntaxKind.NopKeyword, "nop");
        var newline = new GreenToken(SyntaxKind.EndOfFileToken, "");
        var statement = new GreenNode(SyntaxKind.InstructionStatement, [nop]);
        var unit = new GreenNode(SyntaxKind.CompilationUnit, [statement, newline]);

        await Assert.That(unit.Kind).IsEqualTo(SyntaxKind.CompilationUnit);
        await Assert.That(unit.ChildCount).IsEqualTo(2);
        await Assert.That(unit.FullWidth).IsEqualTo(3);
    }
}
```

- [x] **Step 2: Run tests to verify failure**

```bash
dotnet test
```

Expected: FAIL — types do not exist.

- [x] **Step 3: Implement GreenTrivia**

```csharp
namespace Koh.Core.Syntax.InternalSyntax;

public sealed class GreenTrivia
{
    public SyntaxKind Kind { get; }
    public string Text { get; }
    public int Width => Text.Length;

    public GreenTrivia(SyntaxKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }
}
```

- [x] **Step 4: Implement GreenToken**

```csharp
namespace Koh.Core.Syntax.InternalSyntax;

public sealed class GreenToken : GreenNodeBase
{
    public string Text { get; }
    public IReadOnlyList<GreenTrivia> LeadingTrivia { get; }
    public IReadOnlyList<GreenTrivia> TrailingTrivia { get; }

    public override int Width => Text.Length;
    public override int FullWidth => LeadingTriviaWidth + Width + TrailingTriviaWidth;
    public override int ChildCount => 0;

    public int LeadingTriviaWidth => LeadingTrivia.Sum(t => t.Width);
    public int TrailingTriviaWidth => TrailingTrivia.Sum(t => t.Width);

    public GreenToken(SyntaxKind kind, string text,
        IReadOnlyList<GreenTrivia>? leadingTrivia = null,
        IReadOnlyList<GreenTrivia>? trailingTrivia = null)
        : base(kind)
    {
        Text = text;
        LeadingTrivia = leadingTrivia ?? [];
        TrailingTrivia = trailingTrivia ?? [];
    }

    public override GreenNodeBase? GetChild(int index) => null;
}
```

- [x] **Step 5: Implement GreenNode**

```csharp
namespace Koh.Core.Syntax.InternalSyntax;

public abstract class GreenNodeBase
{
    public SyntaxKind Kind { get; }
    public abstract int Width { get; }
    public abstract int FullWidth { get; }
    public abstract int ChildCount { get; }
    public abstract GreenNodeBase? GetChild(int index);

    protected GreenNodeBase(SyntaxKind kind)
    {
        Kind = kind;
    }
}

public sealed class GreenNode : GreenNodeBase
{
    private readonly GreenNodeBase[] _children;

    public override int ChildCount => _children.Length;
    public override int Width => FullWidth; // non-token nodes: width == fullWidth
    public override int FullWidth { get; }

    public GreenNode(SyntaxKind kind, GreenNodeBase[] children) : base(kind)
    {
        _children = children;
        FullWidth = children.Sum(c => c.FullWidth);
    }

    public override GreenNodeBase? GetChild(int index)
    {
        if (index < 0 || index >= _children.Length) return null;
        return _children[index];
    }
}
```

- [x] **Step 6: Run tests**

```bash
dotnet test
```

Expected: PASS

- [x] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: green node infrastructure (GreenNode, GreenToken, GreenTrivia)"
```

### Task 1.4: Red Node Infrastructure

**Files:**
- Create: `src/Koh.Core/Syntax/SyntaxNode.cs`
- Create: `src/Koh.Core/Syntax/SyntaxToken.cs`
- Create: `src/Koh.Core/Syntax/SyntaxTrivia.cs`
- Create: `src/Koh.Core/Syntax/SyntaxNodeOrToken.cs`
- Create: `src/Koh.Core/Syntax/TextSpan.cs`
- Test: `tests/Koh.Core.Tests/Syntax/SyntaxNodeTests.cs`

- [x] **Step 1: Write failing tests for red nodes**

```csharp
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Tests.Syntax;

public class SyntaxNodeTests
{
    [Test]
    public async Task SyntaxNode_HasParent()
    {
        var nopGreen = new GreenToken(SyntaxKind.NopKeyword, "nop");
        var stmtGreen = new GreenNode(SyntaxKind.InstructionStatement, [nopGreen]);
        var eofGreen = new GreenToken(SyntaxKind.EndOfFileToken, "");
        var rootGreen = new GreenNode(SyntaxKind.CompilationUnit, [stmtGreen, eofGreen]);

        var root = new SyntaxNode(rootGreen, parent: null, position: 0);

        await Assert.That(root.Parent).IsNull();
        await Assert.That(root.Kind).IsEqualTo(SyntaxKind.CompilationUnit);

        var children = root.ChildNodes().ToList();
        await Assert.That(children).HasCount().EqualTo(1); // only nodes, not tokens
        await Assert.That(children[0].Parent).IsEqualTo(root);
    }

    [Test]
    public async Task SyntaxNode_TracksPosition()
    {
        // "  nop" — 2 spaces leading trivia + 3 char token
        var trivia = new GreenTrivia(SyntaxKind.WhitespaceTrivia, "  ");
        var nopGreen = new GreenToken(SyntaxKind.NopKeyword, "nop",
            leadingTrivia: [trivia]);
        var stmtGreen = new GreenNode(SyntaxKind.InstructionStatement, [nopGreen]);
        var eofGreen = new GreenToken(SyntaxKind.EndOfFileToken, "");
        var rootGreen = new GreenNode(SyntaxKind.CompilationUnit, [stmtGreen, eofGreen]);

        var root = new SyntaxNode(rootGreen, parent: null, position: 0);
        var stmt = root.ChildNodes().First();
        var token = stmt.ChildTokens().First();

        await Assert.That(token.Position).IsEqualTo(0); // full position including trivia
        await Assert.That(token.Span.Start).IsEqualTo(2); // text start after trivia
        await Assert.That(token.Span.Length).IsEqualTo(3); // "nop"
    }

    [Test]
    public async Task SyntaxNode_Span()
    {
        var nopGreen = new GreenToken(SyntaxKind.NopKeyword, "nop");
        var stmtGreen = new GreenNode(SyntaxKind.InstructionStatement, [nopGreen]);
        var eofGreen = new GreenToken(SyntaxKind.EndOfFileToken, "");
        var rootGreen = new GreenNode(SyntaxKind.CompilationUnit, [stmtGreen, eofGreen]);

        var root = new SyntaxNode(rootGreen, parent: null, position: 0);

        await Assert.That(root.FullSpan).IsEqualTo(new TextSpan(0, 3));
    }
}
```

- [x] **Step 2: Run tests to verify failure**

- [x] **Step 3: Implement TextSpan**

```csharp
namespace Koh.Core.Syntax;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public bool Contains(int position) => position >= Start && position < End;
    public bool OverlapsWith(TextSpan other) => Start < other.End && other.Start < End;
}
```

- [x] **Step 4: Implement SyntaxTrivia**

```csharp
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Syntax;

public readonly struct SyntaxTrivia
{
    private readonly GreenTrivia _green;

    public SyntaxKind Kind => _green.Kind;
    public string Text => _green.Text;
    public int Position { get; }
    public TextSpan Span => new(Position, _green.Width);

    internal SyntaxTrivia(GreenTrivia green, int position)
    {
        _green = green;
        Position = position;
    }
}
```

- [x] **Step 5: Implement SyntaxToken**

```csharp
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Syntax;

public sealed class SyntaxToken
{
    private readonly GreenToken _green;

    public SyntaxKind Kind => _green.Kind;
    public string Text => _green.Text;
    public int Position { get; }
    public SyntaxNode? Parent { get; }

    public TextSpan Span => new(Position + _green.LeadingTriviaWidth, _green.Width);
    public TextSpan FullSpan => new(Position, _green.FullWidth);

    public IEnumerable<SyntaxTrivia> LeadingTrivia
    {
        get
        {
            int pos = Position;
            foreach (var trivia in _green.LeadingTrivia)
            {
                yield return new SyntaxTrivia(trivia, pos);
                pos += trivia.Width;
            }
        }
    }

    public IEnumerable<SyntaxTrivia> TrailingTrivia
    {
        get
        {
            int pos = Position + _green.LeadingTriviaWidth + _green.Width;
            foreach (var trivia in _green.TrailingTrivia)
            {
                yield return new SyntaxTrivia(trivia, pos);
                pos += trivia.Width;
            }
        }
    }

    public bool IsMissing => _green.Width == 0 && Kind != SyntaxKind.EndOfFileToken;

    internal SyntaxToken(GreenToken green, SyntaxNode? parent, int position)
    {
        _green = green;
        Parent = parent;
        Position = position;
    }
}
```

- [x] **Step 6: Implement SyntaxNode**

```csharp
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Syntax;

public class SyntaxNode
{
    private readonly GreenNodeBase _green;

    public SyntaxKind Kind => _green.Kind;
    public SyntaxNode? Parent { get; }
    public int Position { get; }
    public TextSpan FullSpan => new(Position, _green.FullWidth);
    public TextSpan Span => CalculateSpan();

    internal SyntaxNode(GreenNodeBase green, SyntaxNode? parent, int position)
    {
        _green = green;
        Parent = parent;
        Position = position;
    }

    public IEnumerable<SyntaxNode> ChildNodes()
    {
        int offset = Position;
        for (int i = 0; i < _green.ChildCount; i++)
        {
            var child = _green.GetChild(i)!;
            if (child is not GreenToken)
            {
                yield return new SyntaxNode(child, this, offset);
            }
            offset += child.FullWidth;
        }
    }

    public IEnumerable<SyntaxToken> ChildTokens()
    {
        int offset = Position;
        for (int i = 0; i < _green.ChildCount; i++)
        {
            var child = _green.GetChild(i)!;
            if (child is GreenToken token)
            {
                yield return new SyntaxToken(token, this, offset);
            }
            offset += child.FullWidth;
        }
    }

    public IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        int offset = Position;
        for (int i = 0; i < _green.ChildCount; i++)
        {
            var child = _green.GetChild(i)!;
            if (child is GreenToken greenToken)
                yield return new SyntaxNodeOrToken(new SyntaxToken(greenToken, this, offset));
            else
                yield return new SyntaxNodeOrToken(new SyntaxNode(child, this, offset));
            offset += child.FullWidth;
        }
    }

    private TextSpan CalculateSpan()
    {
        // Span excludes leading/trailing trivia of first/last tokens
        int start = Position;
        int end = Position + _green.FullWidth;
        // For simplicity in V1, FullSpan == Span for non-token nodes
        return new TextSpan(start, end - start);
    }
}
```

- [x] **Step 7: Run tests**

```bash
dotnet test
```

Expected: PASS

- [x] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: red node infrastructure (SyntaxNode, SyntaxToken, SyntaxTrivia, TextSpan)"
```

### Task 1.5: SourceText

**Files:**
- Create: `src/Koh.Core/Text/SourceText.cs`
- Create: `src/Koh.Core/Text/TextLine.cs`
- Create: `src/Koh.Core/Text/TextChange.cs`
- Test: `tests/Koh.Core.Tests/Text/SourceTextTests.cs`

- [x] **Step 1: Write failing tests**

```csharp
using Koh.Core.Text;

namespace Koh.Core.Tests.Text;

public class SourceTextTests
{
    [Test]
    public async Task SourceText_FromString()
    {
        var text = SourceText.From("hello\nworld");
        await Assert.That(text.Length).IsEqualTo(11);
        await Assert.That(text[0]).IsEqualTo('h');
    }

    [Test]
    public async Task SourceText_Lines()
    {
        var text = SourceText.From("hello\nworld\n");
        var lines = text.Lines;
        await Assert.That(lines.Count).IsEqualTo(3); // "hello", "world", ""
        await Assert.That(lines[0].Start).IsEqualTo(0);
        await Assert.That(lines[1].Start).IsEqualTo(6);
    }

    [Test]
    public async Task SourceText_GetLineIndex()
    {
        var text = SourceText.From("aaa\nbbb\nccc");
        await Assert.That(text.GetLineIndex(0)).IsEqualTo(0); // 'a'
        await Assert.That(text.GetLineIndex(4)).IsEqualTo(1); // 'b'
        await Assert.That(text.GetLineIndex(8)).IsEqualTo(2); // 'c'
    }

    [Test]
    public async Task SourceText_WithChanges()
    {
        var text = SourceText.From("hello world");
        var changed = text.WithChanges(new TextChange(new(5, 1), "_"));
        await Assert.That(changed.ToString()).IsEqualTo("hello_world");
    }
}
```

- [x] **Step 2: Run tests to verify failure**

- [x] **Step 3: Implement TextChange, TextLine, SourceText**

```csharp
// TextChange.cs
using Koh.Core.Syntax;

namespace Koh.Core.Text;

public readonly record struct TextChange(TextSpan Span, string NewText);
```

```csharp
// TextLine.cs
namespace Koh.Core.Text;

public readonly record struct TextLine(int Start, int Length, int LengthIncludingLineBreak)
{
    public int End => Start + Length;
}
```

```csharp
// SourceText.cs
using Koh.Core.Syntax;

namespace Koh.Core.Text;

public sealed class SourceText
{
    private readonly string _text;
    private readonly TextLine[] _lines;

    public int Length => _text.Length;
    public char this[int index] => _text[index];
    public string FilePath { get; }
    public IReadOnlyList<TextLine> Lines => _lines;

    private SourceText(string text, string filePath = "")
    {
        _text = text;
        FilePath = filePath;
        _lines = ParseLines(text);
    }

    public static SourceText From(string text, string filePath = "")
        => new(text, filePath);

    public int GetLineIndex(int position)
    {
        int lo = 0, hi = _lines.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (position < _lines[mid].Start)
                hi = mid - 1;
            else if (mid + 1 < _lines.Length && position >= _lines[mid + 1].Start)
                lo = mid + 1;
            else
                return mid;
        }
        return 0;
    }

    public string ToString(TextSpan span) => _text.Substring(span.Start, span.Length);
    public override string ToString() => _text;

    public SourceText WithChanges(TextChange change)
    {
        var newText = string.Concat(
            _text.AsSpan(0, change.Span.Start),
            change.NewText,
            _text.AsSpan(change.Span.End));
        return new SourceText(newText, FilePath);
    }

    private static TextLine[] ParseLines(string text)
    {
        var lines = new List<TextLine>();
        int lineStart = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int lineLength = i - lineStart;
                lines.Add(new TextLine(lineStart, lineLength, lineLength + 1));
                lineStart = i + 1;
            }
            else if (text[i] == '\r')
            {
                int lineLength = i - lineStart;
                int lineBreakWidth = (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                lines.Add(new TextLine(lineStart, lineLength, lineLength + lineBreakWidth));
                if (lineBreakWidth == 2) i++;
                lineStart = i + 1;
            }
        }

        lines.Add(new TextLine(lineStart, text.Length - lineStart, text.Length - lineStart));
        return lines.ToArray();
    }
}
```

- [x] **Step 4: Run tests**

```bash
dotnet test
```

Expected: PASS

- [x] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: SourceText with line tracking and incremental changes"
```

### Task 1.6: Lexer (Starter)

**Files:**
- Create: `src/Koh.Core/Syntax/Lexer.cs`
- Test: `tests/Koh.Core.Tests/Syntax/LexerTests.cs`

- [x] **Step 1: Write failing tests**

```csharp
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Syntax;

public class LexerTests
{
    private static List<SyntaxToken> Lex(string source)
    {
        var text = SourceText.From(source);
        var lexer = new Lexer(text);
        var tokens = new List<SyntaxToken>();
        while (true)
        {
            var token = lexer.NextToken();
            tokens.Add(token);
            if (token.Kind == SyntaxKind.EndOfFileToken) break;
        }
        return tokens;
    }

    [Test]
    public async Task Lexer_Nop()
    {
        var tokens = Lex("nop");
        await Assert.That(tokens).HasCount().EqualTo(2); // NOP + EOF
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NopKeyword);
        await Assert.That(tokens[0].Text).IsEqualTo("nop");
    }

    [Test]
    public async Task Lexer_LdAB()
    {
        var tokens = Lex("ld a, b");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LdKeyword);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.AKeyword);
        await Assert.That(tokens[2].Kind).IsEqualTo(SyntaxKind.CommaToken);
        await Assert.That(tokens[3].Kind).IsEqualTo(SyntaxKind.BKeyword);
    }

    [Test]
    public async Task Lexer_WhitespaceTrivia()
    {
        var tokens = Lex("  nop");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NopKeyword);
        var leading = tokens[0].LeadingTrivia.ToList();
        await Assert.That(leading).HasCount().EqualTo(1);
        await Assert.That(leading[0].Kind).IsEqualTo(SyntaxKind.WhitespaceTrivia);
        await Assert.That(leading[0].Text).IsEqualTo("  ");
    }

    [Test]
    public async Task Lexer_LineComment()
    {
        var tokens = Lex("nop ; comment");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NopKeyword);
        var trailing = tokens[0].TrailingTrivia.ToList();
        // trailing trivia: space + comment
        await Assert.That(trailing.Any(t => t.Kind == SyntaxKind.LineCommentTrivia)).IsTrue();
    }

    [Test]
    public async Task Lexer_Number()
    {
        var tokens = Lex("$FF");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NumberLiteral);
        await Assert.That(tokens[0].Text).IsEqualTo("$FF");
    }

    [Test]
    public async Task Lexer_CaseInsensitive()
    {
        var tokens = Lex("NOP");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NopKeyword);
    }
}
```

- [x] **Step 2: Run tests to verify failure**

- [x] **Step 3: Implement Lexer**

The lexer reads from `SourceText`, producing `SyntaxToken` (red nodes directly for now — we'll refactor to produce green tokens and wrap later as complexity grows). It handles:

- Whitespace → `WhitespaceTrivia`
- `;` to end of line → `LineCommentTrivia`
- Keywords (case-insensitive): `nop`, `ld`, `add`, `a`, `b`, `c`, `d`, `e`, `h`, `l`, `hl`, `sp`, `af`, `bc`, `de`, `section`, `db`, `dw`, `ds`
- Numbers: `$hex`, `%binary`, `&octal`, decimal
- Identifiers: everything else that starts with letter/underscore
- Punctuation: `,`, `(`, `)`, `[`, `]`, `:`, `+`, `-`, `*`, `/`, etc.

Trivia attachment rule: leading trivia belongs to the next token, trailing trivia after a token on the same line belongs to that token. Newlines are trailing trivia of the preceding token.

Implementation: ~200-300 lines. Hand-written character-by-character scanner with `_position` cursor, `_start` for current token start, trivia accumulation lists.

Note: The lexer should produce green tokens internally (`GreenToken`) and the test helper wraps them. Adjust the `Lex` helper and `Lexer` to work with `GreenToken` + position tracking to produce `SyntaxToken` for tests.

- [x] **Step 4: Run tests**

```bash
dotnet test
```

Expected: PASS

- [x] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: starter lexer — keywords, numbers, trivia, punctuation"
```

### Task 1.7: Parser (Starter) + SyntaxTree

**Files:**
- Create: `src/Koh.Core/Syntax/Parser.cs`
- Create: `src/Koh.Core/Syntax/SyntaxTree.cs`
- Create: `src/Koh.Core/Diagnostics/Diagnostic.cs`
- Create: `src/Koh.Core/Diagnostics/DiagnosticBag.cs`
- Test: `tests/Koh.Core.Tests/Syntax/ParserTests.cs`

- [x] **Step 1: Write failing tests**

```csharp
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Syntax;

public class ParserTests
{
    [Test]
    public async Task Parser_Nop()
    {
        var tree = SyntaxTree.Parse("nop");
        var root = tree.Root;

        await Assert.That(root.Kind).IsEqualTo(SyntaxKind.CompilationUnit);
        var statements = root.ChildNodes().ToList();
        await Assert.That(statements).HasCount().EqualTo(1);
        await Assert.That(statements[0].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task Parser_LdAB()
    {
        var tree = SyntaxTree.Parse("ld a, b");
        var root = tree.Root;
        var stmt = root.ChildNodes().First();

        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.InstructionStatement);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LdKeyword);
    }

    [Test]
    public async Task Parser_NoDiagnostics_ForValidInput()
    {
        var tree = SyntaxTree.Parse("nop");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parser_ProducesTree_ForInvalidInput()
    {
        var tree = SyntaxTree.Parse("??? invalid");
        // Tree should still be produced
        await Assert.That(tree.Root).IsNotNull();
        // But should have diagnostics
        await Assert.That(tree.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task Parser_MultipleStatements()
    {
        var tree = SyntaxTree.Parse("nop\nnop");
        var statements = tree.Root.ChildNodes().ToList();
        await Assert.That(statements).HasCount().EqualTo(2);
    }
}
```

- [x] **Step 2: Run tests to verify failure**

- [x] **Step 3: Implement Diagnostic and DiagnosticBag**

```csharp
// Diagnostic.cs
using Koh.Core.Syntax;

namespace Koh.Core.Diagnostics;

public enum DiagnosticSeverity { Error, Warning, Info }

public sealed class Diagnostic
{
    public TextSpan Span { get; }
    public string Message { get; }
    public DiagnosticSeverity Severity { get; }

    public Diagnostic(TextSpan span, string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        Span = span;
        Message = message;
        Severity = severity;
    }

    public override string ToString() => $"{Severity}: {Message}";
}
```

```csharp
// DiagnosticBag.cs
using System.Collections;
using Koh.Core.Syntax;

namespace Koh.Core.Diagnostics;

public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = [];

    public void Report(TextSpan span, string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        _diagnostics.Add(new Diagnostic(span, message, severity));
    }

    public void ReportUnexpectedToken(TextSpan span, SyntaxKind actual, SyntaxKind expected)
    {
        Report(span, $"Unexpected token '{actual}', expected '{expected}'");
    }

    public void ReportBadCharacter(int position, char character)
    {
        Report(new TextSpan(position, 1), $"Bad character input: '{character}'");
    }

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public IReadOnlyList<Diagnostic> ToList() => _diagnostics;
}
```

- [x] **Step 4: Implement SyntaxTree**

```csharp
using Koh.Core.Diagnostics;
using Koh.Core.Text;

namespace Koh.Core.Syntax;

public sealed class SyntaxTree
{
    public SourceText Text { get; }
    public SyntaxNode Root { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    private SyntaxTree(SourceText text, SyntaxNode root, IReadOnlyList<Diagnostic> diagnostics)
    {
        Text = text;
        Root = root;
        Diagnostics = diagnostics;
    }

    public static SyntaxTree Parse(string text)
        => Parse(SourceText.From(text));

    public static SyntaxTree Parse(SourceText text)
    {
        var parser = new Parser(text);
        return parser.Parse();
    }

    internal static SyntaxTree Create(SourceText text, SyntaxNode root,
        IReadOnlyList<Diagnostic> diagnostics)
        => new(text, root, diagnostics);
}
```

- [x] **Step 5: Implement Parser (starter)**

Recursive descent parser. For Phase 1 it handles:
- `CompilationUnit` = list of statements + EOF
- `InstructionStatement` = keyword token + optional operand tokens (comma-separated)
- Error recovery: on unexpected token, wrap in `SkippedTokensTrivia` and advance

The parser consumes green tokens from the lexer, builds green nodes, then wraps the root as a red `SyntaxNode` for the `SyntaxTree`.

~150-200 lines for the starter.

- [x] **Step 6: Run tests**

```bash
dotnet test
```

Expected: PASS

- [x] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: starter parser + SyntaxTree — parse NOP and LD instructions"
```

---

## Phase 2: Core Instructions

**Milestone:** Parse any valid SM83 instruction with all addressing modes. Syntax errors for invalid operand combinations.

### Task 2.1: Complete SyntaxKind for All SM83 Instructions

**Files:**
- Modify: `src/Koh.Core/Syntax/SyntaxKind.cs`
- Test: `tests/Koh.Core.Tests/Syntax/SyntaxKindTests.cs`

- [x] **Step 1: Add all 46 SM83 instruction keywords to SyntaxKind**

Add: `AdcKeyword`, `SubKeyword`, `SbcKeyword`, `AndKeyword`, `OrKeyword`, `XorKeyword`, `CpKeyword`, `IncKeyword`, `DecKeyword`, `DaaKeyword`, `CplKeyword`, `RlcaKeyword`, `RlaKeyword`, `RrcaKeyword`, `RraKeyword`, `RlcKeyword`, `RlKeyword`, `RrcKeyword`, `RrKeyword`, `SlaKeyword`, `SraKeyword`, `SrlKeyword`, `SwapKeyword`, `BitKeyword`, `SetKeyword`, `ResKeyword`, `JpKeyword`, `JrKeyword`, `CallKeyword`, `RetKeyword`, `RetiKeyword`, `RstKeyword`, `PopKeyword`, `PushKeyword`, `DiKeyword`, `EiKeyword`, `HaltKeyword`, `StopKeyword`, `CcfKeyword`, `ScfKeyword`, `LdiKeyword`, `LddKeyword`, `LdhKeyword`.

Also add condition flag tokens: `ZKeyword`, `NzKeyword`, `NcKeyword`. Note: `CCondKeyword` was removed — `CKeyword` serves both register and condition flag, disambiguated contextually by the parser. `DlKeyword` was also removed (not in RGBASM spec). `CurrentAddressToken` added for standalone `$`.

- [x] **Step 2: Update lexer keyword table**

- [x] **Step 3: Write tests for each instruction category**

- [x] **Step 4: Run tests, commit**

### Task 2.2: Operand Parsing

**Files:**
- Modify: `src/Koh.Core/Syntax/Parser.cs`
- Create: `src/Koh.Core/Syntax/Nodes/OperandNodes.cs`
- Test: `tests/Koh.Core.Tests/Syntax/OperandParsingTests.cs`

- [x] **Step 1: Write failing tests for operand patterns**

Test cases:
- `ld a, b` — register to register
- `ld a, $FF` — immediate
- `ld a, [hl]` — indirect
- `ld [hl+], a` — indirect with increment
- `ld hl, sp+$05` — SP-relative
- `ldh a, [$FF00+c]` — high RAM
- `bit 3, a` — bit index
- `jr nz, .loop` — condition + relative target
- `rst $38` — restart vector

- [x] **Step 2: Implement operand node types**

`RegisterOperand`, `ImmediateOperand`, `IndirectOperand`, `ConditionOperand`, `BitIndexOperand`

- [x] **Step 3: Extend parser to handle operand patterns**

- [x] **Step 4: Run tests, commit**

### Task 2.3: Lexer Modes Infrastructure

**Files:**
- Modify: `src/Koh.Core/Syntax/Lexer.cs`
- Test: `tests/Koh.Core.Tests/Syntax/LexerModeTests.cs`

- [x] **Step 1: Write failing tests for string mode**

Test: `db "hello\nworld"` — lexer should handle escape sequences inside strings.
Note: String interpolation (`{symbol}`) deferred to Phase 8 — it's EQUS symbol expansion, a binding-phase concern.

- [x] **Step 2: Write failing tests for block comments**

Test: `/* comment */ nop` — lexer should produce `BlockCommentTrivia`.
Test: nested block comments (RGBDS supports them) — verified with depth tracking.

- [x] **Step 3: Implement string escape handling and block comments**

String escapes already handled by the lexer (skips `\x` sequences). Block comments with nesting via depth counter.
Unterminated block comments produce a diagnostic. Multi-line block comments in trailing trivia break the statement boundary.
Lexer now has its own `DiagnosticBag`; parser merges lexer diagnostics after lexing.

- [x] **Step 4: Implement block comment trivia**

- [x] **Step 5: Run tests, commit**

Note: Raw lexer mode (for macro arguments) and string interpolation deferred to Phase 8.

---

## Phase 3: Labels & Expressions

**Milestone:** Parse labels (global and local), evaluate expressions like `$FF00 + 3 * 2`, handle operator precedence correctly.

### Task 3.1: Label Parsing

**Files:**
- Modify: `src/Koh.Core/Syntax/SyntaxKind.cs`
- Modify: `src/Koh.Core/Syntax/Parser.cs`
- Test: `tests/Koh.Core.Tests/Syntax/LabelTests.cs`

- [x] **Step 1: Write failing tests**

```
main:          → global label
.loop:         → local label
main::         → exported global label
```

- [x] **Step 2: Implement label declaration node**

`LabelDeclaration` = `IdentifierToken` + `ColonToken` (+ optional second colon for export)

- [x] **Step 3: Run tests, commit**

### Task 3.2: Expression Parser (Pratt)

**Files:**
- Create: `src/Koh.Core/Syntax/ExpressionParser.cs` (or inline in `Parser.cs`)
- Test: `tests/Koh.Core.Tests/Syntax/ExpressionTests.cs`

- [x] **Step 1: Write failing tests for expression parsing**

```
1 + 2           → BinaryExpression(+)
1 + 2 * 3       → BinaryExpression(+, 1, BinaryExpression(*, 2, 3))
-1              → UnaryExpression(-, 1)
(1 + 2) * 3     → BinaryExpression(*, Paren(Binary(+)), 3)
HIGH($AABB)     → function call expression
BANK("Section") → function call expression
~$FF            → UnaryExpression(~)
1 << 3          → BinaryExpression(<<)
1 == 2          → BinaryExpression(==)
1 && 2          → BinaryExpression(&&)
```

- [x] **Step 2: Implement Pratt parser**

Precedence levels (matching RGBDS):
1. `||`
2. `&&`
3. `== !=`
4. `< > <= >=`
5. `| ^`
6. `&`
7. `<< >>`
8. `+ -`
9. `* / %`
10. Unary: `- ~ ! + HIGH LOW BANK SIZEOF STARTOF`

- [x] **Step 3: Run tests, commit**

### Task 3.3: Built-in Functions

**Files:**
- Modify: `src/Koh.Core/Syntax/SyntaxKind.cs`
- Modify: `src/Koh.Core/Syntax/Parser.cs`
- Test: `tests/Koh.Core.Tests/Syntax/BuiltinFunctionTests.cs`

- [x] **Step 1: Write failing tests**

`HIGH($AABB)`, `LOW($AABB)`, `BANK("name")`, `SIZEOF("name")`, `STARTOF("name")`, `DEF(symbol)`, `STRLEN("hello")`, `STRCAT("a", "b")`, `STRSUB("abc", 2, 1)`, etc.

- [x] **Step 2: Add function keywords to SyntaxKind and lexer**

- [x] **Step 3: Add function call expression parsing**

- [x] **Step 4: Run tests, commit**

---

## Phase 4: Sections & Data

**Milestone:** Parse `SECTION` directives with all modifiers, `DB`/`DW`/`DL`/`DS` data directives. Produce section data model with byte output for constant data.

### Task 4.1: SECTION Directive Parsing

**Files:**
- Modify: `src/Koh.Core/Syntax/Parser.cs`
- Create: `src/Koh.Core/Syntax/Nodes/SectionNode.cs`
- Test: `tests/Koh.Core.Tests/Syntax/SectionTests.cs`

- [x] **Step 1: Write failing tests**

```
SECTION "Main", ROM0
SECTION "Bank1", ROMX[$4000], BANK[1]
SECTION "RAM", WRAM0, ALIGN[8]
SECTION FRAGMENT "Frag", ROMX
SECTION UNION "Shared", WRAM0[$C100]
```

- [x] **Step 2: Add section type keywords to SyntaxKind** (`Rom0Keyword`, `RomxKeyword`, `Wram0Keyword`, `WramxKeyword`, `VramKeyword`, `HramKeyword`, `SramKeyword`, `OamKeyword`, `BankKeyword`, `AlignKeyword`, `FragmentKeyword`, `UnionKeyword`)

- [x] **Step 3: Implement section directive parsing**

- [x] **Step 4: Run tests, commit**

### Task 4.2: Data Directives

**Files:**
- Modify: `src/Koh.Core/Syntax/Parser.cs`
- Test: `tests/Koh.Core.Tests/Syntax/DataDirectiveTests.cs`

- [x] **Step 1: Write failing tests**

```
db $00, $01, $02
db "Hello", 0
dw $1234, label
dl $12345678
ds 10
ds 10, $FF
```

- [x] **Step 2: Implement data directive parsing**

`DataDirective` = `DbKeyword`/`DwKeyword`/`DsKeyword` + comma-separated expression list

- [x] **Step 3: Run tests, commit**

### Task 4.3: Symbol Definition Directives

**Files:**
- Modify: `src/Koh.Core/Syntax/Parser.cs`
- Test: `tests/Koh.Core.Tests/Syntax/SymbolDirectiveTests.cs`

- [x] **Step 1: Write failing tests**

```
MY_CONST EQU $10
MY_STR EQUS "hello"
REDEF MY_CONST EQU $20
DEF MY_VAR = 5
EXPORT my_label
PURGE MY_CONST
```

- [x] **Step 2: Add `EquKeyword`, `EqusKeyword`, `RedefKeyword`, `ExportKeyword`, `PurgeKeyword` to SyntaxKind**

- [x] **Step 3: Implement symbol directive parsing**

- [x] **Step 4: Run tests, commit**

---

## Phase 5: Binding & Emit

**Milestone:** Assemble a single-file program with labels, constants, instructions, and data to a `.kobj` file. All symbols resolved (except forward refs to link-time addresses).

### Task 5.1: Symbol Table

**Files:**
- Create: `src/Koh.Core/Symbols/Symbol.cs`
- Create: `src/Koh.Core/Symbols/SymbolTable.cs`
- Test: `tests/Koh.Core.Tests/Symbols/SymbolTableTests.cs`

- [x] **Step 1: Write failing tests**

- Define a label, look it up by name
- Define a local label, verify scoping to parent global label
- Define an EQU constant, verify value
- Attempt duplicate definition, get diagnostic

- [x] **Step 2: Implement Symbol hierarchy and SymbolTable**

- [x] **Step 3: Run tests, commit**

### Task 5.2: Binder — Section & Symbol Tracking

**Files:**
- Create: `src/Koh.Core/Binding/Binder.cs`
- Create: `src/Koh.Core/Binding/BinderState.cs`
- Test: `tests/Koh.Core.Tests/Binding/BinderSymbolTests.cs`

- [x] **Step 1: Write failing tests**

- Bind `MY_CONST EQU $10` → symbol with value 16
- Bind `main:` followed by `.loop:` → global + local label, local scoped to global
- Bind `SECTION "Main", ROM0` → section state tracking, subsequent data goes to this section
- Duplicate label definition → diagnostic

- [x] **Step 2: Implement BinderState**

Mutable state for the binding pass: current section, current global label (for local label scoping), PC tracking within section.

- [x] **Step 3: Implement Binder (symbol + section tracking only)**

Walk the syntax tree. On labels: create symbols. On EQU/EQUS: evaluate constant expressions, create symbols. On SECTION: open new section context. Does NOT yet encode instructions — just tracks structure.

- [x] **Step 4: Run tests, commit**

### Task 5.2b: Binder — Instruction Validation & Encoding

**Files:**
- Create: `src/Koh.Core/Encoding/Sm83InstructionTable.cs`
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Test: `tests/Koh.Core.Tests/Binding/InstructionBindingTests.cs`

- [x] **Step 1: Write failing tests**

- Bind `nop` in a section → bytes `[0x00]`, PC advances by 1
- Bind `ld a, b` → bytes `[0x78]`
- Bind `ld a, MY_CONST` where `MY_CONST EQU $10` → bytes `[0x3E, 0x10]` (resolved immediate)
- Bind `jp main` where `main` is a label → deferred expression (patch), PC advances by 3
- Bind `ld a, af` → diagnostic: invalid operand combination for LD
- Bind `add hl, a` → diagnostic: invalid operand combination for ADD

- [x] **Step 2: Implement instruction table as data**

Static table of `(SyntaxKind mnemonic, OperandPattern[] patterns)` → `(opcode bytes, size)`. The binder pattern-matches parsed instruction nodes against this table. Unmatched patterns produce diagnostics. This is the single source of truth for instruction validation AND encoding — no separate validation pass.

- [x] **Step 3: Wire instruction encoding into binder**

On instruction node: match against table → encode to bytes if operands are constant, create patch if deferred.

- [x] **Step 4: Run tests, commit**

### Task 5.2c: Binder — Data Directives & Patches

**Files:**
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Create: `src/Koh.Core/Binding/Patch.cs`
- Create: `src/Koh.Core/Binding/DeferredExpression.cs`
- Test: `tests/Koh.Core.Tests/Binding/DataBindingTests.cs`

- [x] **Step 1: Write failing tests**

- Bind `db $01, $02` → section data `[01 02]`
- Bind `dw $1234` → section data `[34 12]` (little-endian)
- Bind `dl $12345678` → section data `[78 56 34 12]`
- Bind `ds 5` → section data `[00 00 00 00 00]`
- Bind `ds 3, $FF` → section data `[FF FF FF]`
- Bind `dw main` where `main` is a label → patch with deferred expression

- [x] **Step 2: Implement Patch and DeferredExpression**

`Patch`: location in section data + expression tree + width (1/2/4 bytes).
`DeferredExpression`: tree-structured expression preserving source spans.

- [x] **Step 3: Wire data directive binding into binder**

- [x] **Step 4: Run tests, commit**

### Task 5.2d: Binder — EmitModel & Forward Reference Resolution

**Files:**
- Create: `src/Koh.Core/Binding/EmitModel.cs`
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Test: `tests/Koh.Core.Tests/Binding/EmitModelTests.cs`

- [ ] **Step 1: Write failing tests**

- Full binding produces `EmitModel` with sections, symbols, and diagnostics
- Forward reference: `jp .end` then `.end:` later → deferred expression correctly references the label
- Undefined symbol → diagnostic: undefined symbol 'foo'
- EXPORT directive → symbol visibility set to exported

- [ ] **Step 2: Implement EmitModel**

```csharp
public record EmitModel(
    IReadOnlyList<SectionData> Sections,
    IReadOnlyList<SymbolData> Symbols,
    IReadOnlyList<Diagnostic> Diagnostics);

public record SectionData(
    string Name, SectionType Type, SectionConstraints Constraints,
    byte[] Data, IReadOnlyList<Patch> Patches,
    IReadOnlyList<SourceMapping> SourceMap);

public record SymbolData(
    string Name, SymbolKind Kind, Visibility Visibility,
    int? SectionIndex, int? Value, DeferredExpression? DeferredValue);

public record SourceMapping(int ByteOffset, int Length, TextSpan SourceSpan, string FilePath);
```

- [ ] **Step 3: Implement forward reference resolution** — two-pass: first pass collects all symbol definitions, second pass resolves references. Unresolved references to labels become deferred expressions (for link-time).

- [ ] **Step 4: Run tests, commit**

### Task 5.3: SemanticModel & Compilation API

**Files:**
- Create: `src/Koh.Core/Compilation.cs`
- Create: `src/Koh.Core/SemanticModel.cs`
- Test: `tests/Koh.Core.Tests/SemanticModelTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
[Test]
public async Task Compilation_Create_ParsesAndBinds()
{
    var tree = SyntaxTree.Parse("MY_CONST EQU $10\nmain: nop");
    var compilation = Compilation.Create(tree);
    var model = compilation.GetSemanticModel(tree);

    await Assert.That(compilation.Diagnostics).IsEmpty();
}

[Test]
public async Task SemanticModel_GetDeclaredSymbol()
{
    var tree = SyntaxTree.Parse("main: nop");
    var compilation = Compilation.Create(tree);
    var model = compilation.GetSemanticModel(tree);
    var label = tree.Root.ChildNodes().First(); // LabelDeclaration
    var symbol = model.GetDeclaredSymbol(label);
    await Assert.That(symbol).IsNotNull();
    await Assert.That(symbol!.Name).IsEqualTo("main");
}

[Test]
public async Task SemanticModel_GetSymbol_Reference()
{
    var tree = SyntaxTree.Parse("MY_CONST EQU $10\nld a, MY_CONST");
    var compilation = Compilation.Create(tree);
    var model = compilation.GetSemanticModel(tree);
    // Find the MY_CONST reference in the ld instruction
    // model.GetSymbol(referenceNode) → returns the ConstantSymbol
}

[Test]
public async Task SemanticModel_LookupSymbols()
{
    var tree = SyntaxTree.Parse("main: nop\n.loop: nop");
    var compilation = Compilation.Create(tree);
    var model = compilation.GetSemanticModel(tree);
    var symbols = model.LookupSymbols(0);
    await Assert.That(symbols.Any(s => s.Name == "main")).IsTrue();
}

[Test]
public async Task Compilation_AddSyntaxTrees()
{
    var tree1 = SyntaxTree.Parse("main: nop");
    var compilation = Compilation.Create(tree1);
    var tree2 = SyntaxTree.Parse("other: nop");
    var newCompilation = compilation.AddSyntaxTrees(tree2);
    // Original compilation unchanged
    await Assert.That(compilation).IsNotEqualTo(newCompilation);
}

[Test]
public async Task Compilation_ReplaceSyntaxTree()
{
    var tree1 = SyntaxTree.Parse("main: nop");
    var compilation = Compilation.Create(tree1);
    var tree2 = SyntaxTree.Parse("main: halt");
    var newCompilation = compilation.ReplaceSyntaxTree(tree1, tree2);
    await Assert.That(newCompilation).IsNotEqualTo(compilation);
}
```

- [ ] **Step 2: Implement Compilation**

Immutable. Holds syntax trees, runs binder lazily, caches results. `AddSyntaxTrees()` and `ReplaceSyntaxTree()` return new instances. `GetSemanticModel(tree)` returns per-file view. `Emit()` produces `EmitModel`.

- [ ] **Step 3: Implement SemanticModel**

Per-file view into compilation. `GetSymbol(node)` → resolve a reference node to its symbol. `GetDeclaredSymbol(node)` → get the symbol declared by a label/EQU node. `LookupSymbols(position)` → all symbols visible at a position. `GetDiagnostics()` → diagnostics for this file.

- [ ] **Step 4: Run tests, commit**

### Task 5.4: SM83 Instruction Encoder

**Files:**
- Create: `src/Koh.Core/Encoding/Sm83Encoder.cs`
- Test: `tests/Koh.Core.Tests/Encoding/Sm83EncoderTests.cs`

- [ ] **Step 1: Write failing tests**

```
nop        → [0x00]
ld a, b    → [0x78]
ld a, $FF  → [0x3E, 0xFF]
ld hl, $1234 → [0x21, 0x34, 0x12]
add a, [hl]  → [0x86]
jr $05     → [0x18, 0x05]
rst $38    → [0xFF]
bit 3, a   → [0xCB, 0x5F]
```

- [ ] **Step 2: Implement encoder as data-driven table**

Map each valid `(mnemonic, operand pattern)` to opcode byte(s) with encoding rules. The table itself is ~300 entries.

- [ ] **Step 3: Run tests, commit**

### Task 5.5: Koh Object Format Writer

**Files:**
- Create: `src/Koh.Emit/Koh.Emit.csproj`
- Create: `src/Koh.Emit/KobjWriter.cs`
- Create: `src/Koh.Emit/KobjReader.cs`
- Create: `tests/Koh.Emit.Tests/Koh.Emit.Tests.csproj`
- Test: `tests/Koh.Emit.Tests/KobjRoundtripTests.cs`

- [ ] **Step 1: Create Koh.Emit project**

```bash
dotnet new classlib -n Koh.Emit -o src/Koh.Emit -f net10.0
dotnet sln add src/Koh.Emit/Koh.Emit.csproj
dotnet add src/Koh.Emit reference src/Koh.Core
dotnet new classlib -n Koh.Emit.Tests -o tests/Koh.Emit.Tests -f net10.0
dotnet sln add tests/Koh.Emit.Tests/Koh.Emit.Tests.csproj
dotnet add tests/Koh.Emit.Tests reference src/Koh.Emit
dotnet add tests/Koh.Emit.Tests package TUnit
```

- [ ] **Step 2: Write failing roundtrip test**

Create an `EmitModel` with a section, symbols, and patches. Write to `.kobj`, read back, verify all data matches.

- [ ] **Step 3: Implement KobjWriter and KobjReader**

Binary format: magic (`KOH\0`), version (1), then sections for symbols, sections, expressions, diagnostics. Use `BinaryWriter`/`BinaryReader`.

- [ ] **Step 4: Run tests, commit**

### Task 5.6: Integration Test — Single File Assembly

**Files:**
- Test: `tests/Koh.Core.Tests/Integration/SingleFileAssemblyTests.cs`

- [ ] **Step 1: Write integration test**

```csharp
[Test]
public async Task Assemble_HelloWorld()
{
    var source = """
        SECTION "Main", ROM0[$0100]
        main:
            nop
            jp main
        SECTION "Data", ROM0
        data:
            db $01, $02, $03
        """;

    var tree = SyntaxTree.Parse(source);
    await Assert.That(tree.Diagnostics).IsEmpty();

    var compilation = Compilation.Create(tree);
    var emitModel = compilation.Emit();

    await Assert.That(emitModel.Diagnostics).IsEmpty();
    await Assert.That(emitModel.Sections).HasCount().EqualTo(2);

    // "Main" section: nop (00) + jp (C3 XX XX)
    var mainSection = emitModel.Sections[0];
    await Assert.That(mainSection.Data[0]).IsEqualTo((byte)0x00); // nop
    await Assert.That(mainSection.Data[1]).IsEqualTo((byte)0xC3); // jp
    await Assert.That(mainSection.Patches).HasCount().EqualTo(1); // jp target is deferred

    // "Data" section: 01 02 03
    var dataSection = emitModel.Sections[1];
    await Assert.That(dataSection.Data).IsEquivalentTo(new byte[] { 0x01, 0x02, 0x03 });
}
```

- [ ] **Step 2: Implement Compilation.Create() and .Emit()**

Wire parser → binder → emit model pipeline.

- [ ] **Step 3: Run tests, commit**

---

## Phase 6: CLI Assembler

**Milestone:** Run `koh-asm input.asm -o output.kobj` from the command line.

### Task 6.1: CLI Project

**Files:**
- Create: `src/Koh.Asm/Koh.Asm.csproj`
- Create: `src/Koh.Asm/Program.cs`

- [ ] **Step 1: Create console project**

```bash
dotnet new console -n Koh.Asm -o src/Koh.Asm -f net10.0
dotnet sln add src/Koh.Asm/Koh.Asm.csproj
dotnet add src/Koh.Asm reference src/Koh.Core
dotnet add src/Koh.Asm reference src/Koh.Emit
```

- [ ] **Step 2: Implement Program.cs**

Parse CLI args (input file, `-o` output path, `--format` kobj/rgbds). Read source file → parse → compile → emit → write object file. Report diagnostics to stderr with file:line:column format.

- [ ] **Step 3: Write automated CLI integration tests**

Test: `tests/Koh.Core.Tests/Integration/CliTests.cs` — run the assembler as a process, verify:
- Exit code 0 on valid input, non-zero on errors
- Output `.kobj` file is created
- Diagnostics printed to stderr with `file:line:column:` format
- `--format rgbds` flag accepted (output deferred to Phase 9)

- [ ] **Step 4: Manual test**

Create a test `.asm` file, run `dotnet run --project src/Koh.Asm -- test.asm -o test.kobj`, verify output.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: CLI assembler — koh-asm"
```

---

## Phase 7: Linker

**Milestone:** Run `koh-link a.kobj b.kobj -o game.gb` and get a working ROM.

### Task 7.1: Linker Core Project

**Files:**
- Create: `src/Koh.Linker.Core/Koh.Linker.Core.csproj`
- Create: `tests/Koh.Linker.Tests/Koh.Linker.Tests.csproj`

- [ ] **Step 1: Create projects and wire references**

### Task 7.2: Object File Loading & Symbol Resolution

**Files:**
- Create: `src/Koh.Linker.Core/LinkerCompilation.cs`
- Create: `src/Koh.Linker.Core/SymbolResolver.cs`
- Test: `tests/Koh.Linker.Tests/SymbolResolutionTests.cs`

- [ ] **Step 1: Write failing tests**

- Two objects: one exports `main`, other imports `main` → resolved
- Two objects both export `main` → diagnostic: duplicate export
- Import with no matching export → diagnostic: unresolved symbol

- [ ] **Step 2: Implement symbol resolution**

- [ ] **Step 3: Run tests, commit**

### Task 7.3: Constraint-Based Section Placement

**Files:**
- Create: `src/Koh.Linker.Core/SectionPlacer.cs`
- Create: `src/Koh.Linker.Core/Constraint.cs`
- Test: `tests/Koh.Linker.Tests/SectionPlacementTests.cs`

- [ ] **Step 1: Write failing tests**

- Section with fixed address → placed at that address
- Section with fixed bank → placed in that bank
- Two sections fitting in one bank → both placed
- Two sections exceeding bank capacity → diagnostic explaining which sections conflict
- Alignment constraint → placed at aligned address
- Fragment constraint → co-located in same bank

- [ ] **Step 2: Implement backtracking constraint solver**

The solver:
1. Collects all constraints from all sections
2. Sorts sections by constraint strictness (fixed address first, then fixed bank, then alignment, then unconstrained)
3. Tries to place each section, propagating constraints
4. On conflict, backtracks and tries alternative placements
5. On failure, identifies minimal conflicting constraint set for diagnostics

- [ ] **Step 3: Run tests, commit**

### Task 7.4: Expression Evaluation & Patch Application

**Files:**
- Create: `src/Koh.Linker.Core/ExpressionEvaluator.cs`
- Test: `tests/Koh.Linker.Tests/ExpressionEvalTests.cs`

- [ ] **Step 1: Write failing tests**

- Simple label reference → resolved to absolute address
- `label + 3` → address + 3
- `BANK("section")` → resolved to bank number
- `SIZEOF("section")` → resolved to section size
- `HIGH(label)` → high byte of address
- Division by zero → diagnostic with source span
- ASSERT failure → diagnostic with assert message

- [ ] **Step 2: Implement expression tree evaluator**

Walk the expression tree with all addresses now known. On error, report using the source span carried in the expression node.

- [ ] **Step 3: Run tests, commit**

### Task 7.5: ROM Output

**Files:**
- Create: `src/Koh.Linker.Core/RomWriter.cs`
- Create: `src/Koh.Linker.Core/SymFileWriter.cs`
- Create: `src/Koh.Linker.Core/MapFileWriter.cs`
- Create: `src/Koh.Linker.Core/HeaderFixer.cs`
- Test: `tests/Koh.Linker.Tests/RomOutputTests.cs`

- [ ] **Step 1: Write failing tests**

- Link a single-section ROM0 program → correct `.gb` bytes
- Verify header checksum at $014D
- Verify global checksum at $014E-014F
- Verify `.sym` file format: `bank:addr symbolname`
- Verify `.map` file shows bank usage

- [ ] **Step 2: Implement ROM writer with header fixup**

- [ ] **Step 3: Implement .sym and .map file writers**

- [ ] **Step 4: Run tests, commit**

### Task 7.6: CLI Linker

**Files:**
- Create: `src/Koh.Link/Koh.Link.csproj`
- Create: `src/Koh.Link/Program.cs`

- [ ] **Step 1: Create console project, wire references**

- [ ] **Step 2: Implement CLI** — parse args (input .kobj files, `-o` output ROM, `-n` sym file, `-m` map file)

- [ ] **Step 3: Manual end-to-end test** — assemble a hello world, link it, run in emulator

- [ ] **Step 4: Commit**

### Task 7.7: End-to-End Integration Test

**Files:**
- Create: `tests/Koh.Compat.Tests/Koh.Compat.Tests.csproj`
- Test: `tests/Koh.Compat.Tests/EndToEndTests.cs`

- [ ] **Step 1: Write test that assembles + links a minimal ROM**

A complete Game Boy ROM with proper header, entry point at $0100, interrupt vectors, and a simple loop. Verify the output is a valid GB ROM (correct header checksum, correct size).

- [ ] **Step 2: Run tests, commit**

---

## Phase 8: Macros & Directives

**Milestone:** Full RGBDS directive coverage. Can assemble real-world RGBDS projects.

### Task 8.1: Macro Definition & Expansion

**Files:**
- Modify: `src/Koh.Core/Syntax/Parser.cs`
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Test: `tests/Koh.Core.Tests/Binding/MacroTests.cs`

- [ ] **Step 1: Write failing tests**

```asm
my_macro: MACRO
    ld a, \1
    add a, \2
ENDM
    my_macro b, c
```

Expected: expands to `ld a, b` + `add a, c`.

Test also: nested macros, `SHIFT`, `\@` unique suffix, `_NARG`, recursion depth limit.

- [ ] **Step 2: Implement macro parsing (MACRO/ENDM)**

- [ ] **Step 3: Implement macro expansion in binder**

- [ ] **Step 4: Run tests, commit**

### Task 8.2: REPT/FOR Loops

**Files:**
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Test: `tests/Koh.Core.Tests/Binding/RepeatTests.cs`

- [ ] **Step 1: Write failing tests**

```asm
REPT 3
    nop
ENDR
; → 3 nops

FOR I, 0, 4
    db I
ENDR
; → db 0, db 1, db 2, db 3
```

- [ ] **Step 2: Implement REPT/FOR expansion in binder**

- [ ] **Step 3: Run tests, commit**

### Task 8.3: Conditional Assembly

**Files:**
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Test: `tests/Koh.Core.Tests/Binding/ConditionalTests.cs`

- [ ] **Step 1: Write failing tests**

```asm
MY_FLAG EQU 1
IF MY_FLAG
    nop
ELIF MY_FLAG == 2
    halt
ELSE
    stop
ENDC
```

- [ ] **Step 2: Implement IF/ELIF/ELSE/ENDC in binder**

- [ ] **Step 3: Run tests, commit**

### Task 8.4: Character Maps

**Files:**
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Create: `src/Koh.Core/Binding/CharMap.cs`
- Test: `tests/Koh.Core.Tests/Binding/CharMapTests.cs`

- [ ] **Step 1: Write failing tests**

```asm
NEWCHARMAP ascii
CHARMAP "A", $41
CHARMAP "B", $42
db "AB" ; → [0x41, 0x42]
```

- [ ] **Step 2: Implement charmap tracking and string encoding**

- [ ] **Step 3: Run tests, commit**

### Task 8.5: INCLUDE / INCBIN

**Files:**
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Create: `src/Koh.Core/SourceFileResolver.cs`
- Test: `tests/Koh.Core.Tests/Binding/IncludeTests.cs`

- [ ] **Step 1: Write failing tests**

Using in-memory `SourceFileResolver` for testability.

- [ ] **Step 2: Implement INCLUDE (parse + bind included file) and INCBIN (embed binary data)**

- [ ] **Step 3: Run tests, commit**

### Task 8.6: UNION / LOAD Sections

**Files:**
- Modify: `src/Koh.Core/Syntax/Parser.cs`
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Test: `tests/Koh.Core.Tests/Binding/UnionLoadTests.cs`

- [ ] **Step 1: Write failing tests for UNION/NEXTU/ENDU**

```asm
SECTION UNION "Shared", WRAM0
first_var: db
NEXTU
second_var: db
ENDU
```
Verify: both variables share the same address. Section size = max of all union members.

- [ ] **Step 2: Write failing tests for LOAD/ENDL**

```asm
SECTION "ROM", ROM0
LOAD "RAM", WRAM0
    my_var: db
ENDL
```
Verify: `my_var` gets a WRAM0 address but the data is placed in ROM.

- [ ] **Step 3: Implement UNION and LOAD semantics in binder**

- [ ] **Step 4: Run tests, commit**

### Task 8.7: Stack Directives & RS Counters

**Files:**
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Test: `tests/Koh.Core.Tests/Binding/StackDirectiveTests.cs`

- [ ] **Step 1: Write failing tests**

```asm
; Section stack
SECTION "A", ROM0
PUSHS
SECTION "B", ROMX
; ... code in B ...
POPS
; back in section A

; RS counters
RSRESET
x_pos RB 1
y_pos RB 1
name  RB 10
name_end
; x_pos=0, y_pos=1, name=2, name_end=12

; Option stack
PUSHO
OPT b.X
; ...
POPO
```

- [ ] **Step 2: Implement PUSHS/POPS, PUSHC/POPC, PUSHO/POPO as binder state stacks**

- [ ] **Step 3: Implement RB, RW, RSRESET, RSSET**

- [ ] **Step 4: Run tests, commit**

### Task 8.8: Control Directives & String Interpolation

**Files:**
- Modify: `src/Koh.Core/Binding/Binder.cs`
- Modify: `src/Koh.Core/Syntax/Lexer.cs`
- Test: `tests/Koh.Core.Tests/Binding/ControlDirectiveTests.cs`

- [ ] **Step 1: Write failing tests for ASSERT/WARN/FAIL/PRINT**

```asm
ASSERT 1 + 1 == 2, "math is broken"
STATIC_ASSERT 2 > 1
WARN "This is a warning"
PRINT "Value: "
PRINTLN "done"
FATAL "stop here"
```

- [ ] **Step 2: Write failing tests for string interpolation**

```asm
X EQU 5
PRINTLN "X is {d:X}"  ; prints "X is 5"
MY_STR EQUS "hello"
PRINTLN "{MY_STR} world"
```

- [ ] **Step 3: Implement control directives in binder**

ASSERT/STATIC_ASSERT: evaluate expression, report diagnostic on failure. STATIC_ASSERT runs at assembly time, ASSERT deferred to link time if expression contains link-time values.

- [ ] **Step 4: Implement string interpolation** — the lexer already produces interpolation tokens (Task 2.3), the binder resolves symbol references and substitutes values.

- [ ] **Step 5: Implement OPT directive**

- [ ] **Step 6: Implement ALIGN (inline, within a section)**

- [ ] **Step 7: Run tests, commit**

### Task 8.9: Raw Lexer Mode for Macro Arguments

**Files:**
- Modify: `src/Koh.Core/Syntax/Lexer.cs`
- Test: `tests/Koh.Core.Tests/Syntax/RawModeTests.cs`

- [ ] **Step 1: Write failing tests**

```asm
my_macro: MACRO
    db \1
ENDM
    my_macro $01 + $02  ; entire "$01 + $02" passed as one argument
    my_macro "hello, world"  ; comma inside string is not an arg separator
```

- [ ] **Step 2: Implement Raw lexer mode**

When the lexer detects a macro invocation (identifier at line start followed by arguments), switch to Raw mode for the rest of the line. In Raw mode, tokenize arguments as string tokens separated by commas (but commas inside strings/parens don't split).

- [ ] **Step 3: Run tests, commit**

### Task 8.10: Real-World Compatibility Test

**Files:**
- Test: `tests/Koh.Compat.Tests/RealWorldTests.cs`

- [ ] **Step 1: Assemble a non-trivial open-source GB homebrew project with Koh**

Find an open-source GB project using RGBDS syntax. Assemble with both RGBDS and Koh. Compare output byte-for-byte.

- [ ] **Step 2: Fix any differences found**

- [ ] **Step 3: Commit**

---

## Phase 9: RGBDS Compatibility

**Milestone:** `--format rgbds` produces `.o` files that `rgblink` can link successfully.

### Task 9.1: RGBDS Object Format Writer

**Files:**
- Create: `src/Koh.Emit/RgbdsObjectWriter.cs`
- Test: `tests/Koh.Emit.Tests/RgbdsFormatTests.cs`

- [ ] **Step 1: Write failing tests**

Create an EmitModel, write as RGBDS `.o`, read with a reference reader (parse the binary), verify structure matches RGB9 rev 13 spec.

- [ ] **Step 2: Implement RGB9 writer**

- Expression tree → RPN flattening
- Symbol table → RGBDS symbol format
- Section → RGBDS section format with patches
- File stack nodes for backtraces

- [ ] **Step 3: Run tests, commit**

### Task 9.2: Cross-Tool Integration Test

**Files:**
- Test: `tests/Koh.Compat.Tests/RgbdsInteropTests.cs`

- [ ] **Step 1: Write test that assembles with Koh, links with rgblink**

Requires `rgblink` available on PATH. Skip if not available (`[ConditionalTest]`).

- [ ] **Step 2: Write test that mixes Koh .o and rgbasm .o files**

Assemble one file with Koh (`--format rgbds`), another with rgbasm, link both with rgblink.

- [ ] **Step 3: Commit**

---

## Phase 10: LSP Server

**Milestone:** VS Code extension with real-time diagnostics, hover, go-to-definition, completion.

### Task 10.1: LSP Server Project

**Files:**
- Create: `src/Koh.Lsp/Koh.Lsp.csproj`
- Create: `src/Koh.Lsp/Program.cs`
- Create: `src/Koh.Lsp/KohLanguageServer.cs`
- Create: `src/Koh.Lsp/Workspace.cs`

- [ ] **Step 1: Create project, add LSP protocol dependency**

- [ ] **Step 2: Implement basic server lifecycle** — initialize, initialized, shutdown, exit

- [ ] **Step 3: Implement Workspace** — holds `Compilation`, updates on `textDocument/didOpen`, `didChange`, `didClose`

- [ ] **Step 4: Commit**

### Task 10.2: Diagnostics

**Files:**
- Modify: `src/Koh.Lsp/KohLanguageServer.cs`
- Create: `tests/Koh.Lsp.Tests/Koh.Lsp.Tests.csproj`
- Test: `tests/Koh.Lsp.Tests/DiagnosticsTests.cs`

- [ ] **Step 1: Create LSP test project**

```bash
dotnet new classlib -n Koh.Lsp.Tests -o tests/Koh.Lsp.Tests -f net10.0
dotnet sln add tests/Koh.Lsp.Tests/Koh.Lsp.Tests.csproj
dotnet add tests/Koh.Lsp.Tests reference src/Koh.Lsp
dotnet add tests/Koh.Lsp.Tests package TUnit
```

- [ ] **Step 2: Write failing test** — send `textDocument/didOpen` with invalid source, verify `textDocument/publishDiagnostics` is received with correct file/line/column.

- [ ] **Step 3: On file change → reparse → publish diagnostics**

Convert `Koh.Core.Diagnostics.Diagnostic` to LSP `Diagnostic` with file/line/column mapping.

- [ ] **Step 4: Run tests, commit**

### Task 10.3: Hover

**Files:**
- Create: `src/Koh.Lsp/Handlers/HoverHandler.cs`

- [ ] **Step 1: Implement textDocument/hover**

Use `SemanticModel.GetSymbol(position)` → return symbol info, value in all bases, instruction details (bytes, cycles, flags).

- [ ] **Step 2: Commit**

### Task 10.4: Go-to-Definition & References

**Files:**
- Create: `src/Koh.Lsp/Handlers/DefinitionHandler.cs`
- Create: `src/Koh.Lsp/Handlers/ReferencesHandler.cs`

- [ ] **Step 1: Implement textDocument/definition** — `SemanticModel.GetDeclaredSymbol()` → return source location

- [ ] **Step 2: Implement textDocument/references** — find all references to a symbol across all files

- [ ] **Step 3: Commit**

### Task 10.5: Completion

**Files:**
- Create: `src/Koh.Lsp/Handlers/CompletionHandler.cs`

- [ ] **Step 1: Implement textDocument/completion**

Context-aware: instructions at line start, registers after instruction, symbols in expressions, directives.

- [ ] **Step 2: Commit**

### Task 10.6: Remaining LSP Features

- [ ] **Step 1: Rename** — `textDocument/rename` using semantic symbol rename
- [ ] **Step 2: Semantic tokens** — `textDocument/semanticTokens/full` from syntax tree
- [ ] **Step 3: Inlay hints** — constant values, macro parameters
- [ ] **Step 4: Signature help** — macro parameter positions
- [ ] **Step 5: Document symbols** — labels, constants, macros, sections
- [ ] **Step 6: Commit**

### Task 10.7: VS Code Extension

**Files:**
- Create: `editors/vscode/package.json`
- Create: `editors/vscode/src/extension.ts`

- [ ] **Step 1: Create VS Code extension** that launches the Koh LSP server
- [ ] **Step 2: Configure language contribution points** (`.asm`, `.inc` file associations)
- [ ] **Step 3: Test locally with VS Code**
- [ ] **Step 4: Commit**

---

## Phase 11: Polish

**Milestone:** Production-quality tool with incremental reparsing, parallel parsing, AOT builds, and excellent diagnostics.

### Task 11.1: Incremental Reparsing

- [ ] **Step 1: Implement green node reuse** — on `SourceText.WithChanges()`, identify unchanged regions, reuse green subtrees
- [ ] **Step 2: Benchmark** — measure reparse time on edit vs full parse
- [ ] **Step 3: Commit**

### Task 11.2: Parallel Parsing

- [ ] **Step 1: Parse multiple files concurrently** in `Compilation.Create()`
- [ ] **Step 2: Benchmark** — measure indexing time on the large benchmark project
- [ ] **Step 3: Commit**

### Task 11.3: Native AOT

- [ ] **Step 1: Add `<PublishAot>true</PublishAot>` to CLI projects**
- [ ] **Step 2: Fix any AOT warnings** (trimming, reflection)
- [ ] **Step 3: Verify single-binary distribution works**
- [ ] **Step 4: Benchmark cold start time**
- [ ] **Step 5: Commit**

### Task 11.4: Constraint Solver Diagnostics

- [ ] **Step 1: Write failing tests for conflict reporting** — test that when placement fails, the diagnostic includes the specific conflicting sections and constraints
- [ ] **Step 2: Implement minimal conflict set identification** — when placement fails, find the smallest set of constraints that are mutually unsatisfiable
- [ ] **Step 3: Generate human-readable error messages** — "Sections 'A' (1234 bytes), 'B' (5678 bytes), and 'C' (9012 bytes) are all constrained to ROMX bank 1, but together they exceed the 16384-byte bank capacity by 532 bytes"
- [ ] **Step 4: Run tests, commit**

### Task 11.5: Performance Benchmark Suite

- [ ] **Step 1: Create benchmark project** using BenchmarkDotNet
- [ ] **Step 2: Add benchmarks**: lex single file, parse single file, full compilation, link, incremental reparse
- [ ] **Step 3: Establish baseline numbers**
- [ ] **Step 4: Commit**
