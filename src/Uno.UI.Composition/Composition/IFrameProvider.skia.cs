#nullable enable

using System;
using SkiaSharp;

namespace Microsoft.UI.Composition;

internal interface IFrameProvider : IDisposable
{
	SKImage? CurrentImage { get; }

	/// <summary>
	/// The memory held by the decoded frames.
	/// </summary>
	long ByteCount { get; }
}
