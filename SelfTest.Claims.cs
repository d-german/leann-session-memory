using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private static void ClaimChecks()
        {
            var temporary = Directory.CreateTempSubdirectory("session-memory-claims-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            var root = temporary.FullName;
            Directory.CreateDirectory(Path.Combine(root, "corpus"));
            Directory.CreateDirectory(Path.Combine(root, "state"));
            File.WriteAllText(Path.Combine(root, "corpus", "source.md"),
                "## Message 000001 part 1/2\nrole: assistant\ntimestamp: 2026-01-01\ntext:\nA proposal was\n"
                + "## Message 000001 part 2/2\nrole: assistant\ntimestamp: 2026-01-01\ntext:\nnot implemented.\n"
                + "## Message 000002 part 1/1\nrole: user\ntimestamp: 2026-01-02\ntext:\nCorrection follows.\n");
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest
            {
                Documents = new Dictionary<string, DocumentMeta>
                {
                    ["source.md"] = new() { SessionId = "source-12345678" },
                    ["excluded.md"] = new() { SessionId = "excluded-12345678" },
                    ["../escape.md"] = new() { SessionId = "escape-12345678" }
                }
            });
            Store.Save(Path.Combine(root, "state", "excluded.json"), new[] { "excluded-12345678" });
            using var services = Services(Path.Combine(root, "home"), root);
            var verifier = new ClaimVerifier(services.GetRequiredService<ExclusionService>(), services.GetRequiredService<SessionReader>());
            var input = Path.Combine(root, "claims.json");
            JsonNode CheckInput(string json)
            {
                File.WriteAllText(input, json);
                var answer = verifier.Verify(input);
                Check(answer.IsSuccess, "verifier accepts valid claim input");
                return JsonNode.Parse(answer.Value)!;
            }
            var output = CheckInput("""
                {"claims":[
                  {"claim":"Implemented","session_id":"source-12345678","message_number":1,"quote":"proposal was not implemented"},
                  {"claim":"Wrong","session_id":"source-12345678","message_number":1,"quote":"was implemented"},
                  {"claim":"No source"},
                  {"claim":"Unknown","session_id":"unknown","message_number":1,"quote":"x"},
                  {"claim":"Excluded","session_id":"excluded-12345678","message_number":1,"quote":"x"},
                  {"claim":"Escape","session_id":"escape-12345678","message_number":1,"quote":"x"}
                ]}
                """);
            var checks = output["checks"]!.AsArray();
            Check(checks[0]!["quote_found"]!.GetValue<bool>() && checks[0]!["verdict"]!.GetValue<string>() == "review_required"
                  && output["semantic_support"]!.GetValue<string>() == "not_assessed"
                  && checks[0]!["context"]!.AsArray().Count == 2,
                "joins message parts and presents neighboring context without deciding claim support");
            Check(!checks[1]!["quote_found"]!.GetValue<bool>() && checks[1]!["verdict"]!.GetValue<string>() == "unsupported"
                  && checks[2]!["reason"]!.GetValue<string>() == "missing_citation"
                  && checks[3]!["reason"]!.GetValue<string>() == "unknown_or_ambiguous_session"
                  && checks[4]!["reason"]!.GetValue<string>() == "excluded_session"
                  && checks[5]!["reason"]!.GetValue<string>() == "invalid_source",
                "rejects false quotes, missing and excluded sources, and unsafe manifest paths");
            File.WriteAllText(Path.Combine(root, "corpus", "source.md"),
                "## Message 000001 part 1/1\nrole: assistant\ntext:\n“Proposal” — not implemented.\n");
            var display = CheckInput("""
                {"claims":[{"claim":"The proposal was implemented","session_id":"source-12345678","message_number":1,"quote":"\"Proposal\" - not implemented."}]}
                """);
            Check(display["checks"]![0]!["quote_found"]!.GetValue<bool>()
                  && display["checks"]![0]!["verdict"]!.GetValue<string>() == "review_required"
                  && display["semantic_support"]!.GetValue<string>() == "not_assessed"
                  && display["checks"]![0]!["context"]![0]!["text_format"]!.GetValue<string>() == "display_normalized",
                "matches reader display punctuation while leaving claim meaning for review");
            var symbols = CheckInput("""
                {"claims":[{"claim":"x","session_id":"source-12345678","message_number":1,"quote":"☃"}]}
                """);
            Check(!symbols["checks"]![0]!["quote_found"]!.GetValue<bool>(), "does not match a symbol absent from the source");
            File.WriteAllText(Path.Combine(root, "corpus", "source.md"),
                "## Message 000001 part 1/1\nrole: assistant\ntimestamp: 2026-01-01\ntext:\nStatus: 🔴 approved.\n"
                + "## Message 000001 part 1/1\nrole: user\ntimestamp: 2026-01-02\ntext:\nSecond message.\n");
            var ambiguous = CheckInput("""
                {"claims":[{"claim":"x","session_id":"source-12345678","message_number":1,"quote":"approved. Second message."}]}
                """);
            Check(!ambiguous["checks"]![0]!["quote_found"]!.GetValue<bool>()
                  && ambiguous["checks"]![0]!["reason"]!.GetValue<string>() == "ambiguous_message",
                "rejects duplicate message numbers instead of joining distinct messages");
            File.WriteAllText(Path.Combine(root, "corpus", "source.md"),
                "## Message 000001 part 1/1\nrole: assistant\ntimestamp: 2026-01-01\ntext:\nStatus: 🔴 approved.\n");
            var differentSymbol = CheckInput("""
                {"claims":[{"claim":"x","session_id":"source-12345678","message_number":1,"quote":"Status: 🟢 approved."}]}
                """);
            Check(!differentSymbol["checks"]![0]!["quote_found"]!.GetValue<bool>(),
                "does not erase differing symbols when checking a quote");
            File.WriteAllText(input, "{bad");
            Check(verifier.Verify(input).IsFailure, "rejects malformed JSON");
            File.WriteAllText(input, "{\"claims\":[],\"extra\":true}");
            Check(verifier.Verify(input).IsFailure, "rejects unknown input fields");
            File.WriteAllText(input, new string('x', 65537));
            Check(verifier.Verify(input).IsFailure, "rejects oversized input");
            File.WriteAllText(Path.Combine(root, "corpus", "source.md"),
                "## Message 000001 part 1/1\nrole: user\ntext:\n" + new string('x', 20000));
            var bounded = CheckInput("{\"claims\":[{\"claim\":\"x\",\"session_id\":\"source-12345678\",\"message_number\":1,\"quote\":\"x\"}]}");
            Check(bounded["checks"]![0]!["context"]!.AsArray().Sum(item => item!["text"]!.GetValue<string>().Length) <= 12000,
                "bounds returned source text");
        }
    }
}
