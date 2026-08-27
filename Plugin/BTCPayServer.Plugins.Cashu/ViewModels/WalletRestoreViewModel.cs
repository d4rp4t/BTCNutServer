#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Common;

namespace BTCPayServer.Plugins.Cashu.ViewModels;

public class WalletRestoreViewModel
{
    public string Mnemonic { get; set; }

    public IReadOnlyList<string> Words =>
        (Mnemonic ?? string.Empty).Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

    public List<string> MintUrls { get; set; }

    public HashSet<int> InvalidWordIndices { get; set; } = [];

    public HashSet<int> InvalidMintsIndices { get; set; } = [];
}
