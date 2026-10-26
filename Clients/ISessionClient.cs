using System.Collections.Immutable;

namespace SessionMemory.Clients
{
    /// <summary>
    /// One AI assistant (a "client") whose sessions this tool indexes: its manifest family, the session variable its host sets,
    /// the files it watches, how they become documents and how find searches them. Corpus.Capture, Finder.Run, Watcher and
    /// Corpus.CurrentSession use clients only through this interface; ClientRegistry lists the implementations.
    /// </summary>
    internal interface ISessionClient
    {
        /// <summary>
        /// The client's family key in the manifest, such as "codex". Every document <see cref="Parse" /> returns carries it as its Family.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// The environment variable the client's host sets, for the commands it runs, to the ID of the session running them;
        /// null when the host announces no session.
        /// </summary>
        string? SessionVariable { get; }

        /// <summary>The files whose size and timestamp decide whether the client must be parsed again.</summary>
        ImmutableArray<string> Sources(ClientContext context);

        /// <summary>Reads the client's local store into one document per session, with the counters the manifest keeps.</summary>
        FamilyResult Parse(ClientContext context);

        /// <summary>
        /// The independent pieces of work that search the client's raw store for a find. Each piece runs on its own core and notes
        /// what it finds in the scan it is given; the longest pieces come first, so they start early.
        /// </summary>
        IEnumerable<Action<Finder.Scan>> FindWork(FindContext context);
    }
}
