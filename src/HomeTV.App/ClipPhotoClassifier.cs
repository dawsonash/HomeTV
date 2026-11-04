using System.Text.Json;
using System.Text.RegularExpressions;
using HomeTV.Core.Photos;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HomeTV.App;

/// <summary>
/// Sorts background photos with OpenAI's CLIP (ViT-B/32, ONNX export from Hugging Face), entirely on the box: no photo
/// leaves the house. CLIP scores how well an image matches a sentence, so each category is a handful of sentences
/// ("a family photo", "a photo of mountains") and a photo goes where its best-matching sentences are.
/// The model (~600 MB) is downloaded on first start into <c>HomeTV:ModelsDir</c>.
/// </summary>
internal sealed partial class ClipPhotoClassifier : IPhotoClassifier, IDisposable
{
    private const string ModelRepo = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/";
    private static readonly string[] ModelFiles = ["onnx/vision_model.onnx", "onnx/text_model.onnx", "vocab.json", "merges.txt"];

    /// <summary>CLIP's learned temperature: similarities are multiplied by this before the softmax.</summary>
    private const float LogitScale = 100f;
    private const int ImageSize = 224;
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    /// <summary>A photo is "people" or "nature" when at least this share of the subject sentences' weight is on that category.</summary>
    internal const float SubjectThreshold = 0.35f;
    /// <summary>Not safe for work once this share of the safety sentences' weight is on the unsafe side.</summary>
    internal const float UnsafeThreshold = 0.5f;

    // Subject: one softmax over all three groups, so "other" soaks up food, screenshots, rooms and so on.
    private static readonly string[] PeoplePrompts =
    [
        "a photo of a person", "a photo of people", "a selfie", "a group photo of friends", "a family photo",
        "a portrait of a child", "a photo of a baby", "people playing a sport", "a couple posing for a photo",
    ];
    private static readonly string[] NaturePrompts =
    [
        "a landscape photo", "a photo of mountains", "a photo of a beach", "a photo of a forest", "a sunset",
        "a photo of flowers", "a photo of the ocean", "a photo of a lake", "a photo of the sky and clouds",
        "a photo of wild animals in nature", "a waterfall", "a snowy landscape", "a photo of a garden",
    ];
    private static readonly string[] OtherPrompts =
    [
        "a photo of food", "a screenshot", "a photo of a document", "a photo of a city street", "a photo of a building",
        "a photo of a room", "a photo of a car", "a photo of a cat", "a photo of cats", "a photo of a dog",
        "a photo of a pet sleeping", "a photo of a sign", "a photo of an object", "a photo of a road",
    ];

    // Safety: its own softmax, unsafe against ordinary.
    private static readonly string[] UnsafePrompts =
    [
        "a nude photo", "a photo of a naked person", "sexually explicit content", "a person in lingerie",
        "people drinking alcohol at a party", "a person drinking beer",
        "a person giving the middle finger", "a photo of blood and gore", "a person smoking a cigarette",
    ];
    private static readonly string[] SafePrompts =
    [
        "a family photo", "an everyday photo", "a landscape", "a photo of a pet", "a photo of food", "people smiling",
        "kids playing", "people at a sports game", "a holiday photo", "a photo of a house", "a person wearing clothes",
        "people at a restaurant", "people at the beach", "a person swimming", "a person using a phone",
        "a photo of a street", "a road with cars", "a street sign", "a photo of a city", "a photo of animals",
        "an outdoor photo", "a selfie", "a photo of a room", "a portrait of a person",
        // Easily mistaken for unsafe: bare skin that's fine, and ordinary nights out.
        "a shirtless man", "a person in a swimsuit", "a baby", "a close-up of a face", "a person in a tank top",
        "friends hanging out at night", "a flash photo taken at night", "people at a concert",
    ];

    private readonly string _modelsDir;
    private readonly HttpClient _http;
    private readonly ILogger<ClipPhotoClassifier> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _vision;
    private float[][]? _subjectText;
    private float[][]? _safetyText;

    public ClipPhotoClassifier(string modelsDir, HttpClient http, ILogger<ClipPhotoClassifier> logger)
    {
        _modelsDir = modelsDir;
        _http = http;
        _logger = logger;
    }

