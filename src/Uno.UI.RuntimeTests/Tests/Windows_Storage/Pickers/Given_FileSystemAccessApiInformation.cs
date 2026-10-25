#if __CROSSRUNTIME__
#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Private.Infrastructure;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;

namespace Uno.UI.RuntimeTests.Tests.Windows_Storage.Pickers;

// Chrome has the File System Access API pickers. Without them, as in Brave, Firefox and Safari, the file pickers
// fall back to the download and upload pickers; run in Chrome with --disable-blink-features=FileSystemAccessLocal to cover that.
[TestClass]
[RunsOnUIThread]
[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
public class Given_FileSystemAccessApiInformation
{
	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/24130")]
	[DataRow(WasmPickerConfiguration.FileSystemAccessApiWithFallback, "IsSavePickerSupported")]
	[DataRow(WasmPickerConfiguration.FileSystemAccessApiWithFallback, "IsOpenPickerSupported")]
	[DataRow(WasmPickerConfiguration.FileSystemAccessApiWithFallback, "AreFilePickersSupported")]
	[DataRow(WasmPickerConfiguration.DownloadUpload, "IsSavePickerSupported")]
	[DataRow(WasmPickerConfiguration.DownloadUpload, "IsOpenPickerSupported")]
	[DataRow(WasmPickerConfiguration.DownloadUpload, "AreFilePickersSupported")]
	public void When_Download_Upload_Pickers_Are_Enabled_Then_File_Pickers_Are_Supported(WasmPickerConfiguration configuration, string property)
	{
		var previousConfiguration = WinRTFeatureConfiguration.Storage.Pickers.WasmConfiguration;
		WinRTFeatureConfiguration.Storage.Pickers.WasmConfiguration = configuration;

		try
		{
			Assert.IsTrue(GetSupport(property));
		}
		finally
		{
			WinRTFeatureConfiguration.Storage.Pickers.WasmConfiguration = previousConfiguration;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/24130")]
	[DataRow("IsSavePickerSupported", "showSaveFilePicker")]
	[DataRow("IsOpenPickerSupported", "showOpenFilePicker")]
	public void When_Only_File_System_Access_Api_Is_Enabled_Then_Support_Follows_The_Browser(string property, string nativePicker)
	{
		var previousConfiguration = WinRTFeatureConfiguration.Storage.Pickers.WasmConfiguration;
		WinRTFeatureConfiguration.Storage.Pickers.WasmConfiguration = WasmPickerConfiguration.FileSystemAccessApi;

		try
		{
			Assert.AreEqual(InvokeJS($"typeof {nativePicker}") == "function", GetSupport(property));
		}
		finally
		{
			WinRTFeatureConfiguration.Storage.Pickers.WasmConfiguration = previousConfiguration;
		}
	}

	// Safari 15.2 to 18 has an origin-private file system without createWritable.
	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/24130")]
	[DataRow(true)]
	[DataRow(false)]
	public async Task When_Download_Is_Committed_Then_The_Browser_Downloads_The_Content(bool hasCreateWritable)
	{
		var previousConfiguration = WinRTFeatureConfiguration.Storage.Pickers.WasmConfiguration;
		WinRTFeatureConfiguration.Storage.Pickers.WasmConfiguration = WasmPickerConfiguration.DownloadUpload;
		InvokeJS(CaptureDownloadsScript.Replace("HAS_CREATE_WRITABLE", hasCreateWritable ? "true" : "false"));
		StorageFile? file = null;

		try
		{
			var picker = new FileSavePicker { SuggestedFileName = "Report" };
			picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });

			file = await picker.PickSaveFileAsync();
			Assert.IsNotNull(file);

			CachedFileManager.DeferUpdates(file);
			await FileIO.WriteTextAsync(file, "Hello, world!");
			Assert.AreEqual(FileUpdateStatus.Complete, await CachedFileManager.CompleteUpdatesAsync(file));

			Assert.AreEqual("1", InvokeJS("String(globalThis.__unoTestDownloads.items.length)"));
			Assert.AreEqual("Report.txt", InvokeJS("globalThis.__unoTestDownloads.items[0].name"));

			InvokeJS(ReadDownloadScript);
			await TestServices.WindowHelper.WaitFor(() => InvokeJS("String(globalThis.__unoTestDownloads.text !== undefined)") == "true", timeoutMS: 5000);
			Assert.AreEqual("Hello, world!", InvokeJS("globalThis.__unoTestDownloads.text"));
		}
		finally
		{
			InvokeJS(RestoreScript);
			WinRTFeatureConfiguration.Storage.Pickers.WasmConfiguration = previousConfiguration;

			if (file is not null)
			{
				await file.DeleteAsync();
			}
		}
	}

	// Records the downloads instead of starting them, and optionally hides createWritable.
	private const string CaptureDownloadsScript = """
		(function () {
			const handlePrototype = globalThis.FileSystemFileHandle?.prototype;
			const state = globalThis.__unoTestDownloads = {
				items: [],
				click: Object.getOwnPropertyDescriptor(HTMLAnchorElement.prototype, "click"),
				createWritable: handlePrototype && Object.getOwnPropertyDescriptor(handlePrototype, "createWritable"),
			};
			HTMLAnchorElement.prototype.click = function () {
				if (this.download) {
					state.items.push({ name: this.download, href: this.href });
				}
				else {
					HTMLElement.prototype.click.call(this);
				}
			};
			if (!HAS_CREATE_WRITABLE && handlePrototype) {
				delete handlePrototype.createWritable;
			}
			return "";
		})()
		""";

	private const string ReadDownloadScript = """
		(function () {
			const state = globalThis.__unoTestDownloads;
			fetch(state.items[0].href)
				.then(response => response.text())
				.then(text => state.text = text, error => state.text = "Failed to read the download: " + error);
			return "";
		})()
		""";

	private const string RestoreScript = """
		(function () {
			const state = globalThis.__unoTestDownloads;
			if (!state) {
				return "";
			}
			if (state.click) {
				Object.defineProperty(HTMLAnchorElement.prototype, "click", state.click);
			}
			else {
				delete HTMLAnchorElement.prototype.click;
			}
			if (state.createWritable) {
				Object.defineProperty(FileSystemFileHandle.prototype, "createWritable", state.createWritable);
			}
			delete globalThis.__unoTestDownloads;
			return "";
		})()
		""";

	private static bool GetSupport(string propertyName)
	{
		var type = Type.GetType("Uno.Storage.Pickers.FileSystemAccessApiInformation, Uno.UI.Runtime.Skia.WebAssembly.Browser", throwOnError: true)!;

		return (bool)type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
	}

	private static string InvokeJS(string script)
		=> _invokeJS.Value.Invoke(null, new object[] { script }) as string ?? "";

	private static readonly Lazy<MethodInfo> _invokeJS = new(() =>
		Type.GetType("Uno.Foundation.WebAssemblyRuntime, Uno.Foundation.Runtime.WebAssembly", throwOnError: true)!
			.GetMethod("InvokeJS", new[] { typeof(string) })!);
}
#endif
