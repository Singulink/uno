#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Uno.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI.Core;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Represents an object that enables the hosting of web content.
/// </summary>
#if IS_UNIT_TESTS || __SKIA__ || __NETSTD_REFERENCE__
[Uno.NotImplemented("IS_UNIT_TESTS", "__SKIA__", "__NETSTD_REFERENCE__")]
#endif
public partial class WebView2 : Control, IWebView
{
	// The messages WinUI uses for the same cases.
	internal const string ClosedMessage = "Cannot create CoreWebView2 for Closed WebView2 element.";
	private const string NoCoreWebView2Message =
		"Failed because a valid CoreWebView2 is not present. Make sure one was created, for example by calling EnsureCoreWebView2Async() API.";

	private CoreWebView2? _coreWebView2;
	private bool _sourceChangeFromCore;
	private bool _coreWebView2Initialized;

	/// <summary>
	/// Initializes a new instance of the WebView2 class.
	/// </summary>
	public WebView2()
	{
		DefaultStyleKey = typeof(WebView2);

		_coreWebView2 = new CoreWebView2(this);
		_coreWebView2.HistoryChanged += CoreWebView2_HistoryChanged;
		_coreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
		_coreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
		_coreWebView2.SourceChanged += CoreWebView2_SourceChanged;
		_coreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

		Loaded += WebView2_Loaded;
#if __SKIA__
		Unloaded += WebView2_Unloaded;
#endif
	}

	/// <summary>
	/// Gets the CoreWebView2 of this control, or null once the control is closed.
	/// </summary>
	public CoreWebView2 CoreWebView2 => _coreWebView2!;

	bool IWebView.IsLoaded => IsLoaded;

	bool IWebView.SwitchSourceBeforeNavigating => false; // WebView2 switches source only when navigation completes.

	CoreDispatcher IWebView.Dispatcher => Dispatcher;

	protected override void OnApplyTemplate() => _coreWebView2?.OnOwnerApplyTemplate();

	private void WebView2_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (_coreWebView2 is null)
		{
			return;
		}

		if (!_coreWebView2Initialized)
		{
			EnsureCoreWebView2();
		}

#if __SKIA__
		// A CoreWebView2Initialized handler may have closed the WebView2.
		_coreWebView2?.OnLoaded();
#endif
	}

#if __SKIA__
	private void WebView2_Unloaded(object sender, RoutedEventArgs e) => _coreWebView2?.OnUnloaded();
#endif

	public IAsyncAction EnsureCoreWebView2Async() =>
		AsyncAction.FromTask(async ct =>
		{
			var coreWebView2 = _coreWebView2 ?? throw new ObjectDisposedException(nameof(WebView2), ClosedMessage);

			if (!_coreWebView2Initialized)
			{
				EnsureCoreWebView2();
			}

			await coreWebView2.EnsureNativeWebViewAsync();
		});

	public IAsyncOperation<string?> ExecuteScriptAsync(string javascriptCode) =>
		GetCoreWebView2OrThrow(nameof(ExecuteScriptAsync)).ExecuteScriptAsync(javascriptCode);

	public void Reload() => GetCoreWebView2OrThrow(nameof(Reload)).Reload();

	public void GoForward() => _coreWebView2?.GoForward();

	public void GoBack() => _coreWebView2?.GoBack();

	public void NavigateToString(string htmlContent) => GetCoreWebView2OrThrow(nameof(NavigateToString)).NavigateToString(htmlContent);

#if __SKIA__
	/// <summary>
	/// Closes the WebView2 and releases its browser. Afterwards, <see cref="CoreWebView2"/> is null and the control can't navigate.
	/// </summary>
	public void Close()
	{
		if (_coreWebView2 is not { } coreWebView2)
		{
			return;
		}

		if (!coreWebView2.TryClose())
		{
			// This platform's native WebView can't release its browser yet.
			global::Windows.Foundation.Metadata.ApiInformation.TryRaiseNotImplemented("Microsoft.UI.Xaml.Controls.WebView2", "Close()");
			return;
		}

		coreWebView2.HistoryChanged -= CoreWebView2_HistoryChanged;
		coreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
		coreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
		coreWebView2.SourceChanged -= CoreWebView2_SourceChanged;
		coreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
		_coreWebView2 = null;

		(CanGoBack, CanGoForward) = (false, false);
	}
#endif

	private void NavigateFromSource(string targetUrl) =>
		(_coreWebView2 ?? throw new ObjectDisposedException(nameof(WebView2), ClosedMessage)).Navigate(targetUrl);

	private CoreWebView2 GetCoreWebView2OrThrow(string memberName) =>
		_coreWebView2 ?? throw new InvalidOperationException($"{memberName}(): {NoCoreWebView2Message}");

	private void EnsureCoreWebView2()
	{
		if (!_coreWebView2Initialized)
		{
			CoreWebView2Initialized?.Invoke(this, new());
			_coreWebView2Initialized = true;
		}
	}

	private void CoreWebView2_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) =>
		NavigationStarting?.Invoke(this, args);

	private void CoreWebView2_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) =>
		NavigationCompleted?.Invoke(this, args);

	private void CoreWebView2_HistoryChanged(CoreWebView2 sender, object args) =>
		(CanGoBack, CanGoForward) = (sender.CanGoBack, sender.CanGoForward);

	private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args) =>
		WebMessageReceived?.Invoke(this, args);

	private void CoreWebView2_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
	{
		_sourceChangeFromCore = true;
		Source = Uri.TryCreate(sender.Source, UriKind.Absolute, out var uri) ? uri : CoreWebView2.BlankUri;
		_sourceChangeFromCore = false;
	}

	protected override Automation.Peers.AutomationPeer OnCreateAutomationPeer()
		=> new Automation.Peers.WebView2AutomationPeer(this);
}
