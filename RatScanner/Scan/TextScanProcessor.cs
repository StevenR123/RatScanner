using OpenCvSharp;
using OpenCvSharp.Extensions;
using RatScanner.TarkovDev.Json;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tesseract;

namespace RatScanner.Scan;

/// <summary>
/// Reads the short name text rendered on top of an item cell and matches it
/// against the item database.
/// </summary>
public static class TextScanProcessor {
	private static readonly object EngineLock = new();
	private static TesseractEngine? _engine;
	private static RatStash.Language? _engineLanguage;
private static int _saveCounter;

	/// <summary>
	/// Extract and sanitize the text visible in the screenshot.
	/// Two binarization polarities are tried (light text on dark background and
	/// dark text on light background) and the higher-confidence result is kept.
	/// </summary>
	/// <param name="screenshot">Small region captured around the cursor</param>
	/// <param name="language">Language to use for OCR</param>
	/// <returns>Sanitized text or empty string if nothing was read</returns>
	public static string Read(Bitmap screenshot, RatStash.Language language) {
		lock (EngineLock) {
			try {
				string bestText = string.Empty;
				float bestConfidence = -1f;
				string bestCandidateName = string.Empty;

				int saveId = Interlocked.Increment(ref _saveCounter);
				SaveDebugImage(screenshot, $"textscan_{saveId:0000}_raw");

				using Mat src = BitmapConverter.ToMat(screenshot);
				using Mat gray = new();
				Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
				Cv2.MedianBlur(gray, gray, 3);

				// Local contrast enhancement before upscaling
				using CLAHE clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
				clahe.Apply(gray, gray);

				const int scale = 6;
				using Mat upscaled = new();
				Cv2.Resize(gray, upscaled, new OpenCvSharp.Size(gray.Width * scale, gray.Height * scale), 0, 0, InterpolationFlags.Cubic);

				// Feed Tesseract clean grayscale and let it binarize internally. Manual
				// thresholding fragments tiny text strokes, so we avoid it entirely.
				// Two polarities are tried in case the text is dark-on-light or light-on-dark.
				using Mat inverted = new();
				Cv2.BitwiseNot(upscaled, inverted);

				(Mat mat, string name)[] candidates = new[] { (upscaled, "normal"), (inverted, "inverted") };
				foreach ((Mat candidate, string name) in candidates) {
					using Mat bgr = new();
					Cv2.CvtColor(candidate, bgr, ColorConversionCodes.GRAY2BGR);
					using Bitmap processed = BitmapConverter.ToBitmap(bgr);
					SaveDebugImage(processed, $"textscan_{saveId:0000}_{name}");
					using Pix pix = PixConverter.ToPix(processed);
					using Page page = GetTesseractEngine(language).Process(pix);
					float confidence = page.GetMeanConfidence();
					string text = Sanitize(page.GetText());
					if (confidence > bestConfidence && !string.IsNullOrWhiteSpace(text)) {
						bestConfidence = confidence;
						bestText = text;
						bestCandidateName = name;
					}
				}

				Logger.LogInfo($"TextScan OCR: id={saveId}, meanConf={bestConfidence:0}, polarity={bestCandidateName}, text=\"{bestText}\"");
				return bestText;
			} catch (Exception e) {
				Logger.LogWarning("Text scan OCR failed", e);
				return string.Empty;
			}
		}
	}

