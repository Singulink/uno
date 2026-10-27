#if HAS_UNO_WINUI
using System;
using System.Threading.Tasks;
using Private.Infrastructure;
using Microsoft.UI.Xaml.Controls;
using System.Linq;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Uno.UI.NativeElementHosting;
using Uno.UI.RuntimeTests.Helpers;
using Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls;

[TestClass]
[RunsOnUIThread]
[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.SkiaFrameBuffer)]
public class Given_WebView2
{
	[TestMethod]
#if __SKIA__
	[Ignore("WebView2 is not yet supported on skia targets")]
#endif
	public async Task When_InvokeScriptAsync()
	{
		var border = new Border();
		var webView = new WebView2();
		webView.Width = 200;
		webView.Height = 200;
		border.Child = webView;
		TestServices.WindowHelper.WindowContent = border;
		bool navigated = false;
		await TestServices.WindowHelper.WaitForLoaded(border);
		webView.NavigationCompleted += (sender, e) => navigated = true;
		webView.NavigateToString("<html><body><div id='test' style='width: 100px; height: 100px; background-color: blue;' /></body></html>");
		await TestServices.WindowHelper.WaitFor(() => navigated, timeoutMS: 10000);

		var sw = Stopwatch.StartNew();
		string color = null;

		do
		{
			// We need to wait for the element to be available, navigated
			// may be set to true too early on wasm.
			color = await webView.ExecuteScriptAsync(
				"""
				(function () {
					let testElement = document.getElementById('test');
					if(testElement){
						return testElement.style.backgroundColor.toString();
					}
					return "";
				})()
				""");

		} while (sw.Elapsed < TimeSpan.FromSeconds(10) && string.IsNullOrEmpty(color.Replace("\"", "")));

		Assert.AreEqual("\"blue\"", color);

		// Change color to red
		await webView.ExecuteScriptAsync("document.getElementById('test').style.backgroundColor = 'red'");
		color = await webView.ExecuteScriptAsync("document.getElementById('test').style.backgroundColor.toString()");

		Assert.AreEqual("\"red\"", color);
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Collected_Then_Native_Iframe_Is_Removed()
	{
		var (webViewReference, elementId) = await LoadNavigateAndUnloadWebView2();

		Assert.IsTrue(await TestHelper.TryWaitUntilCollected(webViewReference), "The WebView2 should be collectable once it left the tree.");

		await TestServices.WindowHelper.WaitFor(
			() => !IsInDom(elementId),
			timeoutMS: 5000,
			message: "The iframe should be removed from the DOM once its WebView2 is collected.");
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25736")]
	public async Task When_Closed_Then_Native_Iframe_Is_Removed()
	{
		var webView = new WebView2 { Width = 200, Height = 200 };

		try
		{
			await UITestHelper.Load(webView);
			await NavigateToMarker(webView, "closed");
			var elementId = GetIframeElementId(webView);

			webView.Close();

			Assert.IsFalse(IsInDom(elementId), "Close should remove the iframe from the DOM right away.");
			Assert.IsNull(webView.CoreWebView2, "As in WinUI, CoreWebView2 should be null once the WebView2 is closed.");
			Assert.IsFalse(webView.CanGoBack);
			Assert.IsFalse(webView.CanGoForward);

			// As in WinUI: a second Close does nothing, GoBack and GoForward do nothing,
			// and the members that need a browser throw.
			webView.Close();
			webView.GoBack();
			webView.GoForward();
			await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await webView.EnsureCoreWebView2Async());
			Assert.ThrowsExactly<ObjectDisposedException>(() => webView.Source = new Uri("https://platform.uno/"));
			Assert.ThrowsExactly<InvalidOperationException>(() => webView.NavigateToString("<p>closed</p>"));
			Assert.ThrowsExactly<InvalidOperationException>(() => webView.Reload());
			Assert.ThrowsExactly<InvalidOperationException>(() => webView.ExecuteScriptAsync("1"));

			// Unloading and loading the closed WebView2 again must neither touch the removed iframe nor create a new one.
			TestServices.WindowHelper.WindowContent = null;
			await TestServices.WindowHelper.WaitForIdle();
			await UITestHelper.Load(webView);

			var root = VisualTreeHelper.GetChild(webView, 0) as ContentPresenter;
			Assert.IsNull(root?.Content, "A closed WebView2 should not present an iframe.");
			Assert.IsNull(webView.CoreWebView2);
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25736")]
	public async Task When_Closed_Then_Collectable()
	{
		var (webViewReference, elementId) = await LoadNavigateAndUnloadWebView2(close: true);

		Assert.IsFalse(IsInDom(elementId), "Close should remove the iframe from the DOM.");
		Assert.IsTrue(await TestHelper.TryWaitUntilCollected(webViewReference), "A closed WebView2 should be collectable once it left the tree.");
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25736")]
	public async Task When_Closed_Before_Loaded_Then_Pending_EnsureCoreWebView2Async_Throws()
	{
		var webView = new WebView2 { Width = 200, Height = 200 };

		// The WebView2 isn't loaded, so the operation waits for its native WebView.
		var ensureTask = webView.EnsureCoreWebView2Async().AsTask();
		Assert.IsFalse(ensureTask.IsCompleted, "EnsureCoreWebView2Async should wait for the WebView2 to load.");

		webView.Close();

		await TestServices.WindowHelper.WaitFor(() => ensureTask.IsCompleted, timeoutMS: 5000, message: "Close should end a pending EnsureCoreWebView2Async.");
		await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await ensureTask);
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25736")]
	public async Task When_Closed_From_CoreWebView2Initialized_On_Load()
	{
		var webView = new WebView2 { Width = 200, Height = 200 };
		webView.CoreWebView2Initialized += (sender, args) => sender.Close();

		// Loaded handlers' exceptions are caught and reported here.
		Exception unhandledException = null;
		void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
		{
			unhandledException ??= args.Exception;
			args.Handled = true;
		}

		Microsoft.UI.Xaml.Application.Current.UnhandledException += OnUnhandledException;

		try
		{
			await UITestHelper.Load(webView);

			Assert.IsNull(unhandledException, $"Closing the WebView2 from CoreWebView2Initialized should not throw: {unhandledException}");
			Assert.IsNull(webView.CoreWebView2, "The CoreWebView2Initialized handler should have closed the WebView2.");

			var root = VisualTreeHelper.GetChild(webView, 0) as ContentPresenter;
			Assert.IsNull(root?.Content, "A closed WebView2 should not present an iframe.");
		}
		finally
		{
			Microsoft.UI.Xaml.Application.Current.UnhandledException -= OnUnhandledException;
			TestServices.WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm | RuntimeTestPlatforms.SkiaDesktop)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25736")]
	public async Task When_Closed_From_CoreWebView2Initialized_Before_Template_Then_EnsureCoreWebView2Async_Completes()
	{
		var webView = new WebView2 { Width = 200, Height = 200 };
		webView.CoreWebView2Initialized += (sender, args) => sender.Close();

		// As in WinUI, closing the WebView2 from CoreWebView2Initialized doesn't fail the call.
		await webView.EnsureCoreWebView2Async().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

		Assert.IsNull(webView.CoreWebView2, "The CoreWebView2Initialized handler should have closed the WebView2.");
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25736")]
	public async Task When_Closed_From_CoreWebView2Initialized_After_Template_Then_EnsureCoreWebView2Async_Completes()
	{
		var border = new Border { Width = 200, Height = 200 };
		var webView = new WebView2 { Width = 200, Height = 200 };
		webView.CoreWebView2Initialized += (sender, args) => sender.Close();

		try
		{
			await UITestHelper.Load(border);

			// Lay the WebView2 out so it creates its iframe, then call before its Loaded raises CoreWebView2Initialized.
			border.Child = webView;
			webView.UpdateLayout();
			GetIframeElementId(webView);

			// As in WinUI, closing the WebView2 from CoreWebView2Initialized doesn't fail the call.
			await webView.EnsureCoreWebView2Async().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

			Assert.IsNull(webView.CoreWebView2, "The CoreWebView2Initialized handler should have closed the WebView2.");

			var root = VisualTreeHelper.GetChild(webView, 0) as ContentPresenter;
			Assert.IsNull(root?.Content, "A closed WebView2 should not present an iframe.");
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25736")]
	public async Task When_Retemplated_Then_Closed_Then_Replaced_Iframe_Is_Removed()
	{
		var webView = new WebView2 { Width = 200, Height = 200 };

		try
		{
			await UITestHelper.Load(webView);
			await NavigateToMarker(webView, "replaced");
			var replacedElementId = GetIframeElementId(webView);

			webView.Template = (ControlTemplate)XamlReader.Load(
				"""
				<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
					<ContentPresenter x:Name="WebViewTemplateRoot" />
				</ControlTemplate>
				""");
			await TestServices.WindowHelper.WaitForIdle();

			var elementId = GetIframeElementId(webView);
			Assert.AreNotEqual(replacedElementId, elementId, "The new template should present a new iframe.");

			webView.Close();
			Assert.IsFalse(IsInDom(elementId), "Close should remove the presented iframe.");

			TestServices.WindowHelper.WindowContent = null;
			await TestServices.WindowHelper.WaitForIdle();

			for (var i = 0; i < 3; i++)
			{
				GC.Collect(2);
				GC.WaitForPendingFinalizers();
				await TestServices.WindowHelper.WaitForIdle();
			}

			await TestServices.WindowHelper.WaitFor(
				() => !IsInDom(replacedElementId),
				timeoutMS: 5000,
				message: "The iframe of the replaced template should not outlive the closed WebView2.");
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Unloaded_While_Referenced_Then_Native_Iframe_Is_Kept()
	{
		var webView = new WebView2 { Width = 200, Height = 200 };

		try
		{
			await UITestHelper.Load(webView);
			await NavigateToMarker(webView, "kept");
			var elementId = GetIframeElementId(webView);

			TestServices.WindowHelper.WindowContent = null;
			await TestServices.WindowHelper.WaitForIdle();

			for (var i = 0; i < 3; i++)
			{
				GC.Collect(2);
				GC.WaitForPendingFinalizers();
				await TestServices.WindowHelper.WaitForIdle();
			}

			// WinUI keeps the browser across unloads and only closes it when the WebView2 is closed or destroyed.
			Assert.AreEqual("kept", ReadMarker(elementId), "Unloading a WebView2 that is still referenced must keep its document.");

			await UITestHelper.Load(webView);

			Assert.AreEqual(elementId, GetIframeElementId(webView), "The reloaded WebView2 should present its original iframe.");
			Assert.AreEqual("kept", ReadMarker(elementId), "The reloaded WebView2 should still show the document it navigated to.");
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25735")]
	public async Task When_Navigated_To_Cross_Origin_Content_Then_Navigation_Completes()
	{
		// A data: URL gets an opaque origin, so the browser hides the iframe's location and document from the app.
		const string DataUrl = "data:text/html;base64,PHRpdGxlPkNyb3NzLW9yaWdpbjwvdGl0bGU+PHA+Q3Jvc3Mtb3JpZ2luPC9wPg==";

		var webView = new WebView2 { Width = 200, Height = 200 };
		CoreWebView2NavigationCompletedEventArgs completedArgs = null;
		void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
		{
			if (sender.CoreWebView2.Source == DataUrl)
			{
				completedArgs = args;
			}
		}

		StartCapturingBrowserErrors();
		try
		{
			await UITestHelper.Load(webView);
			await webView.EnsureCoreWebView2Async();
			webView.NavigationCompleted += OnNavigationCompleted;

			webView.CoreWebView2.Navigate(DataUrl);
			await TestServices.WindowHelper.WaitFor(
				() => completedArgs is not null,
				timeoutMS: 10000,
				message: "NavigationCompleted should be raised for the cross-origin document, with Source set to its URL.");

			Assert.IsTrue(completedArgs.IsSuccess);
			Assert.AreEqual(new Uri(DataUrl), webView.Source);
			Assert.IsNotNull(webView.CoreWebView2.DocumentTitle, "Reading the title of a cross-origin document should not throw.");
			Assert.AreEqual("", StopCapturingBrowserErrors(), "The browser should not report an error for the cross-origin load.");
		}
		finally
		{
			StopCapturingBrowserErrors();
			webView.NavigationCompleted -= OnNavigationCompleted;
			TestServices.WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25735")]
	public async Task When_Navigated_To_Sandboxed_Mapped_Content_Then_Navigation_Completes()
	{
		const string MappedUrl = "http://UnoNativeAssets/index.html";
		const string MappedPath = "/WebContent/index.html";

		var webView = new WebView2 { Width = 200, Height = 200 };
		string completedSource = null;
		void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
		{
			if (sender.CoreWebView2.Source is { } source && source.EndsWith(MappedPath, StringComparison.Ordinal))
			{
				completedSource = source;
			}
		}

		async Task<string> NavigateToMappedContent(string elementId, string sandbox)
		{
			// The frame's sandbox applies to the documents it loads from then on.
			WasmSemanticDomHelper.InvokeBrowserJs(
				$"(function(){{document.getElementById('{elementId}').setAttribute('sandbox', '{sandbox}');return '';}})()");

			completedSource = null;
			webView.CoreWebView2.Navigate(MappedUrl);
			await TestServices.WindowHelper.WaitFor(
				() => completedSource is not null,
				timeoutMS: 10000,
				message: $"NavigationCompleted should be raised for the mapped content (sandbox='{sandbox}'), with Source set to its URL.");

			return completedSource;
		}

		StartCapturingBrowserErrors();
		try
		{
			await UITestHelper.Load(webView);
			await webView.EnsureCoreWebView2Async();
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping("UnoNativeAssets", "WebContent", CoreWebView2HostResourceAccessKind.Allow);
			webView.NavigationCompleted += OnNavigationCompleted;
			var elementId = GetIframeElementId(webView);

			// Keeping the origin leaves the document readable (its scripts are off, which this test doesn't need).
			var readableSource = await NavigateToMappedContent(elementId, "allow-same-origin");

			// Without allow-same-origin the document gets an opaque origin, as with a Content-Security-Policy: sandbox header,
			// so the browser hides its location and the URL known from the navigation has to be reported.
			var hiddenSource = await NavigateToMappedContent(elementId, "");

			Assert.AreEqual(readableSource, hiddenSource, "A hidden document should be reported with the same URL as a readable one.");
			Assert.AreEqual(new Uri(readableSource), webView.Source);
			Assert.AreEqual("", StopCapturingBrowserErrors(), "The browser should not report an error for the sandboxed load.");
		}
		finally
		{
			StopCapturingBrowserErrors();
			webView.NavigationCompleted -= OnNavigationCompleted;
			TestServices.WindowHelper.WindowContent = null;
		}
	}

	private static void StartCapturingBrowserErrors()
		=> WasmSemanticDomHelper.InvokeBrowserJs(
			"(function(){var errors=window.__unoTestErrors=[];window.__unoTestOnError=function(e){errors.push(String(e.message));};" +
			"window.addEventListener('error',window.__unoTestOnError);return '';})()");

	private static string StopCapturingBrowserErrors()
		=> WasmSemanticDomHelper.InvokeBrowserJs(
			"(function(){var handler=window.__unoTestOnError;if(!handler){return '';}window.removeEventListener('error',handler);" +
			"var errors=window.__unoTestErrors.join('\\n');delete window.__unoTestOnError;delete window.__unoTestErrors;return errors;})()");

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static async Task<(WeakReference, string)> LoadNavigateAndUnloadWebView2(bool close = false)
	{
		var webView = new WebView2 { Width = 200, Height = 200 };

		try
		{
			await UITestHelper.Load(webView);

			// A navigated WebView2 registers load and message listeners, which must not keep it alive.
			await NavigateToMarker(webView, "collected");
			var elementId = GetIframeElementId(webView);

			if (close)
			{
				webView.Close();
			}

			return (new WeakReference(webView), elementId);
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
			await TestServices.WindowHelper.WaitForIdle();
		}
	}

	private static async Task NavigateToMarker(WebView2 webView, string marker)
	{
		var navigated = false;
		void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => navigated = true;

		webView.NavigationCompleted += OnNavigationCompleted;
		try
		{
			webView.NavigateToString($"<html><body data-marker='{marker}'></body></html>");
			await TestServices.WindowHelper.WaitFor(() => navigated, timeoutMS: 10000, message: "The WebView2 should complete its NavigateToString.");
		}
		finally
		{
			webView.NavigationCompleted -= OnNavigationCompleted;
		}

		// On WebAssembly, NavigationCompleted is raised before the queued navigation loads the document.
		var elementId = GetIframeElementId(webView);
		await TestServices.WindowHelper.WaitFor(() => ReadMarker(elementId) == marker, timeoutMS: 10000, message: "The iframe should load the marker.");
	}

	private static string GetIframeElementId(WebView2 webView)
	{
		var root = VisualTreeHelper.GetChild(webView, 0) as ContentPresenter;
		Assert.AreEqual("WebViewTemplateRoot", root?.Name, "The WebView2 template root should be its ContentPresenter.");

		var element = root.Content as BrowserHtmlElement;
		Assert.IsNotNull(element, "The WebView2 template root should present its iframe as a BrowserHtmlElement.");

		return element.ElementId;
	}

	private static bool IsInDom(string elementId)
		=> WasmSemanticDomHelper.InvokeBrowserJs($"(function(){{return document.getElementById('{elementId}') ? '1' : '0';}})()") == "1";

	private static string ReadMarker(string elementId)
		=> WasmSemanticDomHelper.InvokeBrowserJs(
			$"(function(){{var body = document.getElementById('{elementId}')?.contentDocument?.body; return body ? (body.dataset.marker || '') : '';}})()");

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25739")]
	public async Task When_Showing_Cross_Origin_Content_Then_It_Can_Use_Browser_Tab_Features()
	{
		var frame = await ShowCrossOriginContent();

		Assert.AreEqual("autoplay *; fullscreen *; encrypted-media *; picture-in-picture *; clipboard-write *", frame.Allow);
		Assert.IsTrue(frame.HasAllowFullscreen, "The frame should also allow fullscreen through the legacy allowfullscreen attribute.");

		if (frame.AllowedFeatures is not null)
		{
			Assert.AreEqual("autoplay fullscreen encrypted-media picture-in-picture clipboard-write", frame.AllowedFeatures);
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25739")]
	public async Task When_Permissions_Policy_Changed_Then_New_Web_View_Uses_It()
	{
		var policy = FeatureConfiguration.WebView2.PermissionsPolicy;
		var defaultPolicy = policy.ToArray();

		try
		{
			policy.Remove("fullscreen");
			policy.Add("camera");
			policy.Add("microphone https://example.com");

			// The browser splits a directive on any ASCII whitespace, not only spaces.
			policy.Add("geolocation\nhttps://example.com");

			var frame = await ShowCrossOriginContent();

			Assert.AreEqual(
				"autoplay *; encrypted-media *; picture-in-picture *; clipboard-write *; camera *; " +
				"microphone https://example.com; geolocation https://example.com",
				frame.Allow);
			Assert.IsFalse(frame.HasAllowFullscreen, "The frame should not allow fullscreen through the legacy allowfullscreen attribute.");

			if (frame.AllowedFeatures is not null)
			{
				// The microphone and the location are delegated to another origin only.
				Assert.AreEqual("autoplay encrypted-media picture-in-picture clipboard-write camera", frame.AllowedFeatures);
			}
		}
		finally
		{
			policy.Clear();
			foreach (var directive in defaultPolicy)
			{
				policy.Add(directive);
			}
		}
	}

	// Shows a document with an opaque origin, so cross-origin to the app, and returns its frame's attributes and the features
	// the document reports it may use. Only Chromium tells the latter, so it's null in other browsers.
	private static async Task<(string Allow, bool HasAllowFullscreen, string AllowedFeatures)> ShowCrossOriginContent()
	{
		const string Probe =
			"<script>var policy = document.featurePolicy;" +
			"var features = ['autoplay', 'fullscreen', 'encrypted-media', 'picture-in-picture', 'clipboard-write', 'clipboard-read'," +
			" 'camera', 'microphone', 'geolocation'];" +
			"var allowed = policy ? features.filter(f => policy.allowsFeature(f)).join(' ') : '?';" +
			"parent.postMessage({ unoAllowedFeatures: allowed }, '*');</script>";

		var webView = new WebView2 { Width = 200, Height = 200 };

		try
		{
			await UITestHelper.Load(webView);
			await webView.EnsureCoreWebView2Async();
			var elementId = GetIframeElementId(webView);

			WasmSemanticDomHelper.InvokeBrowserJs(
				"(function(){window.__unoAllowedFeatures=null;window.__unoAllowedFeaturesListener=function(e){" +
				$"if(e.source===document.getElementById('{elementId}').contentWindow&&e.data&&e.data.unoAllowedFeatures!==undefined)" +
				"{window.__unoAllowedFeatures=e.data.unoAllowedFeatures;}};" +
				"window.addEventListener('message',window.__unoAllowedFeaturesListener);return '';})()");

			webView.CoreWebView2.Navigate("data:text/html;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Probe)));

			var report = "";
			await TestServices.WindowHelper.WaitFor(
				() => (report = WasmSemanticDomHelper.InvokeBrowserJs(
					"(function(){var f=window.__unoAllowedFeatures;return f===null?'':'allowed:'+f;})()")).Length > 0,
				timeoutMS: 10000,
				message: "The cross-origin document should report the features it may use.");

			var allowedFeatures = report["allowed:".Length..];
			var allow = WasmSemanticDomHelper.InvokeBrowserJs($"(function(){{return document.getElementById('{elementId}').getAttribute('allow') ?? '';}})()");
			var hasAllowFullscreen = WasmSemanticDomHelper.InvokeBrowserJs(
				$"(function(){{return document.getElementById('{elementId}').hasAttribute('allowfullscreen') ? '1' : '0';}})()") == "1";

			return (allow, hasAllowFullscreen, allowedFeatures == "?" ? null : allowedFeatures);
		}
		finally
		{
			WasmSemanticDomHelper.InvokeBrowserJs(
				"(function(){window.removeEventListener('message',window.__unoAllowedFeaturesListener);" +
				"delete window.__unoAllowedFeaturesListener;delete window.__unoAllowedFeatures;return '';})()");
			TestServices.WindowHelper.WindowContent = null;
		}
	}
}
#endif
