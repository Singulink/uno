#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PointerEventArgs = global::Windows.UI.Core.PointerEventArgs;
using System.Reflection;
using System.Runtime.CompilerServices;
using Private.Infrastructure;
using Uno.UI.RuntimeTests.Helpers;
using Windows.Foundation;
using Windows.UI.Core;
#if HAS_UNO
using Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation;
#endif

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Input;

[TestClass]
[RunsOnUIThread]
public class Given_BrowserPointerInputSource
{
	// Values mirror the private HtmlPointerEvent / HtmlPointerButtonsState / HtmlPointerButtonUpdate
	// enums in BrowserPointerInputSource, which themselves match the DOM PointerEvent contract.
	private const int PointerDown = 1 << 2;
	private const int PointerUp = 1 << 3;
	private const int Wheel = 1 << 7;
	private const int ButtonsX1 = 8; // back button held
	private const int ButtonsX2 = 16; // forward button held
	private const int UpdateX1 = 3; // back button updated
	private const int UpdateX2 = 4; // forward button updated

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/23509")]
	[DataRow(PointerDown, ButtonsX2, UpdateX2, PointerUpdateKind.XButton2Pressed, DisplayName = "Forward button down => XButton2Pressed")]
	[DataRow(PointerUp, 0, UpdateX2, PointerUpdateKind.XButton2Released, DisplayName = "Forward button up => XButton2Released")]
	[DataRow(PointerDown, ButtonsX1, UpdateX1, PointerUpdateKind.XButton1Pressed, DisplayName = "Back button down => XButton1Pressed")]
	[DataRow(PointerUp, 0, UpdateX1, PointerUpdateKind.XButton1Released, DisplayName = "Back button up => XButton1Released")]
	public void When_XButton_Then_Reports_Matching_UpdateKind(int @event, int buttons, int buttonUpdate, PointerUpdateKind expected)
	{
		var actual = RaiseNativePointerEvent((byte)@event, buttons, buttonUpdate);

		Assert.AreEqual(expected, actual);
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/24703")]
	public void When_WheelDeltaIsFractional_Then_AccumulatesAcrossEvents()
	{
		// Four consecutive trackpad ticks of 0.4 CSS px each (deltaMode=PIXEL). Each event reports the
		// raw fractional delta as-is (unlike Win32/X11, where the OS already accumulates fractional
		// notches into an int before delivering the message), so the source must carry the remainder
		// across events instead of truncating every event to 0.
		var deltas = RaiseWheelEvents(wheelDeltaX: 0, wheelDeltaY: -0.4, count: 4);

		// 0.4+0.4+0.4+0.4 = 1.6px total: the third tick is the first to cross a whole unit (1), and the
		// leftover 0.6 remainder isn't enough to cross another one, so only one event carries a delta.
		CollectionAssert.AreEqual(new[] { 1 }, deltas);
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/24703")]
	public void When_HorizontalWheelDeltaIsFractional_Then_AccumulatesAcrossEvents()
	{
		var deltas = RaiseWheelEvents(wheelDeltaX: 0.4, wheelDeltaY: 0, count: 4);

		CollectionAssert.AreEqual(new[] { 1 }, deltas);
	}

	private static List<int> RaiseWheelEvents(double wheelDeltaX, double wheelDeltaY, int count)
	{
		var type = ResolveBrowserPointerInputSourceType();
		var source = CreateUninitializedSource(type);

		var deltas = new List<int>();
		TypedEventHandler<object, PointerEventArgs> handler = (_, e) => deltas.Add(e.CurrentPoint.Properties.MouseWheelDelta);

		var wheelChanged = type.GetEvent("PointerWheelChanged")!;
		wheelChanged.AddEventHandler(source, handler);

		try
		{
			var onNativeEvent = type.GetMethod("OnNativeEvent", BindingFlags.NonPublic | BindingFlags.Static)!;
			for (var i = 0; i < count; i++)
			{
				onNativeEvent.Invoke(null, new object[]
				{
					source,
					(byte)Wheel,
					/* timestamp */ 0d,
					/* deviceType */ (int)Windows.Devices.Input.PointerDeviceType.Mouse,
					/* pointerId */ 1d,
					/* x */ 0d,
					/* y */ 0d,
					/* ctrl */ false,
					/* shift */ false,
					/* buttons */ 0,
					/* buttonUpdate */ 0,
					/* pressure */ 0.5d,
					/* wheelDeltaX */ 0d,
					wheelDeltaY,
					/* hasRelatedTarget */ false,
				});
			}
		}
		finally
		{
			wheelChanged.RemoveEventHandler(source, handler);
		}

		return deltas;
	}

	private static Type ResolveBrowserPointerInputSourceType()
	{
		var type =
			Type.GetType("Uno.UI.Runtime.BrowserPointerInputSource, Uno.UI")
			?? Type.GetType("Uno.UI.Runtime.BrowserPointerInputSource, Uno.UI.Runtime.WebAssembly.Browser");

		Assert.IsNotNull(type, "BrowserPointerInputSource type was not found in the loaded WASM assemblies.");

		return type!;
	}

	// Bypass the constructor: it calls into JS to register the source, which we don't want from a test.
	private static object CreateUninitializedSource(Type type) => RuntimeHelpers.GetUninitializedObject(type);

	private static PointerUpdateKind RaiseNativePointerEvent(byte @event, int buttons, int buttonUpdate)
	{
		var type = ResolveBrowserPointerInputSourceType();
		var source = CreateUninitializedSource(type);

		PointerEventArgs? captured = null;
		TypedEventHandler<object, PointerEventArgs> handler = (_, e) => captured = e;

		var pressed = type.GetEvent("PointerPressed")!;
		var released = type.GetEvent("PointerReleased")!;
		pressed.AddEventHandler(source, handler);
		released.AddEventHandler(source, handler);

		try
		{
			var onNativeEvent = type.GetMethod("OnNativeEvent", BindingFlags.NonPublic | BindingFlags.Static)!;
			onNativeEvent.Invoke(null, new object[]
			{
				source,
				@event,
				/* timestamp */ 0d,
				/* deviceType */ (int)Windows.Devices.Input.PointerDeviceType.Mouse,
				/* pointerId */ 1d,
				/* x */ 0d,
				/* y */ 0d,
				/* ctrl */ false,
				/* shift */ false,
				buttons,
				buttonUpdate,
				/* pressure */ 0.5d,
				/* wheelDeltaX */ 0d,
				/* wheelDeltaY */ 0d,
				/* hasRelatedTarget */ false,
			});
		}
		finally
		{
			pressed.RemoveEventHandler(source, handler);
			released.RemoveEventHandler(source, handler);
		}

		Assert.IsNotNull(captured, "BrowserPointerInputSource did not raise a pointer event.");

		return (PointerUpdateKind)captured!.CurrentPoint.Properties.PointerUpdateKind;
	}

#if HAS_UNO
	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Thumb_Released_Outside_Page_Then_Drag_Completes()
	{
		var thumb = new Thumb
		{
			Width = 40,
			Height = 40,
			Margin = new Thickness(100),
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
		};
		var verticalChange = 0d;
		var completed = false;
		thumb.DragDelta += (_, e) => verticalChange += e.VerticalChange;
		thumb.DragCompleted += (_, _) => completed = true;

		await UITestHelper.Load(new Grid { Children = { thumb } });
		var (x, y) = (thumb.GetAbsoluteCenter().X, thumb.GetAbsoluteCenter().Y);
		const double outsideY = -40;

		try
		{
			// The sequence Chrome sends when the pointer leaves the page with the button held and is released outside:
			// everything outside the page is targeted at the <html> element.
			DispatchDomMouseEvent("pointerdown", "canvas", x, y, buttons: 1, button: 0);
			DispatchDomMouseEvent("pointermove", "canvas", x, y + 10, buttons: 1);
			DispatchDomMouseEvent("pointerout", "canvas", x, outsideY, buttons: 1, relatedTarget: "html");
			DispatchDomMouseEvent("pointerover", "html", x, outsideY, buttons: 1, relatedTarget: "canvas");
			DispatchDomMouseEvent("pointermove", "html", x, outsideY, buttons: 1);
			var changeOutside = verticalChange;
			DispatchDomMouseEvent("pointerup", "html", x, outsideY, buttons: 0, button: 0);
			var changeAtRelease = verticalChange;
			DispatchDomMouseEvent("pointerout", "html", x, y + 60, buttons: 0, relatedTarget: "canvas");
			DispatchDomMouseEvent("pointerover", "canvas", x, y + 60, buttons: 0, relatedTarget: "html");
			DispatchDomMouseEvent("pointermove", "canvas", x, y + 60, buttons: 0);

			Assert.AreEqual(changeAtRelease, verticalChange, "The thumb kept dragging after the button was released outside of the page.");
			Assert.IsTrue(completed, "The release outside of the page did not complete the drag.");
			Assert.AreEqual(outsideY - y, changeOutside, 1, "The thumb did not follow the pointer out of the page.");
		}
		finally
		{
			if (thumb.IsDragging)
			{
				DispatchDomMouseEvent("pointerup", "canvas", x, y, buttons: 0, button: 0);
			}
		}
	}

	private static void DispatchDomMouseEvent(string type, string target, double x, double y, int buttons, int button = -1, string? relatedTarget = null)
	{
		static string Element(string? name) => name switch
		{
			"canvas" => "document.getElementById('uno-canvas')",
			"html" => "document.documentElement",
			_ => "null",
		};

		WasmSemanticDomHelper.InvokeBrowserJs(string.Create(
			CultureInfo.InvariantCulture,
			$"(function(){{ {Element(target)}.dispatchEvent(new PointerEvent('{type}', {{ bubbles: true, cancelable: true, composed: true, pointerId: 1, pointerType: 'mouse', isPrimary: true, clientX: {x}, clientY: {y}, buttons: {buttons}, button: {button}, relatedTarget: {Element(relatedTarget)} }})); return 'ok'; }})()"));
	}
#endif
}
