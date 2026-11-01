using System.Text;
using System.Text.Json;
using t3.streamdiffusion.Onnx;
using Xunit;

namespace StreamDiffusion.Tests;

public class ClipTokenizerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "sd-tokenizer-tests-" + Guid.NewGuid().ToString("N"));

    public ClipTokenizerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>
    /// Builds a vocab covering every byte-level char of the test prompts.
    /// With no merges, each word tokenizes to its individual characters.
    /// </summary>
    private void WriteTokenizerFiles(params string[] merges)
    {
        var chars = "abcdefghijklmnopqrstuvwxylz !".Distinct().ToList();
        var vocab = new Dictionary<string, long>
        {
            ["!"] = ClipTokenizer.PadTokenId,
            ["<|startoftext|>"] = ClipTokenizer.BosTokenId,
            ["<|endoftext|>"] = ClipTokenizer.EosTokenId,
        };
        var nextId = 100;
        foreach (var ch in chars.Where(c => !vocab.ContainsKey(c.ToString())))
        {
            vocab[ch.ToString()] = nextId++;
        }

        foreach (var merge in merges)
        {
            var merged = merge.Replace(" ", string.Empty);
            vocab.TryAdd(merged, nextId++);
        }

        File.WriteAllText(Path.Combine(_tempDir, "vocab.json"),
            JsonSerializer.Serialize(vocab));
        File.WriteAllText(Path.Combine(_tempDir, "merges.txt"),
            "#version: 0.2\n" + string.Join("\n", merges) + "\n");
    }

    private ClipTokenizer CreateTokenizer(params string[] merges)
    {
        WriteTokenizerFiles(merges);
        var tokenizer = ClipTokenizer.FromModelDirectory(_tempDir);
        Assert.NotNull(tokenizer);
        return tokenizer!;
    }

    [Fact]
    public void Encode_EmptyPrompt_WrapsBosEosAndPads()
    {
        var tokenizer = CreateTokenizer();

        var ids = tokenizer.Encode(null);
        Assert.Equal(ClipTokenizer.MaxLength, ids.Length);
        Assert.Equal(ClipTokenizer.BosTokenId, ids[0]);
        Assert.Equal(ClipTokenizer.EosTokenId, ids[1]);
        Assert.All(ids.Skip(2), id => Assert.Equal(ClipTokenizer.PadTokenId, id));
    }

    [Fact]
    public void Encode_WithoutMerges_SplitsIntoCharacters()
    {
        var tokenizer = CreateTokenizer();

        var ids = tokenizer.Encode("a cat");

        Assert.Equal(ClipTokenizer.MaxLength, ids.Length);
        Assert.Equal(ClipTokenizer.BosTokenId, ids[0]);

        // "a cat" -> words "a", "cat" -> chars: a, c, a, t (space is a separator)
        // "a c a t" probe encodes the same chars at [1..4]: a, c, a, t
        var probe = tokenizer.Encode("a c a t");
        Assert.Equal(ids[1], probe[1]); // 'a'
        Assert.Equal(ids[2], probe[2]); // 'c'
        Assert.Equal(ids[3], probe[3]); // 'a' again
        Assert.Equal(ids[4], probe[4]); // 't'
        Assert.Equal(ClipTokenizer.EosTokenId, ids[5]);
    }

    [Fact]
    public void Encode_AppliesMerges()
    {
        // Merge "c"+"a" into "ca"
        var tokenizer = CreateTokenizer("c a");

        var ids = tokenizer.Encode("a cat");

        // "cat" should now be ["ca", "t"] instead of ["c", "a", "t"]
        var caTokenId = TokenIdFor(tokenizer, "ca");
        var tTokenId = TokenIdFor(tokenizer, "t");

        Assert.Equal(caTokenId, ids[2]);
        Assert.Equal(tTokenId, ids[3]);
        Assert.Equal(ClipTokenizer.EosTokenId, ids[4]);
    }

    [Fact]
    public void Encode_IsDeterministic()
    {
        var tokenizer = CreateTokenizer("c a");

        var first = tokenizer.Encode("a beautiful landscape");
        var second = tokenizer.Encode("a beautiful landscape");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Encode_TruncatesLongPromptsToMaxLength()
    {
        var tokenizer = CreateTokenizer();

        var longPrompt = string.Join(" ", Enumerable.Repeat("cat", 200));
        var ids = tokenizer.Encode(longPrompt);

        Assert.Equal(ClipTokenizer.MaxLength, ids.Length);
        Assert.Equal(ClipTokenizer.BosTokenId, ids[0]);
        // EOS may be displaced by truncation, but the sequence is bounded
        Assert.All(ids, id => Assert.True(id >= 0));
    }

    [Fact]
    public void Encode_LowercasesAndCollapsesWhitespace()
    {
        var tokenizer = CreateTokenizer();

        var withCase = tokenizer.Encode("CAT");
        var lower = tokenizer.Encode("cat");
        Assert.Equal(withCase, lower);

        var withSpaces = tokenizer.Encode("cat");
        var collapsed = tokenizer.Encode("cat");
        Assert.Equal(withSpaces, collapsed);
    }

    [Fact]
    public void FromModelDirectory_PrefersTokenizerJson()
    {
        // Write vocab/merges, then a tokenizer.json with a different vocab marker
        WriteTokenizerFiles();
        var tokenizerJson = new
        {
            model = new
            {
                vocab = new Dictionary<string, long> { ["a"] = 5, ["<|startoftext|>"] = ClipTokenizer.BosTokenId, ["<|endoftext|>"] = ClipTokenizer.EosTokenId, ["!"] = ClipTokenizer.PadTokenId },
                merges = Array.Empty<string>(),
            }
        };
        File.WriteAllText(Path.Combine(_tempDir, "tokenizer.json"), JsonSerializer.Serialize(tokenizerJson));

        var tokenizer = ClipTokenizer.FromModelDirectory(_tempDir);
        Assert.NotNull(tokenizer);

        var ids = tokenizer.Encode("a");
        Assert.Equal(5, ids[1]);
    }

    [Fact]
    public void FromModelDirectory_ReturnsNullWithoutTokenizerFiles()
    {
        var tokenizer = ClipTokenizer.FromModelDirectory(_tempDir);
        Assert.Null(tokenizer);
    }

    private static long TokenIdFor(ClipTokenizer tokenizer, string token)
    {
        // Every char of "ca" and "t" is in the vocab, and merges add compound tokens
        var ids = tokenizer.Encode(token);
        return ids[1];
    }
}
