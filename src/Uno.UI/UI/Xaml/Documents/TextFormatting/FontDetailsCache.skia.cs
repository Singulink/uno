#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Uno.Extensions;
using Uno.Foundation.Logging;
using Uno.UI;
using Uno.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Helpers;
using Windows.UI.Text;
using Uno.Foundation.Extensibility;
using Uno.Helpers;
using SKFontStyleWidth = SkiaSharp.SKFontStyleWidth;

namespace Microsoft.UI.Xaml.Documents.TextFormatting;

/// <remarks>
/// Skia uses the word "typeface" to mean a specific style of a typographic family (e.g. OpenSans with Bold weight, Normal width and Italic slant)
/// and the word "font" to mean a typeface + a specific font size. This is different from the literature where "typeface"
/// means a typographic family (e.g. OpenSans or Segoe UI) and "font" means what Skia means by "typeface".
/// We try to use Skia's wording for code and the accurate wording for logging.
/// </remarks>
internal static class FontDetailsCache
{
	private readonly record struct FontEntry(
		string Name,
		SKFontStyleWeight Weight,
		SKFontStyleWidth Width,
		SKFontStyleSlant Slant);

	private static readonly Dictionary<FontEntry, Task<SKTypeface?>> _fontCache = new();
	private static readonly object _fontCacheGate = new();
	// The installed families don't change during a session, and the lookup can scan them all (fontconfig), so each
	// name is checked once rather than once per requested style. Ordinal, because the platform lookups may not ignore case.
	private static readonly ConcurrentDictionary<string, bool> _familyExists = new(StringComparer.Ordinal);
	private static readonly IFontFallbackService? _fontFallbackService =
		FeatureConfiguration.Font.FallbackService
		?? (ApiExtensibility.CreateInstance<IFontFallbackService>(typeof(FontDetailsCache), out var service) ? service : null);

	private static async Task<SKTypeface?> LoadTypefaceFromApplicationUriAsync(Uri uri, FontWeight weight, FontStyle style, FontStretch stretch)
	{
		try
		{
			var manifestUri = new Uri(uri.OriginalString + ".manifest");
			var path = Uri.UnescapeDataString(manifestUri.PathAndQuery).TrimStart('/');
			if (await StorageFileHelper.ExistsInPackage(path))
			{
				var manifestFile = await StorageFile.GetFileFromApplicationUriAsync(manifestUri);
				using var manifestStream = await manifestFile.OpenStreamForReadAsync();
				uri = new Uri(FontManifestHelpers.GetFamilyNameFromManifest(manifestStream, weight, style, stretch));
			}
		}
		catch (Exception e)
		{
			if (typeof(FontDetailsCache).Log().IsEnabled(LogLevel.Error))
			{
				typeof(FontDetailsCache).Log().LogError($"Failed to load font manifest for {uri}: {e}");
			}
		}

		if (typeof(FontDetailsCache).Log().IsEnabled(LogLevel.Debug))
		{
			typeof(FontDetailsCache).Log().LogDebug($"Fetching font from {uri}");
		}

		try
		{
			using var stream = await AppDataUriEvaluator.ToStream(uri, CancellationToken.None);
			return SKTypeface.FromStream(stream);
		}
		catch (Exception e)
		{
			typeof(FontDetailsCache).LogError()?.Error($"Loading font from {uri} failed: {e}");
			return null;
		}
	}

	private static async Task<SKTypeface?> GetFontInternal(
		string name,
		FontWeight weight,
		FontStretch stretch,
		FontStyle style)
	{
		var skWeight = weight.ToSkiaWeight();
		var skWidth = stretch.ToSkiaWidth();
		var skSlant = style.ToSkiaSlant();

		var hashIndex = name.IndexOf('#');
		if (hashIndex > 0)
		{
			name = name.Substring(0, hashIndex);
		}

		if (Uri.TryCreate(name, UriKind.Absolute, out var uri))
		{
			return await LoadTypefaceFromApplicationUriAsync(uri, weight, style, stretch);
		}

		SKTypeface? fallbackTypeface = null;
		if (_fontFallbackService is { } fallbackService)
		{
			try
			{
				if (await fallbackService.GetFontStreamForFontFamily(name, weight, stretch, style) is { } fallbackStream)
				{
					fallbackTypeface = SKTypeface.FromStream(fallbackStream);
				}
			}
			catch (Exception e)
			{
				typeof(FontDetailsCache).LogError()?.Error($"Font fallback service threw resolving {name}", e);
			}
		}

		// FromFamilyName substitutes the font manager's default typeface for an unknown family rather than
		// failing (on WASM that is Skia's embedded monospace font), so ask the font manager first.
		// FromFontFamilyName may return null: https://github.com/mono/SkiaSharp/issues/1058
		// The default family may be one the platform doesn't have (Segoe UI outside Windows): Skia's default typeface
		// then stands in for it, rather than the default text font being reported as failing to load.
		return fallbackTypeface
			?? (FamilyExists(name) ? SKTypeface.FromFamilyName(name, skWeight, skWidth, skSlant) : null)
			?? (string.Equals(name, FeatureConfiguration.Font.DefaultTextFontFamily, StringComparison.OrdinalIgnoreCase)
				? SKTypeface.FromFamilyName(null, skWeight, skWidth, skSlant) ?? SKTypeface.FromFamilyName(null)
				: null);
	}

	private static bool FamilyExists(string familyName)
		=> _familyExists.GetOrAdd(familyName, static name =>
		{
			// CoreText's FromFamilyName maps these CSS generic names to Helvetica, Times and Courier, but its family
			// lookup doesn't (map_css_names in SkFontMgr_mac_ct.cpp), so they count as present there.
			if ((OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst())
				&& name is "sans-serif" or "serif" or "monospace")
			{
				return true;
			}

			using var styles = SKFontManager.Default.GetFontStyles(name);
			return styles is { Count: > 0 };
		});