    public bool IsReady => _vision is not null;
    public string? Status { get; private set; } = "Getting the image model ready…";

    /// <summary>Downloads the model if needed and loads it. Safe to call again after a failure.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (IsReady)
            return;
        await _gate.WaitAsync(ct);
        try
        {
            if (IsReady)
                return;
            await DownloadAsync(ct);
            Status = "Loading the image model…";
            await Task.Run(() =>
            {
                var options = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                // Leave most of the CPU to whatever's on the TV; sorting is background work.
                options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 4);
                using (var text = new InferenceSession(Path.Combine(_modelsDir, "text_model.onnx"), options))
                {
                    var tokenizer = ClipTokenizer.Load(Path.Combine(_modelsDir, "vocab.json"), Path.Combine(_modelsDir, "merges.txt"));
                    float[][] Embed(IEnumerable<string> prompts) => prompts.Select(p => EmbedText(text, tokenizer, p)).ToArray();
                    _subjectText = Embed([.. PeoplePrompts, .. NaturePrompts, .. OtherPrompts]);
                    _safetyText = Embed([.. UnsafePrompts, .. SafePrompts]);
                }
                _vision = new InferenceSession(Path.Combine(_modelsDir, "vision_model.onnx"), options);
            }, ct);
            Status = null;
            _logger.LogInformation("Photo sorting model loaded from {Dir}", _modelsDir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"The image model couldn't be loaded ({ex.Message}). Trying again later.";
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PhotoTags> ClassifyAsync(string imagePath, CancellationToken ct = default)
    {
        if (!IsReady)
            throw new InvalidOperationException("The image model isn't loaded yet.");
        await _gate.WaitAsync(ct);
        try
        {
            var scores = await Task.Run(() => Score(imagePath), ct);
            _logger.LogDebug("Scores for {Name}: people {People:0.00}, nature {Nature:0.00}, unsafe {Unsafe:0.00} ({Top})",
                Path.GetFileName(imagePath), scores.People, scores.Nature, scores.Unsafe, scores.TopSafety);
            return new PhotoTags(
                People: scores.People >= SubjectThreshold,
                Nature: scores.Nature >= SubjectThreshold,
                SafeForWork: scores.Unsafe < UnsafeThreshold,
                Manual: false,
                Version: "");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal record Scores(float People, float Nature, float Unsafe, string TopSafety = "");

    internal Scores Score(string imagePath)
    {
        var image = EmbedImage(_vision!, imagePath);
        var subject = Softmax(_subjectText!.Select(t => LogitScale * Dot(image, t)).ToArray());
        var safety = Softmax(_safetyText!.Select(t => LogitScale * Dot(image, t)).ToArray());
        return new Scores(
            People: subject.Take(PeoplePrompts.Length).Sum(),
            Nature: subject.Skip(PeoplePrompts.Length).Take(NaturePrompts.Length).Sum(),
            Unsafe: safety.Take(UnsafePrompts.Length).Sum(),
            TopSafety: ((string[])[.. UnsafePrompts, .. SafePrompts])[Array.IndexOf(safety, safety.Max())]);
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_modelsDir);
        foreach (var file in ModelFiles)
        {
            var target = Path.Combine(_modelsDir, Path.GetFileName(file));
            if (File.Exists(target))
                continue;
            _logger.LogInformation("Downloading {File} for photo sorting", file);
            var partial = target + ".download";
            using (var response = await _http.GetAsync(ModelRepo + file, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = File.Create(partial);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 1_000_000)
                        Status = $"Downloading the image model ({Path.GetFileName(file)}, {done * 100 / total}%)…";
                }
            }
            File.Move(partial, target, overwrite: true);
        }
    }

    /// <summary>CLIP's preprocessing: upright, shortest side to 224 (bicubic), centre 224×224, normalized.</summary>
    private static float[] EmbedImage(InferenceSession vision, string path)
    {
        using var image = new MagickImage(path);
        image.AutoOrient();
        image.ColorSpace = ColorSpace.sRGB;
        image.FilterType = FilterType.Catrom; // Bicubic.
        image.Resize(new MagickGeometry(ImageSize, ImageSize) { FillArea = true });
        image.Crop(ImageSize, ImageSize, Gravity.Center);
        image.ResetPage();
        var pixels = image.GetPixels().ToByteArray(PixelMapping.RGB)!;

        var input = new DenseTensor<float>([1, 3, ImageSize, ImageSize]);
        for (var y = 0; y < ImageSize; y++)
            for (var x = 0; x < ImageSize; x++)
                for (var c = 0; c < 3; c++)
                    input[0, c, y, x] = (pixels[(y * ImageSize + x) * 3 + c] / 255f - Mean[c]) / Std[c];

        using var results = vision.Run([NamedOnnxValue.CreateFromTensor("pixel_values", input)]);
        return Normalize(results.First(r => r.Name == "image_embeds").AsEnumerable<float>().ToArray());
    }

    private static float[] EmbedText(InferenceSession text, ClipTokenizer tokenizer, string prompt)
    {
        var ids = tokenizer.Encode(prompt);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids.Select(i => (long)i).ToArray(), [1, ids.Length])),
        };
        if (text.InputMetadata.ContainsKey("attention_mask"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), [1, ids.Length])));
        using var results = text.Run(inputs);
        return Normalize(results.First(r => r.Name == "text_embeds").AsEnumerable<float>().ToArray());
    }

    private static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private static float[] Softmax(float[] logits)
    {
        var max = logits.Max();
        var exp = logits.Select(l => MathF.Exp(l - max)).ToArray();
        var total = exp.Sum();
        return exp.Select(e => e / total).ToArray();
    }

    public void Dispose() => _vision?.Dispose();

    /// <summary>
    /// CLIP's byte-pair tokenizer, enough for the plain English prompts above: lower-cased, split into words, each
    /// word merged by rank with "&lt;/w&gt;" on its last letter, between start and end tokens.
    /// </summary>
    internal sealed partial class ClipTokenizer
    {
        private readonly Dictionary<string, int> _vocab;
        private readonly Dictionary<(string, string), int> _ranks;

        private ClipTokenizer(Dictionary<string, int> vocab, Dictionary<(string, string), int> ranks)
        {
            _vocab = vocab;
            _ranks = ranks;
        }

        public static ClipTokenizer Load(string vocabPath, string mergesPath)
        {
            var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabPath))!;
            var ranks = new Dictionary<(string, string), int>();
            foreach (var line in File.ReadLines(mergesPath).Skip(1))
            {
                var parts = line.Split(' ');
                if (parts.Length == 2)
                    ranks.TryAdd((parts[0], parts[1]), ranks.Count);
            }
            return new ClipTokenizer(vocab, ranks);
        }

        public int[] Encode(string text)
        {
            var ids = new List<int> { _vocab["<|startoftext|>"] };
            foreach (Match word in Words().Matches(Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ")))
            {
                if (word.Value.Any(c => c > 0x7e || c < 0x21))
                    throw new ArgumentException($"Prompts must be plain ASCII: {text}");
                foreach (var piece in Bpe(word.Value))
                    ids.Add(_vocab[piece]);
            }
            ids.Add(_vocab["<|endoftext|>"]);
            return [.. ids];
        }

        private List<string> Bpe(string word)
        {
            var parts = word.Select(c => c.ToString()).ToList();
            parts[^1] += "</w>";
            while (parts.Count > 1)
            {
                var best = -1;
                var bestRank = int.MaxValue;
                for (var i = 0; i < parts.Count - 1; i++)
                    if (_ranks.TryGetValue((parts[i], parts[i + 1]), out var rank) && rank < bestRank)
                        (best, bestRank) = (i, rank);
                if (best < 0)
                    break;
                // Merge every occurrence of the pair, left to right, as the reference implementation does.
                var (a, b) = (parts[best], parts[best + 1]);
                var merged = new List<string>(parts.Count);
                for (var i = 0; i < parts.Count; i++)
                {
                    if (i < parts.Count - 1 && parts[i] == a && parts[i + 1] == b)
                    {
                        merged.Add(a + b);
                        i++;
                    }
                    else
                        merged.Add(parts[i]);
                }
                parts = merged;
            }
            return parts;
        }

        [GeneratedRegex(@"'s|'t|'re|'ve|'m|'ll|'d|\p{L}+|\p{N}|[^\s\p{L}\p{N}]+")]
        private static partial Regex Words();
    }
}
