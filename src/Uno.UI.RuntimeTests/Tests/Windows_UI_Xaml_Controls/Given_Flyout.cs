using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Private.Infrastructure;
using Uno.Extensions;
using Uno.UI.Extensions;
using Uno.UI.RuntimeTests.Extensions;
using Uno.UI.RuntimeTests.FlyoutPages;
using Uno.UI.RuntimeTests.FramePages;
using Uno.UI.RuntimeTests.Helpers;
using Uno.UI.Toolkit.Extensions;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

using MenuBar = Microsoft.UI.Xaml.Controls.MenuBar;
using MenuBarItem = Microsoft.UI.Xaml.Controls.MenuBarItem;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Combinatorial.MSTest;
using Microsoft.UI.Xaml.Tests.Enterprise;
using Microsoft.UI.Xaml.Markup;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls
{
	[TestClass]
	[RunsOnUIThread]
	public class Given_Flyout
	{
		private string GetAllIsOpens() => string.Join(" ", VisualTreeHelper.GetOpenPopupsForXamlRoot(TestServices.WindowHelper.XamlRoot).Select(p => p.IsOpen));

		[TestMethod]
		[RunsOnUIThread]
		public async Task When_Default_ShowMode()
		{
			var flyout = new Flyout();
			Assert.AreEqual(FlyoutShowMode.Standard, flyout.ShowMode);
		}

		[TestMethod]
		[RunsOnUIThread]
		public async Task When_Unloaded_Before_Shown()
		{
			var button = new Button()
			{
				Flyout = new Flyout
				{
					Content = new Border { Width = 50, Height = 30 }
				}
			};

			TestServices.WindowHelper.WindowContent = button;

			await TestServices.WindowHelper.WaitForIdle();

			TestServices.WindowHelper.WindowContent = null;

			await TestServices.WindowHelper.WaitForIdle();
		}

#if HAS_UNO
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_LoadedAndUnloaded_Check_Binding()
		{

			var (flyout, content) = CreateFlyoutWithBindingMultipleChildren();

			var border = new Border()
			{
				DataContext = "My Data Context"
			};

			var buttonA = new Button()
			{
				Content = "Main Button",
				Flyout = flyout,
			};

			border.Child = buttonA;

			TestServices.WindowHelper.WindowContent = border;

			await TestServices.WindowHelper.WaitForLoaded(buttonA);

			await TestServices.WindowHelper.WaitForIdle();

			buttonA.RaiseClick();

			await TestServices.WindowHelper.WaitForIdle();

			await TestServices.WindowHelper.WaitForLoaded(content);

			flyout.Hide();

			await TestServices.WindowHelper.WaitForIdle();

			border.Child = null;

			await TestServices.WindowHelper.WaitForIdle();

			border.DataContext = "A New Context";
			border.Child = buttonA;

			await TestServices.WindowHelper.WaitForIdle();

			await TestServices.WindowHelper.WaitForLoaded(buttonA);

			buttonA.RaiseClick();

			await TestServices.WindowHelper.WaitForLoaded(content);

			var stackPanel = content as StackPanel;

			Assert.AreEqual("A New Context", (stackPanel.Children[0] as TextBlock).Text);

			flyout.Hide();

		}
#endif

		[TestMethod]
		public async Task When_Attached_To_Border_Check_Placement()
		{
			var (flyout, content) = CreateFlyout();

			const double MarginValue = 105;
			const int TargetWidth = 88;
			var target = new Border
			{
				Margin = new Thickness(MarginValue),
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Width = TargetWidth,
				Height = 23,
				Background = new SolidColorBrush(Colors.Red)
			};

			TestServices.WindowHelper.WindowContent = target;

			await TestServices.WindowHelper.WaitForLoaded(target);

			await TestServices.WindowHelper.WaitFor(() => target.ActualWidth == TargetWidth); // For some reason target is initially stretched on iOS

			try
			{
				FlyoutBase.SetAttachedFlyout(target, flyout);
				FlyoutBase.ShowAttachedFlyout(target);

				await TestServices.WindowHelper.WaitForLoaded(content);

				var contentCenter = content.GetOnScreenBounds().GetCenter();
				var targetCenter = target.GetOnScreenBounds().GetCenter();

				Assert.IsGreaterThan(MarginValue, targetCenter.X);
				Assert.AreEqual(targetCenter.X, contentCenter.X, delta: 2);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		public async Task When_Attached_To_TextBlock_Check_Placement()
		{
			var (flyout, content) = CreateFlyout();

			const double MarginValue = 105;
			var target = new TextBlock
			{
				Margin = new Thickness(MarginValue),
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Text = "Tweetle beetle battle"
			};

			TestServices.WindowHelper.WindowContent = target;

			await TestServices.WindowHelper.WaitForLoaded(target);

			try
			{
				FlyoutBase.SetAttachedFlyout(target, flyout);
				FlyoutBase.ShowAttachedFlyout(target);

				await TestServices.WindowHelper.WaitForLoaded(content);

				var contentCenter = content.GetOnScreenBounds().GetCenter();
				var targetCenter = target.GetOnScreenBounds().GetCenter();

				Assert.IsGreaterThan(MarginValue, targetCenter.X);
				Assert.AreEqual(targetCenter.X, contentCenter.X, delta: 2);
			}
			finally
			{
				flyout.Hide();
			}
		}

#if HAS_UNO
		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task BackButtonIntegration()
		{
			Button button = null;
			Flyout flyout = null;

			(flyout, button) = await SetupBasicFlyoutTest();

			var flyoutOpenedEvent = new Event();
			var flyoutClosedEvent = new Event();
			var openedRegistration = TestServices.CreateSafeEventRegistration<Flyout, EventHandler<object>>("Opened");
			var closedRegistration = TestServices.CreateSafeEventRegistration<Flyout, EventHandler<object>>("Closed");

			await TestServices.RunOnUIThread(() =>
			{
				openedRegistration.Attach(flyout, (s, e) =>
				{
					flyoutOpenedEvent.Set();
				});

				closedRegistration.Attach(flyout, (s, e) =>
				{
					flyoutClosedEvent.Set();
				});
			});

			await TestServices.RunOnUIThread(() =>
			{
				TestServices.LOG_OUTPUT("Show flyout");
				flyout.ShowAt(button);
			});
			await flyoutOpenedEvent.WaitForDefault();
			await TestServices.WindowHelper.WaitForIdle();

			bool handled = false;
			TestServices.LOG_OUTPUT("Dismiss flyout using Back button");
			handled = await TestServices.Utilities.InjectBackButtonPress();
			TestServices.VERIFY_IS_TRUE(handled);

			TestServices.LOG_OUTPUT("Waiting for flyout to close");
			await flyoutClosedEvent.WaitForDefault();
			await TestServices.WindowHelper.WaitForIdle();

			TestServices.LOG_OUTPUT("After closing a flyout, further back button presses should not get handled");
			handled = await TestServices.Utilities.InjectBackButtonPress();
			TestServices.VERIFY_IS_FALSE(handled);
		}
#endif

		private async Task<(Flyout flyout, Button button)> SetupBasicFlyoutTest()
		{
			Button button = null;
			Flyout flyout = null;
			var loadedEvent = new Event();
			var loadedRegistration = TestServices.CreateSafeEventRegistration<Grid, RoutedEventHandler>("Loaded");

			await TestServices.RunOnUIThread(() =>
			{
				var rootPanel = (Grid)(XamlReader.Load(
					"<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
					"      x:Name='root' Background='SlateBlue' Width='400' Height='200' VerticalAlignment='Top' HorizontalAlignment='Left'> " +
					"  <Button x:Name='button' Content='button.flyout' HorizontalAlignment='Left' FontSize='25' > " +
					"    <Button.Flyout> " +
					"      <Flyout Placement='Top'> " +
					"        <TextBlock FontSize='30' Text='BUTTON.FLYOUT' Margin='30' Foreground='DarkRed' x:Name='button_flyout_content'/> " +
					"      </Flyout> " +
					"    </Button.Flyout> " +
					"  </Button> " +
					"</Grid>"));

				TestServices.VERIFY_IS_NOT_NULL(rootPanel);
				loadedRegistration.Attach(rootPanel, (s, e) => { loadedEvent.Set(); });
				TestServices.WindowHelper.WindowContent = rootPanel;

				button = (Button)(rootPanel.FindName("button"));
				TestServices.VERIFY_IS_NOT_NULL(button);
				flyout = (Flyout)(button.Flyout);
				TestServices.VERIFY_IS_NOT_NULL(flyout);
			});

			await loadedEvent.WaitForDefault();
			await TestServices.WindowHelper.WaitForIdle();
			return (flyout, button);
		}

		[TestMethod]
		[DataRow(FlyoutPlacementMode.Top, HorizontalPosition.Center, VerticalPosition.BeyondTop)]
		[DataRow(FlyoutPlacementMode.Bottom, HorizontalPosition.Center, VerticalPosition.BeyondBottom)]
		[DataRow(FlyoutPlacementMode.Left, HorizontalPosition.BeyondLeft, VerticalPosition.Center)]
		[DataRow(FlyoutPlacementMode.Right, HorizontalPosition.BeyondRight, VerticalPosition.Center)]
		[DataRow(FlyoutPlacementMode.TopEdgeAlignedLeft, HorizontalPosition.LeftFlush, VerticalPosition.BeyondTop)]
		[DataRow(FlyoutPlacementMode.TopEdgeAlignedRight, HorizontalPosition.RightFlush, VerticalPosition.BeyondTop)]
		[DataRow(FlyoutPlacementMode.BottomEdgeAlignedLeft, HorizontalPosition.LeftFlush, VerticalPosition.BeyondBottom)]
		[DataRow(FlyoutPlacementMode.BottomEdgeAlignedRight, HorizontalPosition.RightFlush, VerticalPosition.BeyondBottom)]
		[DataRow(FlyoutPlacementMode.LeftEdgeAlignedTop, HorizontalPosition.BeyondLeft, VerticalPosition.TopFlush)]
		[DataRow(FlyoutPlacementMode.LeftEdgeAlignedBottom, HorizontalPosition.BeyondLeft, VerticalPosition.BottomFlush)]
		[DataRow(FlyoutPlacementMode.RightEdgeAlignedTop, HorizontalPosition.BeyondRight, VerticalPosition.TopFlush)]
		[DataRow(FlyoutPlacementMode.RightEdgeAlignedBottom, HorizontalPosition.BeyondRight, VerticalPosition.BottomFlush)]
		public async Task Check_Placement_All(
			FlyoutPlacementMode placementMode,
			HorizontalPosition horizontalPosition,
			VerticalPosition verticalPosition)
		{
			var (flyout, content) = CreateFlyout();

			flyout.Placement = placementMode;

			const double MarginValue = 97;
			const int TargetWidth = 88;
			var target = new Border
			{
				Margin = new Thickness(MarginValue),
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Width = TargetWidth,
				Height = 23,
				Background = new SolidColorBrush(Colors.Red)
			};

			TestServices.WindowHelper.WindowContent = target;

			await TestServices.WindowHelper.WaitForLoaded(target);
			await TestServices.WindowHelper.WaitFor(() => target.ActualWidth == TargetWidth); // For some reason target is initially stretched on iOS

			try
			{
				FlyoutBase.SetAttachedFlyout(target, flyout);
				FlyoutBase.ShowAttachedFlyout(target);

				await TestServices.WindowHelper.WaitForLoaded(content);

				VerifyRelativeContentPosition(horizontalPosition, verticalPosition, content, MarginValue, target);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		[DataRow(FlyoutPlacementMode.Top, HorizontalPosition.Center, VerticalPosition.BeyondTop)]
		[DataRow(FlyoutPlacementMode.Bottom, HorizontalPosition.Center, VerticalPosition.BeyondBottom)]
		[DataRow(FlyoutPlacementMode.Left, HorizontalPosition.BeyondLeft, VerticalPosition.Center)]
		[DataRow(FlyoutPlacementMode.Right, HorizontalPosition.BeyondRight, VerticalPosition.Center)]
		[DataRow(FlyoutPlacementMode.TopEdgeAlignedLeft, HorizontalPosition.LeftFlush, VerticalPosition.BeyondTop)]
		[DataRow(FlyoutPlacementMode.TopEdgeAlignedRight, HorizontalPosition.RightFlush, VerticalPosition.BeyondTop)]
		[DataRow(FlyoutPlacementMode.BottomEdgeAlignedLeft, HorizontalPosition.LeftFlush, VerticalPosition.BeyondBottom)]
		[DataRow(FlyoutPlacementMode.BottomEdgeAlignedRight, HorizontalPosition.RightFlush, VerticalPosition.BeyondBottom)]
		[DataRow(FlyoutPlacementMode.LeftEdgeAlignedTop, HorizontalPosition.BeyondLeft, VerticalPosition.TopFlush)]
		[DataRow(FlyoutPlacementMode.LeftEdgeAlignedBottom, HorizontalPosition.BeyondLeft, VerticalPosition.BottomFlush)]
		[DataRow(FlyoutPlacementMode.RightEdgeAlignedTop, HorizontalPosition.BeyondRight, VerticalPosition.TopFlush)]
		[DataRow(FlyoutPlacementMode.RightEdgeAlignedBottom, HorizontalPosition.BeyondRight, VerticalPosition.BottomFlush)]
		public async Task Check_Placement_All_WithPosition(
			FlyoutPlacementMode placementMode,
			HorizontalPosition horizontalPosition,
			VerticalPosition verticalPosition)
		{
			var (flyout, content) = CreateFlyout();
			var position = new Windows.Foundation.Point(50, 50);
			var options = new FlyoutShowOptions
			{
				Placement = placementMode,
				Position = position,
			};

			const double MarginValue = 97;
			const int TargetWidth = 88;
			var target = new Border
			{
				Margin = new Thickness(MarginValue),
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Width = TargetWidth,
				Height = 23,
				Background = new SolidColorBrush(Colors.Red)
			};

			TestServices.WindowHelper.WindowContent = target;

			await TestServices.WindowHelper.WaitForLoaded(target);
			await TestServices.WindowHelper.WaitFor(() => target.ActualWidth == TargetWidth); // For some reason target is initially stretched on iOS

			try
			{
				flyout.ShowAt(target, options);

				await TestServices.WindowHelper.WaitForLoaded(content);

				VerifyRelativeContentPosition(position, horizontalPosition, verticalPosition, content, MarginValue, target);
			}
			finally
			{
				flyout.Hide();
			}
		}

		// "Popup successfully fits left-aligned on Android - possibly because the status bar offset changes the layouting?"
		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeAndroid | RuntimeTestPlatforms.SkiaAndroid | RuntimeTestPlatforms.SkiaIOS | RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_Too_Large_For_Any_Fallback()
		{
			var target = new TextBlock
			{
				Text = "Anchor",
				VerticalAlignment = VerticalAlignment.Bottom
			};

			var stretchedTargetWrapper = new Border
			{
				MinHeight = 600,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Child = target
			};

			var windowHeight = ApplicationView.GetForCurrentView().VisibleBounds.Height;
			if (TestServices.WindowHelper.IsXamlIsland)
			{
				windowHeight = TestServices.WindowHelper.XamlRoot.Size.Height;
			}

			var flyoutContent = new Ellipse
			{
				Width = 90,
				Height = windowHeight * 1.5,
				Fill = new SolidColorBrush(Colors.Tomato)
			};

			var presenterStyle = new Style
			{
				TargetType = typeof(FlyoutPresenter),
				Setters =
				{
					new Setter(FrameworkElement.MaxHeightProperty, windowHeight*1.7)
				}
			};

			var flyout = new Flyout
			{
				FlyoutPresenterStyle = presenterStyle,
				Content = flyoutContent,
				Placement = FlyoutPlacementMode.Top
			};

			TestServices.WindowHelper.WindowContent = stretchedTargetWrapper;

			await TestServices.WindowHelper.WaitForLoaded(target);

			try
			{
				FlyoutBase.SetAttachedFlyout(target, flyout);
				FlyoutBase.ShowAttachedFlyout(target);

				await TestServices.WindowHelper.WaitForLoaded(flyoutContent);

				var contentScreenBounds = flyoutContent.GetOnScreenBounds();
				var contentCenter = contentScreenBounds.GetCenter();
				var targetScreenBounds = target.GetOnScreenBounds();
				var targetCenter = targetScreenBounds.GetCenter();

				var presenter = await TestServices.WindowHelper.WaitForNonNull(() => flyoutContent.FindFirstParent<FlyoutPresenter>());

				VerifyRelativeContentPosition(HorizontalPosition.Center,
					VerticalPosition.BeyondTop,
					presenter, // The content itself is in a ScrollViewer and its bounds will exceed the visible area
					0,
					target
				);
			}
			finally
			{
				flyout.Hide();
			}

		}

		[TestMethod]
		[DataRow(VerticalAlignment.Top)]
		[DataRow(VerticalAlignment.Center)]
		[DataRow(VerticalAlignment.Bottom)]
		public async Task When_Taller_Than_Window(VerticalAlignment targetAlignment)
		{
			var target = await SetupTallFlyoutTarget(targetAlignment);
			var (flyout, lastItem) = CreateTallFlyout();

			await ShowAndVerifyTallFlyout(flyout, target, lastItem, targetAlignment);
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)] // WinUI's menus are windowed, so they can extend past the window
		[DataRow(VerticalAlignment.Top)]
		[DataRow(VerticalAlignment.Center)]
		[DataRow(VerticalAlignment.Bottom)]
		public async Task When_MenuFlyout_Taller_Than_Window(VerticalAlignment targetAlignment)
		{
			var target = await SetupTallFlyoutTarget(targetAlignment);
			var (flyout, lastItem) = CreateTallMenuFlyout();

			await ShowAndVerifyTallFlyout(flyout, target, lastItem, targetAlignment);
		}

		[TestMethod]
		public async Task When_Taller_Than_Window_RightToLeft()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top, FlowDirection.RightToLeft);
			var (flyout, lastItem) = CreateTallFlyout();

			await ShowAndVerifyTallFlyout(flyout, target, lastItem, VerticalAlignment.Top);
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)] // WinUI's menus are windowed, so they can extend past the window
		public async Task When_MenuFlyout_Taller_Than_Window_RightToLeft()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top, FlowDirection.RightToLeft);
			var (flyout, lastItem) = CreateTallMenuFlyout();

			await ShowAndVerifyTallFlyout(flyout, target, lastItem, VerticalAlignment.Top);
		}

		[TestMethod]
		public async Task When_Taller_Than_Window_EdgeAligned()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top);
			var (flyout, lastItem) = CreateTallFlyout();
			flyout.Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft;

			try
			{
				flyout.ShowAt(target);

				await TestServices.WindowHelper.WaitForLoaded(lastItem);
				await TestServices.WindowHelper.WaitForIdle();

				var presenter = GetPresenter(lastItem);
				Assert.AreEqual(target.GetOnScreenBounds().Left, presenter.GetOnScreenBounds().Left, delta: 1, "The flyout should stay aligned with the target's left edge.");
				await VerifyTallFlyoutFitsWindow(presenter, lastItem, target, VerticalAlignment.Top);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		public async Task When_Taller_Than_Window_Target_Partly_Above_Window()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top);
			target.Margin = new Thickness(0, -15, 0, 0);
			await TestServices.WindowHelper.WaitForIdle();
			var (flyout, lastItem) = CreateTallFlyout();

			await ShowAndVerifyTallFlyout(flyout, target, lastItem, VerticalAlignment.Top);
		}

		[TestMethod]
		public async Task When_Taller_Than_Window_Nested()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top);
			var (outer, outerLastItem) = CreateTallFlyout();
			var innerTarget = new Button { Content = "Open" };
			((StackPanel)outer.Content).Children.Insert(0, innerTarget);
			var (inner, innerLastItem) = CreateTallFlyout();

			try
			{
				outer.ShowAt(target);
				await TestServices.WindowHelper.WaitForLoaded(outerLastItem);
				await TestServices.WindowHelper.WaitForIdle();

				inner.ShowAt(innerTarget);
				await TestServices.WindowHelper.WaitForLoaded(innerLastItem);
				await TestServices.WindowHelper.WaitForIdle();

				Assert.IsTrue(outer.IsOpen, "The outer flyout should stay open.");
				await VerifyTallFlyoutFitsWindow(GetPresenter(innerLastItem), innerLastItem, innerTarget, VerticalAlignment.Top);
			}
			finally
			{
				inner.Hide();
				outer.Hide();
			}
		}

		[TestMethod]
		[DataRow(FlyoutPlacementMode.Left, HorizontalAlignment.Left)]
		[DataRow(FlyoutPlacementMode.Right, HorizontalAlignment.Right)]
		public async Task When_Wider_Than_Window(FlyoutPlacementMode placement, HorizontalAlignment targetAlignment)
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Center, horizontalAlignment: targetAlignment);
			var content = new Border
			{
				Width = Math.Ceiling(TestServices.WindowHelper.XamlRoot.Size.Width * 1.5),
				Height = 100,
				Background = new SolidColorBrush(Colors.Green),
			};
			var flyout = new Flyout
			{
				Content = content,
				Placement = placement,
				FlyoutPresenterStyle = new Style
				{
					TargetType = typeof(FlyoutPresenter),
					Setters =
					{
						new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity),
						new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Enabled),
						new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto),
					},
				},
			};

			try
			{
				flyout.ShowAt(target);

				await TestServices.WindowHelper.WaitForLoaded(content);
				await TestServices.WindowHelper.WaitForIdle();

				// The target is at the edge of the window on the preferred side, so the flyout opens on the other side and is made narrower to fit.
				var presenter = GetPresenter(content);
				var presenterBounds = presenter.GetOnScreenBounds();
				var targetBounds = target.GetOnScreenBounds();
				VerifyWithinWindow(presenterBounds);
				VerifyArrangedAtMeasuredSize(presenter);
				Assert.IsTrue(
					placement == FlyoutPlacementMode.Left ? presenterBounds.Left >= targetBounds.Right : presenterBounds.Right <= targetBounds.Left,
					$"The flyout ({presenterBounds}) should open on the other side of its target ({targetBounds}), where there is more space.");
				NumberAssert.Greater(presenter.FindFirstChild<ScrollViewer>().ScrollableWidth, 0);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		public async Task When_Taller_Than_Window_MinHeight_Larger_Than_Room()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Center);
			var (flyout, lastItem) = CreateTallFlyout();
			flyout.FlyoutPresenterStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, TestServices.WindowHelper.XamlRoot.Size.Height * 0.75));

			try
			{
				flyout.ShowAt(target);

				await TestServices.WindowHelper.WaitForLoaded(lastItem);
				await TestServices.WindowHelper.WaitForIdle();

				// Its MinHeight fits on neither side of the target, so it isn't made smaller but is kept within the window, as in WinUI.
				var presenter = GetPresenter(lastItem);
				var presenterBounds = presenter.GetOnScreenBounds();
				VerifyWithinWindow(presenterBounds);
				VerifyArrangedAtMeasuredSize(presenter);
				await VerifyScrollsToLastItem(presenter, presenterBounds, lastItem);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		public async Task When_ShowAt_Without_Target()
			=> await ShowAtWithoutTarget(zeroMinHeight: false);

		// WinUI shrinks the presenter to the space next to its target even when there is none, which collapses it when its MinHeight is 0.
		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_ShowAt_Without_Target_Zero_MinHeight()
			=> await ShowAtWithoutTarget(zeroMinHeight: true);

		private static async Task ShowAtWithoutTarget(bool zeroMinHeight)
		{
			var root = new Grid { Background = new SolidColorBrush(Colors.Gray), Children = { new Border { Width = 10, Height = 10 } } };
			TestServices.WindowHelper.WindowContent = root;
			await TestServices.WindowHelper.WaitForLoaded(root);

			var content = new Border { Width = 150, Height = 100, Background = new SolidColorBrush(Colors.Green) };
			var flyout = new Flyout { Content = content, XamlRoot = TestServices.WindowHelper.XamlRoot };
			if (zeroMinHeight)
			{
				flyout.FlyoutPresenterStyle = GetSimpleFlyoutPresenterStyle();
			}

			try
			{
				// Without a target, the window's content is the target, which leaves no space on any side: the flyout keeps its size.
				flyout.ShowAt((DependencyObject)null, new FlyoutShowOptions());

				await TestServices.WindowHelper.WaitForLoaded(content);
				await TestServices.WindowHelper.WaitForIdle();

				var presenter = GetPresenter(content);
				NumberAssert.GreaterOrEqual(presenter.ActualHeight, 100);
				VerifyArrangedAtMeasuredSize(presenter);
				Assert.AreEqual(0, presenter.FindFirstChild<ScrollViewer>().ScrollableHeight, delta: 0.5);
				VerifyWithinWindow(presenter.GetOnScreenBounds());
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		public async Task When_Taller_Than_Window_Target_Moves_While_Opening()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top);
			var (flyout, lastItem) = CreateTallFlyout();

			// The target moves in the layout pass that opens the flyout: the flyout is measured before the target is arranged at its new position.
			target.VerticalAlignment = VerticalAlignment.Center;

			await ShowAndVerifyTallFlyout(flyout, target, lastItem, VerticalAlignment.Center);
		}

		[TestMethod]
		public async Task When_Taller_Than_Window_Target_Shown_While_Opening()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Bottom);
			target.Visibility = Visibility.Collapsed;
			await TestServices.WindowHelper.WaitForIdle();

			// Moved while collapsed, the target is laid out at its new position only once it is shown again, in the layout pass that opens the flyout.
			target.VerticalAlignment = VerticalAlignment.Top;
			await TestServices.WindowHelper.WaitForIdle();
			var (flyout, lastItem) = CreateTallFlyout();

			target.Visibility = Visibility.Visible;

			await ShowAndVerifyTallFlyout(flyout, target, lastItem, VerticalAlignment.Top);
		}

