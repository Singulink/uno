#nullable enable
using System;
using Windows.Foundation;
using Uno.Disposables;
using Uno.Foundation.Extensibility;
using Uno.Foundation.Logging;
using Uno.UI.Xaml.Controls.Extensions;

namespace Microsoft.UI.Xaml.Controls;

public partial class TextBox
{
	private static IImeTextBoxExtension? _imeExtension;

	private static TextBox? _activeImeTextBox;
	private bool _isComposing;
	private bool _platformTextApplyInProgress;
	// True when the current composition session has the platform applying text directly
	// (e.g., Android's InputConnection). In this mode, key events arrive independently
	// from composition events and should NOT be swallowed by the IsComposing check.
	private bool _compositionAppliedByPlatform;
	// A platform applying the composition itself syncs its text right after reporting it (Android's EndBatchEdit, the
	// browser's input event): TextCompositionChanged waits for that sync, so handlers see the text its range refers to.
	private bool _compositionChangedPending;
	// The range the last TextCompositionChanged of the composition reported, if any.
	private (int StartIndex, int Length)? _lastCompositionChangedRange;
	private int _compositionStartIndex;
	private int _compositionLength;
	private int _compositionResolvedLength;
	// The selection that the composition's first applied text replaces. Until some composition text is applied, the composition
	// has no text of its own (_compositionLength is 0), and an empty replacement leaves the selection alone.
	private int _compositionSelectionLength;
	private bool _compositionApplied;
	// A platform that applies the text itself reports each update before syncing its text, which can still be refused: the range
	// to go back to then, and whether the last update was refused.
	private (int startIndex, int length, int resolvedLength) _compositionRangeBeforePlatformSync;
	private bool _wasPlatformSyncRefused;

	internal bool ShouldSwallowKeyDuringComposition => _isComposing && !_compositionAppliedByPlatform;

	public event TypedEventHandler<TextBox, TextCompositionStartedEventArgs>? TextCompositionStarted;
	public event TypedEventHandler<TextBox, TextCompositionChangedEventArgs>? TextCompositionChanged;
	public event TypedEventHandler<TextBox, TextCompositionEndedEventArgs>? TextCompositionEnded;

	internal bool IsComposing => _isComposing;
	internal int CompositionStartIndex => _compositionStartIndex;
	internal int CompositionLength => _compositionLength;

	/// <summary>
	/// The underline range within the composition, starting after any already-resolved characters.
	/// Used by the renderer to only underline the active (unresolved) portion of the preedit.
	/// </summary>
	internal int CompositionUnderlineStart => _compositionStartIndex + _compositionResolvedLength;
	internal int CompositionUnderlineLength => Math.Max(0, _compositionLength - _compositionResolvedLength);

	private static void InitializeIme()
	{
		if (!ApiExtensibility.CreateInstance<IImeTextBoxExtension>(typeof(TextBox), out var imeExtension))
		{
			typeof(TextBox).LogDebug()?.Debug("No IME extension registered or registration returned null, IME composition will not be supported.");
			return;
		}
		imeExtension.CompositionStarted += static (_, _) => _activeImeTextBox?.OnImeCompositionStarted();
		imeExtension.CompositionUpdated += static (_, e) => _activeImeTextBox?.OnImeCompositionUpdated(e.Text, e.CursorPosition, e.ResolvedLength, e.TextAlreadyApplied, e.CompositionStart);
		imeExtension.CompositionCompleted += static (_, e) => _activeImeTextBox?.OnImeCompositionCompleted(e.Text, e.TextAlreadyApplied);
		imeExtension.CompositionEnded += static (_, _) => _activeImeTextBox?.OnImeCompositionEnded();
		_imeExtension = imeExtension;
	}

	private void StartImeSession()
	{
		// Don't route IME composition events to PasswordBox — password text
		// must not be processed as composition text, even if the extension
		// no-ops for PasswordBox (any later global composition event would
		// otherwise be forwarded to this PasswordBox via _activeImeTextBox).
		if (this is PasswordBox)
		{
			return;
		}

		_activeImeTextBox = this;
		_imeExtension?.StartImeSession(this);
	}

