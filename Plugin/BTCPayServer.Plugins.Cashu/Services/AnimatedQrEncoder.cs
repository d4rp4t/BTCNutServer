using System;
using System.Collections.Generic;
using System.Text;
using DotNut.BcUr;
using QRCoder;

namespace BTCPayServer.Plugins.Cashu.Services;

/// <summary>
/// Turns a payload into the frames of an animated QR code. The payload is split into BC-UR parts
/// and every part is rendered to a PNG up front, so the browser only has to flip through the frames.
/// </summary>
public static class AnimatedQrEncoder
{
    // keeps every frame a low density QR, so a phone camera locks onto it quickly
    private const int MaxUrFragmentLength = 150;

    // the parts past the plain fragments are fountain mixes - they let a scanner that missed a frame
    // recover it instead of sitting through another full loop
    private const int RedundancyFactor = 2;

    /// <summary>
    /// Splits the payload into BC-UR parts. A payload small enough to fit into a single part yields
    /// exactly one, which is a plain single part UR rather than a numbered fragment.
    /// </summary>
    public static IReadOnlyList<string> EncodeParts(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return Array.Empty<string>();
        }

        var encoder = new UREncoder(Ur.FromBytes(Encoding.UTF8.GetBytes(payload)), MaxUrFragmentLength, 0);
        var partCount = encoder.IsSinglePart ? 1 : encoder.FragmentCount * RedundancyFactor;

        var parts = new List<string>(partCount);
        for (var i = 0; i < partCount; i++)
        {
            parts.Add(encoder.NextPart());
        }

        return parts;
    }

    /// <summary>
    /// Renders the payload as PNG data uris, one per BC-UR part.
    /// </summary>
    public static IReadOnlyList<string> Encode(string payload)
    {
        var parts = EncodeParts(payload);
        if (parts.Count == 0)
        {
            return Array.Empty<string>();
        }

        using var generator = new QRCodeGenerator();
        var frames = new List<string>(parts.Count);

        foreach (var part in parts)
        {
            using var qrData = generator.CreateQrCode(part, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(qrData);
            // one pixel per module keeps each frame at a few hundred bytes; the browser scales it
            // back up with image-rendering: pixelated
            frames.Add($"data:image/png;base64,{Convert.ToBase64String(png.GetGraphic(1))}");
        }

        return frames;
    }
}
