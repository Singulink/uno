using System;
using System.Diagnostics.CodeAnalysis;
using Foundation;
using Microsoft.UI.Xaml;
using UIKit;
using Uno.Foundation.Logging;
using Windows.ApplicationModel.Activation;

namespace Uno.UI.Runtime.Skia.AppleUIKit;

/// <summary>
/// Raises <see cref="Application.OnActivated"/> with a <see cref="ProtocolActivatedEventArgs"/> for the URLs
/// UIKit opens the app with: universal links, custom URL schemes and documents opened in the app.
/// </summary>
/// <remarks>
/// Under the scene life cycle UIKit delivers them to the scene delegate. A URL the app is started with
/// comes in the connection options of the first scene, which connects after <c>OnLaunched</c> has
/// returned: an <c>async</c> <c>OnLaunched</c> may still be awaiting, so the app has to keep a URL it
/// can't navigate to yet. Later URLs come through <c>scene:continueUserActivity:</c> and
/// <c>scene:openURLContexts:</c>.
/// </remarks>
internal static class AppleUIKitActivation
{
	private static bool _firstSceneConnected;

	/// <summary>
	/// Reports the URLs in the connection options of a scene.
	/// </summary>
	/// <remarks>
	/// Those of the first scene that connects are reported as <see cref="ApplicationExecutionState.NotRunning"/>,
	/// as that is the scene a cold start opens. After a launch in the background, the first scene may connect
	/// later, while the app is already running.
	/// </remarks>
	internal static void ReportConnection(UISceneConnectionOptions options)
	{
		var previousExecutionState = _firstSceneConnected ? ApplicationExecutionState.Running : ApplicationExecutionState.NotRunning;
		_firstSceneConnected = true;

		if (options.UserActivities is { } userActivities)
		{
			foreach (var userActivity in userActivities)
			{
				TryReportUserActivity(userActivity, previousExecutionState);
			}
		}

		if (options.UrlContexts is { } urlContexts)
		{
			foreach (var urlContext in urlContexts)
			{
				TryReportUrl(urlContext.Url, previousExecutionState);
			}
		}
	}

	/// <summary>
	/// Reports a universal link, which UIKit delivers as a browsing-web user activity.
	/// </summary>
	internal static bool TryReportUserActivity(NSUserActivity? userActivity, ApplicationExecutionState previousExecutionState)
	{
		if (userActivity?.ActivityType != NSUserActivityType.BrowsingWeb)
		{
			if (typeof(AppleUIKitActivation).Log().IsEnabled(LogLevel.Debug))
			{
				typeof(AppleUIKitActivation).Log().Debug(
					$"Ignoring a user activity of type '{userActivity?.ActivityType ?? "(null)"}'; only a browsing-web activity is a universal link.");
			}

			return false;
		}

		return TryReportUrl(userActivity.WebPageUrl, previousExecutionState);
	}

	/// <summary>
	/// Reports a URL the app was opened with.
	/// </summary>
	/// <remarks>
	/// An exception thrown by the app's handler is logged and raised as a recoverable unhandled exception, so
	/// that it neither escapes into UIKit nor keeps the other URLs of the same callback from being reported.
	/// </remarks>
	internal static bool TryReportUrl(NSUrl? url, ApplicationExecutionState previousExecutionState)
	{
		if (!TryParseUri(url, out var uri) || Application.Current is not { } application)
		{
			return false;
		}

		try
		{
			application.OnActivated(new ProtocolActivatedEventArgs(uri, previousExecutionState));
			return true;
		}
		catch (Exception ex)
		{
			// Only the scheme is logged: an activation URL routinely carries an OAuth code or token.
			if (typeof(AppleUIKitActivation).Log().IsEnabled(LogLevel.Error))
			{
				typeof(AppleUIKitActivation).Log().Error($"The app's OnActivated failed for a URL with scheme '{uri.Scheme}'.", ex);
			}

			application.RaiseRecoverableUnhandledException(ex);
			return false;
		}
	}

	private static bool TryParseUri(NSUrl? url, [NotNullWhen(true)] out Uri? uri)
	{
		uri = null;

		if (url is null)
		{
			return false;
		}

		if (Uri.TryCreate(url.AbsoluteString, UriKind.Absolute, out uri))
		{
			return true;
		}

		// Only the scheme is logged: an activation URL routinely carries an OAuth code or token.
		if (typeof(AppleUIKitActivation).Log().IsEnabled(LogLevel.Error))
		{
			typeof(AppleUIKitActivation).Log().Error(
				$"An activation URL with scheme '{url.Scheme ?? "(none)"}' could not be parsed as an absolute URI.");
		}

		return false;
	}
}
