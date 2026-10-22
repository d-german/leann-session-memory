using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionMemory;

static class SelfTest
{
    public static int Run()
    {
        // Verbatim by design: the owner chose to index credentials too, because the corpus stays local.
        var (content, kept, _) = Transcript.Render(new Document("test", "test", "session", "https://user:pass@example.test/x",
            [new Message("1", "user", "api_key=abcdefghijk123456", 0, "")]));
        Check(kept == 1 && content.Contains("user:pass@") && content.Contains("api_key=abcdefghijk123456"), "renders text verbatim");
        Check(Transcript.SplitText(new string('a', 2000)).Select(part => part.Length).SequenceEqual([900, 900, 200]), "splits long text");
        Check(Transcript.SplitText(string.Concat(Enumerable.Repeat("\U0001F600", 950))).Count == 2, "splits by code point");
        Check(Transcript.Normalize("a  \r\n\r\n\r\n\r\n\r\nb\u001f") == "a\n\n\nb", "normalizes like Python");
        Check(Transcript.IsoFromMs(1792588777123) == "2026-10-21T13:19:37.123+00:00", "formats millisecond timestamps");
        var passage = "e: Fix login\nmessage_id: m1\nrole: assistant\ntimestamp: 2026-10-20T19:32:22.000Z\nsource_ref: x\ntext:\nThe fix is up.\n\n" +
            "## Message 000013 part 1/1\nclient: c\nsession_id: s\ntitle: t\nmessage_id: m2\nrole: user\ntimestamp: 2026-10-20T19:40:00Z\nsource_ref: x\ntext:\n" +
            "Thanks\nrole: not a header here\n\n## Message 000014 part 1/1\nclient: c\nsess";
        var segments = Compact.Segments(passage);
        Check(segments.Count == 2 && segments[0].Text == "The fix is up." && Compact.Label(segments[0]) == "[assistant 2026-10-20 19:32]"
            && Compact.Label(segments[1]) == "[#13 user 2026-10-20 19:40]" && segments[1].Text == "Thanks\nrole: not a header here",
            "compacts passages that start and end inside headers");
        var continued = Compact.Segments("rest of a sentence.\n\n## Message 000002 part 1/1\nrole: user\ntimestamp: 2026-10-20T19:40:00Z\ntext:\nNext");
        Check(continued.Count == 2 && Compact.Label(continued[0]) == "[...]" && continued[0].Text == "rest of a sentence.", "keeps text that continues a message");
        Check(Compact.Plain("It\u2019s caf\u00e9 \u2014 ok \U0001F600") == "It's caf\u00e9 - ok ", "writes plain text");
        var clipped = Compact.Clip(new string('a', 100) + "X" + new string('b', 100), 30, 100);
        Check(clipped.Contains('X') && clipped.StartsWith("[90 chars before]", StringComparison.Ordinal), "clips around a match");
        var (titled, _, _) = Transcript.Render(new Document("test", "test", "session", "ref", [new Message("1", "user", "hi", 0, "")])
        {
            Title = "Add AI\nto  Pathway",
        });
        Check(titled.Split("title: Add AI to Pathway").Length == 3, "puts the title in the header and every passage");

        var documents = new Dictionary<string, DocumentMeta>
        {
            ["a.md"] = new() { Sha256 = "1" },
            ["b.md"] = new() { Sha256 = "2" },
            ["c.md"] = new() { Sha256 = "3", SessionId = Indexer.CanaryId },
        };
        var recent = Indexer.RecentDocuments(documents, new Dictionary<string, string> { ["a.md"] = "1", ["b.md"] = "old", ["c.md"] = "3" });
        Check(recent.Keys.Order().SequenceEqual(["b.md", "c.md"]), "selects changed documents and the canary");
        List<JsonObject> ranked =
        [
            Hit("b.md", "session-memory"), Hit("b.md", "session-memory-recent"), Hit("a.md", "session-memory"), Hit("gone.md", "session-memory"),
            Hit("gone.md", "session-memory-recent"),
        ];
        var filtered = Indexer.Filter(ranked, ["recent", "archive"], new HashSet<string> { "b.md" }, new HashSet<string> { "a.md", "b.md" }, 5);
        Check(filtered.Select(hit => $"{hit["tier"].AsString()}:{hit["file"].AsString()}").SequenceEqual(["recent:b.md", "archive:a.md"]),
            "drops stale archive copies and removed sessions, keeping LEANN's order");
        Check(Corpus.IsExcluded("ca7418a7-e5e7:subagent:x", ["ca7418a7"]) && !Corpus.IsExcluded("ca7418a8-0000", ["ca7418a7"]), "matches exclusions by prefix");
        var command = Finder.CodexInput("commandExecution", "{\"command\":\"rg RabbitMQ\",\"aggregatedOutput\":\"noise only\"}");
        Check(command.Contains("rg RabbitMQ") && !command.Contains("noise only"), "finds Codex tool inputs without their output");
        Check(Finder.LikeEscape("50%_x'y") == "50\\%\\_x''y", "escapes LIKE patterns");
        var drill = new Finder.Scan(["a", "b"], "s1");
        drill.Note("x", "s1-parent", "a b", "2026-01-02T00:00:00", () => new Finder.Snippet("", "w", "second", 2));
        drill.Note("x", "s1-parent", "a b", "2026-01-01T00:00:00", () => new Finder.Snippet("", "w", "first", 2));
        drill.Note("x", "s2-other", "a b", "2026-01-03T00:00:00", () => new Finder.Snippet("", "w", "other", 2));
        Check(drill.Sessions.Keys.Single().SessionId == "s1-parent" && drill.Sessions.Values.Single().All.Count == 2, "drills into one session's excerpts");
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
        var single = Indexer.Filter([Hit("a.md", null)], ["archive"], new HashSet<string>(), new HashSet<string>(), 5);
        Check(single.Single()["tier"].AsString() == "archive", "labels a one-index search");
        var chatty = Indexer.Filter([.. Enumerable.Range(0, 5).Select(_ => Hit("a.md", null)), Hit("c.md", null)], ["archive"], new HashSet<string>(), new HashSet<string>(), 5);
        Check(chatty.Select(hit => hit["file"].AsString()).SequenceEqual(["a.md", "a.md", "a.md", "c.md"]), "caps hits per session");

        var now = DateTime.UtcNow;
        Check(Watcher.RecentDue(false, "s", "s", now, now, null, false), "builds a missing recent index");
        Check(!Watcher.RecentDue(true, "s", "s", now, DateTime.MinValue, null, false), "skips an unchanged recent index");
        Check(Watcher.RecentDue(true, "s", "t", now, now.AddSeconds(-100), now.AddSeconds(-100), false), "builds after a quiet period");
        Check(!Watcher.RecentDue(true, "s", "t", now, now.AddSeconds(-10), now.AddSeconds(-100), false), "debounces active sessions");
        Check(Watcher.RecentDue(true, "s", "t", now, now.AddSeconds(-10), now.AddSeconds(-400), false), "caps the debounce");

        var temporary = Directory.CreateTempSubdirectory("session-memory-test-");
        try
        {
            CaptureChecks(temporary.FullName);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
        Console.WriteLine("{\"ok\": true}");
        return 0;
    }

    static void CaptureChecks(string root)
    {
        var source = Path.Combine(root, "synthetic", "canary.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var initial = Line("u1", "user", "What is the retry limit?") + Line("a1", "assistant", "The retry limit is 2.");
        File.WriteAllText(source, initial);
        Family[] families = [Parsers.Synthetic(root)];
        var first = Corpus.Capture(root, families);
        File.AppendAllText(source, Line("a2", "assistant", "Correction: the current retry limit is 5 and supersedes 2."));
        var second = Corpus.Capture(root, families);
        var third = Corpus.Capture(root, families);
        Check(first.Changed && second.Changed && !third.Changed, "detects an appended message once");
        Check(first.Manifest.CorpusHash != second.Manifest.CorpusHash, "changes the corpus hash");
        File.WriteAllText(source, initial + "{partial");
        var removed = Corpus.Capture(root, families);
        var corpus = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "corpus"), "*.md").Select(File.ReadAllText));
        Check(removed.Changed && !corpus.Contains("supersedes 2") && corpus.Contains("The retry limit is 2."), "drops removed messages");
        Check(removed.Manifest.Families["synthetic"].Stats["partial_trailing"] == 1, "ignores a partial trailing record");
        var shown = Compact.Show(root, Indexer.CanaryId, 10, null, 0, 6, 1200);
        var matched = Compact.Show(root, "leann-memory", 10, "retry limit is", 0, 6, 1200);
        Check(shown.Contains("[#2 assistant 2026-10-21 00:00] The retry limit is 2.") && matched.Contains("1 matching messages")
            && !matched.Contains("#1 user"), "shows a session's last or matching messages");
        Store.Save(Corpus.ExclusionsPath(root), new List<string> { Indexer.CanaryId });
        var excluded = Corpus.Capture(root, families);
        Check(excluded.Manifest.DocumentCount == 0 && !Directory.EnumerateFiles(Path.Combine(root, "corpus"), "*.md").Any(), "removes excluded sessions from the corpus");
        File.Delete(Corpus.ExclusionsPath(root));

