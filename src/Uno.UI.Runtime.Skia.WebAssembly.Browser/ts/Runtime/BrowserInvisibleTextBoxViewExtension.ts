namespace Uno.UI.Runtime.Skia {
	export class BrowserInvisibleTextBoxViewExtension {
		private static _exports: any;
		private static _imeExports: any;
		private static readonly isMacOS = navigator?.platform.toUpperCase().includes('MAC') ?? false;

		// iOS WebKit scrolls the page to reveal the focused editable, on focus and on every keyboard frame
		// change. The page is position:fixed, so that scroll pans the whole canvas and shifts hit-testing by
		// the same amount. Off-screen there is nothing to reveal; the TextBox draws its own caret, selection
		// and grippers, so the input's position and size are unused there.
		// A getter, so this class doesn't depend on the order the runtime scripts are emitted in.
		private static get keepsInputOffscreen(): boolean {
			return BrowserInputHelper.isIOS;
		}
		private static readonly offscreenTop = "-10000px";

		// The InputPane drives the bring-into-view of the focused TextBox; the browser's own scroll-to-focus
		// would only pan the visual viewport (see keepsInputOffscreen).
		private static readonly focusOptions: FocusOptions = { preventScroll: true };
		private static inputElement: HTMLInputElement | HTMLTextAreaElement | null;

		// Issue-1 trailing-click guard state (see installTrailingClickGuard).
		private static swallowNextCanvasClick: boolean;
		private static lastPointerType: string;

		// Set while a managed-initiated blur/detach is in progress so the input's own blur
		// listener doesn't report it back to the FocusManager (that would be redundant and
		// could clear focus on the wrong TextBox during a focus switch). Genuine, browser-
		// initiated blurs happen with this false and ARE reported (see OnNativeBlur).
		private static suppressBlurNotification: boolean;

		// Visual handle of the TextBox that currently owns the shared input, so a stale managed
		// blur from a TextBox that already lost it cannot detach its successor's input.
		private static currentHandle: number = 0;

		// Bumped by focus()/detachCore() to supersede the deferred detach scheduled by blur():
		// a blur immediately followed by a focus is a TextBox-to-TextBox move, and detaching
		// would needlessly dismiss the soft keyboard (see blur).
		private static detachGeneration: number = 0;
		private static isInSelectionChange: boolean;
		private static acceptsReturn: boolean;
		private static isComposing: boolean;
		// Set once the composition changes the text: Gboard marks an existing word as composing (to offer
		// corrections) without editing it, and the TextBox must not treat that word as new composition text.
		private static isCompositionReported: boolean;
		private static compositionText: string = "";
		private static compositionStart: number = 0;
		private static pendingCompositionStart: number = -1;
		// The composition text last reported, so a preedit WebKit removes and puts back unchanged isn't reported again.
		private static reportedText: string | null = null;
		// The commit a compositionend announced before the input event that applies it (see awaitsCommitInput).
		private static pendingCommit: string | null = null;
		// WebKit removed the preedit with an input event of its own; it is reported with what follows (see syncCompositionInput).
		private static preeditRemoved: boolean = false;
		// The TextBox's text, set while the browser owns the value during a composition (see setText).
		private static pendingText: string | null = null;
		// The input's value as last synced with the TextBox, either way. A commit the browser applied without an input
		// event yet leaves the value ahead of it.
		private static syncedValue: string | null = null;
		private static isSyncingFromNative: boolean;
		// The presses of Enter that are down, by the code of their key ("" for an event that has none, as a
		// soft keyboard's), so that each is raised once: those whose keydown was seen as Enter's (the ones
		// BrowserKeyboardInputSource tells the key of are raised by it), and those the line break raised,
		// release included. A press can outlive the input it started on (a handler moving focus off it),
		// so an entry lasts until the keyup of its key, wherever that goes, or its next keydown. A keyup that
		// never comes (the page losing focus while the key is down) leaves the entry until that keydown; a
		// keyup of the key that comes before it, with no keydown of its own, is taken for the lost one.
		private static enterKeyDowns = new Set<string>();
		private static enterLineBreaks = new Set<string>();
		// The code of the last keydown when it can be Enter's: the line break that follows is that key's.
		private static lastKeyDownEnterCode: string = "";
		// Whether BrowserKeyboardInputSource raised the last keydown as Enter's by its key, as it has no code.
		private static lastKeyDownNamedEnter: boolean;
		private static compositionGeneration: number = 0;

		private static waitingAsyncOnSelectionChange: boolean;
		private static waitingSelectionValue: string;
		private static nextSelectionStart: number;
		private static nextSelectionEnd: number;
		private static nextSelectionDirection: "forward" | "backward" | "none";

		// Android soft keyboards report all key events with keyCode 229 ("Unidentified").
		// Text changes are synced via the oninput handler instead.
		private static readonly ANDROID_IME_KEYCODE = 229;

		/**
		 * Stops the browser's own paste and returns the pasted text, or null when the clipboard has no text (an image, for
		 * instance).
		 */
		public static getPastedText(ev: ClipboardEvent): string | null {
			ev.preventDefault();
			return ev.clipboardData?.types.includes("text/plain") ? ev.clipboardData.getData("text/plain") : null;
		}

		public static initialize() {
			if (BrowserInvisibleTextBoxViewExtension._exports == undefined) {
				const browserExports = WebAssemblyWindowWrapper.getAssemblyExports();

				BrowserInvisibleTextBoxViewExtension._exports = browserExports.Uno.UI.Runtime.Skia.BrowserInvisibleTextBoxViewExtension;
				BrowserInvisibleTextBoxViewExtension._imeExports = browserExports.Uno.UI.Runtime.Skia.WasmImeTextBoxExtension;

				BrowserInvisibleTextBoxViewExtension.installTrailingClickGuard();

				document.onselectionchange = () => {
					let input = document.activeElement;
					if (input instanceof HTMLInputElement || input instanceof HTMLTextAreaElement) {
						BrowserInvisibleTextBoxViewExtension.isInSelectionChange = true;
						try {
							const restoresSelection = BrowserInvisibleTextBoxViewExtension.waitingAsyncOnSelectionChange
								&& BrowserInvisibleTextBoxViewExtension.isAssignedValueCaret(input);
							BrowserInvisibleTextBoxViewExtension.waitingAsyncOnSelectionChange = false;
							if (restoresSelection) {
								input.setSelectionRange(BrowserInvisibleTextBoxViewExtension.nextSelectionStart, BrowserInvisibleTextBoxViewExtension.nextSelectionEnd, BrowserInvisibleTextBoxViewExtension.nextSelectionDirection);
							}
							else if (BrowserInvisibleTextBoxViewExtension.isCompositionReported && BrowserInvisibleTextBoxViewExtension.pendingText !== null) {
								// The composition holds text the TextBox refused (MaxLength), reconciled when it ends: the input's
								// selection is in that text, not in the TextBox's.
							}
							else {
								if (input.selectionDirection == "backward") {
									BrowserInvisibleTextBoxViewExtension._exports.OnSelectionChanged(input.selectionEnd, input.selectionStart - input.selectionEnd);
								} else {
									BrowserInvisibleTextBoxViewExtension._exports.OnSelectionChanged(input.selectionStart, input.selectionEnd - input.selectionStart);
								}
							}
						}
						finally {
							// Left set, it would keep every later caret move from reaching the input.
							BrowserInvisibleTextBoxViewExtension.isInSelectionChange = false;
						}
					}
				}
			}
		}

		// Assigning the value moves the caret to its end, which the pending restore undoes. Chrome fires no
		// selectionchange for that, so the restore can still be pending when the user or the IME moves on.
		private static isAssignedValueCaret(input: HTMLInputElement | HTMLTextAreaElement) {
			const end = input.value.length;
			return input.value === BrowserInvisibleTextBoxViewExtension.waitingSelectionValue
				&& input.selectionStart === end
				&& input.selectionEnd === end;
		}

		// A modified deletion (Shift+Delete cuts in Chromium) keeps going through the TextBox.
		private static isSoftKeyboardDeletion(ev: KeyboardEvent) {
			return ev.code === ""
				&& (ev.key === "Backspace" || ev.key === "Delete")
				&& !ev.ctrlKey && !ev.altKey && !ev.metaKey && !ev.shiftKey;
		}

		// Neutralizes the touch-only, WebKit-internal race that dismisses the soft keyboard right
		// after a TextBox tap (see swallowNextCanvasClick). Uno already preventDefaults pointer
		// events, but per the Pointer Events spec that does NOT suppress the compatibility
		// mouse events touch browsers synthesize, so the trailing mousedown still reaches the
		// canvas and blurs #uno-input. We can't preventDefault it from the pointer path, so we
		// intercept the mouse event itself, scoped to a touch/pen tap that focused the input or landed
		// while it was focused.
		private static installTrailingClickGuard() {
			// Every new pointer gesture re-decides the flag, so only the mouse events synthesized from
			// a tap the guard covers are ever swallowed. Also records the pointer
			// type: the bug is exclusive to touch/pen, where the compat mousedown is deferred until
			// after focus. With a mouse, mousedown precedes focus, so there is nothing to guard.
			document.addEventListener("pointerdown", (ev: PointerEvent) => {
				BrowserInvisibleTextBoxViewExtension.lastPointerType = ev.pointerType;
				// A touch while the input already has focus (a tap that moves the caret in the focused TextBox)
				// gets the same deferred compat mousedown, which blurs the input and dismisses the keyboard, so
				// every other tap would close it. No focusin fires to arm the guard for that tap: arm it here.
				// A tap that moves focus elsewhere is still honored, since managed code blurs the input itself.
				BrowserInvisibleTextBoxViewExtension.swallowNextCanvasClick =
					(ev.pointerType === "touch" || ev.pointerType === "pen")
					&& document.activeElement?.id === UnoDomIds.input;
			}, { capture: true });

			// focusin bubbles (unlike focus), so a single document-level listener catches the
			// invisible input regardless of when it is (re)created.
			document.addEventListener("focusin", (ev: FocusEvent) => {
				const target = ev.target as HTMLElement;
				if (target?.id === UnoDomIds.input
					&& (BrowserInvisibleTextBoxViewExtension.lastPointerType === "touch"
						|| BrowserInvisibleTextBoxViewExtension.lastPointerType === "pen")) {
					BrowserInvisibleTextBoxViewExtension.swallowNextCanvasClick = true;
				}
			}, { capture: true });

			const swallow = (ev: Event) => {
				if (BrowserInvisibleTextBoxViewExtension.swallowNextCanvasClick
					&& (ev.target as HTMLElement)?.id === UnoDomIds.canvas) {
					ev.preventDefault();
					ev.stopImmediatePropagation();
					BrowserInvisibleTextBoxViewExtension.swallowNextCanvasClick = false;
				}
			};
			document.addEventListener("mousedown", swallow, { capture: true });
			document.addEventListener("click", swallow, { capture: true });
		}

		private static createInput(isPasswordBox: boolean, text: string, acceptsReturn: boolean, inputMode: string, enterKeyHint: string, isReadOnly: boolean) {
			BrowserInvisibleTextBoxViewExtension.acceptsReturn = acceptsReturn;
			// A previous input may have been removed mid-composition without a compositionend;
			// never carry that state over to a fresh element.
			BrowserInvisibleTextBoxViewExtension.resetComposition();
			const input = document.createElement(acceptsReturn && !isPasswordBox ? "textarea" : "input");
			// The keydown/keyup handlers capture acceptsReturn by closure; record it so canRetarget
			// only reuses the element when the captured behavior still matches.
			(input as any).__unoAcceptsReturn = acceptsReturn;
			if (isPasswordBox) {
				(input as HTMLInputElement).type = "password";
				input.autocomplete = "password";
			}

			input.id = UnoDomIds.input;
			input.tabIndex = -1;
			input.spellcheck = false;
			input.style.whiteSpace = "pre-wrap";
			input.style.position = "absolute";
			input.style.padding = "0px";
			input.style.opacity = "0";
			input.style.color = "transparent";
			input.style.background = "transparent";
			input.style.caretColor = "transparent";
			input.style.outline = "none";
			input.style.border = "none";
			input.style.resize = "none";
			input.style.textShadow = "none";
			input.style.overflow = "hidden";
			input.style.pointerEvents = "none";
			input.style.zIndex = "99";
			// Placed before focus() so WebKit never sees an on-screen rect to reveal. Only top goes off-screen:
			// a negative left would be scrollable overflow in a right-to-left document.
			input.style.top = BrowserInvisibleTextBoxViewExtension.keepsInputOffscreen ? BrowserInvisibleTextBoxViewExtension.offscreenTop : "0px";
			input.style.left = "0px";
			input.value = text;
			input.readOnly = isReadOnly;

			input.setAttribute("inputmode", inputMode);
			input.setAttribute("enterkeyhint", enterKeyHint);

			let activeCompositionGeneration = 0;
			// A replaced input, or a composition superseded by invalidateComposition, must not reach the TextBox that
			// now owns the shared input.
			const isActiveComposition = () => input === BrowserInvisibleTextBoxViewExtension.inputElement
				&& activeCompositionGeneration === BrowserInvisibleTextBoxViewExtension.compositionGeneration;

			// The input's value is the source of truth for the text, composition included: browsers apply the
			// composition to the value themselves, and event order around compositionend varies by browser.
			input.oninput = ev => {
				let input = ev.target as HTMLInputElement;
				if (input !== BrowserInvisibleTextBoxViewExtension.inputElement) {
					// A replaced input would push its text into the TextBox that now owns the shared input.
					return;
				}
				BrowserInvisibleTextBoxViewExtension.runSyncingFromNative(() => {
					if (BrowserInvisibleTextBoxViewExtension.isComposing && isActiveComposition()) {
						BrowserInvisibleTextBoxViewExtension.syncCompositionInput(input, ev as InputEvent, isActiveComposition);
					} else {
						BrowserInvisibleTextBoxViewExtension.syncTextFromInput(input);
					}
				});
			};

			input.onpaste = ev => BrowserInvisibleTextBoxViewExtension._exports.OnNativePaste(BrowserInvisibleTextBoxViewExtension.getPastedText(ev));

			// C# drives focus one-way (StartEntry/EndEntry call focus()/blur()), so a blur the
			// browser initiates on its own — e.g. tapping outside, or the touch-synthesized
			// mousedown in issue 1 — is otherwise invisible to the FocusManager and LostFocus
			// never fires. Report only those; managed-initiated blurs set suppressBlurNotification.
			input.addEventListener("blur", () => {
				if (BrowserInvisibleTextBoxViewExtension.suppressBlurNotification) {
					return;
				}
				BrowserInvisibleTextBoxViewExtension._exports.OnNativeBlur();
			});

			// Handle Enter key from Android virtual keyboards which don't fire keydown events.
			// Android keyboards typically fire beforeinput with inputType "insertLineBreak" or "insertParagraph" instead.
			input.addEventListener("beforeinput", (ev: InputEvent) => {
				if ((ev.inputType === "insertLineBreak" || ev.inputType === "insertParagraph") && !BrowserInvisibleTextBoxViewExtension.acceptsReturn) {
					ev.preventDefault();

					// The line break the browser sets out to insert for a keydown BrowserKeyboardInputSource
					// raised is not another press of the key.
					const enterCode = BrowserInvisibleTextBoxViewExtension.lastKeyDownEnterCode;
					const raisedByKeyDown = enterCode === ""
						? BrowserInvisibleTextBoxViewExtension.lastKeyDownNamedEnter
						: BrowserInvisibleTextBoxViewExtension.enterKeyDowns.has(enterCode);
					BrowserInvisibleTextBoxViewExtension.lastKeyDownEnterCode = "";
					BrowserInvisibleTextBoxViewExtension.lastKeyDownNamedEnter = false;
					if (!raisedByKeyDown) {
						BrowserInvisibleTextBoxViewExtension.enterLineBreaks.add(enterCode);
						BrowserInvisibleTextBoxViewExtension._exports.OnEnterKeyPressed();
					}
				}
			});

			BrowserInvisibleTextBoxViewExtension.attachTextInputKeyHandlers(input, acceptsReturn);

			input.addEventListener("compositionstart", () => {
				if (input !== BrowserInvisibleTextBoxViewExtension.inputElement) {
					return;
				}
				const announcedCommit = BrowserInvisibleTextBoxViewExtension.pendingCommit;
				if (announcedCommit !== null && isActiveComposition()) {
					// The commit a compositionend announced never came: that composition ends as it would have there.
					BrowserInvisibleTextBoxViewExtension.runSyncingFromNative(() => BrowserInvisibleTextBoxViewExtension.completeComposition(input, announcedCommit));
					if (input !== BrowserInvisibleTextBoxViewExtension.inputElement) {
						return;
					}
				}
				activeCompositionGeneration = ++BrowserInvisibleTextBoxViewExtension.compositionGeneration;
				BrowserInvisibleTextBoxViewExtension.resetComposition();
				// The IME now drives the selection; a restore pending from an earlier value assignment is stale.
				BrowserInvisibleTextBoxViewExtension.waitingAsyncOnSelectionChange = false;
				BrowserInvisibleTextBoxViewExtension.isComposing = true;
				BrowserInvisibleTextBoxViewExtension.compositionStart = input.selectionStart ?? 0;
				BrowserInvisibleTextBoxViewExtension.syncedValue = input.value;
			});

			input.addEventListener("compositionupdate", (ev: CompositionEvent) => {
				if (!isActiveComposition()) {
					return;
				}
				BrowserInvisibleTextBoxViewExtension.compositionText = ev.data ?? "";
				// Chromium selects the range the composition replaces before dispatching this event, which locates a
				// re-composed word; reportComposition validates it against the applied text.
				if (input.selectionStart !== null && input.selectionEnd !== null) {
					BrowserInvisibleTextBoxViewExtension.pendingCompositionStart = Math.min(input.selectionStart, input.selectionEnd);
				}
			});

			input.addEventListener("compositionend", (ev: CompositionEvent) => {
				if (!isActiveComposition()) {
					return;
				}
				BrowserInvisibleTextBoxViewExtension.runSyncingFromNative(() => {
					const committed = ev.data ?? "";
					const removed = BrowserInvisibleTextBoxViewExtension.preeditRemoved;
					// Firefox dispatches the commit's input event after compositionend. The committed value still belongs
					// to the composition: the TextBox ends it with its final text, and RichEditBox keeps one undo step.
					// A preedit WebKit removed on its way to putting a commit in place waits for that commit instead.
					if (input.value !== BrowserInvisibleTextBoxViewExtension.syncedValue && !(removed && committed.length > 0)) {
						if (removed) {
							BrowserInvisibleTextBoxViewExtension.preeditRemoved = false;
							BrowserInvisibleTextBoxViewExtension.compositionText = "";
						}
						BrowserInvisibleTextBoxViewExtension.syncCompositionFromInput(input);
						if (!isActiveComposition()) {
							return;
						}
					}

					if (BrowserInvisibleTextBoxViewExtension.isCompositionReported && BrowserInvisibleTextBoxViewExtension.awaitsCommitInput(input, committed, removed)) {
						BrowserInvisibleTextBoxViewExtension.compositionText = committed;
						BrowserInvisibleTextBoxViewExtension.pendingCommit = committed;
						// The input event that applies the commit comes in the same task as compositionend, if at all. Blink
						// cancels a composition whose text is selected with a compositionend alone, keeping the text: the
						// composition then ends once this task is over, as it would have at compositionend, with a preedit
						// removed in between synced first.
						const generation = BrowserInvisibleTextBoxViewExtension.compositionGeneration;
						setTimeout(() => {
							if (BrowserInvisibleTextBoxViewExtension.pendingCommit !== committed
								|| !BrowserInvisibleTextBoxViewExtension.isSyncTarget(input, generation)) {
								return;
							}
							BrowserInvisibleTextBoxViewExtension.runSyncingFromNative(() => {
								let commit = committed;
								if (input.value !== BrowserInvisibleTextBoxViewExtension.syncedValue) {
									// The preedit was removed and nothing put in its place: the composition was cancelled.
									commit = "";
									BrowserInvisibleTextBoxViewExtension.preeditRemoved = false;
									BrowserInvisibleTextBoxViewExtension.compositionText = "";
									BrowserInvisibleTextBoxViewExtension.syncCompositionFromInput(input);
									if (!BrowserInvisibleTextBoxViewExtension.isSyncTarget(input, generation)) {
										return;
									}
								}
								BrowserInvisibleTextBoxViewExtension.completeComposition(input, commit);
							});
						}, 0);
						return;
					}
					BrowserInvisibleTextBoxViewExtension.completeComposition(input, committed);
				});
			});

			document.body.appendChild(input);
			BrowserInvisibleTextBoxViewExtension.inputElement = input;
		}

		// Some WebKit orders (not current WebKit's) deliver compositionend before the input event that applies the commit:
		// with the preedit still in the input, selected for the commit (or a cancel) to replace, or with the preedit just
		// removed. The composition then ends with that input event, so TextCompositionEnded comes after the text change, as
		// on WinUI. A commit no input event will apply still ends at compositionend.
		private static awaitsCommitInput(input: HTMLInputElement | HTMLTextAreaElement, committed: string, removed: boolean): boolean {
			if (removed) {
				return committed.length > 0;
			}
			const preedit = BrowserInvisibleTextBoxViewExtension.compositionText;
			const start = BrowserInvisibleTextBoxViewExtension.compositionStart;
			return preedit.length > 0
				&& committed !== preedit
				&& input.selectionStart === start
				&& input.selectionEnd === start + preedit.length;
		}

		// WebKit commits through input events of its own, with no compositionupdate for the committed text: the preedit
		// is removed (deleteCompositionText), then the committed text put in place (insertFromComposition), then
		// compositionend. The removal is reported with the insertion, so handlers don't see the composition emptied and
		// refilled; when compositionend comes next instead, the composition was emptied, and once compositionend
		// announced a cancel, the removal is all that comes.
		private static syncCompositionInput(input: HTMLInputElement | HTMLTextAreaElement, ev: InputEvent, isActiveComposition: () => boolean) {
			const inputType = ev.inputType;
			const pendingCommit = BrowserInvisibleTextBoxViewExtension.pendingCommit;
			if (inputType === "deleteCompositionText" && BrowserInvisibleTextBoxViewExtension.isCompositionReported && pendingCommit !== "") {
				BrowserInvisibleTextBoxViewExtension.preeditRemoved = true;
				return;
			}

			if (pendingCommit !== null && !ev.isComposing
				&& inputType !== "insertCompositionText" && inputType !== "insertFromComposition" && inputType !== "deleteCompositionText") {
				// The commit compositionend announced never came, and this is the next keystroke: the composition ends as it
				// would have there, and the keystroke is a plain edit.
				const generation = BrowserInvisibleTextBoxViewExtension.compositionGeneration;
				BrowserInvisibleTextBoxViewExtension.completeComposition(input, pendingCommit);
				if (BrowserInvisibleTextBoxViewExtension.isSyncTarget(input, generation)) {
					BrowserInvisibleTextBoxViewExtension.syncTextFromInput(input);
				}
				return;
			}

			const removed = BrowserInvisibleTextBoxViewExtension.preeditRemoved;
			BrowserInvisibleTextBoxViewExtension.preeditRemoved = false;
			if (inputType === "insertFromComposition" && ev.data) {
				BrowserInvisibleTextBoxViewExtension.compositionText = ev.data;
			}
			if (!removed
				|| input.value !== BrowserInvisibleTextBoxViewExtension.syncedValue
				|| BrowserInvisibleTextBoxViewExtension.compositionText !== BrowserInvisibleTextBoxViewExtension.reportedText) {
				BrowserInvisibleTextBoxViewExtension.syncCompositionFromInput(input);
			}

			if (pendingCommit !== null && isActiveComposition()) {
				BrowserInvisibleTextBoxViewExtension.completeComposition(input, BrowserInvisibleTextBoxViewExtension.compositionText);
			}
		}

		// Ends the composition with its commit; an empty one cancels it.
		private static completeComposition(input: HTMLInputElement | HTMLTextAreaElement, committed: string) {
			const generation = BrowserInvisibleTextBoxViewExtension.compositionGeneration;
			const wasReported = BrowserInvisibleTextBoxViewExtension.isCompositionReported;
			const textDiffers = BrowserInvisibleTextBoxViewExtension.pendingText !== null;
			BrowserInvisibleTextBoxViewExtension.resetComposition();
			if (wasReported) {
				if (textDiffers) {
					// The TextBox holds other text than the commit: the composition ends with its range in that text.
					BrowserInvisibleTextBoxViewExtension._imeExports.OnCompositionEnded();
				} else if (committed.length > 0) {
					BrowserInvisibleTextBoxViewExtension._imeExports.OnCompositionCompleted(committed);
				} else {
					BrowserInvisibleTextBoxViewExtension._imeExports.OnCompositionEnded();
				}
			}
			if (textDiffers && BrowserInvisibleTextBoxViewExtension.isSyncTarget(input, generation)) {
				// The TextBox re-applies its same-length edits (a casing filter) to the committed value and pushes
				// the result to the input.
				BrowserInvisibleTextBoxViewExtension.syncTextFromInput(input);
			}
		}

		// Tells the TextBox which part of the input's value is being composed. The composition is applied to the
		// value by the browser and synced as text, so this only places the composition range (and its underline).
		private static reportComposition(input: HTMLInputElement | HTMLTextAreaElement) {
			const text = BrowserInvisibleTextBoxViewExtension.compositionText;
			if (!BrowserInvisibleTextBoxViewExtension.isCompositionReported && text.length === 0) {
				return;
			}

			const value = input.value;
			const caret = input.selectionStart ?? value.length;
			const pending = BrowserInvisibleTextBoxViewExtension.pendingCompositionStart;
			BrowserInvisibleTextBoxViewExtension.pendingCompositionStart = -1;
			const known = BrowserInvisibleTextBoxViewExtension.compositionStart;
			// Outside Chromium the selection may not mark the replaced range, and repeated text could match it, so a
			// composition keeps the start it was first reported at.
			const candidates = BrowserInvisibleTextBoxViewExtension.isCompositionReported
				? [known, pending, caret - text.length]
				: [pending, known, caret - text.length];
			const start = candidates.find(s => s >= 0 && value.startsWith(text, s)) ?? Math.min(known, value.length);
			BrowserInvisibleTextBoxViewExtension.compositionStart = start;

			if (!BrowserInvisibleTextBoxViewExtension.isCompositionReported) {
				BrowserInvisibleTextBoxViewExtension.isCompositionReported = true;
				BrowserInvisibleTextBoxViewExtension._imeExports.OnCompositionStarted();
			}
			const cursorPosition = Math.max(0, Math.min(caret - start, text.length));
			BrowserInvisibleTextBoxViewExtension.reportedText = text;
			BrowserInvisibleTextBoxViewExtension._imeExports.OnCompositionUpdated(text, cursorPosition, start);
		}

		// Marks a TextBox update made in response to the input, whose echo back to the input must not be applied.
		private static runSyncingFromNative(action: () => void) {
			const wasSyncing = BrowserInvisibleTextBoxViewExtension.isSyncingFromNative;
			BrowserInvisibleTextBoxViewExtension.isSyncingFromNative = true;
			try {
				action();
			} finally {
				BrowserInvisibleTextBoxViewExtension.isSyncingFromNative = wasSyncing;
			}
		}

		private static syncCompositionFromInput(input: HTMLInputElement | HTMLTextAreaElement) {
			const generation = BrowserInvisibleTextBoxViewExtension.compositionGeneration;
			// Before the text sync, so the TextBox knows the change belongs to the composition.
			BrowserInvisibleTextBoxViewExtension.reportComposition(input);
			if (BrowserInvisibleTextBoxViewExtension.isSyncTarget(input, generation)) {
				BrowserInvisibleTextBoxViewExtension.syncTextFromInput(input);
			}
		}

		// A handler of a composition event (TextCompositionStarted, TextCompositionEnded) may have moved focus, replacing the
		// input or handing it to another TextBox, which ends the composition: the input's value isn't that TextBox's.
		private static isSyncTarget(input: HTMLInputElement | HTMLTextAreaElement, generation: number) {
			return input === BrowserInvisibleTextBoxViewExtension.inputElement && generation === BrowserInvisibleTextBoxViewExtension.compositionGeneration;
		}

		private static syncTextFromInput(input: HTMLInputElement | HTMLTextAreaElement) {
			BrowserInvisibleTextBoxViewExtension.syncedValue = input.value;
			if (input.selectionDirection == "backward") {
				BrowserInvisibleTextBoxViewExtension._exports.OnInputTextChanged(input.value, input.selectionEnd, input.selectionStart - input.selectionEnd);
			} else {
				BrowserInvisibleTextBoxViewExtension._exports.OnInputTextChanged(input.value, input.selectionStart, input.selectionEnd - input.selectionStart);
			}
		}

		// Every composition field except compositionGeneration, which superseded composition events are checked against.
		private static resetComposition() {
			BrowserInvisibleTextBoxViewExtension.isComposing = false;
			BrowserInvisibleTextBoxViewExtension.isCompositionReported = false;
			BrowserInvisibleTextBoxViewExtension.compositionText = "";
			BrowserInvisibleTextBoxViewExtension.pendingCompositionStart = -1;
			BrowserInvisibleTextBoxViewExtension.pendingText = null;
			BrowserInvisibleTextBoxViewExtension.reportedText = null;
			BrowserInvisibleTextBoxViewExtension.pendingCommit = null;
			BrowserInvisibleTextBoxViewExtension.preeditRemoved = false;
		}

		// A textarea normalizes line breaks to \n, and an input strips them.
		private static asInputValue(input: HTMLInputElement | HTMLTextAreaElement, text: string) {
			return input instanceof HTMLTextAreaElement ? text.replace(/\r\n?/g, "\n") : text.replace(/[\r\n]/g, "");
		}

		private static isEnterKeyEvent(ev: KeyboardEvent): boolean {
			return ev.key === "Enter" || ev.keyCode === 13 || ev.code === "Enter" || ev.code === "NumpadEnter";
		}

		// The code BrowserKeyboardInputSource tells Enter by, or "" for an event that has none of them.
		private static enterCodeOf(ev: KeyboardEvent): string {
			return ev.code === "Enter" || ev.code === "NumpadEnter" ? ev.code : "";
		}

		// Whether BrowserKeyboardInputSource raises the event as Enter's: by its code, or by its key when it has no
		// code (a soft keyboard's Enter).
		private static isEnterForKeyboardSource(ev: KeyboardEvent): boolean {
			return BrowserInvisibleTextBoxViewExtension.enterCodeOf(ev) !== "" || (ev.code === "" && ev.key === "Enter");
		}

		// Called by BrowserKeyboardInputSource for a keydown that reached the document. It raises the one
		// it tells Enter by, so the press is recorded here too: it may have started off the
		// TextBox inputs, and whatever an earlier press of the key left behind is over.
		public static onKeyDownAtDocument(ev: KeyboardEvent) {
			const enterCode = BrowserInvisibleTextBoxViewExtension.enterCodeOf(ev);
			BrowserInvisibleTextBoxViewExtension.lastKeyDownEnterCode = enterCode;
			BrowserInvisibleTextBoxViewExtension.lastKeyDownNamedEnter = enterCode === "" && ev.key === "Enter";
			if (BrowserInvisibleTextBoxViewExtension.isEnterForKeyboardSource(ev)) {
				BrowserInvisibleTextBoxViewExtension.enterLineBreaks.delete(enterCode);
				BrowserInvisibleTextBoxViewExtension.enterKeyDowns.add(enterCode);
			}
		}

		// Called by BrowserKeyboardInputSource for a keyup that reached the document: ends the press of
		// Enter, and returns whether its release was raised along with the press, in which case it must
		// not be raised again. The input a press started on may be gone by its keyup, which then does
		// not pass the input's handler.
		public static onKeyUpAtDocument(ev: KeyboardEvent): boolean {
			if (!BrowserInvisibleTextBoxViewExtension.isEnterKeyEvent(ev)) {
				return false;
			}

			const enterCode = BrowserInvisibleTextBoxViewExtension.enterCodeOf(ev);
			const raised = BrowserInvisibleTextBoxViewExtension.isEnterForKeyboardSource(ev)
				&& BrowserInvisibleTextBoxViewExtension.enterLineBreaks.has(enterCode);
			BrowserInvisibleTextBoxViewExtension.endEnterPress(enterCode);
			return raised;
		}

		// The key that has this code is released: its press is over, and a line break that comes after
		// this does not belong to its keydown.
		private static endEnterPress(enterCode: string) {
			BrowserInvisibleTextBoxViewExtension.enterKeyDowns.delete(enterCode);
			BrowserInvisibleTextBoxViewExtension.enterLineBreaks.delete(enterCode);
			if (BrowserInvisibleTextBoxViewExtension.lastKeyDownEnterCode === enterCode) {
				BrowserInvisibleTextBoxViewExtension.lastKeyDownEnterCode = "";
				BrowserInvisibleTextBoxViewExtension.lastKeyDownNamedEnter = false;
			}
		}

		// Applies the same keydown/keyup guards used on the invisible <input> to any text input
		// that must delegate character insertion to managed TextBox KeyDown handling.
		// Without these guards, focused text inputs (e.g. the a11y semantic <input>) would insert
		// the character natively AND via the managed path, producing duplicated input.
		public static attachTextInputKeyHandlers(input: HTMLInputElement | HTMLTextAreaElement, acceptsReturn: boolean) {
			input.addEventListener("keydown", (ev: KeyboardEvent) => {
				// A key press starts here, and the line break that may follow belongs to this keydown.
				// Whatever an earlier press of the same key left behind is over. The keydown of another key
				// leaves an Enter that is still down alone. A keydown without a code may be a soft
				// keyboard's Enter, so it counts as a press of it, unless it names another key.
				const enterCode = BrowserInvisibleTextBoxViewExtension.enterCodeOf(ev);
				BrowserInvisibleTextBoxViewExtension.lastKeyDownEnterCode = enterCode;
				BrowserInvisibleTextBoxViewExtension.lastKeyDownNamedEnter = false;
				const hasCode = ev.code !== "" && ev.code !== "Unidentified";
				const namesKey = ev.key !== "" && ev.key !== "Unidentified" && ev.key !== "Process";
				const isOtherKey = (hasCode || namesKey) && enterCode === ""
					&& ev.key !== "Enter" && ev.keyCode !== 13;
				if (!isOtherKey) {
					BrowserInvisibleTextBoxViewExtension.enterKeyDowns.delete(enterCode);
					BrowserInvisibleTextBoxViewExtension.enterLineBreaks.delete(enterCode);
				}

				// During IME composition, let the browser/IME handle all keys.
				// stopPropagation prevents BrowserKeyboardInputSource from calling preventDefault.
				if (ev.isComposing) {
					ev.stopPropagation();
					return;
				}

				if (ev.ctrlKey || (ev.metaKey && BrowserInvisibleTextBoxViewExtension.isMacOS)) {
					// Due to browser security considerations, we need to let the clipboard operations be handled natively.
					// So, we do stopPropagation instead of preventDefault
					if (ev.key == "c" || ev.key == "C" || ev.key == "v" || ev.key == "V" || ev.key == "x" || ev.key == "X") {
						ev.stopPropagation();
						return;
					}
				}

				// Allow Enter key to propagate when the TextBox doesn't accept returns
				// Desktop/iOS path: keydown is the reliable signal; let it bubble to document so
				// BrowserKeyboardInputSource raises the managed KeyDown. Recording the keydown prevents
				// the keyup branch below from dispatching a duplicate OnEnterKeyPressed.
				// This enables focus navigation (e.g., Uno.Toolkit's AutoFocusNext) on mobile browsers
				if ((ev.key === "Enter" || ev.keyCode === 13) && !acceptsReturn) {
					// Don't call preventDefault() to allow the key event to propagate to document listeners
					BrowserInvisibleTextBoxViewExtension.enterKeyDowns.add(enterCode);
					return;
				}

				// Android soft keyboards fire all keys as keyCode 229 / key "Unidentified".
				// The C# side cannot identify these (maps to VirtualKey.None), so let the browser
				// handle them natively. Text changes sync via the oninput handler.
				// stopPropagation prevents the document-level BrowserKeyboardInputSource from
				// calling preventDefault() on the event.
				if (ev.keyCode === BrowserInvisibleTextBoxViewExtension.ANDROID_IME_KEYCODE) {
					ev.stopPropagation();
					return;
				}

				// Some soft keyboards (Samsung Keyboard) send Backspace and Delete as keys without a physical code. The
				// browser applies them too: an edit made by the TextBox would leave the keyboard's own copy of the text
				// stale, and its next suggestion would replace the wrong range.
				if (BrowserInvisibleTextBoxViewExtension.isSoftKeyboardDeletion(ev)) {
					ev.stopPropagation();
					return;
				}

				ev.preventDefault();
			});

			input.addEventListener("keyup", (ev: KeyboardEvent) => {
				// The line break raised the key, its release included. BrowserKeyboardInputSource raises the
				// release of a keyup it tells the key of.
				const enterCode = BrowserInvisibleTextBoxViewExtension.enterCodeOf(ev);
				const raisedByLineBreak = BrowserInvisibleTextBoxViewExtension.enterLineBreaks.has(enterCode);
				if (raisedByLineBreak && BrowserInvisibleTextBoxViewExtension.isEnterForKeyboardSource(ev)) {
					// Released along with the line break's press: not to be raised again from the document.
					ev.stopPropagation();
				}

				// Android virtual keyboards (Gboard/SwiftKey/Samsung/AOSP) report keydown
				// with keyCode 229 ("Unidentified") for Enter, which is stopPropagation'd
				// in the keydown handler so it never reaches BrowserKeyboardInputSource. They DO report keyup
				// with key === "Enter" though - use that to raise the managed KeyDown so
				// focus-navigation patterns (Uno.Toolkit AutoFocusNext, FocusManager) work
				// on Android browsers. The recorded keydown guards against double-dispatch on desktop/iOS,
				// where the keydown branch already routed Enter through the document listener.
				if (!acceptsReturn
					&& ev.key === "Enter"
					&& !BrowserInvisibleTextBoxViewExtension.enterKeyDowns.has(enterCode)
					&& !raisedByLineBreak
					&& !ev.isComposing) {
					ev.preventDefault();
					// OnEnterKeyPressed raises the release along with the press: not to be raised again from the document.
					ev.stopPropagation();
					BrowserInvisibleTextBoxViewExtension._exports.OnEnterKeyPressed();
				}

				if (BrowserInvisibleTextBoxViewExtension.isEnterKeyEvent(ev)) {
					BrowserInvisibleTextBoxViewExtension.endEnterPress(enterCode);
				}

				if (BrowserInvisibleTextBoxViewExtension.isComposing
					|| ev.keyCode === BrowserInvisibleTextBoxViewExtension.ANDROID_IME_KEYCODE
					|| BrowserInvisibleTextBoxViewExtension.isSoftKeyboardDeletion(ev)) {
					ev.stopPropagation();
				}
			});
		}

		public static setEnterKeyHint(enterKeyHint: string) {
			const input = BrowserInvisibleTextBoxViewExtension.inputElement;
			if (input) {
				input.setAttribute("enterkeyhint", enterKeyHint);
			}
		}

		// A read-only input still takes focus, a caret, a selection, key events and copy, which the TextBox needs; it
		// only refuses edits, so mobile browsers don't open the soft keyboard for it.
		public static setReadOnly(isReadOnly: boolean) {
			const input = BrowserInvisibleTextBoxViewExtension.inputElement;
			if (input) {
				input.readOnly = isReadOnly;
			}
		}

		public static setInputMode(inputMode: string) {
			const input = BrowserInvisibleTextBoxViewExtension.inputElement;
			if (input) {
				input.setAttribute("inputmode", inputMode);
			}
		}

		public static focus(handle: number, isPassword: boolean, text: string, acceptsReturn: boolean, inputMode: string, enterKeyHint: string, isReadOnly: boolean): boolean {
			// Supersede any detach a preceding managed blur scheduled: focus is moving between
			// TextBoxes, and detaching in between would dismiss the soft keyboard (see blur).
			BrowserInvisibleTextBoxViewExtension.detachGeneration++;

			const semanticElement = document.getElementById(`uno-semantics-${handle}`);
			if (semanticElement && document.activeElement === semanticElement) {
				BrowserInvisibleTextBoxViewExtension.detach();
				return false;
			}

			const existingInput = BrowserInvisibleTextBoxViewExtension.inputElement;
			const wasComposing = BrowserInvisibleTextBoxViewExtension.isCompositionReported;
			if (existingInput != null && BrowserInvisibleTextBoxViewExtension.canRetarget(existingInput, isPassword, acceptsReturn, isReadOnly)) {
				// Reuse the shared input in place: mobile browsers keep the soft keyboard up across
				// a TextBox-to-TextBox move only while an editable element stays focused throughout.
				BrowserInvisibleTextBoxViewExtension.acceptsReturn = acceptsReturn;
				existingInput.setAttribute("inputmode", inputMode);
				existingInput.setAttribute("enterkeyhint", enterKeyHint);
				BrowserInvisibleTextBoxViewExtension.setText(text);

				// It's necessary to actually focus the native input, not just make it visible. This is particularly
				// important to mobile browsers (to open the software keyboard) and for assistive technology to not steal
				// events and properly recognize password inputs to not read it.
				if (document.activeElement !== existingInput) {
					existingInput.focus(BrowserInvisibleTextBoxViewExtension.focusOptions);
				}
			}
			else {
				// The element kind must change (input/textarea/password), or an IME composition is in
				// progress and must not leak into the next TextBox. Focus the new element BEFORE
				// removing the old one so focus hands off editable-to-editable without a gap that
				// would dismiss the soft keyboard. The implicit blur of the old element is
				// managed-initiated, so suppress its notification.
				existingInput?.removeAttribute("id");
				this.createInput(isPassword, text, acceptsReturn, inputMode, enterKeyHint, isReadOnly);
				BrowserInvisibleTextBoxViewExtension.runSuppressingBlur(() => {
					BrowserInvisibleTextBoxViewExtension.inputElement.focus(BrowserInvisibleTextBoxViewExtension.focusOptions);
					existingInput?.remove();
				});
			}

			BrowserInvisibleTextBoxViewExtension.currentHandle = Number(handle);

			// The replaced input's compositionend is ignored (see isActiveComposition), so a composition still open on it
			// ends here: the same TextBox re-entered (a tap changing its focus state mid-word) would otherwise stay composing.
			if (wasComposing) {
				BrowserInvisibleTextBoxViewExtension._imeExports.OnCompositionEnded();
			}

			// Set for whichever element ends up live: the shared input is reused across TextBoxes, so tagging
			// it only on creation leaves the policy unreported for every retargeted entry session. Inspectable
			// from the Web Inspector when diagnosing keyboard or IME placement reports.
			BrowserInvisibleTextBoxViewExtension.inputElement.dataset.unoPlacement =
				BrowserInvisibleTextBoxViewExtension.keepsInputOffscreen ? "offscreen" : "tracking";

			// The retarget path keeps the input focused, so no focusin fires and the trailing-click
			// guard never arms; arm it here for both paths (see installTrailingClickGuard).
			if (BrowserInvisibleTextBoxViewExtension.lastPointerType === "touch"
				|| BrowserInvisibleTextBoxViewExtension.lastPointerType === "pen") {
				BrowserInvisibleTextBoxViewExtension.swallowNextCanvasClick = true;
			}
			return true;
		}

		// The shared input can be handed to another TextBox without being recreated only when the
		// element kind it was created with still matches the target TextBox.
		private static canRetarget(input: HTMLInputElement | HTMLTextAreaElement, isPassword: boolean, acceptsReturn: boolean, isReadOnly: boolean): boolean {
			// Mobile browsers show or hide the soft keyboard when an element takes focus, not when readOnly flips on the
			// focused one: moving between a read-only and an editable TextBox focuses a new input.
			if (BrowserInvisibleTextBoxViewExtension.isComposing || input.readOnly !== isReadOnly) {
				return false;
			}
			const needsTextArea = acceptsReturn && !isPassword;
			if (input instanceof HTMLTextAreaElement) {
				return needsTextArea;
			}
			return !needsTextArea
				&& (input.type === "password") === isPassword
				&& (input as any).__unoAcceptsReturn === acceptsReturn;
		}

		// Runs a managed-initiated focus mutation with the blur listener muted, so it isn't
		// reported back to the FocusManager (which already drove the change). Callers make the
		// blur dispatch synchronously inside the window: detachCore blurs explicitly before
		// removing, and the swap path in focus() focuses the successor (implicitly blurring the
		// old input) before removing it.
		private static runSuppressingBlur(action: () => void) {
			BrowserInvisibleTextBoxViewExtension.suppressBlurNotification = true;
			try {
				action();
			} finally {
				BrowserInvisibleTextBoxViewExtension.suppressBlurNotification = false;
			}
		}

		// Called when a TextBox takes focus and when, having it already, it is tapped.
		// By default iOS only shows the soft keyboard for an input that is focused while the user is
		// interacting with the page. An input focused from code at another time, e.g. for a dialog that
		// opens on a TextBox, has none, and a tap on its TextBox would not bring one up: the tap lands on
		// the canvas, and nothing focuses an input that has focus already. Focusing it again from inside
		// the tap tells WebKit that the user asked for it. WebKit does nothing for an input the keyboard
		// is showing for, or when no user gesture is in progress.
		public static refocus() {
			const input = BrowserInvisibleTextBoxViewExtension.inputElement;
			if (!BrowserInputHelper.isIOS || input == null || document.activeElement !== input) {
				return;
			}

			input.focus(BrowserInvisibleTextBoxViewExtension.focusOptions);

			// An input that keeps focus raises no focusin to arm the trailing-click guard with (see
			// installTrailingClickGuard), so it is armed here, as it is for the tap that focuses the input.
			// As there, a focus from code after a touch arms it too; the next pointerdown disarms it.
			if (BrowserInvisibleTextBoxViewExtension.lastPointerType === "touch" || BrowserInvisibleTextBoxViewExtension.lastPointerType === "pen") {
				BrowserInvisibleTextBoxViewExtension.swallowNextCanvasClick = true;
			}
		}

		private static detachCore() {
			BrowserInvisibleTextBoxViewExtension.detachGeneration++;
			BrowserInvisibleTextBoxViewExtension.currentHandle = 0;
			// Focus left the text inputs: a line break that comes after this is not the last keydown's.
			BrowserInvisibleTextBoxViewExtension.lastKeyDownEnterCode = "";
			BrowserInvisibleTextBoxViewExtension.lastKeyDownNamedEnter = false;
			const wasComposing = BrowserInvisibleTextBoxViewExtension.isCompositionReported;
			// Blur explicitly before removing: the .blur() method dispatches synchronously, so it
			// lands inside the suppression window. WebKit can otherwise defer the implicit blur that
			// fires on element removal past that window, which would clear the wrong TextBox's focus.
			BrowserInvisibleTextBoxViewExtension.inputElement?.blur();
			BrowserInvisibleTextBoxViewExtension.inputElement?.remove();
			BrowserInvisibleTextBoxViewExtension.inputElement = null;
			// The removed input's compositionend is ignored, so a composition still open on it ends here: when the TextBox
			// keeps focus (accessibility handing it over to its semantic element), nothing else would end it.
			if (wasComposing) {
				BrowserInvisibleTextBoxViewExtension.resetComposition();
				BrowserInvisibleTextBoxViewExtension._imeExports.OnCompositionEnded();
			}
		}

		public static blur(handle: number) {
			// Managed-initiated blur (EndEntry): the FocusManager already knows focus is leaving.
			const blurredHandle = Number(handle);
			if (blurredHandle !== 0
				&& BrowserInvisibleTextBoxViewExtension.currentHandle !== 0
				&& blurredHandle !== BrowserInvisibleTextBoxViewExtension.currentHandle) {
				// Stale blur from a TextBox that no longer owns the shared input; detaching now
				// would tear down its successor's entry session.
				return;
			}

			// Don't detach synchronously: when focus is moving to another TextBox, EndEntry runs
			// before StartEntry in the same task, and detaching in between commits the soft-keyboard
			// dismissal on iOS even though another TextBox is about to take over. Defer by one
			// microtask; an intervening focus()/detach() supersedes this via detachGeneration.
			const generation = ++BrowserInvisibleTextBoxViewExtension.detachGeneration;
			queueMicrotask(() => {
				if (generation === BrowserInvisibleTextBoxViewExtension.detachGeneration) {
					BrowserInvisibleTextBoxViewExtension.runSuppressingBlur(BrowserInvisibleTextBoxViewExtension.detachCore);
				}
			});
		}

		public static detach() {
			BrowserInvisibleTextBoxViewExtension.runSuppressingBlur(BrowserInvisibleTextBoxViewExtension.detachCore);
		}

		public static hasInput(): boolean {
			return BrowserInvisibleTextBoxViewExtension.inputElement != null;
		}

		public static setText(text: string) {
			const input = BrowserInvisibleTextBoxViewExtension.inputElement;
			if (input != null) {
				// While a composition edits the text, the browser owns the value: overwriting it would destroy the
				// composition. A TextBox text that differs from it only as a same-length filter changes it (upper case) is
				// reconciled once it ends; for any other difference the TextBox ends the composition first.
				if (BrowserInvisibleTextBoxViewExtension.isCompositionReported) {
					BrowserInvisibleTextBoxViewExtension.pendingText = BrowserInvisibleTextBoxViewExtension.asInputValue(input, text) === input.value ? null : text;
					return;
				}
				BrowserInvisibleTextBoxViewExtension.pendingText = null;

				// input could be null beccause we could call setText without focusing first

				// Compared as the input holds it (a textarea's \n for the TextBox's \r): assigning an equivalent value
				// would look like an outside edit to the keyboard.
				if (BrowserInvisibleTextBoxViewExtension.asInputValue(input, text) !== input.value) {
					// When setting input.value, the browser will try to set the selection to the end, which isn't what we want.
					// The browser doesn't raise onselectionchange synchronously though, so we set a flag that we're waiting
					// for a future selection change that is the result of setting value.
					// And we set the existing values of selection start and selection end.
					// On the next onselectionchange event, we will ignore the browser provided selection and use these values.
					// Also, in case we got a managed selection in between here and the next onselectionchange, we will
					// use that instead (see updateSelection below).
					BrowserInvisibleTextBoxViewExtension.waitingAsyncOnSelectionChange = true;
					BrowserInvisibleTextBoxViewExtension.nextSelectionStart = input.selectionStart;
					BrowserInvisibleTextBoxViewExtension.nextSelectionEnd = input.selectionEnd;
					BrowserInvisibleTextBoxViewExtension.nextSelectionDirection = input.selectionDirection;
					input.value = text;
					BrowserInvisibleTextBoxViewExtension.waitingSelectionValue = input.value;
					BrowserInvisibleTextBoxViewExtension.syncedValue = input.value;
				}
			}
		}

		public static invalidateComposition() {
			// The TextBox ended the composition, so its text wins over the value the composition left.
			const text = BrowserInvisibleTextBoxViewExtension.pendingText;
			BrowserInvisibleTextBoxViewExtension.compositionGeneration++;
			BrowserInvisibleTextBoxViewExtension.resetComposition();
			if (text !== null) {
				BrowserInvisibleTextBoxViewExtension.setText(text);
			}
		}

		public static restartComposition() {
			BrowserInvisibleTextBoxViewExtension.invalidateComposition();
			const input = BrowserInvisibleTextBoxViewExtension.inputElement;
			if (input == null || document.activeElement !== input) {
				return;
			}

			const selectionStart = input.selectionStart;
			const selectionEnd = input.selectionEnd;
			const selectionDirection = input.selectionDirection;
			// Managed-initiated: reporting this blur would unfocus the TextBox that owns the session.
			BrowserInvisibleTextBoxViewExtension.runSuppressingBlur(() => {
				input.blur();
				input.focus({ preventScroll: true });
			});
			if (selectionStart !== null && selectionEnd !== null) {
				input.setSelectionRange(selectionStart, selectionEnd, selectionDirection);
			}
		}

		public static updateSize(width: number, height: number) {
			const input = BrowserInvisibleTextBoxViewExtension.inputElement;
			// Sized like the TextBox only where the input is positioned over it: a multi-line TextBox reports
			// its full content height, which would stretch an off-screen input back into the viewport.
			if (input != null && !BrowserInvisibleTextBoxViewExtension.keepsInputOffscreen) {
				input.style.width = `${width}px`;
				input.style.height = `${height}px`;
			}
		}

		public static updatePosition(x: number, y: number) {
			const input = BrowserInvisibleTextBoxViewExtension.inputElement;
			if (input != null && !BrowserInvisibleTextBoxViewExtension.keepsInputOffscreen) {
				input.style.top = `${Math.round(y)}px`;
				input.style.left = `${Math.round(x)}px`;
			}
		}

		public static updateSelection(start: number, length: number, direction: "forward" | "backward") {
			if (!BrowserInvisibleTextBoxViewExtension.isInSelectionChange) {
				const input = BrowserInvisibleTextBoxViewExtension.inputElement;

				// While a composition edits the text, only a caret the user moved elsewhere (a tap) is pushed, which
				// lets the IME commit the composition. Echoes of the input's own selection would disturb it.
				if (BrowserInvisibleTextBoxViewExtension.isCompositionReported
					&& (BrowserInvisibleTextBoxViewExtension.isSyncingFromNative
						|| (input?.selectionStart === start && input?.selectionEnd === start + length))) {
					return;
				}

				// See comment in setText.
				if (BrowserInvisibleTextBoxViewExtension.waitingAsyncOnSelectionChange) {
					BrowserInvisibleTextBoxViewExtension.nextSelectionStart = start;
					BrowserInvisibleTextBoxViewExtension.nextSelectionEnd = start + length;
					BrowserInvisibleTextBoxViewExtension.nextSelectionDirection = direction;
				}

				input?.setSelectionRange(start, start + length, direction);
			}
		}
	}
}
