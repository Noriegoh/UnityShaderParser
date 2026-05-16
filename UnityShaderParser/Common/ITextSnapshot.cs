using System.Collections.Generic;

namespace UnityShaderParser.Common
{
    /// <summary>
    /// Describes a single contiguous text change in a document.
    /// Represents the transition from old text → new text at a given position.
    /// </summary>
    public readonly struct TextChange
    {
        /// <summary>Character offset where the change starts (in the OLD text).</summary>
        public int OldStart { get; }

        /// <summary>Number of characters removed from the old text.</summary>
        public int OldLength { get; }

        /// <summary>The new text inserted at OldStart.</summary>
        public string NewText { get; }

        /// <summary>Shorthand: net character delta (NewText.Length - OldLength).</summary>
        public int Delta => (NewText?.Length ?? 0) - OldLength;

        /// <summary>End offset of the removed range in the old text.</summary>
        public int OldEnd => OldStart + OldLength;

        /// <summary>Length of the newly inserted text.</summary>
        public int NewLength => NewText?.Length ?? 0;

        public TextChange(int oldStart, int oldLength, string newText)
        {
            OldStart = oldStart;
            OldLength = oldLength;
            NewText = newText ?? string.Empty;
        }

        public override string ToString() => $"TextChange(at={OldStart}, del={OldLength}, ins=\"{NewText}\", Δ={Delta})";
    }

    /// <summary>
    /// Read-only view of a document's text at a point in time.
    /// Implemented by the host editor (e.g., wrapping Incode's PieceTree).
    /// 
    /// Designed for append-only buffer structures — avoids materializing
    /// the entire document text unless explicitly requested.
    /// </summary>
    public interface ITextSnapshot
    {
        /// <summary>Total length of the document in characters.</summary>
        int Length { get; }

        /// <summary>Total number of lines in the document.</summary>
        int LineCount { get; }

        /// <summary>A monotonically increasing version number.</summary>
        int Version { get; }

        /// <summary>Get a substring of the document text.</summary>
        string GetText(int offset, int length);

        /// <summary>Get the full text of line at the given 0-based index (including line ending).</summary>
        string GetLine(int lineIndex);

        /// <summary>Get the text of a line without its line ending.</summary>
        string GetLineContent(int lineIndex);

        /// <summary>
        /// Materializes the full document text. Avoid in hot paths — prefer
        /// GetText(offset, length) for incremental operations.
        /// </summary>
        string GetFullText();
    }

    /// <summary>
    /// Simple string-backed implementation of <see cref="ITextSnapshot"/>
    /// for testing and standalone use (when no PieceTree is available).
    /// </summary>
    public sealed class StringTextSnapshot : ITextSnapshot
    {
        private readonly string _text;
        private readonly int _version;
        private int[] _lineStarts; // Lazily computed

        public StringTextSnapshot(string text, int version = 0)
        {
            _text = text ?? string.Empty;
            _version = version;
        }

        public int Length => _text.Length;
        public int LineCount => EnsureLineStarts().Length;
        public int Version => _version;

        public string GetText(int offset, int length) => _text.Substring(offset, length);

        public string GetLine(int lineIndex)
        {
            var starts = EnsureLineStarts();
            if (lineIndex < 0 || lineIndex >= starts.Length)
                throw new System.ArgumentOutOfRangeException(nameof(lineIndex));

            int start = starts[lineIndex];
            int end = lineIndex + 1 < starts.Length ? starts[lineIndex + 1] : _text.Length;
            return _text.Substring(start, end - start);
        }

        public string GetLineContent(int lineIndex)
        {
            string line = GetLine(lineIndex);
            if (line.Length > 0)
            {
                char last = line[line.Length - 1];
                if (line.Length > 1 && last == '\n' && line[line.Length - 2] == '\r')
                    return line.Substring(0, line.Length - 2);
                if (last == '\n' || last == '\r')
                    return line.Substring(0, line.Length - 1);
            }
            return line;
        }

        public string GetFullText() => _text;

        /// <summary>
        /// Lazily computes line start offsets. Each entry is the character offset
        /// of the first character on that line.
        /// </summary>
        private int[] EnsureLineStarts()
        {
            if (_lineStarts != null)
                return _lineStarts;

            var starts = new List<int> { 0 };
            for (int i = 0; i < _text.Length; i++)
            {
                char c = _text[i];
                if (c == '\n')
                {
                    starts.Add(i + 1);
                }
                else if (c == '\r')
                {
                    if (i + 1 < _text.Length && _text[i + 1] == '\n')
                    {
                        starts.Add(i + 2);
                        i++; // Skip the \n in \r\n
                    }
                    else
                    {
                        starts.Add(i + 1);
                    }
                }
            }

            _lineStarts = starts.ToArray();
            return _lineStarts;
        }
    }

    /// <summary>
    /// Callback interface for consumers who want fine-grained AST change notifications.
    /// The host editor implements this to receive incremental update events.
    /// </summary>
    public interface IIncrementalParseCallback
    {
        /// <summary>Called when top-level declarations are removed from the AST.</summary>
        void OnDeclarationsRemoved(int startIndex, int count);

        /// <summary>Called when new top-level declarations are inserted into the AST.</summary>
        void OnDeclarationsInserted(int startIndex, IReadOnlyList<HLSL.HLSLSyntaxNode> newDecls);

        /// <summary>Called when the diagnostics list has been refreshed.</summary>
        void OnDiagnosticsUpdated(IReadOnlyList<Diagnostic> diagnostics);
    }
}