	private static readonly Func<string?, float, FontWeight, FontStretch, FontStyle, (FontDetails details, Task<FontDetails> loadedTask)> _getFont = FuncMemoizeExtensions.AsLockedMemoized((
		string? name,
		float fontSize,
		FontWeight weight,
		FontStretch stretch,
		FontStyle style) =>
	{
		if (name == null || string.Equals(name, "XamlAutoFontFamily", StringComparison.OrdinalIgnoreCase))
		{
			name = FeatureConfiguration.Font.DefaultTextFontFamily;
		}

		var (skWeight, skWidth, skSlant) = (weight.ToSkiaWeight(), stretch.ToSkiaWidth(), style.ToSkiaSlant());
		var key = new FontEntry(name, skWeight, skWidth, skSlant);

		Task<SKTypeface?> typefaceTask;
		lock (_fontCacheGate)
		{
			if (!_fontCache.TryGetValue(key, out var nullableTask))
			{
				_fontCache[key] = nullableTask = GetFontInternal(name, weight, stretch, style);
			}
			typefaceTask = nullableTask;
		}

		var canChange = !typefaceTask.IsCompleted; // don't read from task.IsCompleted again, it could've changed
		var typeface = !canChange ? typefaceTask.Result : null;
		Task<SKTypeface?>? defaultTypefaceTask = null;

		if (typeface == null)
		{
			if (typeof(Inline).Log().IsEnabled(LogLevel.Debug))
			{
				if (canChange)
				{
					typeof(Inline).Log().LogDebug($"{key} is still loading, using system default for now.");
				}
				else
				{
					typeof(Inline).Log().LogDebug($"{key} could not be found, using system default");
				}
			}

			// The default text font is often a URI-backed font that only the loader resolves: FromFamilyName finds nothing
			// and this would end on Skia's default typeface (its embedded monospace font on WASM).
			defaultTypefaceTask = GetDefaultTypefaceTask(name, weight, stretch, style);
			typeface = (defaultTypefaceTask is { IsCompletedSuccessfully: true } ? defaultTypefaceTask.Result : null)
						?? SKTypeface.FromFamilyName(FeatureConfiguration.Font.DefaultTextFontFamily, skWeight, skWidth, skSlant)
						?? SKTypeface.FromFamilyName(null, skWeight, skWidth, skSlant)
						?? SKTypeface.FromFamilyName(null);
		}

		var details = FontDetails.Create(typeface, fontSize);
		var detailsTask = AwaitDetails(typefaceTask);
		return (details, detailsTask);

		async Task<FontDetails> AwaitDetails(Task<SKTypeface?> t)
		{
			SKTypeface? loadedTypeface = null;
			Exception? exception = null;

			try
			{
				loadedTypeface = await t;
			}
			catch (Exception e)
			{
				exception = e;
			}

			// A family name the platform doesn't have quietly gets the default text font, but a font that threw or a
			// font file that didn't load is still reported.
			var isReported = loadedTypeface is null && (exception is not null || Uri.TryCreate(key.Name, UriKind.Absolute, out _));

			if (loadedTypeface is null && defaultTypefaceTask is not null)
			{
				try
				{
					loadedTypeface = await defaultTypefaceTask;
				}
				catch (Exception e)
				{
					exception ??= e;
				}
			}

			if ((loadedTypeface is null || isReported) && typeof(FontDetailsCache).Log().IsEnabled(LogLevel.Error))
			{
				typeof(FontDetailsCache).Log().LogError($"Failed to load {key}", exception);
			}

			if (loadedTypeface is null)
			{
				return details;
			}
			else
			{
				return FontDetails.Create(loadedTypeface, details.SKFontSize);
			}
		}
	});

	/// <summary>
	/// Resolves the default text font for a family that could not be resolved, or returns null when the family is
	/// the default text font itself.
	/// </summary>
	private static Task<SKTypeface?>? GetDefaultTypefaceTask(string name, FontWeight weight, FontStretch stretch, FontStyle style)
	{
		var defaultName = FeatureConfiguration.Font.DefaultTextFontFamily;
		if (string.IsNullOrEmpty(defaultName) || string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var key = new FontEntry(defaultName, weight.ToSkiaWeight(), stretch.ToSkiaWidth(), style.ToSkiaSlant());
		lock (_fontCacheGate)
		{
			if (!_fontCache.TryGetValue(key, out var task))
			{
				_fontCache[key] = task = GetFontInternal(defaultName, weight, stretch, style);
			}

			return task;
		}
	}

	public static (FontDetails details, Task<FontDetails> loadedTask) GetFont(
		string? name,
		float fontSize,
		FontWeight weight,
		FontStretch stretch,
		FontStyle style) => _getFont(name, fontSize, weight, stretch, style);

	public static async Task<FontDetails?> GetFontForCodepoint(
		int codepoint,
		float fontSize,
		FontWeight weight,
		FontStretch stretch,
		FontStyle style)
	{
		if (_fontFallbackService is { } fallbackService)
		{
			string? fallbackServiceResult = null;
			try
			{
				fallbackServiceResult = await fallbackService.GetFontFamilyForCodepoint(codepoint);
			}
			catch (Exception e)
			{
				typeof(UnicodeText).LogError()?.Error($"Font fallback service failed to get font for codepoint U+{codepoint:X4}", e);
			}

			if (fallbackServiceResult is null)
			{
				return null;
			}
			else
			{
				var fallbackFont = await GetFont(fallbackServiceResult, fontSize, weight, stretch, style).loadedTask;
				return fallbackFont;
			}
		}
		else
		{
			return null;
		}
	}
}
