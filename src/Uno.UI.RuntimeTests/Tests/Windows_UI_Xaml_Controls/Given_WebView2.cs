#if HAS_UNO_WINUI
using System;
using System.Threading.Tasks;
using Private.Infrastructure;
using Microsoft.UI.Xaml.Controls;
using System.Linq;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
	public async Task When_Closed_Then_Collectable()
	{
		var (webViewReference, elementId) = await LoadNavigateAndUnloadWebView2(close: true);

		Assert.IsFalse(IsInDom(elementId), "Close should remove the iframe from the DOM.");
		Assert.IsTrue(await TestHelper.TryWaitUntilCollected(webViewReference), "A closed WebView2 should be collectable once it left the tree.");
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
}
#endif
