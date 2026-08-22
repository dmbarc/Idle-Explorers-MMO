using UnityEngine;

/// <summary>
/// Base class for all game screens and panels.
/// Subclasses override Build() to construct their UI via UIFactory,
/// and OnShow/OnHide/OnResume for lifecycle events.
/// </summary>
public abstract class UIScreen : MonoBehaviour
{
    /// <summary>
    /// True for panels that sit on top of the screen below rather than replacing it —
    /// inventory, skills, the menu. The screen underneath stays visible and keeps
    /// its event subscriptions, so the HUD does not vanish when you open a bag.
    /// </summary>
    public virtual bool IsOverlay => false;

    /// <summary>
    /// True for screens whose content is read from game state inside Build().
    ///
    /// Screens are cached and Build() normally runs once, so anything rendered at
    /// build time is frozen at the values of the first show. Setting this makes
    /// UIManager tear the contents down and rebuild them on every push.
    /// </summary>
    public virtual bool RebuildOnShow => false;

    /// <summary>Called when the screen is created, and again per show if RebuildOnShow.</summary>
    public abstract void Build();

    /// <summary>
    /// Destroys existing content and runs Build() again. DestroyImmediate because
    /// Build runs in the same frame and would otherwise see the old children.
    /// </summary>
    public void RebuildContents()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);

        Build();
    }

    /// <summary>Called every time this screen becomes the top of the stack.</summary>
    public virtual void OnShow() { }

    /// <summary>Called when another screen is pushed on top of this one.</summary>
    public virtual void OnHide() { }

    /// <summary>Called when a screen above this one is popped and this screen resumes.</summary>
    public virtual void OnResume() { }
}
