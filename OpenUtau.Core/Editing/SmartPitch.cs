using System;
using System.Collections.Generic;
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
    }
}
