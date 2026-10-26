using SessionMemory.Clients;
using SessionMemory.Clients.Claude;
using SessionMemory.Clients.Codex;
using SessionMemory.Clients.Copilot;
using SessionMemory.Clients.VsCode;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private const string ReplayStart = """{"kind":0,"v":{"a":{"b":1},"list":[1,2,3],"n":null,"s":"x"}}""";

        private const string ReplayStartState = """{"a":{"b":1},"list":[1,2,3],"n":null,"s":"x"}""";

        private static void ParserRefactorChecks()
        {
            var temporary = Directory.CreateTempSubdirectory("session-memory-parsers-");
            try
            {
                ReplayPathChecks(temporary.FullName);
                ReplayPushChecks(temporary.FullName);
                ReplaySilentChecks(temporary.FullName);
                WorkspaceAndWalkChecks(temporary.FullName);
                ParserContractChecks(temporary.FullName);
                ParserFamilyChecks(temporary.FullName);
                ClientRegistryChecks(temporary.FullName);
            }
            finally
            {
                temporary.Delete(recursive: true);
            }
        }

        // The Replay checks pin results for malformed edit logs through VsCodeClient.Replay(path, stats) only, so they pass before and after the
        // Result rewrite: an operation that does not fit the state counts once in operations_failed and changes nothing.
        private static void ReplayPathChecks(string root)
        {
            Check(ReplayRun(root, "negative", ReplayStart,
                    """{"kind":1,"k":["list",-1],"v":9}""",
                    """{"kind":1,"k":["list",-3],"v":7}""",
                    """{"kind":1,"k":["list",-4],"v":0}""",
                    """{"kind":3,"k":["list",-2]}""")
                is (1, """{"a":{"b":1},"list":[7,null,9],"n":null,"s":"x"}"""), "replays negative positions from the end");
            Check(ReplayRun(root, "missing", ReplayStart,
                    """{"kind":1,"k":["missing","x"],"v":1}""",
                    """{"kind":3,"k":["a","nope","deeper"]}""",
                    """{"kind":1,"k":["n","x"],"v":1}""",
                    """{"kind":1,"k":["a","c"],"v":2}""",
                    """{"kind":1,"k":["fresh"]}""",
                    """{"kind":3,"k":["s"]}""")
                is (3, """{"a":{"b":1,"c":2},"list":[1,2,3],"n":null,"s":null,"fresh":null}"""), "fails paths through missing keys and nulls");
            Check(ReplayRun(root, "keys", ReplayStart,
                    """{"kind":1,"v":1}""",
                    """{"kind":1,"k":"a","v":1}""",
                    """{"kind":1,"k":null,"v":1}""",
                    """{"kind":3,"k":[{"x":1}]}""",
                    """{"kind":1,"k":[0,"b"],"v":5}""",
                    """{"kind":1,"k":["list","0"],"v":5}""",
                    """{"kind":1,"k":["list",1.5],"v":5}""",
                    """{"kind":1,"k":["a",1],"v":5}""",
                    """{"kind":1,"k":["list",true],"v":8}""",
                    """{"kind":2,"v":[1]}""")
                is (8, """{"a":{"b":1},"list":[1,8,3],"n":null,"s":"x"}"""), "fails bad or missing key paths");
            Check(ReplayRun(root, "range", ReplayStart,
                    """{"kind":1,"k":["list",3],"v":1}""",
                    """{"kind":1,"k":["list",5,"x"],"v":1}""",
                    """{"kind":2,"k":["list",7],"v":[1]}""",
                    """{"kind":3,"k":["list",-9]}""")
                is (4, ReplayStartState), "fails positions outside the array");
        }

        private static void ReplayPushChecks(string root)
        {
            Check(ReplayRun(root, "cut", ReplayStart,
                    """{"kind":2,"k":["list"],"i":1}""",
                    """{"kind":2,"k":["list"],"i":5,"v":[4]}""",
                    """{"kind":2,"k":["list"],"i":null,"v":[6]}""",
                    """{"kind":2,"k":["list"],"i":-1,"v":[0]}""",
                    """{"kind":2,"k":["list"],"i":"1","v":[0]}""",
                    """{"kind":2,"k":["list"],"i":2,"v":7}""",
                    """{"kind":2,"k":["list"],"v":[{"m":1},[2]]}""")
                is (2, """{"a":{"b":1},"list":[1,4,{"m":1},[2]],"n":null,"s":"x"}"""), "cuts arrays at i and appends v");
            Check(ReplayRun(root, "empty-keys", ReplayStart,
                    """{"kind":1,"k":[],"v":1}""",
                    """{"kind":3,"k":[]}""",
                    """{"kind":2,"k":[],"v":[1]}""",
                    """{"kind":2,"k":[],"i":0}""")
                is (4, ReplayStartState), "fails empty key paths");
            Check(ReplayRun(root, "not-array", ReplayStart,
                    """{"kind":2,"k":["a"],"v":[1]}""",
                    """{"kind":2,"k":["s"],"v":[1]}""",
                    """{"kind":2,"k":["n"],"v":[1]}""",
                    """{"kind":2,"k":["missing"],"v":[1]}""",
                    """{"kind":2,"k":["a"],"i":0}""",
                    """{"kind":2,"k":["list",0],"v":[1]}""")
                is (6, ReplayStartState), "fails pushes to anything but an array");
        }

        private static void ReplaySilentChecks(string root)
        {
            var silent = Path.Combine(root, "silent.jsonl");
            File.WriteAllText(silent, string.Join('\n',
                """{"kind":1,"k":["a"],"v":1}""",
                """{"kind":1,"v":1}""",
                """{"kind":2,"k":[],"v":[1]}""",
                """{"kind":3,"k":["a"]}""",
                """{"kind":0,"v":[1]}""",
                """{"kind":0}""",
                "[1,2]",
                "null",
                "{bad",
                ReplayStart,
                """{"kind":4,"k":["s"],"v":1}""",
                """{"kind":-1,"k":["s"],"v":1}""",
                """{"kind":"1","k":["s"],"v":1}""",
                """{"kind":null,"k":["s"],"v":1}""",
                """{"k":["s"],"v":1}""",
                """{"kind":1.5,"k":["s"],"v":1}""",
                """{"kind":4,"k":[],"v":1}""",
                """{"kind":true,"k":["t"],"v":1}""",
                """{"kind":1.0,"k":["one"],"v":1}""") + "\n" + """{"kind":1,"k":["s"],"v":""");
            var stats = new JsonlStats();
            var (state, errors) = VsCodeClient.Replay(silent, stats);
            Check(errors == 0 && stats is { Malformed: 1, PartialTrailing: 1 }
                              && state.ToJsonString() == """{"a":{"b":1},"list":[1,2,3],"n":null,"s":"x","t":1,"one":1}""",
                "skips unknown kinds and edits before the first state without failing them");
            Check(ReplayRun(root, "reset", ReplayStart,
                    """{"kind":1,"k":["s"],"v":"y"}""",
                    """{"kind":false,"v":{"z":1}}""",
                    """{"kind":1,"k":["z"],"v":2}""")
                is (0, """{"z":2}"""), "replaces the state on kind 0");
            Check(ReplayRun(root, "empty-state", ReplayStart,
                    """{"kind":0,"v":{}}""",
                    """{"kind":1,"k":["z"],"v":3}""",
                    """{"kind":1,"v":3}""",
                    """{"kind":2,"k":[],"v":[1]}""")
                is (0, "{}"), "ignores edits to an empty state");
        }

        private static void WorkspaceAndWalkChecks(string root)
        {
            var walk = Path.Combine(root, "walk");
            string[] files = ["a.jsonl", Path.Combine("x", "b.jsonl"), Path.Combine("x", "skip.txt"), Path.Combine("x", "y", "c.jsonl"), Path.Combine("z", "d.jsonl")];
            foreach (var file in files) ParserFile(Path.Combine(walk, file), "{}");
            Check(SourceFiles.Rglob(walk, "*.jsonl").Select(Path.GetFileName).SequenceEqual(["a.jsonl", "b.jsonl", "d.jsonl", "c.jsonl"])
                  && !SourceFiles.Rglob(Path.Combine(root, "no-walk"), "*.jsonl").Any(), "lists each directory's children before descending, like Python's rglob");
            var named = ParserFile(Path.Combine(root, "named.yaml"), "id: x", "names: no", "name: \"say \\\"hi\\\"\"");
            var unnamed = ParserFile(Path.Combine(root, "unnamed.yaml"), "id: x");
            Check(CopilotClient.WorkspaceName(named) == "say \"hi\"" && CopilotClient.WorkspaceName(unnamed) == "" && CopilotClient.WorkspaceName(walk) == "",
                "reads double-quoted Copilot session names and nothing from unnamed files or folders");
        }

        // Every list a client hands out is a non-default ImmutableArray, and an empty home parses with the stats in their persisted order.
        private static void ParserContractChecks(string root)
        {
            var home = Path.Combine(root, "empty-home");
            Directory.CreateDirectory(home);
            var context = new ClientContext(home, root);
            var clients = ClientRegistry.All;
            Check(clients.Select(item => item.Name).SequenceEqual(["synthetic", "codex", "claude", "copilot", "vscode"])
                  && clients.Select(item => item.Sources(context).Length).SequenceEqual([0, 3, 0, 4, 0]), "lists every family and the database files it watches");
            string[][] statNames =
            [
                ["files", "sessions", "malformed", "partial_trailing"],
                ["sqlite_threads", "fallback_threads", "projection_without_items", "partial_trailing", "malformed"],
                ["files", "parent_files", "subagent_files", "malformed", "partial_trailing"],
                ["event_files", "event_sessions", "store_sessions", "malformed", "partial_trailing", "desktop_sessions"],
                ["files", "sessions", "operations_failed", "malformed", "partial_trailing"]
            ];
            var results = clients.Select(item => item.Parse(context)).ToList();
            Check(results.All(result => result.Documents.IsEmpty) && results.Zip(statNames, (result, names) => result.Stats.Keys.SequenceEqual(names)).All(same => same),
                "parses an empty home with stats in their persisted order");
            Check(VsCodeClient.ChatFiles(Path.Combine(root, "no-code")) is { IsDefault: false, IsEmpty: true }, "finds no chat files without VS Code");
        }

        private static void ParserFamilyChecks(string root)
        {
            var home = ParserHome(root);
            var context = new ClientContext(home, root);
            var clients = ClientRegistry.All;
            var chats = VsCodeClient.ChatFiles(VsCodeClient.CodeUser(home));
            Check(chats is [var profile, var workspace] && profile.EndsWith("e1.jsonl", StringComparison.Ordinal) && workspace.EndsWith("s1.jsonl", StringComparison.Ordinal),
                "finds VS Code chat files in Python's path order");
            var vsCode = clients[4].Parse(context);
            Check(vsCode.Documents is [{ SessionId: "vs-1", Client: "vscode-copilot", Title: "Fix it", Messages: [{ Role: "user" }, { Role: "assistant", Text: "hi there" }] }]
                  && vsCode.Stats["files"] == 2 && vsCode.Stats["operations_failed"] == 2, "parses VS Code sessions and counts failed operations");
            var copilot = clients[3].Parse(context);
            Check(copilot.Documents is [{ SessionId: "c1", Client: "github-copilot-cli", Title: "Named", Messages.Length: 2 }]
                  && copilot.Stats["desktop_sessions"] == 0 && clients[3].Sources(context).Length == 6, "reads Copilot events when data.db has no sessions table");
            var codex = clients[1].Parse(context);
            Check(codex.Documents is [{ SessionId: "cx-1", Title: "Codex title", Messages: [{ Role: "user" }, { Role: "assistant" }] }]
                  && codex.Stats["fallback_threads"] == 1 && clients[1].Sources(context) is [_, _, var rollout, _, var state, _]
                  && rollout.EndsWith("rollout-1.jsonl", StringComparison.Ordinal) && state.EndsWith("state_5.sqlite", StringComparison.Ordinal),
                "keeps index titles when the Codex state database has no threads table");
            var claude = clients[2].Parse(context);
            Check(claude.Documents is [{ SessionId: "cl-1", Client: "claude-code", Title: "Claude title", Messages.Length: 2 }] && claude.Stats["parent_files"] == 1,
                "parses Claude sessions with their titles");
        }

        // The registry keeps the manifest's family order and the hosts' order of precedence for the current session.
        private static void ClientRegistryChecks(string root)
        {
            var all = ClientRegistry.All;
            Check(all is [LambdaSessionClient, CodexClient, ClaudeClient, CopilotClient, VsCodeClient] && all[0] == ClientRegistry.Synthetic,
                "registers one client per family");
            Check(all.Select(client => client.SessionVariable).SequenceEqual([null, "CODEX_THREAD_ID", "CLAUDE_CODE_SESSION_ID", "COPILOT_AGENT_SESSION_ID", null]),
                "names the session variable each host sets");
            Check(ClientRegistry.SessionHosts.Select(client => client.Name).SequenceEqual(["copilot", "codex", "claude"]) && ClientRegistry.SessionHosts.All(all.Contains)
                  && Corpus.CurrentSessionVariables.SequenceEqual(["COPILOT_AGENT_SESSION_ID", "CODEX_THREAD_ID", "CLAUDE_CODE_SESSION_ID"]),
                "reads the current session in the hosts' order of precedence");
            var context = new ClientContext(Path.Combine(root, "lambda-home"), root);
            var find = new FindContext(context.Home, new Dictionary<string, DocumentMeta>(), false);
            var lambda = new LambdaSessionClient("lambda", _ => ["a"], _ => new FamilyResult([], []),
                _ => [scan => scan.Note("lambda", "lambda-session", "a zebra", "2026-01-01T00:00:00", () => null)], "LAMBDA_SESSION_ID");
            var noted = new Finder.Scan(["zebra"]);
            foreach (var work in lambda.FindWork(find)) work(noted);
            Check(lambda is { Name: "lambda", SessionVariable: "LAMBDA_SESSION_ID" } && lambda.Sources(context) is ["a"] && lambda.Parse(context).Documents.IsEmpty
                  && noted.Sessions.Keys.SequenceEqual([new Finder.SessionKey("lambda", "lambda-session")]), "builds a client from delegates");
            Check(!ClientRegistry.Synthetic.FindWork(find).Any() && ClientRegistry.Synthetic.SessionVariable is null, "gives a delegate client no find work by default");
        }

        // A home with one session per client; data.db and the Codex state database are empty files without the queried tables.
        private static string ParserHome(string root)
        {
            var home = Path.Combine(root, "home");
            var codeUser = VsCodeClient.CodeUser(home);
            ParserFile(Path.Combine(codeUser, "workspaceStorage", "w1", "chatSessions", "s1.jsonl"),
                """{"kind":0,"v":{"sessionId":"vs-1","customTitle":"Fix it","requests":[]}}""",
                """{"kind":2,"k":["requests"],"v":[{"requestId":"r1","message":{"text":"hello"},"response":[{"value":"hi there"}],"timestamp":1792588777123}]}""",
                """{"kind":1,"k":["requests",5,"x"],"v":1}""",
                """{"kind":2,"k":[],"v":[1]}""");
            ParserFile(Path.Combine(codeUser, "workspaceStorage", "w1", "chatSessions", "notes.json"), "{}");
            ParserFile(Path.Combine(codeUser, "profiles", "p1", "globalStorage", "emptyWindowChatSessions", "e1.jsonl"), """{"kind":0,"v":{"requests":[]}}""");
            var session = Path.Combine(home, ".copilot", "session-state", "c1");
            ParserFile(Path.Combine(session, "events.jsonl"),
                """{"type":"session.start","data":{"sessionId":"c1","producer":"copilot-agent"}}""",
                """{"type":"user.message","data":{"content":"question","messageId":"m1"},"timestamp":"2026-10-21T00:00:00Z"}""",
                """{"type":"assistant.message","data":{"content":"answer","messageId":"m2"},"timestamp":"2026-10-21T00:00:01Z"}""");
            ParserFile(Path.Combine(session, "workspace.yaml"), "name: Named");
            File.WriteAllBytes(Path.Combine(home, ".copilot", "data.db"), []);
            var codex = Path.Combine(home, ".codex");
            ParserFile(Path.Combine(codex, "sessions", "2026", "rollout-1.jsonl"),
                """{"type":"session_meta","payload":{"id":"cx-1"}}""",
                """{"type":"response_item","ordinal":0,"payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hello codex"}]}}""",
                """{"type":"response_item","ordinal":1,"payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hello human"}]}}""");
            ParserFile(Path.Combine(codex, "session_index.jsonl"), """{"id":"cx-1","thread_name":"Codex title"}""");
            File.WriteAllBytes(Path.Combine(codex, "state_5.sqlite"), []);
            ParserFile(Path.Combine(home, ".claude", "projects", "proj", "cl-1.jsonl"),
                """{"type":"user","sessionId":"cl-1","uuid":"u1","message":{"content":"claude question"}}""",
                """{"type":"assistant","sessionId":"cl-1","uuid":"a1","message":{"id":"x","content":[{"type":"text","text":"claude answer"}]}}""",
                """{"type":"custom-title","customTitle":"Claude title"}""");
            return home;
        }

        private static (long Errors, string State) ReplayRun(string root, string name, params string[] operations)
        {
            var path = Path.Combine(root, name + ".jsonl");
            File.WriteAllLines(path, operations);
            var (state, errors) = VsCodeClient.Replay(path, new JsonlStats());
            return (errors, state.ToJsonString());
        }

        private static string ParserFile(string path, params string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines);
            return path;
        }
    }
}