#if HAS_UNO
		// What a window resize does to an open flyout: its target moves and its popup panel is measured again (XamlRoot.Changed) in one pass.
		[TestMethod]
		public async Task When_Taller_Than_Window_Target_Moves_While_Open()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top);
			var (flyout, lastItem) = CreateTallFlyout();

			try
			{
				flyout.ShowAt(target);
				await TestServices.WindowHelper.WaitForLoaded(lastItem);
				await TestServices.WindowHelper.WaitForIdle();

				target.VerticalAlignment = VerticalAlignment.Center;
				flyout._popup.PopupPanel.InvalidateMeasure();
				await TestServices.WindowHelper.WaitForIdle();

				await VerifyTallFlyoutFitsWindow(GetPresenter(lastItem), lastItem, target, VerticalAlignment.Center);
			}
			finally
			{
				flyout.Hide();
			}
		}
#endif

		[TestMethod]
		public async Task When_Taller_Than_Window_Content_Shrinks_While_Open()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top);
			var (flyout, lastItem) = CreateTallFlyout();
			var panel = (StackPanel)flyout.Content;

			try
			{
				flyout.ShowAt(target);
				await TestServices.WindowHelper.WaitForLoaded(lastItem);
				await TestServices.WindowHelper.WaitForIdle();

				var presenter = GetPresenter(lastItem);
				NumberAssert.Greater(presenter.FindFirstChild<ScrollViewer>().ScrollableHeight, 0);

				while (panel.Children.Count > 3)
				{
					panel.Children.RemoveAt(panel.Children.Count - 1);
				}

				await TestServices.WindowHelper.WaitForIdle();

				var presenterBounds = presenter.GetOnScreenBounds();
				var targetBounds = target.GetOnScreenBounds();
				Assert.AreEqual(0, presenter.FindFirstChild<ScrollViewer>().ScrollableHeight, delta: 0.5);
				VerifyArrangedAtMeasuredSize(presenter);
				NumberAssert.GreaterOrEqual(presenterBounds.Top, targetBounds.Bottom);
				NumberAssert.Less(presenterBounds.Top - targetBounds.Bottom, 10);
#if HAS_UNO
				// Unlike WinUI, which keeps the size it gave the presenter, Uno places the flyout again and it shrinks with its content.
				NumberAssert.Less(presenterBounds.Height, TestServices.WindowHelper.XamlRoot.Size.Height / 2);
#endif
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		public async Task When_Content_Grows_Taller_Than_Window_While_Open()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top);
			var (flyout, lastItem) = CreateTallFlyout();
			var panel = (StackPanel)flyout.Content;
			var removed = new List<UIElement>();

			while (panel.Children.Count > 3)
			{
				removed.Insert(0, panel.Children[panel.Children.Count - 1]);
				panel.Children.RemoveAt(panel.Children.Count - 1);
			}

			try
			{
				flyout.ShowAt(target);
				await TestServices.WindowHelper.WaitForLoaded(panel);
				await TestServices.WindowHelper.WaitForIdle();

				var presenter = GetPresenter(panel);
				Assert.AreEqual(0, presenter.FindFirstChild<ScrollViewer>().ScrollableHeight, delta: 0.5);

				foreach (var item in removed)
				{
					panel.Children.Add(item);
				}

				await TestServices.WindowHelper.WaitForLoaded(lastItem);
				await TestServices.WindowHelper.WaitForIdle();

				await VerifyTallFlyoutFitsWindow(presenter, lastItem, target, VerticalAlignment.Top);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		public async Task When_Taller_Than_Window_Layout_Settles()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Center);
			var panel = new MeasureCountingStackPanel();
			var itemCount = (int)Math.Ceiling(TestServices.WindowHelper.XamlRoot.Size.Height * 1.5 / 40);
			for (var i = 0; i < itemCount; i++)
			{
				panel.Children.Add(new Border { Width = 100, Height = 40, Background = new SolidColorBrush(i % 2 == 0 ? Colors.Green : Colors.Blue) });
			}

			// Without a vertical scroll bar, the content is measured with the presenter's height, so it is measured again whenever the presenter is.
			var presenterStyle = GetUnlimitedHeightFlyoutPresenterStyle();
			presenterStyle.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
			var flyout = new Flyout { Content = panel, FlyoutPresenterStyle = presenterStyle };

			try
			{
				flyout.ShowAt(target);
				await TestServices.WindowHelper.WaitForLoaded(panel);
				await TestServices.WindowHelper.WaitForIdle();

				panel.MeasureCount = 0;
				await Task.Delay(250);
				await TestServices.WindowHelper.WaitForIdle();
				Assert.AreEqual(0, panel.MeasureCount, "The content should not be measured again while nothing changes.");

				panel.MeasureCount = 0;
				((Border)panel.Children[0]).Height = 41;
				await TestServices.WindowHelper.WaitForIdle();
				NumberAssert.Less(panel.MeasureCount, 5);
				VerifyArrangedAtMeasuredSize(GetPresenter(panel));
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)] // WinUI's menus are windowed, so they can extend past the window
		public async Task When_MenuFlyout_Taller_Than_Window_At_Position()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top);
			var (flyout, lastItem) = CreateTallMenuFlyout();

			try
			{
				flyout.ShowAt(target, new FlyoutShowOptions { Position = new Point(10, 10) });

				await TestServices.WindowHelper.WaitForLoaded(lastItem);
				await TestServices.WindowHelper.WaitForIdle();

				var presenter = GetPresenter(lastItem);
				var presenterBounds = presenter.GetOnScreenBounds();
				VerifyWithinWindow(presenterBounds);
				await VerifyScrollsToLastItem(presenter, presenterBounds, lastItem);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		public async Task When_Fits_Below_Only_Not_Resized()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Top);
			target.Margin = new Thickness(0, 20, 0, 0);
			await TestServices.WindowHelper.WaitForIdle();
			var windowHeight = TestServices.WindowHelper.XamlRoot.Size.Height;
			var targetBounds = target.GetOnScreenBounds();

			// Too tall for the space above the target, short enough for the space below it.
			var contentHeight = Math.Floor(Math.Max(targetBounds.Top + 20, (windowHeight - targetBounds.Bottom) / 2));
			var content = new Border { Width = 100, Height = contentHeight, Background = new SolidColorBrush(Colors.Green) };
			var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.Top };

			try
			{
				flyout.ShowAt(target);

				await TestServices.WindowHelper.WaitForLoaded(content);
				await TestServices.WindowHelper.WaitForIdle();

				var presenter = GetPresenter(content);
				var presenterBounds = presenter.GetOnScreenBounds();

				NumberAssert.GreaterOrEqual(presenterBounds.Top, targetBounds.Bottom);
				Assert.AreEqual(contentHeight, content.ActualHeight, delta: 0.5);
				Assert.AreEqual(0, presenter.FindFirstChild<ScrollViewer>().ScrollableHeight, delta: 0.5);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		public async Task When_Fits_Beside_Only_Not_Resized()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Center, horizontalAlignment: HorizontalAlignment.Left);
			var windowHeight = TestServices.WindowHelper.XamlRoot.Size.Height;
			var targetBounds = target.GetOnScreenBounds();

			// Too tall for the space above and below the target, short enough for the window: it opens to its right, the only side with room.
			var contentHeight = Math.Floor(windowHeight * 0.8);
			var content = new Border { Width = 100, Height = contentHeight, Background = new SolidColorBrush(Colors.Green) };
			var presenterStyle = GetSimpleFlyoutPresenterStyle();
			presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, double.PositiveInfinity));
			var flyout = new Flyout
			{
				Content = content,
				Placement = FlyoutPlacementMode.Top,
				FlyoutPresenterStyle = presenterStyle,
			};

			try
			{
				flyout.ShowAt(target);

				await TestServices.WindowHelper.WaitForLoaded(content);
				await TestServices.WindowHelper.WaitForIdle();

				var presenter = GetPresenter(content);
				var presenterBounds = presenter.GetOnScreenBounds();

				NumberAssert.GreaterOrEqual(presenterBounds.Left, targetBounds.Right);
				Assert.AreEqual(contentHeight, content.ActualHeight, delta: 0.5);
				Assert.AreEqual(0, presenter.FindFirstChild<ScrollViewer>().ScrollableHeight, delta: 0.5);
			}
			finally
			{
				flyout.Hide();
			}
		}