	private static void SaveDebugImage(Bitmap bmp, string name) {
		if (!RatConfig.LogDebug) return;
		try {
			string dir = RatConfig.Paths.Debug;
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, name + ".png");
			bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
		} catch (Exception e) {
			Logger.LogWarning("Failed to save text scan debug image", e);
		}
	}

	/// <summary>
	/// Find the item whose short name best matches the sanitized OCR text
	/// </summary>
	/// <param name="text">Sanitized OCR text</param>
	/// <param name="minConfidence">Minimum normalized similarity (0-1) required for a match</param>
	/// <param name="confidence">The similarity of the returned match</param>
	/// <returns>The best matching item or <see langword="null"/></returns>
	public static Item? FindBestMatch(string text, float minConfidence, out float confidence) {
		confidence = 0f;
		if (string.IsNullOrWhiteSpace(text)) return null;

		Item? bestItem = null;
		Item[] items = TarkovDevAPI.GetItems();

		// Try to match the full text line first
		foreach (Item item in items) {
			if (string.IsNullOrEmpty(item.ShortName)) continue;
			float similarity = MaxSimilarity(text, Sanitize(item.ShortName));
			if (similarity <= confidence) continue;
			confidence = similarity;
			bestItem = item;
		}

		// If the full line is not confident enough, fall back to individual tokens.
		// Text from neighbouring cells can leak into the crop.
		if (confidence < minConfidence) {
			foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
				if (token.Length < 3) continue;
				foreach (Item item in items) {
					if (string.IsNullOrEmpty(item.ShortName)) continue;
					float similarity = MaxSimilarity(token, Sanitize(item.ShortName));
					if (similarity <= confidence) continue;
					confidence = similarity;
					bestItem = item;
				}
			}
		}

		if (bestItem == null || confidence < minConfidence) {
			Logger.LogInfo($"TextScan match: \"{text}\" -> no confident match (best={confidence:0.00})");
			return null;
		}

		Logger.LogInfo($"TextScan match: \"{text}\" -> {bestItem.ShortName} ({bestItem.Id}) conf={confidence:0.00}");
		return bestItem;
	}

	private static string Sanitize(string raw) {
		if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
		string cleaned = RatStash.Extensions.CyrillicToLatin(raw);
		cleaned = new string(cleaned.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '-').ToArray());
		return string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
	}

	/// <summary>
	/// Map characters that the bender font / OCR render nearly identically onto a
	/// single canonical form so small misreads don't break the match.
	/// </summary>
	private static string FoldConfusables(string s) {
		StringBuilder folded = new(s.Length);
		foreach (char c in s) {
			folded.Append(c switch {
				'i' or 't' or 'l' => 'i',
				'o' => '0',
				'b' => '8',
				'g' => '6',
				'z' => '2',
				's' => '5',
				_ => c,
			});
		}
		return folded.ToString();
	}

	/// <summary>
	/// Best similarity between two strings, using both exact comparison and a
	/// confusion-folded comparison.
	/// </summary>
	private static float MaxSimilarity(string a, string b) {
		return Math.Max(Similarity(a, b), Similarity(FoldConfusables(a), FoldConfusables(b)));
	}

	private static float Similarity(string a, string b) {
		if (a == b) return 1f;
		if (a.Length == 0 || b.Length == 0) return 0f;
		int distance = LevenshteinDistance(a, b);
		int maxLength = Math.Max(a.Length, b.Length);
		return 1f - (float)distance / maxLength;
	}

	private static int LevenshteinDistance(string a, string b) {
		if (a.Length == 0) return b.Length;
		if (b.Length == 0) return a.Length;

		int[] previous = new int[b.Length + 1];
		int[] current = new int[b.Length + 1];

		for (int j = 0; j <= b.Length; j++) previous[j] = j;

		for (int i = 1; i <= a.Length; i++) {
			current[0] = i;
			for (int j = 1; j <= b.Length; j++) {
				int cost = a[i - 1] == b[j - 1] ? 0 : 1;
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
			}
			(previous, current) = (current, previous);
		}

		return previous[b.Length];
	}

	private static TesseractEngine GetTesseractEngine(RatStash.Language language) {
		if (_engine != null && _engineLanguage == language) return _engine;

		_engine?.Dispose();

		string langCode = RatStash.LanguageConverter.ToISO3Code(language);
		string trainedDataPath = System.IO.Path.Combine(RatConfig.Paths.TrainedData, langCode + ".traineddata");
		if (!System.IO.File.Exists(trainedDataPath)) {
			throw new ArgumentException("Could not find traineddata at: " + trainedDataPath, nameof(language));
		}

		string addLang = language switch {
			RatStash.Language.Czech => "+eng",
			RatStash.Language.Japanese => "+eng",
			RatStash.Language.Korean => "+eng",
			RatStash.Language.Russian => "+eng",
			_ => "",
		};

		_engine = new TesseractEngine(RatConfig.Paths.TrainedData, langCode + addLang, EngineMode.LstmOnly);
		_engine.DefaultPageSegMode = PageSegMode.SingleLine;
		_engineLanguage = language;
		return _engine;
	}
}
