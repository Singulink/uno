using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Private.Infrastructure;
using Uno.UI.RuntimeTests.Helpers;

#if HAS_UNO
using Uno.UI.Runtime.Skia;
using static Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation.WasmSemanticDomHelper;
#endif

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation
{
	/// <summary>
	/// Runtime tests for accessible text box behavior.
	/// Tests automation peer properties, value pattern, and ARIA attribute mapping.
	/// </summary>
	[TestClass]
	public class Given_AccessibleTextBox
	{
#if HAS_UNO
		[TestCleanup]
		public void DisableAccessibilityAfterTest() => DisableAccessibility();
#endif

		/// <summary>
		/// T046: Verifies that a focused textbox exposes its text value
		/// via the IValueProvider pattern.
		/// </summary>
		/// <remarks>
		/// Excluded on NativeWinUI: WinUI surfaces the TextBox Value/Text patterns at the native
		/// UIA layer via the windowless RichEdit provider, not through the managed
		/// AutomationPeer.GetPattern. So peer.GetPattern(PatternInterface.Value) returns null on
		/// WinUI, whereas Uno's TextBoxAutomationPeer implements IValueProvider directly.
		/// </remarks>
		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_TextBox_Focused_Then_Value_Exposed()
		{
			// Arrange
			var textBox = new TextBox
			{
				Text = "Hello world"
			};

			await UITestHelper.Load(textBox);

			// Act
			var peer = FrameworkElementAutomationPeer.CreatePeerForElement(textBox);
			var valueProvider = peer?.GetPattern(PatternInterface.Value) as IValueProvider;

			// Assert
			Assert.IsNotNull(peer, "TextBox should have an automation peer");
			Assert.IsNotNull(valueProvider, "TextBox should support IValueProvider");
			Assert.AreEqual("Hello world", valueProvider.Value, "Value should match TextBox.Text");
		}

		/// <summary>
		/// T047: Verifies that setting text via IValueProvider.SetValue()
		/// updates the underlying TextBox. This is the path used when text
		/// is entered in the semantic input element.
		/// </summary>
		/// <remarks>
		/// Excluded on NativeWinUI: the managed AutomationPeer does not expose IValueProvider for
		/// TextBox on WinUI (the Value pattern comes from the windowless RichEdit UIA provider),
		/// so this Uno-specific peer-level value sync cannot be asserted there.
		/// </remarks>
		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_Text_Entered_Then_Value_Syncs()
		{
			// Arrange
			var textBox = new TextBox { Text = "" };

			await UITestHelper.Load(textBox);

			var peer = FrameworkElementAutomationPeer.CreatePeerForElement(textBox);
			var valueProvider = peer?.GetPattern(PatternInterface.Value) as IValueProvider;

			Assert.IsNotNull(valueProvider, "TextBox should support IValueProvider");

			// Act - Simulate text input via automation peer
			valueProvider.SetValue("New text");

			// Assert
			Assert.AreEqual("New text", textBox.Text, "TextBox.Text should be updated");
			Assert.AreEqual("New text", valueProvider.Value, "ValueProvider should report updated value");
		}

		/// <summary>
		/// T048: Verifies that a PasswordBox has correct automation properties.
		/// When mapped to semantic DOM, this should create input[type=password].
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_PasswordBox_Then_Input_Type_Is_Password()
		{
			// Arrange
			var passwordBox = new PasswordBox
			{
				Password = "secret"
			};

			await UITestHelper.Load(passwordBox);

			// Act
			var peer = FrameworkElementAutomationPeer.CreatePeerForElement(passwordBox);

			// Assert
			Assert.IsNotNull(peer, "PasswordBox should have an automation peer");
			Assert.IsTrue(peer.IsPassword(), "PasswordBox peer should report IsPassword=true");

#if HAS_UNO
			// Verify AriaMapper produces Password element type
			var elementType = AriaMapper.GetSemanticElementType(peer);
			Assert.AreEqual(SemanticElementType.Password, elementType, "PasswordBox should map to Password element type");
#endif
		}

		/// <summary>
		/// Verifies that TextBox automation peer has correct control type.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_TextBox_Created_Then_Has_Edit_ControlType()
		{
			// Arrange
			var textBox = new TextBox();
			await UITestHelper.Load(textBox);

			// Act
			var peer = FrameworkElementAutomationPeer.CreatePeerForElement(textBox);
			var controlType = peer?.GetAutomationControlType();

			// Assert
			Assert.AreEqual(AutomationControlType.Edit, controlType);
		}

		/// <summary>
		/// Verifies that TextBox with AutomationProperties.Name exposes correct name.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_TextBox_Has_AutomationName_Then_Name_Is_Exposed()
		{
			// Arrange
			var textBox = new TextBox();
			AutomationProperties.SetName(textBox, "Enter your name");

			await UITestHelper.Load(textBox);

			// Act
			var peer = FrameworkElementAutomationPeer.CreatePeerForElement(textBox);
			var name = peer?.GetName();

			// Assert
			Assert.AreEqual("Enter your name", name);
		}

		/// <summary>
		/// Verifies that a read-only TextBox reports correct state.
		/// </summary>
		/// <remarks>
		/// Excluded on NativeWinUI: TextBox does not expose IValueProvider via the managed
		/// AutomationPeer on WinUI (the Value pattern is provided by the windowless RichEdit UIA
		/// provider), so peer.GetPattern(PatternInterface.Value) is null there.
		/// </remarks>
		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)]
		public async Task When_TextBox_IsReadOnly_Then_ValueProvider_IsReadOnly()
		{
			// Arrange
			var textBox = new TextBox
			{
				Text = "Read only text",
				IsReadOnly = true
			};
			await UITestHelper.Load(textBox);

			// Act
			var peer = FrameworkElementAutomationPeer.CreatePeerForElement(textBox);
			var valueProvider = peer?.GetPattern(PatternInterface.Value) as IValueProvider;

			// Assert
			Assert.IsNotNull(valueProvider);
			Assert.IsTrue(valueProvider.IsReadOnly, "Read-only TextBox should report IsReadOnly=true");
		}