#if HAS_UNO
		[TestMethod]
		public async Task When_Taller_Than_Window_Presenter_Resizing_Disabled()
		{
			var target = await SetupTallFlyoutTarget(VerticalAlignment.Center);
			var (flyout, lastItem) = CreateTallFlyout();
			flyout.DisablePresenterResizing();

			try
			{
				flyout.ShowAt(target);

				await TestServices.WindowHelper.WaitForLoaded(lastItem);
				await TestServices.WindowHelper.WaitForIdle();

				// Without resizing, the presenter isn't made smaller to fit next to its target, and it is placed within the visible area, covering the
				// target, as in WinUI. WinUI keeps the presenter's full height, cut off by the window, where Uno measures it within the window, so it
				// scrolls.
				var presenter = GetPresenter(lastItem);
				var visibleBounds = TestServices.WindowHelper.XamlRoot.VisualTree.VisibleBounds;
				NumberAssert.GreaterOrEqual(presenter.ActualHeight, visibleBounds.Height - 1);
				VerifyWithin(LayoutInformation.GetLayoutSlot(presenter), visibleBounds, tolerance: 0.01);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		public async Task When_CalendarDatePicker_Fits_No_Side()
		{
			// The calendar fits neither below the picker, beside it, nor above it, where there is no room at all.
			var windowSize = TestServices.WindowHelper.XamlRoot.Size;
			var picker = new CalendarDatePicker
			{
				Width = windowSize.Width,
				Height = windowSize.Height - 100,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
			};
			await SetWindowContentOverWindow(picker);

			try
			{
				picker.IsCalendarOpen = true;
				await TestServices.WindowHelper.WaitForIdle();

				var flyout = FlyoutBase.OpenFlyouts.LastOrDefault();
				Assert.IsNotNull(flyout);
				var presenter = flyout.GetPresenter();
				await TestServices.WindowHelper.WaitForLoaded(presenter);
				await TestServices.WindowHelper.WaitForIdle();

				// As in WinUI, the calendar isn't made smaller to fit the 95 pixels below the picker, but it is kept within the window.
				Assert.IsFalse(flyout.AllowPresenterResizing);
				NumberAssert.Greater(presenter.ActualHeight, 100);
				VerifyArrangedAtMeasuredSize(presenter);
				VerifyWithinWindow(presenter.GetOnScreenBounds());
			}
			finally
			{
				picker.IsCalendarOpen = false;
				await TestServices.WindowHelper.WaitForIdle();
			}
		}
#endif

		// Places the target at the given alignment within the whole window (see SetWindowContentOverWindow).
		private static async Task<Border> SetupTallFlyoutTarget(
			VerticalAlignment verticalAlignment,
			FlowDirection flowDirection = FlowDirection.LeftToRight,
			HorizontalAlignment horizontalAlignment = HorizontalAlignment.Center)
		{
			var target = new Border
			{
				Width = 120,
				Height = 30,
				HorizontalAlignment = horizontalAlignment,
				VerticalAlignment = verticalAlignment,
				Background = new SolidColorBrush(Colors.Red),
			};

			await SetWindowContentOverWindow(target, flowDirection);

			return target;
		}

		// The test content is neither stretched to the window nor at its top left corner: this places the element in a panel moved to the
		// window's origin and made as large as the window, so that its alignment is relative to the window.
		private static async Task SetWindowContentOverWindow(FrameworkElement element, FlowDirection flowDirection = FlowDirection.LeftToRight)
		{
			var windowSize = TestServices.WindowHelper.XamlRoot.Size;
			var root = new Grid
			{
				Width = windowSize.Width,
				Height = windowSize.Height,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Children = { new Grid { FlowDirection = flowDirection, Children = { element } } },
			};

			TestServices.WindowHelper.WindowContent = root;
			await TestServices.WindowHelper.WaitForLoaded(element);

			var rootBounds = root.GetOnScreenBounds();
			root.Margin = new Thickness(-rootBounds.X, -rootBounds.Y, 0, 0);
			await TestServices.WindowHelper.WaitForIdle();

			rootBounds = root.GetOnScreenBounds();
			Assert.AreEqual(0, rootBounds.X, delta: 0.5, "The test content should start at the left of the window.");
			Assert.AreEqual(0, rootBounds.Y, delta: 0.5, "The test content should start at the top of the window.");
		}

		// A flyout whose presenter has no maximum height and whose content is half again as tall as the window.
		private static (Flyout Flyout, FrameworkElement LastItem) CreateTallFlyout()
		{
			const double ItemHeight = 40;
			var itemCount = (int)Math.Ceiling(TestServices.WindowHelper.XamlRoot.Size.Height * 1.5 / ItemHeight);
			var panel = new StackPanel();

			for (var i = 0; i < itemCount; i++)
			{
				panel.Children.Add(new Border
				{
					Width = 100,
					Height = ItemHeight,
					Background = new SolidColorBrush(i % 2 == 0 ? Colors.Green : Colors.Blue),
				});
			}

			var flyout = new Flyout { Content = panel, FlyoutPresenterStyle = GetUnlimitedHeightFlyoutPresenterStyle() };

			return (flyout, (FrameworkElement)panel.Children[itemCount - 1]);
		}

		private static Style GetUnlimitedHeightFlyoutPresenterStyle() => new Style
		{
			TargetType = typeof(FlyoutPresenter),
			Setters = { new Setter(FrameworkElement.MaxHeightProperty, double.PositiveInfinity) },
		};

		// A menu at least half again as tall as the window: its items are counted as 24 pixels tall, less than any menu item style makes them.
		private static (FlyoutBase Flyout, FrameworkElement LastItem) CreateTallMenuFlyout()
		{
			var itemCount = (int)Math.Ceiling(TestServices.WindowHelper.XamlRoot.Size.Height * 1.5 / 24);
			var flyout = new MenuFlyout();

			for (var i = 0; i < itemCount; i++)
			{
				flyout.Items.Add(new MenuFlyoutItem { Text = $"Item {i}" });
			}

			return (flyout, flyout.Items[itemCount - 1]);
		}

		private static FrameworkElement GetPresenter(FrameworkElement content)
			=> (FrameworkElement)content.FindFirstParent<FlyoutPresenter>() ?? content.FindFirstParent<MenuFlyoutPresenter>();

		// Opens the flyout and checks it with VerifyTallFlyoutFitsWindow.
		private static async Task ShowAndVerifyTallFlyout(FlyoutBase flyout, FrameworkElement target, FrameworkElement lastItem, VerticalAlignment targetAlignment)
		{
			try
			{
				flyout.ShowAt(target);

				await TestServices.WindowHelper.WaitForLoaded(lastItem);
				await TestServices.WindowHelper.WaitForIdle();

				await VerifyTallFlyoutFitsWindow(GetPresenter(lastItem), lastItem, target, targetAlignment);
			}
			finally
			{
				flyout.Hide();
			}
		}

		// Checks that the presenter is within the window, next to the target on the side with more space, arranged at the size it was measured
		// at, and that it scrolls to its last item.
		private static async Task VerifyTallFlyoutFitsWindow(
			FrameworkElement presenter,
			FrameworkElement lastItem,
			FrameworkElement target,
			VerticalAlignment targetAlignment)
		{
			var presenterBounds = presenter.GetOnScreenBounds();
			var targetBounds = target.GetOnScreenBounds();

			VerifyWithinWindow(presenterBounds);
			VerifyArrangedAtMeasuredSize(presenter);

#if HAS_UNO
			const double EdgeTolerance = 0;
#else
			const double EdgeTolerance = 1; // WinUI places a resized flyout flush against its target
#endif
			var opensAbove = presenterBounds.Bottom <= targetBounds.Top + EdgeTolerance;
			var opensBelow = presenterBounds.Top >= targetBounds.Bottom - EdgeTolerance;
			Assert.IsTrue(opensAbove || opensBelow, $"The flyout ({presenterBounds}) should not cover its target ({targetBounds}).");

			if (targetAlignment == VerticalAlignment.Top)
			{
				Assert.IsTrue(opensBelow, $"The flyout ({presenterBounds}) should open below its target ({targetBounds}), where there is more space.");
			}
			else if (targetAlignment == VerticalAlignment.Bottom)
			{
				Assert.IsTrue(opensAbove, $"The flyout ({presenterBounds}) should open above its target ({targetBounds}), where there is more space.");
			}

			await VerifyScrollsToLastItem(presenter, presenterBounds, lastItem);
		}

		private static void VerifyWithinWindow(Rect bounds)
		{
#if HAS_UNO
			const double Tolerance = 0.01; // rounding only
#else
			// WinUI adds its 4 pixel gap from the target after fitting the flyout, so it can extend that much past the window.
			const double Tolerance = 5;
#endif
			var windowSize = TestServices.WindowHelper.XamlRoot.Size;

			VerifyWithin(bounds, new Rect(0, 0, windowSize.Width, windowSize.Height), Tolerance);
		}

		private static void VerifyWithin(Rect bounds, Rect area, double tolerance)
		{
			NumberAssert.GreaterOrEqual(bounds.Top, area.Top - tolerance);
			NumberAssert.GreaterOrEqual(bounds.Left, area.Left - tolerance);
			NumberAssert.LessOrEqual(bounds.Bottom, area.Bottom + tolerance);
			NumberAssert.LessOrEqual(bounds.Right, area.Right + tolerance);
		}

		// A presenter measured larger than the size it is arranged at is cut off instead of scrolling.
		private static void VerifyArrangedAtMeasuredSize(FrameworkElement presenter)
		{
			var slot = LayoutInformation.GetLayoutSlot(presenter);
			Assert.AreEqual(slot.Height, presenter.ActualHeight, delta: 1, $"Height arranged in slot {slot}");
			Assert.AreEqual(slot.Width, presenter.ActualWidth, delta: 1, $"Width arranged in slot {slot}");
		}

		private static async Task VerifyScrollsToLastItem(FrameworkElement presenter, Rect presenterBounds, FrameworkElement lastItem)
		{
			var scrollViewer = presenter.FindFirstChild<ScrollViewer>();
			NumberAssert.Greater(scrollViewer.ScrollableHeight, 0);

			scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, disableAnimation: true);
			await TestServices.WindowHelper.WaitForIdle();

			var lastItemBounds = lastItem.GetOnScreenBounds();
			NumberAssert.GreaterOrEqual(lastItemBounds.Top, presenterBounds.Top);
			NumberAssert.LessOrEqual(lastItemBounds.Bottom, presenterBounds.Bottom);
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		[DataRow(FlyoutPlacementMode.Top, HorizontalPosition.Center, VerticalPosition.BeyondTop)]
		[DataRow(FlyoutPlacementMode.Bottom, HorizontalPosition.Center, VerticalPosition.BeyondBottom)]
		[DataRow(FlyoutPlacementMode.Left, HorizontalPosition.BeyondLeft, VerticalPosition.Center)]
		[DataRow(FlyoutPlacementMode.Right, HorizontalPosition.BeyondRight, VerticalPosition.Center)]
		[DataRow(FlyoutPlacementMode.TopEdgeAlignedLeft, HorizontalPosition.LeftFlush, VerticalPosition.BeyondTop)]
		[DataRow(FlyoutPlacementMode.TopEdgeAlignedRight, HorizontalPosition.RightFlush, VerticalPosition.BeyondTop)]
		[DataRow(FlyoutPlacementMode.BottomEdgeAlignedLeft, HorizontalPosition.LeftFlush, VerticalPosition.BeyondBottom)]
		[DataRow(FlyoutPlacementMode.BottomEdgeAlignedRight, HorizontalPosition.RightFlush, VerticalPosition.BeyondBottom)]
		[DataRow(FlyoutPlacementMode.LeftEdgeAlignedTop, HorizontalPosition.BeyondLeft, VerticalPosition.TopFlush)]
		[DataRow(FlyoutPlacementMode.LeftEdgeAlignedBottom, HorizontalPosition.BeyondLeft, VerticalPosition.BottomFlush)]
		[DataRow(FlyoutPlacementMode.RightEdgeAlignedTop, HorizontalPosition.BeyondRight, VerticalPosition.TopFlush)]
		[DataRow(FlyoutPlacementMode.RightEdgeAlignedBottom, HorizontalPosition.BeyondRight, VerticalPosition.BottomFlush)]
		public async Task Check_Placement_All_MenuFlyout(
			FlyoutPlacementMode placementMode,
			HorizontalPosition horizontalPosition,
			VerticalPosition verticalPosition)
		{
			var flyout = CreateBasicMenuFlyout();

			flyout.Placement = placementMode;

			const double MarginValue = 97;
			const int TargetWidth = 88;
			var target = new Border
			{
				Margin = new Thickness(MarginValue),
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Width = TargetWidth,
				Height = 23,
				Background = new SolidColorBrush(Colors.Red)
			};

			TestServices.WindowHelper.WindowContent = target;

			await TestServices.WindowHelper.WaitForLoaded(target);
			await TestServices.WindowHelper.WaitFor(() => target.ActualWidth == TargetWidth); // For some reason target is initially stretched on iOS

			try
			{
				FlyoutBase.SetAttachedFlyout(target, flyout);
				FlyoutBase.ShowAttachedFlyout(target);

				var presenter = flyout.Presenter;

				await TestServices.WindowHelper.WaitForLoaded(presenter);

				var content = presenter.FindFirstChild<ScrollViewer>();

				VerifyRelativeContentPosition(horizontalPosition, verticalPosition, content, MarginValue, target);
			}
			finally
			{
				flyout.Hide();
			}
		}

#if HAS_UNO // For macOS, see https://github.com/unoplatform/uno/issues/626 
		[TestMethod]
		[RunsOnUIThread]
		public async Task Test_Flyout_Binding()
		{

			var (flyout, content) = CreateFlyoutWithBinding();

			var buttonA = new Button()
			{
				Content = "Button A",
				Flyout = flyout,
				DataContext = "My Data Context",
			};

			TestServices.WindowHelper.WindowContent = buttonA;

			await TestServices.WindowHelper.WaitForLoaded(buttonA);

			buttonA.RaiseClick();

			await TestServices.WindowHelper.WaitForLoaded(content);

			var stackPanel = content as StackPanel;
			Assert.AreEqual("My Data Context", (stackPanel.Children[0] as TextBlock).Text);
		}

		[TestMethod]
		[RunsOnUIThread]
		public async Task Test_Flyout_Binding_In_MenuFlyoutItem()
		{
			var menuBar = new MenuBar();
			var menuBarItem = new MenuBarItem();
			var menuFlyoutItem = new MenuFlyoutItem();

			menuFlyoutItem.SetBinding(MenuFlyoutItem.TextProperty, new Binding { Path = "." });

			menuBar.Items.Add(menuBarItem);
			menuBarItem.Items.Add(menuFlyoutItem);

			menuBar.DataContext = "42";

			TestServices.WindowHelper.WindowContent = menuBar;

			await TestServices.WindowHelper.WaitForLoaded(menuBar);

			menuBarItem.Invoke();

			await TestServices.WindowHelper.WaitForLoaded(menuFlyoutItem);

			Assert.AreEqual("42", menuFlyoutItem.Text);

			menuBarItem.CloseMenuFlyout();
		}
#endif

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task Test_Flyout_Binding_With_SetAttachedFlyout()
		{
			Border b1 = new()
			{
				DataContext = "41",
				Width = 10,
				Height = 10,
			};
			Border b2 = new()
			{
				DataContext = "42",
				Width = 10,
				Height = 10,
			};

			var stackPanel = new StackPanel()
			{
				Children = { b1, b2 }
			};

			TestServices.WindowHelper.WindowContent = stackPanel;
			await TestServices.WindowHelper.WaitForLoaded(stackPanel);

			var tb = new TextBlock() { Tag = "SUT" };
			const string EmptyPath =
#if HAS_UNO
				".";
#else
				"";
#endif
			tb.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(EmptyPath) });
			var SUT = new Flyout
			{
				Content = tb
			};

			FlyoutBase.SetAttachedFlyout(b1, SUT);
			FlyoutBase.SetAttachedFlyout(b2, SUT);

			FlyoutBase.ShowAttachedFlyout(b1);
			await TestServices.WindowHelper.WaitForLoaded(tb);
			Assert.AreEqual("41", tb.Text);
			SUT.Hide();

			await TestServices.WindowHelper.WaitForIdle();

			FlyoutBase.ShowAttachedFlyout(b2);
			await TestServices.WindowHelper.WaitForLoaded(tb);
			Assert.AreEqual("42", tb.Text);
			SUT.Hide();
		}

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_Flyout_Content_Takes_Focus()
		{
			var stackPanel = new StackPanel();
			var button = new Button() { Content = "Flyout owner" };
			stackPanel.Children.Add(button);
			TestServices.WindowHelper.WindowContent = stackPanel;
			await TestServices.WindowHelper.WaitForIdle();

			var flyout = new Flyout();
			var flyoutButton = new Button() { Content = "Flyout content" };
			flyout.Content = flyoutButton;
			FlyoutBase.SetAttachedFlyout(button, flyout);
			button.Focus(FocusState.Pointer);

			Assert.AreEqual(button, FocusManager.GetFocusedElement(TestServices.WindowHelper.XamlRoot));

			FlyoutBase.ShowAttachedFlyout(button);
			flyoutButton.Focus(FocusState.Pointer);
			await TestServices.WindowHelper.WaitForIdle();

			var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(flyoutButton.XamlRoot)[0];

			Assert.AreEqual(Visibility.Visible, popup.Visibility);
			Assert.AreNotEqual(button, FocusManager.GetFocusedElement(TestServices.WindowHelper.XamlRoot));

			flyout.Hide();
			await TestServices.WindowHelper.WaitForIdle();

			// The visibility of the popup remains on, but it's closed.
			Assert.AreEqual(Visibility.Visible, popup.Visibility);
			Assert.AreEqual(button, FocusManager.GetFocusedElement(TestServices.WindowHelper.XamlRoot));

			TestServices.WindowHelper.WindowContent = null;
		}

		[TestMethod]
		[RunsOnUIThread]
		public async Task When_Flyout_Has_Focusable_Child()
		{
			var stackPanel = new StackPanel();
			var button = new Button() { Content = "Flyout owner" };
			stackPanel.Children.Add(button);
			TestServices.WindowHelper.WindowContent = stackPanel;
			await TestServices.WindowHelper.WaitForIdle();
			await TestServices.WindowHelper.WaitForLoaded(button);

			var flyout = new Flyout();
			var flyoutButton = new Button() { Content = "Flyout content" };
			flyout.Content = flyoutButton;
			FlyoutBase.SetAttachedFlyout(button, flyout);
			button.Focus(FocusState.Pointer);

			Assert.AreEqual(button, FocusManager.GetFocusedElement(TestServices.WindowHelper.XamlRoot));

			FlyoutBase.ShowAttachedFlyout(button);
			await TestServices.WindowHelper.WaitForIdle();
			await TestServices.WindowHelper.WaitForLoaded(flyoutButton);

			Assert.AreEqual(flyoutButton, FocusManager.GetFocusedElement(TestServices.WindowHelper.XamlRoot));

			flyout.Hide();
			await TestServices.WindowHelper.WaitForIdle();

			TestServices.WindowHelper.WindowContent = null;
		}

		[TestMethod]
		[RunsOnUIThread]
		public async Task When_PlacementTarget_Binding()
		{
			if (TestServices.WindowHelper.IsXamlIsland)
			{
				Assert.Inconclusive($"Not supported under XAML islands yet https://github.com/unoplatform/uno/issues/8978");
			}

			var SUT = new When_PlacementTarget_Binding();
			TestServices.WindowHelper.WindowContent = SUT;
			await TestServices.WindowHelper.WaitForLoaded(SUT);

			try
			{
				SUT.DataContext = 42;

				Assert.AreEqual(SUT.DataContext, SUT.myButton.Content);

				SUT.contextFlyout.ShowAt(SUT.myButton);

				await TestServices.WindowHelper.WaitForIdle();

				Assert.AreEqual(SUT.DataContext, SUT.myButton.Content);
			}
			finally
			{
#if HAS_UNO
				SUT.contextFlyout.Close();
#endif
			}
		}

		[TestMethod]
		[RunsOnUIThread]
		public void When_Hide_Always_Closing()
		{
			Flyout flyout = new Flyout();
			var closingCalled = false;
			flyout.Closing += (sender, args) => closingCalled = true;

			flyout.Hide();
			Assert.IsTrue(closingCalled);
		}

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_Opening_Canceled()
		{

			Flyout flyout = new Flyout();
			try
			{
				var button = new Button()
				{
					Content = "Test"
				};
				TestServices.WindowHelper.WindowContent = button;
				await TestServices.WindowHelper.WaitForIdle();


				var innerBorder = new Border()
				{
					Width = 100,
					Height = 100
				};
				flyout.Content = innerBorder;
				flyout.Opening += (sender, args) => ((Flyout)sender).Hide();
				flyout.ShowAt(button);

				await TestServices.WindowHelper.WaitForIdle();

				Assert.IsFalse(flyout.IsOpen);
				Assert.IsFalse(innerBorder.IsLoaded);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_Opening_And_Closing_Canceled()
		{
			Flyout flyout = new Flyout();
			bool cancelClosing = true;
			try
			{
				var button = new Button()
				{
					Content = "Test"
				};
				TestServices.WindowHelper.WindowContent = button;
				await TestServices.WindowHelper.WaitForIdle();


				var innerBorder = new Border()
				{
					Width = 100,
					Height = 100
				};
				flyout.Content = innerBorder;
				flyout.Opening += (sender, args) => ((Flyout)sender).Hide();

				void OnClosing(object sender, FlyoutBaseClosingEventArgs args)
				{
					args.Cancel = cancelClosing;
				}
				flyout.Closing += OnClosing;
				flyout.ShowAt(button);

				await TestServices.WindowHelper.WaitForIdle();

				Assert.IsTrue(flyout.IsOpen);
				Assert.IsTrue(innerBorder.IsLoaded);
			}
			finally
			{
				cancelClosing = false;
				flyout.Hide();
			}
		}

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_Opening_And_Closing_Nested_Flyouts()
		{
			var flyout1 = new Flyout();
			var flyout2 = new Flyout();
			var flyout3 = new Flyout();
			var flyout4 = new Flyout();
			var flyout5 = new Flyout();
			try
			{
				var button1 = new Button
				{
					Content = "button1",
					Flyout = flyout1
				};

				var button2 = new Button
				{
					Content = "button2",
					Flyout = flyout2
				};

				var button3 = new Button
				{
					Content = "button3",
					Flyout = flyout3
				};

				var button4 = new Button
				{
					Content = "button4",
					Flyout = flyout4
				};

				var button5 = new Button
				{
					Content = "button5",
					Flyout = flyout5
				};

				flyout1.Content = button2;
				flyout2.Content = button3;
				flyout3.Content = button4;
				flyout4.Content = button5;
				flyout5.Content = new TextBox { Text = "text" };

				var output = "";

				flyout1.Closing += (_, args) => output += $"closing1 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";
				flyout2.Closing += (_, args) => output += $"closing2 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";
				flyout3.Closing += (_, args) => output += $"closing3 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";
				flyout4.Closing += (_, args) => output += $"closing4 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";
				flyout5.Closing += (_, args) => output += $"closing5 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";

				flyout1.Closed += (_, _) => output += $"closed1 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()}\n";
				flyout2.Closed += (_, _) => output += $"closed2 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()}\n";
				flyout3.Closed += (_, _) => output += $"closed3 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()}\n";
				flyout4.Closed += (_, _) => output += $"closed4 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()}\n";
				flyout5.Closed += (_, _) => output += $"closed5 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()}\n";

				await UITestHelper.Load(button1);

				var xamlRoot = button1.XamlRoot;
				Assert.IsNotNull(xamlRoot);
				Assert.AreEqual(TestServices.WindowHelper.XamlRoot, xamlRoot);
				flyout1.XamlRoot = xamlRoot;
				flyout2.XamlRoot = xamlRoot;
				flyout3.XamlRoot = xamlRoot;
				flyout4.XamlRoot = xamlRoot;
				flyout5.XamlRoot = xamlRoot;

				flyout1.ShowAt(button1);
				await TestServices.WindowHelper.WaitForLoaded(button2);
				flyout2.ShowAt(button2);
				await TestServices.WindowHelper.WaitForLoaded(button3);
				flyout3.ShowAt(button3);
				await TestServices.WindowHelper.WaitForLoaded(button4);
				flyout4.ShowAt(button4);
				await TestServices.WindowHelper.WaitForLoaded(button5);
				flyout5.ShowAt(button5);
				await TestServices.WindowHelper.WaitForIdle();

				flyout1.Hide();
				await TestServices.WindowHelper.WaitForIdle();

#if UNO_HAS_ENHANCED_LIFECYCLE
				var expected =
				"""
				closing1 True True True True True True True True True True False
				closing2 True True True True True True True True True True False
				closing3 True True True True True True True True True True False
				closing4 True True True True True True True True True True False
				closing5 True True True True True True True True True True False
				closed1 False False False False False 
				closing3 False False False False False  False
				closing4 False False False False False  False
				closing5 False False False False False  False
				closed2 False False False False False 
				closing4 False False False False False  False
				closing5 False False False False False  False
				closed3 False False False False False 
				closing5 False False False False False  False
				closed4 False False False False False 
				closed5 False False False False False 

				""";
#elif HAS_UNO
				var expected =
				"""
				closing1 True True True True True True True True True True False
				closing2 True True True True True True True True True True False
				closing3 True True True True True True True True True True False
				closing4 True True True True True True True True True True False
				closing5 True True True True True True True True True True False
				closed1 False False False False False 
				closing2 False False False False False  False
				closing3 False False False False False  False
				closing4 False False False False False  False
				closing5 False False False False False  False
				closed2 False False False False False 
				closing3 False False False False False  False
				closing4 False False False False False  False
				closing5 False False False False False  False
				closed3 False False False False False 
				closing4 False False False False False  False
				closing5 False False False False False  False
				closed4 False False False False False 
				closing5 False False False False False  False
				closed5 False False False False False 

				""";
#else
				var expected =
				"""
				closing1 True True True True True True True True True True False
				closing2 True True True True True True True True True True False
				closing3 True True True True True True True True True True False
				closing4 True True True True True True True True True True False
				closing5 True True True True True True True True True True False
				closed5 False False False False False 

				""";
#endif

				Assert.AreEqual(expected.Replace("\r\n", "\n"), output);
			}
			finally
			{
				flyout1.Hide();
				flyout2.Hide();
				flyout3.Hide();
				flyout4.Hide();
				flyout5.Hide();
			}
		}

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_Opening_And_Closing_Nested_Flyouts_Not_Open()
		{
			var flyout1 = new Flyout();
			var flyout2 = new Flyout();
			try
			{
				var button1 = new Button
				{
					Content = "button1",
					Flyout = flyout1
				};

				var button2 = new Button
				{
					Content = "button2",
					Flyout = flyout2
				};

				flyout1.Content = button2;
				flyout2.Content = new TextBox { Text = "text" };

				var output = "";

				flyout1.Closing += (_, args) => output += $"closing1 {flyout1.IsOpen} {flyout2.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";
				flyout2.Closing += (_, args) => output += $"closing2 {flyout1.IsOpen} {flyout2.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";

				flyout1.Closed += (_, _) => output += $"closed1 {flyout1.IsOpen} {flyout2.IsOpen} {GetAllIsOpens()}\n";
				flyout2.Closed += (_, _) => output += $"closed2 {flyout1.IsOpen} {flyout2.IsOpen} {GetAllIsOpens()}\n";

				TestServices.WindowHelper.WindowContent = button1;
				await TestServices.WindowHelper.WaitForIdle();

				var xamlRoot = button1.XamlRoot;
				Assert.IsNotNull(xamlRoot);
				Assert.AreEqual(TestServices.WindowHelper.XamlRoot, xamlRoot);
				flyout1.XamlRoot = xamlRoot;
				flyout2.XamlRoot = xamlRoot;

				flyout1.ShowAt(button1);
				await TestServices.WindowHelper.WaitForIdle();

				// flyout2 is not open, so Closing should NOT be invoked.

				flyout1.Hide();
				await TestServices.WindowHelper.WaitForIdle();

				var expected =
				"""
				closing1 True False True False
				closed1 False False 

				""";

				Assert.AreEqual(expected.Replace("\r\n", "\n"), output);
			}
			finally
			{
				flyout1.Hide();
				flyout2.Hide();
			}
		}

		[TestMethod]
		[RunsOnUIThread]
		[Ignore("Fails")]
		public async Task When_Opening_And_Closing_Nested_Flyouts_Canceled()
		{
			var flyout1 = new Flyout();
			var flyout2 = new Flyout();
			var flyout3 = new Flyout();
			var flyout4 = new Flyout();
			var flyout5 = new Flyout();
			var cancel = true;
			try
			{
				var button1 = new Button
				{
					Content = "button1",
					Flyout = flyout1
				};

				var button2 = new Button
				{
					Content = "button2",
					Flyout = flyout2
				};

				var button3 = new Button
				{
					Content = "button3",
					Flyout = flyout3
				};

				var button4 = new Button
				{
					Content = "button4",
					Flyout = flyout4
				};

				var button5 = new Button
				{
					Content = "button5",
					Flyout = flyout5
				};

				flyout1.Content = button2;
				flyout2.Content = button3;
				flyout3.Content = button4;
				flyout4.Content = button5;
				flyout5.Content = new TextBox { Text = "text" };

				var output = "";

				flyout1.Closing += (_, args) => output += $"closing1 {flyout1.IsOpen} {flyout1.IsOpen} {flyout1.IsOpen} {flyout1.IsOpen} {flyout1.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";
				flyout2.Closing += (_, args) => output += $"closing2 {flyout2.IsOpen} {flyout2.IsOpen} {flyout2.IsOpen} {flyout2.IsOpen} {flyout2.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";
				flyout3.Closing += (_, args) =>
				{
					output += $"closing3 {flyout3.IsOpen} {flyout3.IsOpen} {flyout3.IsOpen} {flyout3.IsOpen} {flyout3.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";
					args.Cancel = cancel;
				};
				flyout4.Closing += (_, args) => output += $"closing4 {flyout4.IsOpen} {flyout4.IsOpen} {flyout4.IsOpen} {flyout4.IsOpen} {flyout4.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";
				flyout5.Closing += (_, args) => output += $"closing5 {flyout5.IsOpen} {flyout5.IsOpen} {flyout5.IsOpen} {flyout5.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()} {args.Cancel}\n";

				flyout1.Closed += (_, _) => output += $"closed1 {flyout1.IsOpen} {flyout1.IsOpen} {flyout1.IsOpen} {flyout1.IsOpen} {flyout1.IsOpen} {GetAllIsOpens()}\n";
				flyout2.Closed += (_, _) => output += $"closed2 {flyout2.IsOpen} {flyout2.IsOpen} {flyout2.IsOpen} {flyout2.IsOpen} {flyout2.IsOpen} {GetAllIsOpens()}\n";
				flyout3.Closed += (_, _) => output += $"closed3 {flyout3.IsOpen} {flyout3.IsOpen} {flyout3.IsOpen} {flyout3.IsOpen} {flyout3.IsOpen} {GetAllIsOpens()}\n";
				flyout4.Closed += (_, _) => output += $"closed4 {flyout4.IsOpen} {flyout4.IsOpen} {flyout4.IsOpen} {flyout4.IsOpen} {flyout4.IsOpen} {GetAllIsOpens()}\n";
				flyout5.Closed += (_, _) => output += $"closed5 {flyout5.IsOpen} {flyout5.IsOpen} {flyout5.IsOpen} {flyout5.IsOpen} {flyout5.IsOpen} {GetAllIsOpens()}\n";

				TestServices.WindowHelper.WindowContent = button1;
				await TestServices.WindowHelper.WaitForIdle();

				var xamlRoot = button1.XamlRoot;
				Assert.IsNotNull(xamlRoot);
				Assert.AreEqual(TestServices.WindowHelper.XamlRoot, xamlRoot);
				flyout1.XamlRoot = xamlRoot;
				flyout2.XamlRoot = xamlRoot;
				flyout3.XamlRoot = xamlRoot;
				flyout4.XamlRoot = xamlRoot;
				flyout5.XamlRoot = xamlRoot;

				flyout1.ShowAt(button1);
				await TestServices.WindowHelper.WaitForIdle();
				flyout2.ShowAt(button2);
				await TestServices.WindowHelper.WaitForIdle();
				flyout3.ShowAt(button3);
				await TestServices.WindowHelper.WaitForIdle();
				flyout4.ShowAt(button4);
				await TestServices.WindowHelper.WaitForIdle();
				flyout5.ShowAt(button5);
				await TestServices.WindowHelper.WaitForIdle();

				flyout1.Hide();
				await TestServices.WindowHelper.WaitForIdle();

				var expected =
				"""
				closing1 True True True True True True True True True True False
				closing2 True True True True True True True True True True False
				closing3 True True True True True True True True True True False
				closed1 False False False False False True True True
				closing2 False False False False False True True True False
				closing3 True True True True True True True True False
				closed2 False False False False False True True True
				closing3 True True True True True True True True False

				""";

				Assert.AreEqual(expected.Replace("\r\n", "\n"), output);
			}
			finally
			{
				cancel = false;
				flyout1.Hide();
				flyout2.Hide();
				flyout3.Hide();
				flyout4.Hide();
				flyout5.Hide();
			}
		}

		//#if HAS_UNO
		//		[TestMethod]
		//		[RunsOnUIThread]
		//		public async Task When_Window_Unfocused()
		//		{
		//			var flyout1 = new Flyout();
		//			try
		//			{
		//				var button1 = new Button
		//				{
		//					Content = "button1",
		//					Flyout = flyout1
		//				};

		//				flyout1.Content = new TextBox { Text = "text" };

		//				var output = "";

		//				flyout1.Closing += (_, args) => output += $"closing1 {flyout1.IsOpen} {args.Cancel}\n";
		//				flyout1.Closed += (_, _) => output += $"closed1 {flyout1.IsOpen}\n";

		//				TestServices.WindowHelper.WindowContent = button1;
		//				await TestServices.WindowHelper.WaitForIdle();

		//				flyout1.ShowAt(button1);
		//				await TestServices.WindowHelper.WaitForIdle();

		//				Window.Current.OnNativeActivated(CoreWindowActivationState.Deactivated);
		//				await TestServices.WindowHelper.WaitForIdle();

		//				var expected =
		//				"""
		//				closing1 True False
		//				closed1 False

		//				""";

		//				Assert.AreEqual(expected.Replace("\r\n", "\n"), output);
		//			}
		//			finally
		//			{
		//				Window.Current.OnNativeActivated(CoreWindowActivationState.CodeActivated);
		//				flyout1.Hide();
		//			}
		//		}

		//#if __SKIA__ || __WASM__
		//		[TestMethod]
		//		[RunsOnUIThread]
		//		public async Task When_Window_Resized()
		//		{
		//			var flyout1 = new Flyout();
		//			try
		//			{
		//				var button1 = new Button
		//				{
		//					Content = "button1",
		//					Flyout = flyout1
		//				};

		//				flyout1.Content = new TextBox { Text = "text" };

		//				var output = "";

		//				flyout1.Closing += (_, args) => output += $"closing1 {flyout1.IsOpen} {args.Cancel}\n";
		//				flyout1.Closed += (_, _) => output += $"closed1 {flyout1.IsOpen}\n";

		//				TestServices.WindowHelper.WindowContent = button1;
		//				await TestServices.WindowHelper.WaitForIdle();

		//				flyout1.ShowAt(button1);
		//				await TestServices.WindowHelper.WaitForIdle();

		//				Window.Current.OnNativeSizeChanged(Window.Current.Bounds.Size.Add(new Size(0.0001, 0)));
		//				await TestServices.WindowHelper.WaitForIdle();

		//				var expected =
		//				"""
		//				closing1 True False
		//				closed1 False

		//				""";

		//				Assert.AreEqual(expected.Replace("\r\n", "\n"), output);
		//			}
		//			finally
		//			{
		//				Window.Current.OnNativeSizeChanged(Window.Current.Bounds.Size.Subtract(new Size(0.0001, 0)));
		//				flyout1.Hide();
		//			}
		//		}
		//#endif

		//		[TestMethod]
		//		[RunsOnUIThread]
		//		public async Task When_Window_Unfocused_Canceled()
		//		{
		//			var flyout1 = new Flyout();
		//			var cancel = true;
		//			try
		//			{
		//				var button1 = new Button
		//				{
		//					Content = "button1",
		//					Flyout = flyout1
		//				};

		//				flyout1.Content = new TextBox { Text = "text" };

		//				var output = "";

		//				flyout1.Closing += (_, args) =>
		//				{
		//					output += $"closing1 {flyout1.IsOpen} {args.Cancel}\n";
		//					args.Cancel = cancel;
		//				};
		//				flyout1.Closed += (_, _) => output += $"closed1 {flyout1.IsOpen}\n";

		//				TestServices.WindowHelper.WindowContent = button1;
		//				await TestServices.WindowHelper.WaitForIdle();

		//				flyout1.ShowAt(button1);
		//				await TestServices.WindowHelper.WaitForIdle();

		//				Window.Current.OnNativeActivated(CoreWindowActivationState.Deactivated);
		//				await TestServices.WindowHelper.WaitForIdle();

		//				var expected =
		//					"""
		//					closing1 True False

		//					""";

		//				Assert.AreEqual(expected.Replace("\r\n", "\n"), output);
		//			}
		//			finally
		//			{
		//				cancel = false;
		//				Window.Current.OnNativeActivated(CoreWindowActivationState.CodeActivated);
		//				flyout1.Hide();
		//			}
		//		}

		//#if __SKIA__ || __WASM__
		//		[TestMethod]
		//		[RunsOnUIThread]
		//		public async Task When_Window_Resized_Canceled()
		//		{
		//			var flyout1 = new Flyout();
		//			var cancel = true;
		//			try
		//			{
		//				var button1 = new Button
		//				{
		//					Content = "button1",
		//					Flyout = flyout1
		//				};

		//				flyout1.Content = new TextBox { Text = "text" };

		//				var output = "";

		//				flyout1.Closing += (_, args) =>
		//				{
		//					output += $"closing1 {flyout1.IsOpen} {args.Cancel}\n";
		//					args.Cancel = cancel;
		//				};
		//				flyout1.Closed += (_, _) => output += $"closed1 {flyout1.IsOpen}\n";

		//				TestServices.WindowHelper.WindowContent = button1;
		//				await TestServices.WindowHelper.WaitForIdle();

		//				flyout1.ShowAt(button1);
		//				await TestServices.WindowHelper.WaitForIdle();

		//				Window.Current.OnNativeSizeChanged(Window.Current.Bounds.Size.Add(new Size(0.0001, 0)));
		//				await TestServices.WindowHelper.WaitForIdle();

		//				var expected =
		//					"""
		//					closing1 True False

		//					""";

		//				Assert.AreEqual(expected.Replace("\r\n", "\n"), output);
		//			}
		//			finally
		//			{
		//				Window.Current.OnNativeSizeChanged(Window.Current.Bounds.Size.Subtract(new Size(0.0001, 0)));
		//				cancel = false;
		//				flyout1.Hide();
		//			}
		//		}
		//#endif

		//		[TestMethod]
		//		[RunsOnUIThread]
		//		public async Task When_Window_Unfocused_Nested_Flyouts()
		//		{
		//			var flyout1 = new Flyout();
		//			var flyout2 = new Flyout();
		//			var flyout3 = new Flyout();
		//			var flyout4 = new Flyout();
		//			var flyout5 = new Flyout();
		//			try
		//			{
		//				var button1 = new Button
		//				{
		//					Content = "button1",
		//					Flyout = flyout1
		//				};

		//				var button2 = new Button
		//				{
		//					Content = "button2",
		//					Flyout = flyout2
		//				};

		//				var button3 = new Button
		//				{
		//					Content = "button3",
		//					Flyout = flyout3
		//				};

		//				var button4 = new Button
		//				{
		//					Content = "button4",
		//					Flyout = flyout4
		//				};

		//				var button5 = new Button
		//				{
		//					Content = "button5",
		//					Flyout = flyout5
		//				};

		//				flyout1.Content = button2;
		//				flyout2.Content = button3;
		//				flyout3.Content = button4;
		//				flyout4.Content = button5;
		//				flyout5.Content = new TextBox { Text = "text" };

		//				var output = "";

		//				flyout1.Closing += (_, args) => output += $"closing1 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens(flyout1)} {args.Cancel}\n";
		//				flyout2.Closing += (_, args) => output += $"closing2 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens(flyout2)} {args.Cancel}\n";
		//				flyout3.Closing += (_, args) => output += $"closing3 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens(flyout3)} {args.Cancel}\n";
		//				flyout4.Closing += (_, args) => output += $"closing4 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens(flyout4)} {args.Cancel}\n";
		//				flyout5.Closing += (_, args) => output += $"closing5 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens(flyout5)} {args.Cancel}\n";

		//				flyout1.Closed += (_, _) => output += $"closed1 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens(flyout1)}\n";
		//				flyout2.Closed += (_, _) => output += $"closed2 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens(flyout2)}\n";
		//				flyout3.Closed += (_, _) => output += $"closed3 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens(flyout3)}\n";
		//				flyout4.Closed += (_, _) => output += $"closed4 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens(flyout4)}\n";
		//				flyout5.Closed += (_, _) => output += $"closed5 {flyout1.IsOpen} {flyout2.IsOpen} {flyout3.IsOpen} {flyout4.IsOpen} {flyout5.IsOpen} {GetAllIsOpens(flyout5)}\n";

		//				TestServices.WindowHelper.WindowContent = button1;
		//				await TestServices.WindowHelper.WaitForIdle();

		//				flyout1.ShowAt(button1);
		//				await TestServices.WindowHelper.WaitForIdle();
		//				flyout2.ShowAt(button2);
		//				await TestServices.WindowHelper.WaitForIdle();
		//				flyout3.ShowAt(button3);
		//				await TestServices.WindowHelper.WaitForIdle();
		//				flyout4.ShowAt(button4);
		//				await TestServices.WindowHelper.WaitForIdle();
		//				flyout5.ShowAt(button5);
		//				await TestServices.WindowHelper.WaitForIdle();

		//				Window.Current.OnNativeActivated(CoreWindowActivationState.Deactivated);
		//				await TestServices.WindowHelper.WaitForIdle();

		//				var expected =
		//				"""
		//				closing5 True True True True True True True True True True False
		//				closing4 True True True True False True True True True False
		//				closing5 True True True True False True True True True False
		//				closing3 True True True False False True True True False
		//				closing4 True True True False False True True True False
		//				closing5 True True True False False True True True False
		//				closing2 True True False False False True True False
		//				closing3 True True False False False True True False
		//				closing4 True True False False False True True False
		//				closing5 True True False False False True True False
		//				closing1 True False False False False True False
		//				closing2 True False False False False True False
		//				closing3 True False False False False True False
		//				closing4 True False False False False True False
		//				closing5 True False False False False True False
		//				closed1 False False False False False 
		//				closing2 False False False False False  False
		//				closing3 False False False False False  False
		//				closing4 False False False False False  False
		//				closing5 False False False False False  False
		//				closed2 False False False False False 
		//				closing3 False False False False False  False
		//				closing4 False False False False False  False
		//				closing5 False False False False False  False
		//				closed3 False False False False False 
		//				closing4 False False False False False  False
		//				closing5 False False False False False  False
		//				closed4 False False False False False 
		//				closing5 False False False False False  False
		//				closed5 False False False False False 

		//				""";

		//				Assert.AreEqual(expected.Replace("\r\n", "\n"), output);
		//			}
		//			finally
		//			{
		//				Window.Current.OnNativeActivated(CoreWindowActivationState.CodeActivated);
		//				flyout1.Hide();
		//				flyout2.Hide();
		//				flyout3.Hide();
		//				flyout4.Hide();
		//				flyout5.Hide();
		//			}
		//		}
		//#endif

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_Opening_XamlRootIsSet()
		{
			var flyout = new Flyout();
			try
			{
				var host = new Button() { Content = "Asd" };
				TestServices.WindowHelper.WindowContent = host;
				await TestServices.WindowHelper.WaitForIdle();
				await TestServices.WindowHelper.WaitForLoaded(host);

				var capture = default(XamlRoot);

				flyout.Opening += (s, e) => capture = (s as Flyout).XamlRoot;

				flyout.ShowAt(host);
				await TestServices.WindowHelper.WaitForIdle();
				await TestServices.WindowHelper.WaitForIdle();
				flyout.Hide();

				Assert.AreEqual(host.XamlRoot, capture, "Flyout did not inherit the XamlRoot from its placementTarget.");
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeAndroid | RuntimeTestPlatforms.NativeIOS | RuntimeTestPlatforms.NativeWasm)]
		public async Task When_Button_ContextFlyout_XamlRoot()
		{
			var flyout = new Flyout();
			var host = new Button() { Content = "Asd" };
			host.ContextFlyout = flyout;
			TestServices.WindowHelper.WindowContent = host;
			await TestServices.WindowHelper.WaitForIdle();
			await TestServices.WindowHelper.WaitForLoaded(host);

			Assert.AreEqual(host.XamlRoot, flyout.XamlRoot);
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_SplitButton_Flyout_XamlRoot()
		{
			var flyout = new Flyout();
			var host = new Microsoft.UI.Xaml.Controls.SplitButton() { Content = "Asd" };
			host.Flyout = flyout;
			TestServices.WindowHelper.WindowContent = host;
			await TestServices.WindowHelper.WaitForIdle();
			await TestServices.WindowHelper.WaitForLoaded(host);

			Assert.AreEqual(host.XamlRoot, flyout.XamlRoot);
		}

		[TestMethod]
		public async Task When_Button_Flyout_XamlRoot()
		{
			var flyout = new Flyout();
			var host = new Button() { Content = "Asd" };
			host.Flyout = flyout;
			TestServices.WindowHelper.WindowContent = host;
			await TestServices.WindowHelper.WaitForIdle();
			await TestServices.WindowHelper.WaitForLoaded(host);

			Assert.AreEqual(host.XamlRoot, flyout.XamlRoot);
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_Flyout_Popup_XamlRoot()
		{
			var flyout = new Flyout();
			try
			{
				var host = new Button() { Content = "Asd" };
				flyout.Content = new Button() { Content = "Test" };

				TestServices.WindowHelper.WindowContent = host;
				await TestServices.WindowHelper.WaitForIdle();
				await TestServices.WindowHelper.WaitForLoaded(host);

				flyout.ShowAt(host);
				await TestServices.WindowHelper.WaitForIdle();
				await TestServices.WindowHelper.WaitForIdle();

				var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(host.XamlRoot);
				Assert.AreEqual(host.XamlRoot, flyout.XamlRoot);
				Assert.AreEqual(host.XamlRoot, popups[0].XamlRoot);
				Assert.AreEqual(host.XamlRoot, popups[0].Child.XamlRoot);
			}
			finally
			{
				flyout.Hide();
			}
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_AttachedFlyout_Popup_XamlRoot()
		{
			var flyout = new Flyout();
			try
			{
				var host = new Button() { Content = "Asd" };
				flyout.Content = new Button() { Content = "Test" };
				FlyoutBase.SetAttachedFlyout(host, flyout);

				TestServices.WindowHelper.WindowContent = host;
				await TestServices.WindowHelper.WaitForIdle();
				await TestServices.WindowHelper.WaitForLoaded(host);

				Assert.IsNull(flyout.XamlRoot);
				FlyoutBase.ShowAttachedFlyout(host);

				await TestServices.WindowHelper.WaitForIdle();
				await TestServices.WindowHelper.WaitForIdle();

				var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(host.XamlRoot);
				Assert.AreEqual(host.XamlRoot, flyout.XamlRoot);
				Assert.AreEqual(host.XamlRoot, popups[0].XamlRoot);
				Assert.AreEqual(host.XamlRoot, popups[0].Child.XamlRoot);
			}
			finally
			{
				flyout.Hide();
			}
		}

#if HAS_UNO
		[TestMethod]
		[RunsOnUIThread]
		[CombinatorialData]
		public async Task When_CloseLightDismissablePopups(bool isLightDismissEnabled)
		{
			var flyout = new Flyout()
			{
				Content = new Button() { Content = "Test" }
			};
			bool opened = false;
			flyout.Opened += (s, e) =>
			{
				var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(flyout.XamlRoot).FirstOrDefault(p => p.AssociatedFlyout == flyout);
				Assert.IsNotNull(popup);
				popup.IsLightDismissEnabled = isLightDismissEnabled;
				opened = true;
			};
			try
			{
				var ownerButton = new Button()
				{
					Content = "Owner",
					Flyout = flyout
				};
				TestServices.WindowHelper.WindowContent = ownerButton;
				await TestServices.WindowHelper.WaitForLoaded(ownerButton);
				((IInvokeProvider)ownerButton.GetAutomationPeer()).Invoke();
				await TestServices.WindowHelper.WaitFor(() => opened);
				var popupRoot = ownerButton.XamlRoot.VisualTree.PopupRoot;
				popupRoot.CloseLightDismissablePopups();
				await TestServices.WindowHelper.WaitForIdle();
				Assert.AreEqual(!isLightDismissEnabled, flyout.IsOpen);
			}
			finally
			{
				flyout?.Hide();
			}
		}
#endif

#if __APPLE_UIKIT__
		[TestMethod]
		[RequiresFullWindow]
		public async Task When_Native_DatePickerFlyout_Placement()
		{
			var flyout = new NativeDatePickerFlyout();
			try
			{
				var grid = new Grid();
				var host = new Button() { Content = "Asd", Margin = new Thickness(30, 0) };
				grid.Children.Add(host);
				flyout.Content = new Button() { Content = "Test" };
				FlyoutBase.SetAttachedFlyout(host, flyout);

				TestServices.WindowHelper.WindowContent = grid;
				await TestServices.WindowHelper.WaitForIdle();
				await TestServices.WindowHelper.WaitForLoaded(grid);

				FlyoutBase.ShowAttachedFlyout(host);

				await TestServices.WindowHelper.WaitForIdle();
				await TestServices.WindowHelper.WaitForIdle();

				var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(host.XamlRoot);
				var popupPanel = popups[0].PopupPanel;
				var child = popupPanel.Children[0];
				var transform = child.TransformToVisual(grid);
				var topLeft = transform.TransformPoint(default);
				Assert.AreEqual(0, topLeft.X); // Positioned on the left edge of the screen
				Assert.IsGreaterThan(100, topLeft.Y); // Positioned lower on the screen
			}
			finally
			{
				flyout.Hide();
			}
		}
#endif

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public Task When_Unbound_FullFlyout_Any() =>
			When_Unbound_FullFlyout_Impl(skiaFullScreen: false);

		[TestMethod]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaMobile)]
		[RunsOnUIThread]
		public Task When_Unbound_FullFlyout_SkiaMobile() =>
			// canvas for skia mobile is in absolute fullscreen, including area taken by status bar or bottom navigation bar.
			When_Unbound_FullFlyout_Impl(skiaFullScreen: true);

		private async Task When_Unbound_FullFlyout_Impl(bool skiaFullScreen)
		{
			var host = new Button
			{
				Content = "Asd",
				Flyout = new Flyout
				{
					Placement = FlyoutPlacementMode.Full,
					FlyoutPresenterStyle = new Style
					{
						TargetType = typeof(FlyoutPresenter),
						Setters =
						{
							// style reset
							new Setter(FlyoutPresenter.MarginProperty, new Thickness(0)),
							new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)),
							new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(0)),

							// remove limit from default style
							// Note that NaN isn't allowed to be set for MaxWidth and MaxHeight. However,
							// WinUI swallows failure HResults specifically when applying style setters.
							// So, Uno catches the exception as well.
							new Setter(FlyoutPresenter.MaxWidthProperty, double.NaN),
							new Setter(FlyoutPresenter.MaxHeightProperty, double.NaN),

							// full stretch
							new Setter(FlyoutPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch),
							new Setter(FlyoutPresenter.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
							new Setter(FlyoutPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch),
							new Setter(FlyoutPresenter.VerticalContentAlignmentProperty, VerticalAlignment.Stretch),
						}
					},
					Content = new Border
					{
						Background = new SolidColorBrush(Colors.SkyBlue),
						Child = new TextBlock { Text = "Asd" },
					},
				},
			};

			TestServices.WindowHelper.WindowContent = host;
			await TestServices.WindowHelper.WaitForIdle();
			await TestServices.WindowHelper.WaitForLoaded(host);

			try
			{
				host.Flyout.ShowAt(host);

				bool AnyPopupIsOpen() => VisualTreeHelper.GetOpenPopupsForXamlRoot(host.XamlRoot).Any();
				await TestServices.WindowHelper.WaitFor(AnyPopupIsOpen, message: "Timeout waiting on flyout to open");

				var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(host.XamlRoot).LastOrDefault();
				var presenter = popup.Child as FlyoutPresenter;
				await TestServices.WindowHelper.WaitForLoaded(presenter);

				var bounds = ApplicationView.GetForCurrentView().VisibleBounds;
				var expected = skiaFullScreen
					? host.XamlRoot.Size
					: new Size(bounds.Width, bounds.Height);

				Assert.IsTrue(
					presenter.ActualWidth >= bounds.Width && presenter.ActualHeight >= bounds.Height,
					$"flyout not taking the full size offered: flyout={presenter.ActualWidth}x{presenter.ActualHeight}, {(skiaFullScreen ? "XamlRoot.Bounds" : "VisibleBounds")}={expected.Width}x{expected.Height}");
			}
			finally
			{
				host.Flyout.Hide();
			}
		}

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_Open_In_GotFocus()
		{
			bool keepOpening = true;
			var flyout = new Flyout
			{
				Content = new Button { Content = "Test" }
			};
			try
			{
				bool closed = false;

				var container = new StackPanel();
				var host = new Button
				{
					Content = "Asd",
					Flyout = flyout
				};
				var unfocusButton = new Button();
				container.Children.Add(unfocusButton);
				container.Children.Add(host);

				TestServices.WindowHelper.WindowContent = container;
				await TestServices.WindowHelper.WaitForLoaded(container);
				await TestServices.WindowHelper.WaitForIdle();
				unfocusButton.Focus(FocusState.Programmatic);
				await TestServices.WindowHelper.WaitForIdle();
				bool gotFocus = false;
				bool wasClosedWhenHostGotFocus = false;
				host.GotFocus += (s, e) =>
				{
					gotFocus = true;
					if (closed)
					{
						wasClosedWhenHostGotFocus = true;
					}
					if (!host.Flyout.IsOpen && keepOpening)
					{
						host.Flyout.ShowAt(host);
					}
				};

				bool opened = false;
				flyout.Opened += (s, e) => opened = true;

				host.Focus(FocusState.Programmatic);

				await TestServices.WindowHelper.WaitFor(() => opened);
				Assert.AreNotEqual(host, FocusManager.GetFocusedElement(TestServices.WindowHelper.XamlRoot));

				opened = false;
				flyout.Closed += (s, e) => closed = true;

				gotFocus = false;
				flyout.Hide();

				await TestServices.WindowHelper.WaitFor(() => gotFocus);
				await TestServices.WindowHelper.WaitFor(() => closed);
				Assert.IsFalse(wasClosedWhenHostGotFocus);
				Assert.IsFalse(flyout.IsOpen);
			}
			finally
			{
				keepOpening = false;
				flyout.Hide();
			}
		}

		private static void VerifyRelativeContentPosition(HorizontalPosition horizontalPosition, VerticalPosition verticalPosition, FrameworkElement content, double minimumTargetOffset, FrameworkElement target)
		{
			var contentScreenBounds = content.GetOnScreenBounds();
			var contentCenter = contentScreenBounds.GetCenter();
			var targetScreenBounds = target.GetOnScreenBounds();
			var targetCenter = targetScreenBounds.GetCenter();

			Assert.IsGreaterThan(minimumTargetOffset, targetCenter.X);
			Assert.IsGreaterThan(minimumTargetOffset, targetCenter.Y);
			switch (horizontalPosition)
			{
				case HorizontalPosition.BeyondLeft:
					NumberAssert.LessOrEqual(contentScreenBounds.Right, targetScreenBounds.Left);
					break;
				case HorizontalPosition.LeftFlush:
					Assert.AreEqual(targetScreenBounds.Left, contentScreenBounds.Left, delta: 2);
					break;
				case HorizontalPosition.Center:
					Assert.AreEqual(targetCenter.X, contentCenter.X, delta: 2);
					break;
				case HorizontalPosition.RightFlush:
					Assert.AreEqual(targetScreenBounds.Right, contentScreenBounds.Right, delta: 2);
					break;
				case HorizontalPosition.BeyondRight:
					NumberAssert.GreaterOrEqual(contentScreenBounds.Left, targetScreenBounds.Right);
					break;
			}

			switch (verticalPosition)
			{
				case VerticalPosition.BeyondTop:
					NumberAssert.LessOrEqual(contentScreenBounds.Bottom, targetScreenBounds.Top);
					break;
				case VerticalPosition.TopFlush:
					Assert.AreEqual(targetScreenBounds.Top, contentScreenBounds.Top, delta: 3);
					break;
				case VerticalPosition.Center:
					Assert.AreEqual(targetCenter.Y, contentCenter.Y, delta: 2);
					break;
				case VerticalPosition.BottomFlush:
					Assert.AreEqual(targetScreenBounds.Bottom, contentScreenBounds.Bottom, delta: 3);
					break;
				case VerticalPosition.BeyondBottom:
					NumberAssert.GreaterOrEqual(contentScreenBounds.Top, targetScreenBounds.Bottom);
					break;
			}
		}

		private static void VerifyRelativeContentPosition(Windows.Foundation.Point position, HorizontalPosition horizontalPosition, VerticalPosition verticalPosition, FrameworkElement content, double minimumTargetOffset, FrameworkElement target)
		{
			var contentScreenBounds = content.GetOnScreenBounds();
#if __ANDROID__
			if (FeatureConfiguration.Popup.UseNativePopup)
			{
				// Adjust for status bar height, which is omitted from TransformToVisual() for elements inside of a native popup.
				var rootViewBounds = ((FrameworkElement)Window.Current.Content).GetOnScreenBounds();
				contentScreenBounds.Y += rootViewBounds.Y;
			}
#endif
			var contentCenter = contentScreenBounds.GetCenter();
			var targetScreenBounds = target.GetOnScreenBounds();
			var targetCenter = targetScreenBounds.GetCenter();
			var anchorPoint = new Windows.Foundation.Point(targetScreenBounds.X + position.X, targetScreenBounds.Y + position.Y);

			Assert.IsGreaterThan(minimumTargetOffset, targetCenter.X);
			Assert.IsGreaterThan(minimumTargetOffset, targetCenter.Y);
			switch (horizontalPosition)
			{
				case HorizontalPosition.BeyondLeft:
					NumberAssert.GreaterOrEqual(anchorPoint.X, contentScreenBounds.Right);
					break;
				case HorizontalPosition.LeftFlush:
					Assert.AreEqual(anchorPoint.X, contentScreenBounds.Left, delta: 2);
					break;
				case HorizontalPosition.Center:
					Assert.AreEqual(anchorPoint.X, contentCenter.X, delta: 2);
					break;
				case HorizontalPosition.RightFlush:
					Assert.AreEqual(anchorPoint.X, contentScreenBounds.Right, delta: 2);
					break;
				case HorizontalPosition.BeyondRight:
					NumberAssert.LessOrEqual(anchorPoint.X, contentScreenBounds.Left);
					break;
			}

			switch (verticalPosition)
			{
				case VerticalPosition.BeyondTop:
					NumberAssert.GreaterOrEqual(anchorPoint.Y, contentScreenBounds.Bottom);
					break;
				case VerticalPosition.TopFlush:
					Assert.AreEqual(anchorPoint.Y, contentScreenBounds.Top, delta: 2);
					break;
				case VerticalPosition.Center:
					Assert.AreEqual(anchorPoint.Y, contentCenter.Y, delta: 2);
					break;
				case VerticalPosition.BottomFlush:
					Assert.AreEqual(anchorPoint.Y, contentScreenBounds.Bottom, delta: 2);
					break;
				case VerticalPosition.BeyondBottom:
					NumberAssert.LessOrEqual(anchorPoint.Y, contentScreenBounds.Top);
					break;
			}
		}

		private (Flyout Flyout, FrameworkElement Content) CreateFlyout()
		{
			var content = new Grid { Height = 64, Width = 64, Background = new SolidColorBrush(Colors.Green) };
			var flyout = new Flyout
			{
				Content = content,
				FlyoutPresenterStyle = GetSimpleFlyoutPresenterStyle()
			};
			return (flyout, content);
		}

		private (Flyout Flyout, FrameworkElement Content) CreateFlyoutWithBinding()
		{

			var flyout = new Flyout
			{
				FlyoutPresenterStyle = GetSimpleFlyoutPresenterStyle()
			};

			var content = new StackPanel
			{
				Children =
				{
					new TextBlock().Apply(x => x.SetBinding(TextBlock.TextProperty, new Binding())),
					new Button
					{
						Content = "Button B",
					}.Apply(x => x.SetBinding(Button.CommandParameterProperty, new Binding() { Source = flyout }))
				}
			};

			flyout.Content = content;
			return (flyout, content);
		}

		private (Flyout Flyout, FrameworkElement Content) CreateFlyoutWithBindingMultipleChildren()
		{
			var flyout = new Flyout
			{
				FlyoutPresenterStyle = GetSimpleFlyoutPresenterStyle()
			};

			var content = new StackPanel
			{
				Children =
				{
					new TextBlock().Apply(x => x.SetBinding(TextBlock.TextProperty, new Binding())),
					new Button
					{
						Content = "Button A",
					}.Apply(x => x.SetBinding(Button.CommandParameterProperty, new Binding() { Source = flyout })),
					new Button
					{
						Content = "Button B",
					}.Apply(x => x.SetBinding(Button.CommandParameterProperty, new Binding() { Source = flyout })),
					new Button
					{
						Content = "Button C",
					}.Apply(x => x.SetBinding(Button.CommandParameterProperty, new Binding() { Source = flyout })),
					new Button
					{
						Content = "Button D",
					}.Apply(x => x.SetBinding(Button.CommandParameterProperty, new Binding() { Source = flyout }))
				}
			};

			flyout.Content = content;
			return (flyout, content);
		}

		private static Style GetSimpleFlyoutPresenterStyle() => new Style
		{
			TargetType = typeof(FlyoutPresenter),
			Setters =
					{
						new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)),
						new Setter(FlyoutPresenter.MinWidthProperty, 0d),
						new Setter(FlyoutPresenter.MinHeightProperty, 0d),
						new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(0)),
					}
		};

		private MyMenuFlyout CreateBasicMenuFlyout()
		{
			var flyout = new MyMenuFlyout
			{
				Items =
				{
					new MenuFlyoutItem { Text = "Red" },
					new MenuFlyoutItem { Text = "Blue" },
					new MenuFlyoutItem { Text = "Green" },
				}
			};

			return flyout;
		}

		public enum HorizontalPosition
		{
			BeyondLeft,
			LeftFlush,
			Center,
			RightFlush,
			BeyondRight
		}

		public enum VerticalPosition
		{
			BeyondTop,
			TopFlush,
			Center,
			BottomFlush,
			BeyondBottom
		}


