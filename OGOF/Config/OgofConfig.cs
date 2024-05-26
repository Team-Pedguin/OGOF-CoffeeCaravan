using System;
using System.Text.Json.Serialization;

namespace OGOF;

public sealed class OgofConfig
{
    public static OgofConfig Default { get; } = new();

    //public string FontName { get; set; } = "Segoe UI";

    [JsonPropertyName("fontSize")]
    public int FontSize { get; set; } = 36;

    [JsonPropertyName("fontColors")]
    public string[] FontColors { get; set; } = ["#006ce0", "#a100e0", "#e00013", "#c6e000", "#00e047"];

    [JsonPropertyName("enoughChatters")]
    public int EnoughChatters { get; set; } = 500;

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = "ah1dcykia4chi6pz5f3z80sizxi5ba"; // not secret

    [JsonPropertyName("broadcasterId")]
    public string? BroadcasterId { get; set; }

    [JsonPropertyName("moderatorId")]
    public string? ModeratorId { get; set; }

    [JsonPropertyName("displayNameTransforms")]
    public DisplayNameTransform[] DisplayNameTransforms { get; set; }

    [JsonPropertyName("drawTwitchAuthCode")]
    public bool DrawTwitchAuthCode { get; set; } = false;

    [JsonPropertyName("openTwitchAuthInBrowser")]
    public bool OpenTwitchAuthInBrowser { get; set; } = true;
}