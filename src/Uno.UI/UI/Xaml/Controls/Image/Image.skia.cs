using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Uno.Disposables;
using Uno.Foundation.Logging;
using Uno.UI.Xaml.Media;
using Windows.Foundation;

namespace Microsoft.UI.Xaml.Controls
{
	partial class Image : FrameworkElement
	{
		// What an SvgImageSource needs released: it draws through a child canvas, and its subscription only tells when to
		// raise ImageOpened.
		private readonly SerialDisposable _sourceDisposable = new SerialDisposable();

		// An SvgImageSource's events and data, subscribed only while the image is in the tree: a source that outlives the
		// image (an icon kept in a static field or a resource, say) would otherwise keep it and everything it references
		// alive, as the source holds the handlers.
		private readonly SerialDisposable _svgSubscription = new SerialDisposable();
		private ImageSource.Subscription _subscription;

		// Changes whenever _subscription is set or released, so a Subscribe that the app superseded meanwhile is noticed.
		private int _subscriptionVersion;
		private Size _lastMeasuredSize;
		private SkiaCompositionSurface _currentSurface;
		private CompositionSurfaceBrush _surfaceBrush;
		private readonly SpriteVisual _imageSprite;
		private ImageData? _pendingImageData;
		private int _pendingImageVersion;

		// The SourceVersion of Source that ImageOpened was raised for: WinUI raises it once per source, so the data
		// delivered again when the Image re-enters the tree, or decoded again after its release, raises nothing.
		private int? _imageOpenedVersion;

		// The SVG source ImageOpened was raised for: an SVG source raises it on an image once, however often it loads.
		private SvgImageSource _svgImageOpenedFor;

		// Whether the SVG source delivered data the image has yet to show, which raises ImageOpened when it is measured.
		private bool _svgDataPending;

		public Image()
		{
			_imageSprite = Visual.Compositor.CreateSpriteVisual();
			Visual.Children.InsertAtTop(_imageSprite);
		}

		partial void OnSourceChanged(ImageSource newValue, bool forceReload)
		{
			// We clear the old image first. We do NOT wait for the new image to load
			// for it to replace the old one. This is what happens on WinUI.
			_svgSubscription.Disposable = null;
			_sourceDisposable.Disposable = null;

			// The sprite lets go of the surface before the subscription can release it.
			_imageSprite.Brush = null;
			_currentSurface = null;
			_pendingImageData = null;
			_imageOpenedVersion = null;
			_svgDataPending = false;
			if (!ReferenceEquals(newValue, _svgImageOpenedFor))
			{
				_svgImageOpenedFor = null;
			}

			_subscription?.Applied(ImageData.Empty);
			ReleaseSubscription();
			_lastMeasuredSize = default;
			InvalidateMeasure();

			if (newValue is SvgImageSource svgImageSource)
			{
				InitializeSvgSource(svgImageSource);

				if (IsLoaded)
				{
					SubscribeSvgSource(svgImageSource);
				}
			}
			// Subscribing is what decodes the source, so an unloaded Image (e.g. a recycled container whose binding
			// updates off-tree) waits for OnLoaded, as WinUI decodes only once the element is in the live tree.
			else if (newValue is ImageSource source && IsLoaded)
			{
				InitializeImageSource(source);
			}
		}

		private void InitializeSvgSource(SvgImageSource source)
		{
			_svgCanvas = source.GetCanvas();
			AddChild(_svgCanvas);

			_sourceDisposable.Disposable = Disposable.Create(() =>
			{
				RemoveChild(_svgCanvas);
				_svgCanvas = null;
			});
		}

		private void SubscribeSvgSource(SvgImageSource source)
		{
			source.SourceLoaded += OnSvgSourceLoaded;
			source.OpenFailed += OnSvgSourceFailed;

			// Data arrives after the source's Opened and before its SetSourceAsync completes. The subscription, disposed when
			// the image changes source or leaves the tree, gets none for a previous one; a source that already has its data
			// delivers it again on loading, which raises nothing (_svgImageOpenedFor).
			var subscription = source.Subscribe(data =>
			{
				_svgDataPending = data.HasData;
				InvalidateMeasure();
			});

			_svgSubscription.Disposable = Disposable.Create(() =>
			{
				subscription.Dispose();
				source.SourceLoaded -= OnSvgSourceLoaded;
				source.OpenFailed -= OnSvgSourceFailed;
			});
		}

