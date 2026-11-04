using System.Text.Json.Serialization;

namespace TianWen.OpenPHD2MultiScope;

// The event notifications PHD2 sends, re-created for the proxy's clients. Property names are
// PHD2's own (https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring), which is why they
// are not idiomatic C#: they ARE the wire format.

internal abstract record PhdEvent([property: JsonPropertyOrder(-1)] string Event)
{
    public double Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    public string Host { get; init; } = Environment.MachineName;
    public int Inst { get; init; } = 1;
}

internal sealed record VersionEvent(string PHDVersion, string PHDSubver) : PhdEvent("Version")
{
    public int MsgVersion { get; init; } = 1;
    public bool OverlapSupport { get; init; } = true;
}

internal sealed record AppStateEvent(string State) : PhdEvent("AppState");
/// <summary>
/// An event relayed by name alone. TianWen's driver reports PHD2's events as a name and the app
/// state, so an event's own fields (LockPositionSet's X and Y, Alert's Msg) do not reach here.
/// </summary>
internal sealed record SimpleEvent([property: JsonIgnore] string Name) : PhdEvent(Name)
{
    /// <summary>PHD2's events with no fields a client relies on, or whose fields the driver does not report.</summary>
    public static readonly IReadOnlySet<string> Relayed = new HashSet<string>(StringComparer.Ordinal)
    {
        "Alert", "Calibrating", "CalibrationComplete", "CalibrationDataFlipped", "CalibrationFailed",
        "ConfigurationChange", "GuideParamChange", "GuidingDithered", "GuidingStopped",
        "LockPositionLost", "LockPositionSet", "LockPositionShiftLimitReached", "LoopingExposures",
        "LoopingExposuresStopped", "Paused", "Resumed", "SettleBegin", "StarLost", "StarSelected",
        "StartCalibration", "StartGuiding",
    };
}
/// <summary>
/// A guide step, in PHD2's units: distances in guide-camera PIXELS, pulses in ms with a compass direction.
/// The <c>Frame</c> number counts the steps this proxy relayed, not PHD2's own frame numbers.
/// </summary>
internal sealed record GuideStepEvent(int Frame, double RADistanceRaw, double DECDistanceRaw) : PhdEvent("GuideStep")
{
    public int? RADuration { get; init; }
    public string? RADirection { get; init; }
    public int? DECDuration { get; init; }
    public string? DECDirection { get; init; }
}
internal sealed record SettlingEvent(double Distance, double Time, double SettleTime, bool StarLocked) : PhdEvent("Settling");
internal sealed record SettleDoneEvent(int Status, string? Error) : PhdEvent("SettleDone");

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VersionEvent))]
[JsonSerializable(typeof(AppStateEvent))]
[JsonSerializable(typeof(SimpleEvent))]
[JsonSerializable(typeof(GuideStepEvent))]
[JsonSerializable(typeof(SettlingEvent))]
[JsonSerializable(typeof(SettleDoneEvent))]
internal partial class PhdEventJsonContext : JsonSerializerContext
{
}
