using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;

namespace SessionMemory.Clients.VsCode
{
    // Replays a VS Code chat file's edit log to the chat state it describes.
    sealed internal partial class VsCodeClient
    {
        // VS Code chat files are edit logs (ObjectMutationLog): kind 0 sets the state, 1 sets a path, 2 cuts an array
        // at "i" and appends "v", 3 deletes a property. An operation that does not fit the state changes nothing and is counted.
        public static (JsonObject State, long Errors) Replay(string path, JsonlStats stats)
        {
            var state = new JsonObject();
            long errors = 0;
            foreach (var operation in Jsonl.ReadNodes(path, stats))
            {
                if (Apply(state, operation).TryGetValue(out var next)) state = next;
                else errors++;
            }
            return (state, errors);
        }

        // Edits before the first state, unknown kinds and an initial state that is not an object are skipped and keep the state.
        private static Result<JsonObject, ReplayError> Apply(JsonObject state, JsonObject operation) => OperationKind(operation["kind"]) switch
        {
            0 when operation["v"] is JsonObject initial => Adopt(operation, initial),
            1 when state.Count > 0 => Keys(operation).Bind(keys => SetPath(state, keys, Take(operation))).Map(() => state),
            2 when state.Count > 0 => Keys(operation).Bind(keys => Push(state, keys, operation["i"], Take(operation))).Map(() => state),
            3 when state.Count > 0 => Keys(operation).Bind(keys => SetPath(state, keys, null)).Map(() => state),
            _ => state
        };

        // The initial state leaves its operation, which is discarded, so it can be edited as the root.
        private static JsonObject Adopt(JsonObject operation, JsonObject initial)
        {
            operation.Remove("v");
            return initial;
        }

        private static int OperationKind(JsonNode? kind) => kind.Kind() switch
        {
            JsonValueKind.Number => kind!.AsValue().TryGetValue<double>(out var number) && number is 0 or 1 or 2 or 3 ? (int)number : -1,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => -1
        };

        // A path needs at least one key: with none there is no property to set and the root is never an array.
        private static Result<JsonArray, ReplayError> Keys(JsonObject operation) =>
            operation["k"] is JsonArray { Count: > 0 } keys ? keys : ReplayError.BadPath;

        private static JsonNode? Take(JsonObject operation)
        {
            if (!operation.TryGetPropertyValue("v", out var value)) return null;
            operation.Remove("v");
            return value;
        }

        // Every key but the last must lead to an existing container; the last names the property or position to set.
        private static UnitResult<ReplayError> SetPath(JsonObject root, JsonArray keys, JsonNode? value) =>
            Walk(root, keys.SkipLast(1)).Bind(parent => Assign(parent, keys[^1], value));

        private static UnitResult<ReplayError> Assign(JsonNode parent, JsonNode? last, JsonNode? value)
        {
            switch (parent)
            {
                case JsonObject item when last.AsString() is { } name:
                    item[name] = value;
                    return UnitResult.Success<ReplayError>();
                case JsonArray list when Index(last) is { } position:
                    return ReplayPosition(list, position).Tap(at => list[at] = value);
                // Python accepts a numeric key on an object (a no-op here, transcripts never read it); anything else fails.
                case JsonObject when Index(last) is not null:
                    return UnitResult.Success<ReplayError>();
                default:
                    return ReplayError.BadKeyType;
            }
        }

        // "i" rewrites the array from that index: a streamed or edited item replaces the rest, and "i" alone removes them.
        private static UnitResult<ReplayError> Push(JsonObject root, JsonArray keys, JsonNode? index, JsonNode? value) =>
            Walk(root, keys)
                .Bind(target => (target as JsonArray).ToResult(ReplayError.NotArray))
                .Bind(items => Cut(index).Tap(cut => Splice(items, cut, value)));

        // Without "i" nothing is cut; a cut past the end keeps every item.
        private static Result<long, ReplayError> Cut(JsonNode? index) => index switch
        {
            null => long.MaxValue,
            _ when Index(index) is { } at and >= 0 => at,
            _ => ReplayError.BadIndex
        };

        private static void Splice(JsonArray items, long cut, JsonNode? value)
        {
            while (items.Count > cut) items.RemoveAt(items.Count - 1);
            if (value is not JsonArray additions) return;
            List<JsonNode?> moved = [.. additions];
            additions.Clear();
            foreach (var addition in moved) items.Add(addition);
        }

        private static Result<JsonNode, ReplayError> Walk(JsonNode root, IEnumerable<JsonNode?> keys) =>
            keys.Aggregate(Result.Success<JsonNode, ReplayError>(root), (node, key) => node.Bind(parent => Child(parent, key)));

        // A key must name an existing property of an object or a position in an array; a null value cannot be walked into.
        private static Result<JsonNode, ReplayError> Child(JsonNode node, JsonNode? key) => node switch
        {
            JsonObject item when key.AsString() is { } name => item[name].ToResult(ReplayError.MissingKey),
            JsonArray list when Index(key) is { } position => ReplayPosition(list, position).Bind(at => list[at].ToResult(ReplayError.MissingKey)),
            _ => ReplayError.BadKeyType
        };

        // A negative position counts from the end, as in Python.
        private static Result<int, ReplayError> ReplayPosition(JsonArray list, long position)
        {
            var target = position < 0 ? position + list.Count : position;
            return target >= 0 && target < list.Count ? (int)target : ReplayError.OutOfRange;
        }

        private static long? Index(JsonNode? key) => key.Kind() switch
        {
            JsonValueKind.Number => key!.AsValue().TryGetValue<long>(out var number) ? number : null,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null
        };

        // Why an operation did not fit the state; replay only counts them.
        private enum ReplayError
        {
            BadPath,
            MissingKey,
            BadKeyType,
            OutOfRange,
            NotArray,
            BadIndex
        }
    }
}
