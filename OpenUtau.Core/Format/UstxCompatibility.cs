using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OpenUtau.Core.Ustx;
using YamlDotNet.RepresentationModel;

namespace OpenUtau.Core.Format {
    public sealed class UstxVersionException : MessageCustomizableException {
        public string FilePath { get; }
        public Version FileVersion { get; }
        public Version SupportedVersion { get; }

        public UstxVersionException(string filePath, Version fileVersion, Version supportedVersion)
            : base($"Project file is newer than software: {filePath}",
                $"<translate:errors.failed.opennewerproject>:\n{filePath}",
                new FileFormatException("Project file is newer than software.")) {
            FilePath = filePath;
            FileVersion = fileVersion;
            SupportedVersion = supportedVersion;
        }
    }

    public partial class Ustx {
        /// <summary>
        /// Explicitly attempts to read a newer USTX with the supported model, then writes a
        /// separate current-version copy. Unknown future fields are omitted; the source is untouched.
        /// </summary>
        public static string ConvertToCurrentVersion(string sourcePath) {
            sourcePath = Path.GetFullPath(sourcePath);
            var root = ReadDocument(File.ReadAllText(sourcePath, Encoding.UTF8));
            var version = ReadVersion(root);
            if (version == null || version <= kUstxVersion) {
                throw new FileFormatException("Conversion requires a newer USTX project version.");
            }
            NormalizeMemberNames(root, typeof(UProject));
            RestoreTempoMap(root);

            var stream = new YamlStream(new YamlDocument(root));
            using var normalizedWriter = new StringWriter(CultureInfo.InvariantCulture);
            stream.Save(normalizedWriter, false);
            var project = LoadCore(sourcePath, normalizedWriter.ToString(), true);
            string convertedText;
            try {
                project.BeforeSave();
                convertedText = Yaml.DefaultSerializer.Serialize(project);
            } finally {
                project.AfterSave();
            }
            // Reading the current schema again also catches serialization/conversion inconsistencies
            // before any output is committed. The sibling location preserves relative audio paths.
            LoadCore(sourcePath, convertedText, false);
            return WriteConvertedCopy(sourcePath, convertedText);
        }

        static YamlMappingNode ReadDocument(string text) {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) {
                throw new FileFormatException("A USTX file must contain one project mapping.");
            }
            return root;
        }

