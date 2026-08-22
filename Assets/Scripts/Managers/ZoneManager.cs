using UnityEngine;

/// <summary>Stub — implemented fully in Phase 6 (additive scene loading).</summary>
public class ZoneManager : MonoBehaviour
{
    public string CurrentMapId  { get; private set; }
    public string CurrentZoneId { get; private set; }

    public void EnterMap(string mapId)
    {
        CurrentMapId = mapId;
        var map = GameManager.Content?.GetMap(mapId);
        if (map != null) CurrentZoneId = map.zoneId;
        GameEvents.FireMapEntered(mapId);
        GameManager.Activity?.SetDefaultCombatActivity(mapId);
    }

    public void UnloadCurrentZone() { /* Phase 6 */ }
}
