using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.RuntimeTests.Helpers;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Documents;

[TestClass]
[RunsOnUIThread]
public class Given_UnicodeText_LoneSurrogates
{
	// Named rather than passed as text: test names go into the results XML, which can't hold a lone surrogate.
	public enum Sample
	{
		HighBeforeLetter,
		HighAtEnd,
		LowAfterLetter,
		LowThenHigh,
	}

	[TestMethod]
	[DataRow(Sample.HighBeforeLetter)]
	[DataRow(Sample.HighAtEnd)]
	[DataRow(Sample.LowAfterLetter)]
	[DataRow(Sample.LowThenHigh)]
	public async Task When_TextBlock_Text_Has_Lone_Surrogate(Sample sample)
	{
		var textBlock = new TextBlock();
		await LayOutAndRender(textBlock, () => textBlock.Text = TextFor(sample));
	}

	[TestMethod]
	[DataRow(Sample.HighBeforeLetter)]
	[DataRow(Sample.HighAtEnd)]
	[DataRow(Sample.LowAfterLetter)]
	[DataRow(Sample.LowThenHigh)]
	public async Task When_TextBox_Text_Has_Lone_Surrogate(Sample sample)
	{
		var textBox = new TextBox();
		await LayOutAndRender(textBox, () => textBox.Text = TextFor(sample));
	}

	private static string TextFor(Sample sample) => sample switch
	{
		Sample.HighBeforeLetter => "a\uD83Db",
		Sample.HighAtEnd => "a\uD83D",
		Sample.LowAfterLetter => "a\uDE00b",
		_ => "a\uDE00\uD83Db",
	};

	private static async Task LayOutAndRender(FrameworkElement element, Action setText)
	{
		using var _ = UITestHelper.ResetWindowContent();
		await UITestHelper.Load(element, x => x.IsLoaded);

		// UpdateLayout lays the text out right here, so a throw fails the test. During a frame, a throw is only logged.
		setText();
		element.UpdateLayout();

		await UITestHelper.ScreenShot(element);
	}
}