	private void EndImeSession()
	{
		// Symmetric with StartImeSession — PasswordBoxes never activate the
		// IME session, so don't tear it down (which would clobber _activeImeTextBox).
		if (this is PasswordBox)
		{
			return;
		}

		_imeExtension?.EndImeSession();
		_activeImeTextBox = null;

		// Defensively reset composition state in case the extension's CompositionEnded
		// event didn't fire (e.g., extension's _isComposing already false but TextBox's
		// _isComposing still true from a stale CompositionStarted). Without this, all
		// subsequent key events would be swallowed at the IsComposing check in OnKeyDown.
		_compositionAppliedByPlatform = false;
		_compositionChangedPending = false;
		if (_isComposing)
		{
			var (startIndex, length) = ResetComposition();
			RaiseTextCompositionEnded(startIndex, length);
			InvalidateTextBoxRender();
		}
	}

	/// <summary>
	/// Whether a platform's text input must take the TextBox's text back at once after the TextBox processed an edit the
	/// input applied, which ends the input's composition: the TextBox refused the edit and kept its previous text, or kept
	/// text of another length (MaxLength, a length limit). The keyboard then builds its next keystrokes and suggestions on
	/// the text the TextBox kept. A change that keeps the length (a casing filter) stays in the composition until it ends,
	/// so an IME can still convert it. Line breaks match whether written as '\r' (the TextBox) or '\n' (the platform).
	/// </summary>
	internal static bool MustReturnTextToNative(string keptText, string textBefore, string nativeText)
		=> !IsSameText(keptText, nativeText)
			&& (keptText.Length != nativeText.Length || IsSameText(keptText, textBefore));

