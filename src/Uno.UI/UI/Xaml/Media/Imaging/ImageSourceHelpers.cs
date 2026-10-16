#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Uno.UI.Xaml.Media;
using Windows.ApplicationModel;
using Windows.Storage;
using Microsoft.UI.Composition;
using Uno.Disposables;
using Uno.Foundation.Logging;

#if __SKIA__
using SkiaSharp;
using System.Buffers;
using System.Runtime.InteropServices.JavaScript;
#endif

namespace Uno.Helpers;

internal static partial class ImageSourceHelpers
{
	public static async Task<ImageData> ReadFromStreamAsBytesAsync(Stream stream, CancellationToken ct)
	{
		if (stream.CanSeek && stream.Position != 0)
		{
			stream.Position = 0;
		}

		var memoryStream = new MemoryStream();
		await stream.CopyToAsync(memoryStream, 81920, ct);
		var data = memoryStream.ToArray();
		return ImageData.FromBytes(data);
	}

#if __SKIA__
	public static async Task<ImageData> ReadFromStreamAsCompositionSurface(Stream imageStream, CancellationToken ct, int? targetWidth = null, int? targetHeight = null)
	{
		var buffer = new byte[imageStream.Length - imageStream.Position];
		await imageStream.ReadExactlyAsync(buffer, 0, buffer.Length, ct);

		bool hasTargetSize = targetWidth is not null || targetHeight is not null;

		if (OperatingSystem.IsBrowser() && !hasTargetSize && !MayBeAnimated(buffer))
		{
			// The JS object holds the decoded pixels until it is released, and its finalizer only runs when the GC
			// happens to collect: allocations outside the managed heap don't prompt one.
			using var decodedBufferObject = await LoadFromArray(buffer);

			if (decodedBufferObject.GetPropertyAsString("error") is { } errorMessage)
			{
				typeof(ImageSourceHelpers).LogError()?.Error($"Failed to load image with the browser Canvas API. Falling back to SKCodec-based loading/decoding: {errorMessage}");
			}
			else
			{
				var width = decodedBufferObject.GetPropertyAsInt32("width");
				var height = decodedBufferObject.GetPropertyAsInt32("height");

				if (width == 0 || height == 0)
				{
					return ImageData.Empty;
				}

				using var bytes = decodedBufferObject.GetPropertyAsJSObject("bytes");
				if (bytes is null)
				{
					return ImageData.Empty;
				}

				try
				{
					// Copied once, from the worker's buffer straight into the image's native storage: a managed copy of the
					// image's size would sit in the large object space until a major collection.
					var image = CreateImage(width, height, bytes, static (pixels, source) => CopyPixels(source, pixels));
					return ImageData.FromCompositionSurface(new SkiaCompositionSurface(image));
				}
				catch (Exception e)
				{
					return ImageData.FromError(e);
				}
			}
		}

		var surface = new SkiaCompositionSurface();
		var result = surface.LoadFromStream(targetWidth, targetHeight, new MemoryStream(buffer));

		if (result.success)
		{
			return ImageData.FromCompositionSurface(surface);
		}
		else
		{
			var exception = new InvalidOperationException($"Image load failed ({result.nativeResult})");
			return ImageData.FromError(exception);
		}
	}

	// https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/6.0/byte-array-interop#receive-byte-array-in-javascript-from-net-1
	[JSImport($"globalThis.Uno.UI.Runtime.Skia.ImageLoader.loadFromArray")]
	private static partial Task<JSObject> LoadFromArray(byte[] array);

	[JSImport($"globalThis.Uno.UI.Runtime.Skia.ImageLoader.copyPixels")]
	private static partial void CopyPixels(JSObject source, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

	/// <summary>
	/// Creates an RGBA premultiplied image whose tightly packed pixels <paramref name="writePixels"/> writes into the
	/// image's own storage, so they are copied once. The span is only valid during the call and starts uninitialized.
	/// </summary>
	internal static unsafe SKImage CreateImage<TState>(int width, int height, TState state, SpanAction<byte, TState> writePixels)
	{
		using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
		writePixels(new Span<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount), state);

		// The image shares the pixels of an immutable bitmap instead of copying them.
		bitmap.SetImmutable();
		return SKImage.FromBitmap(bitmap)
			?? throw new InvalidOperationException($"{nameof(SKImage)}.{nameof(SKImage.FromBitmap)} returned null.");
	}
#endif

	public static async Task<ImageData> GetImageDataFromUriAsBytes(Uri uri, CancellationToken ct)
	{
		try
		{
			using var stream = await AppDataUriEvaluator.ToStream(uri, ct);
			return await ReadFromStreamAsBytesAsync(stream, ct);
		}
		catch (Exception e)
		{
			return ImageData.FromError(e);
		}
	}

#if __SKIA__
	public static async Task<ImageData> GetImageDataFromUriAsCompositionSurface(Uri uri, CancellationToken ct, int? targetWidth = null, int? targetHeight = null)
	{
		try
		{
			using var stream = await AppDataUriEvaluator.ToStream(uri, ct);
			return await ReadFromStreamAsCompositionSurface(stream, ct, targetWidth, targetHeight);
		}
		catch (Exception e)
		{
			return ImageData.FromError(e);
		}
	}

	/// <summary>
	/// Returns true for encoded images that may contain animation and must bypass the
	/// browser Canvas API (which only returns the first frame) in favor of SKCodec.
	/// Reads the content, as a stream has no name and a URL need not end in an extension.
	/// </summary>
	/// <remarks>
	/// Any GIF counts, as telling a still one apart means walking all its blocks. APNG does not:
	/// Skia's PNG codec only decodes its default image.
	/// </remarks>
	internal static bool MayBeAnimated(ReadOnlySpan<byte> encoded)
		=> encoded.StartsWith("GIF87a"u8)
			|| encoded.StartsWith("GIF89a"u8)
			// An animated WebP has the animation flag set in its VP8X chunk, which comes first.
			|| (encoded.Length > 20 && encoded.StartsWith("RIFF"u8) && encoded.Slice(8, 8).SequenceEqual("WEBPVP8X"u8) && (encoded[20] & 0x02) != 0);
#endif
}
