﻿using System;

namespace UnityWebBrowser.Shared.Events;

/// <summary>
///     We use a custom event for pixels to not use the byte[] array type reader/writer
/// </summary>
public struct PixelsEvent
{
    public ReadOnlyMemory<byte> PixelData { get; set; }
}