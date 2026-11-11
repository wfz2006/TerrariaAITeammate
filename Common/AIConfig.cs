using System.ComponentModel;
using Terraria;
using Terraria.ModLoader.Config;

namespace TerrariaAITeammate.Common;

public class AIConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    [Header("CompanionProfile")]
    [DefaultValue(CompanionIdentity.DefaultName)]
    public string CompanionName { get; set; } = CompanionIdentity.DefaultName;

    [Header("ModelConnection")]
    [DefaultValue(true)]
    public bool UseLlm { get; set; } = true;

    [DefaultValue("")]
    public string ApiKey { get; set; } = "";

    [DefaultValue("https://api.deepseek.com")]
    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    [DefaultValue("deepseek-flash")]
    public string Model { get; set; } = "deepseek-flash";

    [Header("Interaction")]
    [Dropdown]
    [DefaultValue(CompanionChatMode.Addressed)]
    public CompanionChatMode ChatMode { get; set; } = CompanionChatMode.Addressed;

    [DefaultValue(true)]
    public bool UseCombatLlm { get; set; } = true;

    [Header("ModelParameters")]
    [Dropdown]
    [DefaultValue(LlmThinkingMode.Enabled)]
    public LlmThinkingMode ThinkingMode { get; set; } = LlmThinkingMode.Enabled;

    [Dropdown]
    [DefaultValue(LlmReasoningEffort.Low)]
    public LlmReasoningEffort ReasoningEffort { get; set; } = LlmReasoningEffort.Low;

    [DefaultValue(8192)]
    [Range(1400, 32768)]
    [Increment(256)]
    public int MaxResponseTokens { get; set; } = 8192;

    [DefaultValue(true)]
    public bool UseStructureOverview { get; set; } = true;

    [DefaultValue(false)]
    public bool UsePhysicsStructureView { get; set; } = false;

    [Header("AssistanceAndDebug")]
    [DefaultValue(false)]
    public bool AllowTeleportRecovery { get; set; } = false;

    [DefaultValue(false)]
    public bool AllowBlueprintWorldEditing { get; set; } = false;

    [DefaultValue(false)]
    public bool DebugLog { get; set; } = false;

    private LlmSettings? appliedSettings;

    internal LlmSettings CreateSettings(string key, string baseUrl, string model)
        => new(key.Trim(), baseUrl.Trim(), model.Trim(), MaxResponseTokens, ThinkingMode, ReasoningEffort)
        {
            UsePhysicsStructureView = UsePhysicsStructureView,
            UseStructureOverview = UseStructureOverview
        };

    public override void OnChanged()
    {
        CompanionName = CompanionIdentity.NormalizeName(CompanionName);
        LlmSettings? previous = appliedSettings;
        appliedSettings = UseLlm ? CreateSettings(ApiKey ?? "", BaseUrl ?? "", Model ?? "") : null;
        if (!Main.dedServ && !Main.gameMenu)
        {
            var system = AITeammateSystem.Instance;
            system.RefreshCompanionName(CompanionName);
            system.OnModelConfigChanged(previous != appliedSettings, UseCombatLlm);
        }
    }
}
