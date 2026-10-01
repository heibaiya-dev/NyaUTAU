using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Editing {
    /// <summary>
    /// Creates a simple speech-like score from plain text. This is deliberately offline:
    /// Chinese/Japanese characters become individual notes, Latin words stay together,
    /// and punctuation becomes a short rest. The resulting notes can then be processed by
    /// the normal phonemizer and automatic vocal tuning tools.
    /// </summary>
    public static class SmartSpeechGenerator {
        public sealed class Options {
            public int BaseTone { get; set; } = 45;
            public double BeatsPerToken { get; set; } = 0.3;
            public bool ReplaceSelection { get; set; } = true;
            public bool FitSelection { get; set; } = true;
        }

        enum BoundaryKind {
            None,
            Soft,
            Falling,
            Rising,
        }

        readonly struct SpeechToken {
            public readonly string Lyric;
            public readonly int Weight;
            public readonly bool IsRest;
            public readonly BoundaryKind BoundaryAfter;
            public SpeechToken(string lyric, int weight, bool isRest = false,
                    BoundaryKind boundaryAfter = BoundaryKind.None) {
                Lyric = lyric;
                Weight = weight;
                IsRest = isRest;
                BoundaryAfter = boundaryAfter;
            }
            public SpeechToken WithBoundary(BoundaryKind boundary) {
                return new SpeechToken(Lyric, Weight, IsRest,
                    BoundaryAfter == BoundaryKind.Rising || boundary == BoundaryKind.Rising
                        ? BoundaryKind.Rising
                        : BoundaryAfter == BoundaryKind.Falling || boundary == BoundaryKind.Falling
                            ? BoundaryKind.Falling
                            : boundary == BoundaryKind.Soft || BoundaryAfter == BoundaryKind.Soft
                                ? BoundaryKind.Soft
                                : BoundaryKind.None);
            }
            public SpeechToken WithWeight(int weight) {
                return new SpeechToken(Lyric, weight, IsRest, BoundaryAfter);
            }
        }

        /// <summary>Apply generated notes as one undoable operation.</summary>
        public static IReadOnlyList<UNote> Apply(
                UProject project, UVoicePart part, IReadOnlyCollection<UNote> selectedNotes,
                string text, Options? options, DocManager docManager) {
            options ??= new Options();
            var tokens = Tokenize(text);
            if (tokens.Count == 0) {
                return Array.Empty<UNote>();
            }

            var selected = selectedNotes
                .Where(part.notes.Contains)
                .OrderBy(note => note.position)
                .ToList();
            var start = selected.Count > 0 && options.ReplaceSelection
                ? selected[0].position
                : selected.Count > 0
                    ? selected[^1].End
                : Math.Max(0, docManager.playPosTick - part.position);
            var selectedEnd = selected.Count > 0 ? selected[^1].End : start;
            var ticksPerBeat = Math.Max(1, project.resolution);
            var baseTicks = Math.Max(30, (int)Math.Round(
                ticksPerBeat * Math.Clamp(options.BeatsPerToken, 0.125, 8),
                MidpointRounding.AwayFromZero));
            var totalWeight = tokens.Sum(token => token.Weight);
            var available = selected.Count > 0 && options.FitSelection
                ? selectedEnd - start
                : 0;
            if (available > 0) {
                baseTicks = Math.Max(30, available / Math.Max(1, totalWeight));
            }

            // Keep generated notes out of a neighbouring note when inserting at the playhead.
            if (selected.Count == 0 || !options.ReplaceSelection) {
                var conflict = part.notes
                    .Where(note => note.position < start + baseTicks * totalWeight && note.End > start)
                    .OrderBy(note => note.End)
                    .LastOrDefault();
                if (conflict != null) {
                    start = conflict.End;
                }
            }

            var generated = new List<UNote>(tokens.Count);
            var cursor = start;
            var toneIndex = 0;
            var phrasePosition = 0;
            var phraseLength = tokens.Count(token => !token.IsRest);
            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++) {
                var token = tokens[tokenIndex];
                var duration = Math.Max(30, baseTicks * token.Weight);
                if (available > 0) {
                    duration = Math.Max(30, (int)Math.Round(
                        (double)available * token.Weight / totalWeight,
                        MidpointRounding.AwayFromZero));
                }
                var toneOffset = token.IsRest
                    ? 0
                    : SpeechContour(toneIndex, phrasePosition, phraseLength, token.BoundaryAfter);
                var note = project.CreateNote(
                    Math.Clamp(options.BaseTone + toneOffset, 1, 127),
                    cursor, duration);
                note.lyric = token.Lyric;
                generated.Add(note);
                cursor += duration;
                if (!token.IsRest) {
                    toneIndex++;
                    phrasePosition++;
                }
                if (token.BoundaryAfter != BoundaryKind.None) {
                    phrasePosition = 0;
                    phraseLength = tokens
                        .Skip(tokenIndex + 1)
                        .TakeWhile(next => !next.IsRest)
                        .TakeWhile(next => next.BoundaryAfter == BoundaryKind.None)
                        .Count();
                }
            }

            docManager.StartUndoGroup("command.smart.speech", true);
            try {
                if (options.ReplaceSelection && selected.Count > 0) {
                    docManager.ExecuteCmd(new RemoveNoteCommand(part, selected));
                }
                docManager.ExecuteCmd(new AddNoteCommand(part, generated));
            } finally {
                docManager.EndUndoGroup();
            }
            return generated;
        }

        static int SpeechContour(int index, int phrasePosition, int phraseLength,
                BoundaryKind boundary) {
            // Keep phrases centered around the requested base note, with a gentle rise in
            // the middle and a clear fall at a sentence boundary. The variation is
            // deterministic so reopening a project produces the same score.
            var arc = phraseLength > 1
                ? Math.Sin(Math.PI * phrasePosition / (phraseLength - 1))
                : 0;
            var offset = (int)Math.Round(arc * 1.2, MidpointRounding.AwayFromZero);
            offset += (index % 4) switch {
                1 => 1,
                2 => 0,
                3 => -1,
                _ => 0,
            };
            return boundary switch {
                BoundaryKind.Falling => -2,
                BoundaryKind.Rising => 2,
                BoundaryKind.Soft => -1,
                _ => offset,
            };
        }

        static List<SpeechToken> Tokenize(string text) {
            var result = new List<SpeechToken>();
            var word = new StringBuilder();
            var cjkIndex = 0;
            void FlushWord() {
                if (word.Length > 0) {
                    var value = word.ToString();
                    var weight = EstimateWordWeight(value);
                    if (result.Count > 0 && result[^1].IsRest) {
                        // Give the first word after a pause a little room to land.
                        weight = Math.Max(weight, 2);
                    }
                    result.Add(new SpeechToken(value, weight));
                    word.Clear();
                }
            }
            var normalized = text.Normalize(NormalizationForm.FormC);
            for (var textIndex = 0; textIndex < normalized.Length; textIndex++) {
                var c = normalized[textIndex];
                if (c is '\r' or '\n') {
                    FlushWord();
                    MarkLastPhrase(BoundaryKind.Falling);
                    AddPause(result, 2, BoundaryKind.Falling);
                    continue;
                }
                if (char.IsWhiteSpace(c)) {
                    FlushWord();
                    var spaces = 1;
                    while (textIndex + 1 < normalized.Length
                            && char.IsWhiteSpace(normalized[textIndex + 1])
                            && normalized[textIndex + 1] is not '\r' and not '\n') {
                        spaces++;
                        textIndex++;
                    }
                    StretchLastToken(spaces);
                    continue;
                }
                if (IsCjkOrKana(c)) {
                    FlushWord();
                    var weight = EstimateCjkWeight(c, cjkIndex++);
                    if (result.Count > 0 && result[^1].IsRest) {
                        weight = Math.Max(weight, 2);
                    }
                    result.Add(new SpeechToken(c.ToString(), weight));
                    continue;
                }
                if (char.IsPunctuation(c) || char.IsSymbol(c)) {
                    // Apostrophes and hyphens are part of a Latin word; sentence marks pause longer.
                    if ((c == '\'' || c == '-' || c == '_') && word.Length > 0) {
                        word.Append(c);
                        continue;
                    }
                    FlushWord();
                    if (IsPause(c)) {
                        var boundary = GetBoundary(c);
                        MarkLastPhrase(boundary);
                        AddPause(result, IsLongPause(c) ? 2 : 1, boundary);
                    }
                    continue;
                }
                word.Append(c);
            }
            FlushWord();
            MarkLastPhrase(BoundaryKind.Falling);
            return result;

            void MarkLastPhrase(BoundaryKind boundary) {
                for (var i = result.Count - 1; i >= 0; i--) {
                    if (!result[i].IsRest) {
                        var token = result[i].WithBoundary(boundary);
                        if (boundary == BoundaryKind.Falling) {
                            token = token.WithWeight(Math.Max(token.Weight, 2));
                        }
                        result[i] = token;
                        return;
                    }
                }
            }

            void StretchLastToken(int spaces) {
                if (spaces <= 0) {
                    return;
                }
                // A space is an explicit sustain marker. Four additional spaces add
                // another base slot, with a cap to keep accidental pasted whitespace usable.
                var extraWeight = Math.Clamp((spaces + 3) / 4, 1, 8);
                for (var i = result.Count - 1; i >= 0; i--) {
                    if (!result[i].IsRest) {
                        result[i] = result[i].WithWeight(result[i].Weight + extraWeight);
                        return;
                    }
                }
            }
        }

        static void AddPause(List<SpeechToken> tokens, int weight, BoundaryKind boundary) {
            if (tokens.Count > 0 && tokens[^1].IsRest) {
                tokens[^1] = tokens[^1].WithWeight(Math.Max(tokens[^1].Weight, weight)).WithBoundary(boundary);
            } else {
                tokens.Add(new SpeechToken("R", weight, true, boundary));
            }
        }

        static int EstimateWordWeight(string word) {
            var syllables = 0;
            var previousVowel = false;
            foreach (var c in word) {
                var lower = char.ToLowerInvariant(c);
                var vowel = lower is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
                if (vowel && !previousVowel) {
                    syllables++;
                }
                previousVowel = vowel;
            }
            if (syllables > 1 && word.EndsWith('e')) {
                syllables--;
            }
            return Math.Clamp(syllables, 1, 3);
        }

        static int EstimateCjkWeight(char c, int index) {
            // Function words are naturally shorter; every fourth content syllable gets a
            // slightly longer slot to avoid an artificial metronomic stream.
            const string light = "的了着过吗呢啊呀吧啦和与在是我你他她它这那不也都就还而";
            if (light.Contains(c)) {
                return 1;
            }
            return index % 4 == 2 ? 2 : 1;
        }

        static bool IsPause(char c) => c is ',' or '，' or '、' or ';' or '；' or ':' or '：'
            or '.' or '。' or '!' or '！' or '?' or '？' or '…' or '\n';

        static bool IsLongPause(char c) => c is '.' or '。' or '!' or '！' or '?' or '？' or '…';

        static BoundaryKind GetBoundary(char c) => c is '?' or '？'
            ? BoundaryKind.Rising
            : IsLongPause(c) ? BoundaryKind.Falling : BoundaryKind.Soft;

        static bool IsCjkOrKana(char c) {
            return c is >= '\u3040' and <= '\u30ff' // Hiragana/Katakana
                or >= '\u3400' and <= '\u4dbf'     // CJK extension A
                or >= '\u4e00' and <= '\u9fff'     // CJK unified
                or >= '\uf900' and <= '\ufaff';    // CJK compatibility
        }
    }
}