#if HAS_UNO
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_Shared_ContextFlyout_Opened_In_Second_Window()
		{
			if (!Uno.UI.Xaml.Controls.NativeWindowFactory.SupportsMultipleWindows)
			{
				Assert.Inconclusive("This test can only run in an environment with multiwindow support");
			}

			// Use a single shared flyout instance (simulates the singleton
			// TextControlCommandBarContextFlyout resource in Generic.xaml).
			var sharedFlyout = new Flyout
			{
				Content = new TextBlock { Text = "Shared" }
			};

			// --- Window 1: open and close the flyout ---
			var button1 = new Button { Content = "W1" };
			button1.ContextFlyout = sharedFlyout;
			TestServices.WindowHelper.WindowContent = button1;
			await TestServices.WindowHelper.WaitForLoaded(button1);

			try
			{
				sharedFlyout.ShowAt(button1);
				await TestServices.WindowHelper.WaitForIdle();
				Assert.IsTrue(sharedFlyout.IsOpen, "Flyout should be open in window 1");
			}
			finally
			{
				sharedFlyout.Hide();
				await TestServices.WindowHelper.WaitForIdle();
			}

			// --- Window 2: open the same flyout on a different target ---
			var window2 = new Window();
			try
			{
				var button2 = new Button { Content = "W2" };
				button2.ContextFlyout = sharedFlyout;
				window2.Content = button2;

				bool activated = false;
				window2.Activated += (s, e) => activated = true;
				window2.Activate();
				await TestServices.WindowHelper.WaitFor(() => activated);
				await TestServices.WindowHelper.WaitForLoaded(button2);

				// This must not throw InvalidOperationException("Cannot change XamlRoot for existing element")
				sharedFlyout.ShowAt(button2);
				await TestServices.WindowHelper.WaitForIdle();

				Assert.IsTrue(sharedFlyout.IsOpen, "Flyout should be open in window 2");
			}
			finally
			{
				sharedFlyout.Hide();
				window2.Close();
			}
		}
