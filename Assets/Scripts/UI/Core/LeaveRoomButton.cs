using IdleExplorers.Backend;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The way out of a room you were let into.
///
/// ══ WHY THIS EXISTS ═══════════════════════════════════════════════════════════
///
/// The throne was a one-way door. There was exactly one exit -- the portal
/// BossController opens beside the King's corpse -- and it appears only when he
/// falls. So a player who walked in to look, or who realised halfway through that
/// they were not going to make it, had two ways out: win, or die.
///
/// Dying is not an exit. It is a wipe with a loading screen at the end of it, and a
/// room whose only exit is dying teaches players not to walk into rooms.
///
/// ══ WHY A BUTTON AND NOT A SECOND PORTAL ══════════════════════════════════════
///
/// A portal has to be somewhere, and wherever it is, it is somewhere the player has
/// to get to -- across an arena, past a boss, through a cone. It can also be walked
/// into by accident while dodging, which is the worst possible failure: an exit that
/// ends the fight you were winning.
///
/// A button is in the same place from every position in the room, cannot be strafed
/// into, and is visible from the moment you arrive. It also works when the fight
/// never started, which is the case that matters most -- if engage fails, there is no
/// boss, no corpse and therefore no portal, and the old arena had no exit at all.
///
/// ══ WHY IT ASKS TWICE ═════════════════════════════════════════════════════════
///
/// Because it sits in a row of buttons a player is pressing under pressure, and one
/// stray click must not end a five-minute attempt. The second press has to arrive
/// while the first is still being offered, and the offer expires -- so it costs a
/// deliberate player half a second and saves a mistaken one their evening.
///
/// ══ AND WHY IT TELLS THE SERVER ═══════════════════════════════════════════════
///
/// Walking out leaves a live encounter row behind. Until it is closed the fight
/// follows you: the server still thinks you are in it, and every future engage is
/// refused as "already fighting" -- which, from inside the client, looks like an
/// arena with no boss in it. Leaving properly is the point of this, not travelling.
/// </summary>
public class LeaveRoomButton : MonoBehaviour
{
    /// <summary>How long the confirmation stays on offer.</summary>
    private const float ConfirmWindow = 3f;

    private Button   _button;
    private TMP_Text _label;
    private Image    _image;
    private Color    _restColor;

    private float _confirmUntil = -1f;
    private bool  _leaving;

    /// <summary>Builds it into an existing row and returns it, hidden.</summary>
    public static LeaveRoomButton Attach(Transform parent)
    {
        Button button = UIFactory.Button(parent, "LEAVE", null, width: 68f);

        var leave = button.gameObject.AddComponent<LeaveRoomButton>();

        leave._button = button;
        leave._image  = button.GetComponent<Image>();
        leave._label  = button.GetComponentInChildren<TMP_Text>();

        leave._restColor = leave._image != null ? leave._image.color : Color.white;

        button.onClick.AddListener(leave.Pressed);
        button.gameObject.SetActive(false);

        return leave;
    }

    /// <summary>
    /// Shown only in a room you cannot walk out of.
    ///
    /// portalOnly is the map's own word for "you were let in here" -- it is already
    /// what keeps the arena off the travel list and what sends a dead player outside
    /// rather than reviving them next to the King. Reusing it means a second boss room
    /// gets its exit for nothing, and means there is no separate list to keep in step.
    /// </summary>
    private void Update()
    {
        MapData here = GameManager.Zone?.CurrentMap;

        bool wanted = here != null && here.portalOnly;

        if (_button != null && _button.gameObject.activeSelf != wanted)
        {
            _button.gameObject.SetActive(wanted);

            // Leaving the room resets the offer. Otherwise a half-pressed LEAVE from a
            // previous attempt would still be armed on the next one.
            _confirmUntil = -1f;
            Draw();
        }

        if (_confirmUntil > 0f && Time.time > _confirmUntil)
        {
            _confirmUntil = -1f;
            Draw();
        }
    }

    private void Pressed()
    {
        if (_leaving) return;

        if (Time.time > _confirmUntil)
        {
            _confirmUntil = Time.time + ConfirmWindow;
            Draw();
            return;
        }

        _confirmUntil = -1f;
        _ = LeaveAsync();
    }

    private void Draw()
    {
        bool arming = Time.time <= _confirmUntil;

        if (_label != null) _label.text = arming ? "SURE?" : "LEAVE";

        if (_image != null)
            _image.color = arming ? UIManager.Theme.chatBad : _restColor;
    }

    private async Awaitable LeaveAsync()
    {
        _leaving = true;

        if (_label != null) _label.text = "…";

        try
        {
            // ══ THE ORDER MATTERS ═════════════════════════════════════════════
            //
            // Told first, travelled second. A client that changed map and then
            // reported would leave the fight open for the length of a round trip,
            // and a client that crashed in between would leave it open for good --
            // which is the exact state that made the King unreachable.
            //
            // A failure here does NOT stop the travel. Being stuck in a room is worse
            // than a stale encounter row, and the row expires on its own at enrage.
            if (ServerState.IsAuthoritative && !string.IsNullOrEmpty(ServerState.CharacterId))
                await GameBackend.Current.FleeBossAsync(ServerState.CharacterId);
        }
        catch (BackendException e)
        {
            Debug.LogWarning($"[Leave] Could not close the encounter: {e.Message}");
        }
        finally
        {
            _leaving = false;
            Draw();

            GameEvents.FireToast("You slip back out of the throne.", ChatTone.Info);
            GameManager.Zone?.EnterMap(GameManager.StartingMapId);
        }
    }
}