        static Version? ReadVersion(YamlMappingNode root) {
            var versions = root.Children.Where(pair => pair.Key is YamlScalarNode key &&
                string.Equals(key.Value?.Replace("_", ""), "ustxVersion",
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (versions.Length == 0) {
                return null;
            }
            if (versions.Length != 1 || versions[0].Value is not YamlScalarNode scalar ||
                    !Version.TryParse(scalar.Value, out var version)) {
                throw new FileFormatException("The USTX version header is invalid.");
            }
            return version;
        }

        static void NormalizeMemberNames(YamlNode node, Type type) {
            if (node is YamlSequenceNode sequence) {
                var itemType = YamlValidator.ItemType(type);
                if (itemType != null) {
                    foreach (var item in sequence.Children) {
                        NormalizeMemberNames(item, itemType);
                    }
                }
                return;
            }
            if (node is not YamlMappingNode mapping) {
                return;
            }
            var valueType = YamlValidator.DictionaryValueType(type);
            if (valueType != null) {
                foreach (var value in mapping.Children.Values) {
                    NormalizeMemberNames(value, valueType);
                }
                return;
            }
            var members = YamlValidator.KeysOf(type).ToArray();
            foreach (var (key, value) in mapping.Children.ToArray()) {
                if (key is not YamlScalarNode scalar) {
                    throw new FileFormatException("USTX property names must be scalar values.");
                }
                var member = members.FirstOrDefault(member =>
                    string.Equals(member.name, scalar.Value, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member.property.Name, scalar.Value, StringComparison.OrdinalIgnoreCase));
                if (member.property == null) {
                    continue;
                }
                var canonical = new YamlScalarNode(member.name);
                if (!canonical.Equals(key)) {
                    if (mapping.Children.ContainsKey(canonical)) {
                        throw new FileFormatException($"Duplicate USTX property: {member.name}.");
                    }
                    mapping.Children.Remove(key);
                    mapping.Children.Add(canonical, value);
                }
                NormalizeMemberNames(value, member.property.Type);
            }
        }

        static void RestoreTempoMap(YamlMappingNode root) {
            var bpmKey = new YamlScalarNode("bpm");
            root.Children.TryGetValue(bpmKey, out var headerNode);
            var headerBpm = ReadBpm(headerNode);
            var temposKey = new YamlScalarNode("tempos");
            var tempos = new List<UTempo>();
            if (root.Children.TryGetValue(temposKey, out var tempoNode) && !IsNull(tempoNode)) {
                if (tempoNode is not YamlSequenceNode sequence) {
                    throw new FileFormatException("The USTX tempos field must be a sequence.");
                }
                foreach (var item in sequence.Children) {
                    if (item is not YamlMappingNode tempo ||
                            !tempo.Children.TryGetValue(new YamlScalarNode("position"), out var positionNode) ||
                            positionNode is not YamlScalarNode positionScalar ||
                            !int.TryParse(positionScalar.Value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var position) || position < 0) {
                        throw new FileFormatException("A USTX tempo has an invalid tick position.");
                    }
                    tempo.Children.TryGetValue(bpmKey, out var value);
                    var bpm = ReadBpm(value);
                    if (bpm.HasValue) {
                        tempos.Add(new UTempo(position, bpm.Value));
                    }
                }
            }
            tempos = tempos.OrderBy(tempo => tempo.position).ToList();
            if (tempos.Count == 0 || tempos[0].position != 0) {
                // Do not let UProject's constructor silently replace a missing map with 120 BPM.
                var initialBpm = headerBpm ?? tempos.FirstOrDefault()?.bpm;
                if (!initialBpm.HasValue) {
                    throw new FileFormatException(
                        "No valid BPM was found in the USTX tempo map or the original BPM header.");
                }
                tempos.Insert(0, new UTempo(0, initialBpm.Value));
            }
            var restored = new YamlSequenceNode();
            foreach (var tempo in tempos) {
                restored.Add(new YamlMappingNode {
                    { "position", tempo.position.ToString(CultureInfo.InvariantCulture) },
                    { "bpm", tempo.bpm.ToString("R", CultureInfo.InvariantCulture) },
                });
            }
            root.Children[temposKey] = restored;
            // An obsolete malformed header must not block an otherwise complete valid tempo map.
            if (headerNode != null && !headerBpm.HasValue) {
                root.Children.Remove(bpmKey);
            }
        }

        static double? ReadBpm(YamlNode? node) => node is YamlScalarNode scalar &&
            double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) &&
            double.IsFinite(bpm) && bpm > 0 ? bpm : null;

        static bool IsNull(YamlNode node) => node is YamlScalarNode scalar &&
            (scalar.Value == null || scalar.Value is "" or "~" or "null" or "Null" or "NULL");

        static string WriteConvertedCopy(string sourcePath, string text) {
            var directory = Path.GetDirectoryName(sourcePath)!;
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            var temporaryPath = Path.Combine(directory, $".{name}.conversion-{Guid.NewGuid():N}.tmp");
            try {
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write)) {
                    using var writer = new StreamWriter(output, new UTF8Encoding(false));
                    writer.Write(text);
                }
                for (var suffix = 1; ; suffix++) {
                    var ending = suffix == 1 ? ".converted.ustx" : $".converted-{suffix}.ustx";
                    var convertedPath = Path.Combine(directory, name + ending);
                    try {
                        File.Move(temporaryPath, convertedPath, false);
                        return convertedPath;
                    } catch (IOException) when (File.Exists(convertedPath) || Directory.Exists(convertedPath)) {
                        // A pre-existing conversion (or a concurrent one) is never overwritten.
                    }
                }
            } finally {
                if (File.Exists(temporaryPath)) {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