#endif

		// {ThemeResource} inheritance for popup-hosted (Flyout) content — kahua #475. Flyout content
		// reparented into the PopupRoot must still inherit the placement target's theme. Nested boundaries
		// (OUTER Dark holding an INNER Light button) make the flyout ActualTheme Light, so its theme-keyed
		// {ThemeResource} must resolve Light (Green); Uno regressed to the outer Dark (Red). Runs identically
		// on Skia Desktop and native WinUI.
		private const string ThemeResourceInheritanceRootXaml =
			"""
			<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
					xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
					RequestedTheme="Dark">
				<Border.Resources>
					<ResourceDictionary>
						<ResourceDictionary.ThemeDictionaries>
							<ResourceDictionary x:Key="Light">
								<SolidColorBrush x:Key="FlyoutContentBrush" Color="Green" />
							</ResourceDictionary>
							<ResourceDictionary x:Key="Dark">
								<SolidColorBrush x:Key="FlyoutContentBrush" Color="Red" />
							</ResourceDictionary>
							<ResourceDictionary x:Key="Default">
								<SolidColorBrush x:Key="FlyoutContentBrush" Color="Red" />
							</ResourceDictionary>
						</ResourceDictionary.ThemeDictionaries>
					</ResourceDictionary>
				</Border.Resources>
				<Border RequestedTheme="Light">
					<Button x:Name="btn" Content="Open">
						<Button.Flyout>
							<Flyout>
								<Border x:Name="flyoutContent"
										Width="50"
										Height="50"
										Background="{ThemeResource FlyoutContentBrush}" />
							</Flyout>
						</Button.Flyout>
					</Button>
				</Border>
			</Border>
			""";

		/// <summary>
		/// A flyout opened from a button inside an inner Light theme boundary (nested in an
		/// outer Dark boundary). The flyout content inherits Light (verified), so its
		/// theme-keyed {ThemeResource} background must resolve to the Light value (Green). On
		/// Uno it resolves against the outer/ambient Dark theme (Red).
		/// </summary>
		[TestMethod]
		[RequiresFullWindow]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.Native)] // Owner-subtree theme override is Skia-only; native UI targets honor OS/app theme only
		[GitHubWorkItem("https://github.com/unoplatform/kahua-private/issues/475")]
		public async Task When_Flyout_Opened_From_Inner_Light_Boundary_Resolves_Light_ThemeResource()
		{
#if HAS_UNO
			// Pin the ambient OS theme to Dark so the leak reproduces deterministically regardless of the
			// developer's OS theme (the application follows the system theme since it is not explicitly themed).
			// On WinUI this override is compiled out; the explicit outer-Dark / inner-Light element boundary
			// makes the flyout content Light there regardless of the OS theme.
			using var _ = ThemeHelper.UseSystemThemeOverride(ApplicationTheme.Dark);
			await TestServices.WindowHelper.WaitForIdle();
#endif

			var root = (Border)XamlReader.Load(ThemeResourceInheritanceRootXaml);

			TestServices.WindowHelper.WindowContent = root;
			await TestServices.WindowHelper.WaitForLoaded(root);
			await TestServices.WindowHelper.WaitForIdle();

			var button = (Button)root.FindName("btn");
			var flyout = (Flyout)button.Flyout;
			var flyoutContent = (Border)flyout.Content;

			try
			{
				flyout.ShowAt(button);
				await TestServices.WindowHelper.WaitForIdle();
				await TestServices.WindowHelper.WaitForIdle();

				// The flyout content inherits the inner Light boundary's theme.
				Assert.AreEqual(ElementTheme.Light, flyoutContent.ActualTheme,
					"Flyout content should inherit Light from the inner theme boundary that hosts the button.");

				var brush = flyoutContent.Background as SolidColorBrush;
				Assert.IsNotNull(brush, "Flyout content background should be a SolidColorBrush.");

				// WinUI: resolves {ThemeResource} against the content's inherited Light theme
				// (Green). Uno regression: resolves against the outer/ambient Dark theme (Red).
				Assert.AreEqual(Colors.Green, brush.Color,
					$"Flyout content has ActualTheme=Light, so its {{ThemeResource}} must " +
					$"resolve to the Light value (Green), but it is {brush.Color}. If Red, the " +
					$"{{ThemeResource}} was resolved against the outer/application (Dark) theme " +
					$"instead of the element's own inherited ActualTheme.");
			}
			finally
			{
				flyout.Hide();
				await TestServices.WindowHelper.WaitForIdle();
			}
		}

