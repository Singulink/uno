using System;
using System.Transactions;
using Foundation;
using Microsoft.UI.Xaml;
using ObjCRuntime;
using UIKit;
using Uno.Foundation.Logging;
using Uno.UI.Xaml.Controls;
using Windows.UI.Core;

namespace Uno.UI.Runtime.Skia.AppleUIKit;

[Register("UnoUIApplicationDelegate")]
public partial class UnoUIApplicationDelegate : UIApplicationDelegate
{
	public UnoUIApplicationDelegate()
	{
		SubscribeBackgroundNotifications();
	}

	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
		this.LogDebug()?.LogDebug($"Application finished launching");
		Application.Start(AppleUIKitHost.CreateAppAction);

		return true;
	}

	/// <summary>
	/// Supplies the scene configuration, which adopts the scene-based life cycle.
	/// </summary>
	/// <remarks>
	/// Apps built with the iOS 27 SDK must adopt that life cycle to launch at all. Implementing this method
	/// adopts it without a scene manifest in the app's Info.plist. The configuration UIKit resolved for the
	/// session is kept, including what a manifest declares; application scenes without a delegate get
	/// <see cref="UnoUISceneDelegate"/>, which shows the window in them.
	/// </remarks>
	public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options)
	{
		try
		{
			var configuration = connectingSceneSession.Configuration
				?? new UISceneConfiguration("Default Configuration", connectingSceneSession.Role);

			// A scene with another role gets no delegate, so no window is put in it: on iOS 26 and earlier an
			// external display then keeps mirroring the app. iOS 27 only connects one for a scene accessory.
			if (connectingSceneSession.Role == UIWindowSceneSessionRole.Application && configuration.DelegateClass is null)
			{
				configuration.DelegateClass = new Class(typeof(UnoUISceneDelegate));
			}

			return configuration;
		}
		catch (Exception ex)
		{
			// Reading the role throws for one these bindings don't know, and a managed exception must never
			// escape into a UIKit callback. Such a scene keeps UIKit's configuration, with no window in it.
			if (this.Log().IsEnabled(LogLevel.Warning))
			{
				this.Log().Warn($"Could not configure the scene of session {connectingSceneSession.PersistentIdentifier}.", ex);
			}

			return connectingSceneSession.Configuration;
		}
	}

	private void SubscribeBackgroundNotifications()
	{
		if (UIDevice.CurrentDevice.CheckSystemVersion(13, 0))
		{
			NSNotificationCenter.DefaultCenter.AddObserver(UIScene.DidEnterBackgroundNotification, OnEnteredBackground);
			NSNotificationCenter.DefaultCenter.AddObserver(UIScene.WillEnterForegroundNotification, OnLeavingBackground);
			NSNotificationCenter.DefaultCenter.AddObserver(UIScene.DidActivateNotification, OnActivated);
			NSNotificationCenter.DefaultCenter.AddObserver(UIScene.WillDeactivateNotification, OnDeactivated);
		}
		else
		{
			NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.DidEnterBackgroundNotification, OnEnteredBackground);
			NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillEnterForegroundNotification, OnLeavingBackground);
			NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.DidBecomeActiveNotification, OnActivated);
			NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillResignActiveNotification, OnDeactivated);
		}
	}

	// Under the scene life cycle an external display on iOS 26 and earlier is a scene of its own, whose
	// notifications must not change the state of the app's window.
	private static bool IsFromWindowScene(NSNotification notification) =>
		notification.Object is not UIScene scene || NativeWindowWrapper.IsWindowScene(scene);

	private void OnEnteredBackground(NSNotification notification)
	{
		if (!IsFromWindowScene(notification))
		{
			return;
		}

		NativeWindowWrapper.Instance?.OnNativeVisibilityChanged(false);

		Application.Current?.RaiseEnteredBackground(() => Application.Current.RaiseSuspending());
	}

	private void OnLeavingBackground(NSNotification notification)
	{
		if (!IsFromWindowScene(notification))
		{
			return;
		}

		this.LogDebug()?.LogDebug($"Application leaving background");
		Application.Current?.RaiseResuming();
		Application.Current?.RaiseLeavingBackground(() => NativeWindowWrapper.Instance?.OnNativeVisibilityChanged(true));
	}

	private void OnActivated(NSNotification notification)
	{
		if (!IsFromWindowScene(notification))
		{
			return;
		}

		this.LogDebug()?.LogDebug($"Application activated");
		NativeWindowWrapper.Instance?.OnNativeActivated(CoreWindowActivationState.CodeActivated);
	}

	private void OnDeactivated(NSNotification notification)
	{
		if (!IsFromWindowScene(notification))
		{
			return;
		}

		NativeWindowWrapper.Instance?.OnNativeActivated(CoreWindowActivationState.Deactivated);
	}
}
