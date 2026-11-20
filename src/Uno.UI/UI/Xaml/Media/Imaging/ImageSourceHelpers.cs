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
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using Uno.UI.Composition.Drawing;
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
		=> await DecodeAsCompositionSurface(await ReadAllAsync(imageStream, ct), targetWidth, targetHeight);

	private static async Task<byte[]> ReadAllAsync(Stream imageStream, CancellationToken ct)
	{
		var buffer = new byte[imageStream.Length - imageStream.Position];
		await imageStream.ReadExactlyAsync(buffer, 0, buffer.Length, ct);
		return buffer;
	}

	private static async Task<ImageData> DecodeAsCompositionSurface(byte[] buffer, int? targetWidth, int? targetHeight)
	{
		bool hasTargetSize = targetWidth is not null || targetHeight is not null;

		// Only the codec decodes every frame of an animation. The content check is only a hint, so the browser still
		// gets a buffer the codec declines, and shows its first frame.
		var codecData = OperatingSystem.IsBrowser() && !hasTargetSize && MayBeAnimated(buffer) ? LoadWithCodec(buffer, targetWidth, targetHeight) : default(ImageData?);
		if (codecData is { Kind: not ImageDataKind.Error })
		{
			return codecData.Value;
		}

		if (OperatingSystem.IsBrowser() && !hasTargetSize)
		{
			// The JS object holds the decoded pixels until it is released, and its finalizer only runs when the GC
			// happens to collect: allocations outside the managed heap don't prompt one.
			using var decodedBufferObject = await LoadFromArray(buffer);

			if (decodedBufferObject.GetPropertyAsString("error") is { } errorMessage)
			{
				typeof(ImageSourceHelpers).LogError()?.Error($"Failed to load image with the browser Canvas API: {errorMessage}");
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
					// The worker already converted the pixels to BGRA premultiplied: copy them straight into the image's own storage.
					var image = ImageEncoderDecoder.Current.CreateImage(width, height, bytes, static (pixels, source) => CopyPixels(source, pixels));
					return ImageData.FromCompositionSurface(new CompositionImageSurface(image));
				}
				catch (Exception e)
				{
					return ImageData.FromError(e);
				}
			}
		}

		return codecData ?? LoadWithCodec(buffer, targetWidth, targetHeight);
	}

	private static ImageData LoadWithCodec(byte[] buffer, int? targetWidth, int? targetHeight)
	{
		var surface = new CompositionImageSurface();
		var result = surface.LoadFromStream(targetWidth, targetHeight, new MemoryStream(buffer, writable: false));

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
	[JSImport($"globalThis.Uno.UI.Runtime.ImageLoader.loadFromArray")]
	private static partial Task<JSObject> LoadFromArray(byte[] array);

	[JSImport($"globalThis.Uno.UI.Runtime.ImageLoader.copyPixels")]
	private static partial void CopyPixels(JSObject source, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);
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
	/// <param name="useEncodedCache">Whether the encoded bytes of a downloaded image are kept and reused (see <see cref="EncodedImageCache"/>).</param>
	/// <param name="encoded">The encoded bytes of <paramref name="uri"/> that its source kept, decoded instead of downloading it again.</param>
	/// <param name="keepEncoded">Whether the result of a download carries its encoded bytes (<see cref="ImageData.Encoded"/>), for its source to keep.</param>
	public static async Task<ImageData> GetImageDataFromUriAsCompositionSurface(
		Uri uri,
		CancellationToken ct,
		int? targetWidth = null,
		int? targetHeight = null,
		bool useEncodedCache = false,
		byte[]? encoded = null,
		bool keepEncoded = false)
	{
		try
		{
			var canCache = EncodedImageCache.CanCache(uri);
			useEncodedCache &= canCache;
			keepEncoded &= canCache;

			if (encoded is not null || (useEncodedCache && EncodedImageCache.TryGet(uri, out encoded)))
			{
				return Keep(await DecodeAsCompositionSurface(encoded, targetWidth, targetHeight), encoded);
			}

			byte[] buffer;
			TimeSpan? reusableFor = null;
			if (useEncodedCache)
			{
				// Only http and https get here. Bytes the server asks not to reuse without revalidation are not kept, and
				// those kept are used only while fresh.
				var download = await AppDataUriEvaluator.DownloadAsync(uri, ct);
				using (download.Content)
				{
					buffer = await ReadAllAsync(download.Content, ct);
				}

				reusableFor = download.ReusableFor;
			}
			else
			{
				using var stream = await AppDataUriEvaluator.ToStream(uri, ct);
				buffer = await ReadAllAsync(stream, ct);
			}

			var data = await DecodeAsCompositionSurface(buffer, targetWidth, targetHeight);

			// Only bytes that decode: a truncated or corrupt download is downloaded again.
			if (reusableFor is { } freshFor && data.HasData)
			{
				EncodedImageCache.Add(uri, buffer, freshFor);
			}

			return Keep(data, buffer);
		}
		catch (Exception e)
		{
			return ImageData.FromError(e);
		}

		ImageData Keep(ImageData data, byte[] bytes) => keepEncoded && data.HasData ? data.WithEncoded(bytes) : data;
	}

	/// <summary>
	/// Returns true for encoded images that may contain animation and must bypass the
	/// browser Canvas API (which only returns the first frame) in favor of the image codec.
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
