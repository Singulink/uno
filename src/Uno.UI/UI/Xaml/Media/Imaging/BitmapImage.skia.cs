using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Uno.Extensions;
using Uno.Helpers;
using Uno.UI;
using Uno.UI.Xaml.Media;
using Windows.ApplicationModel;
using Windows.Graphics.Display;

namespace Microsoft.UI.Xaml.Media.Imaging
{
	public sealed partial class BitmapImage : BitmapSource
	{
		// WinUI raises ImageOpened once per source, so decoding the same source again after its release raises nothing
		// (MUX Reference: CImageSource::FireImageOpened, m_publicBitmapEventFired, re-armed by ResetCallbackEvents when
		// the source is set — imagesource.cpp).
		private int? _imageOpenedVersion;

		private void RaiseImageOpened(int version)
		{
			if (_imageOpenedVersion != version)
			{
				_imageOpenedVersion = version;
				RaiseImageOpened();
			}
		}

		// Takes the pixel size of an open's result and raises its event. Setting the size runs property-changed handlers,
		// which may change the source: that supersedes the open, which then raises nothing and releases what it owns.
		private ImageData Publish(ImageData imageData, int version, CancellationToken ct)
		{
			// Superseded while decoding, or by a size setter's handler: the open that superseded this one may have set its own
			// size already (from a completed cache entry), which this one must not overwrite.
			ThrowIfSuperseded(imageData, ct);
			if (imageData.Kind == ImageDataKind.Error)
			{
				PixelWidth = 0;
				ThrowIfSuperseded(imageData, ct);
				PixelHeight = 0;
			}
			else if (imageData.Kind == ImageDataKind.CompositionSurface)
			{
				var image = imageData.CompositionSurface.Image;
				PixelWidth = image.Width;
				ThrowIfSuperseded(imageData, ct);
				PixelHeight = image.Height;
			}

			ThrowIfSuperseded(imageData, ct);

			if (imageData.Kind == ImageDataKind.Error)
			{
				RaiseImageFailed(imageData.Error);
			}
			else if (imageData.Kind == ImageDataKind.CompositionSurface)
			{
				RaiseImageOpened(version);
			}

			return imageData;
		}

		private static void ThrowIfSuperseded(ImageData imageData, CancellationToken ct)
		{
			if (ct.IsCancellationRequested)
			{
				ReleaseSurface(imageData);
				throw new OperationCanceledException(ct);
			}
		}

		private readonly record struct BitmapImageCacheKey(Uri Uri, int? DecodeWidth, int? DecodeHeight);

		// Test hook: resolves the uri like the loader does so tests can observe cache membership for a key.
		internal static async Task<Task<ImageData>> GetCachedImageDataTaskForTesting(Uri uri, int? decodeWidth, int? decodeHeight)
		{
			uri = await TryResolveLocalResource(uri);
			return SharedImageDecode.GetLoadForTesting(new BitmapImageCacheKey(uri, decodeWidth, decodeHeight));
		}

		// TODO: Introduce LRU caching if needed
		private static readonly Dictionary<string, string> _scaledBitmapPathCache = new();

		private protected override bool TryOpenSourceAsync(CancellationToken ct, int? targetWidth, int? targetHeight, out Task<ImageData> asyncImage)
		{
			asyncImage = TryOpenSourceAsync(ct);

			return true;
		}

		private (int? targetWidth, int? targetHeight) GetDecodePixelSize()
		{
			var width = DecodePixelWidth;
			var height = DecodePixelHeight;

			if (width == 0 && height == 0)
			{
				return (null, null);
			}

			if (DecodePixelType == DecodePixelType.Logical)
			{
				var scale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
				if (width > 0)
				{
					width = (int)Math.Round(width * scale);
				}
				if (height > 0)
				{
					height = (int)Math.Round(height * scale);
				}
			}

			return (width > 0 ? width : null, height > 0 ? height : null);
		}

