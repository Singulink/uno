#nullable enable

using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using Uno.UI;
using Uno.Foundation.Logging;
using Uno.Helpers;
using System.Linq;
using WinUICoreServices = Uno.UI.Xaml.Core.CoreServices;

#if __APPLE_UIKIT__
using View = UIKit.UIView;
#elif __ANDROID__
using Android.Views;
#endif

using WindowSizeChangedEventArgs = Microsoft.UI.Xaml.WindowSizeChangedEventArgs;
using XamlWindow = Microsoft.UI.Xaml.Window;

namespace Microsoft.UI.Xaml.Controls.Primitives;

partial class PopupPanel
{
	// This is a base popup panel to calculate the placement near an anchor control.
	// This class exists mostly to reuse the same logic between a Flyout and a ToolTip.
	// This class should eventually be removed, and Uno should match WinUI's approach, where Flyout sets Popup.HorizontalOffset and VerticalOffset
	// as well as Width and Height on FlyoutPresenter when it opens, and then allows the popup layouting to do its job.

	private static readonly Dictionary<FlyoutBase.MajorPlacementMode, Memory<FlyoutBase.MajorPlacementMode>> PlacementsToTry =
		new Dictionary<FlyoutBase.MajorPlacementMode, Memory<FlyoutBase.MajorPlacementMode>>()
		{
			 {FlyoutBase.MajorPlacementMode.Top, new []
			 {
				 FlyoutBase.MajorPlacementMode.Top,
				 FlyoutBase.MajorPlacementMode.Bottom,
				 FlyoutBase.MajorPlacementMode.Left,
				 FlyoutBase.MajorPlacementMode.Right,
				 FlyoutBase.MajorPlacementMode.Top // use preferred choice if no others fit
			 }},
			 {FlyoutBase.MajorPlacementMode.Bottom, new []
			 {
				 FlyoutBase.MajorPlacementMode.Bottom,
				 FlyoutBase.MajorPlacementMode.Top,
				 FlyoutBase.MajorPlacementMode.Left,
				 FlyoutBase.MajorPlacementMode.Right,
				 FlyoutBase.MajorPlacementMode.Bottom // use preferred choice if no others fit
			 }},
			 {FlyoutBase.MajorPlacementMode.Left, new []
			 {
				 FlyoutBase.MajorPlacementMode.Left,
				 FlyoutBase.MajorPlacementMode.Right,
				 FlyoutBase.MajorPlacementMode.Top,
				 FlyoutBase.MajorPlacementMode.Bottom,
				 FlyoutBase.MajorPlacementMode.Left // use preferred choice if no others fit
			 }},
			 {FlyoutBase.MajorPlacementMode.Right, new []
			 {
				 FlyoutBase.MajorPlacementMode.Right,
				 FlyoutBase.MajorPlacementMode.Left,
				 FlyoutBase.MajorPlacementMode.Top,
				 FlyoutBase.MajorPlacementMode.Bottom,
				 FlyoutBase.MajorPlacementMode.Right // use preferred choice if no others fit
			 }},
			 {FlyoutBase.MajorPlacementMode.Full, new []
			 {
				 FlyoutBase.MajorPlacementMode.Full
			 }},
		};

	/// <summary>
	/// This value is an arbitrary value for the placement of
	/// a popup below its anchor.
	/// TODO: To be removed in favor of HorizontalOffset and VerticalOffset.
	/// </summary>
	protected virtual int PopupPlacementTargetMargin => 0;

	private void XamlRootChanged(object sender, XamlRootChangedEventArgs e)
		=> InvalidateMeasure();

	// TODO: Use this whenever popup placement is Auto
	protected virtual PopupPlacementMode DefaultPopupPlacement { get; }

	protected virtual bool FullPlacementRequested { get; }

	internal virtual FlyoutBase? Flyout => null;

	/// <summary>
	/// Gets whether the child can be made smaller than its desired size so that it fits next to the placement target, when it fits on no side
	/// of it. WinUI resizes a flyout's presenter (unless <see cref="FlyoutBase.DisablePresenterResizing"/> was called), but never the child of a
	/// <see cref="Popup"/>.
	/// </summary>
	protected virtual bool AllowChildResizing => false;

	/// <summary>
	/// The placement calculated in measure for a child that can be resized, with the target and visible bounds it was calculated for.
	/// </summary>
	private (UIElement Child, Rect AnchorRect, Rect VisibleBounds, Rect Rect)? _measuredPlacement;