		private void OnSvgSourceLoaded(object sender, EventArgs args) => InvalidateMeasure();

		// Like WinUI, an image raises ImageOpened for an SVG source the first time it shows the source's data, once the
		// SetSourceAsync that loaded it has completed, so a stream set from the handler doesn't cancel it.
		private void TryRaiseSvgImageOpened()
		{
			if (_svgDataPending && Source is SvgImageSource source)
			{
				_svgDataPending = false;
				if (!ReferenceEquals(_svgImageOpenedFor, source))
				{
					_svgImageOpenedFor = source;
					ImageOpened?.Invoke(this, new RoutedEventArgs(this));
				}
			}
		}

		private void OnSvgSourceFailed(SvgImageSource sender, SvgImageSourceFailedEventArgs args)
		{
			InvalidateMeasure();
			ImageFailed?.Invoke(this, new ExceptionRoutedEventArgs(this, "Failed to load Svg source"));
		}

		private void InitializeImageSource(ImageSource source)
		{
			// Back in the tree, the sprite still draws _currentSurface (see OnUnloaded). What it is given is drawn once
			// measured, and acknowledged then (TryProcessPendingSource).
			var version = ++_subscriptionVersion;
			var shown = _currentSurface;
			var subscription = source.Subscribe(
				img =>
				{
					_pendingImageData = img;
					_pendingImageVersion = source.SourceVersion;

					InvalidateMeasure();
				},
				shown: shown,
				appliedOnDelivery: false);

			if (version == _subscriptionVersion)
			{
				_subscription = subscription;
				return;
			}

			// Subscribing ran app code (opening a completed cache entry raises BitmapImage.ImageOpened at once) that changed
			// the source or unloaded this Image, which by now has the subscription it needs, if any.
			if (!ReferenceEquals(_currentSurface, shown))
			{
				subscription.Applied(ImageData.Empty);
			}

			subscription.Dispose();
		}

		private void ReleaseSubscription()
		{
			_subscriptionVersion++;
			_subscription?.Dispose();
			_subscription = null;
		}

		protected override Size MeasureOverride(Size availableSize)
		{
			TryProcessPendingSource();
			TryRaiseSvgImageOpened();

			if (GetSourceSize() is { } sourceSize)
			{
				_lastMeasuredSize = sourceSize;

				Size ret;

				if (Source is BitmapImage bitmapImage)
				{
					bitmapImage.PixelWidth = (int)_lastMeasuredSize.Width;
					bitmapImage.PixelHeight = (int)_lastMeasuredSize.Height;
				}

				if (
					double.IsInfinity(availableSize.Width)
					&& double.IsInfinity(availableSize.Height)
				)
				{
					ret = _lastMeasuredSize;
				}
				else
				{
					ret = this.AdjustSize(availableSize, _lastMeasuredSize);
				}

				// Always making sure the ret size isn't bigger than the available size for an image with a fixed width or height
				ret = new Size(
					!Double.IsNaN(Width) && (ret.Width > availableSize.Width) ? availableSize.Width : ret.Width,
					!Double.IsNaN(Height) && (ret.Height > availableSize.Height) ? availableSize.Height : ret.Height
				);

				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().LogDebug($"Measure {this} availableSize:{availableSize} measuredSize:{_lastMeasuredSize} ret:{ret} Stretch: {Stretch} Width:{Width} Height:{Height}");
				}

				return ret;
			}
			else
			{
				return default;
			}
		}

