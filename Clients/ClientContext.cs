namespace SessionMemory.Clients
{
    /// <summary>
    /// What a client needs to list and parse its files: the user's home folder, where the assistants keep their stores,
    /// and this tool's root folder. Because every call passes it in, the clients keep no state and one instance serves every call.
    /// It is also registered as the data paths every service receives through its constructor.
    /// </summary>
    sealed internal record ClientContext
    {
        public ClientContext(string home, string root)
        {
            Home = home;
            Root = root;
        }

        public string Home { get; init; }

        public string Root { get; init; }
    }
}