	/// <summary>
	/// Gets whether the child can be resized to fit next to its target. As in WinUI, flyouts shown at a position are never resized, as
	/// <c>FlyoutBase::UpdateTargetPosition</c> replaces their size, and neither are Full placements, which <c>CalculatePlacementPrivate</c> handles
	/// separately.
	/// </summary>
	private bool CanResizeChild
		=> AllowChildResizing
			&& !FullPlacementRequested
			&& Popup.DesiredPlacement is not PopupPlacementMode.Auto // Uno places Auto as Full
			&& Flyout is not { IsTargetPositionSet: true };

	/// <summary>
	/// Measures the child. When the child can be resized, it is placed here rather than in arrange, and measured again with the size it is
	/// placed at if that is smaller, so that the presenter's ScrollViewer scrolls instead of the content being cut off.
	/// </summary>
	/// <remarks>
	/// WinUI places a flyout once its presenter has a size and sets the presenter's Width and Height to the result (FlyoutBase::PerformPlacement),
	/// then doesn't place it again when that changes the presenter's size (m_presenterResized). Here the flyout is placed again whenever the popup
	/// is measured: when the content's measured size changes or the window changes. A resized flyout whose content stays larger than the room it
	/// was given keeps its placement, as in WinUI.
	/// </remarks>
	private Size MeasurePlacedChild(UIElement child, Size availableSize)
	{
		_measuredPlacement = null;

		var desiredSize = MeasureElement(child, availableSize);

		if (CanResizeChild && IsPlacedNextToTarget(desiredSize))
		{
			var anchorRect = GetAnchorRect(Popup);
			var visibleBounds = GetVisibleBounds();
			var rect = CalculatePopupPlacement(Popup, desiredSize, GetMaxSize(child));

			_measuredPlacement = (child, anchorRect, visibleBounds, rect);

			if (rect.Width < desiredSize.Width || rect.Height < desiredSize.Height)
			{
				desiredSize = MeasureElement(child, new Size(
					rect.Width < desiredSize.Width ? rect.Width : availableSize.Width,
					rect.Height < desiredSize.Height ? rect.Height : availableSize.Height));
			}
		}

		return desiredSize;
	}

	/// <summary>
	/// Gets whether the child is placed next to the popup's placement target (<see cref="PlacementArrangeOverride"/>) rather than at the popup's
	/// offsets.
	/// </summary>
	private bool IsPlacedNextToTarget(Size childSize)
	{
		if (Popup.PlacementTarget is null)
		{
			return false;
		}

		// TODO: For now, the layouting logic for managed DatePickerFlyout or TimePickerFlyout does not correctly work
		// against the placement target approach.
		var isFlyoutManagedDatePicker =
			(Popup.AssociatedFlyout is DatePickerFlyout || Popup.AssociatedFlyout is TimePickerFlyout)
#if __ANDROID__ || __IOS__
			&& (Popup.AssociatedFlyout is not NativeDatePickerFlyout && Popup.AssociatedFlyout is not NativeTimePickerFlyout)
#endif
			;

		return !isFlyoutManagedDatePicker
#if __ANDROID__ || __APPLE_UIKIT__
			|| NativeAnchor is not null
#endif
			|| !MathHelpers.DoesRectContainRect(GetVisibleBounds(), GetOffsetFrame(GetPopupLocation(), childSize)); // if the frame spills out of the window, always place next to the target
	}

	private Size PlacementArrangeOverride(Popup popup, Size finalSize)
	{
		foreach (var child in Children)
		{
			Rect rect;

			if (_measuredPlacement is { } measured && measured.Child == child)
			{
				rect = measured.Rect;

				if (measured.AnchorRect != GetAnchorRect(popup) || measured.VisibleBounds != GetVisibleBounds())
				{
					// The target or the window moved after measure (e.g. in the same layout pass as the flyout opened): measure again, which on Skia
					// the layout does before this frame is rendered, so that the child is measured with its new placement.
					InvalidateMeasure();
				}
			}
			else
			{
				var desiredSize = child.DesiredSize;
				rect = CalculatePopupPlacement(popup, desiredSize, GetMaxSize(child));

				if (Flyout?.IsTargetPositionSet ?? false)
				{
					rect = Flyout.UpdateTargetPosition(GetVisibleBounds(), desiredSize, rect);
				}
			}

			child.Arrange(rect);
		}

		return finalSize;
	}

