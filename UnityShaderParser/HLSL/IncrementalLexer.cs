using System;
using System.Collections.Generic;
using UnityShaderParser.Common;

namespace UnityShaderParser.HLSL
{
    using HLSLToken = Token<TokenKind>;

    /// <summary>
    /// Performs surgical re-lexing of a token stream after a text change.
    /// 
    /// Algorithm:
    /// 1. Find the range of existing tokens whose SourceSpan overlaps the change region.
    /// 2. Expand that range backwards to a "safe restart boundary" (a token that begins
    ///    at a known-safe position — typically a line boundary or after whitespace).
    /// 3. Expand forward past any tokens that might be split/merged by the edit.
    /// 4. Re-lex just the affected substring using the standard HLSLLexer.
    /// 5. Splice the new tokens into the cached list, replacing the old range.
    /// 6. Shift all subsequent tokens' SourceSpan positions by the text delta.
    /// 7. Re-index token positions for consistency.
    /// 
    /// Complexity: O(K + N_tail) where K = re-lexed tokens, N_tail = tokens after edit.
    /// </summary>
    public class IncrementalLexer
    {
        private List<HLSLToken> _tokens;
        private string _basePath;
        private string _fileName;

        public IncrementalLexer(string basePath, string fileName)
        {
            _tokens = new List<HLSLToken>();
            _basePath = basePath ?? string.Empty;
            _fileName = fileName ?? string.Empty;
        }

        /// <summary>
        /// The current token list. Includes an EOF sentinel as the last token.
        /// </summary>
        public IReadOnlyList<HLSLToken> Tokens => _tokens;

        /// <summary>
        /// The underlying mutable token list, for use by IncrementalParser.
        /// </summary>
        internal List<HLSLToken> MutableTokens => _tokens;

        /// <summary>
        /// Perform a full initial lex of the entire source text (cold start).
        /// </summary>
        public List<Diagnostic> Initialize(string fullText)
        {
            _tokens = HLSLLexer.Lex(
                fullText,
                _basePath,
                _fileName,
                throwExceptionOnError: false,
                out var diagnostics);
            return diagnostics;
        }

        /// <summary>
        /// Apply an incremental text change and return the updated diagnostics.
        /// 
        /// <paramref name="change"/> describes the edit in old-text coordinates.
        /// <paramref name="newFullText"/> is the complete text after the edit has been applied.
        /// 
        /// Returns: the range [firstDirtyToken, lastDirtyToken) of token indices that were affected.
        /// </summary>
        public (int firstDirty, int newCount, List<Diagnostic> diagnostics) ApplyChange(
            TextChange change,
            string newFullText)
        {
            if (_tokens.Count == 0)
            {
                // No existing tokens — fall back to full lex
                var diags = Initialize(newFullText);
                return (0, _tokens.Count, diags);
            }

            // ─── Step 1: Find the affected token range in the OLD token list ───

            // Find the first token whose span overlaps or starts at/after the change start.
            // We use the Index field in SourceSpan for character-offset comparison.
            int firstAffected = FindFirstTokenAtOrAfter(change.OldStart);

            // Expand backwards to include any token that CONTAINS the change start point.
            // This handles edits inside an existing token (e.g., typing inside an identifier).
            while (firstAffected > 0 && _tokens[firstAffected - 1].Span.EndIndex > change.OldStart)
            {
                firstAffected--;
            }

            // ─── Step 2: Expand backwards to a safe restart boundary ───
            // Multi-line comments and string literals can span lines, so we must
            // start re-lexing from a token boundary that is definitely "complete".
            // Walk back to the start of any multi-line token (or to the start of the
            // line if the preceding trivia is a multi-line comment).
            firstAffected = ExpandToSafeStart(firstAffected);

            // ─── Step 3: Find the last affected token ───
            // Any token whose span starts before the end of the old change region is affected.
            int changeOldEnd = change.OldEnd;
            int lastAffected = firstAffected; // inclusive
            while (lastAffected < _tokens.Count - 1 && // Don't include EOF
                   _tokens[lastAffected].Span.StartIndex < changeOldEnd)
            {
                lastAffected++;
            }

            // Expand forward to ensure we don't cut a token in half.
            // After the edit, adjacent text may merge with the last affected token.
            // We include one extra token past the change end as a "lookahead sentinel"
            // and then verify convergence (see Step 6).
            lastAffected = ExpandToSafeEnd(lastAffected, change);

            // ─── Step 4: Determine the re-lex region in the NEW text ───
            int relexStartIndex = _tokens[firstAffected].Span.StartIndex;

            // Calculate the end position in the NEW text.
            // Tokens after lastAffected are in old-text coordinates; shift by delta.
            int relexEndIndex;
            if (lastAffected + 1 < _tokens.Count)
            {
                relexEndIndex = _tokens[lastAffected + 1].Span.StartIndex + change.Delta;
            }
            else
            {
                relexEndIndex = newFullText.Length;
            }

            // Clamp to valid range
            relexStartIndex = Math.Max(0, Math.Min(relexStartIndex, newFullText.Length));
            relexEndIndex = Math.Max(relexStartIndex, Math.Min(relexEndIndex, newFullText.Length));

            string relexSubstring = newFullText.Substring(relexStartIndex, relexEndIndex - relexStartIndex);

            // Compute the line/column offset for the re-lex region
            var startLoc = _tokens[firstAffected].Span.Start;
            var offset = new SourceLocation(startLoc.Line, startLoc.Column, relexStartIndex);

            // ─── Step 5: Re-lex the affected substring ───
            var newTokens = HLSLLexer.LexRange(
                relexSubstring,
                _basePath,
                _fileName,
                throwExceptionOnError: false,
                offset,
                out var diagnostics);

            // ─── Step 6: Convergence check ───
            // If the tail of the new tokens matches the tail of the old affected range,
            // we can trim the splice to avoid unnecessary tree invalidation.
            // This typically happens when an edit is purely internal to a token.
            // (For now we skip this optimization and splice the full range.)

            // ─── Step 7: Splice new tokens into the list ───
            int removeCount = lastAffected - firstAffected + 1;

            // Handle the case where lastAffected points to EOF — don't remove it,
            // we'll ensure EOF is preserved.
            if (lastAffected < _tokens.Count &&
                EqualityComparer<TokenKind>.Default.Equals(_tokens[lastAffected].Kind, TokenKind.EndOfFileToken))
            {
                removeCount--; // EOF stays
            }

            if (removeCount > 0)
            {
                _tokens.RemoveRange(firstAffected, removeCount);
            }
            _tokens.InsertRange(firstAffected, newTokens);

            // ─── Step 8: Shift spans of all tokens AFTER the spliced region ───
            int delta = change.Delta;
            if (delta != 0)
            {
                int shiftStart = firstAffected + newTokens.Count;
                for (int i = shiftStart; i < _tokens.Count; i++)
                {
                    _tokens[i].ShiftSpan(delta);
                }
            }

            // ─── Step 9: Re-index token positions ───
            // Token.Position should reflect the index in the token list.
            for (int i = firstAffected; i < _tokens.Count; i++)
            {
                _tokens[i].SetPosition(i);
            }

            // ─── Step 10: Ensure EOF sentinel exists ───
            if (_tokens.Count == 0 ||
                !EqualityComparer<TokenKind>.Default.Equals(_tokens[_tokens.Count - 1].Kind, TokenKind.EndOfFileToken))
            {
                var eofSpan = new SourceSpan(_basePath, _fileName,
                    new SourceLocation(1, 1, newFullText.Length),
                    new SourceLocation(1, 1, newFullText.Length));
                _tokens.Add(new HLSLToken(TokenKind.EndOfFileToken, null, eofSpan, _tokens.Count));
            }

            return (firstAffected, newTokens.Count, diagnostics);
        }

