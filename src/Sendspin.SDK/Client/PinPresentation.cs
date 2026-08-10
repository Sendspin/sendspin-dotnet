namespace Sendspin.SDK.Client;

/// <summary>
/// A dynamic pairing PIN to present to the operator, with the server's language hint.
/// </summary>
/// <param name="Pin">The derived PIN, exactly the session's <c>pin_length</c> digits.</param>
/// <param name="Languages">
/// BCP 47 tags in descending operator preference from the activation, or null when the server
/// sent none. Informational: emitting in another language is never a protocol error. Match
/// with RFC 4647 Lookup against the languages the application can actually speak, falling back
/// to its own default when nothing matches.
/// </param>
public sealed record PinPresentation(string Pin, IReadOnlyList<string>? Languages);
