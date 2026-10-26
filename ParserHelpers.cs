using System.Globalization;

namespace SessionMemory
{
    /// <summary>Small helpers every client's parser uses to collect its messages and counters.</summary>
    internal static class ParserHelpers
    {
        public static Dictionary<string, long> Stats(params string[] names) => names.ToDictionary(name => name, _ => 0L);

        public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        public static void Add(Dictionary<string, List<Message>> sessions, string sessionId, Message message)
        {
            if (!sessions.TryGetValue(sessionId, out var messages)) sessions[sessionId] = messages = [];
            messages.Add(message);
        }
    }
}
