using System.Text.Json;

namespace SessionMemory.Commands
{
    /// <summary>capture: brings the Markdown corpus up to date once and prints what the capture found.</summary>
    sealed internal class CaptureCommand : ICommand
    {
        private readonly Corpus _corpus;

        public CaptureCommand(Corpus corpus)
        {
            _corpus = corpus;
        }

        public string Verb => "capture";

        public bool RequiresArguments => false;

        public int Execute(CommandArguments arguments)
        {
            var report = _corpus.Capture();
            var manifest = report.Manifest;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                report.Changed,
                report.DurationSeconds,
                report.Reparsed,
                manifest.CorpusHash,
                manifest.DocumentCount,
                manifest.MessageCount,
                manifest.Bytes,
                manifest.Families
            }, Store.Options));
            return 0;
        }
    }
}