        /// <summary>
        /// Binary search for the first token whose span starts at or after the given character offset.
        /// </summary>
        private int FindFirstTokenAtOrAfter(int charOffset)
        {
            int lo = 0, hi = _tokens.Count - 1;
            int result = _tokens.Count; // Default: past end

            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (_tokens[mid].Span.StartIndex >= charOffset)
                {
                    result = mid;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return result;
        }

        /// <summary>
        /// Expands the start index backwards to a "safe restart" point.
        /// A safe point is a token that:
        /// - Is not inside a multi-line comment or multi-line string in the leading trivia
        /// - Has no pending trivia from a prior multi-line construct
        /// 
        /// For simplicity, we walk backwards past any token whose leading trivia includes
        /// multi-line comments, since those can span arbitrary lines.
        /// </summary>
        private int ExpandToSafeStart(int startIndex)
        {
            // Walk back past any multi-line comment trivia.
            // Also walk back if the token itself is part of a construct that started earlier.
            while (startIndex > 0)
            {
                var token = _tokens[startIndex];

                // If this token has multi-line comment trivia, the re-lex must
                // start earlier so it can properly re-scan the comment.
                if (token.HasLeadingTrivia)
                {
                    bool hasMultiLine = false;
                    foreach (var trivia in token.LeadingTrivia)
                    {
                        if (trivia.Kind == SyntaxTriviaKind.MultiLineComment)
                        {
                            hasMultiLine = true;
                            break;
                        }
                    }
                    if (hasMultiLine)
                    {
                        startIndex--;
                        continue;
                    }
                }

                break;
            }

            return Math.Max(0, startIndex);
        }

        /// <summary>
        /// Expands the end index forwards to include tokens that might be
        /// merged or split by the edit. We add at least one extra token
        /// as a lookahead sentinel for convergence checking.
        /// </summary>
        private int ExpandToSafeEnd(int endIndex, TextChange change)
        {
            // Include at least one extra token past the change as a convergence check.
            if (endIndex + 1 < _tokens.Count - 1) // -1 to skip EOF
            {
                endIndex++;
            }

            // If we're at a multi-line comment, expand further.
            while (endIndex + 1 < _tokens.Count - 1)
            {
                var token = _tokens[endIndex + 1];
                if (token.HasLeadingTrivia)
                {
                    bool hasMultiLine = false;
                    foreach (var trivia in token.LeadingTrivia)
                    {
                        if (trivia.Kind == SyntaxTriviaKind.MultiLineComment)
                        {
                            hasMultiLine = true;
                            break;
                        }
                    }
                    if (hasMultiLine)
                    {
                        endIndex++;
                        continue;
                    }
                }
                break;
            }

            return Math.Min(endIndex, _tokens.Count - 1);
        }
    }
}
