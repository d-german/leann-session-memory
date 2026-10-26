using System.Text.Json;
using System.Text.Json.Nodes;
using static SessionMemory.ParserHelpers;

namespace SessionMemory.Clients.VsCode
{
    // Turns each VS Code chat file into one document, from the state its edit log replays to.
    sealed internal partial class VsCodeClient
    {
        private static FamilyResult ReadSessions(string home)
        {
            var documents = new List<Document>();
            var stats = Stats("files", "sessions", "operations_failed", "malformed", "partial_trailing");
            foreach (var path in ChatFiles(CodeUser(home)))
            {
                stats["files"]++;
                var fileStats = new JsonlStats();
                var (state, errors) = Replay(path, fileStats);
                stats["operations_failed"] += errors;
                stats["malformed"] += fileStats.Malformed;
                stats["partial_trailing"] += fileStats.PartialTrailing;
                var sessionId = state["sessionId"].StrOr(Path.GetFileNameWithoutExtension(path));
                var messages = Messages(state["requests"]);
                if (messages.Count > 0)
                    documents.Add(new Document(FamilyName, "vscode-copilot", sessionId, path, [.. messages])
                    {
                        Title = state["customTitle"].AsString() ?? ""
                    });
            }
            stats["sessions"] = documents.Count;
            return new FamilyResult([.. documents], stats);
        }

        private static List<Message> Messages(JsonNode? requests)
        {
            var messages = new List<Message>();
            if (requests is not JsonArray items) return messages;
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index] is not JsonObject request || request["hiddenFromTranscript"].Kind() == JsonValueKind.True) continue;
                AddRequest(messages, request, index);
            }
            return messages;
        }

        private static void AddRequest(List<Message> messages, JsonObject request, int index)
        {
            var timestamp = request["timestamp"].IsoFromMs();
            var text = UserText(request["message"]);
            if (text.Length > 0) messages.Add(new Message(request["requestId"].StrOr($"{index}:user"), "user", text, index * 2, timestamp));
            text = AssistantText(request["response"]);
            if (text.Length == 0) return;
            var responseTimestamp = request["responseTimestamp"].IsoFromMs();
            messages.Add(new Message(request["responseId"].StrOr($"{index}:assistant"), "assistant", text, index * 2 + 1,
                responseTimestamp.Length > 0 ? responseTimestamp : timestamp));
        }

        private static string UserText(JsonNode? message)
        {
            if (message is not JsonObject item) return "";
            if (item["text"].AsString() is { Length: > 0 } text) return text;
            return item["parts"] is JsonArray parts
                ? string.Join('\n', parts.OfType<JsonObject>().Select(part => part["text"].AsString() ?? part["value"].AsString()).OfType<string>())
                : "";
        }

        private static string AssistantText(JsonNode? response)
        {
            if (response is not JsonArray items) return "";
            return string.Join('\n', items.OfType<JsonObject>()
                .Where(item => item["kind"] is null)
                .Select(item => item["value"].AsString() ?? (item["value"] as JsonObject)?["value"].AsString())
                .OfType<string>());
        }
    }
}
