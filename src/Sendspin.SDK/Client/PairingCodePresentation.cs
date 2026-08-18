namespace Sendspin.SDK.Client;

/// <summary>
/// A dynamic pairing pairing code to present to the operator, with the server's language hint.
/// </summary>
/// <param name="PairingCode">The derived pairing code, exactly the session's <c>pin_length</c> digits.</param>
/// <param name="Languages">
/// BCP 47 tags in descending operator preference from the activation, or null when the server
/// sent none. Informational: emitting in another language is never a protocol error. Match
/// with RFC 4647 Lookup against the languages the application can actually speak, falling back
/// to its own default when nothing matches.
/// </param>
public sealed record PairingCodePresentation(string PairingCode, IReadOnlyList<string>? Languages)
{
    /// <summary>
    /// Group sizes the spec recommends for each pairing code length, indexed by length. Entries below
    /// index 4 are unreachable: <c>min_pin_length</c> is bounded to 4-12, and the pairing code is derived
    /// at the negotiated length.
    /// </summary>
    /// <remarks>
    /// This is the table the spec prints, not a derivation of it. The stated rule -- the most
    /// groups of 4 with none smaller than 3, smallest group in the middle, and 3-2 for length 5
    /// because no such split exists -- produces exactly these nine rows, and the domain is only
    /// nine wide. Transcribing is easier to check against the spec than re-deriving.
    /// </remarks>
    private static readonly int[][] GroupSizes =
    [
        [], [], [], [],
        [4],          // 4
        [3, 2],       // 5 - no split into 3s and 4s exists
        [3, 3],       // 6
        [4, 3],       // 7
        [4, 4],       // 8
        [3, 3, 3],    // 9
        [4, 3, 3],    // 10
        [4, 3, 4],    // 11 - smallest group in the middle
        [4, 4, 4],    // 12
    ];

    /// <summary>
    /// <see cref="PairingCode"/> split into the groups the spec recommends for display and for spoken
    /// emission (read the digits of each group, pausing between groups). For a 6-digit pairing code this
    /// is two groups of 3; for an 8-digit static pairing code, two groups of 4.
    /// </summary>
    /// <remarks>
    /// Presentation only. The pairing code value is the contiguous digits in <see cref="PairingCode"/>, and
    /// separators never enter derivation, operator entry, or the <c>PRS</c> transcript -- so
    /// join these with whatever separator suits the surface and accept typed input with the
    /// separators stripped. A pairing code outside the spec's 4-12 range is returned as a single group
    /// rather than throwing: this is display formatting, and failing to format is not worth
    /// failing a pairing over.
    /// </remarks>
    public IReadOnlyList<string> Groups
    {
        get
        {
            if (PairingCode.Length >= GroupSizes.Length || GroupSizes[PairingCode.Length].Length == 0)
                return [PairingCode];

            var groups = new List<string>(3);
            int offset = 0;
            foreach (int size in GroupSizes[PairingCode.Length])
            {
                groups.Add(PairingCode.Substring(offset, size));
                offset += size;
            }

            return groups;
        }
    }
}
