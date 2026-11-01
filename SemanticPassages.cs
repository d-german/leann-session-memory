using System.Text;
using System.Text.RegularExpressions;

namespace SessionMemory
{
    internal static class SemanticPassages
    {
        private sealed record Message(int Number, string Role, string Timestamp, string Text, string Request);
        private static readonly Regex PassageName = new(@"^(?<source>.+\.md)__h(?<hash>[0-9a-f]{24})__m(?<number>[0-9]{6,10})_p(?<part>[0-9]{3,10})\.md$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

        public static int Write(string corpusFile, string targetDirectory, string sourceFileName, string title)
        {
            if (!Flat(sourceFileName) || !sourceFileName.EndsWith(".md", StringComparison.Ordinal))
                throw new ArgumentException("Expected a flat source Markdown filename.", nameof(sourceFileName));
            var messages = new List<Message>();
            var request = "";
            var contentSource = File.ReadAllText(corpusFile).Replace("\r\n", "\n", StringComparison.Ordinal);
            var hash = Transcript.Sha256(contentSource)[..24];
            foreach (var segment in Transcript.SourceMessages(contentSource))
            {
                if (segment.Number <= 0) continue;
                if (messages.Count > 0 && messages[^1].Role == "user") request = messages[^1].Text;
                messages.Add(new Message(segment.Number, segment.Role, segment.Timestamp, segment.Text,
                    segment.Role == "user" ? "" : request));
            }
            Directory.CreateDirectory(targetDirectory);
            var pieces = messages.SelectMany(message => Transcript.SplitText(message.Text, 768)
                .SelectMany(part => part.Length <= 900 ? [part] : Transcript.SplitText(part, 400))
                .Select(part => (message, part))).ToArray();
            var totals = pieces.GroupBy(p => p.message.Number).ToDictionary(g => g.Key, g => g.Count());
            var counts = new Dictionary<int, int>();
            var cleanTitle = Limit(Space.Replace(title, " ").Trim(), 96);
            foreach (var (message, part) in pieces)
            {
                var number = message.Number;
                var index = counts.GetValueOrDefault(number) + 1;
                counts[number] = index;
                var requestText = Limit(Space.Replace(message.Request, " ").Trim(), 256);
                var prefix = (cleanTitle.Length > 0 ? $"Conversation: {cleanTitle}\n" : "")
                             + (requestText.Length > 0 ? $"Prior request: {requestText}\n" : "");
                var header = $"## Message {number:D6} part {index}/{totals[number]}\nrole: {message.Role}\ntimestamp: {message.Timestamp}\ntext_length: {part.Length}\ntext:\n";
                var content = prefix + header + part + "\n";
                if (content.Length >= 1400) throw new InvalidDataException("Passage exceeds the embedding chunk limit.");
                var name = $"{sourceFileName}__h{hash}__m{number:D6}_p{index:D3}.md";
                File.WriteAllText(Path.Combine(targetDirectory, name), content, new UTF8Encoding(false));
            }
            return pieces.Length;
        }

        public static bool TrySourceFile(string name, IReadOnlyDictionary<string, DocumentMeta> documents, out string sourceFile) =>
            TrySourceFile(name, documents, out sourceFile, out _);

        public static bool TrySourceFile(string name, IReadOnlyDictionary<string, DocumentMeta> documents, out string sourceFile, out int? messageNumber)
        {
            sourceFile = "";
            messageNumber = null;
            if (!Flat(name)) return false;
            if (documents.ContainsKey(name)) { sourceFile = name; return true; }
            var match = PassageName.Match(name);
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number) || number <= 0
                || !int.TryParse(match.Groups["part"].Value, out var part) || part <= 0) return false;
            var source = match.Groups["source"].Value;
            if (!Flat(source) || !documents.TryGetValue(source, out var meta)) return false;
            if (meta.Sha256.Length > 0 && (meta.Sha256.Length < 24
                || !meta.Sha256.StartsWith(match.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase))) return false;
            sourceFile = source;
            messageNumber = number;
            return true;
        }

        private static bool Flat(string name) => !string.IsNullOrWhiteSpace(name) && name is not "." and not ".."
            && name.IndexOfAny(['/', '\\', ':']) < 0 && Path.GetFileName(name) == name;

        private static string Limit(string value, int length) => value.Length <= length ? value
            : value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];
    }
}
