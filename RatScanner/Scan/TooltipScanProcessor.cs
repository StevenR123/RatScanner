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
using Rect = OpenCvSharp.Rect;

namespace RatScanner.Scan;

/// <summary>
/// Reads the short name text rendered on top of an item cell and matches it
/// against the item database.
/// </summary>
public static class TooltipScanProcessor {
	private static readonly object EngineLock = new();
	private static TesseractEngine? _engine;
	private static RatStash.Language? _engineLanguage;
private static int _saveCounter;

	/// <summary>
	/// Read the item name from the game's hover tooltip. The tooltip is a black box
	/// with bright text, so a simple fixed threshold produces clean, un-destroyed text.
	/// </summary>
	/// <param name="screenshot">Region captured above/right of the cursor</param>
	/// <param name="language">Language to use for OCR</param>
	/// <returns>Sanitized text or empty string if nothing was read</returns>
	public static string Read(Bitmap screenshot, RatStash.Language language) {
		lock (EngineLock) {
			try {
				int saveId = Interlocked.Increment(ref _saveCounter);
				SaveDebugImage(screenshot, $"tooltipscan_{saveId:0000}_raw");

				using Mat src = BitmapConverter.ToMat(screenshot);
				using Mat gray = new();
				Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

				// 1. Locate the tooltip: a pure-black box that contains bright text.
				// The stash has larger empty black areas, so simply picking the biggest
				// black region fails; instead pick the tooltip-sized box with text inside.
				Rect tooltipBox = FindTooltipBox(gray);
				if (tooltipBox.Width < 60 || tooltipBox.Height < 15) {
					Logger.LogInfo($"TooltipScan OCR: id={saveId}, no tooltip box found");
					return string.Empty;
				}

				// The item name can wrap across multiple lines. Read the entire text
				// region (all lines) and let OCR produce them in reading order.
				Rect textBox = FindTextBounds(gray, tooltipBox);
				if (textBox.Width < 10 || textBox.Height < 6) {
					Logger.LogInfo($"TooltipScan OCR: id={saveId}, no text found");
					return string.Empty;
				}

				using Mat band = gray[textBox].Clone();

				// 2. Fixed threshold inside the tooltip: text is light grey (~120) on pure
				// black, so a low threshold captures the text plus its anti-aliased edges.
				using Mat bin = new();
				Cv2.Threshold(band, bin, 30, 255, ThresholdTypes.BinaryInv);

				// Slightly thicken the thin text strokes to help Tesseract.
				using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2, 2));
				Cv2.Dilate(bin, bin, kernel);
				SaveDebugImage(BitmapConverter.ToBitmap(bin), $"tooltipscan_{saveId:0000}_bin");

				// 3. Upscale and OCR
				const int scale = 4;
				using Mat upscaled = new();
				Cv2.Resize(bin, upscaled, new OpenCvSharp.Size(bin.Width * scale, bin.Height * scale), 0, 0, InterpolationFlags.Cubic);
				using Mat bgr = new();
				Cv2.CvtColor(upscaled, bgr, ColorConversionCodes.GRAY2BGR);
				using Bitmap processed = BitmapConverter.ToBitmap(bgr);
				SaveDebugImage(processed, $"tooltipscan_{saveId:0000}_ocr");

				using Pix pix = PixConverter.ToPix(processed);
				using Page page = GetTesseractEngine(language).Process(pix);
				float confidence = page.GetMeanConfidence();
				string text = Sanitize(page.GetText());

				Logger.LogInfo($"TooltipScan OCR: id={saveId}, meanConf={confidence:0}, text=\"{text}\"");
				return text;
			} catch (Exception e) {
				Logger.LogWarning("Tooltip scan OCR failed", e);
				return string.Empty;
			}
		}
	}

	private static Rect FindTooltipBox(Mat gray) {
		using Mat darkMask = new();
		Cv2.Threshold(gray, darkMask, 10, 255, ThresholdTypes.BinaryInv);
		Cv2.FindContours(darkMask.Clone(), out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

		Rect best = new(0, 0, 0, 0);
		int bestTextPixels = 0;
		foreach (OpenCvSharp.Point[] contour in contours) {
			Rect r = Cv2.BoundingRect(contour);

			// A tooltip is at least one line of text; allow up to ~80px for multi-line
			// tooltips, and require it to be wider than it is tall.
			if (r.Height < 15 || r.Height > 80) continue;
			if (r.Width < 60) continue;
			if (r.Width < r.Height) continue;

			// Count bright text-core pixels inside the candidate; the tooltip is the
			// black box that actually contains text.
			using Mat roi = gray[r];
			using Mat textMask = new();
			Cv2.Threshold(roi, textMask, 100, 255, ThresholdTypes.Binary);
			int textPixels = Cv2.CountNonZero(textMask);
			if (textPixels > bestTextPixels) {
				bestTextPixels = textPixels;
				best = r;
			}
		}

		// Require a minimum amount of text before accepting the box.
		return bestTextPixels >= 30 ? best : new Rect(0, 0, 0, 0);
	}

	/// <summary>
	/// Locate the bounding box of all text inside the tooltip box. Multi-line item
	/// names span several rows, and OCR needs the whole text region to read them in order.
	/// </summary>
	private static Rect FindTextBounds(Mat gray, Rect tooltipBox) {
		using Mat roi = gray[tooltipBox];
		using Mat textMask = new();
		Cv2.Threshold(roi, textMask, 100, 255, ThresholdTypes.Binary);

		using Mat points = new();
		Cv2.FindNonZero(textMask, points);
		if (points.Empty()) return new Rect(0, 0, 0, 0);

		Rect r = Cv2.BoundingRect(points);
		int x1 = Math.Max(0, r.X - 2);
		int y1 = Math.Max(0, r.Y - 2);
		int x2 = Math.Min(tooltipBox.Width - 1, r.X + r.Width + 1);
		int y2 = Math.Min(tooltipBox.Height - 1, r.Y + r.Height + 1);
		if (x2 <= x1 || y2 <= y1) return new Rect(0, 0, 0, 0);
		return new Rect(tooltipBox.X + x1, tooltipBox.Y + y1, x2 - x1 + 1, y2 - y1 + 1);
	}

	private static void SaveDebugImage(Bitmap bmp, string name) {
		if (!RatConfig.LogDebug) return;
		try {
			string dir = RatConfig.Paths.Debug;
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, name + ".png");
			bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
		} catch (Exception e) {
			Logger.LogWarning("Failed to save tooltip scan debug image", e);
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

		// Try to match the full text line against both the full name and the short name
		foreach (Item item in items) {
			float similarity = Math.Max(
				string.IsNullOrEmpty(item.ShortName) ? 0f : MaxSimilarity(text, Sanitize(item.ShortName)),
				string.IsNullOrEmpty(item.Name) ? 0f : MaxSimilarity(text, Sanitize(item.Name)));
			if (similarity <= confidence) continue;
			confidence = similarity;
			bestItem = item;
		}

		// If the full line is not confident enough, fall back to individual tokens.
		if (confidence < minConfidence) {
			foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
				if (token.Length < 3) continue;
				foreach (Item item in items) {
					float similarity = Math.Max(
						string.IsNullOrEmpty(item.ShortName) ? 0f : MaxSimilarity(token, Sanitize(item.ShortName)),
						string.IsNullOrEmpty(item.Name) ? 0f : MaxSimilarity(token, Sanitize(item.Name)));
					if (similarity <= confidence) continue;
					confidence = similarity;
					bestItem = item;
				}
			}
		}

		if (bestItem == null || confidence < minConfidence) {
			Logger.LogInfo($"TooltipScan match: \"{text}\" -> no confident match (best={confidence:0.00})");
			return null;
		}

		Logger.LogInfo($"TooltipScan match: \"{text}\" -> {bestItem.ShortName} ({bestItem.Id}) conf={confidence:0.00}");
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
		_engine.DefaultPageSegMode = PageSegMode.SingleBlock;
		_engineLanguage = language;
		return _engine;
	}
}