		private void TryProcessPendingSource()
		{
			var currentData = _pendingImageData;
			var version = _pendingImageVersion;
			_pendingImageData = null;
			if (currentData is null)
			{
				// No image data is pending
				return;
			}

			var processedData = currentData.Value;

			// The sprite draws this from now on: what it drew before can go once nothing else draws it.
			_subscription?.Applied(processedData);

			if (processedData.HasData)
			{
				_currentSurface = processedData.CompositionSurface;
				_surfaceBrush = Visual.Compositor.CreateSurfaceBrush(_currentSurface);
				_surfaceBrush.MonochromeColor = MonochromeColor?.ToSKColor();
				_imageSprite.Brush = _surfaceBrush;

				if (_imageOpenedVersion != version)
				{
					_imageOpenedVersion = version;
					ImageOpened?.Invoke(this, new RoutedEventArgs(this));
				}
			}
			else if (processedData is { Kind: ImageDataKind.Empty })
			{
				// Ensure the previous content is unloaded
				_currentSurface = null;
				_imageSprite.Brush = null;
			}
			else if (processedData is { Kind: ImageDataKind.Error })
			{
				// A source that failed to open shows nothing, not the image it had before: WinUI drops its surfaces
				// (MUX Reference: CImageSource::FireImageFailed, imagesource.cpp), as the source releases data it owns.
				_currentSurface = null;
				_imageSprite.Brush = null;

				ImageFailed?.Invoke(this, new(
					this,
					processedData.Error?.Message ?? "Unknown error"));
			}
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			if (IsSourceReady())
			{
				// Calculate the resulting space required on screen for the image;
				var containerSize = this.MeasureSource(finalSize, _lastMeasuredSize);

				var roundedSize = LayoutRound(new Vector2((float)containerSize.Width, (float)containerSize.Height));

				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().LogDebug($"Arrange {this} _lastMeasuredSize:{_lastMeasuredSize}, containerSize:{containerSize}, finalSize:{finalSize}");
				}

				if (Source is SvgImageSource)
				{
					_svgCanvas?.Arrange(new Rect(0, 0, roundedSize.X, roundedSize.Y));
					return containerSize;
				}
				else
				{
					_imageSprite.Size = roundedSize;

					if (_currentSurface?.Image is { } image)
					{
						_surfaceBrush.TransformMatrix = Matrix3x2.CreateScale(_imageSprite.Size.X / image.Width, _imageSprite.Size.Y / image.Height);
					}

					// Image has no direct child that needs to be arranged explicitly
					return containerSize;
				}
			}
			else
			{
				_imageSprite.Size = default;
				return ArrangeFirstChild(finalSize);
			}
		}

		private protected override void OnLoaded()
		{
			base.OnLoaded();

			// The subscription was released on unload: subscribe again. A source that kept its data (shared through the
			// cache, or not yet released) delivers it at once; one that released it decodes it again.
			if (Source is SvgImageSource svgImageSource)
			{
				if (_svgSubscription.Disposable is null)
				{
					SubscribeSvgSource(svgImageSource);
				}
			}
			else if (_subscription is null && Source is { } source)
			{
				InitializeImageSource(source);
			}
		}

		private protected override void OnUnloaded()
		{
			base.OnUnloaded();

			// Released so the source can let go of its pixels once they have waited (PendingImageReleases). The sprite keeps
			// its brush: it is not drawn off the tree, and an Image back in time still has them. Data not yet drawn is dropped.
			_pendingImageData = null;
			ReleaseSubscription();
			_svgSubscription.Disposable = null;
		}

		partial void OnMonochromeColorChanged()
		{
			if (_surfaceBrush is not null)
			{
				// When the image is already loaded, just update the color filter on the brush
				// instead of reloading the entire image. This avoids flicker.
				_surfaceBrush.MonochromeColor = MonochromeColor?.ToSKColor();
			}
			else
			{
				OnSourceChanged(Source, forceReload: true);
			}
		}

		private bool IsSourceReady() => GetSourceSize() is not null;

		private Size? GetSourceSize()
		{
			if (Source is SvgImageSource svgImageSource)
			{
				return svgImageSource.IsParsed ? svgImageSource.SourceSize : null;
			}
			else if (Source is ImageSource source)
			{
				// Pixels the source released because nothing displayed them keep their size until it delivers them again (a
				// cache-off URI downloads again), so an auto-sized Image doesn't collapse meanwhile.
				return _currentSurface?.Image is { } image ? new Size(image.Width, image.Height) : source.ReleasedPixelSize;
			}

			return null;
		}

		/// <summary>
		/// Returns a mask that represents the alpha channel of the image as a CompositionBrush.
		/// This brush can be used with CompositionMaskBrush or DropShadow.Mask to create shaped effects.
		/// </summary>
		/// <returns>A CompositionBrush representing the image as an alpha mask.</returns>
		public CompositionBrush GetAlphaMask()
		{
			var compositor = Compositor.GetSharedCompositor();
			var surface = new AlphaMaskSurface(compositor, Visual);
			var brush = compositor.CreateSurfaceBrush(surface);
			brush.Stretch = CompositionStretch.None;
			return brush;
		}
	}
}