	// UWP takes FlyoutPresenter's MaxHeight and MaxWidth into consideration, but ignores Height and Width
	private static Size GetMaxSize(UIElement child)
		=> child is FrameworkElement fe ? fe.GetMaxSize() : new Size(double.PositiveInfinity, double.PositiveInfinity);

#if __ANDROID__ || __APPLE_UIKIT__
	/// <summary>
	/// A native view to use as the anchor, in the case that the managed <see cref="AnchorControl"/> is a proxy that's not actually
	/// included in the visual tree.
	/// </summary>
	protected virtual View? NativeAnchor => null;
#endif

	private Rect GetAnchorRect(Popup popup)
	{
#if __ANDROID__ || __APPLE_UIKIT__
		if (NativeAnchor is not null)
		{
			return NativeAnchor.GetBoundsRectRelativeTo(this);
		}
#endif

		return popup.PlacementTarget.GetBoundsRectRelativeTo(this);
	}

	protected virtual Rect CalculatePopupPlacement(Popup popup, Size childDesiredSize, Size maxSize)
	{
		var anchorRect = GetAnchorRect(popup);

		var visibleBounds = GetVisibleBounds();

		// Make sure the desiredSize fits in the panel
		childDesiredSize.Width = Math.Min(childDesiredSize.Width, visibleBounds.Width);
		childDesiredSize.Height = Math.Min(childDesiredSize.Height, visibleBounds.Height);

		// Try all placements...
		var preferredPlacement = FlyoutBase.GetMajorPlacementFromPlacement(popup.DesiredPlacement, FullPlacementRequested);
		var placementsToTry = PlacementsToTry[preferredPlacement];

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().LogDebug($"Calculating actual placement for preferredPlacement={preferredPlacement} with justification={FlyoutBase.GetJustificationFromPlacementMode(popup.DesiredPlacement, FullPlacementRequested)} from PopupPlacement={popup.DesiredPlacement}, for childDesiredSize={childDesiredSize}, maxSize={maxSize}");
		}

		var halfAnchorWidth = anchorRect.Width / 2;
		var halfAnchorHeight = anchorRect.Height / 2;
		var halfChildWidth = childDesiredSize.Width / 2;
		var halfChildHeight = childDesiredSize.Height / 2;

		var finalRect = default(Rect);
		var placementFits = false;

