using System.Text.Json;
using Terraria;

namespace TerrariaAITeammate.Common;

public partial class AIExecutor
{
    private RouteWorldInspection? routeInspection;
    private void CancelRouteInspection()
    { routeInspection?.Cancel(); routeInspection = null; }

    private void TickRouteInspection(NPC npc, Player player)
    {
        // Hold horizontal input while reading; native gravity/collision remain active.
        npc.velocity.X = 0;
        object actionToken = currentPlan ?? (object)this;
        routeInspection ??= NavigationWorldObserver.BeginRouteInspection(itemQuery, npc, player, actionToken, currentPlan?.X, currentPlan?.Y);
        object? result = routeInspection.Advance(npc, player, actionToken);
        if (result == null) return;
        FinishCurrentStep(JsonSerializer.Serialize(result));
        CancelRouteInspection();
    }
}
