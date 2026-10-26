using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using SessionMemory.Clients;
using SessionMemory.Clients.Codex;
using SessionMemory.Clients.Copilot;
using SessionMemory.Clients.Synthetic;
using SessionMemory.Clients.VsCode;
using SessionMemory.Commands;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        public static int Run()
        {
            TranscriptChecks();
            PassageChecks();

            SelectionChecks();
            CurrentSessionChecks();
            FinderChecks();
            FilterChecks();

            WatcherChecks();
            CliChecks();
            CommandChecks();

            CorpusRefactorChecks();
            InfrastructureRefactorChecks();
            FinderRefactorChecks();
            ParserRefactorChecks();
            JsonRefactorChecks();

            var temporary = Directory.CreateTempSubdirectory("session-memory-test-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            CaptureChecks(temporary.FullName);
            Console.WriteLine("{\"ok\": true}");
            return 0;
        }

        private static void TranscriptChecks()
        {
            // Verbatim by design: the owner chose to index credentials too, because the corpus stays local.
            var (content, kept, _) = Transcript.Render(new Document("test", "test", "session", "https://user:pass@example.test/x",
                [new Message("1", "user", "api_key=abcdefghijk123456", 0, "")]));
            Check(kept == 1 && content.Contains("user:pass@") && content.Contains("api_key=abcdefghijk123456"), "renders text verbatim");
            Check(Transcript.SplitText(new string('a', 2000)).Select(part => part.Length).SequenceEqual([900, 900, 200]), "splits long text");
            Check(Transcript.SplitText(string.Concat(Enumerable.Repeat("\U0001F600", 950))).Length == 2, "splits by code point");
            Check(Transcript.Normalize("a  \r\n\r\n\r\n\r\n\r\nb\u001f") == "a\n\n\nb", "normalizes like Python");
            Check(Transcript.IsoFromMs(1792588777123) == "2026-10-21T13:19:37.123+00:00", "formats millisecond timestamps");
        }

        private static void PassageChecks()
        {
            const string passage = "e: Fix login\nmessage_id: m1\nrole: assistant\ntimestamp: 2026-10-20T19:32:22.000Z\nsource_ref: x\ntext:\nThe fix is up.\n\n" +
                                   "## Message 000013 part 1/1\nclient: c\nsession_id: s\ntitle: t\nmessage_id: m2\nrole: user\ntimestamp: 2026-10-20T19:40:00Z\nsource_ref: x\ntext:\n" +
                                   "Thanks\nrole: not a header here\n\n## Message 000014 part 1/1\nclient: c\nsess";
            var segments = Compact.Segments(passage);
            Check(segments is [{ Text: "The fix is up." }, _] && Compact.Label(segments[0]) == "[assistant 2026-10-20 19:32]"
                                                              && Compact.Label(segments[1]) == "[#13 user 2026-10-20 19:40]" && segments[1].Text == "Thanks\nrole: not a header here",
                "compacts passages that start and end inside headers");
            var continued = Compact.Segments("rest of a sentence.\n\n## Message 000002 part 1/1\nrole: user\ntimestamp: 2026-10-20T19:40:00Z\ntext:\nNext");
            Check(continued.Length == 2 && Compact.Label(continued[0]) == "[...]" && continued[0].Text == "rest of a sentence.", "keeps text that continues a message");
            Check(Compact.Plain("It\u2019s caf\u00e9 \u2014 ok \U0001F600") == "It's caf\u00e9 - ok ", "writes plain text");
            var clipped = Compact.Clip(new string('a', 100) + "X" + new string('b', 100), 30, 100);
            Check(clipped.Contains('X') && clipped.StartsWith("[90 chars before]", StringComparison.Ordinal), "clips around a match");
            var (titled, _, _) = Transcript.Render(new Document("test", "test", "session", "ref", [new Message("1", "user", "hi", 0, "")])
            {
                Title = "Add AI\nto  Pathway"
            });
            Check(titled.Split("title: Add AI to Pathway").Length == 3, "puts the title in the header and every passage");
        }

        private static void SelectionChecks()
        {
            var documents = new Dictionary<string, DocumentMeta>
            {
                ["a.md"] = new()
                {
                    Sha256 = "1"
                },
                ["b.md"] = new()
                {
                    Sha256 = "2"
                },
                ["c.md"] = new()
                {
                    Sha256 = "3",
                    SessionId = Indexer.CanaryId
                }
            };
            var recent = Indexer.RecentDocuments(documents, new Dictionary<string, string>
            {
                ["a.md"] = "1",
                ["b.md"] = "old",
                ["c.md"] = "3"
            });
            Check(recent.Keys.Order().SequenceEqual(["b.md", "c.md"]), "selects changed documents and the canary");
            List<JsonObject> ranked =
            [
                Hit("b.md", "session-memory"), Hit("b.md", "session-memory-recent"), Hit("a.md", "session-memory"), Hit("gone.md", "session-memory"),
                Hit("gone.md", "session-memory-recent")
            ];
            var filtered = Indexer.Filter(ranked, ["recent", "archive"], new HashSet<string>
            {
                "b.md"
            }, new HashSet<string>
            {
                "a.md",
                "b.md"
            }, 5);
            Check(filtered.Select(hit => $"{hit["tier"].AsString()}:{hit["file"].AsString()}").SequenceEqual(["recent:b.md", "archive:a.md"]),
                "drops stale archive copies and removed sessions, keeping LEANN's order");
            Check(ExclusionService.IsExcluded("ca7418a7-e5e7:subagent:x", ["ca7418a7"]) && !ExclusionService.IsExcluded("ca7418a8-0000", ["ca7418a7"]),
                "matches exclusions by prefix");
            var missingRoot = Path.Combine(Path.GetTempPath(), "session-memory-no-such-root");
            using var services = Services(Path.Combine(missingRoot, "home"), missingRoot);
            var exclusions = services.GetRequiredService<ExclusionService>();
            Check(exclusions.ForSearch(false, "19815249-ee80").SequenceEqual(["19815249-ee80"]), "searches leave out the current session");
            Check(exclusions.ForSearch(true, "19815249-ee80").IsEmpty, "--include-current keeps the current session");
            Check(exclusions.ForSearch(false, "abc").IsEmpty, "ignores a current session ID too short to be safe");
            var sessions = new Dictionary<string, DocumentMeta>
            {
                ["parent.md"] = new()
                {
                    SessionId = "abcdef12-3456"
                },
                ["subagent.md"] = new()
                {
                    SessionId = "ABCDEF12-3456:subagent:x"
                },
                ["other.md"] = new()
                {
                    SessionId = "99999999-0000"
                }
            };
            Check(Indexer.Visible(sessions, [], "abcdef12").Order().SequenceEqual(["parent.md", "subagent.md"])
                  && Indexer.Visible(sessions, ["99999999"], null).Order().SequenceEqual(["parent.md", "subagent.md"])
                  && Indexer.Visible(sessions, ["abcdef12"], "abcdef12").Count == 0, "narrows a search to one session and its subagents");
        }

        private static void FinderChecks()
        {
            var command = CodexClient.ItemInput("commandExecution", "{\"command\":\"rg RabbitMQ\",\"aggregatedOutput\":\"noise only\"}");
            Check(command.Contains("rg RabbitMQ") && !command.Contains("noise only"), "finds Codex tool inputs without their output");
            Check(Finder.LikeEscape("50%_x'y") == @"50\%\_x''y", "escapes LIKE patterns");
            var drill = new Finder.Scan(["a", "b"], "s1");
            drill.Note("x", "s1-parent", "a b", "2026-01-02T00:00:00", () => new Finder.Snippet("", "w", "second", 2));
            drill.Note("x", "s1-parent", "a b", "2026-01-01T00:00:00", () => new Finder.Snippet("", "w", "first", 2));
            drill.Note("x", "s2-other", "a b", "2026-01-03T00:00:00", () => new Finder.Snippet("", "w", "other", 2));
            Check(drill.Sessions.Keys.Single().SessionId == "s1-parent" && drill.Sessions.Values.Single().All.Count == 2, "drills into one session's excerpts");
            var escaped = new Finder.Scan(["C:\\hcw", "say \"hi\""]);
            escaped.Note("x", "s", """{"cwd":"C:\\hcw","text":"say \"hi\""}""", "2026-01-01T00:00:00", () => null);
            Check(escaped.Sessions.Values.SingleOrDefault()?.Mentions.All(count => count == 1) == true, "finds terms that JSON escapes");
            Check(!Finder.Scan.HasWord("my param and program", "RAM") && Finder.Scan.HasWord("16 GB RAM", "ram")
                                                                      && Finder.Scan.HasWord("param, then RAM, (RAM)", "RAM") && Finder.Scan.HasWord("{\"t\":\"a\\nRAM\"}", "RAM")
                                                                      && !Finder.Scan.HasWord("RAM_SIZE", "RAM") && Finder.Scan.HasWord(@"cd C:\hcw\src", "C:\\hcw")
                                                                      && !Finder.Scan.HasWord("XC:\\hcw", "C:\\hcw"), "matches whole words");
            var whole = new Finder.Scan(["RAM"]);
            var partial = new Finder.Scan(["RAM"], partial: true);
            foreach (var scan in new[]
                     {
                         whole, partial
                     }) scan.Note("x", "s", "set the param", "2026-01-01T00:00:00", () => null);
            Check(!whole.Matches("set the param") && whole.Sessions.Count == 0 && partial.Matches("set the param") && partial.Sessions.Count == 1,
                "matches substrings with --partial");
            var self = new Finder.Scan(["RAM"]);
            self.Note("x", "s", "& \"C:\\x\\SessionMemory.exe\" find RAM --top 3", "2026-01-01T00:00:00", () => null);
            self.Note("x", "s", "powershell -File Search-SessionMemory.ps1 -Query \"RAM for the VM\"", "2026-01-01T00:00:00", () => null);
            self.Note("x", "s", "SessionMemory show abc RAM", "2026-01-01T00:00:00", () => null);
            self.Note("x", "s", """{"command":"& \"C:\\x\\SessionMemory.exe\" find RAM"}""", "2026-01-01T00:00:00", () => null);
            Check(self.Sessions.Count == 0 && !self.Matches("SessionMemory search RAM") && self.Matches("SessionMemory found 4 GB RAM"),
                "skips the memory tool's own invocations");
            Check(Finder.Scan.IsSelfInvocation("$exe=\"C:\\x\\SessionMemory.exe\"; & $exe find RAM --top 3")
                  && Finder.Scan.IsSelfInvocation("dotnet run --project SessionMemory\\SessionMemory.csproj --no-build -- find RAM")
                  && !Finder.Scan.IsSelfInvocation("The SessionMemory tool can find sessions quickly."), "recognizes invocations through a variable or dotnet run");
            using (var toolUse = JsonDocument.Parse("{\"type\":\"tool_use\",\"name\":\"Write\",\"input\":{\"file_path\":\"C:\\\\x.md\",\"content\":\"threat model " +
                                                    new string('a', 400) + " T05 RabbitMQ threat merged " + new string('b', 400) + "\"}}"))
            {
                var excerpt = Finder.Excerpt(toolUse.RootElement, "", ["RabbitMQ", "threat"]);
                Check(excerpt is { Where: "Write.input.content" } && excerpt.Text.Contains("T05 RabbitMQ threat merged") && excerpt.Text.Length < 420,
                    "excerpts the matching tool input");
            }
            using (var page = JsonDocument.Parse("{\"content\":\"RabbitMQ in green on the diagram " + new string('x', 600) + " 22 removed: RabbitMQ encryption at rest, a kept threat\"}"))
            {
                var excerpt = Finder.Excerpt(page.RootElement, "", ["RabbitMQ", "threat"]);
                Check(excerpt is { Score: 2 } && excerpt.Text.Contains("22 removed: RabbitMQ") && !excerpt.Text.Contains("green"), "centres excerpts where the terms meet");
            }
        }

        private static void FilterChecks()
        {
            var single = Indexer.Filter([Hit("a.md", null)], ["archive"], new HashSet<string>(), new HashSet<string>(), 5);
            Check(single.Single()["tier"].AsString() == "archive", "labels a one-index search");
            var chatty = Indexer.Filter([.. Enumerable.Range(0, 5).Select(_ => Hit("a.md", null)), Hit("c.md", null)], ["archive"], new HashSet<string>(), new HashSet<string>(), 5);
            Check(chatty.Select(hit => hit["file"].AsString()).SequenceEqual(["a.md", "a.md", "a.md", "c.md"]), "caps hits per session");
        }

        private static void WatcherChecks()
        {
            var now = DateTime.UtcNow;
            Check(Watcher.RecentDue(false, "s", "s", now, now, null, false), "builds a missing recent index");
            Check(!Watcher.RecentDue(true, "s", "s", now, DateTime.MinValue, null, false), "skips an unchanged recent index");
            Check(Watcher.RecentDue(true, "s", "t", now, now.AddSeconds(-100), now.AddSeconds(-100), false), "builds after a quiet period");
            Check(!Watcher.RecentDue(true, "s", "t", now, now.AddSeconds(-10), now.AddSeconds(-100), false), "debounces active sessions");
            Check(Watcher.RecentDue(true, "s", "t", now, now.AddSeconds(-10), now.AddSeconds(-400), false), "caps the debounce");
        }

        // The command-line rails: options, transcript headings, exclusions and session lookup against a manifest in a temporary root.
        private static void CliChecks()
        {
            var arguments = new CommandArguments(["search", "--top-k", "12", "q"]);
            var parsed = arguments.Number("--top-k", 8);
            var absent = new CommandArguments(["search"]).Number("--top-k", 8);
            var bad = new CommandArguments(["search", "--top-k", "x"]).Number("--top-k", 8);
            var huge = new CommandArguments(["search", "--top-k", "99999999999"]).Number("--top-k", 8);
            Check(parsed.GetValueOrDefault() == 12 && arguments is { Verb: "search", Operands: ["q"] } && absent.GetValueOrDefault() == 8, "reads whole-number options");
            Check(bad.IsFailure && bad.Error.ExitCode == 2 && bad.Error.Message.StartsWith("'x' is not a whole number for --top-k.\nUsage: ", StringComparison.Ordinal)
                  && huge.IsFailure && huge.Error.Message.StartsWith("'99999999999' is not a whole number for --top-k.\n", StringComparison.Ordinal), "rejects a bad number with the usage");
            CliArgumentChecks();
            var overflow = Compact.Segments("## Message 99999999999 part 1/1\nbody");
            var foreign = Compact.Segments("## Message \u0663 part 1/1\nbody");
            Check(overflow is [{ Number: 0, Role: "", Text: "## Message 99999999999 part 1/1\nbody" }] && foreign is [{ Number: 0, Role: "" }],
                "reads a heading whose number is not a plain int as text");
            var temporary = Directory.CreateTempSubdirectory("session-memory-cli-test-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            CliSessions(temporary.FullName);
            using var services = Services(Path.Combine(temporary.FullName, "home"), temporary.FullName);
            CliExclusionChecks(services.GetRequiredService<ExclusionService>());
            CliFindSessionChecks(services.GetRequiredService<SessionReader>(), temporary.FullName);
            CliShowChecks(services.GetRequiredService<SessionReader>());
        }

        // A flag or an option is taken out at its first occurrence; an option without a value stays among the operands.
        private static void CliArgumentChecks()
        {
            var arguments = new CommandArguments(["find", "--json", "x", "--json", "--top"]);
            Check(arguments.Flag("--json") && !arguments.Flag("--partial") && arguments.Option("--top").HasNoValue && arguments.Operands is ["x", "--json", "--top"],
                "takes the first occurrence of a flag and leaves an option without a value");
            var root = new CommandArguments(["find", "--root", "C:\\data", "x", "--root"]);
            Check(root.Option("--root").GetValueOrDefault() == "C:\\data" && root is { Verb: "find", Operands: ["x", "--root"] }, "takes an option and its value out");
            Check(new CommandArguments(["show", "x", "/?"]).AsksForHelp && new CommandArguments(["-h"]).AsksForHelp && !new CommandArguments(["show", "-help"]).AsksForHelp
                  && new CommandArguments([]) is { Verb: null, Operands.IsEmpty: true }, "recognizes a request for help and a missing verb");
        }

        // Sessions that share ID prefixes: a parent with a subagent, a sibling, an exact ID that prefixes a longer one, and twelve alike.
        private static void CliSessions(string root)
        {
            var documents = new Dictionary<string, DocumentMeta>
            {
                ["parent.md"] = CliSession("abcdef12-3456", "codex", "Parent", ""),
                ["subagent.md"] = CliSession("abcdef12-3456:subagent:x", "codex", "Subagent", ""),
                ["sibling.md"] = CliSession("abcdef12-9999", "claude-code", "Sibling", ""),
                ["exact.md"] = CliSession("fedcba98-7654", "codex", "Exact", "2026-10-20T19:40:00Z"),
                ["longer.md"] = CliSession("fedcba98-7654-extra", "codex", "Longer", "")
            };
            foreach (var number in Enumerable.Range(0, 12)) documents[$"many{number}.md"] = CliSession($"11111111-{number:D2}", "codex", "", "");
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest
            {
                Documents = documents
            });
            Directory.CreateDirectory(Path.Combine(root, "corpus"));
            File.WriteAllText(Path.Combine(root, "corpus", "exact.md"), "## Message 000001 part 1/1\nrole: user\ntimestamp: 2026-10-20T19:40:00Z\ntext:\nThe retry limit is 5.\n");
        }

        private static DocumentMeta CliSession(string sessionId, string client, string title, string lastActivity) => new()
        {
            SessionId = sessionId,
            Client = client,
            Title = title,
            LastActivity = lastActivity
        };

        // The ID is checked before anything is read; nothing is saved or printed until the caller taps the result.
        private static void CliExclusionChecks(ExclusionService exclusions)
        {
            const string canary = "The canary session validates every index build and cannot be excluded.";
            Check(CliFails(exclusions.Exclude(" abc "), "Give at least 8 characters of the session ID.", 2)
                  && CliFails(exclusions.Include("abcdef1"), "Give at least 8 characters of the session ID.", 2), "requires 8 characters of a session ID");
            Check(CliFails(exclusions.Exclude("leann-memory"), canary, 2) && CliFails(exclusions.Include("LEANN-MEMORY-CANARY"), canary, 2),
                "never excludes or includes the canary");
            Check(CliFails(exclusions.Exclude("zzzzzzzz-none"), "No indexed session starts with 'zzzzzzzz-none'.", 1), "reports an unknown session to exclude");
            Check(CliFails(exclusions.Include("zzzzzzzz-none"), "'zzzzzzzz-none' is not excluded.", 1), "reports a session that is not excluded");
            var excluded = exclusions.Exclude(" abcdef12-3456 ");
            Check(excluded.IsSuccess && excluded.Value.Exclusions.SequenceEqual(["abcdef12-3456"])
                                     && excluded.Value.Report.SequenceEqual(["Excluded abcdef12-3456 (codex) Parent", "Excluded abcdef12-3456:subagent:x (codex) Subagent"])
                                     && !File.Exists(exclusions.FilePath), "excludes a session and its subagents without saving");
            exclusions.Save(excluded.Value);
            Check(exclusions.Load().SequenceEqual(["abcdef12-3456"]), "saves the checked exclusions");
            Store.Save(exclusions.FilePath, new List<string>
            {
                "ABCDEF12-3456"
            });
            var again = exclusions.Exclude("abcdef12-3456");
            var included = exclusions.Include("abcdef12-3456");
            Check(again.IsSuccess && again.Value.Exclusions.SequenceEqual(["ABCDEF12-3456"]) && included.IsSuccess && included.Value.Exclusions.IsEmpty
                  && included.Value.Report.SequenceEqual(["Included abcdef12-3456 again."]) && exclusions.Load().Count == 1, "includes an excluded session again");
        }

        // An exact ID wins, then the only session that is not a subagent; otherwise the prefix is unknown or ambiguous.
        private static void CliFindSessionChecks(SessionReader reader, string root)
        {
            using var elsewhere = Services(Path.Combine(root, "home"), Path.Combine(root, "no-such-root"));
            Check(CliFile(reader.FindSession("FEDCBA98-7654")) == "exact.md" && CliFile(reader.FindSession("abcdef12-34")) == "parent.md"
                                                                         && CliFile(reader.FindSession("abcdef12-3456:sub")) == "subagent.md", "resolves an ID prefix to one session");
            Check(CliFails(reader.FindSession("zzzzzzzz"), "No indexed session starts with 'zzzzzzzz'.", 2)
                  && CliFails(elsewhere.GetRequiredService<SessionReader>().FindSession("abcdef12"), "No indexed session starts with 'abcdef12'.", 2), "reports an unknown session");
            Check(CliFails(reader.FindSession("abcdef12"), "Several sessions match; give more of the ID:\n  abcdef12-3456 | codex | Parent\n" +
                                                           "  abcdef12-3456:subagent:x | codex | Subagent\n  abcdef12-9999 | claude-code | Sibling", 2), "lists the sessions a prefix matches");
            var many = reader.FindSession("11111111");
            Check(many.IsFailure && many.Error.ExitCode == 2 && many.Error.Message.Split('\n').Length == 11, "lists at most ten sessions");
        }

        private static void CliShowChecks(SessionReader reader)
        {
            var shown = reader.Show(new ShowRequest
            {
                Id = "fedcba98-7654",
                Around = "retry",
                Max = 1
            });
            Check(shown.GetValueOrDefault("") == "== fedcba98-7654 | codex | 1 messages | last 2026-10-20 19:40 | Exact\n1 matching messages\n" +
                "[#1 user 2026-10-20 19:40] The retry limit is 5.\n", "shows the matching messages of a session");
            var unreadable = reader.Show(new ShowRequest
            {
                Id = "abcdef12-34"
            });
            Check(unreadable.IsFailure && unreadable.Error.ExitCode == 2 && unreadable.Error.Message.StartsWith("FileNotFoundException: ", StringComparison.Ordinal),
                "reports a transcript it cannot read");
            var invalid = reader.Show(new ShowRequest
            {
                Id = "fedcba98-7654",
                Around = "("
            });
            Check(invalid.IsFailure && invalid.Error.ExitCode == 2 && invalid.Error.Message.StartsWith("Invalid pattern '(' at offset 1.", StringComparison.Ordinal),
                "reports an invalid pattern");
        }

        private static bool CliFails<T>(Result<T, CliError> result, string message, int exitCode) => result.IsFailure && result.Error == new CliError(message, exitCode);

        // The corpus file of the session found, or the failure message.
        private static string CliFile(Result<KeyValuePair<string, DocumentMeta>, CliError> found) => found.Match(pair => pair.Key, error => error.Message);

        private static void CaptureChecks(string root)
        {
            var source = Path.Combine(root, "synthetic", "canary.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            var initial = Line("u1", "user", "What is the retry limit?") + Line("a1", "assistant", "The retry limit is 2.");
            File.WriteAllText(source, initial);
            var synthetic = RegisteredClients().Single(client => client.Name == SyntheticTranscripts.FamilyName);
            using var services = Services(Path.Combine(root, "home"), root, [synthetic]);
            var capture = services.GetRequiredService<Corpus>();
            var first = capture.Capture();
            File.AppendAllText(source, Line("a2", "assistant", "Correction: the current retry limit is 5 and supersedes 2."));
            var second = capture.Capture();
            var third = capture.Capture();
            Check(first.Changed && second.Changed && !third.Changed, "detects an appended message once");
            Check(first.Manifest.CorpusHash != second.Manifest.CorpusHash, "changes the corpus hash");
            File.WriteAllText(source, initial + "{partial");
            var removed = capture.Capture();
            var corpus = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "corpus"), "*.md").Select(File.ReadAllText));
            Check(removed.Changed && !corpus.Contains("supersedes 2") && corpus.Contains("The retry limit is 2."), "drops removed messages");
            Check(removed.Manifest.Families["synthetic"].Stats["partial_trailing"] == 1, "ignores a partial trailing record");
            var reader = services.GetRequiredService<SessionReader>();
            var shown = reader.Show(new ShowRequest
            {
                Id = Indexer.CanaryId
            });
            var matched = reader.Show(new ShowRequest
            {
                Id = "leann-memory",
                Around = "retry limit is"
            });
            Check(shown.IsSuccess && shown.Value.Contains("[#2 assistant 2026-10-21 00:00] The retry limit is 2.") && matched.IsSuccess
                  && matched.Value.Contains("1 matching messages") && !matched.Value.Contains("#1 user"), "shows a session's last or matching messages");
            var exclusions = services.GetRequiredService<ExclusionService>();
            Store.Save(exclusions.FilePath, new List<string>
            {
                Indexer.CanaryId
            });
            var excluded = capture.Capture();
            Check(excluded.Manifest.DocumentCount == 0 && !Directory.EnumerateFiles(Path.Combine(root, "corpus"), "*.md").Any(), "removes excluded sessions from the corpus");
            File.Delete(exclusions.FilePath);
            Check(capture.Capture().Manifest.DocumentCount == 1, "brings an included session back");

            var chat = Path.Combine(root, "chat.jsonl");
            File.WriteAllLines(chat,
            [
                """{"kind":0,"v":{"requests":[],"pendingRequests":[1,2],"parts":["a","partial","stale"],"title":"t"}}""",
                """{"kind":2,"k":["requests"],"v":[{"message":{"text":"x"}}]}""",
                """{"kind":1,"k":["requests",0,"result"],"v":{"ok":true}}""",
                """{"kind":2,"k":["pendingRequests"],"i":0}""",
                """{"kind":2,"k":["parts"],"i":1,"v":["final"]}""",
                """{"kind":3,"k":["title"]}"""
            ]);
            var (state, errors) = VsCodeClient.Replay(chat, new JsonlStats());
            Check(errors == 0 && state["requests"]![0]!["result"]!["ok"]!.GetValue<bool>() && state["pendingRequests"]!.AsArray().Count == 0
                  && state["parts"]!.AsArray().Select(part => part!.GetValue<string>()).SequenceEqual(["a", "final"]) && state["title"] is null,
                "replays VS Code edit logs");

            var compressed = Path.Combine(root, "rollout.jsonl.zst");
            using (var compressor = new ZstdSharp.Compressor())
                File.WriteAllBytes(compressed, compressor.Wrap("{\"a\":1}\n{\"a\":2}\n"u8).ToArray());
            Check(Jsonl.Read(compressed, new JsonlStats()).Count() == 2, "reads Zstandard-compressed rollouts");
            Check(CodexClient.StripInjectedContext("<environment_context>\n  <cwd>C:\\x</cwd>\n</environment_context>") == ""
                  && CodexClient.StripInjectedContext("<recommended_plugins>a</recommended_plugins>\n<environment_context>b</environment_context>\n") == ""
                  && CodexClient.StripInjectedContext("# AGENTS.md instructions for C:\\x\n\n<INSTRUCTIONS>rules</INSTRUCTIONS>\nFix the bug") == "Fix the bug"
                  && CodexClient.StripInjectedContext("<b>bold</b> stays") == "<b>bold</b> stays"
                  && CodexClient.StripInjectedContext("<send_user_message_question_reply>yes</send_user_message_question_reply>")
                  == "<send_user_message_question_reply>yes</send_user_message_question_reply>", "strips injected Codex context only");
            var workspace = Path.Combine(root, "workspace.yaml");
            File.WriteAllText(workspace, "id: x\ncwd: C:\\x\nname: 'It''s done'\nuser_named: false\n");
            Check(CopilotClient.WorkspaceName(workspace) == "It's done" && CopilotClient.WorkspaceName(Path.Combine(root, "missing.yaml")) == "", "reads Copilot session names");

            var fresh = Path.Combine(root, "fresh");
            SyntheticTranscripts.EnsureCanary(fresh);
            var seeded = synthetic.Parse(new ClientContext(fresh, fresh)).Documents.Single();
            Check(seeded is { SessionId: Indexer.CanaryId, Messages.Length: 3 }, "seeds the canary on a new machine");
        }

        private static string Line(string id, string role, string text) =>
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["session_id"] = Indexer.CanaryId,
                ["message_id"] = id,
                ["role"] = role,
                ["timestamp"] = "2026-10-21T00:00:00Z",
                ["text"] = text
            }) + "\n";

        private static JsonObject Hit(string file, string? index) => new()
        {
            ["file"] = file,
            ["index"] = index
        };

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"Self-test failed: {name}");
        }
    }
}
