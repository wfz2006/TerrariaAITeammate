using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.UI;
using static TerrariaAITeammate.Common.CompanionControlsUI;

namespace TerrariaAITeammate.Common;

internal sealed class CompanionTestDiagnosticsUI(AITeammateSystem system, bool inventoryWasOpen) : UIState
{
    private UIText status = null!;
    private UITextPanel<string> recordingButton = null!;

    public override void OnInitialize()
    {
        var panel = new UIPanel
        {
            BackgroundColor = PanelColor,
            BorderColor = BorderColor,
            HAlign = 0.5f,
            VAlign = 0.46f
        };
        panel.Width.Set(-32f, 1f);
        panel.MaxWidth.Set(470f, 0f);
        panel.Height.Set(253f, 0f);
        panel.SetPadding(14f);
        Append(panel);

        AddLabel(panel, Text("Diagnostics"), 1.2f, 0f, Color.White);
        var close = CreateButton("Close", ButtonColor, IngameFancyUI.Close);
        close.HAlign = 1f;
        close.Width.Set(32f, 0f);
        close.Height.Set(30f, 0f);
        panel.Append(close);

        status = AddLabel(panel, "", 0.85f, 37f, MutedText);
        recordingButton = AddButton(panel, "DiagnosticsStart", 0, 68f, ButtonColor, () =>
        {
            system.ToggleTestRecording();
            Refresh();
        }, columns: 2);
        AddButton(panel, "DiagnosticsBug", 1, 68f, ButtonColor, () =>
        {
            system.AddTestBug();
            Refresh();
        }, columns: 2);
        AddButton(panel, "DiagnosticsExport", 0, 112f, ButtonColor, system.ExportTestDiagnostics, columns: 2);
        AddButton(panel, "DiagnosticsBack", 1, 112f, ButtonColor,
            () => system.OpenCompanionPanel(inventoryWasOpen), columns: 2);
        AddLabel(panel, Text("DiagnosticsLocal"), 0.8f, 164f, MutedText);
        AddLabel(panel, Text("DiagnosticsNote"), 0.8f, 192f, MutedText);
    }

    public override void OnActivate() => Refresh();

    public override void OnDeactivate()
    {
        if (!Main.inFancyUI) Main.playerInventory = inventoryWasOpen;
    }

    private void Refresh()
    {
        string state = system.IsTestRecording ? "DiagnosticsActive"
            : system.TestRecordingFailed ? "DiagnosticsWriteError" : "DiagnosticsInactive";
        status.SetText(Text("DiagnosticsStatus", Text(state), system.TestEventCount));
        recordingButton.SetText(Text(system.IsTestRecording ? "DiagnosticsStop" : "DiagnosticsStart"));
    }

}
