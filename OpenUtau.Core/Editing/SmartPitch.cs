using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Editing {
    /// <summary>Builds a restrained pitch gesture from note timing and melodic context.</summary>
    public class SmartPitch : BatchEdit {
        public string Name => "pianoroll.menu.notes.smartpitch";

        public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes,
                DocManager docManager) {
            var commands = BuildCommands(project, part, selectedNotes);
            if (commands.Count == 0) {
                return;
            }
            docManager.StartUndoGroup(Name, true);
            try {
                foreach (var command in commands) {
                    docManager.ExecuteCmd(command);
                }
            } finally {
                docManager.EndUndoGroup();
            }
        }

        internal static List<SetPitchPointsCommand> BuildCommands(UProject project,
                UVoicePart part, IReadOnlyCollection<UNote> selectedNotes, float strength = 1f) {
            strength = Math.Clamp(strength, 0, 1);
            var selected = selectedNotes.Count > 0 ? new HashSet<UNote>(selectedNotes) : null;
            var commands = new List<SetPitchPointsCommand>();
            UNote? previous = null;
            foreach (var note in part.notes) {
                if ((selected == null || selected.Contains(note)) && IsPitched(note)) {
                    var startMs = project.timeAxis.TickPosToMsPos((double)part.position + note.position);
                    var endMs = project.timeAxis.TickPosToMsPos(
                        (double)part.position + note.position + note.duration);
                    var durationMs = (float)(endMs - startMs);
                    if (float.IsFinite(durationMs) && durationMs > 0) {
                        var connected = previous != null && IsPitched(previous) &&
                            (long)previous.position + previous.duration == note.position;
                        var interval = connected ? note.AdjustedTone - previous!.AdjustedTone : 0;
                        if (float.IsFinite(interval)) {
                            var pitch = CreatePitch(durationMs, connected, interval, strength);
                            commands.Add(new SetPitchPointsCommand(part, note, pitch));
                        }
                    }
                }
                previous = note;
            }
            return commands;
        }

        /// <summary>
        /// Builds the pitch gestures used by automatic vocal tuning.  The original
        /// smart-pitch command intentionally stays conservative (it is also used by
        /// the quick menu action), while this variant uses the surrounding phrase to
        /// add the small timing and intonation changes that make a line feel sung.
        /// Only notes in <paramref name="selectedNotes"/> are replaced; neighbouring
        /// notes are read as context and are never modified.
        /// </summary>
        internal static List<SetPitchPointsCommand> BuildHumanizedCommands(UProject project,
                UVoicePart part, IReadOnlyCollection<UNote> selectedNotes, float strength = 1f) {
            strength = Math.Clamp(strength, 0, 1);
            var selected = selectedNotes.Count > 0 ? new HashSet<UNote>(selectedNotes) : null;
            var commands = new List<SetPitchPointsCommand>();
            foreach (var phrase in Phrases(part)) {
                for (int i = 0; i < phrase.Count; i++) {
                    var note = phrase[i];
                    if (selected != null && !selected.Contains(note)) {
                        continue;
                    }
                    var durationMs = NoteDurationMs(project, part, note);
                    if (!float.IsFinite(durationMs) || durationMs <= 0) {
                        continue;
                    }
                    var previous = i > 0 ? phrase[i - 1] : null;
                    var next = i + 1 < phrase.Count ? phrase[i + 1] : null;
                    var interval = previous == null ? 0 : note.AdjustedTone - previous.AdjustedTone;
                    var progress = phrase.Count <= 1 ? .5f : (float)i / (phrase.Count - 1);
                    commands.Add(new SetPitchPointsCommand(part, note,
                        CreateHumanPitch(durationMs, previous != null, interval, progress,
                            note, next, strength)));
                }
            }
            return commands;
        }

        internal static bool IsPitched(UNote note) {
            if (note.duration <= 0 || string.IsNullOrWhiteSpace(note.lyric)) {
                return false;
            }
            // The same conventional rest/breath lyrics are excluded by the lyric macros.
            return note.lyric.Trim() is not ("R" or "SP" or "AP" or "br" or "cl" or "息" or "吸");
        }

        static UPitch CreatePitch(float durationMs, bool connected, float interval, float strength = 1f) {
            // Stay inside the target note: a selected note must not retune its neighbour's tail.
            // Explicit starts also keep validation from reconnecting across a rest or breath.
            var pitch = new UPitch { snapFirst = false };
            var attackMs = Math.Min(durationMs * 0.25f, 45f + Math.Min(Math.Abs(interval), 12f) * 7.5f);
            var startPitch = (connected ? -interval * 10f : -Math.Min(2f, durationMs / 150f)) * strength;
            AddPoint(0, startPitch);
            AddPoint(attackMs, 0);

            if (durationMs >= 160) {
                // Pitch-point Y is in ten-cent units. Body movement stays within twelve cents,
                // with a smaller opposite drift and a soft recovery before the next note.
                var direction = interval < 0 ? -1f : 1f;
                var depth = Math.Min(1.2f, (durationMs - 90f) / 450f) * direction * strength;
                var bodyMs = durationMs - attackMs;
                AddPoint(attackMs + bodyMs * 0.2f, depth);
                AddPoint(attackMs + bodyMs * 0.55f, -depth * 0.55f);
                AddPoint(attackMs + bodyMs * 0.82f, depth * 0.2f);
            }
            AddPoint(durationMs, 0);
            return pitch;

            void AddPoint(float x, float y) {
                // Sub-millisecond notes can round adjacent float timestamps to the same value.
                if (pitch.data.Count == 0 || x > pitch.data[^1].X) {
                    pitch.data.Add(new PitchPoint(x, y, PitchPointShape.io));
                }
            }
        }

        static UPitch CreateHumanPitch(float durationMs, bool connected, float interval,
                float phraseProgress, UNote note, UNote? next, float strength) {
            // A broad phrase arc (slightly rising towards the middle, then relaxing)
            // plus a deterministic, very small singer-to-singer variation. Values are
            // in ten-cent pitch units, as required by UPitch.
            var arc = (float)Math.Sin(Math.PI * phraseProgress);
            var phraseOffset = (arc * 1.7f - phraseProgress * 0.85f) * strength;
            var neighbouringSlope = next == null ? 0 : next.AdjustedTone - note.AdjustedTone;
            var anticipation = Math.Clamp(neighbouringSlope * 0.75f, -2.4f, 2.4f) * strength;
            var seed = (float)Math.Sin(note.position * 0.071 + note.tone * 0.17);
            var variation = seed * Math.Min(1.15f, durationMs / 500f) * strength;
            var body = phraseOffset + variation;
            var pitch = new UPitch { snapFirst = false };
            var transition = connected
                ? Math.Clamp(-interval * 10f, -52f, 52f) * (0.82f + 0.12f * strength)
                : -Math.Min(.85f, durationMs / 220f) * strength;
            var attackMs = Math.Clamp(durationMs * (0.13f + Math.Abs(interval) * 0.012f),
                18f, Math.Min(75f, durationMs * .48f));
            AddPoint(0, transition);
            AddPoint(attackMs, body + anticipation * .18f);

            if (durationMs >= 130) {
                var bodyMs = durationMs - attackMs;
                // A gentle scoop into the body and a release before the next onset.
                AddPoint(attackMs + bodyMs * .28f, body + anticipation * .55f);
                AddPoint(attackMs + bodyMs * .62f, body - anticipation * .25f);
                AddPoint(attackMs + bodyMs * .86f, body * .35f);
            }
            AddPoint(durationMs, 0);
            return pitch;

            void AddPoint(float x, float y) {
                if (pitch.data.Count == 0 || x > pitch.data[^1].X) {
                    pitch.data.Add(new PitchPoint(x, y, PitchPointShape.io));
                }
            }
        }

        static IEnumerable<List<UNote>> Phrases(UVoicePart part) {
            var phrase = new List<UNote>();
            UNote? previous = null;
            foreach (var note in part.notes.OrderBy(note => note.position)) {
                if (!IsPitched(note)) {
                    if (phrase.Count > 0) {
                        yield return phrase;
                        phrase = new List<UNote>();
                    }
                    previous = null;
                    continue;
                }
                if (previous != null && previous.End != note.position && phrase.Count > 0) {
                    yield return phrase;
                    phrase = new List<UNote>();
                }
                phrase.Add(note);
                previous = note;
            }
            if (phrase.Count > 0) {
                yield return phrase;
            }
        }

        static float NoteDurationMs(UProject project, UVoicePart part, UNote note) {
            var start = project.timeAxis.TickPosToMsPos((double)part.position + note.position);
            var end = project.timeAxis.TickPosToMsPos((double)part.position + note.End);
            return (float)(end - start);
        }
    }
}
