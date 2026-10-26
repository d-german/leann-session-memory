namespace SessionMemory.Clients
{
    /// <summary>
    /// What a client needs to plan a find: the user's home folder, the documents of the last capture and whether tool outputs
    /// are searched too (find --outputs).
    /// </summary>
    sealed internal record FindContext
    {
        public FindContext(string home, IReadOnlyDictionary<string, DocumentMeta> documents, bool outputs)
        {
            Home = home;
            Documents = documents;
            Outputs = outputs;
        }

        public string Home { get; init; }

        // Lets a client report a session under the ID its transcript uses.
        public IReadOnlyDictionary<string, DocumentMeta> Documents { get; init; }

        public bool Outputs { get; init; }
    }
}
