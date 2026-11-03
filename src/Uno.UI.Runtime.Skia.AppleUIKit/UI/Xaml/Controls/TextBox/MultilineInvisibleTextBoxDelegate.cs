using System;
using System.Linq;
using Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UIKit;
using Uno.Extensions;

namespace Uno.WinUI.Runtime.Skia.AppleUIKit.Controls;

internal partial class MultilineInvisibleTextBoxDelegate : UITextViewDelegate
{
	private readonly WeakReference<InvisibleTextBoxViewExtension> _textBoxViewExtension;
	private string? _lastText;

	public MultilineInvisibleTextBoxDelegate(WeakReference<InvisibleTextBoxViewExtension> textBoxViewExtension)
	{
		_textBoxViewExtension = textBoxViewExtension ?? throw new ArgumentNullException(nameof(textBoxViewExtension));
	}

	public override void Changed(UITextView textView)
	{
		if (textView is MultilineInvisibleTextBoxView bindableTextView)
		{
			var currentText = textView.Text;
			if (_lastText != currentText)
			{
				_lastText = currentText;
				bindableTextView.OnTextChanged();
			}
		}
	}

	public override bool ShouldChangeText(UITextView textView, NSRange range, string replacementString)
	{
		if (textView is MultilineInvisibleTextBoxView textBoxView)
		{
			if (_textBoxViewExtension.GetTarget()?.Owner.TextBox is not TextBox textBox)
			{
				return false;
			}

			// Both IsReadOnly = true and IsTabStop = false can prevent editing
			if (textBox.IsReadOnly || !textBox.IsTabStop)
			{
				return false;
			}

			if (range.Length == 0 && string.IsNullOrEmpty(replacementString))
			{
				return false;
			}

			// During IME composition, allow text changes through without
			// MaxLength interference — the composition system manages length.
			if (textBoxView.IsComposing)
			{
				return true;
			}

			// Suppress the iOS autocorrect autospace fired when the caret leaves a word (see IsNoOpAutocorrectReplacement).
			if (InvisibleTextBoxAutocorrect.IsNoOpAutocorrectReplacement(textView.Text, range, replacementString))
			{
				return false;
			}

			// TODO:MZ:
			//if (textBox.OnKey(text.FirstOrDefault()))
			//{
			//	return false;
			//}

			// Typing and pasting go in only when they fit; deleting and putting the TextBox's own text back always do.
			return InvisibleTextBoxViewExtension.IsAllowedByMaxLength(textBox, textBoxView.Text, range, replacementString);
		}

		return true;
	}

	public override bool ShouldEndEditing(UITextView textView)
	{
		return true;
	}

	/// <summary>
	/// Corresponds to a gain of focus
	/// </summary>
	public override void EditingStarted(UITextView textView)
	{
		if (_textBoxViewExtension.GetTarget()?.Owner.TextBox is TextBox textBox && textBox.FocusState == FocusState.Unfocused)
		{
			textBox.Focus(FocusState.Pointer);
		}
	}

	/// <summary>
	/// Corresponds to a loss of focus
	/// </summary>
	public override void EditingEnded(UITextView textView)
	{
		var bindableTextView = textView as MultilineInvisibleTextBoxView;
		bindableTextView?.OnTextChanged();

		if (_textBoxViewExtension.GetTarget()?.Owner.TextBox is TextBox { FocusState: not FocusState.Unfocused } textBox)
		{
			textBox.Unfocus();
		}
	}
}
