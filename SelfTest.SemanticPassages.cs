namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private static void SemanticPassagesChecks()
        {
            var temporary = Directory.CreateTempSubdirectory("semantic-passages-test-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            var input = Path.Combine(temporary.FullName, "source.md");
            var output = Path.Combine(temporary.FullName, "passages");
            var corpus = """
                # Local session transcript
                session_id: PRIVATE-SESSION-ID
                source_ref: C:\private\source.json

                ## Message 000001 part 1/1
                role: user
                timestamp: 2026-01-01T00:00:00Z
                text:
                What happened to ProjectAlpha?

                ## Message 000002 part 1/2
                role: assistant
                timestamp: 2026-01-01T00:01:00Z
                text:
                The result was

                ## Message 000002 part 2/2
                role: assistant
                timestamp: 2026-01-01T00:01:00Z
                text:
                successful.

                ## Message 000003 part 1/1
                role: assistant
                timestamp: 2026-01-01T00:02:00Z
                text:
                """ + "\n" + string.Concat(Enumerable.Repeat("😀", 1100));
            File.WriteAllText(input, corpus);
            var count = SemanticPassages.Write(input, output, "source.md", "A project");
            var files = Directory.GetFiles(output).OrderBy(Path.GetFileName).ToArray();
            Check(count == files.Length && count > 3, "writes deterministic bounded semantic passages");
            var passages = files.Select(File.ReadAllText).ToArray();
            Check(passages.All(s => s.Length < 1400 && !s.Contains("PRIVATE-SESSION-ID") && !s.Contains(@"C:\private\source.json")),
                "omits session identity metadata and bounds embedding passages");
            var answer = passages.Single(s => s.Contains("The result was"));
            var segments = Compact.Segments(answer).Where(s => s.Number == 2).ToArray();
            Check(answer.Contains("Prior request: What happened to ProjectAlpha?")
                && segments.Length == 1 && segments[0].Number == 2 && segments[0].Role == "assistant"
                && segments[0].Timestamp == "2026-01-01T00:01:00Z"
                && segments[0].Text == "The result was\nsuccessful.", "joins multipart replies and preserves source citations");
            var hash = Transcript.Sha256(corpus.Replace("\r\n", "\n", StringComparison.Ordinal));
            var docs = new Dictionary<string, DocumentMeta> { ["source.md"] = new() { SessionId = "source", Sha256 = hash } };
            Check(files.All(f => SemanticPassages.TrySourceFile(Path.GetFileName(f), docs, out var source, out var number)
                                 && source == "source.md" && number is 1 or 2 or 3),
                "decodes every generated passage to its known source");
            var answerFile = Path.GetFileName(files.Single(f => File.ReadAllText(f).Contains("The result was")));
            Check(SemanticPassages.TrySourceFile(answerFile, docs, out _, out var answerNumber) && answerNumber == 2,
                "decodes the original message number");
            Check(SemanticPassages.TrySourceFile("source.md", docs, out var legacy, out var legacyNumber) && legacy == "source.md" && legacyNumber is null
                && !SemanticPassages.TrySourceFile("../source.md", docs, out _)
                && !SemanticPassages.TrySourceFile(@"..\source.md", docs, out _)
                && !SemanticPassages.TrySourceFile($"missing.md__h{hash[..24]}__m000001_p001.md", docs, out _)
                && !SemanticPassages.TrySourceFile($"source.md__h{hash[..24]}__m000000_p001.md", docs, out _)
                && !SemanticPassages.TrySourceFile($"source.md__h{'x'}{hash[1..24]}__m000002_p001.md", docs, out _)
                && !SemanticPassages.TrySourceFile($"source.md__h{new string('0', 24)}__m000002_p001.md", docs, out _),
                "decodes legacy names and rejects traversal and unknown sources");
            Check(SemanticPassages.Write(input, output, "source.md", "A project") == count
                && Directory.GetFiles(output).OrderBy(Path.GetFileName).Select(File.ReadAllText).SequenceEqual(passages),
                "repeated generation is deterministic");
            var canaryRoot = Path.Combine(temporary.FullName, "canary");
            Clients.Synthetic.SyntheticTranscripts.EnsureCanary(canaryRoot);
            var canary = Clients.Synthetic.SyntheticTranscripts.Parse(canaryRoot).Documents.Single();
            Check(canary.Title == Indexer.CanaryId, "the reserved validation source keeps its searchable marker as content context");
            var canaryCorpus = Path.Combine(temporary.FullName, "canary.md");
            File.WriteAllText(canaryCorpus, Transcript.Render(canary).Content);
            var canaryOutput = Path.Combine(temporary.FullName, "canary-passages");
            SemanticPassages.Write(canaryCorpus, canaryOutput, "canary.md", canary.Title);
            Check(Directory.GetFiles(canaryOutput).All(file => File.ReadAllText(file).Contains(Indexer.CanaryId)),
                "semantic staging retains the canary marker without relying on repeated identity metadata");
        }
    }
}
