using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using SessionMemory.Clients;

namespace SessionMemory
{
    /// <summary>The state the status command reports: the watcher, manifest and index states without their document lists, and the exclusions.</summary>
    sealed internal class StatusReport
    {
        private static readonly ImmutableArray<string> StateFiles = ["watcher", "manifest", "archive-state", "recent-state", "archive-progress", "recent-progress"];

        private readonly string _root;
        private readonly ExclusionService _exclusions;

        public StatusReport(ClientContext paths, ExclusionService exclusions)
        {
            _root = paths.Root;
            _exclusions = exclusions;
        }

        // A state file that is missing or unreadable reads as null.
        public JsonObject Read()
        {
            var status = new JsonObject();
            var current = Indexer.ActiveCurrentFormat(_root);
            foreach (var name in StateFiles)
            {
                var file = name switch
                {
                    "archive-state" => Indexer.StateFileName("archive", current),
                    "recent-state" => Indexer.StateFileName("recent", current),
                    _ => $"{name}.json"
                };
                var node = Store.LoadNode(Path.Combine(_root, "state", file));
                (node as JsonObject)?.Remove("documents");
                status[name] = node;
            }
            status["excluded"] = JsonSerializer.SerializeToNode(_exclusions.Load());
            return status;
        }
    }
}
