using Microsoft.Extensions.DependencyInjection;
using SessionMemory.Commands;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private static void ExactMessageChecks()
        {
            var temporary = Directory.CreateTempSubdirectory("session-memory-exact-message-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            var root = temporary.FullName;
            var corpus = Path.Combine(root, "corpus");
            Directory.CreateDirectory(corpus);
            const string id = "exact-session-12345";
            const string file = "exact.md";
            var transcript =
                "## Message 000001 part 1/1\nrole: user\ntimestamp: 2026-01-01T00:00:00Z\ntext:\nEarlier request.\n"
                + "## Message 000002 part 1/2\nrole: assistant\ntimestamp: 2026-01-01T03:00:00+02:00\ntext:\nBeginning " + new string('a', 250) + "\n"
                + "## Message 000002 part 2/2\nrole: assistant\ntimestamp: 2026-01-01T03:00:00+02:00\ntext:\nMiddle fact and end.\n"
                + "## Message 000003 part 1/1\nrole: user\ntimestamp: 2026-01-01T01:01:00Z\ntext:\nLater question.\n";
            File.WriteAllText(Path.Combine(corpus, file), transcript);
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest
            {
                Documents = new Dictionary<string, DocumentMeta> { [file] = new() { SessionId = id, Client = "test", Title = "Example" } }
            });
            using var services = Services(Path.Combine(root, "home"), root);
            var reader = services.GetRequiredService<SessionReader>();
            var request = new ShowRequest { Id = id, MessageNumber = 2, Chars = 6000 };
            var exact = reader.Show(request);
            Check(exact.IsSuccess && exact.Value.Contains("== " + id, StringComparison.Ordinal)
                  && exact.Value.Contains("[#2 assistant 2026-01-01 01:00 UTC]", StringComparison.Ordinal)
                  && exact.Value.Contains("Beginning ", StringComparison.Ordinal) && exact.Value.Contains("Middle fact and end.", StringComparison.Ordinal)
                  && !exact.Value.Contains("Earlier request.", StringComparison.Ordinal) && !exact.Value.Contains("Later question.", StringComparison.Ordinal),
                "shows the joined original message with identity and UTC time");
            var neighbors = reader.Show(request with { Context = 1 });
            Check(neighbors.IsSuccess && neighbors.Value.Contains("Earlier request.", StringComparison.Ordinal)
                                      && neighbors.Value.Contains("Middle fact and end.", StringComparison.Ordinal)
                                      && neighbors.Value.Contains("Later question.", StringComparison.Ordinal),
                "shows bounded neighboring messages around the exact message");
            var clipped = reader.Show(request with { Chars = 200 });
            Check(clipped.IsSuccess && clipped.Value.Contains("chars]", StringComparison.Ordinal)
                                    && !clipped.Value.Contains("Middle fact and end.", StringComparison.Ordinal),
                "marks a truncated exact message");
            var firstPage = reader.Show(request with { Chars = 200, Offset = 0 });
            var nextPage = reader.Show(request with { Chars = 200, Offset = 200 });
            Check(firstPage.IsSuccess && firstPage.Value.Contains("next offset 200", StringComparison.Ordinal)
                  && nextPage.IsSuccess && nextPage.Value.Contains("[offset 200; next offset end;", StringComparison.Ordinal)
                  && nextPage.Value.Contains("Middle fact and end.", StringComparison.Ordinal)
                  && nextPage.Value.Contains("[#2 assistant 2026-01-01 01:00 UTC]", StringComparison.Ordinal),
                "paginates the joined message without changing its citation");
            Check(reader.Show(request with { MessageNumber = 9 }) is { IsFailure: true, Error.ExitCode: 2 }
                  && reader.Show(request with { MessageNumber = 0 }) is { IsFailure: true, Error.ExitCode: 2 }
                  && reader.Show(request with { Offset = -1 }) is { IsFailure: true, Error.ExitCode: 2 }
                  && reader.Show(request with { Offset = 9999 }) is { IsFailure: true, Error.ExitCode: 2 }
                  && reader.Show(request with { MessageNumber = null, Offset = 1 }) is { IsFailure: true, Error.ExitCode: 2 }
                  && reader.Show(request with { Around = "fact" }) is { IsFailure: true, Error.ExitCode: 2 },
                "rejects missing messages, nonpositive numbers and conflicting selection");

            var command = new ShowCommand(reader, services.GetRequiredService<CurrentSession>());
            var savedError = Console.Error;
            using (var captured = new StringWriter())
            {
                Console.SetError(captured);
                try
                {
                    Check(command.Execute(new CommandArguments(["show", id, "--message", "0"])) == 2
                          && command.Execute(new CommandArguments(["show", id, "--message", "no"])) == 2
                          && command.Execute(new CommandArguments(["show", id, "--message"])) == 2
                          && command.Execute(new CommandArguments(["show", id, "--message", "2", "--offset", "-1"])) == 2
                          && command.Execute(new CommandArguments(["show", id, "--message", "2", "--offset", "bad"])) == 2
                          && command.Execute(new CommandArguments(["show", id, "--message", "2", "--offset"])) == 2
                          && command.Execute(new CommandArguments(["show", id, "--offset", "0"])) == 2
                          && command.Execute(new CommandArguments(["show", id, "--message", "2", "--around", "fact"])) == 2,
                        "rejects invalid or conflicting command options");
                }
                finally { Console.SetError(savedError); }
            }

            var unicodeText = new string('x', 199) + "\U0001F600" + " after the face.";
            File.WriteAllText(Path.Combine(corpus, file),
                "## Message 000002 part 1/1\nrole: assistant\ntimestamp: 2026-01-01T03:00:00+02:00\ntext:\n" + unicodeText + "\n");
            var beforeFace = reader.Show(request with { Chars = 200 });
            var throughFace = reader.Show(request with { Chars = 200, Offset = 200 });
            Check(beforeFace.IsSuccess && beforeFace.Value.Contains("next offset 199", StringComparison.Ordinal)
                  && throughFace.IsSuccess && throughFace.Value.Contains("[offset 199;", StringComparison.Ordinal)
                  && throughFace.Value.Contains("\U0001F600 after the face.", StringComparison.Ordinal),
                "does not split a UTF-16 surrogate pair at a page boundary");
            var spacedText = string.Concat(Enumerable.Repeat("row\n\n", 50)) + "needle is here.";
            File.WriteAllText(Path.Combine(corpus, file),
                "## Message 000002 part 1/1\nrole: assistant\ntimestamp: 2026-01-01T03:00:00+02:00\ntext:\n" + spacedText + "\n");
            var spacedOffset = spacedText.IndexOf("needle", StringComparison.Ordinal);
            var spaced = reader.Show(request with { Chars = 200, Offset = spacedOffset });
            Check(spaced.IsSuccess && spaced.Value.Contains($"[offset {spacedOffset};", StringComparison.Ordinal)
                                   && spaced.Value.Contains("needle is here.", StringComparison.Ordinal),
                "uses navigation offsets from original messages containing blank lines");
            var embeddedHeader = "Before the example.\n\n## Message 000099 part 1/1\nrole: user\ntimestamp: 2026-01-02T00:00:00Z\ntext:\nThis is quoted transcript syntax.\n\n"
                                 + string.Concat(Enumerable.Repeat("boundary words ", 100)) + "\n\nAfter the example.";
            var rendered = Transcript.Render(new Document("test", "test", id, "ref",
                [new Message("m1", "assistant", embeddedHeader, 0, "2026-01-01T00:00:00Z")])).Content;
            File.WriteAllText(Path.Combine(corpus, file), rendered);
            var embedded = reader.Show(request with { MessageNumber = 1, Chars = 6000 });
            Check(embedded.IsSuccess && embedded.Value.Contains(Transcript.Normalize(embeddedHeader), StringComparison.Ordinal)
                  && reader.Show(request with { MessageNumber = 99 }).IsFailure
                  && Compact.Segments(rendered) is [{ Number: 1 }],
                "keeps embedded transcript headings and split-boundary whitespace inside the original message");

            File.WriteAllText(Path.Combine(corpus, file),
                "## Message 000002 part 1/1\nrole: assistant\ntext_length: 999\ntext:\nPartial message.");
            Check(reader.Show(request).IsFailure, "rejects a truncated framed source without throwing outside the read boundary");
            Check(Compact.Segments(File.ReadAllText(Path.Combine(corpus, file))).Any(segment => segment.Text.Contains("Partial message.", StringComparison.Ordinal)),
                "compact search passages tolerate a truncated framed message");

            var wordText = string.Concat(Enumerable.Repeat("alphabet ", 60)).TrimEnd();
            File.WriteAllText(Path.Combine(corpus, file),
                "## Message 000002 part 1/1\nrole: assistant\ntimestamp: 2026-01-01T03:00:00+02:00\ntext:\n" + wordText + "\n");
            var words = reader.Show(request with { Chars = 200, Offset = 203 });
            Check(words.IsSuccess && words.Value.Contains("[offset 198; next offset 396;", StringComparison.Ordinal),
                "aligns exact-message pages to short word boundaries");

            File.WriteAllText(Path.Combine(corpus, file),
                "## Message 000002 part 1/1\nrole: assistant\ntimestamp: 2026-01-01T00:00:00Z\ntext:\nFirst.\n"
                + "## Message 000002 part 1/1\nrole: assistant\ntimestamp: 2026-01-02T00:00:00Z\ntext:\nSecond.\n");
            Check(reader.Show(request) is { IsFailure: true, Error.ExitCode: 2 },
                "rejects ambiguous original message numbers rather than selecting one");
            File.WriteAllText(Path.Combine(corpus, file),
                "## Message 000002 part 1/1\nrole: assistant\ntimestamp: 2026-01-01T00:00:00Z\ntext:\n\n");
            var empty = reader.Show(request);
            Check(empty.IsSuccess && empty.Value.Contains("[offset 0; next offset end; total 0 UTF-16 units]", StringComparison.Ordinal)
                                  && reader.Show(request with { Offset = 1 }).IsFailure,
                "reads an empty original message at offset zero");

            var exclusionPath = Path.Combine(root, "state", "excluded.json");
            Store.Save(exclusionPath, new[] { id });
            Check(reader.Show(request) is { IsFailure: true, Error.ExitCode: 2 }, "does not expose an excluded message before capture");
            File.Delete(exclusionPath);
            var outside = Path.Combine(root, "outside.md");
            File.WriteAllText(outside, transcript);
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest
            {
                Documents = new Dictionary<string, DocumentMeta> { ["../outside.md"] = new() { SessionId = id } }
            });
            Check(reader.Show(request) is { IsFailure: true, Error.ExitCode: 2 }, "rejects manifest paths outside the corpus");
            var claimsPath = Path.Combine(root, "link-claims.json");
            Store.Save(claimsPath, new { claims = new[] { new { claim = "x", session_id = id, message_number = 1, quote = "Earlier request." } } });
            try
            {
                File.CreateSymbolicLink(Path.Combine(corpus, "link.md"), outside);
                Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest
                {
                    Documents = new Dictionary<string, DocumentMeta> { ["link.md"] = new() { SessionId = id } }
                });
                Check(reader.Show(request) is { IsFailure: true, Error.ExitCode: 2 }, "does not follow a corpus file link");
                var verified = services.GetRequiredService<ClaimVerifier>().Verify(claimsPath);
                Check(verified.IsSuccess && verified.Value.Contains("invalid_source", StringComparison.Ordinal),
                    "claim verification uses the same corpus file-link guard");
            }
            catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Console.Error.WriteLine("Self-test: exact-message symlink check skipped because file links are unavailable.");
            }
            try
            {
                var linkedRoot = Path.Combine(root, "linked-root");
                Directory.CreateDirectory(linkedRoot);
                Directory.CreateSymbolicLink(Path.Combine(linkedRoot, "corpus"), corpus);
                Store.Save(Path.Combine(linkedRoot, "state", "manifest.json"), new Manifest
                {
                    Documents = new Dictionary<string, DocumentMeta> { [file] = new() { SessionId = id } }
                });
                using var linkedServices = Services(Path.Combine(root, "home"), linkedRoot);
                Check(linkedServices.GetRequiredService<SessionReader>().Show(request) is { IsFailure: true, Error.ExitCode: 2 },
                    "does not follow a linked corpus directory");
                var verified = linkedServices.GetRequiredService<ClaimVerifier>().Verify(claimsPath);
                Check(verified.IsSuccess && verified.Value.Contains("invalid_source", StringComparison.Ordinal),
                    "claim verification uses the same corpus directory-link guard");
            }
            catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Console.Error.WriteLine("Self-test: exact-message directory link check skipped because directory links are unavailable.");
            }
        }
    }
}