		private async Task<ImageData> TryOpenSourceAsync(CancellationToken ct)
		{
			var version = SourceVersion;

			try
			{
				var (decodeWidth, decodeHeight) = GetDecodePixelSize();
				var uri = UriSource;
				if (uri is null)
				{
					if (_stream is null)
					{
						return ImageData.Empty;
					}
					else
					{
						var clonedStream = _stream.CloneStream().AsStreamForRead();
						ImageData imageData;
						try
						{
							imageData = await Task.Run(async () =>
							{
								try
								{
									using (clonedStream)
									{
										return await ImageSourceHelpers.ReadFromStreamAsCompositionSurface(clonedStream, ct, targetWidth: decodeWidth, targetHeight: decodeHeight);
									}
								}
								catch (Exception e)
								{
									return ImageData.FromError(e);
								}
							}, ct);
						}
						catch (OperationCanceledException)
						{
							// If ct is already canceled, Task.Run never executes the
							// delegate and clonedStream would leak.
							clonedStream.Dispose();
							throw;
						}

						return Publish(imageData, version, ct);
					}
				}
				else
				{
					if (!uri.IsAbsoluteUri)
					{
						return ImageData.FromError(new InvalidOperationException($"UriSource must be absolute"));
					}

					if (uri.IsLocalResource())
					{
						uri = await TryResolveLocalResource(uri);
					}

					var useCache = FeatureConfiguration.Image.EnableBitmapImageCache
						&& !CreateOptions.HasFlag(BitmapCreateOptions.IgnoreImageCache);

					// A cached load is shared by every BitmapImage opening the same key, so it never runs on a single
					// requester's token (cancelling one must not fail the others). Only an uncached load is cancelled with its
					// sole requester.
					SharedImageDecode share = null;
					Task<ImageData> imageDataTask;

					if (useCache)
					{
						share = SharedImageDecode.GetOrStart(
							new BitmapImageCacheKey(uri, decodeWidth, decodeHeight),
							() => ImageSourceHelpers.GetImageDataFromUriAsCompositionSurface(uri, CancellationToken.None, decodeWidth, decodeHeight, useEncodedCache: true));

						share.BeginOpen();
						imageDataTask = share.Load;
					}
					else
					{
						imageDataTask = Task.Run(async () =>
						{
							try
							{
								return await ImageSourceHelpers.GetImageDataFromUriAsCompositionSurface(uri, ct, decodeWidth, decodeHeight);
							}
							catch (Exception e)
							{
								return ImageData.FromError(e);
							}
						}, ct);
					}

					// This open stays a user of a shared result until its source counts its own use (ImageSource.Open), so
					// that nothing releases the result in between. Any other way out, cancelled, failed or a handler that
					// throws, ends that use here; the shared load keeps running for its other users.
					var handedOver = false;
					try
					{
						ImageData imageData;

						try
						{
							imageData = await imageDataTask.WaitAsync(ct);

							// The shared load may have completed between the cancellation and the await.
							ct.ThrowIfCancellationRequested();
						}
						catch (OperationCanceledException) when (ct.IsCancellationRequested && share is null)
						{
							// The decode cannot be stopped and this requester will never show its result, so release the
							// surface it produces instead of leaving it to finalization.
							_ = imageDataTask.ContinueWith(
								static t => ReleaseSurface(t.Result),
								CancellationToken.None,
								TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
								TaskScheduler.Default);
							throw;
						}

						var published = Publish(imageData, version, ct);
						handedOver = published.IsShared;
						return published;
					}
					finally
					{
						if (share is not null && !handedOver)
						{
							share.EndOpen();
						}
					}
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				// A superseded open is not a load failure; ImageSource.Open drops it.
				throw;
			}
			catch (Exception e)
			{
				return ImageData.FromError(e);
			}
		}

		private static readonly int[] KnownScales =
		{
			(int)ResolutionScale.Scale100Percent,
			(int)ResolutionScale.Scale120Percent,
			(int)ResolutionScale.Scale125Percent,
			(int)ResolutionScale.Scale140Percent,
			(int)ResolutionScale.Scale150Percent,
			(int)ResolutionScale.Scale160Percent,
			(int)ResolutionScale.Scale175Percent,
			(int)ResolutionScale.Scale180Percent,
			(int)ResolutionScale.Scale200Percent,
			(int)ResolutionScale.Scale225Percent,
			(int)ResolutionScale.Scale250Percent,
			(int)ResolutionScale.Scale300Percent,
			(int)ResolutionScale.Scale350Percent,
			(int)ResolutionScale.Scale400Percent,
			(int)ResolutionScale.Scale450Percent,
			(int)ResolutionScale.Scale500Percent
		};

		internal static async Task<Uri> TryResolveLocalResource(Uri uri, ResolutionScale? scaleOverride = null)
		{
			if (!uri.IsLocalResource())
			{
				return uri;
			}

			// GetScaledPath uses DisplayInformation so it needs to be called on the UI thread
			if (OperatingSystem.IsIOS())
			{
				// For iOS, [Uno.netcoremobile]\PlatformImageHelpers.GetScaledPath just returns the input uri back as is.
				// Presumably under the assumption that the native control will handle the resultion of @2x @3x assets accordingly.
				// However, on skia-ios, this is handled by uno (right here),
				// so we need to resolve the appropriate asset here, with the scale-XYZ qualifier if available.

				var path = uri.PathAndQuery;
				if (uri.Host is { Length: > 0 } host)
				{
					path = host + "/" + path.TrimStart('/');
				}

				return new Uri(GetScaledPath(path, scaleOverride));
			}
			else
			{
				return new Uri(await PlatformImageHelpers.GetScaledPath(uri, scaleOverride));
			}
		}

		internal static string GetScaledPath(string rawPath, ResolutionScale? scaleOverride = null)
		{
			// Avoid querying filesystem if we already seen this file
			if (_scaledBitmapPathCache.TryGetValue(rawPath, out var result))
			{
				return result;
			}

			var originalLocalPath =
				Path.Combine(Package.Current.InstalledPath,
					 rawPath.TrimStart('/').Replace('/', global::System.IO.Path.DirectorySeparatorChar)
				);

			var resolutionScale = (int)(scaleOverride ?? DisplayInformation.GetForCurrentView().ResolutionScale);

			var baseDirectory = Path.GetDirectoryName(originalLocalPath);
			var baseFileName = Path.GetFileNameWithoutExtension(originalLocalPath);
			var baseExtension = Path.GetExtension(originalLocalPath);

			var applicableScale = FindApplicableScale(true);
			if (applicableScale is null)
			{
				applicableScale = FindApplicableScale(false);
			}

			result = applicableScale ?? originalLocalPath;
			_scaledBitmapPathCache[rawPath] = result;
			return result;

			string FindApplicableScale(bool onlyMatching)
			{
				for (var i = KnownScales.Length - 1; i >= 0; i--)
				{
					var probeScale = KnownScales[i];

					if ((onlyMatching && resolutionScale >= probeScale) ||
						(!onlyMatching && resolutionScale < probeScale))
					{
						var filePath = Path.Combine(baseDirectory, $"{baseFileName}.scale-{probeScale}{baseExtension}");

						if (File.Exists(filePath))
						{
							return filePath;
						}
					}
				}

				return null;
			}
		}
	}
}