		for (var i = 0; i < placementsToTry.Length; i++)
		{
			var placement = placementsToTry.Span[i];

			Point finalPosition;

			switch (placement)
			{
				case FlyoutBase.MajorPlacementMode.Top:
					finalPosition = new Point(
						x: anchorRect.Left + halfAnchorWidth - halfChildWidth + popup.HorizontalOffset,
						y: anchorRect.Top - PopupPlacementTargetMargin - childDesiredSize.Height + popup.VerticalOffset);
					break;
				case FlyoutBase.MajorPlacementMode.Bottom:
					finalPosition = new Point(
						x: anchorRect.Left + halfAnchorWidth - halfChildWidth + popup.HorizontalOffset,
						y: anchorRect.Bottom + PopupPlacementTargetMargin + popup.VerticalOffset);
					break;
				case FlyoutBase.MajorPlacementMode.Left:
					finalPosition = new Point(
						x: anchorRect.Left - PopupPlacementTargetMargin - childDesiredSize.Width + popup.HorizontalOffset,
						y: anchorRect.Top + halfAnchorHeight - halfChildHeight + popup.VerticalOffset);
					break;
				case FlyoutBase.MajorPlacementMode.Right:
					finalPosition = new Point(
						x: anchorRect.Right + PopupPlacementTargetMargin + popup.HorizontalOffset,
						y: anchorRect.Top + halfAnchorHeight - halfChildHeight + popup.VerticalOffset);
					break;
				case FlyoutBase.MajorPlacementMode.Full:
					if (FeatureConfiguration.Popup.ConstrainByVisibleBounds)
					{
#if !__APPLE_UIKIT__
						childDesiredSize = visibleBounds.Size;
#else
						// The mobile status bar should always remain visible.
						// On droid, this panel is placed beneath the status bar.
						// On iOS, this panel will cover the status bar, so we have to substract it out.
						childDesiredSize = new Size(ActualWidth, ActualHeight)
							.Subtract(0, visibleBounds.Y)
							.AtMost(maxSize);
#endif
					}
					else
					{
						// When ConstrainByVisibleBounds is false, we let the flyout to occupy the entire area available,
						// as dictated by the root skia-canvas. This extends into the status bar, and bottom bar if present.
#if __SKIA__
						childDesiredSize = m_unclippedDesiredSize;
#else
						childDesiredSize = DesiredSize;
#endif
					}

					finalPosition = new Point(
#if __SKIA__
						x: FindOptimalOffset(childDesiredSize.Width, visibleBounds.X, visibleBounds.Width, (Parent as FrameworkElement)?.ActualWidth ?? ActualWidth),
						y: FindOptimalOffset(childDesiredSize.Height, visibleBounds.Y, visibleBounds.Height, (Parent as FrameworkElement)?.ActualHeight ?? ActualHeight)
#else
						x: FindOptimalOffset(childDesiredSize.Width, visibleBounds.X, visibleBounds.Width, ActualWidth),
						y: FindOptimalOffset(childDesiredSize.Height, visibleBounds.Y, visibleBounds.Height, ActualHeight)
#endif
					);

					double FindOptimalOffset(double desired, double visibleOffset, double visibleLength, double constraint)
					{
						// Center the flyout inside the first area that fits: within visible bounds, below status bar, the screen
						if (visibleLength >= desired)
						{
							return visibleOffset + (visibleLength - desired) / 2;
						}
						if (constraint - visibleOffset >= desired)
						{
							return visibleOffset + ((constraint - visibleOffset) - desired) / 2;
						}
						else
						{
							return constraint == 0 ? 0 : (constraint - desired) / 2;
						}
					}
					break;
				default: // Other unsupported placements
					finalPosition = new Point(
						x: (visibleBounds.Width - childDesiredSize.Width) / 2.0,
						y: (visibleBounds.Height - childDesiredSize.Height) / 2.0);
					break;
			}

			var justification = FlyoutBase.GetJustificationFromPlacementMode(popup.DesiredPlacement, FullPlacementRequested);

			var fits = true;
			if (!FullPlacementRequested)
			{
				if (IsPlacementModeVertical(placement))
				{
					fits = TestAndCenterAlignWithinLimits(
						anchorRect.X,
						anchorRect.Width,
						childDesiredSize.Width,
						visibleBounds.Left,
						visibleBounds.Right,
						justification,
						out var controlXPos
					);
					finalPosition.X = controlXPos;
				}
				else
				{
					fits = TestAndCenterAlignWithinLimits(
						anchorRect.Y,
						anchorRect.Height,
						childDesiredSize.Height,
						visibleBounds.Top,
						visibleBounds.Bottom,
						justification,
						out var controlYPos
					);
					finalPosition.Y = controlYPos;
				}
			}

			finalRect = new Rect(finalPosition, childDesiredSize);

			if (fits && RectHelper.Union(visibleBounds, finalRect).Equals(visibleBounds))
			{
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().LogDebug($"Accepted placement {placement} (choice {i}) with finalRect={finalRect} in visibleBounds={visibleBounds}");
				}
				placementFits = true;
				break; // this placement is acceptable
			}
		}

		if (!placementFits && Flyout is not null && preferredPlacement is not FlyoutBase.MajorPlacementMode.Full)
		{
			// A flyout that fits on no side is resized to fit next to its target, or failing that kept within the visible bounds, as in WinUI
			// (FlyoutBase::CalculatePlacementPrivate). finalRect is the preferred placement, which the loop tries last.
			finalRect = CanResizeChild
				? ResizeToFit(preferredPlacement, anchorRect, visibleBounds, (popup.Child as FrameworkElement)?.GetMinSize() ?? default, finalRect)
				: KeepWithinBounds(finalRect, visibleBounds);
		}

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().LogDebug($"Calculated placement, finalRect={finalRect}");
		}

		// Ensure the popup is not positioned fully beyond visible bounds.
		// This can happen for example when the user is trying to show a flyout at
		// Window.Current.Content - which will match Top position, which would be outside
		// the visible bounds (negative Y).
		// Flyouts that fit on no side are already kept within the visible bounds above: this is for tooltips, plain popups and Full placements.

		if (finalRect.Bottom < visibleBounds.Top)
		{
			finalRect = new Rect(
				finalRect.Left,
				visibleBounds.Top,
				finalRect.Width,
				finalRect.Height);
		}

		if (finalRect.Top > visibleBounds.Bottom)
		{
			finalRect = new Rect(
				finalRect.Left,
				visibleBounds.Bottom - finalRect.Height,
				finalRect.Width,
				finalRect.Height);
		}

		if (finalRect.Right < visibleBounds.Left)
		{
			finalRect = new Rect(
				visibleBounds.Left,
				finalRect.Top,
				finalRect.Width,
				finalRect.Height);
		}

		if (finalRect.Left > visibleBounds.Right)
		{
			finalRect = new Rect(
				visibleBounds.Right - finalRect.Width,
				finalRect.Top,
				finalRect.Width,
				finalRect.Height);
		}

		return finalRect;
	}

	// Return true if placement is along vertical axis, false otherwise.
	private static bool IsPlacementModeVertical(
		FlyoutBase.MajorPlacementMode placementMode)
	{
		// We are safe even if placementMode is Full. because the case for placementMode is Full has already been put in another if branch in function PerformPlacement.
		// if necessary, we can add another function : IsPlacementModeHorizontal
		return (placementMode == FlyoutBase.MajorPlacementMode.Top ||
				placementMode == FlyoutBase.MajorPlacementMode.Bottom);
	}

	/// <summary>
	/// Resizes a child that fits on no side of its target: along the axis of the preferred placement, the child moves to the side of the target
	/// with more space if it doesn't fit on the preferred side, and is made smaller to fit there. The result is then kept within the visible bounds.
	/// The child's size has already been limited to the visible bounds, so unlike WinUI there is no need to limit it along the other axis.
	/// Ported from WinUI: FlyoutBase_partial.cpp ResizeToFit.
	/// </summary>
	/// <remarks>
	/// Uno: the space next to the target excludes the gap kept from the target, as the child cannot extend beyond the visible bounds. The child
	/// is not shrunk when there is no space at all on either side (e.g. a target that fills the window), which would leave it with no size when
	/// its minimum size is 0; it is kept within the visible bounds instead.
	/// </remarks>
	private Rect ResizeToFit(FlyoutBase.MajorPlacementMode placement, Rect anchorRect, Rect visibleBounds, Size minSize, Rect rect)
	{
		var availableSpace = CalculateAvailableSpace(placement, anchorRect, visibleBounds);

		if (IsPlacementModeVertical(placement))
		{
			if (rect.Height > availableSpace)
			{
				var moreSpacePlacement = SelectSideWithMoreSpace(placement, anchorRect, visibleBounds, out var moreAvailableSpace);

				// Switch sides only if there's enough space to fit the minimum size.
				if (moreSpacePlacement != placement && moreAvailableSpace >= minSize.Height)
				{
					placement = moreSpacePlacement;
					availableSpace = moreAvailableSpace;
				}

				// Adjust the size only if it doesn't go under the minimum size.
				if (rect.Height > availableSpace && availableSpace > 0 && availableSpace >= minSize.Height)
				{
					rect.Height = availableSpace;
				}

				rect.Y = placement == FlyoutBase.MajorPlacementMode.Top
					? anchorRect.Top - PopupPlacementTargetMargin - rect.Height
					: anchorRect.Bottom + PopupPlacementTargetMargin;
			}
		}
		else
		{
			if (rect.Width > availableSpace)
			{
				var moreSpacePlacement = SelectSideWithMoreSpace(placement, anchorRect, visibleBounds, out var moreAvailableSpace);

				// Switch sides only if there's enough space to fit the minimum size.
				if (moreSpacePlacement != placement && moreAvailableSpace >= minSize.Width)
				{
					placement = moreSpacePlacement;
					availableSpace = moreAvailableSpace;
				}

				// Adjust the size only if it doesn't go under the minimum size.
				if (rect.Width > availableSpace && availableSpace > 0 && availableSpace >= minSize.Width)
				{
					rect.Width = availableSpace;
				}

				rect.X = placement == FlyoutBase.MajorPlacementMode.Left
					? anchorRect.Left - PopupPlacementTargetMargin - rect.Width
					: anchorRect.Right + PopupPlacementTargetMargin;
			}
		}

		// Finally, make sure we do not exceed the visible bounds.
		return KeepWithinBounds(rect, visibleBounds);
	}

	// Moves a rect no larger than the visible bounds so that it is within them.
	private static Rect KeepWithinBounds(Rect rect, Rect visibleBounds)
	{
		if (rect.X < visibleBounds.X)
		{
			rect.X = visibleBounds.X;
		}
		else if (rect.Right > visibleBounds.Right)
		{
			rect.X = visibleBounds.Right - rect.Width;
		}

		if (rect.Y < visibleBounds.Y)
		{
			rect.Y = visibleBounds.Y;
		}
		else if (rect.Bottom > visibleBounds.Bottom)
		{
			rect.Y = visibleBounds.Bottom - rect.Height;
		}

		return rect;
	}

	// Calculates the space between the target, less the gap kept from it, and the edge of the visible bounds on the side of the given placement.
	private double CalculateAvailableSpace(FlyoutBase.MajorPlacementMode placement, Rect anchorRect, Rect visibleBounds)
	{
		var (space, visibleLength) = placement switch
		{
			FlyoutBase.MajorPlacementMode.Top => (anchorRect.Top - visibleBounds.Top, visibleBounds.Height),
			FlyoutBase.MajorPlacementMode.Bottom => (visibleBounds.Bottom - anchorRect.Bottom, visibleBounds.Height),
			FlyoutBase.MajorPlacementMode.Left => (anchorRect.Left - visibleBounds.Left, visibleBounds.Width),
			FlyoutBase.MajorPlacementMode.Right => (visibleBounds.Right - anchorRect.Right, visibleBounds.Width),
			_ => throw new ArgumentOutOfRangeException(nameof(placement)),
		};

		return Math.Max(0, Math.Min(visibleLength, space - PopupPlacementTargetMargin));
	}

	// Returns the side of the target with more space along the axis of the given placement, or the given placement if both have the same space.
	private FlyoutBase.MajorPlacementMode SelectSideWithMoreSpace(FlyoutBase.MajorPlacementMode placement, Rect anchorRect, Rect visibleBounds, out double availableSpace)
	{
		var (before, after) = IsPlacementModeVertical(placement)
			? (FlyoutBase.MajorPlacementMode.Top, FlyoutBase.MajorPlacementMode.Bottom)
			: (FlyoutBase.MajorPlacementMode.Left, FlyoutBase.MajorPlacementMode.Right);

		var spaceBefore = CalculateAvailableSpace(before, anchorRect, visibleBounds);
		var spaceAfter = CalculateAvailableSpace(after, anchorRect, visibleBounds);

		if (spaceBefore > spaceAfter)
		{
			availableSpace = spaceBefore;
			return before;
		}

		if (spaceBefore < spaceAfter)
		{
			availableSpace = spaceAfter;
			return after;
		}

		availableSpace = spaceBefore;
		return placement;
	}

	// Align centers of anchor and control while keeping control coordinates within limits.
	private static bool TestAndCenterAlignWithinLimits(
		double anchorPos,
		double anchorSize,
		double controlSize,
		double lowLimit,
		double highLimit,
		FlyoutBase.PreferredJustification justification,
		out double controlPos
	)
	{
		bool fits = true;

		if (anchorSize == 0 && typeof(PopupPanel).Log().IsEnabled(LogLevel.Warning))
		{
			typeof(PopupPanel).Log().LogWarning($"{nameof(anchorSize)} is 0");
		}

		if (controlSize == 0 && typeof(PopupPanel).Log().IsEnabled(LogLevel.Warning))
		{
			typeof(PopupPanel).Log().LogWarning($"{nameof(anchorSize)} is 0");
		}

		if ((highLimit - lowLimit) > controlSize &&
			anchorSize >= 0.0 &&
			controlSize >= 0.0)
		{

			if (justification == FlyoutBase.PreferredJustification.Center)
			{
				controlPos = anchorPos + 0.5 * (anchorSize - controlSize);
			}
			else if (justification == FlyoutBase.PreferredJustification.Top || justification == FlyoutBase.PreferredJustification.Left)
			{
				controlPos = anchorPos;
			}
			else if (justification == FlyoutBase.PreferredJustification.Bottom || justification == FlyoutBase.PreferredJustification.Right)
			{
				controlPos = anchorPos + (anchorSize - controlSize);
			}
			else
			{
				throw new InvalidOperationException("Unsupported FlyoutBase.PreferredJustification");
			}

			if (controlPos < lowLimit)
			{
				controlPos = lowLimit;
			}
			else if (controlPos + controlSize > highLimit)
			{
				controlPos = highLimit - controlSize;
			}
		}
		else
		{
			controlPos = lowLimit;
			fits = false;
		}

		return fits;
	}

	private Rect GetVisibleBounds() => XamlRoot?.VisualTree.VisibleBounds ?? default;
}
