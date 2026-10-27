#nullable enable

using System;
using System.Collections.Generic;
using Uno.UI;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// The Permissions Policy attributes of the iframe that shows the web content of a <see cref="WebView2"/>, from
/// <see cref="FeatureConfiguration.WebView2.PermissionsPolicy"/>. Used by both WebAssembly renderers.
/// </summary>
internal static class WebViewFramePermissions
{
	private static readonly char[] _asciiWhitespace = [' ', '\t', '\n', '\f', '\r'];

	/// <summary>
	/// Gets the value of the frame's <c>allow</c> attribute, or null when it has none, and whether the frame gets the legacy
	/// <c>allowfullscreen</c> attribute. The policy applies to the documents the frame loads after it is set, so it must be set
	/// before the first navigation.
	/// </summary>
	internal static (string? Allow, bool AllowFullscreen) Get()
	{
		var directives = new List<string>();
		var allowsFullscreen = false;

		foreach (var entry in FeatureConfiguration.WebView2.PermissionsPolicy)
		{
			if (entry is null)
			{
				continue;
			}

			// Split as the browser splits the attribute, so that a directive is read here as the browser will read it.
			foreach (var directive in entry.Split(';'))
			{
				var tokens = directive.Split(_asciiWhitespace, StringSplitOptions.RemoveEmptyEntries);
				if (tokens.Length == 0)
				{
					continue;
				}

				// A feature alone would only be allowed for the origin of the frame's src attribute, which navigating it doesn't set.
				var isForEveryOrigin = tokens.Length == 1 || Array.IndexOf(tokens, "*", 1) > 0;

				directives.Add(tokens.Length == 1 ? $"{tokens[0]} *" : string.Join(' ', tokens));
				allowsFullscreen |= tokens[0] == "fullscreen" && isForEveryOrigin;
			}
		}

		return (directives.Count > 0 ? string.Join("; ", directives) : null, allowsFullscreen);
	}
}
