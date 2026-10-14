using System;
using Foundation;
using Microsoft.UI.Xaml;
using UIKit;
using Uno.UI.Xaml.Controls;

namespace Uno.UI.Runtime.Skia.AppleUIKit;

/// <summary>
/// Hosts the app's window in the application scene UIKit connects.
/// </summary>
/// <remarks>
/// Apps built with the iOS 27 SDK must adopt the scene-based life cycle, or UIKit refuses to launch them.
/// <see cref="UnoUIApplicationDelegate"/> hands this delegate out for application scenes, which adopts that
/// life cycle without a scene manifest in the app's Info.plist.
/// </remarks>
[Register("UnoUISceneDelegate")]
internal sealed class UnoUISceneDelegate : UISceneDelegate
{
	private UIWindowScene? _scene;

	/// <summary>
	/// Gets the window shown in this delegate's scene, as the <c>UIWindowSceneDelegate</c> protocol defines it.
	/// </summary>
	[Export("window")]
	public UIWindow? Window =>
		_scene is not null && NativeWindowWrapper.Instance?.NativeWindow is { } window && _scene.Equals(window.WindowScene)
			? window
			: null;

	[Export("scene:willConnectToSession:options:")]
	public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
	{
		try
		{
			if (scene is UIWindowScene windowScene)
			{
				_scene = windowScene;
				NativeWindowWrapper.OnSceneConnected(windowScene);
			}
		}
		catch (Exception ex)
		{
			// A managed exception must never escape into a UIKit callback.
			Application.Current?.RaiseRecoverableUnhandledException(ex);
		}
	}

	[Export("sceneDidDisconnect:")]
	public override void DidDisconnect(UIScene scene)
	{
		try
		{
			NativeWindowWrapper.OnSceneDisconnected(scene);
			_scene = null;
		}
		catch (Exception ex)
		{
			// A managed exception must never escape into a UIKit callback.
			Application.Current?.RaiseRecoverableUnhandledException(ex);
		}
	}
}