#if HAS_UNO
		/// <summary>
		/// Application/OS theme is Dark, a content-root subtree pins RequestedTheme=Light, and a
		/// flyout is opened from a button inside it. The flyout content inherits Light, so its
		/// theme-keyed {ThemeResource} must resolve to the Light value (Green), not the
		/// application/OS Dark value (Red).
		/// </summary>
		[TestMethod]
		[RequiresFullWindow]
		[GitHubWorkItem("https://github.com/unoplatform/kahua-private/issues/475")]
		public async Task When_Flyout_Opened_From_Light_Subtree_Under_Dark_App_Resolves_Light_ThemeResource()
		{
			using var darkApp = ThemeHelper.UseApplicationDarkTheme();
			await TestServices.WindowHelper.WaitForIdle();

			var root = (Border)XamlReader.Load(
				"""
				<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
						xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
						RequestedTheme="Light">
					<Border.Resources>
						<ResourceDictionary>
							<ResourceDictionary.ThemeDictionaries>
								<ResourceDictionary x:Key="Light">
									<SolidColorBrush x:Key="FlyoutContentBrush" Color="Green" />
								</ResourceDictionary>
								<ResourceDictionary x:Key="Dark">
									<SolidColorBrush x:Key="FlyoutContentBrush" Color="Red" />
								</ResourceDictionary>
								<ResourceDictionary x:Key="Default">
									<SolidColorBrush x:Key="FlyoutContentBrush" Color="Red" />
								</ResourceDictionary>
							</ResourceDictionary.ThemeDictionaries>
						</ResourceDictionary>
					</Border.Resources>
					<Button x:Name="btn" Content="Open">
						<Button.Flyout>
							<Flyout>
								<Border x:Name="flyoutContent"
										Width="50"
										Height="50"
										Background="{ThemeResource FlyoutContentBrush}" />
							</Flyout>
						</Button.Flyout>
					</Button>
				</Border>
				""");

			TestServices.WindowHelper.WindowContent = root;
			await TestServices.WindowHelper.WaitForLoaded(root);
			await TestServices.WindowHelper.WaitForIdle();

			var button = (Button)root.FindName("btn");
			var flyout = (Flyout)button.Flyout;
			var flyoutContent = (Border)flyout.Content;

			try
			{
				flyout.ShowAt(button);
				await TestServices.WindowHelper.WaitForIdle();
				await TestServices.WindowHelper.WaitForIdle();

				Assert.AreEqual(ElementTheme.Light, flyoutContent.ActualTheme,
					"Flyout content should inherit Light from the host even though the app is Dark.");

				var brush = flyoutContent.Background as SolidColorBrush;
				Assert.IsNotNull(brush, "Flyout content background should be a SolidColorBrush.");

				Assert.AreEqual(Colors.Green, brush.Color,
					$"Flyout content has ActualTheme=Light, so its {{ThemeResource}} must " +
					$"resolve to the Light value (Green), but it is {brush.Color}. If Red, the " +
					$"{{ThemeResource}} was resolved against the application/OS Dark theme " +
					$"instead of the element's own inherited ActualTheme.");
			}
			finally
			{
				flyout.Hide();
				await TestServices.WindowHelper.WaitForIdle();
			}
		}
#endif
	}

	public partial class MeasureCountingStackPanel : StackPanel
	{
		public int MeasureCount { get; set; }

		protected override Size MeasureOverride(Size availableSize)
		{
			MeasureCount++;
			return base.MeasureOverride(availableSize);
		}
	}

	public partial class MyMenuFlyout : MenuFlyout
	{
		public MenuFlyoutPresenter Presenter { get; private set; }

		protected override Control CreatePresenter()
		{
			var presenter = base.CreatePresenter();
			Presenter = presenter as MenuFlyoutPresenter;
			return presenter;
		}
	}
}