#if __SKIA__
		/// <summary>
		/// Verifies that browser-originated typing through the semantic textbox keeps
		/// the caret at the browser-managed position instead of jumping back to the start.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_SemanticTextBox_Receives_Input_Then_Text_Order_And_Caret_Are_Preserved()
		{
			var textBox = new TextBox();

			await UITestHelper.Load(textBox);
			textBox.GetOrCreateAutomationPeer();

			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => SemanticElementExists(textBox), timeoutMS: 5000, message: "Timed out waiting for the semantic textbox to be created.");
			await UITestHelper.WaitForIdle();

			TypeCharacterIntoSemanticTextBox(textBox, "a");
			await UITestHelper.WaitForIdle();
			TypeCharacterIntoSemanticTextBox(textBox, "b");
			await UITestHelper.WaitForIdle();
			TypeCharacterIntoSemanticTextBox(textBox, "c");
			await UITestHelper.WaitForIdle();

			Assert.AreEqual("abc", textBox.Text, "Semantic typing should preserve text order in the managed TextBox.");
			Assert.AreEqual("abc", GetSemanticTextBoxValue(textBox), "Semantic textbox DOM value should stay in sync with managed text.");
			Assert.AreEqual("3", GetSemanticTextBoxCaret(textBox), "Caret should remain at the end after sequential semantic input.");
		}

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_Accessibility_Is_Enabled_After_Text_Exists_Then_SemanticTyping_Appends_At_Current_Caret()
		{
			var textBox = new TextBox
			{
				Text = "test"
			};

			await UITestHelper.Load(textBox);
			textBox.Focus(FocusState.Programmatic);
			textBox.Select(textBox.Text.Length, 0);
			textBox.GetOrCreateAutomationPeer();

			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => SemanticElementExists(textBox), timeoutMS: 5000, message: "Timed out waiting for the semantic textbox to be created.");
			await UITestHelper.WaitForIdle();

			Assert.AreEqual("test", GetSemanticTextBoxValue(textBox), "Semantic textbox should mirror the existing managed value when accessibility is enabled.");
			Assert.AreEqual("4", GetSemanticTextBoxCaret(textBox), "Semantic textbox should inherit the managed caret position when created.");
			// The hidden native <input> is detached asynchronously once the semantic textbox owns focus;
			// poll for it instead of asserting synchronously, which raced on the slower WASM scheduler.
			await UITestHelper.WaitFor(() => !HiddenNativeTextBoxExists(), timeoutMS: 5000,
				message: "The hidden browser textbox should be detached once the semantic textbox owns focus.");

			TypeTextIntoSemanticTextBox(textBox, "test");
			await UITestHelper.WaitForIdle();

			Assert.AreEqual("testtest", textBox.Text, "Semantic typing should append at the current caret instead of inserting in reverse at the start.");
			Assert.AreEqual("testtest", GetSemanticTextBoxValue(textBox), "Semantic textbox DOM value should stay aligned after appending text.");
			Assert.AreEqual("8", GetSemanticTextBoxCaret(textBox), "Caret should remain at the end after appending to preexisting text.");
		}

		/// <summary>
		/// Typing through the semantic textbox is user input, so MaxLength refuses what doesn't fit, and the semantic element
		/// shows the TextBox's text again.
		/// </summary>
		[TestMethod]
		[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25997")]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		[DataRow(false, DisplayName = "Single-line")]
		[DataRow(true, DisplayName = "Multi-line, the browser reports \\n")]
		public async Task When_SemanticTextBox_Input_Exceeds_MaxLength_Then_It_Is_Refused(bool multiline)
		{
			var textBox = new TextBox { AcceptsReturn = multiline, Text = multiline ? "a\rb" : "abc", MaxLength = 3 };

			try
			{
				await LoadWithSemanticElement(textBox);

				SetSemanticTextControlValueAndSelection(textBox, multiline ? "a\nxb" : "axbc", 2, 2);
				await UITestHelper.WaitForIdle();

				Assert.AreEqual(multiline ? "a\rb" : "abc", textBox.Text, "the typed character doesn't fit");
				Assert.AreEqual(multiline ? "a\nb" : "abc", GetSemanticTextBoxValue(textBox), "the semantic element shows the TextBox's text again");
			}
			finally
			{
				TestServices.WindowHelper.WindowContent = null;
			}
		}

		/// <summary>
		/// Semantic input that only deletes goes in, even while the text set from code is longer than MaxLength.
		/// </summary>
		[TestMethod]
		[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25997")]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		[DataRow(false, DisplayName = "Single-line")]
		[DataRow(true, DisplayName = "Multi-line, the browser reports \\n")]
		public async Task When_SemanticTextBox_Deletes_From_Text_Longer_Than_MaxLength(bool multiline)
		{
			var textBox = new TextBox { AcceptsReturn = multiline, Text = multiline ? "ab\rcdefghij" : "0123456789", MaxLength = 5 };

			try
			{
				await LoadWithSemanticElement(textBox);

				SetSemanticTextControlValueAndSelection(textBox, multiline ? "ab\ncdefghi" : "012345678", multiline ? 10 : 9, multiline ? 10 : 9);
				await UITestHelper.WaitForIdle();

				Assert.AreEqual(multiline ? "ab\rcdefghi" : "012345678", textBox.Text);
			}
			finally
			{
				TestServices.WindowHelper.WindowContent = null;
			}
		}

		/// <summary>
		/// A read-only or disabled TextBox refuses semantic input, and the semantic element shows its text again.
		/// </summary>
		[TestMethod]
		[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25997")]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		[DataRow(true, DisplayName = "Read-only")]
		[DataRow(false, DisplayName = "Disabled")]
		public async Task When_SemanticTextBox_Input_Reaches_A_ReadOnly_Or_Disabled_TextBox(bool readOnly)
		{
			var textBox = new TextBox { Text = "abc" };

			try
			{
				await LoadWithSemanticElement(textBox);
				if (readOnly)
				{
					textBox.IsReadOnly = true;
				}
				else
				{
					textBox.IsEnabled = false;
				}

				await UITestHelper.WaitForIdle();

				SetSemanticTextControlValueAndSelection(textBox, "abcd", 4, 4);
				await UITestHelper.WaitForIdle();

				Assert.AreEqual("abc", textBox.Text);
				Assert.AreEqual("abc", GetSemanticTextBoxValue(textBox));
			}
			finally
			{
				TestServices.WindowHelper.WindowContent = null;
			}
		}

		[TestMethod]
		[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25997")]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_SemanticPasswordBox_Input_Exceeds_MaxLength_Then_It_Is_Refused()
		{
			var passwordBox = new PasswordBox { Password = "abc", MaxLength = 3 };

			try
			{
				await LoadWithSemanticElement(passwordBox);

				SetSemanticTextControlValueAndSelection(passwordBox, "abcd", 4, 4);
				await UITestHelper.WaitForIdle();

				Assert.AreEqual("abc", passwordBox.Password);
				Assert.AreEqual("abc", GetSemanticTextBoxValue(passwordBox), "the semantic element shows the password again");
			}
			finally
			{
				TestServices.WindowHelper.WindowContent = null;
			}
		}

		/// <summary>
		/// T046/FR-016 (WASM DOM): a single-line TextBox emits a native &lt;input type="text"&gt; semantic element
		/// whose value mirrors TextBox.Text.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_TextBox_Then_Dom_Is_Text_Input_With_Value()
		{
			var textBox = new TextBox { Text = "Hello world" };

			await UITestHelper.Load(textBox);
			textBox.GetOrCreateAutomationPeer();

			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => SemanticElementExists(textBox), timeoutMS: 5000, message: "Timed out waiting for the semantic textbox to be created.");
			await UITestHelper.WaitForIdle();

			Assert.AreEqual("input", GetSemanticElementTagName(textBox), "A single-line TextBox must emit a native <input> semantic element.");
			Assert.AreEqual("text", GetSemanticInputType(textBox), "A single-line TextBox must emit input[type=text].");
			Assert.AreEqual("Hello world", GetSemanticTextBoxValue(textBox), "The semantic input value must mirror TextBox.Text.");
		}

		/// <summary>
		/// FR-016 (WASM DOM): a multiline TextBox (AcceptsReturn=true) emits a &lt;textarea&gt; semantic element
		/// instead of an &lt;input&gt;.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_TextBox_AcceptsReturn_Then_Dom_Is_Textarea()
		{
			var textBox = new TextBox { AcceptsReturn = true, Text = "Line 1" };

			await UITestHelper.Load(textBox);
			textBox.GetOrCreateAutomationPeer();

			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => SemanticElementExists(textBox), timeoutMS: 5000, message: "Timed out waiting for the multiline semantic textbox to be created.");
			await UITestHelper.WaitForIdle();

			Assert.AreEqual("textarea", GetSemanticElementTagName(textBox), "A multiline TextBox (AcceptsReturn=true) must emit a <textarea> semantic element.");
		}

		/// <summary>
		/// FR-016 (WASM DOM): a read-only TextBox emits a semantic input carrying the readonly attribute.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_TextBox_IsReadOnly_Then_Dom_Input_Is_ReadOnly()
		{
			var textBox = new TextBox { Text = "Read only text", IsReadOnly = true };

			await UITestHelper.Load(textBox);
			textBox.GetOrCreateAutomationPeer();

			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => SemanticElementExists(textBox), timeoutMS: 5000, message: "Timed out waiting for the read-only semantic textbox to be created.");
			await UITestHelper.WaitForIdle();

			Assert.IsTrue(SemanticElementHasAttribute(textBox, "readonly"), "A read-only TextBox must emit a semantic input with the readonly attribute set.");
		}

		/// <summary>
		/// T048/FR-016 (WASM DOM): a PasswordBox emits a native &lt;input type="password"&gt; semantic element so
		/// the browser masks the value.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_PasswordBox_Then_Dom_Input_Type_Is_Password()
		{
			var passwordBox = new PasswordBox { Password = "secret" };

			await UITestHelper.Load(passwordBox);
			passwordBox.GetOrCreateAutomationPeer();

			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => SemanticElementExists(passwordBox), timeoutMS: 5000, message: "Timed out waiting for the password semantic element to be created.");
			await UITestHelper.WaitForIdle();

			Assert.AreEqual("input", GetSemanticElementTagName(passwordBox), "A PasswordBox must emit a native <input> semantic element.");
			Assert.AreEqual("password", GetSemanticInputType(passwordBox), "A PasswordBox must emit input[type=password].");
		}


		// TextBox-specific accessors. Generic helpers (existence, attribute, tag name, input type,
		// attribute presence) live in WasmSemanticDomHelper and are used directly via the
		// `using static` above; only TextBox-only behaviors (value, caret, character injection,
		// hidden native input check) stay local.

		private static string GetSemanticTextBoxValue(TextBox textBox)
			=> InvokeBrowserJs($"(function(){{const element = document.getElementById('{GetSemanticElementId(textBox)}'); return element ? element.value : '';}})()");

		private static string GetSemanticTextBoxCaret(TextBox textBox)
			=> InvokeBrowserJs($"(function(){{const element = document.getElementById('{GetSemanticElementId(textBox)}'); return element ? String(element.selectionStart ?? -1) : '-1';}})()");

		private static async Task LoadWithSemanticElement(Control control)
		{
			await UITestHelper.Load(control);
			control.Focus(FocusState.Programmatic);
			control.GetOrCreateAutomationPeer();

			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(
				() => SemanticElementExists(control), timeoutMS: 5000, message: "Timed out waiting for the semantic element to be created.");
			await UITestHelper.WaitForIdle();
		}

		private static void SetSemanticTextControlValueAndSelection(UIElement element, string value, int selectionStart, int selectionEnd)
		{
			var escapedValue = EscapeJavaScriptString(value);
			InvokeBrowserJs($"(function(){{const semanticElement = document.getElementById('{GetSemanticElementId(element)}'); if (!semanticElement) {{ return 'missing'; }} semanticElement.focus(); semanticElement.value = '{escapedValue}'; semanticElement.setSelectionRange({selectionStart}, {selectionEnd}); semanticElement.dispatchEvent(new Event('input', {{ bubbles: true }})); return semanticElement.value; }})()");
		}

		private static string EscapeJavaScriptString(string value)
			=> value
				.Replace("\\", "\\\\")
				.Replace("'", "\\'")
				.Replace("\r", "\\r")
				.Replace("\n", "\\n");

		private static void TypeCharacterIntoSemanticTextBox(TextBox textBox, string character)
		{
			var escapedCharacter = character
				.Replace("\\", "\\\\")
				.Replace("'", "\\'");

			InvokeBrowserJs($"(function(){{const element = document.getElementById('{GetSemanticElementId(textBox)}'); if (!element) {{ return 'missing'; }} element.focus(); const start = element.selectionStart ?? 0; const end = element.selectionEnd ?? start; const character = '{escapedCharacter}'; element.value = element.value.slice(0, start) + character + element.value.slice(end); const caret = start + character.length; element.setSelectionRange(caret, caret); element.dispatchEvent(new Event('input', {{ bubbles: true }})); return element.value; }})()");
		}

		private static void TypeTextIntoSemanticTextBox(TextBox textBox, string text)
		{
			foreach (var character in text)
			{
				TypeCharacterIntoSemanticTextBox(textBox, character.ToString());
			}
		}