	// Compares without allocating a normalized copy, as this runs for every edit the platform applies.
	private static bool IsSameText(string first, string second)
	{
		if (first.Length != second.Length)
		{
			return false;
		}

		for (var i = 0; i < first.Length; i++)
		{
			var a = first[i] == '\r' ? '\n' : first[i];
			var b = second[i] == '\r' ? '\n' : second[i];
			if (a != b)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// The platform's IME (Skia Android's EndBatchEdit) synced the text it applied itself, with its caret.
	/// </summary>
	internal void UpdateTextFromNative(string text, int selectionStart, int selectionLength)
	{
		var textBefore = Text;
		ProcessTextInput(text, out var applied);

		// The native caret belongs to the native text, so a refused edit keeps the TextBox's own selection.
		if (applied)
		{
			Select(selectionStart, selectionLength);
		}

		// The editable breaks lines with \n where the TextBox stores \r: a text differing only by them is no edit.
		OnPlatformTextSynced(refused: Text == textBefore && text.Replace('\n', '\r') != textBefore);
	}

	// The platform's text sync has landed: raises the TextCompositionChanged it was holding, unless the TextBox refused
	// that text, which isn't in Text then.
	private void OnPlatformTextSynced(bool refused)
	{
		if (!_compositionChangedPending)
		{
			return;
		}

		_compositionChangedPending = false;
		if (_isComposing && !refused)
		{
			RaiseTextCompositionChanged();
		}
	}

	private void OnImeCompositionStarted()
	{
		if (IsReadOnly)
		{
			return;
		}

		_isComposing = true;
		_compositionChangedPending = false;
		_lastCompositionChangedRange = null;
		_compositionStartIndex = SelectionStart;
		// The first ReplaceCompositionText replaces the selected range, matching normal typing behavior.
		_compositionSelectionLength = SelectionLength;
		_compositionLength = 0;
		_compositionResolvedLength = 0;
		_compositionApplied = false;
		_wasPlatformSyncRefused = false;

		TextCompositionStarted?.Invoke(this, new TextCompositionStartedEventArgs(_compositionStartIndex, _compositionSelectionLength));
	}

	private void OnImeCompositionUpdated(string compositionText, int cursorPosition, int resolvedLength, bool textAlreadyApplied, int compositionStart)
	{
		if (IsReadOnly)
		{
			return;
		}

		if (textAlreadyApplied)
		{
			// The platform (e.g., Android InputConnection) already applied the text.
			// Suppress CancelCompositionOnExternalChange for the ProcessTextInput call
			// that will follow from the platform's text sync (EndBatchEdit).
			_platformTextApplyInProgress = true;
			// Mark the session so key events aren't swallowed by the IsComposing check
			// (Android key events are independent of composition events).
			_compositionAppliedByPlatform = true;
			OnPlatformCompositionUpdate();
			if (compositionStart >= 0)
			{
				// An IME re-composing an existing word starts the composition before the caret.
				_compositionStartIndex = compositionStart;
			}
		}
		else if (!ReplaceCompositionText(compositionText, cursorPosition))
		{
			// Keep the range on the text that is really there, so clearing or cancelling the composition can't delete a neighbor.
			return;
		}

		_compositionLength = compositionText.Length;
		_compositionResolvedLength = resolvedLength;

		// Text the platform applied is synced after this call (see OnPlatformTextSynced).
		_compositionChangedPending = textAlreadyApplied;
		if (!textAlreadyApplied)
		{
			RaiseTextCompositionChanged();
		}
		InvalidateTextBoxRender();
	}

	private void OnImeCompositionCompleted(string committedText, bool textAlreadyApplied)
	{
		if (IsReadOnly)
		{
			return;
		}

		TrySetCurrentlyTyping(true);
		var startIndex = _compositionStartIndex;
		var committedLength = committedText.Length;
		if (textAlreadyApplied ? _wasPlatformSyncRefused : !ReplaceCompositionText(committedText))
		{
			// A refused commit leaves the composition text that was already there, so report that range.
			startIndex = Math.Min(startIndex, Text.Length);
			committedLength = Math.Min(_compositionLength, Text.Length - startIndex);
		}

		ResetComposition();

		// The committed text's own range, not clamped: Skia Android raises this before syncing that text.
		TextCompositionEnded?.Invoke(this, new TextCompositionEndedEventArgs(startIndex, committedLength));
		InvalidateTextBoxRender();
	}

	private void OnImeCompositionEnded()
	{
		if (!_isComposing)
		{
			_compositionAppliedByPlatform = false;
			_compositionChangedPending = false;
			return;
		}

		// Composition ended without explicit commit — keep text as-is (matches WinUI behavior).
		// The composition text was already inserted via ProcessTextInput during OnImeCompositionUpdated.
		var (startIndex, length) = ResetComposition();
		RaiseTextCompositionEnded(startIndex, length);
		InvalidateTextBoxRender();
	}

	// Clears the composition state and returns the range it covered.
	private (int StartIndex, int Length) ResetComposition()
	{
		var range = (_compositionStartIndex, _compositionLength);
		_isComposing = false;
		_compositionAppliedByPlatform = false;
		_compositionChangedPending = false;
		_lastCompositionChangedRange = null;
		_compositionLength = 0;
		_compositionStartIndex = 0;
		_compositionResolvedLength = 0;
		return range;
	}

	// The composition's range can outlast its text (a coercion or a deletion during the composition), so handlers get
	// the part of it that is in the text.
	private (int StartIndex, int Length) ClampToText(int startIndex, int length)
	{
		var textLength = Text.Length;
		var start = Math.Clamp(startIndex, 0, textLength);
		return (start, Math.Clamp(length, 0, textLength - start));
	}

	private void RaiseTextCompositionChanged()
	{
		var (start, length) = ClampToText(_compositionStartIndex, _compositionLength);
		_lastCompositionChangedRange = (start, length);
		TextCompositionChanged?.Invoke(this, new TextCompositionChangedEventArgs(start, length));
	}

	private void RaiseTextCompositionEnded(int startIndex, int length)
	{
		var (start, clampedLength) = ClampToText(startIndex, length);
		TextCompositionEnded?.Invoke(this, new TextCompositionEndedEventArgs(start, clampedLength));
	}

	// Runs before the update changes the range.
	private void OnPlatformCompositionUpdate()
	{
		_compositionRangeBeforePlatformSync = (_compositionStartIndex, _compositionLength, _compositionResolvedLength);
		_wasPlatformSyncRefused = false;
		_compositionApplied = true;
	}

	/// <summary>
	/// The text the platform synced after reporting a composition update was refused, so no text change ends the platform's text
	/// apply, and the range goes back to the text that is really there.
	/// </summary>
	private void OnPlatformSyncRefused()
	{
		_platformTextApplyInProgress = false;
		if (!_isComposing)
		{
			return;
		}

		(_compositionStartIndex, _compositionLength, _compositionResolvedLength) = _compositionRangeBeforePlatformSync;
		_wasPlatformSyncRefused = true;

		// The refused update's range is never raised (OnPlatformTextSynced). The range that is really there is, unless it is
		// the one handlers last got.
		_compositionChangedPending = false;
		if (_lastCompositionChangedRange != ClampToText(_compositionStartIndex, _compositionLength))
		{
			RaiseTextCompositionChanged();
		}
		InvalidateTextBoxRender();
	}

	/// <summary>
	/// Replaces the composition range with <paramref name="newText"/>, through the user-input path.
	/// </summary>
	/// <returns>Whether the replacement was applied: MaxLength or a BeforeTextChanging handler can refuse it, and an empty
	/// replacement before any composition text was applied leaves the selection it would replace.</returns>
	private bool ReplaceCompositionText(string newText, int cursorPosition = -1)
	{
		if (!_compositionApplied && newText.Length == 0)
		{
			return false;
		}

		var text = Text;
		// Clamp indices to text bounds in case the platform modified the text
		// out-of-band (e.g., Android IME autocorrect during a composition session)
		// leaving _compositionStartIndex/_compositionLength stale.
		var startIndex = Math.Min(_compositionStartIndex, text.Length);
		var length = _compositionApplied ? _compositionLength : _compositionSelectionLength;
		var endIndex = Math.Min(_compositionStartIndex + length, text.Length);
		var replaced = text[..startIndex] + newText + text[endIndex..];

		// Place the caret at the IME-reported cursor position within the composition,
		// or at the end of the new text if not available. Clamp to valid range
		// since some IMEs may report offsets beyond the current preedit length.
		var caretOffset = cursorPosition >= 0
			? Math.Min(cursorPosition, newText.Length)
			: newText.Length;

		_pendingSelection = (startIndex + caretOffset, 0);
		var applied = ProcessTextInputKeepingHistory(replaced);
		_compositionApplied |= applied;
		return applied;
	}

	/// <summary>
	/// Called from OnTextChangedPartial. If the text was changed externally
	/// (not by the IME composition path), cancel the active composition.
	/// </summary>
	private void CancelCompositionOnExternalChange()
	{
		if (!_isComposing || _suppressCurrentlyTyping)
		{
			// Not composing, or the change came from ReplaceCompositionText — nothing to do.
			return;
		}

		if (_platformTextApplyInProgress)
		{
			// The platform already applied the text (e.g., Android EndBatchEdit), so don't cancel the composition.
			// While its text is processed, edits by TextChanging handlers belong to it too: ProcessTextInput clears the flag. The
			// platform ends the composition afterwards if such an edit changed the text's length (MustReturnTextToNative).
			if (!_isInputModifyingText)
			{
				_platformTextApplyInProgress = false;
			}
			return;
		}

		var (startIndex, length) = ResetComposition();

		// End and restart the session so that further IME input still works
		// while the active TextBox reference stays in sync.
		_imeExtension?.EndImeSession();
		RaiseTextCompositionEnded(startIndex, length);
		InvalidateTextBoxRender();

		// Restart the IME session so the user can continue typing with IME.
		if (_activeImeTextBox == this)
		{
			_imeExtension?.StartImeSession(this);
		}
	}

	private void InvalidateTextBoxRender() => TextBoxView?.DisplayBlock.InvalidateInlines(false);

	/// <summary>
	/// Installs a fake IME extension for testing. The extension's events are
	/// forwarded to the active TextBox. Returns a disposable that restores the original.
	/// </summary>
	internal static IDisposable SetImeExtensionForTesting(IImeTextBoxExtension extension)
	{
		var original = _imeExtension;
		_imeExtension = extension;

		EventHandler onStarted = (_, _) => _activeImeTextBox?.OnImeCompositionStarted();
		EventHandler<ImeCompositionEventArgs> onUpdated = (_, e) => _activeImeTextBox?.OnImeCompositionUpdated(e.Text, e.CursorPosition, e.ResolvedLength, e.TextAlreadyApplied, e.CompositionStart);
		EventHandler<ImeCompositionEventArgs> onCompleted = (_, e) => _activeImeTextBox?.OnImeCompositionCompleted(e.Text, e.TextAlreadyApplied);
		EventHandler onEnded = (_, _) => _activeImeTextBox?.OnImeCompositionEnded();

		extension.CompositionStarted += onStarted;
		extension.CompositionUpdated += onUpdated;
		extension.CompositionCompleted += onCompleted;
		extension.CompositionEnded += onEnded;

		return Disposable.Create(() =>
		{
			extension.CompositionStarted -= onStarted;
			extension.CompositionUpdated -= onUpdated;
			extension.CompositionCompleted -= onCompleted;
			extension.CompositionEnded -= onEnded;
			_imeExtension = original;
		});
	}
}
