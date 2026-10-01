using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.Format {
    [Collection(RenderSingletonCollection.Name)]
    public class UstxCompatibilityTest : IDisposable {
        readonly string directory = Path.Combine(Path.GetTempPath(), $"ustx-compat-{Guid.NewGuid():N}");

        public UstxCompatibilityTest() {
            Directory.CreateDirectory(directory);
        }

        public void Dispose() {
            Directory.Delete(directory, true);
        }

        string WriteSource(string text, string name = "song.ustx") {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, text, new UTF8Encoding(true));
            return path;
        }

        static string FutureProject(string fields) => "name: Compatibility test\nustx_version: 99.0\n" + fields;

        [Fact]
        public void NormalLoadRejectsNewerVersionWithoutCreatingOrChangingFiles() {
            var source = WriteSource(FutureProject("bpm: 173\n"));
            var original = File.ReadAllBytes(source);

            var error = Assert.Throws<UstxVersionException>(() => Ustx.Load(source));

            Assert.Equal(source, error.FilePath);
            Assert.Equal(new Version(99, 0), error.FileVersion);
            Assert.Equal(Ustx.kUstxVersion, error.SupportedVersion);
            Assert.Contains("errors.failed.opennewerproject", error.TranslatableMessage);
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Single(Directory.GetFiles(directory));
        }

        [Fact]
        public void ConversionPreservesSourceAndRecognizedNotesInANewSupportedFile() {
            var project = new UProject { name = "Preserve me", ustxVersion = new Version(99, 0) };
            var note = project.CreateNote(64, 120, 480);
            note.lyric = "你好";
            note.pitch.snapFirst = false;
            note.pitch.data.Clear();
            note.pitch.data.Add(new PitchPoint(-25, -5, PitchPointShape.o));
            note.pitch.data.Add(new PitchPoint(100, 0, PitchPointShape.sp));
            var part = new UVoicePart { name = "Voice", position = 480, duration = 1920 };
            part.notes.Add(note);
            project.parts.Add(part);
            project.BeforeSave();
            var text = Yaml.DefaultSerializer.Serialize(project) + "future_only: { extra: true }\n";
            project.AfterSave();
            var source = WriteSource(text);
            var original = File.ReadAllBytes(source);

            var converted = Ustx.ConvertToCurrentVersion(source);
            var loaded = Ustx.Load(converted);

            Assert.Equal(Path.Combine(directory, "song.converted.ustx"), converted);
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Equal(Ustx.kUstxVersion, loaded.ustxVersion);
            Assert.Equal(converted, loaded.FilePath);
            Assert.Equal("Preserve me", loaded.name);
            var loadedPart = Assert.IsType<UVoicePart>(Assert.Single(loaded.parts));
            var loadedNote = Assert.Single(loadedPart.notes);
            Assert.Equal((480, 120, 480, 64, "你好"),
                (loadedPart.position, loadedNote.position, loadedNote.duration, loadedNote.tone, loadedNote.lyric));
            Assert.Equal(note.pitch.data.Select(point => (point.X, point.Y, point.shape)),
                loadedNote.pitch.data.Select(point => (point.X, point.Y, point.shape)));
            Assert.DoesNotContain("future_only", File.ReadAllText(converted));
        }

        [Theory]
        [InlineData("")]
        [InlineData("tempos: []\n")]
        [InlineData("tempos: null\n")]
        [InlineData("tempos:\n- {position: 0, bpm: 0}\n")]
        public void MissingInitialTempoRecoversOriginalHeaderInsteadOfConstructorDefault(string tempos) {
            var source = WriteSource(FutureProject("bpm: 173.5\n" + tempos));

            var loaded = Ustx.Load(Ustx.ConvertToCurrentVersion(source));

            var tempo = Assert.Single(loaded.tempos);
            Assert.Equal(0, tempo.position);
            Assert.Equal(173.5, tempo.bpm);
            Assert.Equal(60000d / 173.5, loaded.timeAxis.TickPosToMsPos(480), 6);
        }

        [Fact]
        public void ValidTempoMapWinsOverHeaderAndPreservesEveryTempoChange() {
            var source = WriteSource(FutureProject("bpm: 173\n" +
                "tempos:\n- {position: 0, bpm: 96}\n- {position: 480, bpm: 144}\n" +
                "- {position: 1920, bpm: 118.25}\n"));

            var loaded = Ustx.Load(Ustx.ConvertToCurrentVersion(source));

            Assert.Equal(new[] { (0, 96d), (480, 144d), (1920, 118.25) },
                loaded.tempos.Select(tempo => (tempo.position, tempo.bpm)));
        }

        [Fact]
        public void HeaderFillsMissingBeginningWithoutRemovingLaterValidTempos() {
            var source = WriteSource(FutureProject("bpm: 173\n" +
                "tempos:\n- {position: 0, bpm: -1}\n- {position: 480, bpm: 150}\n"));

            var loaded = Ustx.Load(Ustx.ConvertToCurrentVersion(source));

            Assert.Equal(new[] { (0, 173d), (480, 150d) },
                loaded.tempos.Select(tempo => (tempo.position, tempo.bpm)));
        }

        [Fact]
        public void InvalidHeaderFallsBackToAValidTempoRatherThanInventing120Bpm() {
            var source = WriteSource(FutureProject("bpm: not-a-number\n" +
                "tempos:\n- {position: 960, bpm: 156}\n"));

            var loaded = Ustx.Load(Ustx.ConvertToCurrentVersion(source));

            Assert.Equal(new[] { (0, 156d), (960, 156d) },
                loaded.tempos.Select(tempo => (tempo.position, tempo.bpm)));
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-30")]
        [InlineData(".nan")]
        [InlineData(".inf")]
        [InlineData("broken")]
        public void NoValidTempoOrHeaderFailsClearlyWithoutWritingACopy(string bpm) {
            var source = WriteSource(FutureProject($"bpm: {bpm}\ntempos: []\n"));
            var original = File.ReadAllBytes(source);

            var error = Assert.Throws<FileFormatException>(() => Ustx.ConvertToCurrentVersion(source));

            Assert.Contains("No valid BPM", error.Message);
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Single(Directory.GetFiles(directory));
        }

        [Fact]
        public void JsonHeaderAndMemberCasingAreRecovered() {
            var source = WriteSource("{\"name\":\"JSON source\",\"ustxVersion\":\"99.0\",\"Bpm\":181.25}");
            Assert.Throws<UstxVersionException>(() => Ustx.Load(source));

            var loaded = Ustx.Load(Ustx.ConvertToCurrentVersion(source));

            Assert.Equal("JSON source", loaded.name);
            Assert.Equal(181.25, Assert.Single(loaded.tempos).bpm);
        }

        [Fact]
        public void ExistingConversionsAreNeverOverwritten() {
            var source = WriteSource(FutureProject("bpm: 173\n"));
            var existing = WriteSource("keep this copy", "song.converted.ustx");

            var converted = Ustx.ConvertToCurrentVersion(source);
            var another = Ustx.ConvertToCurrentVersion(source);

            Assert.Equal(Path.Combine(directory, "song.converted-2.ustx"), converted);
            Assert.Equal(Path.Combine(directory, "song.converted-3.ustx"), another);
            Assert.Equal("keep this copy", File.ReadAllText(existing));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }

        [Theory]
        [InlineData("ustx_version: 99.0\nbpm: 173\nvoice_parts: [\n")]
        [InlineData("ustx_version: 99.0\nbpm: 173\ntracks: broken\n")]
        [InlineData("ustx_version: 99.0\nbpm: 173\ntempos: broken\n")]
        [InlineData("ustx_version: 99.0\nbpm: 173\ntempos: [{position: later, bpm: 160}]\n")]
        [InlineData("ustx_version: broken\nbpm: 173\n")]
        public void StructurallyInvalidInputIsNotForcedThrough(string text) {
            var source = WriteSource(text);
            var original = File.ReadAllBytes(source);

            Assert.ThrowsAny<Exception>(() => Ustx.ConvertToCurrentVersion(source));

            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Single(Directory.GetFiles(directory));
        }
    }
}