#endif

#if HAS_UNO
		/// <summary>
		/// Verifies that AriaMapper correctly identifies TextBox semantic element type.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_TextBox_Mapped_Then_SemanticElementType_Is_TextBox()
		{
			// Arrange
			var textBox = new TextBox();
			await UITestHelper.Load(textBox);

			// Act
			var peer = textBox.GetOrCreateAutomationPeer();
			var elementType = AriaMapper.GetSemanticElementType(peer);

			// Assert
			Assert.AreEqual(SemanticElementType.TextBox, elementType);
		}

		/// <summary>
		/// Verifies that multiline TextBox maps to TextArea element type.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_TextBox_AcceptsReturn_Then_SemanticElementType_Is_TextArea()
		{
			// Arrange
			var textBox = new TextBox { AcceptsReturn = true };
			await UITestHelper.Load(textBox);

			// Act
			var peer = textBox.GetOrCreateAutomationPeer();
			var elementType = AriaMapper.GetSemanticElementType(peer);

			// Assert
			Assert.AreEqual(SemanticElementType.TextArea, elementType);
		}

		/// <summary>
		/// Verifies that AriaMapper correctly detects value capability.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_TextBox_Mapped_Then_PatternCapabilities_CanValue_Is_True()
		{
			// Arrange
			var textBox = new TextBox();
			await UITestHelper.Load(textBox);

			// Act
			var peer = textBox.GetOrCreateAutomationPeer();
			var capabilities = AriaMapper.GetPatternCapabilities(peer);

			// Assert
			Assert.IsTrue(capabilities.CanValue, "TextBox should have CanValue capability");
		}
#endif
	}
}
