namespace TerrariaAITeammate.Common;

/// <summary>Explicit assistance policies shared by normal play and acceptance runs.</summary>
public sealed record ExecutionPolicy(bool AllowTeleportRecovery = false, bool AllowBlueprintWorldEditing = false)
{
    public string MaterialSource => "companion_inventory_only";
    public bool PreserveExistingStructures => true;
}
