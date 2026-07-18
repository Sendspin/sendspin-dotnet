namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Sendspin protocol message type identifiers.
/// Format: "direction/action" where direction is "client" or "server"
/// </summary>
public static class MessageTypes
{
    // Handshake
    public const string ClientHello = "client/hello";
    public const string ServerHello = "server/hello";
    public const string ServerActivate = "server/activate";
    public const string ClientGoodbye = "client/goodbye";

    // Pairing
    public const string ClientPairFinalize = "client/pair-finalize";
    public const string ServerPairFinalize = "server/pair-finalize";
    public const string PairAbort = "pair/abort";
    public const string ClientPairInit = "client/pair-init";
    public const string ServerPairInit = "server/pair-init";
    public const string ServerPairAuth = "server/pair-auth";
    public const string ClientPairAuth = "client/pair-auth";
    public const string ServerPairConfirm = "server/pair-confirm";
    public const string ClientPairConfirm = "client/pair-confirm";

    // Management (all requests answered by management/result)
    public const string ManagementListRecords = "management/list-records";
    public const string ManagementAddRecord = "management/add-record";
    public const string ManagementRemoveRecord = "management/remove-record";
    public const string ManagementGetPairingConfig = "management/get-pairing-config";
    public const string ManagementSetPairingConfig = "management/set-pairing-config";
    public const string ManagementResult = "management/result";
    public const string ServerUnpair = "server/unpair";

    // Clock synchronization
    public const string ClientTime = "client/time";
    public const string ServerTime = "server/time";

    // Stream lifecycle
    public const string StreamStart = "stream/start";
    public const string StreamEnd = "stream/end";
    public const string StreamClear = "stream/clear";
    public const string StreamRequestFormat = "stream/request-format";

    // Group state
    public const string GroupUpdate = "group/update";

    // Player commands and state
    public const string ClientCommand = "client/command";
    public const string ServerCommand = "server/command";
    public const string ClientState = "client/state";
    public const string ServerState = "server/state";
}

/// <summary>
/// Binary message type identifiers (first byte of binary messages).
/// </summary>
public static class BinaryMessageTypes
{
    // Player audio (role 1, slots 0-3)
    public const byte PlayerAudio0 = 4;
    public const byte PlayerAudio1 = 5;
    public const byte PlayerAudio2 = 6;
    public const byte PlayerAudio3 = 7;

    // Artwork (role 2, slots 0-3)
    public const byte Artwork0 = 8;
    public const byte Artwork1 = 9;
    public const byte Artwork2 = 10;
    public const byte Artwork3 = 11;

    // Visualizer (role 4, slots 0-7). Each binary carries one feature type.
    public const byte VisualizerLoudness = 16; // slot 0 (also the legacy draft_r1 data blob)
    public const byte VisualizerBeat = 17;     // slot 1
    public const byte VisualizerFPeak = 18;    // slot 2
    public const byte VisualizerSpectrum = 19; // slot 3
    public const byte VisualizerPeak = 20;     // slot 4
    public const byte VisualizerPitch = 21;    // slot 5

    public static bool IsPlayerAudio(byte type) => type >= 4 && type <= 7;
    public static bool IsArtwork(byte type) => type >= 8 && type <= 11;
    public static bool IsVisualizer(byte type) => type >= 16 && type <= 23;
}
