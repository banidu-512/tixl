using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace t3.streamdiffusion.Onnx;

/// <summary>
/// CLIP (ViT-L/14) byte-level BPE tokenizer for Stable Diffusion 1.x / SD-Turbo prompts.
/// Loads vocab from tokenizer.json or vocab.json + merges.txt next to the models.
/// </summary>
public sealed partial class ClipTokenizer
{
    public const int BosTokenId = 49406;
    public const int EosTokenId = 49407;
    public const int PadTokenId = 0; // "!"
    public const int MaxLength = 77;
    private const int MaxContentTokens = MaxLength - 2;

    private static readonly Lazy<Dictionary<int, string>> ByteToUnicodeMap = new(BuildByteToUnicode);

    private static readonly Regex SplitPattern = new(
        @"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
        RegexOptions.Compiled);

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, long> _vocab = new();
    private readonly Dictionary<(string First, string Second), int> _ranks = new();
    private readonly Dictionary<string, string[]> _bpeCache = new();

    public int VocabularySize => _vocab.Count;

    /// <summary>
    /// Creates a tokenizer from a model directory containing either tokenizer.json
    /// or vocab.json + merges.txt, either flat or inside a tokenizer/ subfolder
    /// (diffusers export layout).
    /// </summary>
    public static ClipTokenizer? FromModelDirectory(string modelDirectory)
    {
        foreach (var dir in new[] { modelDirectory, Path.Combine(modelDirectory, "tokenizer") })
        {
            var tokenizerJson = Path.Combine(dir, "tokenizer.json");
            if (File.Exists(tokenizerJson))
            {
                try
                {
                    return FromTokenizerJson(tokenizerJson);
                }
                catch
                {
                    // Fall through to vocab.json/merges.txt
                }
            }

            var vocabJson = Path.Combine(dir, "vocab.json");
            var mergesTxt = Path.Combine(dir, "merges.txt");
            if (File.Exists(vocabJson) && File.Exists(mergesTxt))
            {
                try
                {
                    return FromVocabAndMerges(vocabJson, mergesTxt);
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    public static ClipTokenizer FromTokenizerJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var model = document.RootElement.GetProperty("model");

        var tokenizer = new ClipTokenizer();
        foreach (var entry in model.GetProperty("vocab").EnumerateObject())
        {
            tokenizer._vocab[entry.Name] = entry.Value.GetInt64();
        }

        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            var parts = merge.GetString()?.Split(' ');
            if (parts is { Length: 2 })
            {
                tokenizer._ranks[(parts[0], parts[1])] = tokenizer._ranks.Count;
            }
        }

        return tokenizer;
    }

    public static ClipTokenizer FromVocabAndMerges(string vocabPath, string mergesPath)
    {
        var tokenizer = new ClipTokenizer();
        using var document = JsonDocument.Parse(File.ReadAllText(vocabPath));
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            tokenizer._vocab[entry.Name] = entry.Value.GetInt64();
        }

        foreach (var line in File.ReadAllLines(mergesPath))
        {
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            var parts = line.Split(' ');
            if (parts is { Length: 2 })
            {
                tokenizer._ranks[(parts[0], parts[1])] = tokenizer._ranks.Count;
            }
        }

        return tokenizer;
    }

    /// <summary>
    /// Encodes a prompt into a fixed-length (77) CLIP token id sequence:
    /// [BOS] content... [EOS] padding...
    /// </summary>
    public long[] Encode(string? text)
    {
        var ids = new List<long> { BosTokenId };

        if (!string.IsNullOrEmpty(text))
        {
            var normalized = WhitespacePattern.Replace(text.ToLowerInvariant().Trim(), " ");
            foreach (var word in SplitPattern.Matches(normalized).Select(m => m.Value))
            {
                foreach (var token in ByteLevelBpe(word))
                {
                    if (_vocab.TryGetValue(token, out var id))
                    {
                        ids.Add(id);
                        if (ids.Count >= MaxContentTokens + 1)
                            break;
                    }
                }

                if (ids.Count >= MaxContentTokens + 1)
                    break;
            }
        }

        ids.Add(EosTokenId);
        while (ids.Count < MaxLength)
        {
            ids.Add(PadTokenId);
        }

        return ids.ToArray();
    }

    private IEnumerable<string> ByteLevelBpe(string word)
    {
        if (_bpeCache.TryGetValue(word, out var cached))
            return cached;

        var byteToUnicode = ByteToUnicodeMap.Value;
        var symbols = new List<string>(word.Length);
        var bytes = Encoding.UTF8.GetBytes(word);
        foreach (var b in bytes)
        {
            symbols.Add(byteToUnicode[b]);
        }

        while (symbols.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;
            for (var i = 0; i < symbols.Count - 1; i++)
            {
                if (_ranks.TryGetValue((symbols[i], symbols[i + 1]), out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                break;

            var merged = symbols[bestIndex] + symbols[bestIndex + 1];
            symbols.RemoveAt(bestIndex + 1);
            symbols[bestIndex] = merged;
        }

        var result = symbols.ToArray();
        _bpeCache[word] = result;
        return result;
    }

    /// <summary>
    /// The GPT-2 byte-to-unicode table: printable bytes map to themselves,
    /// the rest to code points starting at 256 so every byte has a visible glyph.
    /// </summary>
    private static Dictionary<int, string> BuildByteToUnicode()
    {
        var bytes = new List<int>();
        for (var i = 33; i < 127; i++) bytes.Add(i);   // '!'..'~'
        for (var i = 161; i <= 172; i++) bytes.Add(i); // non-breaking space..'¬'
        for (var i = 174; i <= 255; i++) bytes.Add(i); // '®'..'ÿ'

        var map = new Dictionary<int, string>(256);
        var nextCodePoint = 256;
        for (var b = 0; b < 256; b++)
        {
            if (bytes.Contains(b))
            {
                map[b] = char.ConvertFromUtf32(b);
            }
            else
            {
                map[b] = char.ConvertFromUtf32(nextCodePoint++);
            }
        }

        return map;
    }
}
