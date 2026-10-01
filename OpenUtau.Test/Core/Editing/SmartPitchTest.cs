using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.Editing {
    public class SmartPitchTest {
        static UNote Note(int position, int duration = 480, int tone = 60, string lyric = "a") {
            var note = UNote.Create();
            note.position = position;
            note.duration = duration;
            note.tone = tone;
            note.lyric = lyric;
            note.pitch.snapFirst = false;
            note.pitch.data.Add(new PitchPoint(-30, -3, PitchPointShape.o, true));
            note.pitch.data.Add(new PitchPoint(70, 0, PitchPointShape.sp));
            return note;
        }

        static (UProject project, UVoicePart part) Project(params UNote[] notes) {
            var project = new UProject();
            var part = new UVoicePart();
            foreach (var note in notes) {
                part.notes.Add(note);
            }
            project.parts.Add(part);
            return (project, part);
        }

        static (float, float, PitchPointShape, bool)[] Points(UNote note) => note.pitch.data
            .Select(point => (point.X, point.Y, point.shape, point.autoCompleted)).ToArray();

        static void Execute(IEnumerable<SetPitchPointsCommand> commands) {
            foreach (var command in commands) {
                command.Execute();
            }
        }

        [Fact]
        public void SelectedNotesOnlyPreserveOtherNotesAndMusicalData() {
            var first = Note(0);
            var middle = Note(480, tone: 62);
            var last = Note(960, tone: 65, lyric: "u");
            var (project, part) = Project(first, middle, last);
            var originals = part.notes.Select(note => note.pitch).ToArray();
            var unchangedPoints = Points(middle);
            var musicalData = part.notes
                .Select(note => (note.position, note.duration, note.tone, note.lyric, note.tuning, note.vibrato))
                .ToArray();

            var commands = SmartPitch.BuildCommands(project, part, new[] { last, first, first });
            Assert.Equal(2, commands.Count);
            Execute(commands);

            Assert.NotSame(originals[0], first.pitch);
            Assert.Same(originals[1], middle.pitch);
            Assert.Equal(unchangedPoints, Points(middle));
            Assert.NotSame(originals[2], last.pitch);
            Assert.NotSame(first.pitch, last.pitch);
            Assert.Equal(musicalData, part.notes
                .Select(note => (note.position, note.duration, note.tone, note.lyric, note.tuning, note.vibrato))
                .ToArray());
        }

        [Fact]
        public void EmptySelectionTargetsAllPitchedNotesInCurrentPart() {
            var first = Note(0);
            var rest = Note(480, lyric: "R");
            var last = Note(960, tone: 67);
            var (project, part) = Project(first, rest, last);
            var other = new UVoicePart { notes = { Note(0) } };
            project.parts.Add(other);
            var restPitch = rest.pitch;
            var otherPitch = other.notes.First().pitch;

            var commands = SmartPitch.BuildCommands(project, part, Array.Empty<UNote>());
            Assert.Equal(2, commands.Count);
            Execute(commands);

            Assert.True(first.pitch.data.Count > 2);
            Assert.True(last.pitch.data.Count > 2);
            Assert.Same(restPitch, rest.pitch);
            Assert.Same(otherPitch, other.notes.First().pitch);
        }

        [Fact]
        public void InvalidOrEmptyTargetsDoNotFallBackToAllNotes() {
            var rest = Note(480, lyric: "R");
            var (project, part) = Project(Note(0), rest, Note(960, duration: 0));

            Assert.Empty(SmartPitch.BuildCommands(project, part, new[] { rest }));
            Assert.Empty(SmartPitch.BuildCommands(project, part, new[] { Note(0) }));
            Assert.Empty(SmartPitch.BuildCommands(project, new UVoicePart(), Array.Empty<UNote>()));
            Assert.Single(SmartPitch.BuildCommands(project, part, Array.Empty<UNote>()));
        }

        [Fact]
        public void AdjacentNotesGlideFromThePreviousTunedPitchInsideTargetNote() {
            var previous = Note(0, tone: 60);
            previous.tuning = 25;
            var current = Note(480, tone: 64);
            current.tuning = -15;
            var (project, part) = Project(previous, current);
            var originalPrevious = previous.pitch;

            Execute(SmartPitch.BuildCommands(project, part, new[] { current }));

            Assert.False(current.pitch.snapFirst);
            Assert.Equal(0, current.pitch.data[0].X);
            Assert.Equal(-36f, current.pitch.data[0].Y, 3);
            Assert.Equal(0, current.pitch.data[1].Y);
            Assert.InRange(current.pitch.data[1].X, 1f, 125f);
            Assert.Equal(0, current.pitch.data[^1].Y);
            Assert.Same(originalPrevious, previous.pitch);
            Assert.All(current.pitch.data.Skip(1), point => Assert.InRange(point.Y, -1.2f, 1.2f));
        }

        [Theory]
        [InlineData("R")]
        [InlineData("SP")]
        [InlineData("AP")]
        [InlineData("br")]
        [InlineData("息")]
        public void RestOrBreathDoesNotCarryItsPitchIntoTheNextNote(string lyric) {
            var previous = Note(0, tone: 36, lyric: lyric);
            var current = Note(480, tone: 72);
            var (project, part) = Project(previous, current);
            var oldPitch = previous.pitch;

            Execute(SmartPitch.BuildCommands(project, part, Array.Empty<UNote>()));

            Assert.Same(oldPitch, previous.pitch);
            Assert.InRange(current.pitch.data[0].Y, -2f, 0f);
            Assert.False(current.pitch.snapFirst);
        }

        [Theory]
        [InlineData(600)]
        [InlineData(240)]
        public void GapOrOverlapStartsANewGesture(int nextPosition) {
            var (project, part) = Project(Note(0, tone: 36), Note(nextPosition, tone: 72));
            var current = part.notes.Last();

            Execute(SmartPitch.BuildCommands(project, part, new[] { current }));

            Assert.Equal(0, current.pitch.data[0].X);
            Assert.InRange(current.pitch.data[0].Y, -2f, 0f);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(10)]
        [InlineData(40)]
        [InlineData(80)]
        public void ShortNotesHaveFiniteStrictlyOrderedPointsWithinTheirDuration(int duration) {
            var note = Note(0, duration);
            var (project, part) = Project(note);
            project.tempos[0].bpm = 1000;
            project.timeAxis.BuildSegments(project);
            var expectedDuration = (float)project.timeAxis.TickPosToMsPos(duration);

            Execute(SmartPitch.BuildCommands(project, part, Array.Empty<UNote>()));

            Assert.True(note.pitch.data.Count >= 2);
            for (var i = 0; i < note.pitch.data.Count; i++) {
                var point = note.pitch.data[i];
                Assert.True(float.IsFinite(point.X) && float.IsFinite(point.Y));
                Assert.InRange(point.X, 0f, expectedDuration);
                if (i > 0) {
                    Assert.True(point.X > note.pitch.data[i - 1].X);
                }
            }
            Assert.Equal(expectedDuration, note.pitch.data[^1].X);
            Assert.Equal(0, note.pitch.data[^1].Y);
            Assert.Equal(duration, note.duration);
        }

        [Fact]
        public void TimingIncludesPartOffsetAndTempoChangesWithoutUsingCachedMilliseconds() {
            var note = Note(0);
            note.PositionMs = 12345;
            note.EndMs = 12346;
            var (project, part) = Project(note);
            part.position = 240;
            project.tempos.Add(new UTempo(480, 60));
            project.timeAxis.BuildSegments(project);

            Execute(SmartPitch.BuildCommands(project, part, Array.Empty<UNote>()));

            Assert.Equal(750f, note.pitch.data[^1].X);
        }

        [Fact]
        public void UndoRestoresExactPitchObjectsAndRedoRepeatsTheSameResult() {
            var first = Note(0);
            first.pitch.snapFirst = true;
            var second = Note(480, tone: 67);
            var (project, part) = Project(first, second);
            var originals = part.notes.Select(note => note.pitch).ToArray();
            var originalPoints = part.notes.Select(Points).ToArray();
            var commands = SmartPitch.BuildCommands(project, part, Array.Empty<UNote>());
            Execute(commands);
            var generated = part.notes.Select(Points).ToArray();

            foreach (var command in commands.AsEnumerable().Reverse()) {
                command.Unexecute();
            }

            var notes = part.notes.ToArray();
            for (var i = 0; i < notes.Length; i++) {
                Assert.Same(originals[i], notes[i].pitch);
                Assert.Equal(originalPoints[i], Points(notes[i]));
            }
            Assert.True(first.pitch.snapFirst);
            Execute(commands);
            for (var i = 0; i < notes.Length; i++) {
                Assert.Equal(generated[i], Points(notes[i]));
            }
        }

        [Fact]
        public void RegenerationIsDeterministicAndDoesNotAccumulateMovement() {
            var note = Note(0, duration: 1920);
            var (project, part) = Project(note);
            Execute(SmartPitch.BuildCommands(project, part, Array.Empty<UNote>()));
            var firstResult = Points(note);

            Execute(SmartPitch.BuildCommands(project, part, Array.Empty<UNote>()));

            Assert.Equal(firstResult, Points(note));
        }
    }
}
