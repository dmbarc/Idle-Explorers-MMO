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

    /// <summary>Called once when the screen is first created. Build all UI elements here.</summary>
    public abstract void Build();

    /// <summary>Called every time this screen becomes the top of the stack.</summary>
    public virtual void OnShow() { }

    /// <summary>Called when another screen is pushed on top of this one.</summary>
    public virtual void OnHide() { }

    /// <summary>Called when a screen above this one is popped and this screen resumes.</summary>
    public virtual void OnResume() { }
}
