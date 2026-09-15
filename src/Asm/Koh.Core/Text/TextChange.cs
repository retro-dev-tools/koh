using Koh.Core.Syntax;

namespace Koh.Core.Text;

public readonly record struct TextChange(TextSpan Span, string NewText);