        var chat = Path.Combine(root, "chat.jsonl");
        File.WriteAllLines(chat,
        [
            """{"kind":0,"v":{"requests":[],"pendingRequests":[1]}}""",
            """{"kind":2,"k":["requests"],"v":[{"message":{"text":"x"}}]}""",
            """{"kind":1,"k":["requests",0,"result"],"v":{"ok":true}}""",
            """{"kind":2,"k":["pendingRequests"],"i":0}""",
        ]);
        var (state, errors) = Parsers.Replay(chat, new JsonlStats());
        Check(errors == 0 && state["requests"]![0]!["result"]!["ok"]!.GetValue<bool>() && state["pendingRequests"]!.AsArray().Count == 0,
            "replays VS Code edit logs");

        var compressed = Path.Combine(root, "rollout.jsonl.zst");
        using (var compressor = new ZstdSharp.Compressor())
            File.WriteAllBytes(compressed, compressor.Wrap(System.Text.Encoding.UTF8.GetBytes("{\"a\":1}\n{\"a\":2}\n")).ToArray());
        Check(Jsonl.Read(compressed, new JsonlStats()).Count() == 2, "reads Zstandard-compressed rollouts");
        Check(Parsers.StripInjectedContext("<environment_context>\n  <cwd>C:\\x</cwd>\n</environment_context>") == ""
            && Parsers.StripInjectedContext("<recommended_plugins>a</recommended_plugins>\n<environment_context>b</environment_context>\n") == ""
            && Parsers.StripInjectedContext("# AGENTS.md instructions for C:\\x\n\n<INSTRUCTIONS>rules</INSTRUCTIONS>\nFix the bug") == "Fix the bug"
            && Parsers.StripInjectedContext("<b>bold</b> stays") == "<b>bold</b> stays"
            && Parsers.StripInjectedContext("<send_user_message_question_reply>yes</send_user_message_question_reply>")
                == "<send_user_message_question_reply>yes</send_user_message_question_reply>", "strips injected Codex context only");
        var workspace = Path.Combine(root, "workspace.yaml");
        File.WriteAllText(workspace, "id: x\ncwd: C:\\x\nname: 'It''s done'\nuser_named: false\n");
        Check(Parsers.WorkspaceName(workspace) == "It's done" && Parsers.WorkspaceName(Path.Combine(root, "missing.yaml")) == "", "reads Copilot session names");

        var fresh = Path.Combine(root, "fresh");
        Parsers.EnsureCanary(fresh);
        var seeded = Parsers.Synthetic(fresh).Parse().Documents.Single();
        Check(seeded.SessionId == Indexer.CanaryId && seeded.Messages.Count == 3, "seeds the canary on a new machine");
    }

    static string Line(string id, string role, string text) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["session_id"] = Indexer.CanaryId, ["message_id"] = id, ["role"] = role, ["timestamp"] = "2026-10-21T00:00:00Z", ["text"] = text,
        }) + "\n";

    static JsonObject Hit(string file, string? index) => new() { ["file"] = file, ["index"] = index };

    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Self-test failed: {name}");
    }
}
