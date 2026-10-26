using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private static void FinderRefactorChecks()
        {
            FinderTermChecks();
            FinderKeyChecks();
            FinderSinceChecks();
            FinderExcerptChecks();
            var temporary = Directory.CreateTempSubdirectory("session-memory-finder-test-");
            try
            {
                var request = FinderStores(temporary.FullName);
                FinderOutputChecks(request);
                FinderWindowChecks(request);
                FinderCurrentChecks(request);
            }
            finally
            {
                temporary.Delete(recursive: true);
            }
        }

        private static void FinderTermChecks()
        {
            var none = new Finder.Scan([]);
            none.Note("x", "s", "anything at all", "2026-01-01T00:00:00", () => new Finder.Snippet("", "w", "never", 0));
            Check(none.Terms.IsEmpty && !none.Mentions("anything at all") && none.Matches("anything at all") && none.Sessions.Count == 0,
                "scans with no terms note nothing");
            var unset = new Finder.Scan(default);
            unset.Note("x", "s", "anything", "2026-01-01T00:00:00", () => null);
            Check(unset.Terms is { IsDefault: false, IsEmpty: true } && !unset.Mentions("anything") && unset.Sessions.Count == 0, "treats unset terms as none");
            Check(Finder.Scan.Forms("plain") is ["plain"] && Finder.Scan.Forms("C:\\x \"y\"").SequenceEqual(["C:\\x \"y\"", "C:\\\\x \\\"y\\\""]),
                "adds a JSON-escaped form only when it differs");
            var counted = new Finder.Scan(["alpha", "beta", "gamma"]);
            counted.Note("x", "s", "alpha beta", "2026-01-01T00:00:00", () => throw new InvalidOperationException("excerpt for a partial match"));
            counted.Note("x", "s", "gamma only", "2026-01-02T00:00:00", () => throw new InvalidOperationException("excerpt for a partial match"));
            counted.Note("x", "s", "alpha beta gamma", "2026-01-03T00:00:00", () => new Finder.Snippet("", "w", "all", 3));
            counted.Note("x", "s", "alphabet soup", "2026-01-04T00:00:00", () => null);
            var tally = counted.Sessions.Values.Single();
            Check(tally is { Mentions: [2, 2, 2], LastMention: "2026-01-03T00:00:00", LastAllTerms: "2026-01-03T00:00:00", Best: { Text: "all", At: "2026-01-03T00:00:00" } },
                "tallies each term per record and excerpts only records holding all");
        }

        private static void FinderKeyChecks()
        {
            var key = new Finder.SessionKey("copilot", "s1");
            var renamed = key with
            {
                SessionId = "s2"
            };
            var counts = new Dictionary<Finder.SessionKey, int>
            {
                [key] = 1
            };
            counts[new Finder.SessionKey("copilot", "s1")]++;
            counts[new Finder.SessionKey("codex", "s1")] = 5;
            Check(counts.Count == 2 && counts[key] == 2 && key == new Finder.SessionKey("copilot", "s1") && key != new Finder.SessionKey("Copilot", "s1")
                  && renamed is { Client: "copilot", SessionId: "s2" } && renamed != key, "keys sessions by client and ID, case-sensitively");
            var first = new Finder.Scan(["RAM"]);
            var second = new Finder.Scan(["RAM"]);
            first.Note("claude", "s1", "16 GB RAM", "2026-01-01T00:00:00", () => new Finder.Snippet("", "w", "older", 1));
            second.Note("claude", "s1", "more RAM", "2026-01-02T00:00:00", () => new Finder.Snippet("", "w", "newer", 1));
            second.Note("codex", "s1", "RAM again", "2026-01-03T00:00:00", () => new Finder.Snippet("", "w", "codex", 1));
            var merged = new Dictionary<Finder.SessionKey, Finder.Found>();
            first.MergeInto(merged);
            second.MergeInto(merged);
            var claude = merged[new Finder.SessionKey("claude", "s1")];
            Check(merged.Count == 2 && claude is { Mentions: [2], LastMention: "2026-01-02T00:00:00", Best.Text: "newer" },
                "merges one session's results from several scans");
        }

        private static void FinderSinceChecks()
        {
            var since = new Finder.Scan(["RAM"], since: new DateTimeOffset(2026, 2, 1, 1, 0, 0, TimeSpan.FromHours(1)));
            since.Note("x", "before", "RAM", "2026-01-31T23:59:59", () => null);
            since.Note("x", "unparsed", "RAM", "", () => null);
            since.Note("x", "at", "RAM", "2026-02-01T00:00:00", () => null);
            since.Note("x", "after", "RAM", "2026-03-01T00:00:00", () => null);
            Check(since.Sessions.Keys.Select(key => key.SessionId).Order().SequenceEqual(["after", "at"]), "keeps records at or after --since in UTC");
            var always = new Finder.Scan(["RAM"]);
            always.Note("x", "unparsed", "RAM", "", () => null);
            Check(always.Sessions.Count == 1, "keeps records without a time when no --since is given");
        }

        private static void FinderExcerptChecks()
        {
            using var tool = JsonDocument.Parse("""{"name":"Write","input":{"content":"the quokka met a zebra"},"note":"zebra only"}""");
            ImmutableArray<string> terms = ["zebra", "quokka"];
            Check(Finder.Excerpt(tool.RootElement, "", terms) is { Where: "Write.input.content", Score: 2, Text: "the quokka met a zebra" },
                "excerpts the value holding the most terms");
            Check(Finder.Excerpt(tool.RootElement, "label", []) is null && Finder.Excerpt(tool.RootElement, "", ["absent"]) is null,
                "finds no excerpt without a matching term");
            using var bare = JsonDocument.Parse("\"QUOKKA in capitals\"");
            Check(Finder.Excerpt(bare.RootElement, "data", ["quokka"]) is { Where: "data", Score: 1, Text: "QUOKKA in capitals" },
                "matches excerpt terms ignoring case and keeps the label");
        }

        // Copilot CLI sessions with messages, a tool result, and the result of the memory tool's own find; one of them is indexed.
        private static FindRequest FinderStores(string directory)
        {
            var root = Path.Combine(directory, "root");
            var home = Path.Combine(directory, "home");
            FinderEvents(home, "aaaaaaaa-1111",
            [
                """{"type":"user.message","timestamp":"2026-01-01T00:00:00Z","data":{"content":"an old zebra note"}}""",
                """{"type":"user.message","timestamp":"2026-03-01T00:00:00Z","data":{"content":"a recent zebra and quokka"}}""",
                """{"type":"assistant.message","timestamp":"2026-03-04T00:00:00Z","data":{"content":"zebra beside a quokka"}}"""
            ]);
            FinderEvents(home, "bbbbbbbb-2222",
            [
                """{"type":"tool.execution_start","timestamp":"2026-03-02T00:00:00Z","data":{"toolCallId":"call-1","toolName":"powershell","arguments":{"command":"Get-ChildItem"}}}""",
                """{"type":"tool.execution_complete","timestamp":"2026-03-02T00:00:01Z","data":{"toolCallId":"call-1","result":{"content":"zebra.txt quokka.txt"}}}"""
            ]);
            FinderEvents(home, "cccccccc-3333",
            [
                """{"type":"tool.execution_start","timestamp":"2026-03-03T00:00:00Z","data":{"toolCallId":"call-2","toolName":"powershell","arguments":{"command":"& \"C:\\x\\SessionMemory.exe\" find zebra quokka"}}}""",
                """{"type":"tool.execution_complete","timestamp":"2026-03-03T00:00:01Z","data":{"toolCallId":"call-2","result":{"content":"zebra and quokka in another session"}}}"""
            ]);
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest
            {
                Documents =
                {
                    ["copilot-bbbbbbbb.md"] = new DocumentMeta
                    {
                        Family = "copilot",
                        Client = "copilot-cli",
                        SessionId = "bbbbbbbb-2222",
                        Title = "Listing files"
                    }
                }
            });
            return new FindRequest
            {
                Root = root,
                Home = home,
                Terms = ["zebra", "quokka"],
                Json = true
            };
        }

        private static void FinderEvents(string home, string sessionId, ImmutableArray<string> lines)
        {
            var directory = Path.Combine(home, ".copilot", "session-state", sessionId);
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, "events.jsonl"), lines);
        }

        private static void FinderOutputChecks(FindRequest request)
        {
            var plain = FinderRun(request);
            var messages = plain["sessions"]!.AsArray().Single()!;
            Check(plain["matching_sessions"]!.GetValue<int>() == 1 && plain["since"] is null && messages["session_id"]!.GetValue<string>() == "aaaaaaaa-1111"
                  && messages["client"]!.GetValue<string>() == "copilot" && messages["title"]!.GetValue<string>() == "" && messages["transcript"] is null
                  && messages["mentions"]!["zebra"]!.GetValue<int>() == 3 && messages["mentions"]!["quokka"]!.GetValue<int>() == 2
                  && messages["snippets"]![0]!["where"]!.GetValue<string>() == "assistant.message.content", "finds Copilot messages but not tool results by default");
            var outputs = FinderRun(request with
            {
                Outputs = true
            });
            var sessions = outputs["sessions"]!.AsArray();
            var tool = sessions[1]!;
            Check(outputs["matching_sessions"]!.GetValue<int>() == 2 && sessions[0]!["session_id"]!.GetValue<string>() == "aaaaaaaa-1111"
                  && tool["session_id"]!.GetValue<string>() == "bbbbbbbb-2222" && tool["client"]!.GetValue<string>() == "copilot-cli"
                  && tool["title"]!.GetValue<string>() == "Listing files"
                  && tool["transcript"]!.GetValue<string>() == Path.Combine(request.Root, "corpus", "copilot-bbbbbbbb.md")
                  && tool["snippets"]![0]!["where"]!.GetValue<string>() == "tool output content", "adds tool results with --outputs, but not the memory tool's own");
        }

        private static void FinderWindowChecks(FindRequest request)
        {
            var since = FinderRun(request with
            {
                Since = new DateTimeOffset(2026, 2, 1, 1, 0, 0, TimeSpan.FromHours(1))
            });
            Check(since["since"]!.GetValue<string>() == "2026-02-01T00:00:00" && since["sessions"]!.AsArray().Single()!["mentions"]!["zebra"]!.GetValue<int>() == 2,
                "counts only records since the given time, read as UTC");
            var later = FinderRun(request with
            {
                Since = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                Outputs = true
            });
            Check(later["matching_sessions"]!.GetValue<int>() == 0 && later["sessions"]!.AsArray().Count == 0, "finds nothing after the newest record");
            var drill = FinderRun(request with
            {
                Only = "AAAAAAAA",
                Top = 1
            });
            var drilled = FinderRun(request with
            {
                Only = "aaaaaaaa"
            });
            Check(drill["sessions"]!.AsArray().Single()!["snippets"]!.AsArray().Single()!["text"]!.GetValue<string>() == "zebra beside a quokka"
                  && drilled["sessions"]!.AsArray().Single()!["snippets"]!.AsArray().Count == 2, "drills into one session's excerpts, newest first, up to --top");
            var none = FinderRun(request with
            {
                Terms = []
            });
            var unset = FinderRun(request with
            {
                Terms = default
            });
            Check(none["terms"]!.AsArray().Count == 0 && none["matching_sessions"]!.GetValue<int>() == 0
                  && unset["terms"]!.AsArray().Count == 0 && unset["matching_sessions"]!.GetValue<int>() == 0, "finds without terms like find --top 3");
        }

        // The session running a find is left out unless it is asked for, with --include-current or by naming it.
        private static void FinderCurrentChecks(FindRequest request)
        {
            var variable = Corpus.CurrentSessionVariables[0];
            var original = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, "aaaaaaaa-1111");
            try
            {
                var skipped = FinderRun(request);
                var included = FinderRun(request with
                {
                    IncludeCurrent = true
                });
                var named = FinderRun(request with
                {
                    Only = "aaaaaaaa"
                });
                Check(skipped["matching_sessions"]!.GetValue<int>() == 0 && included["matching_sessions"]!.GetValue<int>() == 1
                      && named["matching_sessions"]!.GetValue<int>() == 1, "leaves out the current session unless asked for");
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, original);
            }
        }

        // Runs a JSON find and parses what it wrote.
        private static JsonNode FinderRun(FindRequest request)
        {
            var console = Console.Out;
            using var output = new StringWriter();
            Console.SetOut(output);
            try
            {
                Check(Finder.Run(request) == 0, "finds with exit code 0");
            }
            finally
            {
                Console.SetOut(console);
            }
            return JsonNode.Parse(output.ToString())!;
        }
    }
}
